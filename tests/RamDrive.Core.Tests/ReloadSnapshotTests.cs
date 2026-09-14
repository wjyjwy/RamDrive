using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;

namespace RamDrive.Core.Tests;

/// <summary>
/// Snapshot / restore round-trips for the reload feature. The whole volume lives in RAM, so
/// a reload captures a FileSystemSnapshot (no disk I/O), builds a fresh filesystem from new
/// configuration, restores the snapshot into it, and re-mounts. These tests exercise the
/// platform-neutral restore logic; the Windows-only mount wiring lives in WinFspHostedService.
/// </summary>
public class ReloadSnapshotTests
{
    private const long PageSize = 64 * 1024;

    private static (RamFileSystem fs, PagePool pool) NewFs(long capacityMb = 2, int pageSizeKb = 64)
    {
        var pool = new PagePool(
            new OptionsWrapper<RamDriveOptions>(new RamDriveOptions { CapacityMb = capacityMb, PageSizeKb = pageSizeKb }),
            NullLogger<PagePool>.Instance);
        return (new RamFileSystem(pool), pool);
    }

    [Fact]
    public void RoundTrip_PreservesTree_Data_Times_Attributes_AndSparseLayout()
    {
        var (fs, pool) = NewFs(2);
        using var _ = pool;

        // Dense file: 1.5 pages of patterned data.
        var dense = fs.CreateFile(@"\dense.bin")!;
        var denseData = new byte[(int)(PageSize * 1.5)];
        for (int i = 0; i < denseData.Length; i++) denseData[i] = (byte)(i % 251);
        dense.Content!.Write(0, denseData).Should().Be(denseData.Length);
        dense.Attributes |= FileAttributes.Archive;
        dense.LastWriteTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        // Sparse file: 3 pages logical, data only in pages 0 and 2.
        var sparse = fs.CreateFile(@"\sparse.bin")!;
        sparse.Content!.SetLength(3 * PageSize).Should().BeTrue();
        sparse.Content.Write(0, new byte[] { 0x11, 0x22 }).Should().Be(2);
        sparse.Content.Write(2 * PageSize, new byte[] { 0xAA, 0xBB }).Should().Be(2);
        long sparseAllocBefore = sparse.Content.AllocatedBytes;

        // Nested directory structure.
        fs.CreateDirectory(@"\dir");
        fs.CreateDirectory(@"\dir\sub");
        var nested = fs.CreateFile(@"\dir\sub\n.txt")!;
        nested.Content!.Write(0, new byte[] { 1, 2, 3 }).Should().Be(3);

        // Snapshot and restore into a fresh filesystem.
        var snapshot = fs.CreateSnapshot();
        var (fs2, pool2) = NewFs(2);
        using var _2 = pool2;
        using var _3 = fs2;
        fs2.RestoreSnapshot(snapshot).Should().BeNull();

        // Tree shape.
        fs2.FindNode(@"\dense.bin").Should().NotBeNull();
        fs2.FindNode(@"\sparse.bin").Should().NotBeNull();
        fs2.FindNode(@"\dir\sub\n.txt").Should().NotBeNull();
        fs2.FindNode(@"\missing").Should().BeNull();

        // Dense content byte-for-byte.
        var dense2 = fs2.FindNode(@"\dense.bin")!;
        var readBack = new byte[denseData.Length];
        dense2.Content!.Read(0, readBack).Should().Be(denseData.Length);
        readBack.Should().Equal(denseData);

        // Metadata.
        dense2.LastWriteTime.Should().Be(dense.LastWriteTime);
        dense2.Attributes.Should().HaveFlag(FileAttributes.Archive);

        // Sparse layout preserved: length, zero middle page, real edge pages, and the
        // allocated-page account matches the original (sparse holes stay sparse).
        var sparse2 = fs2.FindNode(@"\sparse.bin")!;
        sparse2.Content!.Length.Should().Be(3 * PageSize);
        sparse2.Content.AllocatedBytes.Should().Be(sparseAllocBefore);
        var mid = new byte[4];
        sparse2.Content.Read(PageSize, mid).Should().Be(4);
        mid.Should().Equal(0, 0, 0, 0); // hole reads as zeroes
        var tail = new byte[4];
        sparse2.Content.Read(2 * PageSize, tail).Should().Be(4);
        tail.Take(2).Should().Equal((byte)0xAA, (byte)0xBB);

        // Nested file content.
        var n2 = fs2.FindNode(@"\dir\sub\n.txt")!;
        var nb = new byte[3];
        n2.Content!.Read(0, nb).Should().Be(3);
        nb.Should().Equal(1, 2, 3);

        // IndexNumber uniqueness holds across the restored tree.
        var all = new[] { dense2, sparse2, n2 };
        all.Select(n => n.IndexNumber).Distinct().Count().Should().Be(all.Length);
    }

    [Fact]
    public void Restore_IntoNonEmptyFilesystem_ReturnsError()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;
        fs.CreateFile(@"\a.txt");

        var snapshot = fs.CreateSnapshot();

        var (fs2, pool2) = NewFs();
        using var _2 = pool2;
        fs2.CreateFile(@"\already.there");
        fs2.RestoreSnapshot(snapshot).Should().NotBeNull();
    }

    [Fact]
    public void Restore_WhenCapacityTooSmall_ReturnsError_AndLeavesTargetEmpty()
    {
        var (fs, pool) = NewFs(2);
        using var _ = pool;
        using var __ = fs;
        var f = fs.CreateFile(@"\big.bin")!;
        f.Content!.SetLength(PageSize * 20).Should().BeTrue(); // 20 pages used
        var snapshot = fs.CreateSnapshot();

        var (small, smallPool) = NewFs(1); // 16 pages total
        using var _2 = smallPool;
        using var _3 = small;
        var error = small.RestoreSnapshot(snapshot);
        error.Should().NotBeNull();
        error.Should().Contain("pages");
        small.FindNode(@"\big.bin").Should().BeNull(); // nothing partially restored
    }

    [Fact]
    public void Restore_WithDifferentPageSize_StillByteCorrect()
    {
        // Reload with a changed page size must re-chunk data correctly.
        var (fs, pool) = NewFs(2, pageSizeKb: 64);
        using var _ = pool;
        using var __ = fs;

        var f = fs.CreateFile(@"\x.bin")!;
        var data = new byte[200_000]; // spans 4 x 64KB pages
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31);
        f.Content!.Write(0, data).Should().Be(data.Length);

        var snapshot = fs.CreateSnapshot();

        var (fs2, pool2) = NewFs(2, pageSizeKb: 32); // 32KB pages
        using var _2 = pool2;
        using var _3 = fs2;
        fs2.RestoreSnapshot(snapshot).Should().BeNull();

        var f2 = fs2.FindNode(@"\x.bin")!;
        var readBack = new byte[data.Length];
        f2.Content!.Read(0, readBack).Should().Be(data.Length);
        readBack.Should().Equal(data);
    }

    [Fact]
    public void AfterRestore_FileCanBeModified_AndReadsStayConsistent()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;
        var f = fs.CreateFile(@"\mod.bin")!;
        f.Content!.Write(0, Enumerable.Repeat((byte)0xAB, 1000).ToArray());

        var snapshot = fs.CreateSnapshot();
        var (fs2, pool2) = NewFs();
        using var _2 = pool2;
        fs2.RestoreSnapshot(snapshot).Should().BeNull();

        var m = fs2.FindNode(@"\mod.bin")!;
        m.Content!.Write(0, new byte[] { 0x00, 0x01, 0x02 }).Should().Be(3); // overwrite start
        var buf = new byte[1000];
        m.Content.Read(0, buf).Should().Be(1000);
        buf.Take(3).Should().Equal((byte)0x00, (byte)0x01, (byte)0x02);
        buf.Skip(3).Should().OnlyContain(b => b == 0xAB);
    }

    [Fact]
    public void RoundTrip_PreservesExactLogicalLength_WhenNotPageAligned()
    {
        // Regression: the restore wrote a full page (pageSize bytes) at a page-aligned offset,
        // and PagedFileContent.Write grows the logical length to the end of the written span.
        // Every file whose length was not a page multiple therefore came back padded with
        // zeroes up to the page boundary ("null characters at the end of the file" after a
        // capacity-only reload).
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        var f = fs.CreateFile(@"\notes.txt")!;
        var data = new byte[1000]; // not a page multiple
        for (int i = 0; i < data.Length; i++) data[i] = (byte)('a' + i % 26);
        f.Content!.Write(0, data).Should().Be(data.Length);

        // Same, but the last page is only partially covered by data before the EOF.
        var g = fs.CreateFile(@"\extended.bin")!;
        g.Content!.Write(0, new byte[10]).Should().Be(10);
        g.Content.Write(123, new byte[7]).Should().Be(7);   // still inside page 0
        g.Content.SetLength(4096).Should().BeTrue();        // logical length beyond the written data

        // Sparse file: length only, no allocated page at all.
        var s = fs.CreateFile(@"\len-only.bin")!;
        s.Content!.SetLength(100).Should().BeTrue();

        var snapshot = fs.CreateSnapshot();

        // Restore into a LARGER pool — the capacity-only edit that triggered the bug.
        var (fs2, pool2) = NewFs(4);
        using var _2 = pool2;
        using var _3 = fs2;
        pool2.CapacityBytes.Should().BeGreaterThan(pool.CapacityBytes);
        fs2.RestoreSnapshot(snapshot).Should().BeNull();

        fs2.FindNode(@"\notes.txt")!.Size.Should().Be(data.Length);
        fs2.FindNode(@"\extended.bin")!.Size.Should().Be(4096);
        fs2.FindNode(@"\len-only.bin")!.Size.Should().Be(100);

        // Content is still byte-for-byte, and nothing is readable past the logical end.
        var probe = new byte[data.Length + 8];
        fs2.FindNode(@"\notes.txt")!.Content!.Read(0, probe).Should().Be(data.Length);
        probe.Take(data.Length).Should().Equal(data);
    }
}