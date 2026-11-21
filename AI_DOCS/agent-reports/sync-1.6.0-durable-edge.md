# Pi 1.6.0 sync: durable-edge

Branch `sync/1.6.0-durable-edge`, worktree `.claude/worktrees/sync-1.6.0-durable-edge`, from `1753dfae` (the merge of
`sync/1.6.0-durable-core`). Upstream v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470` to v1.6.0
`faac585884ae2c013a6b1400f3e9e77f10b34bac`, `packages/durable/src/codemode`, `src/images`, `src/tools`, `src/env`, `test/examples` and the
tests of those areas; and PiSharp's own hosts built on Durable.

Nothing was pushed, tagged or published. No live provider was called. Suite counts and what did not run are in "Suites".

## Upstream changes

Status as in the plan's rule 3. Tests are under `tests/PiSharp.Durable.Tests` unless a suite is named.

| # | Upstream change | Status | C# | Cases |
| --- | --- | --- | --- | --- |
| 1 | `codemode/describe.ts`, `codemode.ts`, `index.ts`: namespaces from `Extension.namespace`; `CodemodeNamespace`, `prefix`, `namespaceOf`, `CodemodeToolOptions.namespaces` removed with no fallback; the section lists every namespace of a selected extension, also one without tools, without counts; `namespacesOf` exported | ported | `src/PiSharp.Durable.Tools/Codemode/Describe.cs` (`NamespacesOf`, `DeclaredNamespaces`, `Section`), `Codemode.cs` (`DurableCodemode.NamespacesOf`, start data from `agent.Extensions`) | `codemode.describe.*` (3 rewritten, 1 removed with the API), `sync160.codemode.*` (3 upstream, 3 authored), `codemode.output.finds-tools-with-searchtools-...`, `search.code-mode.search-tools-searches-namespace-instructions-...` |
| 1 | `codemode/output.ts`: `saveImages` uses `fromBase64` | no-op | the same decode moved into a helper; `Output.cs` keeps `Convert.FromBase64String` | existing `codemode.output.*` |
| 2 | `images/fit.ts`: `fitImage`, `fitImages`, `createFitImagesExtension` | ported | `src/PiSharp.Durable.Tools/Images/Fit.cs` (`FitImages.FitImageAsync`, `FitAsync`, `CreateExtension`, `FittedImage`, `FittedImages`) | `sync160.images-fit.*` (11 upstream, 3 authored) |
| 2 | `tools/image-processor.ts`: `imageLimits(model)`, `preparedNotes`, `fromBase64`; `tools/index.ts` exports `imageLimits` | ported | `Tools/ImageProcessor.cs` (`ImageLimits.Of`, `ImageFormats.PreparedNotes`, `ImageFormats.FromBase64`), `Tools/Tools.cs` (`CodingTools.ImageLimitsOf`) | `sync160.images-fit.from-base64-decodes-as-atob-does`, `...image-limits-of-a-model-default-each-absent-limit`, `tools.images.*` |
| 3 | `tools/read.ts`: no diagnostic that the model takes no images; `details.imageUnsupportedByModel` (#12131) | ported | `Tools/Read.cs` (`ReadTool.ImageUnsupportedByModelProperty`) | `tools.images.without-processor.names-a-text-only-model-in-details-for-uis-not-in-what-the-model-gets` (replaces the v1.5.2 case) |
| 4 | `env/node.ts`: `listDir()` leaves out an entry removed between enumeration and `lstat`, and names that are not valid UTF-8 (#11994) | ported | `src/PiSharp.Durable/Env/Local/LocalFileSystem.cs` (`ListDirAsync`, the internal seam `StatListed`) | `sync160.env-list-race.*` (1 upstream, 3 authored) |
| 5 | `test/examples/38-signals.ts` to `47-fit-images.ts`; `32-tests-after-edits.ts` (`hookTask`) | ported (32 by durable-core) | `samples/Durable.Examples/ExamplesSignals.cs` (38, 39), `ExamplesToolParks.cs` (40 to 43), `ExamplesGates.cs` (44 to 46), `ExamplesFitImages.cs` (47), `Examples.cs`, `ScriptedModels.cs` (`CallCount`, `Model` settable) | `examples.memory.upstream-output`, `examples.jsonl.reopen`, `examples.sqlite.reopen` (lines authored, see below) |
| 6 | `test/codemode-describe.test.ts`, `codemode.test.ts`, `images-fit.test.ts`, `image-fixtures.ts`, `tools-images.test.ts`, `tools.test.ts`, `env-node-list-race.test.ts` | ported | see "Upstream test files" | |
| 6 | `test/spec-usage.test.ts` additions that depend on items 1 and 2 | no-op | the v1.6.0 additions to that file use the Harness API only (signals, parks, hook tasks), which durable-core ported in `SpecUsageCases.cs`; none names a namespace, `fitImage` or `imageLimits` | |
| - | `src/index.ts` exports | no-op | C# has no barrel file; `DurableUpstream.cs`'s comment now lists the v1.6.0 names | |

## Upstream test files

| Upstream test | Cases | Ported | C# file | Not ported, or changed |
| --- | --- | --- | --- | --- |
| `codemode-describe.test.ts` | 7 | 7 | `CodemodeDescribeCases.cs` (4: one unchanged, three under their new upstream names), `Sync160CodemodeCases.cs` (3 added upstream) | `uses the longest matching namespace prefix` is deleted, as upstream deleted it with the prefix API. Three authored cases in `Sync160CodemodeCases.cs`. |
| `codemode.test.ts` (1 case changed) | 1 | 1 | `CodemodeCases.cs` | the case now installs two extensions with namespaces, one without tools; `Chat` no longer takes namespaces |
| `images-fit.test.ts` | 11 | 11 | `Sync160ImagesFitCases.cs` | none. Photon is `SkiaImageCodec`, as in `ToolsImagesCases.cs`. |
| `image-fixtures.ts` | - | - | `ToolsImagesCases.cs` | the fixtures stay private members of the suite's partial `Program`, which the fit cases share |
| `tools-images.test.ts` (-134/+26) | 1 changed | 1 | `ToolsImagesCases.cs` | the fixtures moved out upstream (no native change); the stand-in model gained `id: "test"` (it had one); `says when the model sees a placeholder ...` became `names a text-only model in details for UIs ...` (case id renamed) |
| `tools.test.ts` | 0 | - | `ToolsCases.cs` | a TypeScript cast (`as ToolExecutionResult`): no native change |
| `env-node-list-race.test.ts` | 1 | 1 | `Sync160EnvListRaceCases.cs` | upstream mocks `lstat`; here `LocalFileSystem.StatListed` is overridden |

Expectations changed because upstream changed the behavior (rule 4): `codemode.describe.*` (three cases rewritten, one deleted),
`codemode.output.finds-tools-...`, `tools.images.without-processor.says-when-...` (renamed), and
`search.code-mode.search-tools-searches-namespace-instructions-...` (authored; its namespaces are now declared by extensions, same
expected result).

## Examples 38 to 47

The upstream checkout has no `node_modules`, so no example was run under Node. Each example's lines are authored from its source
(`console.log`'s arguments joined with spaces; a value that is not a string as JSON in place of `util.inspect`), as the v1.5.0 examples'
were, and each was checked against the C# run. Three values are placeholders in the comparison: the model's tool call IDs (random on both
sides), 40's "wake while parked" deadline (the system clock plus two hours), and 41's count of `onWake` calls (13 here; it depends on how
many wake values the scheduler reports between the host's reads).

PiSharp-only parts, each named in the sample's header:

- Upstream fixes the order of a task's or tool's line and the host's by Node's single thread, and in 45 and 46 by a 5 ms wait that lets
  the next task start. Here `ExampleTurn` holds a line until the host printed its own, and the hosts wait for the state they print. In
  38 the first greeter starts its work once the host made its four deliveries, so `retry` finds the task alive; upstream's task may have
  printed its line earlier among those four.
- 40: upstream leaves the first Harness open while a second opens the same SQLite file. Here the first is closed while the call is cut
  off after it triggered the build, as 12-recovery does. On memory storage it uses a temporary SQLite file, as upstream always does.
- 47 uses `BuiltinImageCodec` (PNG in, PNG out), so the sample takes no SkiaSharp dependency; a host with JPEG or WebP input passes
  `SkiaImageCodec`.
- On a file backend every example reads its storage back in a plain Session (the tasks' outcomes for 38 and 39, the transcript's kinds
  for the rest).

## Hosts: what a parked call and a yielding run show

Agent events (`src/PiSharp.Durable.Hosting/AgentEvents.cs`, PiSharp's projection of v1.2.0's events; every change is additive):

1. A slot is live while it is `running`, `sleeping` or `waiting`. A call has one `tool_execution_start`: when its slot first becomes
   live. A parked slot that turns `running` again is never a second start, whenever the stream attached, because the rule reads the
   slot's previous status and a snapshot lists the slot.
2. A park is a `tool_execution_update` with `status: "sleeping"` or `"waiting"`, and `output: {set: ""}` when the park cleared a window
   that held text. Its wake is a `tool_execution_update` with `status: "running"` and, when the stream saw the intent the woken call
   recorded, `args`.
3. A call parked at its gates before it ran (a `beforeTool` sleep) is live, so it has its start, with `status: "sleeping"` and
   `args: {}` (it has no intent yet); the update of its wake carries the arguments it runs with, the gates' replacement included.
4. A run whose answer waits for its `onYield` tasks keeps its inputs, so it stays the run on the wire: no `run_end` until the answer
   stands. The snapshot's `run` carries `yield: {answer}` meanwhile, and a new event `run_yield` says when that begins
   (`{inputs, answer}`) and when the tasks continued the run (`{inputs}`). An `afterTools` task needs no event: the run ends, or goes on
   with the steer.
5. Unchanged for a call that never parks: `tool_execution_start` has no `status`, updates have no `status` or `args`
   (`sync160.agent-events.tool-events-without-parks-are-as-before`). An older client reads a parked call as running, since it ignores
   the new field, and ignores `run_yield`.

Why a status on `tool_execution_update` and not a new event pair: every client that follows v1.2.0's events already creates or updates a
tool row on an update, so a park costs old clients nothing, and one start per call stays true for them.

Client models (`src/PiSharp.Durable.Client`): `AgentModel` takes `status` from a start and an update (an update without it no longer
resets a tool to `running`), `args` from a wake, and exposes `YieldAnswer`. `ToolStatus` gains `Sleeping` and `Waiting`;
`Transcript.StatusText` and the JSON transcript say `sleeping` and `waiting`; `Transcript.LiveStatus` is new. `TaskNodeState` gains
`Until` (absent for a sleep on signals only) and `Signals` (`TaskSignalKey`), which `DeviceService.LiveTasksAsync` fills from the task
graph.

Displays: the web hub's tool card shows a pause icon with `sleeping` or `waiting`, and its task tree says "Waiting for a signal" with
the keys and the deadline in the chip's tooltip (`Display.TaskStatus`, `Display.TaskWaitsFor`). The Web Agent's tool card shows a
`sleeping` or `waiting` chip instead of the spinner. `pisharp durable attach` prints one line when a call parks. The durable TUI's
status line says `<tool> is sleeping...` or `waiting...` when a call is parked and nothing runs, and its task panel names the signal
keys of a sleep (`DurableTuiState.DescribeTask`; upstream's line is unchanged for a sleep until a time). A parked nested call stays a
`running` row in pi's codemode row shape, which has no parked status.

Not carried anywhere, by design: the wake reason. Upstream's task graph has no wake reason either (`TaskGraphState` is
`{status, phase, until?, signals?}`); it is on the task record (`PendingTaskState.Wake`, `RunningTaskState.Wake`), which hosts that
serialize records with `ToJson()` already pass through.

Cases: `sync160.agent-events.*` (6, in `PiSharp.Durable.Tests`), `sync160.park-model.*` (5, `PiSharp.Durable.Server.Tests`),
`sync160.tui.*` (3, `PiSharp.DurableCli.Tests`). `Display.TaskStatus` and `TaskWaitsFor` have no unit case: `PiSharp.Durable.Web.Tests`
runs the hub as a child process, and no hub job sleeps on a signal yet.

## Web Agent notes (item 8)

- `Runs/RunModelOptions.cs`: max tokens on Codex is sent. `PiSharp.AI`'s Codex transport sends it as `max_output_tokens` through the
  router's capped route, which `runs.engine.codex-takes-its-service-tier-and-the-patched-options` now checks on the wire (1,000 in the
  body, with the tier and the prompt cache key kept). Two refusals remain, both the transport's own limits: a cap below 16 (the
  transport raises it to 16, so it would not be the whole output the Spend Guard reserved) and a model whose compat says
  `supportsMaxOutputTokens: false`. `runs.options.refuses-what-the-api-would-reject-or-not-send` asserts the new behavior in place of
  the old refusal.
- `Bench/BenchStrict.cs`: its walk did not know the 1.6.0 rule. It now lists a node with no `type`, no `anyOf` and no `enum`
  ("add a type ..."); a bare `enum` and an `anyOf` count as typed, a bare `const` does not, as in `constrained-sampling.ts`. A `$ref`
  node is left alone: the bench sends the user's schema as it is and the provider resolves the reference, whereas pi-ai's strict
  projection (which the bench does not use) refuses `$ref` altogether. Case `bench.strict.lists-a-schema-node-without-a-type`.

## Port bookkeeping (item 9)

- `tools/DurablePortMap/sync-1.6.0-native-map.json` (new data): the native source and test locations of the 21 Durable files the range
  added (durable-core's `signals.ts`, `harness/parks.ts` and five test files; this package's `images/fit.ts`, three test files and ten
  examples) and of four changed files whose port spans more than the historical location (`tools/image-processor.ts`,
  `codemode/describe.ts`, `env/node.ts`, `codemode-describe.test.ts`).
- `compatibility/durable-sync-1.6.0-overlay.json`: 25 rows carry that entry as `nativeMapping`. The overlay is generated, and
  `sync-1.6.0-applicability.test.mjs` requires the pending snapshot to reproduce, so the generator copies the entries in (see "Edits
  outside the owned paths"). `node tools/CompatibilityReport/sync-1.6.0-applicability.mjs --check` passes; the table and the map's
  candidate pointer are unchanged.
- `src/PiSharp.Durable/DurableUpstream.cs`: the comment names `Define.HookTask` and `HookTask`, and lists what v1.6.0 adds. Its
  constants are unchanged; `setup.port-map-follows-the-pinned-commit` passes. New case
  `setup.sync160-native-map-names-the-new-durable-files`.

## Breaking native API changes (for the release notes)

- `PiSharp.Durable.Tools`: `CodemodeNamespace` and `CodemodeToolOptions.Namespaces` are removed, with no fallback. Declare
  `Extension.Namespace = new ToolNamespace(name, description, instructions?)` on the extension that brings the tools. A tool whose name
  starts with a server prefix belongs to no namespace unless its extension declares one. The `codemode` section's namespace headings
  no longer say "(tools not listed)" or "(some tools not listed)"; one line under the section's first says some tools are not listed.
- `PiSharp.Durable.Tools`: the durable `read` tool no longer adds "The current model does not support images; it sees a placeholder
  instead." to its `image` diagnostic. A host that showed that text reads `details.imageUnsupportedByModel` instead.
- `PiSharp.Durable.Client`: `ToolStatus` gains `Sleeping` and `Waiting` (a `switch` over it without a default no longer covers every
  value); `Transcript.StatusText` may return `sleeping` or `waiting`; `TaskNodeState` gains `Until` and `Signals`.
- `PiSharp.Durable.Hosting` agent events (wire, additive): `tool_execution_start` may carry `status`, `tool_execution_update` may carry
  `status` and `args` and is now also sent for a parked call, the snapshot's `run` may carry `yield`, and `run_yield` is a new event
  type. A consumer that treated every `tool_execution_update` as "the call runs" reads a parked call as running.
- Additions, not breaking: `FitImages`, `FittedImage`, `FittedImages`, `ImageLimits.Of`, `CodingTools.ImageLimitsOf`,
  `DurableCodemode.NamespacesOf`, `AgentModel.YieldAnswer`, `Transcript.LiveStatus`, `TaskSignalKey`, `RunYieldEvent`.
- `PiSharp.WebAgent` behavior: a run with max tokens on a Codex model is sent instead of refused.

## Edits outside the owned paths

- `tools/CompatibilityReport/sync-1.6.0-applicability.mjs` (root's): 7 lines. It reads
  `tools/DurablePortMap/sync-1.6.0-native-map.json` when the file exists and adds each entry to the overlay row of the same upstream
  file as `nativeMapping`. Without it a hand-edited overlay fails `the pending 1.6.0 snapshot reproduces from its generator`. The
  table and the port map it writes are byte-identical to before.
- `src/PiSharp.Durable/DurableUpstream.cs` (durable-core's): comment only, as the brief asked.
- `tests/PiSharp.Durable.Tests/Harness/ChatSupport.cs` (shared test support): `FauxModelOptions` gains an optional `InputLimits`.
- `tests/PiSharp.DurableCli.Tests/TuiSupport.cs`: `State(...)` gains an optional `signals` argument.
- No file under `src/PiSharp.Durable/Harness`, `Signals.cs` or `Session` changed. No defect was found in the Harness.

## Follow-ups not done

- No host delivers a signal: no HTTP or hub endpoint calls `IHarness.SignalAsync`, and nothing sets `SubmissionDraft.Notify` or
  `OnAbort: keep`. Until one does, a call parked on a signal in a hosted session can be woken only by code in the same process (an
  extension's own task or tool), by its deadline, or by an abort.
- No host installs `FitImages.CreateExtension`. The device host and the Web Agent store the images tools return as they are; installing
  the extension with `ImageProcessors.CreateThreaded(SkiaImageCodec.Instance)` is a few lines per host, but it changes what is stored,
  so it is a product decision.
- No host shows `imageUnsupportedByModel` from the durable `read` (the coding agent's own renderer does for its read). The durable
  TUI, the hub and the Web Agent render the image result as before.
- `PiSharp.CustomAgents` workflows still sleep until a time and poll; they could sleep on signals.
- The web hub and the Web Agent do not show `AgentModel.YieldAnswer` ("answer under review"); the value is in the model.
- `src/PiSharp.Durable.Web/wwwroot/js/automation.js` `isLive` lists `pending, running, waiting, completing` but not `sleeping`, which
  executions have had since 1.5.0: a sleeping execution's audit label reads "Unaudited" instead of "Pending". Not a 1.6.0 change; left
  as it is.
- Examples 38 to 47 were not run under Node (see above).

## What root must do

- `compatibility/sync-1.5.0-applicability.json`, row `durable.unoffered-tool-calls`: it names the case
  `tests/PiSharp.Durable.Tests/HarnessNestedToolsCases.cs: reserves-a-stored-version-2-task-under-version-3-and-runs-it-as-a-model-issued-call`,
  which durable-core renamed `...-under-version-4-...`. `node --test tools/CompatibilityReport/sync-applicability.test.mjs` fails on it
  ("statuses, phases, releases and PiSharp paths"; 3 passed, 1 failed, 1 skipped). The table and that test file are unchanged from the
  base commit `1753dfae`, which already holds the renamed case, so the failure is the base's; I did not run the test on a base
  checkout. The row needs a `currentImplementation.testReplacements` entry. A scan of every test reference in that table found no
  other stale one: none of the cases this package renamed or removed is named there.
- `compatibility/durable-port-map.json` and `docs/decisions/0005-pi-durable-experimental.md` still mention `CodemodeNamespace` in notes.
- `website/src/content/docs/docs/embed/durable.md` lists the examples up to `34-submit-in-commit`; 38 to 47 are new rows. The website
  and `docs/` also need the code-mode namespace migration, image fitting and the `read` detail.
- Changelog: the section above.
- Review the 7-line generator edit, or replace it with root's own way to carry `nativeMapping`.

## Commits

`git log 1753dfae..sync/1.6.0-durable-edge`:

| Commit | Item | Subject |
| --- | --- | --- |
| `af409d99` | 1 | Take code-mode namespaces from extensions, not name prefixes |
| `266abea2` | 2 | Add image fitting for Durable conversations |
| `7d6393cc` | 3 | Name a text-only model in the durable read tool's details, not in a diagnostic |
| `eacd9caa` | 4 | Leave out an entry removed during a local directory listing |
| `bfc6a452` | 5 | Port Durable examples 38 to 47 |
| `eff3e8dc` | 8 | Web Agent: send max tokens to Codex and list untyped schema nodes for Strict |
| `3b6797a8` | 7 | Show parked tool calls and yielding runs in the Durable hosts |
| `48fbc5c4` | 9 | Record the native locations of the Pi Durable 1.6.0 files |

Item 6's cases are in the commits of the items they test.

## Suites

Pinned SDK (`artifacts/sample-tools/dotnet`), Release, on Windows, at `48fbc5c4` (the last source commit; the commits after it change
this report only). No live provider, no SQL Server. Each suite ran alone, after every build had finished.

| Suite | Cases | Passed | Failed | Skipped | Notes |
| --- | --- | --- | --- | --- | --- |
| PiSharp.Durable.Tests (full) | 4851 | 4847 | 0 | 4 | 4722 authored, 125 upstream capture. Base: 4821. +31 new (`sync160.codemode` 6, `sync160.images-fit` 14, `sync160.env-list-race` 4, `sync160.agent-events` 6, `setup.sync160-native-map-...` 1), -1 (`codemode.describe.uses-the-longest-matching-namespace-prefix`). The 4 skips are the base's: `compat.reverse.upstream-reads-pisharp-sessions` (PISHARP_DURABLE_UPSTREAM unset) and three `plan.symlink-escape.*` (no symlink privilege). |
| PiSharp.Durable.Hosting.Tests | 129 | 129 | 0 | 0 | |
| PiSharp.Durable.Server.Tests | 36 | 36 | 0 | 0 | 31 on the base, +5 `sync160.park-model.*` |
| PiSharp.Durable.Web.Tests | 17 | 17 | 0 | 0 | |
| PiSharp.DurableCli.Tests | 122 | 122 | 0 | 0 | 119 on the base, +3 `sync160.tui.*` |
| PiSharp.CustomAgents.Tests | 104 | 104 | 0 | 0 | |
| PiSharp.WebAgent.Tests | 544 | 544 | 0 | 0 | 543 on the base, +1 `bench.strict.lists-a-schema-node-without-a-type`; `runs.options.refuses-...` and `runs.engine.codex-takes-...` changed |
| PiSharp.Codemode.Tests | 149 | 149 | 0 | 0 | |
| PiSharp.Chord.Durable.Integration.Tests | 7 | 7 | 0 | 0 | |
| PiSharp.Durable150.Capture.Tests | 35 | 35 | 0 | 0 | not in the brief's list; run because the read tool and code mode changed |
| `node --test tools/CompatibilityReport/sync-1.6.0-applicability.test.mjs` | 5 | 5 | 0 | 0 | includes "the pending 1.6.0 snapshot reproduces from its generator" |
| `node --test tools/CompatibilityReport/sync-1.6.0-source.test.mjs` | 3 | 2 | 0 | 1 | |
| `node --test tools/CompatibilityReport/sync-applicability.test.mjs` | 5 | 3 | 1 | 1 | the base's failure: see "What root must do" |

The `sync160.*` cases of PiSharp.Durable.Tests (100 with durable-core's) ran six more times in a row with no failure, and the ten new
examples seven times on memory storage with identical output after the three placeholders.

Builds, 0 errors and 0 warnings each: `src/PiSharp.Cli`, `src/PiSharp.Durable.Web`, `src/PiSharp.WebAgent`, `src/PiSharp.CustomAgents`,
`samples/Durable.Examples`, `samples/Durable.Chat`, `samples/Durable.Chord.Examples`, `samples/Durable.ReleaseWatcher`, and the ten test
projects above.

Not run:

- The SQL Server-backed groups (`storage.mssql.conformance.*`, `session.mssql.*`, `harness.*.mssql.*`, `agent-events.mssql.*`): not
  enumerated without `PISHARP_DURABLE_MSSQL_CONNECTION_STRING`.
- `PiSharp.Durable.Web.BrowserTests` and `PiSharp.WebAgent.BrowserTests`: not built or run. The three Razor views changed here
  (`_ToolCall.cshtml`, `_TaskTree.cshtml`, `_ChatToolCall.cshtml`) compile, and no case renders a parked call in a browser.
- `PiSharp.McpParity.Tests`, `PiSharp.ToolSearch.Tests`, `PiSharp.CodingAgent.Tests`: the coding agent's MCP and code-mode integration
  did not change. It uses `PiSharp.Codemode`, not Durable's code mode, and no file of it named the removed API. `src/PiSharp.Cli`
  changed only under `Durable/` (attach and the durable TUI), which `PiSharp.DurableCli.Tests` covers.
- No failure needed a comparison with the base commit: the only failing check is the node test above.
