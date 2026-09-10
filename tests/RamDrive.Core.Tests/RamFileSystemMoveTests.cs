using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;

namespace RamDrive.Core.Tests;

/// <summary>
/// Regression tests for structure-operation correctness of RamFileSystem.Move:
/// P0-3 (move into own subtree creates a Parent/Children cycle — the subtree becomes
/// unreachable and Dispose recurses forever; case-only variants bypass the Win32 guard),
/// P0-3 self-replace use-after-dispose, and P1-1 (a file may never replace a directory,
/// even an empty one).
/// </summary>
public class RamFileSystemMoveTests
{
    private static (RamFileSystem fs, PagePool pool) NewFs(long capacityMb = 2)
    {
        var pool = new PagePool(
            new OptionsWrapper<RamDriveOptions>(new RamDriveOptions { CapacityMb = capacityMb, PageSizeKb = 64 }),
            NullLogger<PagePool>.Instance);
        return (new RamFileSystem(pool), pool);
    }

    [Fact]
    public void MoveDirIntoOwnSubtree_ReturnsFalse_TreeIntact()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        fs.CreateDirectory(@"\a").Should().NotBeNull();
        fs.CreateDirectory(@"\a\inner").Should().NotBeNull();

        fs.Move(@"\a", @"\a\b", replace: false).Should().BeFalse();
        fs.Move(@"\a", @"\a\inner\newa", replace: false).Should().BeFalse();

        // No node was lost or relocated — the tree is exactly as before.
        fs.FindNode(@"\a").Should().NotBeNull();
        fs.FindNode(@"\a\inner").Should().NotBeNull();
        fs.FindNode(@"\a\inner\newa").Should().BeNull();
    }

    [Fact]
    public void MoveDirIntoOwnSubtree_CaseVariant_ReturnsFalse_TreeIntact()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        fs.CreateDirectory(@"\a").Should().NotBeNull();
        fs.CreateDirectory(@"\a\inner").Should().NotBeNull();

        // Live-tested bypass on 0.4.7: "\a" → "\A\inner\newa" differs only in case, so the
        // Win32 "target is a subdirectory" check is bypassed and the request reached the FS.
        fs.Move(@"\a", @"\A\inner\newa", replace: false).Should().BeFalse();
        fs.Move(@"\a", @"\A\inner\newa", replace: true).Should().BeFalse();

        fs.FindNode(@"\a").Should().NotBeNull();
        fs.FindNode(@"\a\inner").Should().NotBeNull();
    }

    [Fact]
    public void MoveOntoItself_WithReplace_IsNoOp_AndFileRemainsReadable()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        var file = fs.CreateFile(@"\f.txt");
        file.Should().NotBeNull();
        file!.Content!.Write(0, new byte[1024].AsSpan());

        fs.Move(@"\f.txt", @"\f.txt", replace: true).Should().BeTrue();

        // Regression: the old code disposed the source node (existing.Dispose()) and then
        // re-attached it — every later I/O on the path threw ObjectDisposedException.
        var node = fs.FindNode(@"\f.txt");
        node.Should().NotBeNull();
        node!.Content!.Length.Should().Be(1024);
        var buf = new byte[1024];
        node.Content.Read(0, buf).Should().Be(1024);
        buf.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void CaseOnlyRenameDir_Succeeds_AndParentChainStaysConsistent()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        fs.CreateDirectory(@"\a").Should().NotBeNull();
        fs.CreateDirectory(@"\a\inner").Should().NotBeNull();

        fs.Move(@"\a", @"\A", replace: false).Should().BeTrue();

        // The rename-in-place path must not have created a cycle.
        var node = fs.FindNode(@"\A");
        node.Should().NotBeNull();
        node!.Name.Should().Be("A");
        node.IsDirectory.Should().BeTrue();
        // Walk up: A → root → null (no self-reference).
        node.Parent.Should().NotBeNull();
        node.Parent!.Parent.Should().BeNull();
        fs.FindNode(@"\A\inner").Should().NotBeNull();
    }

    [Fact]
    public void MoveFileOntoEmptyDirectory_ReturnsFalse()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        fs.CreateFile(@"\file.txt").Should().NotBeNull();
        fs.CreateDirectory(@"\emptydir").Should().NotBeNull();

        // NTFS (and MemfsReferenceFs) return ACCESS_DENIED for file-replaces-directory
        // even when the directory is empty; production must agree (P1-1).
        fs.Move(@"\file.txt", @"\emptydir", replace: true).Should().BeFalse();

        fs.FindNode(@"\file.txt").Should().NotBeNull();
        fs.FindNode(@"\emptydir").Should().NotBeNull();
        fs.FindNode(@"\emptydir")!.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void MoveFile_ReplaceExistingFile_Works()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;

        fs.CreateFile(@"\src.txt").Should().NotBeNull();
        var dst = fs.CreateFile(@"\dst.txt");
        dst.Should().NotBeNull();
        dst!.Content!.Write(0, Encoding.ASCII.GetBytes("old content"));

        fs.Move(@"\src.txt", @"\dst.txt", replace: true).Should().BeTrue();
        fs.FindNode(@"\src.txt").Should().BeNull();
        fs.FindNode(@"\dst.txt").Should().NotBeNull();
    }

    [Theory]
    [InlineData(@"\bad?name")]
    [InlineData(@"\name with trailing..")]
    [InlineData(@"\name with space ")]
    [InlineData(@"\CON")]
    [InlineData(@"\a*b")]
    public void CreateFile_InvalidWindowsName_ReturnsNull(string path)
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;
        fs.CreateFile(path).Should().BeNull();
    }

    [Fact]
    public void Move_InvalidTargetName_ReturnsFalse()
    {
        var (fs, pool) = NewFs();
        using var _ = pool;
        using var __ = fs;
        fs.CreateDirectory(@"\a").Should().NotBeNull();
        fs.Move(@"\a", @"\a\CON", replace: false).Should().BeFalse();
        fs.FindNode(@"\a").Should().NotBeNull();
    }
}