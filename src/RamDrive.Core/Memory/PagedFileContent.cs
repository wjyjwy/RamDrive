using System.Runtime.InteropServices;

namespace RamDrive.Core.Memory;

/// <summary>
/// File content stored as a page table: an array of native page pointers.
/// Supports sparse allocation — only written pages consume memory.
/// Thread-safe via ReaderWriterLockSlim.
/// </summary>
public sealed class PagedFileContent : IDisposable
{
    private readonly PagePool _pool;
    private readonly int _pageSize;
    private nint[] _pages;      // index → native page pointer; nint.Zero = not allocated
    private long _length;       // logical file size in bytes
    private int _allocatedPageCount; // pages actually holding data (non-zero entries in _pages)
    private int _reservedPages; // pages reserved in pool but not yet allocated
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    // int (not bool) so Dispose can claim it atomically with Interlocked.Exchange.
    private int _disposed;

    public long Length
    {
        get
        {
            _lock.EnterReadLock();
            try { return _length; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Actual bytes allocated (pages with data × page size).
    /// Sparse pages (nint.Zero) are not counted.
    /// </summary>
    public long AllocatedBytes
    {
        get
        {
            _lock.EnterReadLock();
            try { return (long)_allocatedPageCount * _pageSize; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Pages this file has reserved in the pool but not yet backed with data — the
    /// capacity <see cref="SetLength"/> claimed so a later write cannot fail. A snapshot
    /// must carry this across a reload: a file that was SetLength'd to 20 pages and never
    /// written still holds a genuine 20-page claim on the volume.
    /// </summary>
    public int ReservedPages
    {
        get
        {
            _lock.EnterReadLock();
            try { return Volatile.Read(ref _reservedPages); }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>Pages actually holding data (non-zero page-table entries).</summary>
    public int AllocatedPageCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _allocatedPageCount; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public PagedFileContent(PagePool pool)
    {
        _pool = pool;
        _pageSize = pool.PageSize;
        _pages = [];
        _length = 0;
    }

    /// <summary>
    /// Read data from the file into the destination span. Returns bytes actually read.
    /// Unallocated pages read as zeroes.
    /// </summary>
    public unsafe int Read(long offset, Span<byte> destination)
    {
        _lock.EnterReadLock();
        try
        {
            if (offset >= _length) return 0;

            int toRead = (int)Math.Min(destination.Length, _length - offset);
            int totalRead = 0;

            while (totalRead < toRead)
            {
                int pageIndex = (int)((offset + totalRead) / _pageSize);
                int pageOffset = (int)((offset + totalRead) % _pageSize);
                int chunkSize = Math.Min(toRead - totalRead, _pageSize - pageOffset);

                if (pageIndex < _pages.Length && _pages[pageIndex] != nint.Zero)
                {
                    new Span<byte>((byte*)_pages[pageIndex] + pageOffset, chunkSize)
                        .CopyTo(destination.Slice(totalRead, chunkSize));
                }
                else
                {
                    // Sparse: unallocated page reads as zeroes
                    destination.Slice(totalRead, chunkSize).Clear();
                }

                totalRead += chunkSize;
            }

            return totalRead;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Write data to the file from the source span. Returns bytes written, or -1 if out of disk space.
    /// A write ending past the current EOF grows the logical length to <c>offset + source.Length</c>:
    /// callers that must NOT extend the file (e.g. snapshot restore replaying whole page buffers
    /// for a file whose length is not a page multiple) have to clip the span to the logical
    /// length themselves.
    /// Pages are pre-allocated outside the write lock to minimize lock hold time.
    /// </summary>
    public unsafe int Write(long offset, ReadOnlySpan<byte> source)
    {
        if (source.Length == 0) return 0;

        long endOffset = offset + source.Length;
        int requiredPages = (int)((endOffset + _pageSize - 1) / _pageSize);

        // --- Phase 1: determine which pages need allocation (read lock only) ---
        int neededCount = 0;
        _lock.EnterReadLock();
        try
        {
            int firstPage = (int)(offset / _pageSize);
            int lastPage = (int)((endOffset - 1) / _pageSize);
            for (int i = firstPage; i <= lastPage; i++)
            {
                if (i >= _pages.Length || _pages[i] == nint.Zero)
                    neededCount++;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // --- Phase 2: batch-allocate pages outside any lock ---
        // Unreserve own reservations first so AllocateNewPageIfUnderCapacity
        // can use that capacity. CAS loop prevents TOCTOU with concurrent SetLength.
        nint[]? preAllocated = null;
        int preAllocatedCount = 0;
        int unreservedForAlloc = 0;
        if (neededCount > 0)
        {
            // CAS loop: atomically read and decrement _reservedPages
            while (true)
            {
                int currentReserved = Volatile.Read(ref _reservedPages);
                if (currentReserved <= 0) break;
                int toUnreserve = Math.Min(currentReserved, neededCount);
                if (Interlocked.CompareExchange(ref _reservedPages, currentReserved - toUnreserve, currentReserved) == currentReserved)
                {
                    unreservedForAlloc = toUnreserve;
                    break;
                }
            }
            if (unreservedForAlloc > 0)
                _pool.Unreserve(unreservedForAlloc);

            preAllocated = new nint[neededCount];
            preAllocatedCount = _pool.RentBatch(preAllocated, neededCount);
            if (preAllocatedCount < neededCount)
            {
                // Not enough capacity — return what we got, then try to restore the
                // reservation we briefly released. P1-3: only restore the file-level
                // count when the pool can still honor it; if another writer claimed the
                // capacity between our Unreserve and this failure, the reservation is
                // genuinely gone and re-adding the file-level count here would let a
                // later Unreserve drive the pool's committed counter negative. The
                // file is left at its pre-write length; a later SetLength re-reserves.
                if (preAllocatedCount > 0)
                    _pool.ReturnBatch(preAllocated, preAllocatedCount);
                if (unreservedForAlloc > 0 && _pool.Reserve(unreservedForAlloc))
                    Interlocked.Add(ref _reservedPages, unreservedForAlloc);
                return -1;
            }
        }

        // --- Phase 3: write lock — only page table + memcpy, no OS allocations ---
        // Reservations already consumed in Phase 2 via Unreserve.
        _lock.EnterWriteLock();
        try
        {
            if (requiredPages > _pages.Length)
                Array.Resize(ref _pages, requiredPages);

            int preAllocIdx = 0;
            int totalWritten = 0;

            while (totalWritten < source.Length)
            {
                int pageIndex = (int)((offset + totalWritten) / _pageSize);
                int pageOffset = (int)((offset + totalWritten) % _pageSize);
                int chunkSize = Math.Min(source.Length - totalWritten, _pageSize - pageOffset);

                if (_pages[pageIndex] == nint.Zero)
                {
                    // Use pre-allocated page
                    if (preAllocated != null && preAllocIdx < preAllocatedCount)
                    {
                        _pages[pageIndex] = preAllocated[preAllocIdx++];
                    }
                    else
                    {
                        // Deficit: concurrent truncation between Phase 1 and Phase 3
                        // freed pages, creating more holes than pre-allocated for.
                        // Fall back to single Rent. CAS loop prevents TOCTOU with
                        // concurrent Phase 2 on another write to this file.
                        while (true)
                        {
                            int curReserved = Volatile.Read(ref _reservedPages);
                            if (curReserved <= 0) break;
                            if (Interlocked.CompareExchange(ref _reservedPages, curReserved - 1, curReserved) == curReserved)
                            {
                                _pool.Unreserve(1);
                                break;
                            }
                        }
                        nint page = _pool.Rent();
                        if (page == nint.Zero)
                        {
                            // Return unused pre-allocated pages
                            if (preAllocated != null && preAllocIdx < preAllocatedCount)
                                _pool.ReturnBatch(preAllocated[preAllocIdx..], preAllocatedCount - preAllocIdx);
                            // Even on a mid-write capacity failure, push the logical length up to the
                            // bytes we DID copy (offset + totalWritten): the pages are already in
                            // _pages and counted in _allocatedPageCount, so skipping this (or returning
                            // -1 outright) would orphan data the caller believes it wrote — a later
                            // read would return zeroes. Use the partial total, NOT the full request
                            // endOffset, so the file is not extended past what actually landed.
                            long partialEnd = offset + totalWritten;
                            if (totalWritten > 0 && partialEnd > _length)
                                _length = partialEnd;
                            return totalWritten > 0 ? totalWritten : -1;
                        }
                        _pages[pageIndex] = page;
                    }
                    _allocatedPageCount++;
                }

                source.Slice(totalWritten, chunkSize)
                    .CopyTo(new Span<byte>((byte*)_pages[pageIndex] + pageOffset, chunkSize));

                totalWritten += chunkSize;
            }

            // Return any excess pre-allocated pages (rare: another thread filled gaps between phases)
            if (preAllocated != null && preAllocIdx < preAllocatedCount)
                _pool.ReturnBatch(preAllocated[preAllocIdx..], preAllocatedCount - preAllocIdx);

            if (endOffset > _length)
                _length = endOffset;

            // Adjust excess reservations caused by concurrent SetLength
            // during write phases. DoExtend may have over-reserved because
            // writePreAlloc pages weren't yet visible in _allocatedPageCount.
            int currentPageCount = (int)((_length + _pageSize - 1) / _pageSize);
            int correctReserved = Math.Max(0, currentPageCount - _allocatedPageCount);
            while (true)
            {
                int currentReserved = Volatile.Read(ref _reservedPages);
                if (currentReserved <= correctReserved) break;
                int excess = currentReserved - correctReserved;
                if (Interlocked.CompareExchange(ref _reservedPages, correctReserved, currentReserved) == currentReserved)
                {
                    _pool.Unreserve(excess);
                    break;
                }
            }

            return totalWritten;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Copy every allocated page's contents into <paramref name="target"/> as
    /// (page-start byte offset, page bytes) pairs. Sparse pages are skipped. Used by
    /// <see cref="RamDrive.Core.FileSystem.RamFileSystem.CreateSnapshot"/> to keep file
    /// data in memory across a reload.
    /// </summary>
    public unsafe void EnumerateAllocatedData(List<(long Offset, byte[] Data)> target)
    {
        _lock.EnterReadLock();
        try
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                if (_pages[i] == nint.Zero) continue;
                var copy = new byte[_pageSize];
                new ReadOnlySpan<byte>((byte*)_pages[i], _pageSize).CopyTo(copy);
                target.Add((i * (long)_pageSize, copy));
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Restore the LOGICAL length and the reserved-page count, without reserving capacity
    /// for the sparse holes the length spans. For snapshot/restore only.
    ///
    /// <para>On reload the real data pages are replayed first with <see cref="Write"/>,
    /// which reserves only the pages it actually touches. Two things then need restoring:
    /// the original logical length, and any reservation the live file still held from an
    /// earlier <see cref="SetLength"/>. Calling <see cref="SetLength"/> here instead would
    /// reserve every page across the hole, so a file that is (say) 64&nbsp;MB logical but
    /// 64&nbsp;KB allocated would demand 1024 extra pages — failing the restore or silently
    /// eating the new volume's free space.</para>
    ///
    /// <paramref name="reservedPages"/> is the count captured from the live file; the
    /// difference between it and what the replayed pages already reserved is claimed from
    /// the pool here. Reads past the replayed data return zeroes, as in the live file.</summary>
    public bool RestoreSetLength(long newLength, int reservedPages)
    {
        if (newLength < 0 || reservedPages < 0) return false;

        _lock.EnterWriteLock();
        try
        {
            if (newLength < _length)
            {
                // Truncation: free pages beyond the new end (mirrors SetLength).
                int newPageCount = (int)((newLength + _pageSize - 1) / _pageSize);
                int toFreeCount = 0;
                for (int i = newPageCount; i < _pages.Length; i++)
                    if (_pages[i] != nint.Zero) toFreeCount++;
                if (toFreeCount > 0)
                {
                    var toFree = new nint[toFreeCount];
                    int idx = 0;
                    for (int i = newPageCount; i < _pages.Length; i++)
                    {
                        if (_pages[i] != nint.Zero)
                        {
                            toFree[idx++] = _pages[i];
                            _pages[i] = nint.Zero;
                        }
                    }
                    _pool.ReturnBatch(toFree, toFreeCount);
                    _allocatedPageCount -= toFreeCount;
                }
                if (newPageCount < _pages.Length)
                    Array.Resize(ref _pages, newPageCount);
            }
            else if (newLength > _length)
            {
                // Extension grows only the page TABLE — the holes are deliberately left
                // unbacked (see the remarks above).
                int newPageCount = (int)((newLength + _pageSize - 1) / _pageSize);
                if (newPageCount > _pages.Length)
                    Array.Resize(ref _pages, newPageCount);
            }

            // Reinstate the live file's reservation. The replayed Write calls already
            // reserved a page per written page; claim only the remainder so the pool's
            // committed counter ends up exactly where the source volume had it.
            while (true)
            {
                int current = Volatile.Read(ref _reservedPages);
                if (current >= reservedPages) break;
                int need = reservedPages - current;
                if (!_pool.Reserve(need))
                {
                    // The new pool cannot honour the claim. Fail the restore rather than
                    // silently shrinking the file's guaranteed capacity.
                    return false;
                }
                if (Interlocked.CompareExchange(ref _reservedPages, current + need, current) == current)
                    break;
                _pool.Unreserve(need);
            }

            _length = newLength;
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Set logical file length. Truncation frees pages beyond the new length.
    /// Extension reserves capacity in the pool (without allocating pages) to guarantee
    /// that subsequent writes will succeed — required for kernel cache mode where the
    /// cache manager calls SetFileSize before issuing writes.
    /// Returns false if extending would exceed capacity.
    /// </summary>
    public bool SetLength(long newLength)
    {
        if (newLength < 0) return false;

        _lock.EnterWriteLock();
        try
        {
            if (newLength < _length)
            {
                // --- Truncation ---
                int newPageCount = (int)((newLength + _pageSize - 1) / _pageSize);

                // Zero partial data in the last retained page
                if (newLength > 0 && newLength % _pageSize != 0 && newPageCount > 0)
                {
                    int lastPageIndex = newPageCount - 1;
                    if (lastPageIndex < _pages.Length && _pages[lastPageIndex] != nint.Zero)
                    {
                        int keepBytes = (int)(newLength % _pageSize);
                        unsafe
                        {
                            NativeMemory.Clear(
                                (byte*)_pages[lastPageIndex] + keepBytes,
                                (nuint)(_pageSize - keepBytes));
                        }
                    }
                }

                // Collect pages to free, then batch-return
                int toFreeCount = 0;
                for (int i = newPageCount; i < _pages.Length; i++)
                {
                    if (_pages[i] != nint.Zero)
                        toFreeCount++;
                }

                if (toFreeCount > 0)
                {
                    nint[] toFree = new nint[toFreeCount];
                    int idx = 0;
                    for (int i = newPageCount; i < _pages.Length; i++)
                    {
                        if (_pages[i] != nint.Zero)
                        {
                            toFree[idx++] = _pages[i];
                            _pages[i] = nint.Zero;
                        }
                    }
                    _pool.ReturnBatch(toFree, toFreeCount);
                    _allocatedPageCount -= toFreeCount;
                }

                if (newPageCount < _pages.Length)
                    Array.Resize(ref _pages, newPageCount);

                // Release excess reservations: keep only enough to cover
                // unallocated pages in the retained range.
                // CAS loop, not a plain read-modify-write: Phase 2 of Write mutates
                // _reservedPages WITHOUT taking this write lock, so a plain store here
                // would silently drop its decrement and leave the file-level count
                // disagreeing with the pool's committed counter (a later Unreserve would
                // then drive that counter negative).
                int newReserved = Math.Max(0, newPageCount - _allocatedPageCount);
                while (true)
                {
                    int currentReserved = Volatile.Read(ref _reservedPages);
                    int reserveDelta = currentReserved - newReserved;
                    if (reserveDelta <= 0) break;
                    if (Interlocked.CompareExchange(
                            ref _reservedPages, newReserved, currentReserved) == currentReserved)
                    {
                        _pool.Unreserve(reserveDelta);
                        break;
                    }
                }
            }
            else if (newLength > _length)
            {
                // --- Extension: reserve capacity for unallocated pages ---
                // Single file cannot exceed total pool capacity.
                if (newLength > _pool.CapacityBytes)
                    return false;

                int newPageCount = (int)((newLength + _pageSize - 1) / _pageSize);

                // Reserve pool capacity for pages not yet allocated or reserved.
                // This guarantees subsequent writes will succeed and prevents
                // aggregate file sizes from exceeding total capacity.
                // CAS loop for the same reason as the shrink branch above: the count is
                // also mutated lock-free by Write's Phase 2.
                while (true)
                {
                    int currentReserved = Volatile.Read(ref _reservedPages);
                    int additionalNeeded = newPageCount - _allocatedPageCount - currentReserved;
                    if (additionalNeeded <= 0) break;
                    if (!_pool.Reserve(additionalNeeded))
                        return false;
                    if (Interlocked.CompareExchange(
                            ref _reservedPages, currentReserved + additionalNeeded,
                            currentReserved) == currentReserved)
                        break;
                    // Lost the race: undo this reservation and retry against the fresh
                    // count so the pool and the file-level number stay in step.
                    _pool.Unreserve(additionalNeeded);
                }

                if (newPageCount > _pages.Length)
                    Array.Resize(ref _pages, newPageCount);
            }

            _length = newLength;
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        // Interlocked, not a plain flag test: two threads can race through
        // "if (_disposed) return; _disposed = true;" and the loser then calls
        // _lock.EnterWriteLock() on a ReaderWriterLockSlim that the winner already
        // disposed — an ObjectDisposedException out of Dispose itself. Exchange makes
        // exactly one caller the winner; everyone else returns immediately.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _lock.EnterWriteLock();
        try
        {
            // Release any outstanding reservations. Exchange-to-zero (rather than read
            // then assign) so a concurrent lock-free Phase-2 decrement in Write cannot be
            // lost and leave the pool's committed counter permanently over-counted.
            int reserved = Interlocked.Exchange(ref _reservedPages, 0);
            if (reserved > 0)
                _pool.Unreserve(reserved);

            for (int i = 0; i < _pages.Length; i++)
            {
                if (_pages[i] != nint.Zero)
                {
                    _pool.Return(_pages[i]);
                    _pages[i] = nint.Zero;
                }
            }
            _pages = [];
            _length = 0;
            _allocatedPageCount = 0;
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }
}
