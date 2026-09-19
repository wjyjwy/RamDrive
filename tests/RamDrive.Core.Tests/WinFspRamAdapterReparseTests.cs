// Unit tests for the reparse-point (symlink / junction) callbacks on WinFspRamAdapter:
// Get/Set/DeleteReparsePoint (handle-based, FSCTL_*_REPARSE_POINT) and
// GetReparsePointByName (name-based, used by WinFsp 2.x user-mode link resolution).
//
// These exercise the adapter directly without a mount (FileOperationInfo.Context is
// seeded with a FileNode), which is enough to pin memfs-lockstep semantics:
//   - set stores the opaque blob + tag + FILE_ATTRIBUTE_REPARSE_POINT
//   - get round-trips the blob
//   - tag/GUID mismatch on replace and delete is rejected
//   - a non-empty directory cannot become a junction
//   - name probe: missing -> OBJECT_NAME_NOT_FOUND, plain node -> NOT_A_REPARSE_POINT
// End-to-end traversal (STATUS_REPARSE name resolution) is covered by the mount-based
// integration tests in RamDrive.IntegrationTests.ReparsePointTests.

using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using WinFsp.Native;

namespace RamDrive.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class WinFspRamAdapterReparseTests : IDisposable
{
    private const uint SymlinkTag = 0xA000000C;
    private const uint MountPointTag = 0xA0000003;

    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;
    private readonly WinFspRamAdapter _adapter;

    public WinFspRamAdapterReparseTests()
    {
        var opts = new RamDriveOptions { CapacityMb = 8, PageSizeKb = 64, VolumeLabel = "Test" };
        _pool = new PagePool(new OptionsWrapper<RamDriveOptions>(opts), NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);
        _adapter = new WinFspRamAdapter(_fs, new OptionsWrapper<RamDriveOptions>(opts),
            NullLogger<WinFspRamAdapter>.Instance);
    }

    public void Dispose()
    {
        _fs.Dispose();
        _pool.Dispose();
    }

    private static byte[] SymlinkBuffer(string target)
    {
        // Minimal valid SYMBOLIC_LINK_REPARSE_BUFFER-style blob; the FS treats the payload
        // as opaque, only the leading tag is inspected. 64 bytes to stay realistic.
        var b = new byte[64];
        BitConverter.GetBytes(SymlinkTag).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)(target.Length * 2)).CopyTo(b, 4); // ReparseDataLength
        BitConverter.GetBytes((ushort)0).CopyTo(b, 8);                  // SYMLINK_FLAG_RELATIVE
        return b;
    }

    private FileOperationInfo Open(FileNode node) => new() { Context = node, IsDirectory = node.IsDirectory };

    [Fact]
    public void SetReparsePoint_StoresTagBlobAndAttribute()
    {
        var node = _fs.CreateFile(@"\link")!;
        var info = Open(node);
        var blob = SymlinkBuffer("target");

        _adapter.SetReparsePoint(@"\link", blob, info).Should().Be(NtStatus.Success);

        node.IsReparsePoint.Should().BeTrue();
        node.ReparseTag.Should().Be(SymlinkTag);
        node.Attributes.Should().HaveFlag(FileAttributes.ReparsePoint);
        // Stored blob is an independent copy.
        node.ReparseData.Should().Equal(blob);
        node.ReparseData.Should().NotBeSameAs(blob);
    }

    [Fact]
    public void GetReparsePoint_RoundTripsBlob_AndRejectsPlainOrMissing()
    {
        var node = _fs.CreateFile(@"\link")!;
        var info = Open(node);
        _adapter.SetReparsePoint(@"\link", SymlinkBuffer("t"), info).Should().Be(NtStatus.Success);

        byte[]? data = null;
        _adapter.GetReparsePoint(@"\link", ref data, info).Should().Be(NtStatus.Success);
        data.Should().NotBeNull().And.HaveCount(64);

        var plain = _fs.CreateFile(@"\plain")!;
        byte[]? none = null;
        _adapter.GetReparsePoint(@"\plain", ref none, Open(plain)).Should().Be(NtStatus.NotAReparse);

        var missing = new FileOperationInfo();
        byte[]? missingData = null;
        _adapter.GetReparsePoint(@"\ghost", ref missingData, missing).Should().Be(NtStatus.ObjectNameNotFound);
    }

    [Fact]
    public void SetReparsePoint_ReplacingWithDifferentTag_IsRejected()
    {
        var node = _fs.CreateFile(@"\link")!;
        var info = Open(node);
        _adapter.SetReparsePoint(@"\link", SymlinkBuffer("a"), info).Should().Be(NtStatus.Success);

        var mount = new byte[64];
        BitConverter.GetBytes(MountPointTag).CopyTo(mount, 0);
        _adapter.SetReparsePoint(@"\link", mount, info).Should().Be(NtStatus.IoReparseTagMismatch);

        // Original point survives the rejected replacement.
        node.ReparseTag.Should().Be(SymlinkTag);
    }

    [Fact]
    public void SetReparsePoint_TooShortBuffer_IsRejected()
    {
        var node = _fs.CreateFile(@"\bad")!;
        _adapter.SetReparsePoint(@"\bad", new byte[2], Open(node)).Should().Be(NtStatus.IoReparseDataInvalid);
        node.IsReparsePoint.Should().BeFalse();
    }

    [Fact]
    public void SetReparsePoint_OnNonEmptyDirectory_ReturnsDirectoryNotEmpty()
    {
        _fs.CreateDirectory(@"\junction");
        var dir = _fs.FindNode(@"\junction")!;
        _ = _fs.CreateFile(@"\junction\child")!;

        var blob = new byte[64];
        BitConverter.GetBytes(MountPointTag).CopyTo(blob, 0);
        _adapter.SetReparsePoint(@"\junction", blob, Open(dir)).Should().Be(NtStatus.DirectoryNotEmpty);
        dir.IsReparsePoint.Should().BeFalse();
    }

    [Fact]
    public void SetReparsePoint_OnEmptyDirectory_Succeeds_ThenDeleteRestoresPlainState()
    {
        _fs.CreateDirectory(@"\junction");
        var dir = _fs.FindNode(@"\junction")!;
        var info = Open(dir);
        var blob = new byte[64];
        BitConverter.GetBytes(MountPointTag).CopyTo(blob, 0);

        _adapter.SetReparsePoint(@"\junction", blob, info).Should().Be(NtStatus.Success);
        dir.IsReparsePoint.Should().BeTrue();
        dir.Attributes.Should().HaveFlag(FileAttributes.ReparsePoint | FileAttributes.Directory);

        // Delete requires a matching-tag buffer (FSCTL_DELETE_REPARSE_POINT payload).
        _adapter.DeleteReparsePoint(@"\junction", blob, info).Should().Be(NtStatus.Success);
        dir.IsReparsePoint.Should().BeFalse();
        dir.ReparseTag.Should().Be(0);
        dir.Attributes.Should().NotHaveFlag(FileAttributes.ReparsePoint);
    }

    [Fact]
    public void DeleteReparsePoint_WithWrongTag_OrOnPlainNode_Fails()
    {
        var node = _fs.CreateFile(@"\link")!;
        var info = Open(node);
        _adapter.SetReparsePoint(@"\link", SymlinkBuffer("a"), info).Should().Be(NtStatus.Success);

        var wrong = new byte[64];
        BitConverter.GetBytes(MountPointTag).CopyTo(wrong, 0);
        _adapter.DeleteReparsePoint(@"\link", wrong, info).Should().Be(NtStatus.IoReparseTagMismatch);
        node.IsReparsePoint.Should().BeTrue("rejected delete must not strip the point");

        var plain = _fs.CreateFile(@"\plain")!;
        _adapter.DeleteReparsePoint(@"\plain", SymlinkBuffer("a"), Open(plain)).Should().Be(NtStatus.NotAReparse);
    }

    [Fact]
    public void GetReparsePointByName_ReportsMissing_Plain_AndLink_States()
    {
        var node = _fs.CreateFile(@"\link")!;
        _adapter.SetReparsePoint(@"\link", SymlinkBuffer("a"), Open(node)).Should().Be(NtStatus.Success);
        _fs.CreateFile(@"\plain");

        byte[]? data = null;
        _adapter.GetReparsePointByName(@"\missing", false, ref data).Should().Be(NtStatus.ObjectNameNotFound);
        data = null;
        _adapter.GetReparsePointByName(@"\plain", false, ref data).Should().Be(NtStatus.NotAReparse);
        data = null;
        _adapter.GetReparsePointByName(@"\link", true, ref data).Should().Be(NtStatus.Success);
        data.Should().NotBeNull().And.HaveCount(64);
    }

    [Fact]
    public void GetFileSecurityByName_ReportsReparseAttribute_AfterSet()
    {
        var node = _fs.CreateFile(@"\link")!;
        _adapter.SetReparsePoint(@"\link", SymlinkBuffer("a"), Open(node)).Should().Be(NtStatus.Success);

        byte[]? sd = null;
        _adapter.GetFileSecurityByName(@"\link", out uint attrs, ref sd)
            .Should().Be(NtStatus.Success);
        ((FileAttributes)attrs).Should().HaveFlag(FileAttributes.ReparsePoint);
    }
}
