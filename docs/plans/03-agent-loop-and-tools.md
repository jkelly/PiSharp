# Phase 3 Agent loop and tools

## Objective and boundary

Deliver a terminal-independent agent that can complete real read, edit, write, search and shell tasks using the Phase 2 provider interface. Its observable event sequence, queued-input behavior and tool results must match the selected Pi baseline. The native runtime and built-in tools must run without Node; a Node installation used by the development-only reference harness does not become a product dependency.

This phase supplies the execution engine and the integration seams for sessions and plugins. Phase 4 owns persistent context and automatic recovery; Phase 6 supplies real plugin loading and dispatch. Phase 3 uses deterministic hook doubles and an in-memory transcript to prove those seams before either later subsystem exists. It does not claim a security sandbox for shell commands or trusted in-process plugins.

**Baseline:** Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. Upstream facts below are compatibility requirements. Proposed C# names, file layout, policy hardening and estimates are PiSharp design choices. No implementation or conformance tests have been executed as part of this plan.

## Entry prerequisites

- Phase 1 has pinned the reference checkout, identified license notices, established the compatibility manifest and selected supported OS/architecture targets
- Phase 2 has frozen messages, finalized assistant results, provider errors, cancellation and stream events sufficiently to use an injected deterministic provider
- The Phase 1 schema inventory identifies tool declaration/input/result shapes, event payloads and required MCP/codemode behavior; the team has distinguished mandatory native-v1 behavior from preview exclusions
- An owner has approved where intentional hardening may differ from upstream, especially final-action authorization, conflict checks and resource limits

A live provider is useful for the phase demonstration but not a prerequisite for deterministic runtime work. Never make paid network calls the only proof that ordering is correct.

## Contracts to freeze first

**Verified upstream behavior.** Parallel execution is the default, with sequential preflight and source-ordered transcript results even when completion events arrive out of order. A sequential tool serializes its batch. The high-level Agent awaits message processing before tool preflight. All finalized results must request termination to suppress automatic continuation. `finishTurn` can end a run or ensure one subsequent request; errors and aborts remain exits. Low-level observational streams do not supply the same awaited subscriber barrier. [S31]

**Proposed implementation.** Keep an explicit turn state machine, an awaited authoritative event sink, an optional observational stream and a tool scheduler as separate components. A single session/run coordinator assigns operation IDs and generation tokens. Short state changes are serialized; no coordinator lock remains held across provider I/O, hooks, nested tool calls, dialogs or process waits. Every event carries the correlation information needed to connect a tool call, turn, run and session. Adapters translate to the pinned Pi wire shape rather than adding native fields indiscriminately.

## Work packages

### P3-01 Freeze runtime contracts and reference scenarios

**Effort:** 2–3 engineer-days. **Depends on:** Phase 1 manifest and Phase 2 contracts.

**Work:** Propose `src/PiSharp.Agent/IAgent.cs`, `AgentOptions.cs`, `AgentSnapshot.cs`, `AgentEvents.cs`, `ToolContracts.cs` and `docs/decisions/agent-state-ownership.md`. Specify prompt, continue, steer, follow-up, abort, wait-for-idle and disposal independently. Separate the mutable streaming snapshot from finalized messages. Define exactly what an SDK completion task waits for and what an observational subscriber can delay. Freeze unknown-tool, invalid-argument, blocked, execution-error and cancellation envelopes. Record which tool schema normalization happens before validation.

**Fixtures and evidence:** Add `compatibility/agent/scenarios.json` and the proposed `tests/PiSharp.Agent.Tests/ContractTests.cs`. Each scenario points to the upstream file/line range, immutable SHA, fixture inputs, allowed normalized fields and expected events. Normalize timestamps, generated IDs, temporary paths and wall-clock durations only; never normalize away missing events, result order, errors or tool arguments.

**Done when:** Agent, Sessions and Extensions owners approve the contracts and each required scenario has an owner. A single event diagram covers normal, tools, error, cancellation and disposal paths without ambiguous terminal states.

### P3-02 Implement the turn runner and request lifecycle

**Effort:** 4–6 days. **Depends on:** P3-01 and the Phase 2 deterministic provider.

**Work:** Implement proposed `TurnRunner.cs`, `RequestPreparer.cs`, `TranscriptTransform.cs` and `AwaitedEventDispatcher.cs`. Follow input selection, request preparation, context transformation, provider streaming, finalized assistant commit, tools, turn finalization and continuation. Preserve transcript system-message and tool-loadout updates. Keep conversion to provider messages at the boundary. Provide the high-level awaited event barrier and clearly label any low-level observational API. Prevent a slow renderer from becoming the authoritative persistence path.

**Fixtures and evidence:** `LifecycleTests.cs` should cover a single text response, multiple content blocks, a provider failure before start, failure after partial output, a delayed message subscriber, subscriber registration order and an awaited run-end subscriber. Add deterministic tests for end/continue decisions and for a selected next request satisfying a continue decision exactly once. Add a tool-loadout change between turns and verify the next provider request sees it.

**Done when:** Trace diffs show the intended request/event order and every started operation reaches one terminal outcome. No tool side effect can begin while the finalized assistant-message barrier is deliberately blocked.

### P3-03 Implement scheduler ordering and queue semantics

**Effort:** 4–6 days. **Depends on:** P3-02.

**Work:** Implement proposed `ToolBatchScheduler.cs`, `PendingInputQueue.cs` and `ContinuationPlanner.cs`. Preflight in assistant order, run allowed parallel calls, finalize outcomes independently, then commit transcript results in source order. Preserve explicit sequential execution and batch-wide overrides. Implement separate steering/follow-up queues and both queue-drain modes. Define race-free admission of a prompt while busy and preserve a rejected prompt without silently queueing it. Distinguish agent-run completion from session settlement, which P4-07 completes.

**Fixtures and evidence:** Barrier-controlled tools A/B/C finish C/A/B; assert completion events C/A/B and transcript A/B/C. Repeat with a blocked B, failing A, a sequential tool anywhere in the batch and cancellation during preflight. Test all-terminate versus mixed outcomes. Queue input during tools, request preparation and turn finalization. Exercise continue with empty, system-only, user/tool-result and assistant-tailed contexts without unintentionally consuming queued input. Test one-at-a-time and all-message modes. [S31, S32]

**Done when:** Scheduler tests rely on explicit task gates rather than sleeps; repeated runs cannot change permitted ordering. No frontend is required to make a queued turn run or to decide that a run is finished.

### P3-04 Build one tool invocation and policy path

**Effort:** 3–5 days. **Depends on:** P3-01; integrate with P3-03.

**Work:** Implement proposed `ToolInvoker.cs`, `ToolArgumentNormalizer.cs`, `IToolPolicy.cs`, `ToolInvocationContext.cs` and `InvocationScope.cs`. Resolve a tool, normalize supported argument variants, validate, run the baseline hook sequence, revalidate any changed action, authorize the final executable action and execute it. Process final result hooks before emitting terminal tool events. Authorization must include the resolved path or final shell command, cwd and environment after host transformations. Policy denial is a structured result, not an exception that tears down unrelated tools.

Use this same invoker for host-mediated nested tools, MCP wrappers and future codemode calls. Carry parent invocation identity, cancellation, generation and bounded call depth; do not hold an outer concurrency permit while synchronously waiting for an inner call that needs the same permit. Distinguish trusted code running outside host APIs from actions the policy actually mediates. The hook/authorization additions are explicit hardening, not a claim that native assemblies are confined.

**Fixtures and evidence:** `ToolPipelineTests.cs` covers validation before hooks, transforms that change a previously authorized target, result overrides, thrown hooks and unknown tools. A fake outer tool calls a blocked inner file tool; assert no filesystem call. Include nested calls at scheduler capacity to detect deadlocks. Pinned upstream rejects tool execution from an output-length-truncated assistant response even when salvaged arguments appear usable; record one error per affected call and verify zero invocations. [S32]

**Done when:** No supported host-mediated path bypasses final-action validation/policy, and every denial, malformed call or nested failure has the documented observable outcome.

### P3-05 Implement file access primitives and read and write tools

**Effort:** 3–5 days. **Depends on:** P3-04; can run alongside P3-03.

**Work:** Add proposed `PiSharp.Tools/Files/IFileOperations.cs`, `PathResolver.cs`, `FileMutationQueue.cs`, `ReadTool.cs`, `WriteTool.cs`, `TextEncodingPolicy.cs` and shared output-limit helpers. Port path expansion and cwd-relative behavior from the pinned implementation. Characterize image/binary detection and read offsets, limits and line numbering before selecting .NET libraries. Match write directory-creation and overwrite semantics. Preserve cancellation checks without releasing a file mutation slot while an underlying write can still finish.

**Proposed hardening:** Resolve existing aliases for queue keys; handle not-yet-existing destinations consistently. Implement same-directory atomic replacement only where its behavior is proven acceptable. Explicitly test ACL/mode, symlink, hard-link and file-watcher consequences before treating replacement as equivalent to in-place writing. Compare file identity/content before commit where feasible to detect outside edits; a detected conflict must not silently overwrite newer content. Document residual races with external processes and hostile path changes.

**Fixtures and evidence:** Use an isolated temporary filesystem for Unicode and spaced paths, relative paths, empty files, long lines, BOMs, CRLF/LF, invalid encoding, images, binary input, permission failures and directory collisions. Verify saved bytes, reported output and unchanged files on pre-commit cancellation. Queue two writes through a symlink alias, then cancel the first during a delayed write and prove the second starts only after that write settles. [S35]

**Done when:** Cross-platform byte fixtures pass and effect logs show serialized same-file mutations without serializing unrelated files. Diagnostics distinguish a denied action, failed I/O, detected conflict and cancellation after an effect may already have happened.

### P3-06 Port multi-edit matching and mutation behavior

**Effort:** 4–6 days. **Depends on:** P3-05.

**Work:** Implement proposed `EditTool.cs`, `EditMatcher.cs`, `EditPlan.cs` and `DiffFormatter.cs`. Freeze the wire schema and argument-preparation compatibility. Validate the entire edit plan against original content before mutation; do not implement `edits[]` as successively applied replacements. Preserve ambiguity and overlap failures, BOM/newline handling, diff/patch data and first changed line. The baseline accepts the older single replacement form as well as the new array and certain model-produced argument variants. [S34]

**Fixtures and evidence:** `EditGoldenTests.cs` covers disjoint replacements in reverse source order, replacement text that would match a later old-text only after mutation, overlapping/nested regions, repeated matches, no match, no-op edits, empty input, normalization/fallback matching from `edit-diff.ts`, supplementary Unicode, BOM and mixed endings. Record input bytes, expected output bytes, result text and diff data. Add cancellation at every awaited boundary, two queued edits to one file and an external change between read and commit.

**Done when:** Every invalid edit leaves original bytes unchanged; successful edits produce the pinned content and result shape. Any stronger conflict-detection or atomic-replacement behavior has a manifest entry and a test instead of being described as exact parity.

### P3-07 Implement bash and PowerShell with process cleanup

**Effort:** 4–7 days. **Depends on:** P3-04; can parallelize with P3-05/06.

**Work:** Implement proposed `Processes/IProcessRunner.cs`, `ShellResolver.cs`, `ProcessTreeLifetime.cs`, `ShellOutputAccumulator.cs`, `BashTool.cs` and `PowerShellTool.cs`. Reproduce supported executable discovery, quoting/command transport, command prefixes, cwd, environment and spawn hooks. Do not silently substitute another shell. Distinguish missing executable, spawn failure, nonzero exit, signal termination, timeout and user abort. Propagate progress, flush output and close temporary files on all paths.

**Verified output contract:** Bash uses model-facing tail truncation and a separate structured result capped at 1 MiB, including output, truncation, exit status and elapsed time; long structured output retains both ends. Timeout is optional rather than a mandatory default. [S36] The common truncation constants are 2,000 lines and 50 KiB; head/tail and byte-boundary behavior must be selected per tool. [S37]

**Fixtures and evidence:** Windows, Linux and macOS process fixtures cover spaces/quotes, Unicode, stdout/stderr interleaving, absent cwd, environment overrides, invalid timeout, empty output, nonzero exit, oversized output, a single oversized UTF-8 line, a child holding inherited pipe handles, grandchildren and cancellation before spawn/during execution/during drain. Verify no hangs, no duplicate final updates and no supported descendant left running after cancellation. Use platform-specific cleanup implementations where needed; a .NET process-tree kill call alone is not evidence for every descendant shape.

**Done when:** The OS capability matrix names tested shell versions and missing-shell behavior. Cleanup evidence includes child PIDs/liveness and temporary file lifecycle, with any platform limitations disclosed. Cancellation does not imply rollback of shell side effects.

### P3-08 Implement grep find and ls

**Effort:** 2–4 days. **Depends on:** P3-04/05 and Phase 1 tool inventory.

**Work:** Add proposed `Search/GrepTool.cs`, `FindTool.cs`, `ListTool.cs`, `ISearchBackend.cs` and `SearchBackendCapabilities.cs`. Map all baseline schema parameters and output formatting. Decide managed implementation versus packaged external utilities using measured semantic fixtures, including regex behavior, hidden/ignored files, globbing, recursion, sorting, symlinks and case sensitivity. If binaries are required, pin version, platform availability, license and integrity checks; do not accidentally rely on a developer's PATH.

**Fixtures and evidence:** Small checked-in directory fixtures should exercise dotfiles, ignore files, nested roots, symlink loops, Unicode, binary matches, no results and limited results. Assert path formatting and truncation independently of directory enumeration order. Compare the same corpus against the pinned tools on each supported platform. The tool inventory includes all eight named tools in this phase. [S33]

**Done when:** Each supported parameter has a fixture, and an unavailable backend causes an explicit capability failure rather than silently changing search behavior.

### P3-09 Connect cancellation disposal and coding-agent composition

**Effort:** 3–5 days. **Depends on:** P3-02 through P3-08.

**Work:** Add proposed `PiSharp.CodingAgent/AgentSession.cs`, `RunLifetime.cs`, `SessionGeneration.cs` and tool-factory composition. Propagate cancellation to providers, preflight, hooks, tools and nested calls. After cancellation, prevent new execution while awaiting cleanup of already-started effects. Distinguish cancellation requested from operation settled. Dispose old listeners and contexts; guard late progress/results with generation checks. Keep failed tool effects out of automatic retry logic. Provide interfaces for P4-07 to supply finalized context and recovery, and for Phase 6 to supply hook dispatch.

**Fixtures and evidence:** Inject cancellation at every state transition, a non-cooperative fake tool, a listener failure and session disposal during progress. Verify no late events mutate a replacement session. Add integration traces for read → edit → shell check → assistant summary using a scripted provider, plus a nested invocation and denied mutation. A timeout or cancellation after a file/process effect must remain visible as uncertain or partially completed where appropriate.

**Done when:** A disposed session releases owned streams/processes/listeners, refuses new work and cannot commit stale work elsewhere. Cooperative cancellation behavior and the limits for arbitrary trusted code are documented.

### P3-10 Produce the runtime conformance report

**Effort:** 3–4 days. **Depends on:** all Phase 3 packages.

**Work and evidence:** Produce proposed `compatibility/reports/phase-3.json`, human-readable `docs/compatibility/agent-and-tools.md` and archived normalized event/result traces. Run the suite against the pinned reference and PiSharp build, with recorded source SHA, PiSharp revision, OS, architecture, shell/backend versions and any environment normalization. Add a small real-provider demonstration only after deterministic tests pass. Update the manifest per supported operation and intentional deviation.

**Exit gate G3:** All mandatory Phase 3 rows have passing evidence on required platforms; ordering/barrier, truncated-call, nested-policy, edit atomicity and cleanup tests are mandatory blockers. The native demonstration runs without Node. No unexplained transcript difference, orphaned process, silent edit conflict or policy bypass remains. Any preview restriction is explicit; later plugin loading, persisted settlement and full MCP/codemode runtime parity cannot be claimed merely because their test doubles passed.

## Dependencies effort and parallelization

**Total:** 32–51 engineer-days, including implementation, fixtures, review and phase acceptance. These are planning ranges for an experienced engineer with Phase 1/2 foundations available, not measured velocity or a promised ship date. They exclude blocked time and unexpected cross-platform dependency replacement.

Critical path: P3-01 → P3-02 → P3-03 → P3-09 → P3-10. P3-04 can start from the agreed contract; file/edit, shell and search work can then progress independently. P4-01/02 can start once finalized-message and storage interfaces are stable, but P4's final gate requires P3's cancellation and barrier guarantees. Phase 6 may design the native SDK in parallel against hook contracts; do not duplicate the tool invoker inside the plugin runtime.

## Main risks and early decisions

- **Async deadlocks and reordered events:** Separate awaited authoritative dispatch from visual observation; test reentrancy and nested calls using task gates
- **Hidden OS differences:** Schedule Windows shell/path tests early; decide required shell availability before promising universal tool support
- **Filesystem interference:** Same-process queues coordinate PiSharp calls only. External writes, alias changes and shell mutations need detection or documented limits
- **Lost output and runaway processes:** Validate inherited pipes and descendants, not only direct child cancellation; retain evidence for truncation and cleanup
- **Hardening versus compatibility:** Record policy checks, conflict rejection and resource caps as deliberate deviations where upstream differs
- **Native-v1 scope:** Required MCP/codemode execution behavior needs its own traced delivery owner in Phase 6 or 7; a nested-call interface alone does not finish it

## Sources

All links target the immutable baseline SHA unless stated. Selected raw files were read through the equivalent `v0.99.1` tag where the web fetch of the SHA URL was unavailable; Phase 1 should retain hashes when importing these into the reference corpus.

- **S31** [Agent README and lifecycle contract](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/README.md)
- **S32** [Agent loop implementation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent-loop.ts)
- **S33** [Built-in tool inventory](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/index.ts)
- **S34** [Edit schema and implementation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/edit.ts)
- **S35** [Per-file mutation queue](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/file-mutation-queue.ts)
- **S36** [Shell execution and structured output](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/bash.ts)
- **S37** [Shared truncation implementation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/truncate.ts)
- **S41** [CLI session format](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/session-format.md)
- **S42** [Session entry definitions migrations and projections](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/session-manager.ts)
- **S43** [Compaction and recovery reference](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/compaction.md)
- **S44** [Compaction implementation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/compaction/compaction.ts)
- **S45** [Coding agent SDK session authority and settlement](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/sdk.md)
