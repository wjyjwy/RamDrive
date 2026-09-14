# Postmortem: capacity reload padded every file with NUL bytes to the page boundary

This document is the record of the bug fixed by openspec change
[`fix-reload-null-padding`](../openspec/changes/fix-reload-null-padding/).
It is written so a future session (or human reader) with no context beyond this file, the source
tree and the diff that landed the fix can pick up follow-on work.

---

## 1. Symptom

The user edited `appsettings.jsonc` to change **only `CapacityMb`** (e.g. 2048 → 4096) while the
service was running. The automatic reload ran, the drive re-mounted, contents looked fine at first
glance — until ordinary text files were opened in an IDE: **a long run of NUL characters at the end**
of many files. Reported as "修改配置只增加容量，很多文件用IDE打开，发现文件末尾会出现大量的null字符".

Measured on the failing build (unit-level reproduction, 64 KB pages):

| original length | length after the reload |
|-----------------|-------------------------|
| 1000 B          | 65536 B                 |
| 4096 B          | 65536 B                 |
| 3 B             | 65536 B                 |

The padding is not a display artefact: the restored file's *length* is wrong, so `dir`, the shell,
the kernel `FileInfo` cache and any later snapshot all agree on the padded size. There is no way to
tell the corruption from real data at the file-system level.

## 2. Root cause

A capacity change forces the **full reload path** (`WinFspHostedService.ReloadAsync`): the zero-copy
fast path is only taken when both `PageSizeKb` and `CapacityMb` are unchanged, because a different
capacity means a different `PagePool`. The full path is snapshot → fresh session → restore → re-mount,
and the bug was in the restore:

```csharp
// RamFileSystem.RestoreNode (before the fix)
if (!content.SetLength(snap.Length)) return "...";
foreach (var (offset, data) in snap.Content)              // data.Length == pageSize
    if (content.Write(offset, data.AsSpan()) != data.Length) return "...";
```

`PagedFileContent.EnumerateAllocatedData` snapshots **whole pages** `(page-aligned offset, pageSize
bytes)`, and `PagedFileContent.Write` ends with:

```csharp
if (endOffset > _length) _length = endOffset;   // extend to cover what was written
```

So replaying the last allocated page of a non-page-aligned file wrote bytes *past* EOF and dragged
the logical length up to the page boundary. `SetLength(snap.Length)` — which is called first and
looks like it establishes the length — is undone by the very next statement. The result is exactly
`ceil(length / pageSize) * pageSize` bytes, with zeroes in the tail (pages come from
`NativeMemory.AllocZeroed` and truncation zeroes page tails, so the padding reads as NULs).

Nothing about this is specific to a capacity change: any edit that forces the full path (capacity or
page size) pads every file whose length is not a multiple of the page size — which is essentially
every file a user cares about.

## 3. Why the test suite did not catch it

`tests/RamDrive.Core.Tests/ReloadSnapshotTests.cs` existed and passed. Two properties of the test
style made it blind to a wrong length:

1. Every content assertion read through a buffer **no larger than the original file**
   (`readBack = new byte[data.Length]; Read(0, readBack)`), and `PagedFileContent.Read` clamps to the
   logical length — so a too-large length is invisible to that shape of assertion.
2. The only assertion on a length used `3 * PageSize`, i.e. a value that is **exactly page-aligned**
   and therefore unaffected by the bug.

Lesson: when a round-trip is supposed to preserve metadata, assert the metadata (`Size`, `Length`,
`AllocatedBytes`, timestamps) directly and with values that are *not* aligned to the mechanism's
granularity. Testing bytes alone hides everything the mechanism rounds, clamps or pads.

## 4. The fix

`RamFileSystem.RestoreNode` now clips each page payload to the snapshotted length before writing:

```csharp
long remaining = snap.Length - offset;
if (remaining <= 0) continue;
var chunk = data.AsSpan(0, (int)Math.Min(data.Length, remaining));
if (content.Write(offset, chunk) != chunk.Length) return "...";
```

plus an XML-doc note on `PagedFileContent.Write` recording that it grows the logical length — the
property that is easy to miss from a call site.

The alternative "write everything, then `SetLength(snap.Length)` to trim back" was rejected: it makes
the correct end state depend on a second call that must never be forgotten, and costs an extra
full-page clear per file. See `design.md` §1 for the full comparison.

## 5. Invariant this relies on

*Bytes past a file's logical length are always zero* in `PagedFileContent`:

- `Read` clamps to `_length`, so they are unobservable to clients;
- every write extends `_length` to cover what it wrote, so non-zero bytes can only exist past EOF
  after a shrink;
- a shrink zeroes the retained last page's tail and batch-returns all pages beyond it;
  `OverwriteFile` goes through `SetLength(0)`, which frees everything;
- freshly rented pages are `AllocZeroed`.

Therefore dropping the tail during restore loses nothing, and a later `SetLength` extension exposes
zeroes — the same thing NTFS shows for the extended region.

## 6. Operational note for volumes that already reloaded with the bug

The padded lengths are now the volume's real data. Upgrading and reloading again preserves them (the
snapshot faithfully records the padded length), so affected files must be rewritten or truncated
once. Files that were never reloaded are unaffected.

## 7. Why neither the TLA+ model nor the differential checker caught it

- `tla/RamDiskSystem.tla` does not model the reload, and models data at **page granularity** by
  design ("Model at page granularity — bytes are unnecessary"). This defect lives precisely in the
  gap between a page-granular mechanism (page replay) and a byte-granular property (exact file
  length). Anything byte-level is out of the model's reach and needs a unit test.
- The differential checker (`DifferentialAdapter` vs `MemfsReferenceFs`) never sees a reload either —
  it compares live callbacks on one session.

The regression test therefore lives in `RamDrive.Core.Tests` (platform-neutral, no WinFsp mount) and
asserts exactly the metadata that the mechanism could silently round.
