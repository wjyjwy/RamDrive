namespace RamDrive.Core.FileSystem;

/// <summary>
/// Immutable in-memory snapshot of the whole volume. Used by the reload feature:
/// the running filesystem is captured into this snapshot, the old session is torn
/// down and a fresh one built from new configuration, then the snapshot is restored
/// into the fresh filesystem. Everything stays in RAM — no disk I/O, no external
/// format, and all sparse regions are preserved because only the pages that were
/// actually allocated are carried over.
/// </summary>
public sealed class FileSystemSnapshot
{
    public required NodeSnapshot Root { get; init; }
}

/// <summary>
/// One node of a <see cref="FileSystemSnapshot"/>, mirroring <see cref="FileNode"/>
/// metadata plus (for files) a copy of every allocated page's data as
/// (byte-offset, page-bytes) pairs. Sparse pages are omitted; on restore they read
/// back as zeroes, matching the original sparse layout.
/// </summary>
public sealed class NodeSnapshot
{
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }

    public FileAttributes Attributes { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime LastAccessTime { get; set; }
    public byte[]? SecurityDescriptor { get; set; }

    /// <summary>Reparse tag (0 for plain nodes).</summary>
    public uint ReparseTag { get; set; }

    /// <summary>Raw reparse buffer for symbolic links / junctions (null for plain nodes).</summary>
    public byte[]? ReparseData { get; set; }

    /// <summary>Logical file length. Directories use 0.</summary>
    public long Length { get; set; }

    /// <summary>
    /// Pages the file had RESERVED in the pool (claimed by SetLength but never written).
    /// Carried across a reload because that claim is a real occupancy of the volume: a
    /// file SetLength'd to 20 pages and never written must still reserve 20 pages in the
    /// new pool, or a reload would silently free capacity that the live volume had
    /// committed (and the subsequent write would unexpectedly fail).
    /// </summary>
    public int ReservedPages { get; set; }

    /// <summary>Allocated page data (file nodes only).</summary>
    public List<(long Offset, byte[] Data)> Content { get; } = [];

    public List<NodeSnapshot> Children { get; } = [];
}