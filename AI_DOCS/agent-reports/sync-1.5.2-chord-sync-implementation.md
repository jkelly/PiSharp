# Chord sync implementation

Status: implementation complete. First runtime pass found two root causes (empty gzip encoding and missing-file nullable conversion); both are repaired and focused retest is queued.

Source: Pi v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`, all eight `packages/chord/src/sync/*.ts` files. Approved contracts: `sync-1.5.2-chord-sync-discovery.md`.

## Changes

- New `src/PiSharp.Chord/Sync/{Remote,SessionSync,FlushScheduler}.cs`: remote/state interfaces and DTOs, history and snapshot selection, adaptive paged outbox upload and byte-bounded batches, interrupted pull shared-head recovery, lost-response handling, stale epochs, divergence/behind results, archive bootstrap, streamed replay/import, snapshot fold and tombstones, thread-safe single-flight scheduler with urgent/pending/retry/close behavior.
- New `src/PiSharp.Chord/Sync/{Codec,BodyStreams}.cs`: versioned log envelope, strict UTF8/newline/sequence validation, lazy gzip streams with bounded output channel, cancellation and source disposal, CRC/ISIZE verification, existing Store JSONL reuse for snapshots. SYNC-C report has details.
- New `src/PiSharp.Chord/Sync/{Files,FileSystemRemote,FileState}.cs`: portable injectable filesystem interfaces, native adapter, temp-file rename publication, body-before-slot ordering, folded slots, metadata/session enumeration, 256KiB streamed body reads, atomic per-session state. In-process concurrent writers are serialized; cross-process CAS is not supported (same limitation as upstream).
- New `tests/PiSharp.Chord.Tests/Sync/{CodecCases,FileCases,SessionCases,SchedulerCases}.cs`: 36 authored/source-derived native cases, auto-discovered by the existing Program harness. No test registration or shared file changes required.

## Native adaptations

`IAsyncEnumerable<ReadOnlyMemory<byte>>` represents a body, `long` represents a JS safe sequence, .NET cancellation tokens flow from ChordContext. Stream consumers own iterator disposal. Native filesystem streams open lazily after an existence probe to avoid retaining a file handle for an unconsumed body; a disappearance between probe and iteration is an ordinary filesystem failure. File writes use unique temporary names and cancellation-independent cleanup. Scheduler close is idempotent to keep concurrent native callers single-flight. Malformed nonincreasing remote chains are rejected instead of looping forever. Encoded batch/snapshot formats and valid protocol behavior match source.

## Verification

No local .NET builds or test runs were launched: `/root/validation` owns the sole build slot. Static source/API review completed. Coordinator has been asked to compile and run `--only sync.`. Coordinator build-r2 compiled production; its initial seven unqualified test-helper references were corrected to `ChordSupport.Rejection`. Coordinator subsequently confirmed no Sync compile errors remain. Final runtime acceptance remains pending. Existing Store APIs used directly; no Store or shared metadata files edited.

Full-r3 (`artifacts/gap-validation/sync152-chord-full-r3-8e4f1f6555654f2b9287ee6db8f4f144`) ran 33 sync cases: 26 passed, 7 failed. All nine session-client and all five scheduler cases passed, including the 24MiB history and interrupted 4MiB pull recovery. The codec failure was its existing empty-batch check: an unused .NET GZipStream emitted no gzip member. The six filesystem failures shared one missing-file cause: a conditional expression converted null through byte[] to a default ReadOnlyMemory rather than a nullable absence, attempting to parse empty JSON before the tested operations. Repairs emit a canonical empty gzip member and use an explicit nullable branch; strict decoding and error expectations remain. Three additional regressions cover empty streams, missing versus empty JSON files, and end-to-end filesystem/state upload/replay/fold/restore. Focused coordinator retest is pending.

Independent Node-only interoperability check: the new 20-byte canonical empty gzip member was accepted by the actual `DecompressionStream("gzip")` primitive used by upstream and decoded to zero bytes. `CompressionStream("gzip")` also emits a 20-byte member for empty input. This check does not substitute for the queued native test run.

### Coverage authored

Envelope/wire ordering and malformed fields; UTF8/gzip/CRC/newline/sequence boundaries; early iterator cancellation/release; filesystem atomicity/error cleanup/slot refusal/tombstones/reader closure; restore every store write kind; no echo upload; device handoff/divergence; lost response and epoch; lost local commits; unavailable outbox; archive bootstrap and unread rejection unlocking export; 24MiB bounded upload, 4MiB partial pull cancellation/resume; fold/snapshot restoration/missing history; scheduler urgency/concurrency/backoff/stale callback/close failure.

Requested model/tier: gpt-6.1-sol xhigh (parent), high (focused workers), ultrafast. Effective service tier unverified; delegation tools expose no tier selector.
