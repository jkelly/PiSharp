# Telemetry 1.5.2 read-only review

Reviewed the native telemetry recorder, ingest contracts/facet/sender, SQLite recorder/schema/reader, shared clock and mixed attribute arrays against frozen upstream `b4afdddf44e059c67ccf5097e5317d913521b470`. Included the worker's sender lifetime because cancellation/cleanup depends on it. No implementation edits, builds, or test executions performed; findings below are source-proven and runtime reproduction remains with serialized validation.

## Finding: P1 — a stuck telemetry request blocks worker shutdown indefinitely

`src/PiSharp.Chord.Hosting/App/WorkerApp.cs:36-40` and `:103-107` cancel `sending` and await `sendLoop` before entering the final send's one-second timeout. However `SendLoopAsync` at `:127` only passes that token to `PeriodicTimer.WaitForNextTickAsync`; its current `sender.SendAsync()` wait has no cancellation. `src/PiSharp.Chord/TelemetryIngest/Sender.cs:20` issues the ingest with `ChordContext.Background`, and `:65` waits for that request without a bound.

Concrete reproduction: make the authorized live worker telemetry endpoint's `ingest` return a never-settling task; queue one span and allow the periodic send to enter that call; request worker close/cancellation. Both the registered `OnClosing` callback and the outer `finally` wait forever at `await sendLoop`, so neither reaches the advertised final-send timeout, `sender.Stop`, nor remaining host/store cleanup. This makes best-effort telemetry a prerequisite for application termination.

The frozen upstream `packages/chord/src/node/app/worker.ts:90-97` clears the periodic timer and directly races `sender.send(true)` against a one-second sleep. Since sends serialize, that bound covers any existing in-flight send as well. Native should cancel/bound the periodic loop wait (without requiring its outstanding transport to settle), then run the bounded final send and release the sender in cleanup. A regression should hold the first send forever and prove close completes within the chosen bound and still releases the held reference/continues cleanup.

Reported to root and `/root/chord_app`; this review did not apply a fix.

Follow-up: the owner replaced both shutdown paths with `WorkerTelemetryPump`. Its periodic send waits through `WaitAsync(stopping.Token)`, and close cancels that wait before bounding the serialized final send; `sender.Stop()` runs in `finally`. Source re-review confirms this addresses the reported unbounded wait. The owner added `AppStartTests.TelemetryShutdown`; runtime validation remains with the serialized validator. The original line references above describe the reviewed defect before that fix.

## Other reviewed areas

- Ingest page/worker limits and scalar/mixed-array shapes agree with upstream batch schemas. Page identity/trace/parent prefixes, interval ordering, stripped `chord.*` attributes, trusted principal stamping, rate receipts, and worker authorization/stamping match the frozen facet behavior.
- Recorder queue/in-flight limits, dropped counters, interval merges, scope identity sharing, stop semantics and serialized sender batches match the source. Worker metric truncation and page metric discard are explicit upstream behavior, not new defects.
- SQLite v2 migrates v1 rows by multiplying and rounding both timestamps inside the existing atomic migration runner. Index replacement follows table replacement. Metrics remain unchanged. Readers require schema 2 without attempting migration; the native file helper opens `ReadOnly`, the adapter uses deferred `BEGIN` for read-only transactions, and read-only close skips WAL checkpoint writes.
- Clock anchor reset tolerance and microsecond rounding match upstream. Mixed string/number values preserve order through JSON and render paths; homogeneous arrays keep their prior representations.

No additional concrete defect was found in those paths. This is a source review, not a claim that the unexecuted regression suites passed.
