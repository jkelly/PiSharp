# Chord Host/Reload/Jobs independent review

Reviewed 2026-11-08, working tree `E:/AI/PiSharp/gap-implementation`, against frozen v1.5.0 `E:/AI/PiSharp/upstream-v150` and D14 public mapping. Static read-only review except this report and status updates to review-core.md. No tests, builds, restore, live calls, or commits; I13 retains runtime validation ownership. Requested model gpt-6.1-sol/high/Ultrafast; effective tier unverified.

## Findings and disposition

All confirmed findings below have owner source corrections present at final source recheck. Runtime regressions remain queued with I13; this is not a passing-test claim.

### P2 — Native reload reported the wrong job cancellation reason (source corrected)

Original `Host/Attempt.cs` Seal passed literal `restart` to every `job.Seal`, including `Host/ReloadObject.cs` native restarts. A running transient job interrupted by native reload therefore produced `failed/aborted/restart` instead of `failed/aborted/reload`. Frozen `next/host/object.ts` explicitly seals native restarts with `reload`. H1 added a reason argument and H2 now supplies `reload`; source verification confirmed it. Dedicated native reload job regression is present; no passing execution inferred.

### P2 — Reference-state watch detach raced host state transitions (source corrected)

Original `Host/Reference.cs` ReferenceState retained native watches in an ordinary Dictionary and passed `() => watches.Remove(watch)` to NativeWatch. `WatchLifecycle.StopAsync` and cancellation call this detach synchronously on the caller thread, while the host adds or snapshots that Dictionary on its dispatcher. Concurrent external stop/cancel could corrupt the collection or throw during state delivery. Existing State/Internal.cs documents the thread-safe follower requirement. H1 replaced it with `Followers<StateWatch>`; source verification confirmed add/remove/snapshot now use that collection.

### P2 — ReleaseOwner retained the original host diagnostic delegate (source corrected)

Original `State/Batch.cs` constructor used `ReportListener = error => report(...)`, capturing the constructor parameter. Its new ReleaseOwner method cleared only `this.report`. Retaining an ended source/Batch after close could keep the original Host.Report delegate and closed host alive. H1 changed the lambda to use `this.report`; verified in source. This review does not claim GC/runtime qualification.

### P2 — Pure swap stopped forwarding old admitted calls' state too early (source corrected; tests pending)

Trigger: hold a state member/watch, begin an old method that will update it, start an in-place reload whose replacement activation waits in a Follow attachment, then allow the old method to update before the replacement becomes live. `Host/ReloadObject.cs:23-24` set Current to the new attempt and immediately retargeted to loading. `Host/Object.cs` Followed only used Current when live; `Host/Reference.cs:187` unsubscribed the old source before finding any replacement. Existing readers retained stale values and missed the old admitted call's changes during the swap. Frozen v1.5.0 keeps an exposed/following old attempt until the replacement goes live or parks and admitted changes settle. H2 confirmed no equivalent seam existed and implemented FollowingAttempt with H1 plus the exact delayed-Follow regression. Rechecked Object.Followed/PinKey/Variant/Instance and ReferenceKeySource: the old source remains selected during the swap, including keyed pins. Runtime validation remains pending.

### Drain/publication barrier concern (owner addressed in source; not independently runtime-proven)

The first reviewed RestartAsync did not await earlier retiring calls or Documents.SettledAsync after sealing, whereas frozen v1.5.0 waits for admitted document steps before native close and successor hydration. Transaction.StepAsync explicitly admits a Stepping commit beyond a seal. H2 added prior-retiring call drain and post-seal Documents.SettledAsync in native restart and pure retirement. Confirmed source presence. This was sent as a concern while tracing an admissible delayed-store trigger, not as a reproduced failure.

### P2 — Seal detached sources before an admitted document step could publish (source correction present; broader follow-up sent)

The originally reviewed `Host/Attempt.cs` Seal fenced/cancelled but also immediately called Self.Detach and detached adapted views. Self.Detach releases dependency references and terminates derived/followed sources. A `Documents/Transaction.cs` step already in Stepping survives seal and may still adopt into documents. Trigger: a persisted commit is already admitted but delayed beyond CloseTimeout during reload; old-call drain expires, seal detaches its derived/re-exposed values, and the accepted commit publishes afterward. The new Documents.SettledAsync barrier does not restore detached sources. Frozen v1.5.0 separates seal, documents.settled, stopFollowing and final detach/releaseReferences. H1/H2 split Seal from idempotent DetachSources, and reload/close now await Documents.SettledAsync before detaching. Source presence verified. A broader follow-up was also addressed in source: native restart and ordinary failure now preserve a following source through its HasSources lifetime; StopFollowing occurs after settlement/detachment. H2 added a real-host delayed-store fixture crossing the drain timeout and retaining final derived publication. No test execution by reviewer.
### P2 — A throwing job cancellation callback aborted teardown (source corrected; tests pending)

Locations: `Jobs/Records.cs:213`, `:286`, `:397` in the originally reviewed state. AbortAsync, Cut and End invoke `run.Invocation.Abort.Cancel()` directly. A job can register a throwing callback on JobContext.AbortSignal; CallContext links that signal to invocation.Abort. Cancel then throws AggregateException back into these lifecycle methods. During host close this stops End before waiter rejection and CountRuns cleanup, then escapes job.Seal/Attempt.Seal and can bypass remaining object/native cleanup and Store.Detach. J1 confirmed and centralized cancellation in a catch/report Cancel(JobRun) helper while continuing mandatory bookkeeping, with abort/native-reload/persisted-close regressions added. Source recheck confirmed all three callsites use it; no passing runtime claim.

### P2 — Reentrant close could start a second physical teardown (source corrected; tests pending)

`Host/Host.cs:248` initializes `closing` with `closing ??= CloseCoreAsync(context)`. The async method executes its synchronous preamble before the assignment: Closing callbacks and root.Dispose run while closing is null. A plugin/root cancellation callback (or owned link close callback) calling host.CloseAsync reenters the dispatcher inline and starts a second CloseCoreAsync, receiving a distinct task. The first task's completion can no longer promise the only physical close is finished; a later duplicate Store.Detach may detach a newly attached successor host. H1 now assigns an upfront TaskCompletionSource task to closing before calling CloseCoreAsync and added a root cancellation reentrancy case asserting exact task identity and one native teardown. Source correction verified; no runtime test run.

### P2 — Internal end/attempt close had the same reentrancy window (source corrected; tests pending)

`Host/Object.cs` assigns endingTask from FinishEndAsync, and `Host/Attempt.cs` assigns closing from CloseCoreAsync, after their synchronous prefixes execute. During last-reference teardown a native close callback can call host.CloseAsync synchronously and return a pending cleanup task. Nested host close reenters the object and attempt before those cached tasks are assigned. NativeHandle already marks the native closed, so the duplicate path skips the original pending close and can let host close finish/store detach early. H1 applied upfront TaskCompletionSource ownership to both internal methods, with a last-reference teardown/native callback reentry regression. Verified both cached tasks are assigned before Seal/native callbacks and nested callers join their actual pending completion. No runtime execution by reviewer.

## Scope read

D14 and current Host public contracts; dispatcher queue/reentry; native open/abandon/reverse close; Attempt activation, dependency wait, calls/routines, drain/seal; Host publication, acquire, registry wait, close, activity; Object references, retry, lifetime and document ownership; Reload stage/compatibility closure, swap/restart/retire and adapter sharing; Jobs declarations, dedupe/concurrency, transaction adoption, reservation, run state, finish retry, block/unblock, persisted decode/close writers; existing Documents transaction and seal implementation where needed for lifecycle tracing. Read current authored Host and ReloadJob regression sources. Compared relevant exact upstream next/host and next/jobs paths.

No finding is based merely on missing TypeScript-shaped API syntax. Unimplemented remote/legacy/hosting paths are outside this review. Current code is concurrently edited, so this report is a review log rather than an immutable acceptance receipt.
