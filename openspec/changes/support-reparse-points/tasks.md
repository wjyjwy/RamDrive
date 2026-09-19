## 1. Vendored WinFsp.Native with slot-19 wiring

- [x] 1.1 Vendor `WinFsp.Native` 0.1.3-pre.1 source (commit `5a0dd4d`) into
      `src/WinFsp.Native/` with MIT `LICENSE`; new net10.0 csproj (unsafe, AOT-compatible);
      add to `RamDrive.slnx`.
- [x] 1.2 `FspFileSystemInterface.ResolveReparsePoints` (slot 19) typed as the native
      function pointer (`VENDORED DELTA`).
- [x] 1.3 `FspApi`: `LibraryImport` wrappers for exported
      `FspFileSystemFindReparsePoint` / `FspFileSystemResolveReparsePoints`; new
      `NtStatus` constants `IoReparseTagMismatch` / `IoReparseDataInvalid` /
      `ReparseAttributeConflict`.
- [x] 1.4 `FileSystemHost`: `OnResolveReparsePoints` + `OnGetReparsePointByName` shims;
      slot 19 wired in `PopulateInterface`; `OnGetSecurityByName` suffix-probes with
      `FspFileSystemFindReparsePoint` on not-found and returns `STATUS_REPARSE`.
- [x] 1.5 `IFileSystem.GetReparsePointByName` (default `NOT_A_REPARSE_POINT`).
- [x] 1.6 Switch all 8 projects from the `WinFsp.Native` PackageReference to a
      ProjectReference; remove the package version pin.

## 2. Core reparse support

- [x] 2.1 `FileNode.ReparseTag` / `ReparseData` / `IsReparsePoint`; release blob on
      `Dispose`.
- [x] 2.2 New pure helper `ReparsePointBuffer` (tag/flag constants, tag read,
      `CanReplace` — port of `FspFileSystemCanReplaceReparsePoint`).
- [x] 2.3 `WinFspRamAdapter.Init`: `ReparsePoints=true`, `ReparsePointsAccessCheck=false`.
- [x] 2.4 `WinFspRamAdapter`: `GetReparsePointByName`, `GetReparsePoint`,
      `SetReparsePoint`, `DeleteReparsePoint` with memfs semantics (empty-dir-only,
      replacement gate, attribute/tag maintenance, ChangeAttributes notify).
- [x] 2.5 `MakeFileInfo` reports `ReparseTag`.
- [x] 2.6 Snapshot capture/restore carries tag + blob (cloned) so links survive reload.

## 3. Differential oracle parity

- [x] 3.1 `MemfsNode.ReparseTag`; `MkInfo` reports it.
- [x] 3.2 `MemfsReferenceFs`: same four callbacks + private LOCKSTEP CanReplace port.
- [x] 3.3 `DifferentialAdapter.GetReparsePointByName`; compare blob on Get/ByName and
      `ReparseTag` in `CompareFileInfo`; source-generated contract still passes.

## 4. Tests

- [x] 4.1 `ReparsePointBufferTests` (cross-platform): replacement-gate truth table.
- [x] 4.2 Snapshot reparse round-trip case in `ReloadSnapshotTests`.
- [x] 4.3 `WinFspRamAdapterReparseTests` (Windows): all four callbacks + rejection
      branches + attribute visibility.
- [x] 4.4 `ReparsePointTests` integration (UNC mount): absolute/relative file symlink,
      directory symlink, junction via raw FSCTL, delete-keeps-target, late target.

## 5. Verify

- [x] 5.1 `dotnet build RamDrive.slnx` — clean, no new warnings (Linux; net10.0.103).
- [x] 5.2 `dotnet test tests/RamDrive.Core.Tests` — all non-SDDL tests green on Linux
      (Windows-only classes fail identically to the pre-existing 24 SDDL-platform tests;
      they run on Windows CI).
- [x] 5.3 `dotnet test` full suite green on Windows (mount integration incl. symlink
      traversal; `RAMDRIVE_DIFF=1` differential leg). Measured: Core 154/154,
      Integration 59/59, differential leg 47/47 (concurrency suites excluded — see the
      `--filter` note in `ci.yml`).
- [ ] 5.4 `openspec validate support-reparse-points` — not runnable in this environment
      (no `openspec` CLI on PATH); must be run in a shell that has it.
- [x] 5.5 Manual Windows acceptance: `mklink`, `mklink /D`, `mklink /J` on R: all work,
      links resolve from cmd/PowerShell. Junction traversal is subject to the
      target-volume limitation documented in `design.md` (RAM→RAM fails at the I/O
      manager, not in the adapter).
