using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using WinFsp.Native;

namespace RamDrive.Core.Tests;

/// <summary>
/// Failure-path behavior of WinFspRamAdapter. These tests construct the real adapter, which
/// only works on Windows (RawSecurityDescriptor / ACL APIs throw PlatformNotSupportedException
/// on other platforms — same as the existing security test suites).
/// </summary>
public class WinFspRamAdapterFailurePathTests
{
    private static (WinFspRamAdapter adapter, RamFileSystem fs, PagePool pool) New(int capacityMb = 2)
    {
        var pool = new PagePool(
            new OptionsWrapper<RamDriveOptions>(new RamDriveOptions { CapacityMb = capacityMb, PageSizeKb = 64 }),
            NullLogger<PagePool>.Instance);
        var fs = new RamFileSystem(pool);
        var adapter = new WinFspRamAdapter(fs, new OptionsWrapper<RamDriveOptions>(new RamDriveOptions { CapacityMb = capacityMb, PageSizeKb = 64 }), NullLogger<WinFspRamAdapter>.Instance);
        return (adapter, fs, pool);
    }

    [Fact]
    public void OverwriteFile_DiskFull_LeavesOriginalDataIntact()
    {
        var (adapter, fs, pool) = New(capacityMb: 2); // 32 x 64KB pages
        using var _ = pool;
        using var __ = fs;

        var node = fs.CreateFile(@"\victim.bin")!;
        var original = new byte[30 * 64 * 1024]; // ~30 pages
        for (int i = 0; i < original.Length; i++) original[i] = (byte)(i % 239);
        node.Content!.Write(0, original).Should().Be(original.Length);
        fs.FreeBytes.Should().Be(2 * 64 * 1024); // ~128KB left

        // OverwriteFile hints at a 2MB allocation — way more than is free.
        var info = new FileOperationInfo { Context = node };
        var result = adapter.OverwriteFile(0, replaceFileAttributes: false, allocationSize: 2UL * 1024 * 1024, info: info, ct: CancellationToken.None).Result;

        result.Status.Should().Be(NtStatus.DiskFull);
        // P0-2 regression: the original file must still be intact.
        node.Content.Length.Should().Be(original.Length);
        var readBack = new byte[64 * 1024];
        node.Content.Read(0, readBack).Should().Be(readBack.Length);
        readBack.Should().Equal(original.AsSpan(0, readBack.Length).ToArray());
    }

    [Fact]
    public void OverwriteFile_WithFitAllocation_Succeeds()
    {
        var (adapter, fs, pool) = New(capacityMb: 2);
        using var _ = pool;
        using var __ = fs;

        var node = fs.CreateFile(@"\ok.bin")!;
        node.Content!.Write(0, new byte[10 * 1024]);

        var info = new FileOperationInfo { Context = node };
        var result = adapter.OverwriteFile(0, replaceFileAttributes: false, allocationSize: 100_000UL, info: info, ct: CancellationToken.None).Result;
        result.Status.Should().Be(NtStatus.Success);
        node.Content.Length.Should().Be(0); // truncated
    }

    [Fact]
    public void SetFileSecurity_MalformedDescriptor_ReturnsInvalidSecurityDescriptor_NotException()
    {
        var (adapter, fs, pool) = New(capacityMb: 1);
        using var _ = pool;
        using var __ = fs;

        var node = fs.CreateFile(@"\sec.bin")!;
        var info = new FileOperationInfo { Context = node };

        // 16 bytes of garbage: SD revision byte (0) is invalid → RawSecurityDescriptor throws.
        var garbage = new byte[16];
        int status = adapter.SetFileSecurity(@"\sec.bin", 0x7, garbage, info);
        status.Should().Be(unchecked((int)0xC0000058)); // STATUS_INVALID_SECURITY_DESCRIPTOR

        // Node is unharmed.
        fs.FindNode(@"\sec.bin").Should().NotBeNull();
    }

    [Fact]
    public void SetFileSize_AllocationShrink_AlsoShrinksLogicalSize()
    {
        var (adapter, fs, pool) = New(capacityMb: 2);
        using var _ = pool;
        using var __ = fs;

        var node = fs.CreateFile(@"\shrink.bin")!;
        node.Content!.SetLength(5 * 64 * 1024).Should().BeTrue();
        node.Content!.Write(0, new byte[1024]).Should().Be(1024);

        var info = new FileOperationInfo { Context = node };
        var result = adapter.SetFileSize(@"\shrink.bin", (ulong)(64 * 1024), setAllocationSize: true, info: info, ct: CancellationToken.None).Result;
        result.Status.Should().Be(NtStatus.Success);

        // P1-2: match NTFS + MemfsReferenceFs — FileSize follows the shrunk allocation.
        node.Content.Length.Should().Be(64 * 1024);
    }
}