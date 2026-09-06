using RamDrive.Core.Memory;

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

    /// <summary>
    /// Resolve a path to a FileNode. Returns null if not found.
    /// Path uses backslash separator (Dokan convention). "\" is root.
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
    /// </summary>
    public bool Move(string oldPath, string newPath, bool replace)
    {
        lock (_structureLock)
        {
            var sourceNode = FindNodeInternal(oldPath);
            if (sourceNode == null || sourceNode == _root) return false;

            var (newParent, newName) = ResolvePath(newPath);
            if (newParent == null || !newParent.IsDirectory || newName == null) return false;
            if (!WindowsNameRules.IsValid(newName)) return false;

            // P0-3: reject moving a node into itself or one of its own descendants.
            // That would create a Parent/Children cycle — the subtree becomes unreachable
            // and FileNode.Dispose recurses forever at unmount. The Win32 layer blocks the
            // obvious case, but a case-only variant ("\a" → "\A\inner\newa") falls through
            // to us, so the check MUST live inside the filesystem (see 0.4.7 live-test).
            for (var p = newParent; p != null; p = p.Parent)
            {
                if (ReferenceEquals(p, sourceNode))
                    return false;
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
                    return false;
                else if (existing.IsDirectory)
                {
                    // P1-1: a file may never replace a directory, even an empty one
                    // (NTFS returns ACCESS_DENIED and MemfsReferenceFs does the same —
                    // differential parity requires identical behavior here).
                    return false;
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

            return true;
        }
    }

    /// <summary>
    /// List immediate children of a directory.
    /// </summary>
    public IReadOnlyList<FileNode>? ListDirectory(string path)
    {
        lock (_structureLock)
        {
            var node = FindNodeInternal(path);
            if (node == null || !node.IsDirectory) return null;
            // Must be sorted by name (case-insensitive) for WinFsp marker-based
            // directory enumeration pagination to work correctly.
            return node.Children!.Values
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
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

            // Capacity pre-check: each file can eventually occupy ceil(Length/pageSize)
            // pages. Validating up front means we bail before mutating anything, so a
            // reload that would exceed the new capacity never discards the old session.
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
            Length = node.Size,
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
            if (!content.SetLength(snap.Length))
                return $"file '{snap.Name}' length {snap.Length} exceeds remaining capacity";

            foreach (var (offset, data) in snap.Content)
            {
                if (content.Write(offset, data.AsSpan()) != data.Length)
                    return $"file '{snap.Name}': restore write at offset {offset} failed (disk full)";
            }
        }
        return null;
    }

    private void CountRequiredPages(NodeSnapshot node, ref long pages)
    {
        if (!node.IsDirectory)
            pages += (node.Length + _pool.PageSize - 1) / _pool.PageSize;
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
