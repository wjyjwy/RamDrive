using RamDrive.Core.Memory;

namespace RamDrive.Core.FileSystem;

public enum FileNodeType
{
    File,
    Directory
}

/// <summary>
/// Represents a file or directory in the RAM file system.
/// </summary>
public sealed class FileNode : IDisposable
{
    public string Name { get; set; }
    public FileNodeType NodeType { get; }
    public FileAttributes Attributes { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime LastAccessTime { get; set; }

    /// <summary>File content. Null for directories.</summary>
    public PagedFileContent? Content { get; }

    /// <summary>Children (directories only). Case-insensitive on Windows.</summary>
    public Dictionary<string, FileNode>? Children { get; }

    /// <summary>Self-relative security descriptor (binary form). Null = no ACL.</summary>
    public byte[]? SecurityDescriptor { get; set; }

    /// <summary>
    /// Reparse tag (first DWORD of the reparse buffer), 0 when the node is not a reparse
    /// point. Surfaced to WinFsp as <c>FspFileInfo.ReparseTag</c> and used to answer
    /// name-resolution probes without parsing the buffer.
    /// </summary>
    public uint ReparseTag { get; set; }

    /// <summary>
    /// Raw REPARSE_DATA_BUFFER (opaque; symlink target / junction target / third-party
    /// payload). Null when the node is not a reparse point. A non-null value always
    /// implies <see cref="FileAttributes.ReparsePoint"/> is present in
    /// <see cref="Attributes"/>.
    /// </summary>
    public byte[]? ReparseData { get; set; }

    /// <summary>True for symbolic links, junctions, and any other reparse point.</summary>
    public bool IsReparsePoint => ReparseData != null;

    public FileNode? Parent { get; set; }

    /// <summary>
    /// Unique, stable per-node file id (NTFS-style file index / inode number). Surfaced to WinFsp
    /// as <c>FspFileInfo.IndexNumber</c>. MUST be unique across all live nodes: the CRT/STL
    /// <c>std::filesystem::copy_file</c> (and Win32 same-volume copy fast paths) compare
    /// <c>(VolumeSerialNumber, file id)</c> of source and destination to detect "copying a file onto
    /// itself". If every node reported id 0, a same-volume copy of two distinct files would be
    /// rejected with <c>std::errc::file_exists</c> — the dotTrace ETW-collector deploy failure.
    /// Allocated from a monotonic counter starting at 1 (0 is reserved / "unknown").
    /// </summary>
    public ulong IndexNumber { get; }

    private static long _nextIndexNumber;

    public long Size => Content?.Length ?? 0;

    /// <summary>Actual bytes backed by allocated pages. 0 for directories and sparse regions.</summary>
    public long AllocatedBytes => Content?.AllocatedBytes ?? 0;

    private FileNode(string name, FileNodeType nodeType, PagedFileContent? content)
    {
        Name = name;
        NodeType = nodeType;
        Content = content;
        IndexNumber = (ulong)System.Threading.Interlocked.Increment(ref _nextIndexNumber);
        var now = DateTime.UtcNow;
        CreationTime = now;
        LastWriteTime = now;
        LastAccessTime = now;

        if (nodeType == FileNodeType.Directory)
        {
            Attributes = FileAttributes.Directory;
            Children = new Dictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            Attributes = FileAttributes.Normal;
        }
    }

    public static FileNode CreateFile(string name, PagePool pool)
        => new(name, FileNodeType.File, new PagedFileContent(pool));

    public static FileNode CreateDirectory(string name)
        => new(name, FileNodeType.Directory, null);

    public bool IsDirectory => NodeType == FileNodeType.Directory;
    public bool IsFile => NodeType == FileNodeType.File;

    /// <summary>
    /// Releases this node's content and the whole subtree beneath it.
    /// Idempotent and O(1) on a repeat call: a node can legitimately be disposed twice
    /// (the reload fallback disposes a partially torn-down session, and this method
    /// recurses every child), and the recursion is O(subtree). <see cref="PagedFileContent.Dispose"/>
    /// already guards itself, but without the guard here the whole subtree walk is repeated.
    /// </summary>
    public void Dispose()
    {
        // Interlocked, not a plain flag test: concurrent Dispose calls (e.g. a delete
        // racing the reload teardown) could both pass "if (_disposed) return; _disposed =
        // true;" and then both walk the subtree — and, with the content already released
        // by the winner, the loser's recursion operates on a torn-down tree.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Content?.Dispose();
        // Release the (potentially large) reparse blob eagerly rather than waiting on GC,
        // mirroring the explicit free in memfs Dereference.
        ReparseData = null;
        if (Children != null)
        {
            foreach (var child in Children.Values)
                child.Dispose();
            Children.Clear();
        }
    }

    // int (not bool) so Dispose can claim it atomically with Interlocked.Exchange.
    private int _disposed;
}
