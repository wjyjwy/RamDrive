## ADDED Requirements

### Requirement: A reload SHALL preserve the volume exactly

Editing `appsettings.jsonc` (or a CLI override) while the service runs triggers a debounced reload:
the filesystem is captured into an in-memory `FileSystemSnapshot`, a fresh session is built from the
new configuration, the snapshot is restored into it, and the drive is re-mounted. The restored volume
MUST be observationally identical to the volume that was captured: same node tree, same attributes
and timestamps, same security descriptors, same file data, same allocated/sparse page layout, and —
for every file — the same logical length.

#### Scenario: Contents survive a capacity-only edit
- **WHEN** `CapacityMb` is increased and the reload completes (page size, mount point and everything
  else unchanged)
- **THEN** every file and directory that existed before the reload still exists with byte-identical
  content and unchanged metadata
- **AND** no file has gained or lost bytes

#### Scenario: A reload that does not fit is a no-op
- **WHEN** the new configuration cannot hold the current contents (e.g. a smaller `CapacityMb`)
- **THEN** the reload aborts, the running volume keeps serving the pre-reload state, and the error is
  logged
- **AND** the aborted attempt does not consume pages from the running volume

#### Scenario: A malformed configuration is a no-op
- **WHEN** `appsettings.jsonc` fails validation at reload time
- **THEN** the reload aborts, the running volume is untouched, and the validation errors are logged

### Requirement: A restored file SHALL keep its exact logical length

Restoring a file MUST NOT change its logical length, and in particular MUST NOT round it up to a
multiple of the page size. A file whose length is not page-aligned MUST come back with exactly the
snapshotted length (and therefore the same `FileNode.Size` / `FspFileInfo.FileSize`), no matter how
many of its pages were allocated. Restore MUST NOT write bytes at or past the snapshotted length:
`PagedFileContent.Write` grows the logical length to the end of the written span, so the restore
clips each page payload to the length instead of replaying whole pages.

#### Scenario: Non-page-aligned file is not padded
- **WHEN** a 1000-byte file is snapshotted and restored with a 64 KB page size
- **THEN** the restored file's length is 1000
- **AND** the end of the file does not contain NUL padding (the original defect restored it as 65536)

#### Scenario: Nothing is readable past the logical end
- **WHEN** a read is issued at exactly the restored file's length
- **THEN** zero bytes are returned (EOF)

#### Scenario: Partially filled page, then extended with SetLength
- **WHEN** a file whose last written byte lies well below a page boundary is extended with
  `SetLength` and then restored
- **THEN** the restored file's length equals the extended length
- **AND** bytes beyond the originally written data read as zeroes

#### Scenario: Length without any allocated page
- **WHEN** a file was only ever extended via `SetLength` and holds no allocated page
- **THEN** the restored file has the same length and still consumes no page

#### Scenario: Sparse holes stay holes
- **WHEN** a multi-page file with an unallocated middle page is restored
- **THEN** the hole still reads as zeroes
- **AND** the restored file's `AllocatedBytes` equals the original's, and its length is exact

### Requirement: Restore SHALL preserve file metadata an editor or shell can observe

Beyond bytes and length, a restored file MUST keep its creation / last-write / last-access timestamps,
its `FileAttributes`, and its security descriptor, so tools that compare "file changed since last
build/run" (editors, git, build systems) behave as if nothing happened.

#### Scenario: Timestamps and attributes round-trip
- **WHEN** a file with a pinned `LastWriteTime` and the `Archive` attribute is restored
- **THEN** the restored node reports the same `LastWriteTime` and still has `Archive` set
