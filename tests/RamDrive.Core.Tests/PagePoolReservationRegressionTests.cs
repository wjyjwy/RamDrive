using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.Memory;

namespace RamDrive.Core.Tests;

/// <summary>
/// Regression tests for the P0-1 "reservation theft" flaw: Rent() popping a page from the
/// free stack used to ignore _reservedCount, so a file that reserved capacity via SetLength
/// could have it stolen by another file's write (FreeBytes went negative via ulong wraparound;
/// the reserving file then got DISK_FULL while the volume looked empty).
/// Fix: a single atomic "committed" counter gates EVERY capacity consumer — reservation,
/// free-stack pop and fresh OS allocation share one CAS budget.
/// </summary>
public class PagePoolReservationRegressionTests
{
    private static PagePool CreatePool(long capacityMb, bool preAllocate = false)
        => new(
            new OptionsWrapper<RamDriveOptions>(new RamDriveOptions
            {
                CapacityMb = capacityMb,
                PageSizeKb = 64,
                PreAllocate = preAllocate,
            }),
            NullLogger<PagePool>.Instance);

    [Fact]
    public void ReserveFullCapacity_ThenRent_Fails_EvenWhenFreeStackHasPages()
    {
        using var pool = CreatePool(capacityMb: 1); // 16 pages of 64KB
        long maxPages = pool.MaxPages;

        // Put a page on the free stack (the old bug required the stack to be non-empty).
        nint page = pool.Rent();
        page.Should().NotBe(nint.Zero);
        pool.Return(page);

        // File A reserves everything.
        pool.Reserve(maxPages).Should().BeTrue();
        pool.FreeBytes.Should().Be(0);

        // File B tries to write one page: must NOT steal a free-stack page.
        pool.Rent().Should().Be(nint.Zero);
        pool.FreeBytes.Should().Be(0);
        pool.CommittedCount.Should().Be(maxPages);

        // Releasing the reservation makes capacity available again.
        pool.Unreserve(maxPages);
        pool.Rent().Should().NotBe(nint.Zero);
    }

    [Fact]
    public void PreAllocate_ReserveEverything_ThenRent_IsBlocked_AndFreeBytesNeverNegative()
    {
        using var pool = CreatePool(capacityMb: 2, preAllocate: true); // 32 pages, all on the free stack
        long maxPages = pool.MaxPages;
        pool.AllocatedCount.Should().Be(maxPages); // every page physically allocated
        pool.FreeBytes.Should().Be(maxPages * pool.PageSize);

        pool.Reserve(maxPages).Should().BeTrue();

        // Every free-stack page is "promised" to the reservation — no stealing possible.
        pool.Rent().Should().Be(nint.Zero);
        pool.FreeBytes.Should().Be(0);

        // Reserved page consumed by the reserving file: unreserve-then-rent works.
        pool.Unreserve(1);
        nint p = pool.Rent();
        p.Should().NotBe(nint.Zero);
        pool.FreeCount.Should().Be(0); // the slot the unreserved rental re-claimed
        pool.Return(p);
    }

    [Fact]
    public void RentBatch_DoesNotExceedCapacity_WhenReserved()
    {
        using var pool = CreatePool(capacityMb: 1); // 16 pages
        long maxPages = pool.MaxPages;
        pool.Reserve(maxPages - 4).Should().BeTrue(); // 4 pages left

        var buffer = new nint[8];
        int got = pool.RentBatch(buffer, 8);
        got.Should().Be(4); // only the unreserved budget may be rented
        pool.CommittedCount.Should().Be(maxPages);
        pool.FreeBytes.Should().Be(0);
        pool.ReturnBatch(buffer, got);
    }

    [Fact]
    public void Committed_And_FreeBytes_StayInRange_ThroughMixedOps()
    {
        using var pool = CreatePool(capacityMb: 2, preAllocate: true); // 32 pages
        long maxPages = pool.MaxPages;
        var rented = new nint[32];
        int rentedCount = 0;
        long reserved = 0;

        for (int i = 0; i < 500; i++)
        {
            int op = i % 5;
            switch (op)
            {
                case 0 when rentedCount < maxPages - reserved && pool.Reserve(1):
                    reserved++;
                    break;
                case 1 when reserved > 0:
                    pool.Unreserve(1);
                    reserved--;
                    break;
                case 2 when rentedCount < maxPages - reserved:
                    var p = pool.Rent();
                    if (p != nint.Zero) rented[rentedCount++] = p;
                    break;
                case 3 when rentedCount > 0:
                    pool.Return(rented[--rentedCount]);
                    break;
                default:
                    break;
            }

            (pool.RentedCount + pool.ReservedCount).Should().Be(pool.CommittedCount);
            pool.CommittedCount.Should().BeLessThanOrEqualTo(maxPages);
            pool.CommittedCount.Should().BeGreaterThanOrEqualTo(0);
            pool.FreeBytes.Should().BeGreaterThanOrEqualTo(0);
            pool.FreeBytes.Should().BeLessThanOrEqualTo(pool.CapacityBytes);
        }
    }
}