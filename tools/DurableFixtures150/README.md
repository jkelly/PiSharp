# Genuine Durable v1.5.0 captures

This is separate from `tools/DurableFixtures`, its historical pins, and its v1.1/v1.2 fixtures. Nothing here transforms an old capture into a new expectation.

```powershell
node tools/DurableFixtures150/capture.mjs E:/AI/PiSharp/upstream-v150 <new-output-directory>
```

The input must be an unmodified tracked checkout at `f9a26365568127461c47a07a37dcb42c140a3ac5`, with its lockfile dependencies already installed. The destination must not exist. The driver does not install packages or access the network. It locally emits telemetry, schema and AI JavaScript using the pinned TypeScript compiler with `--noCheck`: the checked-out AI package imports emitted JS via package subpaths, while optional generated provider catalogs are absent from Git. Only the core and faux provider entry graph is executed. No native addon build was required.

Two independent subprocesses receive an allowlisted environment and execute real upstream code. No provider credentials, `NODE_OPTIONS`, or provider settings are inherited; `fetch` throws. The only registered model provider is `faux` with its API name fixed explicitly. The Harness clock and faux message timestamps are fixed. Real upstream storage implementations write SQLite and JSONL.

Each run retains both backends and eight stored stages:

- `sleeping`: a custom task persisted after `sleepUntil`.
- `early-reopen`: a fresh Harness at one millisecond before the deadline; the after phase must remain unexecuted.
- `due-reopen`: another fresh Harness at the deadline; the after phase completes exactly once.
- `process-exit`: an actual child process exits without closing its Harness while a task is durably running.
- `restart-opened`: reopening that session increments `restarts` to one and reconciles the task to pending.
- `restart-completed`: resuming in another Harness completes the interrupted task exactly once.
- `commit-observers`: two ordered concurrent commits, their document callbacks, stored commit publications, promise resolutions and close notifications.
- `compaction`: three real faux-model turns, a manual compaction task, its placed summary, and a follow-up turn using the compacted context.

`records.json` contains every current record as read back by upstream, including document versions and values. `persisted.json` additionally captures every SQLite table row (JSON record/content columns decoded) or every parsed JSONL line, preserving stored order and historical revisions. `observers.json` and `observed.json` record runtime observations and assertions. These are generated artifacts, never hand-edited.

Only `sessionId` string values are normalized for comparison because upstream generates these provider identities with UUIDv7. Every sequence, state, clock, model response and callback order is retained. Physical SQLite bytes are hashed and retained; its semantic rows rather than page layout are compared across runs. The manifest includes hashes of each raw file, source files, the Node executable, dependency lockfile and capture scripts.

`tests/PiSharp.Durable150.Capture.Tests` opens copies of all thirty-two captured databases/directories using the C# storage implementations and compares the complete current records with the upstream output. It independently verifies raw file hashes, script identity and two-run reproducibility. It does not relabel old authored expectations, claim native re-execution of the whole scenario, or assert cross-runtime observer behavior.
