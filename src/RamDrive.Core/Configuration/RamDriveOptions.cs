namespace RamDrive.Core.Configuration;

public sealed class RamDriveOptions
{
    public string MountPoint { get; set; } = @"R:\";
    public long CapacityMb { get; set; } = 512;
    public int PageSizeKb { get; set; } = 64;
    public bool PreAllocate { get; set; }
    public string VolumeLabel { get; set; } = "RamDrive";

    /// <summary>
    /// Enable Windows kernel-level file data caching. When true, the WinFsp host's
    /// <c>FileInfoTimeout</c> is set to <see cref="FileInfoTimeoutMs"/>. When false the
    /// timeout is forced to 0 (no kernel cache, ~3× lower throughput) regardless of
    /// <see cref="FileInfoTimeoutMs"/> — this is the documented backout switch.
    /// </summary>
    public bool EnableKernelCache { get; set; } = true;

    /// <summary>
    /// Lifetime of the WinFsp kernel <c>FileInfo</c> cache, in milliseconds. Default 1000.
    ///
    /// <para>Cache coherence relies on every IFileSystem callback returning the correct
    /// post-operation <c>FspFileInfo</c> in its response (the kernel updates Cc with that
    /// FileInfo), plus the explicit invalidation performed when
    /// <see cref="EnableNotifications"/> is on — which is the default. This timeout bounds
    /// how long a stale entry can survive if both of those are missed: defence in depth,
    /// not the primary mechanism.</para>
    ///
    /// <para>Special values:
    /// <list type="bullet">
    /// <item><c>0</c> — cache disabled (same effect as <see cref="EnableKernelCache"/>=false).</item>
    /// <item><c>uint.MaxValue</c> (4294967295) — cache effectively permanent. Correctness
    /// then depends entirely on the notification matrix, so <see cref="EnableNotifications"/>
    /// MUST be on; <see cref="Validate"/> refuses the combination otherwise. The integration
    /// test fixtures pin this value (with notifications on) so that a missing notification
    /// shows up as a test failure.</item>
    /// </list></para>
    /// </summary>
    public uint FileInfoTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Send <c>FspFileSystemNotify</c> after every path-mutating callback (Create, Write,
    /// SetFileSize, SetFileAttributes, Rename, Delete) to proactively invalidate the WinFsp
    /// kernel <c>FileInfo</c> cache.
    ///
    /// <para>Default <c>true</c>. The "every callback returns the correct post-operation
    /// FileInfo" argument covers only callbacks that HAVE a FileInfo to return — it does not
    /// cover <c>MoveFile</c> (returns an NTSTATUS), <c>Cleanup</c> (void) or
    /// <c>CanDelete</c> (an NTSTATUS). For those, the notification is the only signal the
    /// kernel gets that a cached entry for a path is now wrong. Rename-replace is exactly
    /// the leveldb/Chromium pattern that produced the "CURRENT file does not end with
    /// newline" profile corruption (see docs/leveldb-cache-coherency-postmortem.md), and a
    /// read microseconds after the rename cannot be saved by the
    /// <see cref="FileInfoTimeoutMs"/> fallback. Cost of leaving this on is one kernel IOCTL
    /// per mutation, dispatched off the WinFsp dispatcher thread.</para>
    ///
    /// <para>Set to <c>false</c> only if a metadata-heavy workload measurably regresses; doing
    /// so makes <see cref="FileInfoTimeoutMs"/> the sole coherence mechanism, which is why
    /// Validate() then refuses <c>FileInfoTimeoutMs=uint.MaxValue</c>.</para>
    /// </summary>
    public bool EnableNotifications { get; set; } = true;

    /// <summary>
    /// Tree of directories to create at the root of the RAM disk after mounting.
    /// JSON keys are directory names; nested objects define subdirectories.
    /// Example: <c>{ "Temp": {}, "Cache": { "App1": {} } }</c>
    /// </summary>
    public DirectoryNode? InitialDirectories { get; set; }

    /// <summary>
    /// Validate this configuration. Returns a list of human-readable errors (empty = valid).
    /// Called from the <c>PagePool</c> constructor; a failing volume refuses to start with
    /// a descriptive message instead of crashing later with a divide-by-zero or negative
    /// capacity (e.g. <c>PageSizeKb=0</c> or <c>CapacityMb=-1</c>).
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(MountPoint))
            errors.Add("MountPoint must not be empty");
        else if (!IsValidMountPoint(MountPoint))
            errors.Add(
                $"MountPoint must be a drive letter with an optional single trailing backslash, " +
                $"for example \"R:\" or \"R:\" (got \"{MountPoint}\"). The \"\\\\.\\R:\" Mount Manager " +
                "form and UNC paths are NOT valid here — the host prepends \"\\\\.\\\" itself. A " +
                "malformed value reaches FspFileSystemSetMountPointEx, which has no error path " +
                "and access-violates the process.");

        if (CapacityMb <= 0)
            errors.Add($"CapacityMb must be positive (got {CapacityMb})");
        else if (CapacityMb > 1_048_576) // 1 TB sanity cap
            errors.Add($"CapacityMb is unreasonably large ({CapacityMb}); refusing to start");

        if (PageSizeKb <= 0)
            errors.Add($"PageSizeKb must be positive (got {PageSizeKb})");
        else if (PageSizeKb > 1_048_576)
            errors.Add($"PageSizeKb is unreasonably large ({PageSizeKb})");
        else if (PageSizeKb > CapacityMb * 1024)
            errors.Add($"PageSizeKb ({PageSizeKb}KB) exceeds capacity ({CapacityMb}MB) — pool would have fewer than 1 page");

        if (VolumeLabel.Length > 32)
            errors.Add($"VolumeLabel must be at most 32 characters (got {VolumeLabel.Length})");

        // NOTE: the "permanent cache + notifications off" combination is deliberately NOT a
        // hard error. It is hazardous (cached metadata for a path never expires and nothing
        // invalidates it — the leveldb/Chromium failure mode), but it is also a legitimate
        // configuration: the differential test leg uses exactly FileInfoTimeoutMs=uint.MaxValue
        // with notifications off to keep its comparison a pure semantic one. The host logs a
        // warning for it instead; see WinFspHostedService.

        return errors;
    }

    /// <summary>
    /// Accepts the only mount-point forms the rest of the code supports: a bare drive letter
    /// with an optional single trailing backslash (<c>R:</c> or <c>R:\</c>).
    ///
    /// <para>The <c>\\.\R:</c> "Mount Manager" form is deliberately NOT accepted. Both the
    /// Windows-service host and the diagnostic host build that form themselves by prepending
    /// <c>\\.\</c> to the configured value, so a config value that already contained it would
    /// become <c>\\.\\.\R:</c> and access-violate inside FspFileSystemSetMountPointEx. UNC
    /// paths, forward slashes and a missing colon reach the same native call, which has no
    /// error path — so all of them are rejected at startup instead.</para>
    /// </summary>
    private static bool IsValidMountPoint(string mountPoint)
        => mountPoint.Length switch
        {
            2 => mountPoint[1] == ':' && IsAsciiLetter(mountPoint[0]),
            3 => mountPoint[1] == ':' && mountPoint[2] == '\\' && IsAsciiLetter(mountPoint[0]),
            _ => false,
        };

    private static bool IsAsciiLetter(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
