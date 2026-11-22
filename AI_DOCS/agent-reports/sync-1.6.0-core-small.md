# Pi 1.6.0 sync: core-small (agent, env, mcp)

Branch `sync/1.6.0-core-small`, from `8352a672`. Upstream range `v1.5.2..v1.6.0` (`b4afdddf..faac5858`) of `packages/agent`, `packages/env` and `packages/mcp`. Nothing was pushed, tagged or published. No version constant or root-owned file was changed.

## Commits

| commit | subject |
| --- | --- |
| `cba6affb` | Answer every tool call of a batch aborted mid-batch (Pi 1.6.0 agent) |
| `99b74336` | Cover an abort during a tool batch at the persistent session level |
| `169f698d` | Leave vanished and non-UTF-8 entries out of the remote listDir (Pi 1.6.0 env) |
| `d2ad0c4a` | Keep reading a program's output after exit while the link is backed up (Pi 1.6.0 env daemon) |
| `be6c1e77` | Add McpCatalogPlanner.ServerSummary (Pi 1.6.0 mcpServerSummary) |
| (this commit) | Report, the `pty.rs` header and two comments |

## Upstream changes

### packages/agent

| upstream | status | C# | cases |
| --- | --- | --- | --- |
| `src/agent-loop.ts` `executeToolCallsSequential`: no `break` after an aborted call | ported | `src/PiSharp.Agent/ToolBatchScheduler.cs` `RunSequentialAsync` | `sync160.sequential-batch-aborted-mid-batch-answers-every-call` |
| `src/agent-loop.ts` `executeToolCallsParallel`: no `break` while calls are prepared | ported | `ToolBatchScheduler.cs` `RunParallelAsync`, `RunSourceParallelAsync` | `sync160.parallel-batch-aborted-during-preparation-answers-every-call` (NativeAwaited and SourceCompatible) |
| `src/agent-loop.ts` `prepareToolCall`: the abort check comes first, before the tool lookup and `beforeToolCall`; the second check before `prepared` is gone | ported | `ToolBatchScheduler.cs` `PrepareAsync` (after the truncated-message check, which upstream makes outside `prepareToolCall`) | `sync160.aborted-signal-answers-before-tool-lookup-and-before-hook` |
| the loop enters `executeToolCalls` whatever the signal | ported | `ToolBatchScheduler.RunAsync` no longer returns an empty batch for a token cancelled before the batch; each call is answered `Operation aborted` | same case; `cancellation before batch prevents execution and answers the call` |
| `runToolCall`: an aborted signal comes back first, before the hooks | already-ported | `src/PiSharp.Agent/ToolInvocationContext.cs` `ExecuteToolAsync` rejects a nested call on a cancelled token before any lookup or hook; `ToolInvoker.ExecuteCoreAsync` checks the token first | existing `ToolInvokerTests` (pre-cancelled token), `NestedToolInvocationTests`; no new case |
| `test/agent-loop.test.ts` (+123 lines: `runToolBatch`, two cases) | ported | `tests/PiSharp.Agent.Tests/Sync160AgentCases.cs` | the two scheduler cases above, and the same two scenarios through `Agent` (`sync160.agent-sequential-abort-...`, `sync160.agent-parallel-abort-...`) |
| `CHANGELOG.md` [1.6.0] #9986 | ported | as above | as above |
| `package.json` (version, dependency pins) | no-op | version pins belong to `meta` | |

Coding agent (`packages/coding-agent/CHANGELOG.md` "pressing Esc during a batch of tool calls"): Esc sends the RPC `abort` command (`InteractiveMode.AbortAsync`), `RpcSessionDispatcher.AbortAsync` calls `PersistentAgentSession.Abort()`, which cancels the agent's run token, and the scheduler change above answers the remaining calls. The case `sync160.Persistent Agent abort during a tool batch persists a result for every call` (`tests/PiSharp.CodingAgent.Tests/PersistentAgentSessionTests.cs`) aborts a three-call batch through the session, checks the three results in the session file and after reopening it, and checks that the next request carries no tool call without its result. Nothing was run through the interactive mode or the RPC dispatcher itself.

Expectations changed because upstream changed the behavior (rule 4), all in `tests/PiSharp.Agent.Tests/Program.cs`:

- `cancellation during preflight prevents execution` and `preflight deadline joins original batch and stops admission` (shared fixture): expected two outcomes and `C` never started; now three outcomes, `C` gets its start, an `Operation aborted` result and its end, and its hook is still not asked. The first case is renamed `... and answers every call`.
- `preflight early hook failure joins original batch`: expected only `A`'s outcome; now `B` and `C` also get `Operation aborted`.
- `cancellation before batch prevents execution`: expected no outcome and one event; now the call is answered `Operation aborted` (five events). Renamed `... and answers the call`.

With the old scheduler restored, these four cases and all six `sync160` cases fail; with the change they pass.

Not changed: `ToolInvoker` and `ToolInvocationContext` word their own cancellation results `Tool invocation was cancelled.` and `Nested tool invocation was cancelled.` where upstream's `runToolCall` says `Operation aborted`. That wording predates this range (upstream 1.5.2 already said `Operation aborted` there) and is used outside these paths (`src/PiSharp.Extensions.Runtime/Registration/ExtensionRegistrySnapshot.cs`), so it is left as it was.

### packages/env

| upstream | status | C# | cases |
| --- | --- | --- | --- |
| `src/remote-env.ts` `listDir`: skip an entry whose error is `ENOENT`; other entry errors fail (#11994) | ported | `src/PiSharp.Env/RemoteFileSystem.Listing.cs` `ListDirAsync` | `tests/PiSharp.Env.Adapter.Tests/Program.cs` (fixture daemon: vanished entry and a non-UTF-8 name left out, `EACCES` entry fails with `PermissionDenied` and the entry's path) |
| changelog: names that are not valid UTF-8 are left out | ported | same; the native daemon reports such a name as `ENOENT` because `DaemonDirectory` hands `lstat` the replacement-decoded name | `tests/PiSharp.Env.Adapter.Tests/RealDaemonTests.cs` `InvalidUtf8NameAsync` against the real daemon (Linux only; prints `SKIP` elsewhere) |
| `daemon/src/exec.rs`, `process.rs`, `output.rs` (`Gate`): silence while the link holds a reader up is not the end of the output | ported | `src/PiSharp.Env.Daemon/Operations/ProcessOperations.cs`: the grace after exit restarts after a chunk is handed to the link, not before | `tests/PiSharp.Env.Process.Tests/Sync160EnvProcessCases.cs`: `sync160.process-reads-on-after-exit-while-link-holds-output-back`, `sync160.exec-without-window-reads-on-after-exit-while-link-holds-output-back` |
| `daemon/src/pty.rs` (`Gate` for the terminal reader) | already-ported | `src/PiSharp.Env.Daemon/Operations/TerminalOperations.cs`: `OutputPending` keeps the drain going while a chunk is handed over, and `LastOutput` is stamped after the hand-over. Header moved to the v1.6.0 commit, one comment added | existing `native-terminal-drains-output-through-slow-consumer` (ran on Linux) |
| `test/process.test.ts` (+34 lines) | ported | `Sync160EnvProcessCases.cs` | `sync160.process-output-left-in-pipe-survives-exit-while-link-is-backed-up (upstream scenario)` |
| `test/build-daemon.ts`, `vitest.config.ts` (build the Rust daemon before the tests) | no-op | upstream build tooling; the native suites reference the daemon project and build it | |
| `package.json` | no-op | version pins belong to `meta` | |

How the daemon fix maps. Upstream reader threads wait for room in the output queue after each chunk, and the command loop took their silence for a silent pipe. The native loop is shaped differently: two pumps read up to nine 16 KiB chunks ahead into a bounded channel, and the command loop hands one chunk at a time to the link and waits for it. The same fault was there in another place: the loop stamped the grace before the hand-over, so a hand-over the link held up for longer than the grace (100 ms for `process`, 200 ms for `exec` without a window) cancelled the pumps, and the chunk a pump had in hand and the rest of the pipe were dropped. `exec` with an output window never waits for the link in that loop (as upstream, which has no gate there) and is unchanged.

The upstream scenario (one 1.5 s stall, 96 KiB) passes on the old native code too, because the pumps read 96 KiB ahead. The two other cases stall the link on every output event of a program that writes 267,387 bytes and exits. Against the old `ProcessOperations.cs` they fail: on Windows `262144 of 267387 bytes in 16 events` (the last partial chunk), on Linux `229376 of 267387 bytes in 14 events`. With the fix they pass on both.

Expectation changed because upstream changed the behavior: `tests/PiSharp.Env.Adapter.Tests/Program.cs` expected `ListDirAsync` to fail with `NotFound` for the fixture's vanished entry; it now expects the listing without it.

### packages/mcp

| upstream | status | C# | cases |
| --- | --- | --- | --- |
| `src/naming.ts` `mcpServerSummary(description, instructions)`; `src/index.ts` export | ported | `McpCatalogPlanner.ServerSummary(string? description, string? instructions)` in `src/PiSharp.Extensions.Abstractions/Mcp/Configuration/McpCatalogPlanner.cs` (namespace `PiSharp.Extensions.Mcp.Configuration`, beside `Namespace` and `CreateToolName`, the ports of `mcpNamespace` and `createMcpToolName`) | `tests/PiSharp.McpSync.Tests/Sync160McpCases.cs`: `mcp160.server-summary-first-line-of-description-else-instructions` (the upstream case), `mcp160.server-summary-trims-like-javascript` |
| `test/naming.test.ts` (+11 lines) | ported | `Sync160McpCases.cs` | as above |
| `package.json` | no-op | version pin belongs to `meta` | |

## Suites run

Pinned SDK 10.0.401, Release, Windows unless noted.

| suite | result |
| --- | --- |
| `PiSharp.Agent.Tests` | 243 passed, 1 failed, 0 unexecuted. The failure is the known `released-radius-all-28-row-routing` (missing `artifacts/released-npm-ai/pi-ai-0.99.1.tgz`) |
| `PiSharp.CodingAgent.Tests` | Three full runs, 50 skipped each (no published fixtures). Last run, on the final code: 866 passed, 0 failed. The two earlier runs had intermittent failures in cases this package does not touch: run 1 (before the env and mcp commits) 865 passed, 1 failed; run 2 (final code) 864 passed, 2 failed. See the last section |
| `PiSharp.McpSync.Tests` | 66 cases, 0 failures |
| `PiSharp.Env.Process.Tests` | 14 cases passed (Windows); 14 passed (Linux) |
| `PiSharp.Env.Adapter.Tests` | passed, 55 `PASS` lines and 1 `SKIP` (the Linux-only name case) on Windows; 62 `PASS` lines, 0 skipped on Linux |
| `PiSharp.Env.Daemon.Tests` | 10 passed (Windows and Linux) |
| `PiSharp.Env.Connection.Tests` | 10 passed (Windows and Linux) |
| `PiSharp.Env.Terminal.Tests` | 7 passed on Windows (protocol and `ENOTSUP`); 14 passed on Linux (native PTY) |
| `PiSharp.Env.Watch.Tests` | 5 passed (Windows and Linux) |
| `PiSharp.Env.Ssh.Tests` | 9 passed (Windows; not run on Linux) |
| `PiSharp.Rpc.Tests` | 137 passed, 0 failed |
| `PiSharp.Transport.Tests` | 245 passed, 0 failed |
| `PiSharp.WireSync.Tests` | 0 failures |
| `PiSharp.Extensions.ContractTests` | 381 of 397 passed. The 16 failures all say the published fixture projects are missing (`Parent must publish approved fixture project`, `Root must publish the approved ... fixture`). Not checked on the base commit |

The Linux runs used WSL (Ubuntu 24.04, .NET SDK 10.0.401): the commit was exported with `git archive` to a WSL-local directory and built there, so the native helper `libpisharp_env_native.so` was compiled. The export held `d2ad0c4a`; the later commits do not touch the env sources except for comments and the `pty.rs` header. macOS was not run.

Not run: `PiSharp.CompletionsGrammar.Tests` and `PiSharp.CompletionsThinking.Tests` (they build the scheduler but need a fixture file and a report path; neither cancels a tool batch), the Durable suites (Durable has its own tool loop) and the interactive suites.

## Deferred

None in this area.

## For root and other packages

- `cli-session`: `McpServersSection.Summary` (`src/PiSharp.Extensions.Abstractions/Mcp/Configuration/McpServersSection.cs`, the port of `renderServersSection`) still has the 1.5.2 logic. Upstream now calls `mcpServerSummary(server.entry.config.description, server.connection?.instructions)`; replace the private helper with `McpCatalogPlanner.ServerSummary(server.Entry.Config.Description, server.Instructions)`. The result differs for instructions that start with white space or a blank line: 1.5.2 took the first line of the untrimmed instructions (possibly empty), 1.6.0 trims first.
- `cli-session`, `cli-ui`: the coding agent changelog bullet for #9986 needs no further native code. A tool result with `Operation aborted` now follows every call of a batch interrupted by Esc; anything that counted on calls without results after an abort (transcript repair, rendering of pending calls) should be checked against that.
- `durable-edge`: the remote `ListDirAsync` now leaves out vanished entries. If the Durable env conformance suite (`PiSharp.Durable.Testing.EnvConformance`) gains a listing case for 1.6.0, `PiSharp.Env.Adapter.Tests` runs it against the real daemon.
- `meta`/root: `src/PiSharp.Extensions.Abstractions` gained one public method (`McpCatalogPlanner.ServerSummary`); `tests/PiSharp.CodingAgent.Tests/PersistentAgentSessionTests.cs` gained one case (count +1). No lock file, project file or version string changed.
- The Env suites are not in any workflow under `.github/workflows`; the Linux results above come from a local WSL run.
- `PiSharp.CodingAgent.Tests` has timing-sensitive cases that failed in a full run and passed alone. Run 1: `mcp-http-loopback.concurrent-and-later-request-survive-peer-abort` (`Not listening. You must call the Start() method`). Run 2: `mcp-profile. profile replacement binds fresh generation and retires old resources` and `terminal-session.receipt-primary-held-physical-cancel-callback-original-join`. Each group passed three times in a row when run by `--filter`, and the third full run had no failure. None of them was run on the base commit, so whether they are equally intermittent there is not established.
