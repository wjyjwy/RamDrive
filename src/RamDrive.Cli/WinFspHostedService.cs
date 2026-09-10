using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using WinFsp.Native;

namespace RamDrive.Cli;

/// <summary>
/// Hosts the WinFsp mount as a Windows service / console session.
///
/// <para><b>Reload</b>: the volume lives entirely in RAM, so "reload" = capture the full
/// filesystem into a <see cref="FileSystemSnapshot"/> (in memory, no disk I/O), tear
/// down the current session, build a fresh one from the current configuration, restore
/// the snapshot into it, and re-mount. Because the restore happens on the NEW session
/// while the old one is still serving, a reload that would not fit into the new capacity
/// (or fails for any other reason) aborts cleanly and the running volume is untouched.
/// The reload is triggered automatically by a change to <c>appsettings.jsonc</c>
/// (debounced).</para>
///
/// <para>Sequence on reload: snapshot → create fresh session (validates config) → restore
/// snapshot (validates capacity) → unmount old → mount fresh. Each step before the
/// unmount keeps the old volume live.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinFspHostedService : BackgroundService
{
    private readonly IOptionsMonitor<RamDriveOptions> _optionsMonitor;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WinFspHostedService> _logger;

    private Session? _session;

    // Reload is serialized: only one reload in flight, config changes during it are
    // coalesced (a new change re-arms the debounce afterwards).
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile bool _reloadScheduled;
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(1000);

    public WinFspHostedService(
        IOptionsMonitor<RamDriveOptions> optionsMonitor,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory)
    {
        _optionsMonitor = optionsMonitor;
        _lifetime = lifetime;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WinFspHostedService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var opts = _optionsMonitor.CurrentValue;
            _logger.LogInformation("RamDrive starting: mount={MountPoint} capacity={CapacityMb}MB pageSize={PageSizeKb}KB",
                opts.MountPoint, opts.CapacityMb, opts.PageSizeKb);

            // Validate the configuration up front. A bad appsettings.jsonc then produces a
            // readable, actionable message instead of the generic "Unexpected error during
            // mount" wrapping a PagePool InvalidOperationException — and, critically, this runs
            // before a malformed MountPoint can reach the native mount call, which access-violates.
            var configErrors = opts.Validate();
            if (configErrors.Count > 0)
            {
                _logger.LogError("Invalid configuration. Please fix appsettings.jsonc:{NewLine}{Errors}",
                    Environment.NewLine, string.Join(Environment.NewLine, configErrors));
                Environment.ExitCode = 1;
                _lifetime.StopApplication();
                return;
            }

            // Hazardous-but-legal combination: a permanent kernel FileInfo cache with no active
            // invalidation means a stale entry for a path never expires AND nothing invalidates
            // it — the leveldb/Chromium failure mode in docs/leveldb-cache-coherency-postmortem.md.
            // Warn instead of refusing to start, because the differential test leg legitimately
            // uses this combination.
            if (opts.EnableKernelCache && !opts.EnableNotifications && opts.FileInfoTimeoutMs == uint.MaxValue)
            {
                _logger.LogWarning(
                    "FileInfoTimeoutMs=4294967295 (permanent kernel cache) with EnableNotifications=false: " +
                    "cached metadata for a path never expires and nothing invalidates it. Set " +
                    "EnableNotifications=true, or lower FileInfoTimeoutMs to bound the staleness.");
            }

            // Ensure WinFsp uses Mount Manager from kernel driver (no admin needed for mount).
            // WinFsp reads this once from HKLM\SOFTWARE\WOW6432Node\WinFsp on x64.
            EnsureMountMgrFromFSD();

            var session = CreateSession(opts);
            if (!MountSession(session))
            {
                session.Dispose();
                Environment.ExitCode = 1;
                _lifetime.StopApplication();
                return;
            }

            CreateInitialDirectories(session);
            _session = session;

            // Auto-reload whenever appsettings.jsonc (or a CLI override) changes.
            using var registration = _optionsMonitor.OnChange((_, _) => ScheduleReload());

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (DllNotFoundException)
        {
            _logger.LogError("WinFsp is not installed. Install from: https://winfsp.dev/rel/");
            Environment.ExitCode = 1;
            _lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Mount cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during mount");
            Environment.ExitCode = 1;
            _lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Unmounting drive...");

        // Signal cancellation to ExecuteAsync first (BackgroundService contract)
        await base.StopAsync(cancellationToken);

        try
        {
            _session?.Dispose();
            _session = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during unmount");
        }

        _logger.LogInformation("Drive unmounted");
    }

    // ─── Reload ────────────────────────────────────────────────────────────────────

    private void ScheduleReload()
    {
        if (_reloadScheduled) return;
        _reloadScheduled = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ReloadDebounce); // debounce package-touches of appsettings.jsonc
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reload failed");
            }
            finally
            {
                _reloadScheduled = false;
            }
        });
    }

    private async Task ReloadAsync()
    {
        await _reloadLock.WaitAsync();
        try
        {
            var old = _session;
            if (old == null) return; // not mounted (startup failed) — nothing to reload
            if (old.Host == null) return;

            RamDriveOptions opts;
            try
            {
                opts = _optionsMonitor.CurrentValue;
                var errors = opts.Validate();
                if (errors.Count > 0)
                {
                    _logger.LogError("Reload aborted — invalid configuration: {Errors}",
                        string.Join("; ", errors));
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reload aborted — could not build a new session from current configuration");
                return;
            }

            _logger.LogInformation("Reloading RAM disk with current configuration...");

            // Fast path: the page layout (page size / capacity) is unchanged, so the
            // existing tree AND page pool are reused as-is — the adapter (volume label,
            // kernel-cache flags, mount point) and the host are the only things swapped.
            // This is ZERO data copies: no snapshot, no restore. Applies to the most
            // common edits (MountPoint, VolumeLabel, EnableKernelCache, FileInfoTimeoutMs,
            // InitialDirectories). If it fails, the old filesystem object is still alive
            // and we fall through to the snapshot path with no data loss.
            if (opts.PageSizeKb == old.Options.PageSizeKb &&
                opts.CapacityMb == old.Options.CapacityMb)
            {
                if (ReloadFastPath(old, opts)) return;
                _logger.LogWarning("Fast reload failed — falling back to full snapshot reload");
            }

            await ReloadFullPathAsync(old, opts);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>
    /// Re-mount the existing filesystem+pools with a new adapter (no data copies).
    /// Returns true on success. On failure the old filesystem/pool objects are left
    /// alive so the caller can fall back to the snapshot path.
    /// </summary>
    private bool ReloadFastPath(Session old, RamDriveOptions opts)
    {
        // Rebuild the adapter around the SAME filesystem. The adapter constructor keeps
        // the root security descriptor when it already exists (fast-reload contract).
        var newAdapter = new WinFspRamAdapter(
            old.Fs,
            Options.Create(opts),
            _loggerFactory.CreateLogger<WinFspRamAdapter>());
        var pending = new Session { Pool = old.Pool, Fs = old.Fs, Adapter = newAdapter, Options = opts };

        // Unmount the old host; tree + pages keep living in memory.
        try
        {
            old.Host?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error unmounting old session during fast reload");
        }

        if (!MountSession(pending))
        {
            _logger.LogError("Fast reload failed — new mount did not succeed");
            return false; // old.Fs / old.Pool still alive → snapshot fallback owns disposal
        }

        CreateInitialDirectories(pending);
        _session = pending;
        _logger.LogInformation("Fast reload complete (zero data copies): capacity={CapacityMb}MB pageSize={PageSizeKb}KB",
            opts.CapacityMb, opts.PageSizeKb);
        return true;
    }

    /// <summary>
    /// Full reload: snapshot the whole volume into memory, rebuild a fresh session from
    /// the new configuration, restore, then swap mounts. Used when the page layout
    /// changed (capacity / page size) — the data must be re-chunked into the new pool.
    /// Every step before the unmount keeps the old volume live and fails safe.
    /// </summary>
    private async Task ReloadFullPathAsync(Session old, RamDriveOptions opts)
    {
        var snapshot = old.Fs.CreateSnapshot(); // step 1: capture state (in RAM)

        // Step 2: try to build + restore the NEW session while the old one still serves.
        // Any failure here (invalid config, capacity too small) aborts with zero impact
        // on the running volume.
        Session fresh;
        try
        {
            fresh = CreateSession(opts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reload aborted — could not build a new session from current configuration");
            return;
        }

        var restoreError = fresh.Fs.RestoreSnapshot(snapshot);
        if (restoreError != null)
        {
            _logger.LogError("Reload aborted — snapshot does not fit: {Error}. Old volume unchanged.", restoreError);
            fresh.Dispose();
            return;
        }

        // Step 3: commit — unmount the old session (freeing the old MountPoint) ...
        try
        {
            old.Host?.Dispose();
            old.Fs.Dispose();
            old.Pool.Dispose();
        }
        catch (Exception ex)
        {
            // Data is safe (snapshot is in RAM); surface and continue so the new
            // session can still be mounted.
            _logger.LogWarning(ex, "Error disposing old session during reload");
        }

        // Step 4: mount the fresh, pre-restored session.
        if (!MountSession(fresh))
        {
            _logger.LogError("Reload failed — new session could not be mounted; data is preserved in memory");
            fresh.Dispose();
            _session = null;
            return;
        }

        CreateInitialDirectories(fresh);
        _session = fresh;
        _logger.LogInformation("Reload complete: {FileCount} files/directories restored, capacity={CapacityMb}MB pageSize={PageSizeKb}KB",
            CountNodes(snapshot.Root), fresh.Options.CapacityMb, fresh.Options.PageSizeKb);
    }

    private static int CountNodes(NodeSnapshot n) =>
        1 + n.Children.Sum(CountNodes);

    /// <summary>
    /// A running mount: pool → filesystem → adapter → host, all bound to one
    /// configuration snapshot. Dispose unmounts and releases everything.
    /// </summary>
    private sealed class Session : IDisposable
    {
        public required PagePool Pool { get; init; }
        public required RamFileSystem Fs { get; init; }
        public required WinFspRamAdapter Adapter { get; init; }
        public required RamDriveOptions Options { get; init; }
        public FileSystemHost? Host { get; set; }

        public void Dispose()
        {
            Host?.Dispose();
            Fs.Dispose();
            Pool.Dispose();
        }
    }

    private Session CreateSession(RamDriveOptions opts)
    {
        var pool = new PagePool(Options.Create(opts), _loggerFactory.CreateLogger<PagePool>());
        var fs = new RamFileSystem(pool);
        var adapter = new WinFspRamAdapter(fs, Options.Create(opts), _loggerFactory.CreateLogger<WinFspRamAdapter>());
        return new Session { Pool = pool, Fs = fs, Adapter = adapter, Options = opts };
    }

    /// <summary>
    /// Mount <paramref name="session"/>: Mount Manager first, fall back to
    /// DefineDosDevice on failure. Returns true when mounted; the caller owns
    /// disposal on failure.
    /// </summary>
    private bool MountSession(Session session)
    {
        // WinFsp mount point format:
        //   "X:"     → DefineDosDevice (no admin required, invisible to disk benchmark tools)
        //   "\\.\X:" → Mount Manager (visible to all apps including ATTO, requires admin
        //               OR registry key MountUseMountmgrFromFSD=1)
        //
        // Strategy: try Mount Manager first, fallback to DefineDosDevice if it fails.
        string driveLetter = session.Options.MountPoint.TrimEnd('\\');
        string mountManagerPoint = @"\\.\" + driveLetter;

        var host = new FileSystemHost(session.Adapter);
        int result = host.Mount(mountManagerPoint);
        if (result >= 0)
        {
            _logger.LogInformation("Drive mounted at {MountPoint} via Mount Manager (visible to all apps).",
                session.Options.MountPoint);
        }
        else
        {
            _logger.LogWarning(
                "Mount Manager mount failed (0x{Status:X8}). Falling back to DefineDosDevice. " +
                "The drive works but may be invisible to disk tools (e.g. ATTO, Get-Volume). " +
                "Cause: WinFsp reads the MountUseMountmgrFromFSD registry value only when its " +
                "kernel driver loads, so a value written earlier in this same session has no " +
                "effect yet. Fix: reload that driver — restart Windows, or run " +
                "'sc stop winfsp' then 'sc start winfsp' as administrator — and restart the " +
                "RamDrive service. Running RamDrive.exe again without a driver reload will not help.",
                result);

            // FileSystemHost is not reusable after a failed mount — create a new one
            host.Dispose();
            host = new FileSystemHost(session.Adapter);

            result = host.Mount(driveLetter);
            if (result < 0)
            {
                _logger.LogError("WinFsp mount failed: 0x{Status:X8}", result);
                host.Dispose();
                return false;
            }

            _logger.LogInformation("Drive mounted at {MountPoint} via DefineDosDevice.", session.Options.MountPoint);
        }

        session.Host = host;
        return true;
    }

    private void CreateInitialDirectories(Session session)
    {
        var options = session.Options;
        if (options.InitialDirectories is not { Count: > 0 })
            return;

        var errors = options.InitialDirectories.Validate();
        if (errors.Count > 0)
        {
            _logger.LogError(
                "Invalid InitialDirectories configuration. Please fix appsettings.jsonc:{NewLine}{Errors}",
                Environment.NewLine,
                string.Join(Environment.NewLine, errors));
            return;
        }

        var count = CreateDirectoriesRecursive(@"\", options.InitialDirectories, session.Fs);
        _logger.LogInformation("Created {Count} initial directories on RAM disk", count);
    }

    private static int CreateDirectoriesRecursive(string parentPath, DirectoryNode entries, RamFileSystem fs)
    {
        int count = 0;
        foreach (var (name, children) in entries)
        {
            var path = parentPath == @"\" ? @"\" + name : parentPath + @"\" + name;
            if (fs.CreateDirectory(path) != null)
                count++;

            if (children.Count > 0)
                count += CreateDirectoriesRecursive(path, children, fs);
        }

        return count;
    }

    /// <summary>
    /// Ensures the WinFsp registry key MountUseMountmgrFromFSD=1 is set so that
    /// the WinFsp kernel driver handles Mount Manager registration (no admin needed for mount).
    /// On x64 systems, WinFsp reads from HKLM\SOFTWARE\WOW6432Node\WinFsp.
    /// Writing to HKLM requires elevation — if we don't have it, we silently skip.
    /// </summary>
    private void EnsureMountMgrFromFSD()
    {
        const string keyPath = @"SOFTWARE\WOW6432Node\WinFsp";
        const string valueName = "MountUseMountmgrFromFSD";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
            if (key?.GetValue(valueName) is int val && val != 0)
                return; // already set

            // Try to write it — requires admin
            using var writeKey = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            if (writeKey != null)
            {
                writeKey.SetValue(valueName, 1, RegistryValueKind.DWord);
                _logger.LogInformation("Set WinFsp MountUseMountmgrFromFSD=1 in registry. " +
                    "Mount Manager will be available from kernel driver on next launch.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // No admin — can't write. The fallback logic in ExecuteAsync will handle it.
        }
    }
}