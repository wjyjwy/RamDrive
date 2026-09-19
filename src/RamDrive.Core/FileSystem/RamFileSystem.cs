using RamDrive.Core.Memory;
using WinFsp.Native;

namespace RamDrive.Core.FileSystem;

/// <summary>
/// In-memory file system with path resolution, CRUD, and capacity tracking.
/// Thread-safe: uses a global lock for structural operations (create/delete/move)
/// and per-file ReaderWriterLockSlim for content I/O.
/// </summary>
public sealed class RamFileSystem : IDisposable
{
    private readonly PagePool _pool;
    private readonly FileNode _root;
    private readonly object _structureLock = new();

    public RamFileSystem(PagePool pool)
    {
        _pool = pool;
        _root = FileNode.CreateDirectory(string.Empty);
    }

    /// <summary>
    /// Set the security descriptor on the root directory.
    /// Must be called before mounting if ACL support is desired.
    /// </summary>
    public void SetRootSecurityDescriptor(byte[] securityDescriptor)
    {
        _root.SecurityDescriptor = securityDescriptor;
    }

    public long TotalBytes => _pool.CapacityBytes;
    public long UsedBytes => _pool.UsedBytes;
    public long FreeBytes => _pool.FreeBytes;

    /// <summary>Root directory security descriptor (null until SetRootSecurityDescriptor).</summary>
    internal byte[]? RootSecurityDescriptor => _root.SecurityDescriptor;

    /// <summary>
    /// Resolve a path to a FileNode. Returns null if not found.
    /// Path uses backslash separators (Windows convention); "\" is root.
    /// </summary>
    public FileNode? FindNode(string path)
    {
        lock (_structureLock)
        {
            return FindNodeInternal(path);
        }
    }

    /// <summary>
    /// Create a file at the given path. Parent directory must exist.
    /// Returns the new FileNode, or null if parent not found or name already exists.
    /// </summary>
    public FileNode? CreateFile(string path, byte[]? securityDescriptor = null)
    {
        lock (_structureLock)
        {
            var (parent, name) = ResolvePath(path);
            if (parent == null || !parent.IsDirectory || name == null) return null;
            if (!WindowsNameRules.IsValid(name)) return null;
            if (parent.Children!.ContainsKey(name)) return null;

            var node = FileNode.CreateFile(name, _pool);
            // When the caller does not supply an SD, inherit the parent's by reference.
            // Copy-on-write contract: SetFileSecurity allocates a fresh byte[] before
            // assigning, so this share is broken on the first explicit ACL change.
            node.SecurityDescriptor = securityDescriptor ?? parent.SecurityDescriptor;
            node.Parent = parent;
            parent.Children[name] = node;
            parent.LastWriteTime = DateTime.UtcNow;
            return node;
        }
    }

    /// <summary>
    /// Create a directory at the given path. Parent must exist.
    /// Returns the new FileNode, or null if parent not found or name already exists.
    /// </summary>
    public FileNode? CreateDirectory(string path, byte[]? securityDescriptor = null)
    {
        lock (_structureLock)
        {
            var (parent, name) = ResolvePath(path);
            if (parent == null || !parent.IsDirectory || name == null) return null;
            if (!WindowsNameRules.IsValid(name)) return null;
            if (parent.Children!.ContainsKey(name)) return null;

            var node = FileNode.CreateDirectory(name);
            // See CreateFile for the inherit-by-reference / copy-on-write contract.
            node.SecurityDescriptor = securityDescriptor ?? parent.SecurityDescriptor;
            node.Parent = parent;
            parent.Children[name] = node;
            parent.LastWriteTime = DateTime.UtcNow;
            return node;
        }
    }

    /// <summary>
    /// Delete a file or empty directory. Returns true if deleted.
    /// </summary>
    public bool Delete(string path)
    {
        lock (_structureLock)
        {
            var node = FindNodeInternal(path);
            if (node == null || node == _root) return false;

            if (node.IsDirectory && node.Children!.Count > 0) return false;

            var parent = node.Parent;
            if (parent?.Children?.Remove(node.Name) == true)
            {
                parent.LastWriteTime = DateTime.UtcNow;
                node.Dispose();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Move/rename a file or directory.
    ///
    /// Returns an NTSTATUS so the caller can distinguish the two failure modes NTFS
    /// reports differently (measured against a real NTFS volume, and matched by
    /// <c>MemfsReferenceFs</c>):
    /// <list type="bullet">
    /// <item>destination exists and <paramref name="replace"/> is false →
    /// <c>STATUS_OBJECT_NAME_COLLISION</c></item>
    /// <item>destination is an existing DIRECTORY and the source is anything else
    /// (a file, or another directory) → <c>STATUS_ACCESS_DENIED</c>. NTFS refuses to
    /// replace a directory in either direction, even when it is empty.</item>
    /// </list>
    /// Note that a DIRECTORY source replacing an existing FILE is legal on NTFS and
    /// succeeds — the file is removed and the directory takes its name.
    /// </summary>
    public int Move(string oldPath, string newPath, bool replace)
    {
        lock (_structureLock)
        {
            var sourceNode = FindNodeInternal(oldPath);
            if (sourceNode == null || sourceNode == _root) return NtStatus.ObjectNameNotFound;

            var (newParent, newName) = ResolvePath(newPath);
            if (newParent == null || !newParent.IsDirectory || newName == null)
                return NtStatus.ObjectPathNotFound;
            if (!WindowsNameRules.IsValid(newName)) return NtStatus.ObjectNameInvalid;

            // P0-3: reject moving a node into itself or one of its own descendants.
            // That would create a Parent/Children cycle — the subtree becomes unreachable
            // and FileNode.Dispose recurses forever at unmount. The Win32 layer blocks the
            // obvious case, but a case-only variant ("\a" → "\A\inner\newa") falls through
            // to us, so the check MUST live inside the filesystem (see 0.4.7 live-test).
            for (var p = newParent; p != null; p = p.Parent)
            {
                if (ReferenceEquals(p, sourceNode))
                    return NtStatus.ObjectNameInvalid;
            }

            if (newParent.Children!.TryGetValue(newName, out var existing))
            {
                if (ReferenceEquals(existing, sourceNode))
                {
                    // Self-target (same path or a case-only rename like "\a" → "\A"):
                    // NTFS treats this as a successful no-op, with or without the
                    // replace flag. Crucially we must NOT dispose the source node —
                    // that was the old use-after-dispose: 'existing.Dispose()' released
                    // a node that was then re-attached to the tree, so any later I/O
                    // on it threw ObjectDisposedException.
                }
                else if (!replace)
                    return NtStatus.ObjectNameCollision;
                else if (existing.IsDirectory)
                {
                    // A directory may never be replaced, by a file OR by another
                    // directory, even when empty. Verified on real NTFS:
                    //   file -> dir (replace) => ACCESS_DENIED
                    //   dir  -> dir (replace) => ACCESS_DENIED
                    // MemfsReferenceFs agrees, so returning OBJECT_NAME_COLLISION here
                    // would be both wrong for NTFS parity and a differential divergence.
                    return NtStatus.AccessDenied;
                }
                else
                {
                    existing.Dispose();
                }
            }

            var oldParent = sourceNode.Parent;
            oldParent?.Children?.Remove(sourceNode.Name);
            if (oldParent != null) oldParent.LastWriteTime = DateTime.UtcNow;

            sourceNode.Name = newName;
            sourceNode.Parent = newParent;
            newParent.Children[newName] = sourceNode;
            newParent.LastWriteTime = DateTime.UtcNow;

            return NtStatus.Success;
        }
    }

    /// <summary>
    /// List immediate children of a directory.
    /// </summary>
    public IReadOnlyList<FileNode>? ListDirectory(string path)
    {
        List<FileNode> snapshot;
        lock (_structureLock)
        {
            var node = FindNodeInternal(path);
            if (node == null || !node.IsDirectory) return null;
            // Copy the child references under the lock, then sort OUTSIDE it. WinFsp enumerates a
            // directory page by page, so a large directory used to be fully re-sorted
            // (O(n log n)) on every page while holding the global structure lock — stalling every
            // concurrent Create / Delete / Move / FindNode. The result is still a consistent
            // point-in-time snapshot, so ordering semantics are unchanged.
            snapshot = new List<FileNode>(node.Children!.Count);
            snapshot.AddRange(node.Children.Values);
        }

        // Must be sorted by name (case-insensitive) for WinFsp marker-based directory
        // enumeration pagination to work correctly.
        snapshot.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return snapshot;
    }

    // ─── Snapshot / restore (reload support) ───
    //
    // The whole volume lives in RAM, so a reload can capture the full state into a
    // FileSystemSnapshot, tear the old session down, build a fresh one from new
    // configuration, then restore. No disk I/O and no external format involved —
    // sparse regions stay sparse because only allocated pages are carried over.

    /// <summary>
    /// Capture the whole volume into an in-memory snapshot. Safe to call at any time;
    /// the structure lock plus each file's reader lock make it internally consistent.
    /// </summary>
    public FileSystemSnapshot CreateSnapshot()
    {
        lock (_structureLock)
        {
            return new FileSystemSnapshot { Root = CaptureNode(_root) };
        }
    }

    /// <summary>
    /// Restore a snapshot into this filesystem. The filesystem must be empty (only the
    /// root node) — callers build a fresh filesystem for the target. Returns an error
    /// message on failure (capacity too small for the snapshot, etc.) or null on success.
    /// A failed restore leaves the target filesystem in an undefined state; the caller
    /// must dispose it (which never affects the snapshot or the source).
    /// </summary>
    public string? RestoreSnapshot(FileSystemSnapshot snapshot)
    {
        lock (_structureLock)
        {
            if (_root.Children!.Count > 0)
                return "target filesystem is not empty";

            // Capacity pre-check. Count the pages the snapshot will ACTUALLY occupy, i.e.
            // the pages that carry data (NodeSnapshot.Content), not ceil(Length/pageSize):
            // a sparse file can be far longer than the pool is large while occupying a
            // single page, and the live volume it was captured from obviously fit. Sizing
            // from the logical length rejected such snapshots outright ("needs 1025 pages
            // but pool capacity is 32 pages") even though restoring them costs one page.
            // Validating up front still means we bail before mutating anything, so a
            // reload that genuinely would not fit never discards the old session.
            long requiredPages = 0;
            CountRequiredPages(snapshot.Root, ref requiredPages);
            if (requiredPages > _pool.MaxPages)
                return $"snapshot needs {requiredPages} pages but pool capacity is {_pool.MaxPages} pages";

            if (snapshot.Root.SecurityDescriptor != null)
                _root.SecurityDescriptor = snapshot.Root.SecurityDescriptor;
            _root.CreationTime = snapshot.Root.CreationTime;
            _root.LastWriteTime = snapshot.Root.LastWriteTime;
            _root.LastAccessTime = snapshot.Root.LastAccessTime;

            foreach (var child in snapshot.Root.Children)
            {
                var err = RestoreNode(child, _root);
                if (err != null) return err;
            }
            return null;
        }
    }

    private static NodeSnapshot CaptureNode(FileNode node)
    {
        var snap = new NodeSnapshot
        {
            Name = node.Name,
            IsDirectory = node.IsDirectory,
            Attributes = node.Attributes,
            CreationTime = node.CreationTime,
            LastWriteTime = node.LastWriteTime,
            LastAccessTime = node.LastAccessTime,
            SecurityDescriptor = node.SecurityDescriptor,
            // Copy the reparse blob: a snapshot must stay consistent if the live node's
            // link is deleted or re-pointed while the reload is in flight.
            ReparseTag = node.ReparseTag,
            ReparseData = node.ReparseData == null ? null : (byte[])node.ReparseData.Clone(),
            Length = node.Size,
            ReservedPages = node.Content?.ReservedPages ?? 0,
        };

        if (node.IsDirectory)
        {
            foreach (var child in node.Children!.Values)
                snap.Children.Add(CaptureNode(child));
        }
        else
        {
            node.Content!.EnumerateAllocatedData(snap.Content);
        }
        return snap;
    }

    private string? RestoreNode(NodeSnapshot snap, FileNode parent)
    {
        var node = snap.IsDirectory
            ? FileNode.CreateDirectory(snap.Name)
            : FileNode.CreateFile(snap.Name, _pool);

        node.SecurityDescriptor = snap.SecurityDescriptor ?? parent.SecurityDescriptor;
        node.Attributes = snap.Attributes;
        node.CreationTime = snap.CreationTime;
        node.LastWriteTime = snap.LastWriteTime;
        node.LastAccessTime = snap.LastAccessTime;
        // Restore link state before children/content replay so a restored junction/symlink
        // is traversable immediately after reload.
        node.ReparseTag = snap.ReparseTag;
        node.ReparseData = snap.ReparseData == null ? null : (byte[])snap.ReparseData.Clone();
        node.Parent = parent;
        parent.Children![snap.Name] = node;

        if (snap.IsDirectory)
        {
            foreach (var child in snap.Children)
            {
                var err = RestoreNode(child, node);
                if (err != null) return err;
            }
        }
        else
        {
            var content = node.Content!;

            // Replay the data pages FIRST. Write reserves only the pages it actually
            // touches, so a sparse file costs exactly the pages it owns.
            foreach (var (offset, data) in snap.Content)
            {
                // Snapshot entries carry a whole page, but only the bytes inside the logical
                // length may be replayed: PagedFileContent.Write() grows the length to the end
                // of the written span, so writing the last page in full would pad every file
                // whose length is not a page multiple with zeroes up to the page boundary —
                // the "trailing null characters" seen in IDEs after a capacity reload. Bytes
                // past EOF are read as zeroes anyway (unallocated pages and page tails are
                // zero-filled), so clipping loses nothing.
                long remaining = snap.Length - offset;
                if (remaining <= 0) continue;

                var chunk = data.AsSpan(0, (int)Math.Min(data.Length, remaining));
                if (content.Write(offset, chunk) != chunk.Length)
                    return $"file '{snap.Name}': restore write at offset {offset} failed (disk full)";
            }

            // THEN set the logical length without reserving the holes. Using SetLength here
            // would reserve ceil(Length/pageSize) pages regardless of how sparse the file
            // is, which is both wrong (the pages may not exist in the new pool) and wasteful.
            if (!content.RestoreSetLength(snap.Length, snap.ReservedPages))
                return $"file '{snap.Name}': could not restore length {snap.Length} " +
                       $"with {snap.ReservedPages} reserved page(s)";
        }
        return null;
    }

    /// <summary>
    /// Create the configured initial directory tree. Returns the number of directories
    /// actually created (pre-existing ones are skipped and not counted).
    ///
    /// <para>Lives here rather than in the service so the configuration → directory
    /// pipeline is testable without a mount or a host. The service calls this on mount and
    /// after every reload.</para>
    /// </summary>
    public int CreateInitialDirectories(Configuration.DirectoryNode entries)
    {
        var errors = entries.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(
                "Invalid initial-directory configuration:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors), nameof(entries));

        return CreateDirectoriesRecursive(@"\", entries);
    }

    private int CreateDirectoriesRecursive(string parentPath, Configuration.DirectoryNode entries)
    {
        int count = 0;
        foreach (var (name, children) in entries)
        {
            var path = parentPath == @"\" ? @"\" + name : parentPath + @"\" + name;
            if (CreateDirectory(path) != null)
                count++;

            if (children.Count > 0)
                count += CreateDirectoriesRecursive(path, children);
        }

        return count;
    }

    /// <summary>
    /// Sum the pages the snapshot will actually occupy in the new pool: one per captured
    /// data page, plus any reservation the live file still held.
    ///
    /// <para>Deliberately NOT derived from the logical length. Sparse holes carry no page
    /// in <see cref="NodeSnapshot.Content"/> and cost nothing to restore, but a file that
    /// was <c>SetLength</c>-extended holds a real capacity claim that must be counted —
    /// that is what makes a 20-page reservation fail to restore into a 16-page pool.</para>
    /// </summary>
    private static void CountRequiredPages(NodeSnapshot node, ref long pages)
    {
        if (!node.IsDirectory)
            pages += node.Content.Count + node.ReservedPages;
        foreach (var child in node.Children)
            CountRequiredPages(child, ref pages);
    }

    public void Dispose()
    {
        lock (_structureLock)
        {
            _root.Dispose();
        }
    }

    // --- Internals ---

    private FileNode? FindNodeInternal(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "\\") return _root;

        string[] parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        FileNode current = _root;

        foreach (string part in parts)
        {
            if (!current.IsDirectory) return null;
            if (!current.Children!.TryGetValue(part, out var child)) return null;
            current = child;
        }

        return current;
    }

    /// <summary>
    /// Split path into parent node + child name.
    /// </summary>
    private (FileNode? parent, string? name) ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "\\") return (null, null);

        string[] parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (null, null);

        FileNode current = _root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!current.IsDirectory) return (null, null);
            if (!current.Children!.TryGetValue(parts[i], out var child)) return (null, null);
            current = child;
        }

        return (current, parts[^1]);
    }
}
