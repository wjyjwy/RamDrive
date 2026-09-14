## 1. Fix

- [x] 1.1 `RamFileSystem.RestoreNode`: clip each snapshot page to the snapshotted length
      (`remaining = snap.Length - offset`; `continue` when `remaining <= 0`; write
      `data[..min(data.Length, remaining)]`), so `PagedFileContent.Write` can never extend the file.
- [x] 1.2 Document the length-extension behaviour of `PagedFileContent.Write` in its XML doc — it is
      the trap that made the verbatim page replay look correct.

## 2. Regression test

- [x] 2.1 `ReloadSnapshotTests.RoundTrip_PreservesExactLogicalLength_WhenNotPageAligned`: a 1000 B
      patterned file, a 130 B file extended to 4096 B via `SetLength`, and a length-only sparse file
      (100 B, no allocated page); snapshot → restore into a **larger** pool (2 MB → 4 MB, i.e. the
      capacity-only reload that triggers the bug) → assert each `Size`, byte-for-byte content, and
      `Read(length, probe) == 0`.
- [x] 2.2 Confirmed the test reproduces the defect on the pre-fix code with the reported symptom:
      `Expected fs2.FindNode(@"\notes.txt")!.Size to be 1000L, but found 65536L (difference of 64536)`.

## 3. Verification

- [x] 3.1 `dotnet test tests/RamDrive.Core.Tests` — 106/106 green, including the 6 reload/snapshot
      tests.
- [x] 3.2 `dotnet build src/RamDrive.Cli/RamDrive.Cli.csproj` — clean. (AOT publish itself needs the
      VS C++ Build Tools and was not run in this session.)
- [x] 3.3 Reviewed the other write paths for the same trap: `WinFspRamAdapter.WriteFile` clamps to the
      file size when `constrainedIo` is set and intentionally extends otherwise; `OverwriteFile` /
      `SetFileSize` only call `SetLength`. `RestoreNode` was the only caller that wrote past EOF by
      accident.
- [ ] 3.4 Manual end-to-end on a live mount (optional, operator-side): write a 1000-byte text file,
      increase `CapacityMb` in `appsettings.jsonc`, let the reload finish, verify the size stays 1000
      and the tail contains no NUL padding. The defect lives in the platform-neutral restore path, so
      the unit test covers it without WinFsp.

## 4. Docs and spec

- [x] 4.1 `openspec/changes/fix-reload-null-padding/` — proposal, design, tasks, capability spec
      `volume-reload`.
- [x] 4.2 `docs/reload-null-padding-postmortem.md`.
- [x] 4.3 `README.md` — reload section references the exact-length guarantee.
- [x] 4.4 `CLAUDE.md` — restore invariant under Key Design Decisions, plus test/spec/postmortem
      pointers.
- [ ] 4.5 `openspec validate fix-reload-null-padding` — needs the openspec CLI, which is not
      available in this session.
