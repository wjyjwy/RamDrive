# Design — fix-reload-null-padding

## 1. Where the fix belongs

Three candidate places, one correct:

- **`PagedFileContent.Write`** — stop extending the length. Wrong. Growing the length to cover a
  write past EOF is required behaviour (a plain `WriteFile` at an offset past EOF must extend the
  file), and this is the hot path.
- **`PagedFileContent.EnumerateAllocatedData`** — snapshot only the bytes inside the length.
  Tempting but worse: the snapshot is meant to be a faithful dump of the page table (the "sparse
  layout stays sparse" guarantee is expressed as *which pages are allocated*), and clipping at
  capture time would either drop that information or make the payload inconsistent with the page
  list.
- **`RamFileSystem.RestoreNode`** — correct. The restore is the only place that knows both the
  logical length (`snap.Length`) and the byte-level page payload, and clipping there makes
  "a restored file has exactly the snapshotted length" hold by construction.

The alternative implementation — write the whole page and then call `SetLength(snap.Length)` to trim
back — was rejected: it makes the correct end state depend on a second call that must never be
forgotten, costs an extra full-page `NativeMemory.Clear` per non-aligned file, and every future
restore entry point would have to remember it. (It is also the reason the current code looked fine
at a glance: `SetLength(snap.Length)` *is* called first, it is just immediately undone by the write.)

## 2. Why clipping loses nothing

Bytes past EOF are unobservable, and the data structure guarantees they are zero anyway:

- `PagedFileContent.Read` clamps to `_length` (`if (offset >= _length) return 0`;
  `toRead = min(destination.Length, _length - offset)`), so no client can read past EOF.
- Every write covers what it wrote with the length (`_length = endOffset` when larger), so for a page
  to hold non-zero bytes past EOF there must have been a later shrink.
- A shrink (`SetLength(newLength < _length)`) zeroes the tail of the last retained page and
  batch-returns every page past it; `WinFspRamAdapter.OverwriteFile` goes through `SetLength(0)`,
  which frees every page. Freshly allocated pages come from `NativeMemory.AllocZeroed`.

So replaying the page tail only ever changed the length — which is precisely the bug — and dropping
it is safe for future extensions too: `SetLength` extension exposes zeroes either way, matching NTFS
semantics.

## 3. Why the existing tests missed it

`ReloadSnapshotTests` (before this change) verified content through `Read(dest)` calls whose
destination was never larger than the original file. Since `Read` clamps to the logical length, an
inflated length is completely invisible to that style of assertion. The one test that did assert a
length used `3 * PageSize` — a value that is exactly page-aligned and therefore unaffected by the
padding. Nothing asserted `Size`/`Length` for a file that is not a page multiple.

The new test asserts length first, then content, then EOF behaviour, and restores into a larger pool
so it reproduces the reported scenario (capacity-only edit) rather than a same-size round trip.

## 4. Sparse layout, reservations and capacity

`RestoreNode` still calls `SetLength(snap.Length)` before replaying pages, so odd-length sparse files
keep the same behaviour as before: pages inside the length that are not in the snapshot remain holes,
and pages in the snapshot are materialised. Clipping only ever *reduces* the number of bytes/pages a
file claims, so `RestoreSnapshot`'s `CountRequiredPages` pre-check stays a valid upper bound — a
snapshot that passes the check still restores successfully, and a failed restore still leaves the
target session disposable (the old volume is only torn down after a successful restore).

One incidental improvement: `Write`'s post-write reservation trim computes
`correctReserved = ceil(_length / pageSize) - allocatedPages`. With the inflated length it used to
trim against a page count the file did not really have; with the clipped write it trims against the
file's true page count.

## 5. Why a capacity-only edit takes the restore path

`WinFspHostedService.ReloadAsync` takes the zero-copy fast path only when **both** `PageSizeKb` and
`CapacityMb` are unchanged, because a different capacity means a different `PagePool` (the pool's
page count is fixed at construction). "Only increase the capacity" is therefore the canonical way to
reach the snapshot/restore path by accident, which is how users hit this.
