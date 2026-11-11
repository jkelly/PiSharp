# Chord host-data, live-query and app data review

Read-only static review against frozen upstream `b4afdddf44e059c67ccf5097e5317d913521b470`. No builds, tests, server calls, or implementation edits by this reviewer. Other owners were editing concurrently; line numbers below identify the reviewed state and may move.

Owner response: `/root/chord_files` confirmed both findings and is implementing outcome-aware cleanup using commit preparation/admission state, a source export hold through blob copying, and post-COMMIT fault-injection regressions. This review report describes the pre-fix findings; it does not claim those fixes have passed tests.

## Findings sent to implementation owner and root

### P1: Files failure cleanup can delete content after its metadata committed

`src/PiSharp.Chord.Hosting/App/Files.cs:101` and `:152` unconditionally delete the newly placed blob when `FilesIndex.ChangeAsync` throws. `src/PiSharp.Chord.Hosting/App/FilesCopy.cs:46-49` likewise removes the placed destination after the metadata import fails.

The exception does not imply rollback: `ChordStore.SeriesAsync` receives its backend transaction result at `src/PiSharp.Chord/Store/Store.cs:637`, then publishes metadata and invokes receipt/adoption callbacks at `:645-663`. A backend wrapper can commit and then throw; an adoption callback can throw after the commit. Both are surfaced as `unavailable/storage_failed`. `FilesIndex.ChangeAsync` publishes `Changed` after setting its current snapshot, so a throwing observer is another concrete post-commit trigger. The unconditional catch then removes the only file generation referenced by durable metadata. The store may be stopped, but reopening cannot recover the deleted bytes.

`AppArchiveService.ImportAsync` already explicitly preserves both generations for `unavailable/storage_failed`, recognizing the same unknown-outcome contract. Files put/finish/copy need equivalent outcome-aware cleanup. This does not require assuming SQL rollback failure: a commit-then-throw fake backend suffices.

Requested regression: inject one backend transaction that commits and then throws, separately for put, finish, and copy; assert the caller observes failure, reopen the target store, and verify every durable file record retains readable blob bytes. Also cover a throwing post-commit observer and an ordinary pre-commit refusal, which should still clean newly created unreferenced files.

### P2: File copy does not retain its source snapshot through blob acquisition

`src/PiSharp.Chord.Hosting/App/FilesCopy.cs:21` finishes `FilesIndex.ReadAsync` before the blob loop at `:25-37`. The read helper uses `ReadTransactionAsync`; its transaction has ended before `RejectLink`/`FileStream` opens the first or a later source blob at `:28-31`.

A concurrent `FilesService.RemoveAsync` can commit after the metadata snapshot and unlink a referenced blob before copy opens it. Sharing delete access protects a descriptor that is already open, but does not protect descriptors not yet acquired. The copy then fails `FileNotFoundException` despite starting with a valid source; with multiple files the same gap occurs between successive opens.

Frozen `packages/chord/src/node/app/files-copy.ts:1-5` explicitly holds the source export/commit queue through blob copying; `filesParts` copies each blob before advancing its export part. This is a functional lifetime guarantee, not a syntax difference. Retain the source commit queue through snapshot and blob acquisition/copy, or acquire equivalent pins/handles before releasing it.

Requested regression: gate execution after the source index snapshot and before the first blob open; concurrently remove its final source reference; then release the copy and verify the target contains the original bytes. Repeat with removal of a later blob while an earlier one is copying. Verify source removal eventually completes and cancellation releases every acquired hold.

## Other reviewed paths

- `Store/HostData.cs`, `Store/ImportMigration.cs`, `Store/HostColumn.cs`, `Store/Export.cs`, `Host/HostData.cs`, and activation/query hooks in `Host/Attempt.cs`.
- `Store/LiveQuery.cs`: declaration validation, captured runs cleared on replay, post-commit adoption, activation generation fencing, detached suppression, last-good state/failure episodes, token-based row reuse and structural diffs. No additional concrete serious defect established by this static pass.
- `Hosting/App/Archive.cs`, `Tar.cs`, `HostData.cs`, `FilesEntry.cs`, `FilesIndex.cs`, `Files.cs`, `FilesCopy.cs`, `Uploads.cs`, `Blobs.cs`, and current corresponding tests.
- Archive live export deliberately skips a deleted file before it can be opened; frozen upstream `archive.ts` documents that same behavior, so it was not reported as a native regression. Native tar/path/quota guards are stricter in several cases. Host import rechecks migrated host cells against the recorded destination host column, and query refresh remains inside the staged commit.
- The files owner was concurrently adding stored-index validation and closed-service read checks; this reviewer did not duplicate those edits or claim they were validated.

## Evidence commands

Used scoped `rg --files`, `rg -n`, and `Get-Content` to trace the native paths, matching frozen `packages/chord/src/{store/host-data.ts,store/import-migration.ts,store/live-query.ts,node/app/archive.ts,node/app/files-copy.ts}` and existing `HostDataCases.cs`, `LiveQueryCases.cs`, `FilesCases.cs`, `ArchiveCases.cs`. Findings were messaged directly to `/root/chord_files` and `/root`. Execution remains with `/root/validation`.
