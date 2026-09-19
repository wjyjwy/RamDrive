using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;

namespace RamDrive.IntegrationTests;

/// <summary>
/// End-to-end pin of the path-status semantics through the REAL WinFsp mount.
///
/// Why this exists: <c>GetFileSecurityByName</c> used to collapse every "leaf missing"
/// case into STATUS_OBJECT_NAME_NOT_FOUND, so a probe of a path whose ANCESTOR was missing
/// returned ERROR_FILE_NOT_FOUND instead of ERROR_PATH_NOT_FOUND. Applications branch on
/// that distinction — the classic pattern is "probe, then create if not found" (leveldb,
/// SQLite, package managers) — so returning the wrong code can send a caller down the
/// "create it" path when the parent directory does not even exist.
///
/// The unit tests in WinFspRamAdapterSecurityTests assert the NTSTATUS at the adapter
/// boundary; these assert the Win32 error an application actually observes after WinFsp
/// has mapped it, which is the contract that matters.
///
/// Expected values were measured against a real NTFS volume (via both
/// GetFileAttributesEx and CreateFile) — see the "parent is a file" case below for the
/// one deliberate divergence.
/// </summary>
[Collection("RamDrive")]
[SupportedOSPlatform("windows")]
public partial class PathStatusSemanticsTests : IDisposable
{
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_DIRECTORY = 267;

    private readonly string _root;

    public PathStatusSemanticsTests(RamDriveFixture fx)
    {
        _root = Path.Combine(fx.Root, $"pathstatus_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void MissingLeaf_UnderExistingDirectory_IsFileNotFound()
    {
        string dir = Path.Combine(_root, "exists");
        Directory.CreateDirectory(dir);

        // NTFS: ERROR_FILE_NOT_FOUND. The caller may safely create this leaf.
        ProbeAttributes(Path.Combine(dir, "nope.txt")).Should().Be(ERROR_FILE_NOT_FOUND);
    }

    [Fact]
    public void MissingAncestor_IsPathNotFound()
    {
        // NTFS: ERROR_PATH_NOT_FOUND. Creating the leaf would fail anyway, so callers must
        // NOT be told FILE_NOT_FOUND here — that was the bug this test pins.
        ProbeAttributes(Path.Combine(_root, "no_such_dir", "nope.txt"))
            .Should().Be(ERROR_PATH_NOT_FOUND);
    }

    [Fact]
    public void MissingAncestor_ThroughCreateFile_IsPathNotFound()
    {
        // Same path, but through CreateFile — the API leveldb and friends actually use.
        OpenExisting(Path.Combine(_root, "no_such_dir", "nope.txt"))
            .Should().Be(ERROR_PATH_NOT_FOUND);
    }

    [Fact]
    public void ParentIsAFile_IsDirectoryError_KnownDivergenceFromNtfs()
    {
        string file = Path.Combine(_root, "plain.txt");
        File.WriteAllText(file, "x");

        // MEASURED DIVERGENCE: NTFS reports ERROR_PATH_NOT_FOUND here, while this volume
        // (and upstream tst/memfs, with which it must stay in lockstep) reports
        // ERROR_DIRECTORY. STATUS_NOT_A_DIRECTORY is a legitimate status that WinFsp's own
        // driver emits, and it is the more precise answer. Pinned so a future change is a
        // deliberate decision rather than an accident.
        ProbeAttributes(Path.Combine(file, "child.txt")).Should().Be(ERROR_DIRECTORY);
    }

    [Fact]
    public void ProbeThenCreate_Pattern_StillWorksForTheValidCase()
    {
        // The whole point of the distinction: for a genuinely creatable path the probe must
        // report FILE_NOT_FOUND so the caller proceeds to create, and the create must work.
        string dir = Path.Combine(_root, "probe_then_create");
        Directory.CreateDirectory(dir);
        string leaf = Path.Combine(dir, "newfile.dat");

        ProbeAttributes(leaf).Should().Be(ERROR_FILE_NOT_FOUND);

        File.WriteAllText(leaf, "created");
        File.ReadAllText(leaf).Should().Be("created");
    }

    /// <summary>Attribute-only probe (does not follow reparse points); returns the raw Win32 error.</summary>
    private static int ProbeAttributes(string path)
    {
        var data = new Win32FileAttributeData();
        return GetFileAttributesEx(path, 0, ref data) ? 0 : Marshal.GetLastWin32Error();
    }

    /// <summary>Open-existing probe; returns 0 on success or the raw Win32 error.</summary>
    private static int OpenExisting(string path)
    {
        using var h = CreateFileW(path, 0, 7, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
        return h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32FileAttributeData
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileAttributesEx(string name, int infoLevel, ref Win32FileAttributeData info);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
