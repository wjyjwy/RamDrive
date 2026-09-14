# RamDrive

A high-performance RAM disk for Windows, built with [WinFsp](https://winfsp.dev/) and .NET 10. Mounts a virtual drive backed entirely by system memory for maximum I/O throughput.

## Performance

Benchmarked with ATTO Disk Benchmark (Queue Depth 4):

| I/O Size | Write (Direct I/O) | Read (Direct I/O) | Read (Cached) |
|----------|--------------------|--------------------|---------------|
| 64 KB | 3.74 GB/s | 4.67 GB/s | ~9.5 GB/s |
| 256 KB | **6.47 GB/s** | **9.31 GB/s** | ~9.5 GB/s |
| 1 MB | 4.48 GB/s | 3.51 GB/s | ~9.5 GB/s |

With `EnableKernelCache: true` (default), cached reads match kernel-mode ImDisk performance (~9.5 GB/s).

## Architecture

```
WinFspRamAdapter (IFileSystem — all WinFsp callbacks, zero managed heap alloc on hot path)
    │
    ▼
RamFileSystem (path resolution, directory tree)
    │
    ▼
PagedFileContent (per-file page table: nint[])
    │
    ▼
PagePool (NativeMemory + ConcurrentStack<nint>)
```

**Key design decisions:**

- **NativeMemory page pool** -- all file data stored outside the GC heap via `NativeMemory.AllocZeroed`, zero GC pressure
- **Lock-free page allocation** -- `ConcurrentStack<nint>` with batch `TryPopRange`/`PushRange` for O(1) rent/return
- **Per-file ReaderWriterLockSlim** -- concurrent reads don't block each other
- **Sparse files** -- pages allocated on demand; unwritten regions consume no memory
- **Write-lock minimization** -- pages pre-allocated outside the lock, only memcpy inside
- **Native AOT compiled** -- single-file executable, no .NET runtime required

## Quick Start

**Option A: Installer (recommended)**

1. Download `RamDrive-X.Y.Z-setup.exe` from [Releases](https://github.com/hooyao/RamDrive/releases)
2. Run the installer — it bundles WinFsp, configures the drive letter and capacity, and registers a Windows Service
3. The RAM disk starts automatically on boot

**Option B: Portable**

1. Download the `.zip` from [Releases](https://github.com/hooyao/RamDrive/releases) and extract
2. Install [WinFsp](https://winfsp.dev/rel/) 2.x manually
3. Run `RamDrive.exe` — press `Ctrl+C` to unmount

### Configuration

Edit `appsettings.jsonc` or override via command line (`--RamDrive:CapacityMb=4096`):

```jsonc
{
  "RamDrive": {
    "MountPoint": "R:\\",           // Drive letter
    "CapacityMb": 2048,             // Total capacity in MB
    "PageSizeKb": 64,               // Page size (64 KB default, try 256 for large files)
    "PreAllocate": false,           // true = allocate all memory at startup
    "VolumeLabel": "RamDrive",      // Volume label in Explorer
    "EnableKernelCache": true,      // Kernel page cache (~3x read throughput)
    "FileInfoTimeoutMs": 1000,      // Kernel metadata cache lifetime in ms (0 = off)
    "EnableNotifications": true,    // Cache-invalidation notify after mutations (keep on)
    "InitialDirectories": {         // Directories created on mount
      "Temp": {}                    //   e.g. { "Temp": {}, "Cache": { "App1": {} } }
    }
  }
}
```

#### Reload on config change

Editing `appsettings.jsonc` while the service runs triggers an automatic, debounced **volume reload**: the current filesystem is captured as an in-memory snapshot (no disk I/O; sparse regions stay sparse), a fresh session is built from the new configuration (new capacity / page size / mount point), the snapshot is restored into it and the drive re-mounts. If the new configuration is invalid or the snapshot doesn't fit the new capacity, the reload aborts and the running volume is left untouched. Volume contents are preserved across the reload — including each file's **exact** length, so a non-page-aligned file never comes back padded with trailing NUL bytes. The contract is pinned by the `volume-reload` spec; the historical bug that violated it is recorded in `docs/reload-null-padding-postmortem.md`.

## Formal Verification

The core concurrency protocol is formally verified with [TLA+](https://lamport.azurewebsites.net/tla/tla.html) and the TLC model checker.

**What's verified (`tla/RamDiskSystem.tla`):**

| Property | Meaning |
|----------|---------|
| PoolConsistent | Pool accounting: `rented <= allocated <= maxPages` |
| NoPageLeak | Every rented page is accounted for (in a file or in-transit) |
| FreeBytesAccurate | `FreeBytes = Capacity - RentedPages` — never polluted by intermediate state |
| DataIntegrity | No file ever contains another file's data |
| ReadConsistent | Reads always return data belonging to the file being read |
| DeadFilesClean | Deleted files hold no pages |
| WriteTerminates | Every write eventually completes or fails (liveness) |

**Verified configurations:**

| Config | Pages | Files | Distinct States | Result |
|--------|-------|-------|----------------|--------|
| Minimal | 3 | 2 | 3,163,692 | All pass |
| Standard | 4 | 2 | 66,190,728 | All pass |

The model covers: 3-phase write (scan/rent/assign), concurrent reads, sparse extend, truncate, delete, file creation, `SetAllocationSize` TOCTOU check, and `GetVolumeInfo` as an external observer.

**Historical context:** An earlier narrow model (`PagePoolFixed.tla`) only verified the PagePool reserve/rent protocol. It missed a system-level bug where `Reserve()` in `SetLength` polluted `FreeBytes`, causing stale metadata under high concurrency. The full-system model (`RamDiskSystem.tla`) was built to prevent such gaps.

## Building from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [WinFsp](https://winfsp.dev/rel/) 2.x (install with Developer files).

```bash
# Regular build
dotnet build

# AOT publish (requires Visual Studio C++ Build Tools) — self-contained single exe
dotnet publish src/RamDrive.Cli/RamDrive.Cli.csproj -c Release -r win-x64 -o ./publish-aot

# Framework-dependent publish (no C++ toolchain; the machine that runs it needs .NET 10)
dotnet publish src/RamDrive.Cli/RamDrive.Cli.csproj -c Release -r win-x64 \
  -p:PublishAot=false --self-contained false -o ./publish-fx
```

The framework-dependent output is a folder rather than a single file; run it directly with
`publish-fx\RamDrive.exe` (WinFsp must be installed, and `appsettings.jsonc` sits next to the
executable). Use `-r win-arm64` / `publish-fx-arm64` for ARM64.

#### Run it as a Windows service

Running the exe in a console is fine for a test (Ctrl+C unmounts), but for daily use register the
build as a service. Why a service and not a console window:

- it mounts the RAM disk **at boot, before anyone logs in**, so `%TEMP%` / browser cache / app data
  directories that point at it already exist when the first user process starts;
- it survives logoff, screen lock, RDP disconnects and closing the window — a console instance dies
  with its window and takes the volume *and everything on it* with it;
- no console window in the taskbar;
- it runs as LocalSystem (independent of any logged-in user) and Windows restarts it if it crashes
  (5 s / 10 s / 30 s).

**Drag-and-drop deploy** (no installer, no command line):

```powershell
# from the repo root — builds the folder you copy
.\scripts\RamDrive-Service.ps1 stage
#   -> publish-fx\RamDrive\{ RamDrive.exe, *.dll, appsettings.jsonc, RamDrive-Service.cmd/.ps1 }
```

1. uninstall / stop the old installation (the old `RamDrive.exe` is locked while it runs)
2. copy `publish-fx\RamDrive` to `C:\Program Files\` → `C:\Program Files\RamDrive\`
3. double-click **`RamDrive-Service.cmd`** in the copied folder and press Enter

The launcher raises the UAC prompt, then offers a one-letter menu — `I` install (default),
`U` uninstall, `R` restart, `S` status — and keeps the elevated window open with the result. `stage`
inherits `appsettings.jsonc` from the currently installed copy, so the drive letter, capacity and
initial directories survive an upgrade; edit it before copying if you want different values.

The same script also works as a normal command line:

```powershell
.\scripts\RamDrive-Service.ps1 status                     # service / binary / config / WinFsp state
.\scripts\RamDrive-Service.ps1 install                    # copy the repo's publish folder to C:\Program Files\RamDrive
.\scripts\RamDrive-Service.ps1 install -Source .\publish-fx -Target D:\RamDrive
.\scripts\RamDrive-Service.ps1 restart
.\scripts\RamDrive-Service.ps1 uninstall
```

It registers exactly what the installer registers (auto start, LocalSystem, DisplayName, description,
crash recovery, early-boot group, WinFsp Mount Manager flag). Two safety rules worth knowing:

- It **refuses to install onto the drive the service mounts** — Windows has to read the binary before
  the service exists, so a service hosted on its own RAM disk would never start. (Also why the staged
  folder must be copied off the RAM disk before installing.)
- It **copies the files before stopping the old service**, so a source folder that itself lives on the
  RAM disk (a scratch checkout, for instance) survives the upgrade.

### Running the tests

```bash
# Unit tests — no WinFsp mount required, safe anywhere
dotnet test tests/RamDrive.Core.Tests

# Integration tests — REQUIRE a real WinFsp mount and a normally-launched console
dotnet test tests/RamDrive.IntegrationTests
```

**Run the integration tests from an elevated PowerShell or cmd**, not from a restricted or
tooling-spawned shell. In a constrained session every `Directory.CreateDirectory` /
file-create on a freshly mounted volume can fail with `UnauthorizedAccessException`, which
surfaces as ~42 spurious failures. The same calls succeed from a normally launched process
on the same volume, so this is a session artifact, not a product defect.

### Where the configuration actually lives

The host calls `UseContentRoot(AppContext.BaseDirectory)`, so `appsettings.jsonc` is read
from **the directory the executable sits in** — `C:\Program Files\RamDrive\` for an
installed service, the build output directory for a local `dotnet build` / `dotnet run`, or
`publish-fx\` for the framework-dependent publish.

Editing `src/RamDrive.Cli/appsettings.jsonc` therefore only affects a locally built binary.
It does **not** reach an installed service, and it will **not** trigger a reload of a
running service.

## Release

Push a tag matching `release-X.Y.Z` to trigger the CI pipeline, which builds, tests, and creates a GitHub Release with the AOT-compiled binary.

## License

MIT

This project uses [WinFsp - Windows File System Proxy](https://github.com/winfsp/winfsp), Copyright (C) Bill Zissimopoulos, under the GPLv3 with FLOSS exception.
