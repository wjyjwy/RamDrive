## Why

Editing `appsettings.jsonc` to change **only `CapacityMb`** triggers the automatic volume reload.
Because the page layout changed, the reload takes the full path (snapshot → new session → restore →
re-mount) rather than the zero-copy fast path. After it finishes, text files opened in an editor show
a long run of NUL characters at the end, and their reported size is a multiple of the page size.

Root cause, in one line: **the restore replayed each snapshotted page as a whole page**, and
`PagedFileContent.Write` grows the logical length to the end of the written span
(`if (endOffset > _length) _length = endOffset`).

`PagedFileContent.EnumerateAllocatedData` hands the snapshot one `(page-aligned offset, pageSize
bytes)` pair per allocated page, and `RamFileSystem.RestoreNode` wrote those bytes back verbatim.
For a file whose length is not a multiple of the page size the last entry covers bytes *past* EOF,
so the restored file came back padded up to the page boundary:

| original length | after a capacity-only reload (before the fix) |
|-----------------|-----------------------------------------------|
| 1000 B          | 65536 B — 64536 NUL bytes                     |
| 4096 B          | 65536 B — 61440 NUL bytes                     |
| 3 B             | 65536 B — 65533 NUL bytes                     |

Every file is affected, not only files being written during the reload: the padding is baked into
the restored file's `Length`, so it is what the volume, the kernel `FileInfo` cache and every later
snapshot report from then on.

This is silent distortion of the whole volume, produced by an operation whose documented contract is
"volume contents are preserved across the reload" — so it is fixed at the source and pinned by a
regression test rather than worked around.

## What Changes

- `RamFileSystem.RestoreNode` clips each replayed page to the bytes that lie **inside** the
  snapshotted logical length (`remaining = snap.Length - offset`, skip entries at/after EOF), so a
  restored file's length is exactly the source file's length. Nothing is lost by clipping: bytes
  past EOF are provably always zero (see `design.md` §2).
- `PagedFileContent.Write`'s XML doc now states the length-extension behaviour explicitly — it is
  what turned the restore into a trap and it is not visible from the restore call site.
- New regression test `ReloadSnapshotTests.RoundTrip_PreservesExactLogicalLength_WhenNotPageAligned`
  covering a 1000 B file, a write-then-`SetLength`-extended file and a length-only sparse file,
  restored into a **larger** pool (the exact capacity-only reload). It fails on the pre-fix code with
  the reported symptom (`Expected ... Size to be 1000L, but found 65536L`).
- New capability spec `volume-reload` capturing the reload contract that was previously only implied
  by the README paragraph, with the exact-length guarantee as an explicit requirement.
- `docs/reload-null-padding-postmortem.md` records the full story (symptom → root cause → why the
  suite missed it → invariant), for readers with no session context.

## Capabilities

### New Capabilities

- `volume-reload`: the contract for the config-change reload (capture → fresh session → restore →
  re-mount): contents are preserved (node tree, attributes, timestamps, security descriptors, data,
  sparse layout), every file keeps its **exact** logical length, and a reload whose contents do not
  fit the new configuration aborts without touching the running volume.

### Modified Capabilities

None.

## Impact

- **Code**: `src/RamDrive.Core/FileSystem/RamFileSystem.cs` (`RestoreNode`),
  `src/RamDrive.Core/Memory/PagedFileContent.cs` (XML doc only, no behaviour change).
- **Tests**: `tests/RamDrive.Core.Tests/ReloadSnapshotTests.cs` (+1 test). The pre-existing tests
  could not catch this: they asserted data through `Read` destinations no larger than the original
  file (and `Read` clamps to the logical length), and the only `Length` assertion used an exactly
  page-aligned value.
- **Specs**: new capability `openspec/specs/volume-reload/spec.md` (created on archive).
- **Behavioural impact for users**: capacity / page-size edits no longer pad files. A volume that was
  already reloaded on a build with this bug carries the padded lengths as real data — those files
  must be rewritten or truncated once; a later reload preserves whatever is currently there.
- **TLA+ model**: no change, and none is possible. `tla/RamDiskSystem.tla` is page-granular by
  design ("Model at page granularity — bytes are unnecessary"), and it does not model the reload at
  all; this defect lives exactly in the gap between a page-granular mechanism (page replay) and a
  byte-granular property (exact file length). Byte-level behaviour therefore needs unit tests.
- **Performance**: negligible, marginally positive — the last page of a non-page-aligned file is no
  longer copied in full (and the `SetLength` truncation pass that would have been needed to fix the
  length afterwards is not needed either).
