using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;

namespace RamDrive.Core.Memory;

/// <summary>
/// Fixed-size page pool backed by NativeMemory. Lock-free rent/return via ConcurrentStack.
///
/// <para>Capacity accounting ("committed" invariant): every page that is either rented out
/// or reserved for a future write counts against the single atomic
/// <see cref="_committedCount"/> (rented + reserved, never exceeding
/// <see cref="_maxPages"/>). A rent that pops from the free stack therefore consumes a
/// committed slot exactly like a fresh OS allocation. This closes the original flaw where
/// <c>Rent()</c> took pages from the (possibly pre-allocated) free stack without looking at
/// <c>_reservedCount</c> — a file that reserved capacity via SetLength could have it stolen
/// by another file's write, making <c>FreeBytes</c> go negative and the reserving file hit
/// DISK_FULL while the volume was nowhere near full.</para>
///
/// <para>Pages on the free stack were already allocated from the OS (<c>_allocatedCount</c>)
/// but do not count against <c>committed</c> until rented — they are the physical backing
/// that reservations later consume.</para>
/// </summary>
public sealed class PagePool : IDisposable
{
    private readonly int _pageSize;
    private readonly long _maxPages;
    private readonly ConcurrentStack<nint> _freePages = new();
    private readonly ConcurrentStack<nint> _allPages = new(); // tracks every allocation for cleanup
    private long _allocatedCount; // total pages ever allocated from OS
    private long _rentedCount;    // pages currently in use (not on free stack)
    private long _reservedCount;  // pages reserved by SetLength but not yet allocated
    private long _committedCount; // rented + reserved — the single capacity gate
    private volatile bool _disposed;

    public int PageSize => _pageSize;
    public long MaxPages => _maxPages;
    public long AllocatedCount => Volatile.Read(ref _allocatedCount);
    public long RentedCount => Volatile.Read(ref _rentedCount);
    public long ReservedCount => Volatile.Read(ref _reservedCount);
    /// <summary>Pages that are neither rented nor reserved — never negative.</summary>
    public long FreeCount => _maxPages - CommittedCount;

    /// <summary>Total committed pages (rented + reserved). May exceed <see cref="AllocatedCount"/>
    /// when reservations exist — reserved pages are promises backed by future allocations.</summary>
    public long CommittedCount => Volatile.Read(ref _committedCount);
    public long CapacityBytes => _maxPages * _pageSize;

    /// <summary>Bytes committed (rented + reserved).</summary>
    public long UsedBytes => CommittedCount * _pageSize;

    /// <summary>Free bytes reported to the volume — never negative.</summary>
    public long FreeBytes => (_maxPages - CommittedCount) * _pageSize;

    public PagePool(IOptions<RamDriveOptions> options, ILogger<PagePool> logger)
    {
        var opts = options.Value;
        var errors = opts.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Invalid RamDrive configuration: " + string.Join("; ", errors));

        _pageSize = opts.PageSizeKb * 1024;
        _maxPages = (opts.CapacityMb * 1024L * 1024L) / _pageSize;

        logger.LogInformation("PagePool: {PageSizeKb}KB pages, capacity {CapacityMb}MB ({MaxPages} pages), preAllocate={PreAllocate}",
            opts.PageSizeKb, opts.CapacityMb, _maxPages, opts.PreAllocate);

        if (opts.PreAllocate)
        {
            logger.LogInformation("PagePool: pre-allocating {Count} pages...", _maxPages);
            for (long i = 0; i < _maxPages; i++)
            {
                nint page = AllocateNativePage();
                _freePages.Push(page);
                Interlocked.Increment(ref _allocatedCount);
            }
            logger.LogInformation("PagePool: pre-allocation complete");
        }
    }

    /// <summary>
    /// Reserve capacity for pages without allocating them. Returns true if reservation succeeded.
    /// Reserved pages consume committed capacity (backing the <c>SetLength</c>-promise that a
    /// subsequent write will succeed) but zero OS memory until actually rented.
    /// </summary>
    public bool Reserve(long count)
    {
        if (count <= 0) return true;
        // Single atomic gate: committed + count <= maxPages. prevents both
        // over-reservation and free-stack rentals from exceeding capacity.
        while (true)
        {
            long committed = Volatile.Read(ref _committedCount);
            if (committed + count > _maxPages)
                return false;
            if (Interlocked.CompareExchange(ref _committedCount, committed + count, committed) == committed)
            {
                Interlocked.Add(ref _reservedCount, count);
                Debug.Assert(Volatile.Read(ref _committedCount) <= _maxPages, "reserve must not exceed maxPages");
                return true;
            }
        }
    }

    /// <summary>
    /// Release a previous reservation. Called when reserved pages are no longer needed
    /// (e.g., file truncated or deleted).
    /// </summary>
    public void Unreserve(long count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _reservedCount, -count);
        Interlocked.Add(ref _committedCount, -count);
        Debug.Assert(Volatile.Read(ref _committedCount) >= 0, "committed must never go negative");
    }

    /// <summary>
    /// Rent a zeroed page from the pool. Returns nint.Zero if capacity exhausted
    /// (including pages reserved by other files' SetLength).
    /// </summary>
    public nint Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryClaimCommitted(1))
            return nint.Zero; // capacity exhausted (incl. reservations)

        if (_freePages.TryPop(out nint page))
        {
            Interlocked.Increment(ref _rentedCount);
            return page;
        }

        nint allocated = AllocateNewPageIfUnderCapacity();
        if (allocated != nint.Zero)
        {
            Interlocked.Increment(ref _rentedCount);
            return allocated;
        }

        Interlocked.Add(ref _committedCount, -1); // allocation failed — roll back the claim
        return nint.Zero;
    }

    /// <summary>
    /// Rent multiple pages in one batch. Returns actual count rented (may be less than
    /// requested if capacity exhausted). Uses TryPopRange for a single CAS on the free
    /// stack and one CAS on the committed gate for the whole batch.
    /// </summary>
    public int RentBatch(nint[] buffer, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count <= 0) return 0;

        // Claim as many of the requested slots as the committed gate allows — the batch
        // may be granted partially when other files' reservations consume capacity.
        int claim;
        while (true)
        {
            long committed = Volatile.Read(ref _committedCount);
            long room = _maxPages - committed;
            if (room <= 0) return 0;
            claim = (int)Math.Min(count, room);
            if (Interlocked.CompareExchange(ref _committedCount, committed + claim, committed) == committed)
                break;
        }

        int total = _freePages.TryPopRange(buffer, 0, claim);

        // If the free stack didn't have enough, allocate the rest.
        while (total < claim)
        {
            nint page = AllocateNewPageIfUnderCapacity();
            if (page == nint.Zero) break; // physical allocation under capacity
            buffer[total++] = page;
        }

        Interlocked.Add(ref _rentedCount, total);
        int shortfall = claim - total;
        if (shortfall > 0)
            Interlocked.Add(ref _committedCount, -shortfall); // give back unfulfilled claims
        return total;
    }

    /// <summary>
    /// Return a page to the pool. The page is zeroed before returning to the free list.
    /// </summary>
    public unsafe void Return(nint page)
    {
        if (page == nint.Zero) return;
        ObjectDisposedException.ThrowIf(_disposed, this);

        NativeMemory.Clear((void*)page, (nuint)_pageSize);
        Interlocked.Decrement(ref _rentedCount);
        Interlocked.Add(ref _committedCount, -1);
        _freePages.Push(page);
    }

    /// <summary>
    /// Return multiple pages in one batch. Pages are zeroed, then pushed via PushRange (single CAS).
    /// </summary>
    public unsafe void ReturnBatch(nint[] pages, int count)
    {
        if (count <= 0) return;
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (int i = 0; i < count; i++)
        {
            if (pages[i] != nint.Zero)
                NativeMemory.Clear((void*)pages[i], (nuint)_pageSize);
        }

        _freePages.PushRange(pages, 0, count);
        Interlocked.Add(ref _rentedCount, -count);
        Interlocked.Add(ref _committedCount, -count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_allPages.TryPop(out nint page))
        {
            unsafe { NativeMemory.AlignedFree((void*)page); }
        }
    }

    /// <summary>
    /// Atomically claim <paramref name="count"/> committed slots. Returns false when
    /// capacity (including other files' reservations) is exhausted.
    /// </summary>
    private bool TryClaimCommitted(long count)
    {
        while (true)
        {
            long committed = Volatile.Read(ref _committedCount);
            if (committed + count > _maxPages) return false;
            if (Interlocked.CompareExchange(ref _committedCount, committed + count, committed) == committed)
                return true;
        }
    }

    /// <summary>
    /// Allocate one new page from the OS, gated on total/allocation+reservation capacity.
    /// Does not touch <c>_rentedCount</c>/<c>_committedCount</c> — the caller already
    /// claimed a committed slot via <see cref="TryClaimCommitted"/>.
    /// </summary>
    private nint AllocateNewPageIfUnderCapacity()
    {
        long current = Volatile.Read(ref _allocatedCount);
        while (true)
        {
            long reserved = Volatile.Read(ref _reservedCount);
            if (current + reserved >= _maxPages)
                return nint.Zero; // physical allocation limit reached (incl. reservations)

            long next = Interlocked.CompareExchange(ref _allocatedCount, current + 1, current);
            if (next == current)
            {
                // Won the race — allocate from OS
                return AllocateNativePage();
            }
            current = next;
        }
    }

    private unsafe nint AllocateNativePage()
    {
        void* ptr = NativeMemory.AlignedAlloc((nuint)_pageSize, (nuint)_pageSize);
        NativeMemory.Clear(ptr, (nuint)_pageSize);
        nint page = (nint)ptr;
        _allPages.Push(page);
        return page;
    }
}