## Why

The RamDrive accepted every ordinary file/directory operation but could not host symbolic
links: `mklink`, `mklink /D`, .NET's `File.CreateSymbolicLink` /
`Directory.CreateSymbolicLink`, and any tool that relocates content via links (package
managers, build systems, browser profile redirects) failed against the drive.

Scope note on junctions (`mklink /J`, `IO_REPARSE_TAG_MOUNT_POINT`), established by
live testing on Windows against WinFsp 2.1 (a link-location × target-location matrix over
both the RAM drive and real NTFS):

- `mklink /J` cannot create a junction on a WinFsp volume: `cmd.exe` refuses because the
  volume is not a local fixed disk (`GetDriveType == DRIVE_REMOTE`; the file system name is
  correctly reported as `NTFS`). Junctions are therefore only reachable through raw
  `FSCTL_SET_REPARSE_POINT`.
- A junction created that way stores/queries/deletes correctly, but it can only be
  TRAVERSED when its TARGET is on a local fixed volume — and the RAM drive is not one
  (WinFsp mounts a UNC path as `FILE_DEVICE_NETWORK_FILE_SYSTEM` / `FILE_REMOTE_DEVICE`).
  Measured: `link=RAM→target=RAM` fails, `link=RAM→target=NTFS` works,
  `link=NTFS→target=RAM` fails, `link=NTFS→target=NTFS` works. So only the target volume
  matters, not where the link lives.

Directory symbolic links have neither limitation and are the supported way to alias a
directory on this volume.

Root cause on WinFsp 2.x (confirmed against winfsp `src/dll/fsop.c` / `tst/memfs/memfs.cpp`):

- `WinFspRamAdapter.Init` never set `host.ReparsePoints = true`, the adapter implemented
  none of `GetReparsePoint` / `SetReparsePoint` / `DeleteReparsePoint`, and `FileNode` had
  nowhere to store a reparse tag or blob.
- Even with those added, links would be **creatable but not followable**. On WinFsp 2.x
  reparse-name resolution lives in the **user-mode DLL** through interface slot 19
  `ResolveReparsePoints` (plus a `FspFileSystemFindReparsePoint` probe out of
  `GetSecurityByName`). The pinned `WinFsp.Native` 0.1.3-pre.1 package leaves slot 19 as
  `nint`/NULL and exposes no way to set it (`FileSystemHost` is sealed, its raw FS is
  private); when the slot is NULL every link traversal fails with
  `STATUS_INVALID_DEVICE_REQUEST`. The latest 0.1.3-pre.3 does not wire it either.

## What Changes

- **Vendor the `WinFsp.Native` binding** (MIT, zero-dependency, ~2.5k LOC) into
  `src/WinFsp.Native/` at exact upstream commit `5a0dd4d` (the 0.1.3-pre.1 source), then
  apply a clearly-marked `VENDORED DELTA`:
  - wire interface slot 19 to a shim that delegates to the exported
    `FspFileSystemResolveReparsePoints` helper;
  - make the `GetSecurityByName` shim run `FspFileSystemFindReparsePoint` on
    OBJECT_NAME_NOT_FOUND / OBJECT_PATH_NOT_FOUND, returning STATUS_REPARSE when a path
    prefix is a link (memfs parity);
  - add `IFileSystem.GetReparsePointByName(fileName, isDirectory, ref data)` with a
    STATUS_NOT_A_REPARSE_POINT default;
  - add the two helper P/Invokes and three reparse NTSTATUS constants.
- **Core reparse support**: `FileNode` gains `ReparseTag` / `ReparseData`;
  `WinFspRamAdapter` enables `ReparsePoints` (+ `ReparsePointsAccessCheck=false`, memfs
  parity), implements the three FSCTL callbacks and the name probe with 1:1 memfs
  semantics (empty-directory-only junctions → `DIRECTORY_NOT_EMPTY`; tag/GUID replacement
  gate → `STATUS_IO_REPARSE_TAG_MISMATCH` / `STATUS_REPARSE_ATTRIBUTE_CONFLICT`);
  `MakeFileInfo` reports the tag; set/delete notify the kernel cache. Snapshot/restore
  (reload) carries reparse state so links survive a config reload.
- **Differential parity**: `MemfsReferenceFs` gains the same four methods and
  `MemfsNode.ReparseTag`; `DifferentialAdapter` dispatches the new name probe and now
  compares both reparse statuses, returned blobs, and `FspFileInfo.ReparseTag`; the
  source-generated `IFileSystemContract` forces the new member onto every adapter.
- **Tests**: cross-platform unit tests for the tag/GUID replacement gate (every byte of the
  16-byte GUID) and the reparse snapshot round-trip; Windows-only adapter tests for all
  four callbacks; end-to-end mount tests for absolute/relative file symlinks, directory
  symlinks, junction create/read-back/delete via raw `FSCTL_SET_REPARSE_POINT`, link
  deletion, and a late-appearing target.

## Capabilities

### New Capabilities

- `reparse-points`: the volume supports NTFS reparse points. File and directory symbolic
  links can be created, queried, traversed, and deleted, behaving like a normal NTFS volume
  from the Win32 API's point of view. Junctions can be created, queried, and deleted the
  same way, but same-volume junction traversal is not supported (see the Why section) —
  directory symbolic links are the supported directory-alias mechanism.

### Modified Capabilities

None.

## Impact

- **Code**: new vendored project `src/WinFsp.Native` (all 8 consumers switch from
  `PackageReference` to `ProjectReference`; the package pin leaves
  `Directory.Packages.props`); `FileNode`, `WinFspRamAdapter`, `ReparsePointBuffer` (new),
  `RamFileSystem` snapshot paths, the memfs oracle, and the differential checker.
- **Tests**: 1 new cross-platform unit-test file + snapshot case + 1 Windows adapter-test
  file + 1 integration-test file.
- **Specs**: new capability `openspec/specs/reparse-points/spec.md` (created on archive).
- **User behaviour**: `mklink` and `mklink /D`, `File.CreateSymbolicLink` /
  `Directory.CreateSymbolicLink`, and link-aware applications now work on the RAM disk.
  `mklink /J` still does not (a `cmd.exe` restriction on any non-local-fixed volume, not
  something this change can affect), and same-volume junctions created via raw FSCTL are
  not traversable — use `mklink /D` to alias a directory.
- **TLA+**: no change. Reparse data is immutable-at-rest opaque metadata carried outside
  the page/pool model the verifier tracks; it does not touch pool accounting or write
  protocols.
- **Performance**: zero impact on the Read/Write hot path. Link resolution is a rare
  metadata operation; the vendored helpers allocate one scratch blob per resolution inside
  WinFsp's own DLL (as for stock memfs).
- **Upstream**: a follow-up could contribute the slot-19 wiring back to
  `hooyao/winfsp-native`; until then the local delta is marked with `VENDORED DELTA` at
  every deviation so a future package upgrade is a mechanical diff.
