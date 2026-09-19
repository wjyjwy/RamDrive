using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;

namespace RamDrive.IntegrationTests;

/// <summary>
/// End-to-end reparse-point coverage against the REAL mounted drive.
///
/// Symlinks and junctions are the one feature where a unit test against the adapter is
/// not enough: on WinFsp 2.x the link NAME RESOLUTION (turning \link\child into
/// \target\child) runs in the WinFsp user-mode DLL through the ResolveReparsePoints
/// interface slot, with STATUS_REPARSE bounced through the kernel. These tests prove the
/// full chain — set reparse data, traverse, resolve, delete — works from Win32's point
/// of view:
///   - file symbolic link (absolute + relative) read/write through the link
///   - directory symbolic link and directory junction (MOUNT_POINT tag) traversal
///   - deleting the link leaves the target intact
///
/// Requires symlink creation privilege (elevated, or Developer Mode) — Windows CI
/// runners execute elevated, mirroring how mklink is used in production.
/// </summary>
[Collection("RamDrive")]
[SupportedOSPlatform("windows")]
public class ReparsePointTests : IDisposable
{
    private readonly string _root;

    public ReparsePointTests(RamDriveFixture fx)
    {
        _root = Path.Combine(fx.Root, $"reparse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void FileSymlink_Absolute_TraversesToTarget()
    {
        string target = Path.Combine(_root, "abs-target.txt");
        string link = Path.Combine(_root, "abs-link.txt");
        File.WriteAllText(target, "hello-reparse");

        File.CreateSymbolicLink(link, target);

        File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue();
        File.ReadAllText(link).Should().Be("hello-reparse");

        // Writes through the link land on the target.
        File.WriteAllText(link, "overwritten");
        File.ReadAllText(target).Should().Be("overwritten");

        // Win32 link resolution API sees the target. Assert the RAW link target
        // (returnFinalTarget: false) rather than the fully-resolved path: the latter is
        // produced by GetFinalPathNameByHandle, which WinFsp answers with a
        // volume-relative name ("\UNC\<prefix>\...") that .NET then rebases onto the
        // process CWD. That is a property of WinFsp's path reporting, not of the reparse
        // implementation, so asserting on it would fail on any WinFsp volume.
        var resolved = File.ResolveLinkTarget(link, returnFinalTarget: false);
        resolved.Should().NotBeNull();
        Path.GetFullPath(resolved!.FullName)
            .Should().Be(Path.GetFullPath(target));

        // Deleting the link must not delete the target.
        File.Delete(link);
        File.Exists(link).Should().BeFalse();
        File.Exists(target).Should().BeTrue();
    }

    [Fact]
    public void FileSymlink_Relative_TraversesToTarget()
    {
        // Relative symlinks exercise FspFileSystemResolveReparsePoints' in-place path
        // rewrite (SYMLINK_FLAG_RELATIVE), a completely different branch than absolute.
        string target = Path.Combine(_root, "rel-target.bin");
        string link = Path.Combine(_root, "rel-link.bin");
        File.WriteAllBytes(target, new byte[] { 7, 8, 9 });

        File.CreateSymbolicLink(link, "rel-target.bin");

        File.ReadAllBytes(link).Should().Equal(7, 8, 9);
        File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue();
    }

    [Fact]
    public void DirectorySymlink_TraversesToTargetDirectory()
    {
        string target = Path.Combine(_root, "dir-target");
        Directory.CreateDirectory(target);
        string link = Path.Combine(_root, "dir-link");

        Directory.CreateSymbolicLink(link, target);

        File.GetAttributes(link).Should()
            .HaveFlag(FileAttributes.ReparsePoint).And.HaveFlag(FileAttributes.Directory);

        // Create/list through the symlinked path.
        string viaLink = Path.Combine(link, "inside.txt");
        File.WriteAllText(viaLink, "via-dir-link");
        File.ReadAllText(Path.Combine(target, "inside.txt")).Should().Be("via-dir-link");

        Directory.GetFiles(link).Should().ContainSingle(f => Path.GetFileName(f) == "inside.txt");

        Directory.Delete(link);
        Directory.Exists(link).Should().BeFalse();
        Directory.Exists(target).Should().BeTrue();
        File.Exists(Path.Combine(target, "inside.txt")).Should().BeTrue();
    }

    /// <summary>
    /// The supported "directory alias" pattern on this volume: a directory symlink whose
    /// target is another directory ON THE RAM DRIVE, used the way a repository symlink is —
    /// point a path at content that lives somewhere else, then read/write/list through it.
    ///
    /// This is the case that matters in practice, because a junction CANNOT do it here
    /// (Windows will not follow mount-point indirection onto a non-local volume, and the RAM
    /// drive reports DRIVE_REMOTE). See
    /// <see cref="Junction_CreatesReportsAndDeletes_WithoutTouchingTarget"/> for the matrix.
    /// </summary>
    [Fact]
    public void DirectorySymlink_AliasesAnotherDirectoryOnTheRamDrive()
    {
        // "Real" content location, and the alias a tool would be pointed at.
        string realDir = Path.Combine(_root, "repo-real");
        Directory.CreateDirectory(realDir);
        File.WriteAllText(Path.Combine(realDir, "payload.txt"), "real-content");

        string alias = Path.Combine(_root, "repo-link");
        Directory.CreateSymbolicLink(alias, realDir);

        // Reads through the alias reach the real content.
        File.ReadAllText(Path.Combine(alias, "payload.txt")).Should().Be("real-content");

        // Enumeration through the alias shows the real directory's entries.
        Directory.GetFiles(alias).Select(Path.GetFileName)
            .Should().Contain("payload.txt");

        // Writes through the alias land in the real directory.
        File.WriteAllText(Path.Combine(alias, "created-via-alias.txt"), "written");
        File.ReadAllText(Path.Combine(realDir, "created-via-alias.txt")).Should().Be("written");

        // New subdirectories created through the alias are real subdirectories.
        string subViaAlias = Path.Combine(alias, "sub");
        Directory.CreateDirectory(subViaAlias);
        Directory.Exists(Path.Combine(realDir, "sub")).Should().BeTrue();

        // The alias itself is a reparse point, not a copy of the target.
        File.GetAttributes(alias).Should()
            .HaveFlag(FileAttributes.ReparsePoint).And.HaveFlag(FileAttributes.Directory);

        // Deleting the alias must leave the real tree completely intact.
        Directory.Delete(alias);
        Directory.Exists(alias).Should().BeFalse();
        Directory.Exists(realDir).Should().BeTrue();
        File.ReadAllText(Path.Combine(realDir, "payload.txt")).Should().Be("real-content");
        File.Exists(Path.Combine(realDir, "created-via-alias.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(realDir, "sub")).Should().BeTrue();
    }

    /// <summary>
    /// A junction (IO_REPARSE_TAG_MOUNT_POINT) can be created via raw FSCTL, is reported as
    /// a reparse directory, round-trips its blob byte-for-byte through FSCTL_GET_REPARSE_POINT,
    /// and can be removed without touching the target tree.
    ///
    /// <para>
    /// NOT covered here: TRAVERSING the junction to reach the target. A direction matrix
    /// (link-location × target-location, over both the RAM drive and real NTFS) shows that
    /// junction traversal only works when the TARGET is on a local fixed volume:
    ///   link=RAM  target=RAM  -> fail (DATA_INVALID)
    ///   link=RAM  target=NTFS -> ok
    ///   link=NTFS target=RAM  -> fail (DATA_INVALID)
    ///   link=NTFS target=NTFS -> ok
    /// WinFsp mounts a UNC path as FILE_DEVICE_NETWORK_FILE_SYSTEM / FILE_REMOTE_DEVICE, so
    /// the RAM drive reports DRIVE_REMOTE (non-local). Windows does not follow mount-point
    /// reparse indirection onto a non-local volume, so the limitation is about the TARGET
    /// being on the RAM drive — not about the link's location, and not about "same volume".
    /// Directory symlinks are unaffected: the DLL resolves them into an absolute path
    /// itself, so they traverse to a RAM-drive target fine — see
    /// <see cref="DirectorySymlink_TraversesToTargetDirectory"/>.
    /// </summary>
    [Fact]
    public void Junction_CreatesReportsAndDeletes_WithoutTouchingTarget()
    {
        string target = Path.Combine(_root, "junc-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        string link = Path.Combine(_root, "junc-link");
        Directory.CreateDirectory(link); // junctions reparse an empty placeholder directory

        JunctionHelper.Create(link, target);

        File.GetAttributes(link).Should()
            .HaveFlag(FileAttributes.ReparsePoint).And.HaveFlag(FileAttributes.Directory);

        // The stored blob must survive a raw read-back: same tag, same substitute name.
        byte[] blob = JunctionHelper.GetReparseData(link);
        blob.Length.Should().BeGreaterThan(16);
        BitConverter.ToUInt32(blob, 0).Should().Be(0xA0000003);
        int dataLen = BitConverter.ToUInt16(blob, 4);
        int subOff = BitConverter.ToUInt16(blob, 8);
        int subLen = BitConverter.ToUInt16(blob, 10);
        (dataLen + 8).Should().Be(blob.Length);
        Encoding.Unicode.GetString(blob, 16 + subOff, subLen)
            .Should().Be(@"\??\" + Path.GetFullPath(target).TrimEnd('\\'));

        // Removing the junction (an empty placeholder on our volume) keeps the target tree.
        Directory.Delete(link);
        Directory.Exists(link).Should().BeFalse();
        Directory.Exists(target).Should().BeTrue();
        File.ReadAllText(Path.Combine(target, "keep.txt")).Should().Be("keep");
    }

    [Fact]
    public void LinkToMissingTarget_OpenReportsNotFound_ThenBecomesTraversable()
    {
        // The link exists while its target does not: traversal must surface NAME NOT FOUND,
        // and once the target appears the same link works without remount (no stale state).
        string target = Path.Combine(_root, "late-target.txt");
        string link = Path.Combine(_root, "late-link.txt");
        File.CreateSymbolicLink(link, target);

        // NTFS parity: a dangling link still EXISTS as a directory entry, so
        // File.Exists/GetAttributes report the LINK (attribute-only probes do not follow
        // reparse points). Only an operation that actually traverses it fails. Verified
        // against a real NTFS volume — asserting File.Exists == false here would be wrong
        // on NTFS too.
        File.Exists(link).Should().BeTrue();
        File.GetAttributes(link).Should().HaveFlag(FileAttributes.ReparsePoint);

        ((Action)(() => File.OpenRead(link))).Should().Throw<FileNotFoundException>();

        File.WriteAllText(target, "now-present");
        File.ReadAllText(link).Should().Be("now-present");
    }
}

/// <summary>
/// Minimal FSCTL_SET_REPARSE_POINT writer for IO_REPARSE_TAG_MOUNT_POINT junctions
/// (there is no managed API for junctions, unlike symlinks). Layout per winnt.h
/// REPARSE_DATA_BUFFER + MOUNT_POINT_REPARSE_BUFFER.
/// </summary>
internal static partial class JunctionHelper
{
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    public static void Create(string junctionPath, string targetPath)
    {
        using var handle = CreateFileW(
            junctionPath,
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile({junctionPath})");

        // Junctions store the NT-namespace name in the substitute field. The prefix is
        // the literal `\??\` (backslash, question, question, backslash) — NOT `\\??\`.
        string target = Path.GetFullPath(targetPath).TrimEnd('\\');
        string substitute = @"\??\" + target;
        string print = target;

        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] printBytes = Encoding.Unicode.GetBytes(print);

        // Layout REQUIRED by the kernel's FsRtlValidateReparsePointBuffer (verified against
        // what `mklink /J` itself writes, and by variant testing on a real NTFS volume):
        //   - the two names are separated by a UTF-16 NUL that is NOT counted in either length
        //   - PrintNameOffset == SubstituteNameLength + 2
        //   - the data area ends with a second NUL:
        //     ReparseDataLength == 8 + subLen + 2 + printLen + 2
        // Getting any of these wrong makes FSCTL_SET_REPARSE_POINT fail with
        // ERROR_IO_REPARSE_DATA_INVALID (4392) on EVERY file system, including NTFS.
        int printNameOffset = substituteBytes.Length + sizeof(char);
        int dataLength = 8 + substituteBytes.Length + sizeof(char)
            + printBytes.Length + sizeof(char);
        var buffer = new byte[8 + dataLength];
        BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
        // Reserved @6 = 0
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                        // SubstituteNameOffset
        BitConverter.GetBytes((ushort)substituteBytes.Length).CopyTo(buffer, 10);   // SubstituteNameLength
        BitConverter.GetBytes((ushort)printNameOffset).CopyTo(buffer, 12);          // PrintNameOffset
        BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buffer, 14);        // PrintNameLength
        substituteBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 16 + printNameOffset);

        if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, (uint)buffer.Length,
                IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_SET_REPARSE_POINT (junction)");
        }
    }

    /// <summary>FSCTL_GET_REPARSE_POINT — return the raw reparse blob stored on <paramref name="reparsePath"/>.</summary>
    public static byte[] GetReparseData(string reparsePath)
    {
        using var handle = CreateFileW(
            reparsePath,
            0, // no access needed for GET on a reparse dir when opened with OPEN_REPARSE_POINT
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile({reparsePath})");

        var buffer = new byte[4096];
        if (!DeviceIoControlOut(handle, FSCTL_GET_REPARSE_POINT, null, 0,
                buffer, (uint)buffer.Length, out uint returned, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_GET_REPARSE_POINT (junction)");
        }
        return buffer.AsSpan(0, (int)returned).ToArray();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControlOut(
        SafeFileHandle hDevice, uint dwIoControlCode,
        byte[]? lpInBuffer, uint nInBufferSize,
        [Out] byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
