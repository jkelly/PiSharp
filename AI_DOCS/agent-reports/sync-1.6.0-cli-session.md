# cli-session: Pi 1.5.2 -> 1.6.0

Branch `sync/1.6.0-cli-session`, from `8352a672`. Upstream v1.6.0 `faac585884ae2c013a6b1400f3e9e77f10b34bac`, compared with v1.5.2
`b4afdddf44e059c67ccf5097e5317d913521b470`. Scope: `packages/coding-agent/src` outside `core/tools`, `core/exec.ts`,
`core/export-html` and `modes/interactive/**`.

Four slices (package installs, llama.cpp, MCP, event-bus errors with the session list loader) were implemented by helper agents in
this worktree and committed here after their suites passed; the rest was done directly.

## Status per upstream source or changelog bullet

| Upstream | Status | Native files | Cases |
| --- | --- | --- | --- |
| `core/agent-session-runtime.ts` `takeUnreportedDiagnostics()` | ported | `src/PiSharp.CodingAgent/AgentSessionRuntimeDiagnostics.cs` (new, public: `AgentSessionRuntimeDiagnostic`, `AgentSessionRuntimeDiagnostics.Replace/Add/Current/TakeUnreported`) | CodingAgent `sync160 runtime diagnostics returns each diagnostic once, at startup and after replacements`, `... keys on type and message and keeps the runtime's order` |
| `modes/diagnostics-stderr.ts` `writeDiagnosticsToStderr` | ported | `src/PiSharp.Cli/Pi/PiDiagnosticsStderr.cs` (new); `PiCommand.Report` uses it | CliParity `sync160.diagnostics.stderr-lines-follow-diagnostics-stderr` |
| `main.ts`: startup diagnostics taken once (`runtime.takeUnreportedDiagnostics()`) | ported | `PiCommand.cs`, `PiEntryOptions.cs` (`RuntimeDiagnostics`, `CollectReplacementDiagnostics`, `DiagnosticsColor`) | CliParity `sync160.diagnostics.model-fallback-is-reported-at-startup-in-every-mode` |
| `main.ts`: `Could not restore model <provider>/<model>. Using <fallback>` is a diagnostic in every mode at startup; interactive keeps `modelFallbackMessage` only without any model | ported | `PiCommand.cs` (the selection's fallback message moves into the runtime diagnostics and is cleared) | same case (print, JSON, interactive options, `--model`, restored model, no model) |
| `rpc-mode.ts`/`print-mode.ts` `setRebindSession`: unreported diagnostics to stderr after each replacement; model fallback after `switch_session`/`fork` | ported | `RpcSessionCommand.cs` (wraps the session owner's `AttachmentChanged`; `ReplacementDiagnosticsAsync`) | CliParity `sync160.diagnostics.model-fallback-is-written-to-stderr-when-rpc-switch-session-falls-back`, `...-after-rpc-fork` |
| `main.ts`/`model-resolver.ts`: `enabledModels`/`--models` warnings are diagnostics, shown once at startup, not per replacement (#11264) | ported (the native entry never repeated them: the scope was resolved once per run and replacement warnings were dropped; they are now collected per replacement and reported once) | `RpcSessionCommand.cs`, `SettingsModelSelection.ReadPatterns` (internal) | CliParity `sync160.diagnostics.unmatched-enabled-models-pattern-is-reported-once-not-again-on-new-session` |
| Changelog: problems found when replacing the session are reported once (RPC `new_session`, `switch_session`, `fork`) | ported for what a native replacement re-reads: settings file errors, model scope, model fallback | `RpcSessionCommand.cs`, `PiCommand.cs` | CliParity `sync160.diagnostics.a-diagnostic-that-first-appears-on-rpc-new-session-is-written-to-stderr-once`, `sync160.diagnostics.a-settings-error-that-first-appears-on-replacement-is-written-to-stderr-once` |
| Same bullet: a project extension that fails to load after `/resume` into another project | no-op | A native replacement keeps the run's cwd, extensions and resources (they load once per run and on `/reload`); there is no per-replacement extension load to fail | none |
| `rpc-mode.ts`: `new_session`, `switch_session`, `fork`, `clone` ran `session_start`/`resources_discover` twice; clients receive events of `session_start` handlers (#10470) | already-ported | `ReplaceableAgentSession.ReplaceCoreAsync` publishes `AttachmentChanged` (the dispatcher subscribes to the new session) before the single `AfterReplacement` notification that dispatches `session_start`; the dispatcher does not rebind again | ExtensionParity `sync160.rpc-session-replacement-runs-session-start-once-and-streams-its-entries` |
| `print-mode.ts`: `--mode json` dropped events of extension `session_start` handlers at startup and after an extension replaces the session | already-ported | Print and JSON modes run over the RPC host, whose dispatcher subscribes before `sessionStartup` | ExtensionParity `sync160.json-mode-streams-entries-appended-by-session-start-handlers` |
| `agent-session.ts` `_extensionsBound`: `reload()` emits `session_start` when bound with `bindExtensions({})` | already-ported | `OfflineSessionProfile.PiReloadAsync` always dispatches `session_shutdown` then `session_start` (reason `reload`) for an activation; there is no bindings heuristic | ExtensionParity `sync160.reload-emits-session-shutdown-then-session-start-once` |
| `agent-session.ts` `getLastAssistantText` (#12119), RPC `get_last_assistant_text`, `/copy` | ported | `src/PiSharp.Rpc/Protocol/RpcSessionDispatcher.cs` (`LastAssistantText`; interactive `/copy` asks the same RPC command) | Rpc `rpc.sync160.last-assistant-text-returns-the-previous-answer-after-an-aborted-tool-call`, `...-after-a-provider-error`, `...-keeps-a-normal-reply-without-text-as-the-last-reply` |
| `agent-session.ts` `_restoredToolNames` (#11996) | ported (helper) | `PersistentAgentSession.PendingTools.cs`, `.Catalog.cs`, `.Navigation.cs`, `PersistentAgentSession.cs`, `SessionRuntimeRegistry.cs` | CliSync `sync160.resume-keeps-pending-tools-...`, `sync160.resume-drops-pending-tools-...`, `sync160.tree-navigation-keeps-pending-tools-...`, `sync160.reload-keeps-pending-tools-...`, `sync160.reload-publications-of-one-reservation-form-one-reloaded-loadout`; McpParity `sync160.resume-keeps-the-loaded-tool-when-only-an-unrestored-tool-is-deactivated` |
| `extensions/mcp/index.ts`: `mcpServerSummary(description, instructions)` | ported against a local helper | `src/PiSharp.Extensions.Abstractions/Mcp/Configuration/McpServersSection.cs` (private `ServerSummary`, marked `MERGE(sync-1.6.0)`) | McpParity `sync160.servers-section-summary-is-the-library-server-summary` |
| `core/extensions/loader.ts`, `runner.ts`, `types.ts`: `pi.events` handler errors (#11264) | ported (helper), for the Node host and for native extensions | `tools/NodeBridge/pi-host/runtime.mjs`, `PiExtensionHost.Requests.cs`, `NativeExtensionActivation.cs`, `NativeExtensionActivation.Pi.cs`, `ExtensionRegistry.EventBus.cs`, `ExtensionRegistry.cs` (new public `EventBusErrorReporter`), `RegistrationScope.cs` | ExtensionParity 8 `sync160.*events-handler-error*` cases; SdkEventBus 5 and SdkAsyncEventBus 3 `sync160 ...` controls |
| `core/package-manager.ts`, `resource-loader.ts`, `main.ts`: captured package command output, `Installing <source>...`, non-prompting git/ssh (#11264) | ported (helper) | `PiPackageProcesses.cs`, `PiPackageRunOutput.cs` (new), `PiPackageManager.cs`, `PiPackageCommands.cs`, `PiExtensionRun.cs`, `PiCommand.cs` | ExtensionParity 8 `sync160.package.*` cases |
| `package-manager-cli.ts` `getManagedReleaseVerifyEnv` (#12048, `pi update` on Windows managed installs) | no-op | PiSharp has no managed npm release installer (`PiSelfUpdate.cs` runs a configured command) | none |
| `utils/changelog.ts`: unreadable changelog prints nothing (#11264) | ported | `src/PiSharp.Cli/Interactive/Mode/Utilities/Changelog.cs` (the `warn` parameter is gone) | InteractiveParity `sync160.changelog.unreadable-changelog-has-no-entries-and-prints-nothing` |
| `core/session-manager.ts` `buildSessionInfo` (#11956) | closing: already-ported; cancellation: ported (helper) | `src/PiSharp.Cli/Pi/PiSessions.cs` (optional `CancellationToken` on `List`, `ListAll`) | SessionParity 5 `sync160.session-list-*`/`sync160.session-catalog-*` cases |
| `utils/flush-stdout.ts`, `cli/config-selector.ts`, `cli/session-picker.ts`, `cli/startup-ui.ts`, exit paths (#12133) | no-op | Node drops writes still queued in `process.stdout` at `process.exit()`. Native terminal writes are synchronous (`RawTerminalIo.Write`), and the entry flushes its writers before it returns (`InteractiveModeHost.RunAsync`, `PiCommand.RunAsync`), so nothing is queued at exit | none (`flush-stdout.test.ts` tests Node's queue) |
| `interactive-mode.ts`: resume command on its own line (#12237) | ported | `src/PiSharp.Cli/Interactive/Mode/InteractiveModeHost.cs` | InteractiveParity `sync160.exit.resume-command-is-on-its-own-line` |
| `session-share.ts`: `/share` crash when the GitHub CLI fails to start | already safe (a failed start was a null exit code, shown as `Unknown error`); the start error is now the message, as upstream | `src/PiSharp.CodingAgent/Export/SessionShare.cs` (`SystemShareProcessRunner`) | SessionParity `sync160.share.gist-start-failure-reports-the-spawn-error`, `sync160.share.a-program-that-cannot-start-yields-its-error-not-an-exception` |
| `extensions/llama/provider.ts` default URL 9931 (#12001) | ported (helper) | `src/PiSharp.Cli/Llama/LlamaProvider.cs`, `src/PiSharp.Cli/docs/llama-cpp.md` | CliParity `sync160.llama.default-server-url-is-9931` and updated `llama.*` cases; InteractiveParity `e2e.llama.login-stores-the-server-url` (expectation updated) |
| `provider.ts` decision classifiers with image input (#12282) | ported (helper) | `LlamaProvider.cs` (`ToClassifierModel`) | CliParity `sync160.llama.decision-models-with-image-input-send-data-urls`; Classifiers `sync160.llama.*` (3 cases) |
| `provider.ts` drops `supportsDeveloperRole: false` | deferred | Needs pi-ai's `detectCompat` default (#12134), which `ai-providers` ports; removing it now sends the `developer` role | guard: CliParity `sync160.llama.reasoning-models-send-the-system-role` |
| `packages/ai/src/utils/retry.ts` patterns (#12064, #12128, #12124) in the coding-agent copy | ported | `src/PiSharp.CodingAgent/Configuration/AgentRetryPolicy.cs` (the pattern lists equal v1.6.0; header moved) | CodingAgent 5 `sync160 retry policy ...` cases |
| `cli/args.ts` help: Qwen Token Plan Team and Individual key variables | ported | `src/PiSharp.Cli/Pi/PiHelp.cs` | CliParity `sync160.help.names-the-qwen-token-plan-team-and-individual-key-variables` |
| `core/model-resolver.ts`: `resolveModelScope` (printing wrapper) removed | no-op | The native resolver always returned diagnostics | none |
| `core/model-resolver.ts`: `restoreModelFromSession` removed | deferred | `ModelResolver.RestoreModelFromSession` has no production caller; only `tests/PiSharp.Models.Tests/ResolverCases.cs` (`RestoreModel`) calls it. Left in place because `ai-catalog` edits the same file's default-model table and that suite | none |
| `core/model-resolver.ts` default model ids | not mine | `ai-catalog` | none |
| `core/remote-catalog-provider.ts` | no-op | Comment only | none |
| `utils/deprecation.ts` (removed) | no-op | No native counterpart; no caller upstream | none |
| Standalone binary's extension npm dependency resolution (#12012) | no-op | Upstream change is `scripts/build-binaries.sh` and `package.json` for the Bun binary; native extensions run in Node, which resolves `main`/`exports` itself | none |
| `install-lock/package-lock.json`, `install-lock/package.json`, `package.json` | no-op here | Upstream npm installer lock and dependency versions; Node runtime pins are `meta`'s | none |
| `examples/extensions/gondolin/index.ts` | no-op | Upstream example extension (find glob semantics); PiSharp ships no copy | none |
| `docs/rpc-commands.md`, `docs/extensions.md`, `docs/tui.md`, `docs/providers.md`, `docs/models-configuration.md`, `docs/nvidia-rtx-llama-cpp.md` | no-op in my paths | PiSharp ships only `src/PiSharp.Cli/docs/codemode.md` and `llama-cpp.md` (updated); website copies are root's | none |

## Surface for `cli-ui`

- **Replacement diagnostics in the chat.** `PiEntryOptions.RuntimeDiagnostics` (`PiSharp.CodingAgent.AgentSessionRuntimeDiagnostics`) holds
  the current runtime's diagnostics. The startup ones are already taken and are in `PiEntryOptions.StartupDiagnostics`. After every
  session replacement (`/new`, `/resume`, `/fork`, import, an extension's `newSession`/`switchSession`/`fork`) the session host
  replaces them before it publishes the `session_switched` record, and in interactive mode (`ExtensionMode == "tui"`) it writes
  nothing to stderr. Interactive mode should call `options.RuntimeDiagnostics.TakeUnreported()` after it rebinds to the new session
  (on `session_switched`, or after the replacing command returns) and show each entry as upstream `showRuntimeDiagnostics` does:
  `error` -> `ShowError`, `warning` -> `ShowWarning`, otherwise `ShowStatus`. This call is not wired yet; until it is, replacement
  diagnostics in interactive mode are collected but not shown.
- **Model fallback at startup.** A fallback to another model is now in `StartupDiagnostics` (a warning), and
  `Selection.FallbackMessage` is null in that case. `InteractiveModeHost` still passes `Selection.FallbackMessage` as
  `ModelFallbackMessage`, which is now only the login guidance of a session without any model. No change is needed for startup.
- **Dropped reasoning notice.** No new data path: the notice reads `diagnostics` on the assistant message, as the Anthropic notice
  does (`InteractiveMode.Events.cs`, `anthropic_input_transformations`). The OpenAI diagnostic (`openai_input_transformations`, entries
  of type `reasoning` with a `reason`) is produced by `ai-providers`.
- **Session picker.** `PiSessions.List(cwd, sessionDir, agentDir, token)` and `ListAll(sessionDir, agentDir, token)` accept the
  loader's token; `InteractiveMode.Selectors.cs` should pass it (today the token only reaches `Task.Run`).
- **Package installs.** Nothing to do: `PiCommand` stops the `Installing ...` announcements before `host.RunInteractive`.
- **Files I changed under `src/PiSharp.Cli/Interactive/**`** (small, because the native code for these upstream utilities lives
  there): `Mode/Utilities/Changelog.cs` (no warning; `ParseChangelog(path)` lost its unused `warn` parameter) and
  `Mode/InteractiveModeHost.cs` (resume command line). Cases: `tests/PiSharp.InteractiveParity.Tests/Sync160CliSessionCases.cs`;
  `tests/PiSharp.InteractiveParity.Tests/LlamaCases.cs` has one updated expectation.

## Deferred

1. `supportsDeveloperRole: false` stays in the llama.cpp chat model compat until `ai-providers` ports #12134. Then remove the field
   and its comment in `LlamaProvider.cs` `ToChatModel`, remove `"supportsDeveloperRole":false,` from the two pinned JSON strings in
   `tests/PiSharp.CliParity.Tests/LlamaCases.cs`, and move the file header; `sync160.llama.reasoning-models-send-the-system-role` must
   stay green.
2. `ModelResolver.RestoreModelFromSession` and its authored case in `PiSharp.Models.Tests` can be deleted after `ai-catalog` merges.
3. Interactive display of replacement diagnostics (see the surface above) is `cli-ui`'s.
4. `PiSessions` still lists sequentially without upstream's `onProgress`; not part of this delta.

## For the merge, root and other packages

- **core-small:** replace the private `ServerSummary` in `McpServersSection.cs` (marked `MERGE(sync-1.6.0)`) with the ported
  `mcpServerSummary`. The port must use the JavaScript trim (`McpJson.JsTrim`), or two rows of the McpParity summary case fail.
- **ai-providers:** #12134 (see deferred 1). `AgentRetryPolicy.cs` (coding-agent copy of `retry.ts`) is brought to v1.6.0 here; if
  `ai-providers` also edited it the lists must stay equal to upstream's. The Durable copy (`src/PiSharp.Durable/Internal/PiAi/Retry.cs`)
  is not touched here.
- **ai-catalog:** `ProviderAuthCatalog.cs` still has the old Qwen Token Plan wording; the help text here already names the four new
  variables, which only work once the catalog knows them.
- **Root, integrity:** changed files that the integrity tables list include `NativeExtensionActivation.cs`, the three
  `src/PiSharp.Extensions.Runtime/Registration/*` files, both Sdk event-bus suites' `Program.cs`, and `src/PiSharp.Cli/docs/llama-cpp.md`;
  `tools/NodeBridge/pi-host/runtime.mjs` changed too. New test files may need adding.
- **Root, changelog and docs:** new public API `AgentSessionRuntimeDiagnostics`/`AgentSessionRuntimeDiagnostic` (PiSharp.CodingAgent)
  and `ExtensionRegistry.EventBusErrorReporter` (a host that does not set it no longer gets `Event handler error (...)` on stderr for
  owner listeners). Website pages that still say port 8080 for llama.cpp: `website/src/content/docs/docs/guides/llama-cpp.md`.
  `docs/rpc-commands.md` upstream now says `get_last_assistant_text` skips textless aborted, failed and tool-only messages.
- **Root, version labels:** suite report constants (`sourceSha`, profile strings) still name older upstream commits; left alone.
- **Behavior changes worth a changelog line:** `/reload` outside interactive mode now sends git/npm output to stderr instead of
  discarding it; `--help`/`--list-models` on a terminal announce installs on stderr; the model fallback warning now appears on
  stderr in print, JSON and RPC modes.

## Observations outside this delta (not changed)

- RPC `fork` after `switch_session` under `--no-session` fails with `Session branch publication failed`; seen while writing a case,
  not compared with the base commit.
- `McpServersSection.Truncate` can split a surrogate pair (upstream uses `sliceHead`).
- Package commands in print, JSON and RPC modes still inherit stdin (upstream ignores it when stdout is taken over).
- After a session switch the Node extension runtime is the same one, so a `pi.events` handler that fails later reports into the
  current session; the drop applies to reload, shutdown and disposal.

## Suites

All through the pinned SDK (`artifacts/sample-tools/dotnet/dotnet.exe`, 10.0.401), Release, on Windows, on the final sources
(`PiSharp.Cli` built at the default version). No case failed, so nothing had to be compared with the base commit.

| Suite | Cases | Pass | Fail | Skip | Notes |
| --- | --- | --- | --- | --- | --- |
| PiSharp.CliParity.Tests | 237 | 226 | 0 | 11 | skips: Unix-only and session-format cases that need the installed Pi runtime |
| PiSharp.InteractiveParity.Tests | 752 | 746 | 0 | 6 | |
| PiSharp.ExtensionParity.Tests | 177 | 165 | 0 | 12 | skips: no Pi runtime fixture, no production runtime, ancestor skills |
| PiSharp.CodingAgent.Tests (`--dotnet-host`, `--cli`) | 922 | 872 | 0 | 50 | the 50 skips need `artifacts/extensions/published-fixtures`, as the plan says |
| PiSharp.Rpc.Tests | 140 | 140 | 0 | 0 | |
| PiSharp.SessionParity.Tests | 134 | 134 | 0 | 0 | |
| PiSharp.Sessions.Tests | 138 | 138 | 0 | 0 | |
| PiSharp.CliSync.Tests | 73 | 73 | 0 | 0 | |
| PiSharp.McpParity.Tests | 49 | 49 | 0 | 0 | |
| PiSharp.McpSync.Tests | 64 | 64 | 0 | 0 | |
| PiSharp.ToolSearch.Tests | 25 | 25 | 0 | 0 | |
| PiSharp.Classifiers.Tests | 66 | 66 | 0 | 0 | |
| PiSharp.ModelRuntime.Tests | 22 | 22 | 0 | 0 | |
| PiSharp.Models.Tests | 124 | 124 | 0 | 0 | |
| PiSharp.SdkEventBus.Tests | 19 | 19 | 0 | 0 | |
| PiSharp.SdkAsyncEventBus.Tests | 23 | 23 | 0 | 0 | |
| PiSharp.DurableCli.Tests | 119 | 119 | 0 | 0 | uses `PiHelp.Text` |
| PiSharp.ExtensionHost.Tests | 25 | 25 | 0 | 0 | |
| PiSharp.ProviderSync.Tests | all | all | 0 | 0 | exit 0, `"failures":0`; uses `AgentRetryPolicy` |
| PiSharp.OpenAICompatibilityGap.Tests | 588 assertions | pass | 0 | 0 | uses `AgentRetryPolicy` |
| PiSharp.Durable.Tests `--only piai.retry` | 5 | 5 | 0 | 0 | the parity of the Durable retry copy with `AgentRetryPolicy`; the rest of that suite was not run |

Not run: the other suites under `tests/` (they do not reference the changed code paths, or belong to other packages), the Unix
branches of the new cases, and any real git, npm, ssh, GitHub CLI, llama-server or provider call. Interactive mode was exercised
only through InteractiveParity's virtual terminal.
