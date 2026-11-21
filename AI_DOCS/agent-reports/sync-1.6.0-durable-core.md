# Pi 1.6.0 sync: durable-core

Branch `sync/1.6.0-durable-core`, worktree `.claude/worktrees/sync-1.6.0-durable-core`, from `8352a672`. Upstream v1.5.2
`b4afdddf44e059c67ccf5097e5317d913521b470` to v1.6.0 `faac585884ae2c013a6b1400f3e9e77f10b34bac`, `packages/durable/src/harness`,
`src/signals.ts`, `src/session`, `src/types.ts`, `src/index.ts`, `src/testing`.

Suite counts and anything that did not run are in "Suites" below; the numbers there are filled in from the final runs.

## How the TypeScript API maps to C# (decisions another agent needs)

| Upstream | C# | Why |
| --- | --- | --- |
| `execute()` returns `ToolExecutionResult \| ToolPark` | `ToolExecutionResult.Park` (`ToolPark` with `Sleep` or `Wait`); `ToolPark` converts implicitly to a result; `ToolPark.Sleeping(...)`, `ToolPark.Waiting(...)` | C# has no union types. `ToolExecute`'s signature is unchanged, so no tool, wrapper or host needed an edit. A park with a result field, or with both `Sleep` and `Wait`, is refused at run time (`invalid_park`), where upstream's `parkError` checks it too. |
| `BASE_PARKS` symbol on the api, passed on by a wrapper's spread | `ToolExecutionApi.BaseParks`, reached through `DelegatingToolExecutionApi.InnerApi` (`Parks.CarrierOf`) | A wrapper api derives from `DelegatingToolExecutionApi`; an api built from scratch carries none, as upstream's without a spread. Parks are told apart by reference. |
| `cancel(args, api, context)` | `ToolRegistration.Cancel` (`ToolCancel`) | |
| `executeTool(..., { wait: false })` | `IToolExecutionApi.StartToolAsync(...)` returning `NestedToolCall { TaskId, ResultAsync(context) }` | An overload by return type is not possible. |
| `api.createTask(task, input, { ..., key }, context)` | `CreateTaskAsync(task, input, options, key, context)` (new overload) | `TaskOptions` stays the Session's type. |
| `api.wake`, `api.checkpoint`, `readSignal` | `IToolExecutionApi.Wake`, `.Checkpoint`, `.ReadSignalAsync` | |
| `defineSignal`, `SignalToken<T>`, `SignalKey<T>`, `SignalState<T>`, `SignalResult`, `SubmissionSettled` | `Signals.DefineSignal(SignalDefinition)`, `SignalToken`, `SignalKey`, `SignalState`, `SignalResult`, `Signals.SubmissionSettled` | Values are `JsonValue`, so nothing is generic; a schema is a JSON Schema object checked with pi-schema. |
| `harness.signal`, `tx.signal` | `IHarness.SignalAsync`, `ITx.SignalAsync` | See "Deliberate differences" for `ReadAfterWrite`. |
| `TaskState.wake`, `WakeReason` | `PendingTaskState.Wake`, `RunningTaskState.Wake`, `RunningTask.Wake`, `WakeReason` (`Reason`, `Signal`, `Key`, `Ended`) | |
| `{ status: "sleeping", signals, until? }` | `SleepingTaskState(checkpoint, double? until, signals?)`, `NextTaskState.Sleeping(checkpoint, until, signals)` | `Until` is now `double?` on `SleepingTaskState` and `TaskGraphState` (`TaskInspectionState.Until` already was). |
| `policy: "any"` | `JoinPolicy.Any` | |
| `beforeTool` returning `{ sleep }`, `GateSleep` | `BeforeToolDecision(Sleep: new GateSleep(Until, Signals))` | |
| `HookApi.now()`, `HookApi.readSignal()` | `IHookApi.Now()`, `IHookApi.ReadSignalAsync()` | `Now()` moved from `ITaskRuntime` to `IHookApi`, which it inherits. |
| `afterTools` returning `{ tasks }` | `GenerationHooks.AfterTools` returns `Task<IReadOnlyList<HookTask>?>` | As `AfterRun` returns its list. Source-breaking for existing `AfterTools` hooks (they return null). |
| `onYield` returning `{ continue } \| { tasks }` | `GenerationHooks.OnYield` returns `Task<OnYieldResult?>`; `OnYieldResult.Continue(input)`, `.Tasks(list)`; `JsonValue` and `string` convert implicitly | A hook that returned its continuation from an async lambda still compiles; `Task.FromResult<JsonValue?>(...)` must become `Task.FromResult<OnYieldResult?>(...)`. |
| `RunEndTask`, `runEndTask` | `HookTask`, `Define.HookTask` | Renamed, no alias, as upstream. |
| `YieldDecision`, `AfterToolsDecision` | records with `ToJson()` (what a task completes with) | |
| `SubmissionDraft.notify`, `onAbort` | `SubmissionDraft.Input(..., onAbort:, notify:)`, `.Write(..., notify:)`, `.Continue(..., notify:)`; `SubmissionNotify`, `InputOnAbort`; `SubmissionRecord.Notify` | |
| `Extension.namespace`, `ToolNamespace` | `Extension.Namespace`, `ToolNamespace` | Type only, for `durable-edge`; nothing in the Harness reads it. |
| `closeSignal(runtime)`, `providerSessionsByRuntime` | `ISchedulerRuntime` (`CloseSignal`, `UseProviderSession`), implemented by the scheduler's runtime | WeakMaps keyed by the runtime become an internal interface. |

## Upstream changes

Status as in the plan's rule 3. C# paths are under `src/PiSharp.Durable`; tests under `tests/PiSharp.Durable.Tests`.

| # | Upstream change | Status | C# | Cases |
| --- | --- | --- | --- | --- |
| 1 | `signals.ts`: `defineSignal`, delivery results, values kept in the task, `readSignal`, key expiry, `SubmissionSettled` | ported | `Signals.cs` (new), `Session/Transaction.cs` (`SignalAsync`, `StageTask`), `Harness/Harness.cs` (`SignalAsync`), `Harness/Scheduler.cs` (`ReadSignalAsync`), `TypesSession.cs`, `Harness/Types.cs` | `sync160.signals.*` (21) |
| 2 | Sleeps on signals, optional `until`, `policy: "any"`, `state.wake`, parked work inactive in `wake()`, no `at` without `until` | ported | `Types.cs` (`WakeReason`, states, `JoinPolicy.Any`), `Tasks.cs`, `Harness/Scheduler.cs` (`Asleep`, `WokeLocked`, `Reserved`, `PendingAgain`, `WaitingOnLocked`, `InactiveLocked`), `Harness/TaskGraph.cs` | `sync160.signals.*`, `harness.idle-scopes.kept-context-ranges.count-a-conversation-whose-live-work-is-all-parked-...` |
| 3 | Tools that park: `ToolPark`, `parks.ts`, the `parked` phase, `api.wake`/`checkpoint`, slots `sleeping`/`waiting`, `invalid_park`, `cancel`, wrappers never park, `executeTool` `wait: false`, `createTask` with a key, kept `abandonOnRestart` children | ported | `Harness/Parks.cs` (new), `Harness/Tool.cs`, `Harness/Types.cs`, `Harness/Agent.cs` (`Parks.WrapComposite`), `Harness/Live.cs` (`ClearOutput`), `Harness/Scheduler.cs` (`KeepsAbandoned`) | `sync160.tool-parks.*` (18), `sync160.park-children.*` (10) |
| 4 | `SubmissionDraft.notify` delivering `SubmissionSettled` in the settling commit; `onAbort: "keep"` | ported | `Session/Transaction.cs` (`NotifyAsync`), `Harness/Submissions.cs`, `Harness/Inbox.cs` (`WithdrawAbortedInputsAsync`), `Types.cs` (`SubmissionNotify`), `Harness/Types.cs` | `sync160.park-children.submissions-that-notify-a-task.*` (5), `harness.input-entries.keeps-onabort-keep-inputs-...` |
| 5 | Gates: `beforeTool` may sleep, the `call` phase runs again on every wake, registry publication and open; `HookApi.now()` | ported | `Harness/Tool.cs` (`ParkAtGateAsync`, `GatedCall`), `Harness/Scheduler.cs` (`gateWakes`, `WakeGateLocked`, publication count) | `sync160.tool-gates.*` (9) |
| 6 | Hooks that start tasks: `afterTools` and `onYield` `{ tasks }`, `AfterToolsDecision`, `YieldDecision`, `pi.live.run.yield`, `HookTask`/`hookTask` | ported | `Harness/Generation.cs` (`AfterToolsAsync`, `YieldAsync`, `ConcludeAnswerAsync`, `EndToolRoundAsync`, `PrepareHookTasks`), `Harness/Live.cs`, `Harness/Define.cs`, `Harness/Types.cs` | `sync160.hook-tasks.*` (12), `harness.after-run.*` (renamed type and messages) |
| 7 | `durationMs` is the span from the task's first run; results that never executed have one; old measure without `startedAt` | ported | `Harness/Tool.cs` (`SettleAsync`) | `harness.tools.records-each-result-s-span-...` (replaces the v1.5.2 case), `harness.nested-tools.shows-a-finished-nested-call-...`, `sync160.tool-parks.*`, compat via `Durations160.cs` |
| 8 | `pi.tool` version 4, `pi.generation` version 3 | ported | `Harness/Tool.cs`, `Harness/Generation.cs` | `harness.nested-tools.version-1-and-2.*`, `harness.tool-hooks.calls-to-tools-the-request-did-not-offer.keep-their-flag-...`, `compat.upgrade-from-v1.5.2.*` (26, new), every `compat.*` and differential case through `ToolTasks150.cs` |
| 9a | `Submission.wait()`/`waitForTask()` unhandled rejection (#11963, #11820) | ported | `Harness/Util.cs` (`Waiters.Add` observes the waiter's fault at creation) | `harness.submissions.reports-no-unhandled-rejection-...` (see "Weak evidence") |
| 9b | Idle-scope walk O(n^2) in a long ownership chain | ported | `Harness/Scheduler.cs` (`AddScopesAboveLocked`; replaces PiSharp's earlier `walked` set) | `harness.ownership.tracks-ownership-chains-deeper-than-the-call-stack`, `harness.idle-scopes.*`, `harness.scheduler-indexes.*` (existing) |
| 10 | `testing/storage-conformance.ts`: "round-trips sleeps on signals, any waits, and wakes" | ported | `Testing/StorageConformance.cs` | runs on every store: `storage.memory.*`, `storage.jsonl.*`, `storage.sqlite.*`, `storage.mssql.*` (needs a server), `store.*`. No schema or query change was needed: every store keeps a task's state as JSON and indexes only its status. |
| - | `harness.ts` close releases provider session resources (#11676); `provider.ts` records the session IDs | ported | `Harness/Harness.cs` (`BeforeCloseAsync`), `Harness/Provider.cs`, `Harness/Scheduler.cs` (`TakeProviderSessions`) over `PiSharp.AI.SessionResources.Cleanup` | `harness.lifecycle.cleans-up-provider-session-resources-*` (3 per backend) |
| - | `inbox.ts` `onAbort` item field, `withdrawAbortedInputs` | ported | `Harness/Inbox.cs` | as row 4 |
| - | `live.ts` slot statuses, `clearOutput`, `run.yield`, `settleSchedulerOutcome` for a yielding run | ported | `Harness/Live.cs` | `sync160.hook-tasks.recovery.*`, `sync160.tool-parks.*` |
| - | `task-graph.ts` sleeping state with optional `until` and `signals` | ported | `Harness/TaskGraph.cs` | `sync160.signals.sleeping.holds-no-invocation-...` |
| - | `types.ts` (harness): `HarnessSettings.contextRetentionMs` doc ("inactive") | no-op | doc wording only | |
| - | `define.ts` `defineTool` third type parameter | no-op | TypeScript typing only | |
| - | `index.ts` exports | no-op | C# has no barrel file; the new types are public in `PiSharp.Durable` | |
| - | `src/codemode/**`, `src/images/**`, `src/tools/read.ts`, `src/tools/image-processor.ts`, `src/env/node.ts`, `test/examples/38-47` | not in this package | `durable-edge` | |

## Upstream test files

| Upstream test | Cases | Ported | C# file | Not ported |
| --- | --- | --- | --- | --- |
| `harness-signals.test.ts` | 21 | 21 | `Sync160SignalsCases.cs` | The NaN delivery assertion inside "answers not_found, invalid, ...": a `JsonValue` cannot hold NaN. "reads the task, so it throws ReadAfterWrite ..." is adapted (below). |
| `harness-tool-parks.test.ts` | 18 | 18 | `Sync160ToolParksCases.cs` | none. The two "process dies" cases crash the first Harness (`Session.Fail`) instead of leaving it open beside a second one. |
| `harness-park-children.test.ts` | 10 | 10 | `Sync160ParkChildrenCases.cs` | none. One extra barrier (both wakes recorded) in the first case, and the same crash idiom. |
| `harness-tool-gates.test.ts` | 9 | 9 | `Sync160ToolGatesCases.cs` | none |
| `harness-hook-tasks.test.ts` | 12 | 12 | `Sync160HookTasksCases.cs` | none. The afterTools crash case kills its storage (`KilledStorages`) instead of leaving the first Harness open. |
| `harness-lifecycle.test.ts` (+3) | 3 | 3 | `Harness/HarnessLifecycleCases.cs` | none |
| `harness-idle-scopes.test.ts` (+1) | 1 | 1 | `HarnessIdleScopesCases.cs` | none |
| `harness-input-entries.test.ts` (+1) | 1 | 1 | `HarnessInputEntriesCases.cs` | none |
| `harness-submissions.test.ts` (+1) | 1 | 1 | `Harness/HarnessSubmissionsCases.cs` | none |
| `harness-after-run.test.ts` (renames) | - | - | `HarnessAfterRunCases.cs` | `HookTask`, and the rejection messages "A hook cannot start the built-in task ..." |
| `harness-nested-tools.test.ts` (changed expectations) | - | - | `HarnessNestedToolsCases.cs` | version 4, and a `durationMs` in the summary of a call that never executed; one case id renamed (`...-under-version-4-...`) |
| `harness-tool-hooks.test.ts` (changed expectations) | - | - | `HarnessToolHooksCases.cs` | generation version 3 |
| `harness-tools.test.ts` (changed case) | 1 | 1 | `Harness/HarnessToolsCases.cs` | "records each result's span from the call's first run, hooks included, also for calls that did not execute" replaces the v1.5.2 case (case id renamed) |
| `spec-usage.test.ts` (new examples) | - | all | `SpecUsageCases.cs` | compile-only, as before |

Expectations changed because upstream changed the behavior (rule 4): the four rows above, `ExamplesCases.cs` (example 12 prints the saved
state with `"wake":{"reason":"time"}`), and the compat and differential cases, which compare with authored v1.6.0 forms of the v1.2.0
captures: `ToolTasks150.cs` now authors `pi.tool` 4 and `pi.generation` 3, and `Durations160.cs` (new) authors a `durationMs` on every
result a tool task writes and `kind: "nested"` on the nested call documents a run or resume creates.

## Deliberate differences from upstream

1. `tx.signal()` and `ReadAfterWrite`. Upstream throws after any table write, a signal's own included, and delivers several signals in one
   commit with `Promise.all`, where every call passes the check before the first one's await resumes. A .NET task can complete
   synchronously, so the first of `Task.WhenAll`'s deliveries may have staged its write before the second call is made. Here a delivery
   passes after the writes of earlier deliveries of the same transaction and throws after any other table write
   (`Session/Transaction.cs`). The upstream case is adapted to assert that.
2. Registry publication count. A reservation reads the count under the scheduler lock and takes the snapshot right after, outside it
   (the registry is host code). A publication between the two gives a gated call one extra pass over its gates, never a missed one.
3. The intent commits keep PiSharp's existing order (state first, slot second), also in the new `parked` phase.
4. A woken record gets `memos: {}` when it had none, as upstream's spread writes it.

## Weak evidence

- `harness.submissions.reports-no-unhandled-rejection-...` is a faithful port, but in .NET the cancelled wait's task is awaited once
  the line delivers it, so the case may pass without the `Waiters` change. The change is still the right port: a waiter whose caller
  never gets the task (its own line read threw first) would otherwise reach the finalizer unobserved.
- "A record written before `startedAt` existed keeps the old measure" has no case: a reservation stamps `startedAt` on such a record
  (upstream's `#stampTimes` does too), so the old measure is reached only by a record that settles without being reserved again.
- `storage.mssql.*` and the SQL Server upgrade cases skip without `PISHARP_DURABLE_MSSQL_CONNECTION_STRING`.

## Edits outside the owned paths

- `samples/Durable.Examples/ExamplesAgents.cs`: the `OnYield` hook returns `Task.FromResult<OnYieldResult?>(...)`.
- `samples/Durable.Examples/ExamplesTestsAfterEdits.cs`: `Define.RunEndTask` is `Define.HookTask`.
- `tests/PiSharp.DurableCli.Tests/TuiSupport.cs`: its `State(...)` helper builds a `TaskGraphState` through the private constructor by
  reflection and now passes the new seventh argument (`signals`, null).

Nothing else needed an edit: `PiSharp.Durable.Tools`, `.Hosting`, `.Server`, `.Client`, `.Web`, `PiSharp.CustomAgents*`, `PiSharp.WebAgent`,
`PiSharp.Cli` and `PiSharp.Chord*` compile unchanged, because a park is carried by `ToolExecutionResult` and the new interface members
have implementations in `ToolExecutionApi` and `DelegatingToolExecutionApi` (every other implementer derives from the latter or is a
`DispatchProxy`).

## What `durable-edge` and the hosts still need

- `PiSharp.Durable.Hosting/AgentEvents.cs` projects a slot turning `running` as `tool_execution_start`. A parked call's slot goes
  `running`, `sleeping` or `waiting`, then `running` again, so it would project a second start; decide what the events show for a park.
- Slot statuses `sleeping` and `waiting`, `pi.live.run.yield`, a sleeping task without `until`, and `signals` in the task graph are new
  values for `PiSharp.Durable.Web` (`Hub/Display.cs`), `PiSharp.Durable.Client` (`Models.cs` reads `Status == "running"`),
  `PiSharp.Durable.Server` and `PiSharp.WebAgent` to display. `PiSharp.Durable.Hosting/ExecutionHistory.cs` already accepts both statuses.
- No host exposes `IHarness.SignalAsync` (an HTTP or hub endpoint to deliver a signal), `SubmissionDraft.Notify` or `OnAbort` yet.
- `Extension.Namespace` is declared but unused: code mode's namespaces (`src/codemode/**`) are `durable-edge`'s.
- Examples 38 to 46 (`samples/Durable.Examples`) exercise this package's API and are `durable-edge`'s.
- `PiSharp.CustomAgents` workflows sleep with `NextTaskState.Sleeping(checkpoint, wakeAt)`; they could wait on signals instead of polling.

## What root must do

- `compatibility/durable-port-map.json` and the 1.6.0 overlay: new upstream files `src/signals.ts`, `src/harness/parks.ts`, and tests
  `harness-signals`, `harness-tool-parks`, `harness-park-children`, `harness-tool-gates`, `harness-hook-tasks` map to the C# files above.
- `DurableUpstream.cs` still names `RunEndTask` in a comment and the v1.5.x pin; `tests/PiSharp.Durable.Tests/Program.cs`'s report label
  reads `DurableUpstream.Commit`. Both are `meta`'s.
- Changelog: the breaking changes for C# users are `RunEndTask` to `HookTask`, `GenerationHooks.AfterTools` and `OnYield` return types,
  `SleepingTaskState.Until` and `TaskGraphState.Until` nullable, `ITaskRuntime.Now()` now declared on `IHookApi`,
  `Inbox.WithdrawQueuedInputsAsync` without a default reason, new members on `IToolExecutionApi`, `IHookApi`, `ITx` and `IHarness`, and
  the record versions (a database written by this version is not meant to be opened by PiSharp 1.5.2).

## Commits

See `git log 8352a672..sync/1.6.0-durable-core`. The source port is one commit: the scheduler, tool and type changes of items 1 to 9
depend on each other and do not build apart. The tests follow in one commit per upstream file group.

## Suites

Pinned SDK (artifacts/sample-tools/dotnet), Release, on Windows, at the branch head. No live provider, no SQL Server.

| Suite | Cases | Passed | Failed | Skipped | Notes |
| --- | --- | --- | --- | --- | --- |
| PiSharp.Durable.Tests (full) | 4821 | 4817 | 0 | 4 | 4692 authored, 125 upstream capture. Skips, all for the environment and none compared with the base: compat.reverse.upstream-reads-pisharp-sessions (PISHARP_DURABLE_UPSTREAM unset; the upstream checkout has no node_modules) and three plan.symlink-escape.* (no symlink privilege). |
| PiSharp.Durable.Hosting.Tests | 129 | 129 | 0 | 0 | |
| PiSharp.Durable.Server.Tests | 31 | 31 | 0 | 0 | |
| PiSharp.DurableCli.Tests | 119 | 119 | 0 | 0 | After the one-line edit of TuiSupport.cs (see Edits outside the owned paths); before it tui.command.tasks and tui.state.describe-task failed on this branch only. |
| PiSharp.CustomAgents.Tests | 104 | 104 | 0 | 0 | |
| PiSharp.Chord.Durable.Integration.Tests | 7 | 7 | 0 | 0 | |
| PiSharp.Durable150.Capture.Tests | 35 | 35 | 0 | 0 | The v1.5.0 captures still read unchanged. |
| PiSharp.Durable.Web.Tests | 17 | 17 | 0 | 0 | |

The first full run of PiSharp.Durable.Tests after the source port, before any test was changed, had 4699 cases and 114 failures, all of
them expectations upstream changed (task versions, durations, the example's printed state) or their authored forms over the v1.2.0
captures; none was a regression. They are the rows of "Upstream test files" and the paragraph under it.

Builds: src/PiSharp.Cli, src/PiSharp.Durable.Web, src/PiSharp.WebAgent, src/PiSharp.CustomAgents, src/PiSharp.Chord.Hosting and the test
projects PiSharp.Durable.Tests, .Durable.Hosting.Tests, .Durable.Server.Tests, .DurableCli.Tests, .CustomAgents.Tests,
.Chord.Durable.Integration.Tests, .Durable.Web.Tests, .WebAgent.Tests, .Chord.Hosting.Tests, .DurableMessages.Tests and
.Durable150.Capture.Tests build with 0 errors.

Not run: the SQL Server-backed groups (storage.mssql.conformance.*, session.mssql.*, harness.*.mssql.*, the mssql upgrade cases), which
are not enumerated without PISHARP_DURABLE_MSSQL_CONNECTION_STRING, so the new conformance case did not run against SQL Server; the 20
storage.mssql.offline.* cases ran and passed. PiSharp.Durable.Web.BrowserTests and PiSharp.WebAgent.BrowserTests were not built or run.
PiSharp.WebAgent.Tests, PiSharp.DurableMessages.Tests and PiSharp.Chord.Hosting.Tests were built; their runs were not finished when
this report was written.
