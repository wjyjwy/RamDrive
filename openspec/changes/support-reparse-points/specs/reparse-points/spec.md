## ADDED Requirements

### Requirement: The volume SHALL support reparse points (symbolic links and junctions)

The file system MUST report `ReparsePoints = true` in its WinFsp volume parameters and MUST
implement the WinFsp 2.x reparse-point callbacks `GetReparsePoint`, `SetReparsePoint`,
`DeleteReparsePoint`, plus the name-based `GetReparsePointByName` used by user-mode link
resolution. Each node MUST store the raw reparse buffer (whose first DWORD is the reparse
tag) and expose `FILE_ATTRIBUTE_REPARSE_POINT` in its attributes and the tag in
`FspFileInfo.ReparseTag` while a reparse buffer is attached.

The opaque buffer MUST be returned byte-for-byte unchanged for get and resolution
operations; the file system does not itself parse or interpret link targets. Link NAME
RESOLUTION — following a symlink or junction during Create/Open, including relative and
NT-namespace-absolute targets, `.`/`..`, and link chains up to WinFsp's hop limit — is
performed by WinFsp via the `ResolveReparsePoints` interface slot (slot 19), which MUST be
wired.

#### Scenario: File symbolic link is created and followed
- **WHEN** a file symbolic link is created on the mounted volume (via `mklink`,
  `File.CreateSymbolicLink`, or `FSCTL_SET_REPARSE_POINT` with `IO_REPARSE_TAG_SYMLINK`)
- **THEN** the link node reports `FILE_ATTRIBUTE_REPARSE_POINT` and tag
  `IO_REPARSE_TAG_SYMLINK`
- **AND** opening or reading through the link path reaches the target file
- **AND** deleting the link removes only the link, never the target

#### Scenario: Relative symbolic link resolves against its containing directory
- **WHEN** a symlink stores a relative target (`SYMLINK_FLAG_RELATIVE`)
- **THEN** traversing the link resolves the target relative to the link's directory
- **AND** the resolved file is readable through the link path

#### Scenario: Directory symbolic link traverses into the target directory
- **WHEN** a directory symbolic link is created (`IO_REPARSE_TAG_SYMLINK`)
- **THEN** enumerating and creating files through the link path operates inside the target
  directory

#### Scenario: Junction is created, queried, and deleted
- **WHEN** a directory junction is created (`IO_REPARSE_TAG_MOUNT_POINT`, via raw
  `FSCTL_SET_REPARSE_POINT` — `mklink /J` itself refuses any volume that is not a local
  fixed NTFS volume, and WinFsp volumes report `DRIVE_REMOTE`)
- **THEN** the node reports `FILE_ATTRIBUTE_REPARSE_POINT` + `FILE_ATTRIBUTE_DIRECTORY` and
  tag `IO_REPARSE_TAG_MOUNT_POINT`
- **AND** `FSCTL_GET_REPARSE_POINT` returns the stored buffer byte-for-byte
- **AND** deleting the junction removes only the junction, never the target tree

#### Scenario: Junction traversal requires a local-fixed target volume
- **WHEN** a junction's target lies on the RAM drive (or any volume that is not a local
  fixed disk)
- **THEN** traversal fails with `STATUS_IO_REPARSE_DATA_INVALID`. WinFsp mounts a UNC path
  as `FILE_DEVICE_NETWORK_FILE_SYSTEM` / `FILE_REMOTE_DEVICE`, so the RAM drive reports
  `DRIVE_REMOTE`; Windows does not follow mount-point reparse indirection onto a non-local
  volume. Measured direction matrix (link location × target location):
  `RAM→RAM` fails, `RAM→NTFS` works, `NTFS→RAM` fails, `NTFS→NTFS` works — i.e. only the
  TARGET volume matters, not where the link itself lives.
- **WHEN** the target lies on a local fixed volume
- **THEN** traversal succeeds
- **NOTE** Directory symbolic links are unaffected — the DLL resolves them into an absolute
  path itself — so they traverse to a RAM-drive target normally and are the supported way to
  alias a directory on this volume.

#### Scenario: The link itself can be opened with FILE_OPEN_REPARSE_POINT
- **WHEN** an existing link is opened with `FILE_OPEN_REPARSE_POINT`
- **THEN** the open succeeds against the link node itself (no name resolution), allowing
  `FSCTL_GET_REPARSE_POINT` / `FSCTL_DELETE_REPARSE_POINT` to address the link

#### Scenario: Reparse data is queried and deleted through FSCTL
- **WHEN** `FSCTL_GET_REPARSE_POINT` is issued against a link
- **THEN** the exact stored `REPARSE_DATA_BUFFER` is returned
- **WHEN** `FSCTL_DELETE_REPARSE_POINT` with a matching tag is issued against a link
- **THEN** the buffer, tag, and `FILE_ATTRIBUTE_REPARSE_POINT` are all removed and the
  node becomes an ordinary file/directory
- **WHEN** the same requests reach a plain node
- **THEN** get returns `STATUS_NOT_A_REPARSE_POINT` and delete returns
  `STATUS_NOT_A_REPARSE_POINT`

### Requirement: Reparse point replacement SHALL enforce tag/GUID consistency

`SetReparsePoint` on a node that already carries a reparse point, and every
`DeleteReparsePoint`, MUST validate the incoming buffer against the stored point using the
same rule as WinFsp's `FspFileSystemCanReplaceReparsePoint`:

- a buffer shorter than the leading tag DWORD fails with `STATUS_IO_REPARSE_DATA_INVALID`;
- a different reparse tag fails with `STATUS_IO_REPARSE_TAG_MISMATCH`;
- for non-Microsoft tags (high bit clear), a buffer too short for the
  `REPARSE_GUID_DATA_BUFFER` header, or a differing identifying GUID, fails with
  `STATUS_REPARSE_ATTRIBUTE_CONFLICT`. The GUID comparison MUST cover all 16 bytes
  (`Data1`@4, `Data2`@8, `Data3`@10, `Data4`@12) — WinFsp compares it as four DWORDs, so a
  difference in any byte blocks the replacement.

A rejected set/delete MUST leave the existing reparse state and attributes unchanged.

#### Scenario: Replacing a symlink with a junction is rejected
- **WHEN** `SetReparsePoint` offers an `IO_REPARSE_TAG_MOUNT_POINT` buffer on a node that
  already carries `IO_REPARSE_TAG_SYMLINK`
- **THEN** the call returns `STATUS_IO_REPARSE_TAG_MISMATCH`
- **AND** the node remains a symlink with its original buffer

#### Scenario: Delete requires a matching tag
- **WHEN** `DeleteReparsePoint` is issued with a buffer whose tag differs from the stored
  tag
- **THEN** the call returns `STATUS_IO_REPARSE_TAG_MISMATCH` and the link remains

### Requirement: A non-empty directory SHALL NOT become a reparse point

`SetReparsePoint` against a directory that contains children MUST return
`STATUS_DIRECTORY_NOT_EMPTY` without changing the directory. This matches WinFsp memfs and
NTFS behavior for junctions/symlink directories, which are always created from empty
placeholders, and keeps a directory's child list consistent with its reparse state.

#### Scenario: Junction creation on a directory with children fails
- **WHEN** `FSCTL_SET_REPARSE_POINT` with `IO_REPARSE_TAG_MOUNT_POINT` targets a directory
  that contains one or more children
- **THEN** the call returns `STATUS_DIRECTORY_NOT_EMPTY` and the directory is not marked
  as a reparse point

### Requirement: Reparse state SHALL survive a reload snapshot/restore

The volume snapshot used for configuration reload MUST capture and restore each node's
reparse tag, raw reparse buffer (as independent copies), and attributes (including
`FILE_ATTRIBUTE_REPARSE_POINT`). After restore into a fresh file system, existing
symlinks MUST be traversable without being re-created, and junctions MUST keep their
blob/attribute/tag (subject to the target-volume traversal limitation above).

#### Scenario: Links remain valid after a reload
- **WHEN** a snapshot containing a file symlink and a directory junction is restored into
  a fresh `RamFileSystem` and the volume is mounted
- **THEN** both nodes report their original tag, attribute, and reparse buffer
- **AND** traversing the file symlink reaches its target
