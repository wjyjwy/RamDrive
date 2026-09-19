# Design — Reparse point (symlink / junction) support

## WinFsp 2.x reparse architecture (the constraint that shapes everything)

On WinFsp 2.x symbolic-link/junction **name resolution is performed by the user-mode DLL**,
not the kernel driver. Verified against `winfsp` v2.1/master sources:

1. During Create/Open the DLL runs an access check (`src/dll/security.c`). When the path
   being opened carries (or crosses) `FILE_ATTRIBUTE_REPARSE_POINT` and the create did NOT
   pass `FILE_OPEN_REPARSE_POINT`, the check returns `STATUS_REPARSE`.
2. Every check site in `src/dll/fsop.c` (`CreateCheck`, `OpenCheck`, `OverwriteCheck`,
   `OpenTargetDirectoryCheck`, collision/not-found checks) then calls
   `FspFileSystemCallResolveReparsePoints`, which invokes interface **slot 19**
   `ResolveReparsePoints`. **When slot 19 is NULL it returns `STATUS_INVALID_DEVICE_REQUEST`.**
3. `GetSecurityByName` for a missing name is expected (as `tst/memfs` does) to call
   `FspFileSystemFindReparsePoint` and return `STATUS_REPARSE` when an intermediate path
   component is itself a link.
4. The FSCTL triple `FSCTL_{GET,SET,DELETE}_REPARSE_POINT` is dispatched to slots 20-22.
5. The two resolution primitives (`FspFileSystemFindReparsePoint`,
   `FspFileSystemResolveReparsePoints`) are `FSP_API` exports of `winfsp-x64.dll`; the
   reference file system implements slot 19 as a one-line forward to the exported helper,
   supplying only a `GetReparsePointByName(FileSystem, Context, FileName, IsDirectory,
   Buffer, PSize)` callback. All the hard logic — relative vs NT-namespace-absolute
   targets, `.`/`..`, junctions vs symlinks, 32-hop cap — lives inside WinFsp.

Consequently there are exactly two viable designs: (a) make slot 19 reachable and forward
to the exported helpers, or (b) reimplement the NT name-resolution algorithm ourselves.
(b) is hundreds of lines of surrogate-path edge cases already maintained in WinFsp; (a) is
what memfs does and is clearly correct. We chose (a).

## Why the binding is vendored

`WinFsp.Native` (same ecosystem, MIT, transitive-zero dependencies, AOT-ready) pins slot
19 as `public nint ResolveReparsePoints;` and `FileSystemHost` never assigns it, in both
0.1.3-pre.1 (the pinned version) and the current 0.1.3-pre.3. There is no supported hook
to add the slot from RamDrive:

- `FileSystemHost` is `sealed`; `PopulateInterface` is a private static method and the
  underlying `WinFspFileSystem` instance is a private field.
- Reflection cannot patch it under Native AOT (the shipping binary is AOT-compiled), and
  reaching `FSP_FILE_SYSTEM*` by relying on native struct offsets across WinFsp releases
  would be fragile.
- Re-mounting via the low-level API means re-implementing the ~900-line marshalling host
  we would otherwise duplicate wholesale.

The whole binding is only ~2.5k lines of C# with no dependencies, so we copy it into
`src/WinFsp.Native/` at the exact pinned commit and keep every file byte-identical to
upstream apart from blocks marked `VENDORED DELTA` (the slot-19 shim, the
FindReparsePoint suffix probe, the new `IFileSystem` member, two P/Invokes, and three
status constants). Upgrading later is a folder-diff.

## Data model

`FileNode` gains two fields:

- `uint ReparseTag` — first DWORD of the buffer, surfaced in every `FspFileInfo`;
- `byte[]? ReparseData` — the raw `REPARSE_DATA_BUFFER`, treated as opaque. The adapter
  never parses the target name: name resolution runs inside WinFsp against the stored
  blob. A non-null blob implies `FILE_ATTRIBUTE_REPARSE_POINT` in `Attributes`; this
  invariant is maintained only inside the Set/Delete callbacks.

Reparse blobs live on the GC heap. They are small metadata (a link target path), not file
data: keeping them out of `PagePool` preserves the zero-GC-heap hot-path guarantee
(Read/Write never touch them) and pool accounting/TLA+ scope.

## Callback semantics (memfs lockstep)

| Callback | Rule |
|---|---|
| `GetReparsePointByName` | missing node → `OBJECT_NAME_NOT_FOUND`; plain node → `NOT_A_REPARSE_POINT`; link → blob |
| `GetReparsePoint` (handle) | same states against the open node; host marshals size/copy |
| `SetReparsePoint` | `<4` byte buffer → `IO_REPARSE_DATA_INVALID`; non-empty directory → `DIRECTORY_NOT_EMPTY`; existing point must pass the `FspFileSystemCanReplaceReparsePoint` gate (same tag; same GUID for non-MS tags); then store clone, OR in the attribute, set the tag, send `ChangeAttributes` notify |
| `DeleteReparsePoint` | plain node → `NOT_A_REPARSE_POINT`; otherwise same replacement gate; then clear blob/tag/attribute and notify |

`ReparsePointBuffer.CanReplace` is a pure managed 1:1 port of
`FspFileSystemCanReplaceReparsePoint`, duplicated in the oracle with a LOCKSTEP comment
(the oracle stays project-independent; the differential checker proves the two agree).

## Paths deliberately not special-cased

- **Create/Open on the link itself**: the DLL resolves the target *before* dispatching
  Create/Open unless `FILE_OPEN_REPARSE_POINT` is present (mklink's placeholder open).
  No adapter change is needed.
- **Read/Write on a link node**: only reachable with `FILE_OPEN_REPARSE_POINT`; memfs
  allows ordinary data ops on such handles and so do we. Normal link nodes stay size 0.
- **`SetFileAttributes`**: assigned raw, exactly like the oracle. Attempts to toggle the
  reparse bit through basic info are filtered/handled above us the same way for both
  adapters, so differential parity is preserved.
- **Privilege checks**: symlink creation privilege / Developer Mode is enforced by the
  Windows I/O manager on the STATUS_REPARSE bounce; `ReparsePointsAccessCheck=false`
  matches memfs and only skips a second access check against the reparse point itself.

## Reload (snapshot) consistency

`NodeSnapshot` carries `ReparseTag`/`ReparseData` (cloned both on capture and on
restore), and `Attributes` already contains the reparse bit. No page-pool capacity is
consumed by links, so the restore capacity pre-check is unchanged. A restored symbolic
link is traversable immediately after the fresh volume mounts; a restored junction keeps
its blob/tag/attribute but is subject to the target-volume traversal limitation below.

## Junction traversal limitation (measured, not assumed)

Live testing on Windows against WinFsp 2.1 (`winfsp-x64.dll` 2.1.25156) with a full
link-location × target-location matrix:

| link on | target on | junction traversal |
| --- | --- | --- |
| RAM drive | RAM drive | **fails** `STATUS_IO_REPARSE_DATA_INVALID` |
| RAM drive | local NTFS | works |
| local NTFS | RAM drive | **fails** `STATUS_IO_REPARSE_DATA_INVALID` |
| local NTFS | local NTFS | works |

Only the TARGET volume matters; where the link itself lives is irrelevant. The cause is not
the adapter, not `ReparsePointBuffer`, and not the vendored slot-19 wiring (which is
line-for-line equivalent to upstream `tst/memfs/memfs.cpp`; instrumenting
`OnResolveReparsePoints` confirmed slot 19 IS invoked and DOES return `STATUS_REPARSE` with
the correct stored buffer):

- WinFsp mounts a UNC path as `FILE_DEVICE_NETWORK_FILE_SYSTEM`, and
  `src/sys/volume.c` passes `FILE_REMOTE_DEVICE` when creating the volume device for any
  non-disk device type. Hence `GetDriveType == DRIVE_REMOTE` for the RAM drive.
- Windows does not follow mount-point reparse indirection onto a non-local volume, so the
  create fails once the resolved target is seen to be remote. (This also explains why
  `mklink /J` refuses up front with "local NTFS volume required".)

Directory symlinks avoid it entirely because the DLL resolves them into an absolute NT path
and the FSD rewrites `FileObject->FileName` itself (`src/sys/create.c`, the `IO_REPARSE`
branch) instead of forwarding a reparse buffer to the I/O manager. That is also the shape a
future fix would need — return the resolved absolute path with `IO_REPARSE` in
`Information` — but since the target volume is the deciding factor, that alone would not
make RAM→RAM junctions work; it is deliberately NOT part of this change.

## Validation

- Cross-platform unit: replacement-gate truth table; snapshot round-trip incl. blob
  isolation.
- Windows-only adapter unit (constructed without a mount): all four callbacks, every
  rejection branch, attribute/tag visibility.
- Mount integration (UNC fixture): absolute & relative file symlinks (read/write/resolve/
  delete), directory symlink, junction built from a raw MOUNT_POINT buffer, a link whose
  target appears after the link does.
- Differential mode (`RAMDRIVE_DIFF=1`) now covers reparse status + blob + tag because
  the oracle implements the same methods and the source-generated contract forces
  dispatch completeness.
