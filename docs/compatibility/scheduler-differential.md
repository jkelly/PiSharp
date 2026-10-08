# Bounded native scheduler differential

The native `ToolBatchScheduler` has one differential test against the frozen genuine upstream awaited-loop oracle introduced in commit `2486e4b2a8389729e4fb120829697e23a9bf6380`. This compares a scheduler projection of that oracle. It does not compare full Agent-loop events, provider streaming, request preparation, tool declarations or production provider wire behavior.

The unchanged upstream capture uses Pi v0.99.1 at `d86654abb8862e201933517d6f1fce9f88dd117f`. Its [capture qualification](full-agent-reference.md), [authored input](../../fixtures/pi-v0.99.1/agent/awaited-parallel.input.json), [observed oracle](../../fixtures/pi-v0.99.1/agent/awaited-parallel.expected.json) and [manifest](../../fixtures/pi-v0.99.1/agent/manifest.json) remain untouched. The native test pins the input SHA-256 `8807a17dcfb167ffce1103a117ddcb495495e535180f5b7ee8dc236653aefc51` and oracle SHA-256 `fd21837b2427e24d2a6d565a554ac1fc91518aa152dac2dfcbcf8874062f814b`. Missing or altered fixture bytes fail before execution.

`agent.FrozenSchedulerProjection` reads the finalized assistant with the native wire reader, executes synthetic native tools and compares the following retained observations:

| Retained contract | Frozen oracle source | Native observation |
| --- | --- | --- |
| Awaited assistant barrier | `controls.barrier_probe` and `checks.barrierBlockedTools` | Preflight/execution remain zero while the sink gate is held |
| Preflight order and exact final arguments | `toolTrace` preflight entries | Hook input IDs and owned JSON arguments |
| Parallel admission and maximum concurrency | `toolTrace` execute-start entries and `checks.maxConcurrentTools` | Start IDs/arguments and measured three active executors |
| C/A/B completion observations | `events` tool-execution-end entries and completion checks | Native finalized execution events |
| A/B/C committed result projection | `finalResult` tool-result entries and result-order checks | Awaited transcript-end observations and returned messages |
| Unanimous termination hint | Every execution-end result requests termination | Batch `Terminate=true`, `ShouldContinue=false` |

Native tools independently replay the authored synthetic behavior in `tools/PiReferenceRunner/full-capture.mjs`: return text `result:<callId>`, details from the integer `value` argument, and a termination hint. They do not obtain result bodies from expected output. Explicit gates from the input completion script release C, A and B; each release waits for its native completion observation. The assistant sink barrier and all-started gates control admission. There are no sleeps; the existing console suite timeout only guards a hang.

The result projection compares IDs, names, ordered text content, error flags and JSON details. It excludes upstream timestamps and `role`, which are absent from the native scheduler message type. JSON object-key order alone is ignored; array order, presence versus null, exact strings and numeric lexemes are preserved. Upstream request counts and `agent_end` counts are deliberately outside this projection: the native scheduler supplies a continuation hint and does not issue a provider request or own Agent lifecycle events.

Run `tools/test-native.ps1` from the repository root. The existing native console suite copies the two unchanged fixtures to its output directory and requires no Node, provider credentials or network. Its `--report` output adds a `schedulerDifferential` evidence object scoped as `bounded-scheduler-projection`, including fixture hashes, measured orders/concurrency and `fullAgentLoopParity=false`; the test row is `bounded scheduler projection matches frozen awaited upstream oracle`. The standard helper writes this under ignored `artifacts/native/agent-results.json`.

The first targeted Windows run on 30 September 2026 built `tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj` in Release with `--no-restore`, zero warnings and zero errors. Executing its DLL with `--report artifacts/native/agent-results.json` passed 17/17 cases, including the new frozen projection. CLI home, package cache, temporary and app-data directories were isolated under ignored task artifacts using the helper's environment settings; the test PATH contained only the .NET executable directory, with Node absence checked before execution. This is targeted native evidence; release acceptance and the common helper verification are owned separately.

This adds one native differential scenario to the existing deterministic scheduler suite. The accepted scheduler and public contract source are unchanged. Other agent scenarios, cancellation/error differential capture, queues, nested tools, schema/policy enforcement, durable state, complete lifecycle/provider parity and cross-platform qualification remain open. No Phase 1/2/3 gate closes from this result.
