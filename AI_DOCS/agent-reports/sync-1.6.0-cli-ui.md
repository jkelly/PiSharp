# cli-ui: Pi 1.5.2 -> 1.6.0

Branch `sync/1.6.0-cli-ui`, from `c0eb0bcd` (the integration branch with `tui`, `cli-tools` and `cli-session` merged). Upstream v1.6.0
`faac585884ae2c013a6b1400f3e9e77f10b34bac`, compared with v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`. Scope:
`packages/coding-agent/src/modes/interactive/**` and `packages/coding-agent/src/cli/{config-selector,session-picker,startup-ui}.ts`.
Host: Windows 11, pinned SDK. Nothing was pushed.

Every upstream change in the scope is ported, already ported by an earlier package, or has no native counterpart. Nothing is deferred.
What could not be exercised on this host is real `SIGTSTP` delivery (see "Not verified").

## Commits

| Commit | Subject |
| --- | --- |
| `2b928046` | Highlight the top match for a search query and keep it on cursor keys (Pi 1.6.0 #12245, #12246) |
| `54b1f8fd` | Keep the /resume selection through a reload and select the top match for a query (Pi 1.6.0 #12106, #12245) |
| `b5928571` | Show a transcript notice when OpenAI drops reasoning items, and name the reasons (Pi 1.6.0) |
| `4255e6d6` | Show the runtime's diagnostics in the chat after a session replacement (Pi 1.6.0 #11264) |
| `46a51e75` | Suspend like Ctrl+Z on SIGTSTP from outside (Pi 1.6.0 #12283) |
| `04e9beb0` | Color unscoped highlighted text with the theme foreground (Pi 1.6.0 #11092) |
| `7f77ec46` | Cover reused render output and very large edit diffs; move headers to Pi 1.6.0 (#9549, #8036) |
| (this file) | Add the cli-ui 1.6.0 report |

The suites ran on the final tree. Each of the seven code commits builds `PiSharp.Cli` and `PiSharp.InteractiveParity.Tests` with 0
errors and 0 warnings (checked from an export of each commit).

## Status per upstream source

New cases are in `tests/PiSharp.InteractiveParity.Tests`, one file per commit: `Sync160UiSelectorCases.cs`,
`Sync160UiSessionSelectorCases.cs`, `Sync160UiNoticeCases.cs`, `Sync160UiDiagnosticsCases.cs`, `Sync160UiSuspendCases.cs`,
`Sync160UiHighlightCases.cs` and `Sync160UiRenderCases.cs` (32 cases). All ids start with `sync160.ui.`; the table leaves that prefix
out. The suite finds case classes by reflection, so `Program.cs` did not change.

| Upstream | Status | Native files | Cases |
| --- | --- | --- | --- |
| `components/oauth-selector.ts`: a query highlights the top row (#12245, #12246); a cursor key that leaves the query unchanged does not filter again | ported | `Interactive/Mode/Components/OAuthSelector.cs` (`FilterProviders`, `HandleInput`) | `oauth.selects-the-best-match-after-typing-a-query-even-after-scrolling`, `oauth.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged`, `oauth.native-clearing-the-query-keeps-the-row` |
| `components/scoped-models-selector.ts`: a new query selects the top row; cursor keys keep the selection | ported | `Components/ScopedModelsSelector.cs` (`HandleInput`) | `scoped-models.selects-the-best-match-after-typing-a-query-even-after-scrolling`, `scoped-models.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged` |
| `components/model-selector.ts`: cursor keys keep the selection | ported | `Components/ModelSelector.cs` (`HandleInput`) | `model.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged` |
| `components/session-selector.ts` `handleSearchInput`: a changed, non-empty query selects the top row and clears `selectionTouched`; cursor keys keep the selection (#12245) | ported | `Components/SessionSelector.cs` (`SessionList.HandleSearchInput`) | `session.selects-the-best-match-after-typing-a-query-even-after-scrolling`, `session.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged`, `session.keeps-the-top-row-selected-when-sessions-arrive-after-typing-a-query` |
| `components/session-selector.ts` `pendingSelectedPath`, `setSessions(..., partial)`: the selected session stays the target while a progressive reload has not delivered it (#12106) | ported | `Components/SessionSelector.cs` (`SessionList.SetSessions`, `LoadScope`, `ToggleScope`) | `session.keeps-the-next-session-selected-when-a-progressive-reload-after-delete-starts-with-another-session`, `session.keeps-the-renamed-session-selected-when-the-reload-starts-with-another-session`, `session.native-a-complete-list-drops-the-pending-target` |
| `interactive-mode.ts` `showSessionSelector`: the loaders pass the abort signal to `SessionManager.list`/`listAll` (unchanged upstream; the native loaders only gave the token to `Task.Run`) | ported | `Interactive/Mode/InteractiveMode.Selectors.cs` (`SessionLoaders`, new, internal static; passes the token to `PiSessions.List`/`ListAll`) | `session.native-the-resume-listing-stops-when-its-load-is-cancelled` |
| `components/settings-submenu.ts` `SelectSubmenu`: cursor keys keep the selection | ported | `Components/SettingsSubmenu.cs` | `settings-submenu.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged` |
| `components/settings-submenu.ts` `SteppedSubmenu.render` returns read-only lines | already-ported | `Components/SettingsSubmenu.cs` (`IReadOnlyList<string> Render`, package `tui`) | builds |
| `components/config-selector.ts` `ResourceList`: cursor keys keep the selection (`pi config`) | ported | `Components/ConfigSelector.cs` | `config.keeps-the-selection-when-a-cursor-key-leaves-the-query-unchanged` |
| `components/thinking-selector.ts`: a cursor key does not rebuild the list | ported | `Components/ThinkingSelector.cs` | `thinking.native-a-cursor-key-does-not-rebuild-the-list` (upstream has no case) |
| `/settings` and the per-model settings model picker (`SettingsList` in `packages/tui`) | already-ported | `src/PiSharp.Tui/Pi/SettingsListAndImage.cs` (package `tui`); the per-model picker is a `SelectSubmenu` (row above) | Tui.Tests `sync160.settings-list.cursor-keys-keep-selection` |
| `interactive-mode.ts` `REASONING_DROP_DIAGNOSTICS`, `getDroppedReasoning`, `maybeShowThinkingDropNotice`: OpenAI notice, reasons in both notices, comparison only with a previous response of the same diagnostic kind | ported | `Interactive/Mode/InteractiveMode.Events.cs` (`ReasoningDropDiagnostic`, `DroppedReasoning`, `GetDroppedReasoning`, `ReasoningDropNotice`, `MaybeShowThinkingDropNotice`) | `notice.anthropic-thinking-drops-name-the-reason`, `notice.does-not-repeat-unchanged-anthropic-thinking-drops`, `notice.shows-openai-reasoning-removals-with-their-reasons`, `notice.shows-openai-reasoning-removals-only-when-new-items-are-removed`, `notice.native-untyped-diagnostics`, `notice.e2e-anthropic-notice-is-shown-for-new-drops-when-cache-miss-notices-are-on`, `notice.e2e-no-notice-while-cache-miss-notices-are-off` |
| `interactive-mode.ts` `showRuntimeDiagnostics`; `setRebindSession` shows `takeUnreportedDiagnostics()` after the rebind; startup uses the same method (#11264) | ported | `Interactive/Mode/InteractiveMode.cs` (`ShowRuntimeDiagnostics`, startup), `InteractiveMode.Events.cs` (`OnSessionSwitchedAsync`), `InteractiveModeContext.cs` (`TakeUnreportedRuntimeDiagnostics`), `InteractiveModeHost.cs` (wired to `PiEntryOptions.RuntimeDiagnostics.TakeUnreported()`) | `diagnostics.replacement-diagnostics-are-shown-in-the-chat-by-type`, `diagnostics.model-scope-warning-once-at-startup-and-a-new-problem-once-after-new`, `diagnostics.native-a-startup-problem-is-in-the-chat-once` |
| `interactive-mode.ts` `setSuspendSignalHandler(() => this.handleCtrlZ())` before `ui.start()` and on a replaced renderer (#12283) | ported | `Interactive/Mode/InteractiveMode.cs` (`InitAsync`, `SwitchTuiMode`) | `suspend.sigtstp-from-outside-takes-the-ctrl-z-path-also-after-a-renderer-switch` |
| `interactive-mode.ts` `flushStdout()` before the three `process.exit` calls (#12133) | no-op | Node drops writes still queued in `process.stdout` at exit. Native terminal writes are synchronous (`RawTerminalIo.Write` loops over `write(1, ...)` under a lock), as `cli-session` recorded | none |
| `interactive-mode.ts`: resume command on its own line (#12237) | already-ported | `Interactive/Mode/InteractiveModeHost.cs` (package `cli-session`) | `sync160.exit.resume-command-is-on-its-own-line` |
| `components/tool-execution.ts`: append in a loop instead of a spread (#8036) | no-op, regression case added | `Components/ToolExecution.cs` uses `List.AddRange`, which has no argument limit; the diff path (`Diff.cs`, `Text` wrapping) has no recursion over lines. A comment names the upstream change | `tool-execution.native-a-very-large-edit-diff-renders` (two 12 MB lines at 100 columns, the diff upstream verified by hand: about 240,000 wrapped lines, rendered twice) |
| `components/tool-execution.ts`, `visual-truncate.ts`, `themed-text.ts`, `bash-execution.ts`: read-only render types | already-ported | package `tui` changed the return types and `VisualTruncate.SliceLast`; `VisualLinePreview` and `BashExecutionComponent.CollapsedPreview` cache a list of their own | builds |
| `theme/theme.ts` `buildCliHighlightTheme`: `default` formatter with the theme's `text` color (#11092) | ported | `Interactive/Mode/Theme/SyntaxHighlight.cs` | `highlight.colors-each-line-of-python-docstrings-independently`, `highlight.colors-unscoped-highlighted-code-with-the-theme-foreground`; `highlight.cli-theme-scopes-and-language-validation` (expectation updated, below) |
| `components/assistant-message.ts`, `user-message.ts`: OSC 133 zone lines cached per source list | already-ported | `Components/AssistantMessage.cs`, `Components/UserMessage.cs` (`zoneLines`, package `tui`) | upstream's two new tests ported here: `assistant-message.adds-osc-133-zone-markers-once-across-repeated-renders`, `user-message.adds-osc-133-zone-markers-once-across-repeated-renders` |
| `components/radius-login-selector.ts`: copy before replacing the animated line | already-ported | `Components/RadiusLoginSelector.cs` (package `tui`) | existing `set.radius.shimmers-selected-option` |
| `session-share.ts`: `/share` when the GitHub CLI fails to start | already-ported | `src/PiSharp.CodingAgent/Export/SessionShare.cs` (`SystemShareProcessRunner`, package `cli-session`) | SessionParity `sync160.share.gist-start-failure-reports-the-spawn-error`, `sync160.share.a-program-that-cannot-start-yields-its-error-not-an-exception` |
| `cli/config-selector.ts`, `cli/session-picker.ts`, `cli/startup-ui.ts`: `flushStdout()` after `ui.stop()` (#12133) | no-op | `Interactive/Mode/StartupUi.cs` is the native port of all three; its `ui.Stop()` writes synchronously before `Environment.Exit` or the return | none |
| `cli/args.ts`: Qwen Token Plan key variables in the help | already-ported | `src/PiSharp.Cli/Pi/PiHelp.cs` (package `cli-session`) | CliParity `sync160.help.names-the-qwen-token-plan-team-and-individual-key-variables` |
| Read tool `imageUnsupportedByModel` warning | already-ported | `Components/ToolRenderers/Read.cs` (package `cli-tools`) | `sync160.read-image-warning.*` |
| Unreadable changelog prints nothing | already-ported | `Interactive/Mode/Utilities/Changelog.cs` (package `cli-session`) | `sync160.changelog.unreadable-changelog-has-no-entries-and-prints-nothing` |

### The OpenAI diagnostic the notice reads

The provider side is `ai-providers`' and is not in this tree. The shape is taken from upstream
`packages/ai/src/api/openai-responses-shared.ts` `appendReasoningRemovals` at v1.6.0, and it is carried like the Anthropic one: an entry
of the assistant message's `diagnostics` array.

```json
{
  "type": "openai_input_transformations",
  "timestamp": 1790000000000,
  "details": {
    "transformations": [
      { "type": "reasoning", "id": "rs_1", "action": "removed", "reason": "prefix_mismatch" }
    ]
  }
}
```

- The provider writes only entries with `type: "reasoning"`, `action: "removed"` and a `reason` other than `turn_boundary`, each
  projected to `type`, `id`, `action`, `reason`.
- The notice reads, as upstream does: the first diagnostic (in order) whose `type` is `anthropic_input_transformations` or
  `openai_input_transformations` and whose `details.transformations` holds at least one object with `type` `thinking_dropped`
  (Anthropic) or `reasoning` (OpenAI). The count is the number of those objects; the reasons are their distinct string `reason`
  values in first-seen order. `action` and `id` are not read. If `ai-providers` writes another shape, `GetDroppedReasoning` in
  `InteractiveMode.Events.cs` is the one place to change.
- Behavior change from 1.5.2 that upstream made and that is ported: counts are no longer summed over several diagnostics of one
  message, and a previous response only suppresses the notice when it reports the same kind of diagnostic.

### Choices that are not a line-by-line copy

- **Where replacement diagnostics are shown.** Upstream shows them in the `setRebindSession` callback. The native mode rebinds in
  `OnSessionSwitchedAsync` (the `session_switched` record of the in-process RPC host); the session host replaces
  `PiEntryOptions.RuntimeDiagnostics` before it publishes that record, so the take is at the end of the rebind, before the
  generation waiters are completed. The command's own line (`New session started`, `Resumed session`, ...) therefore follows the
  diagnostics, as upstream. This covers `/new`, `/resume`, `/fork`, `/clone`, `/import` and replacements an extension starts.
- **Startup.** The entry already took the startup diagnostics into `StartupDiagnostics` (`cli-session`). `RunAsync` shows those and
  then also takes from the runtime view, which is empty unless something was added after the entry's take.
- **`InteractiveModeContext.TakeUnreportedRuntimeDiagnostics`** is the seam (default: none); `InteractiveModeHost` wires it. The mode
  does not see `PiEntryOptions`.
- **Suspend.** Both sites pass `HandleCtrlZ`, the existing Ctrl+Z path (`context.Suspend` -> `PosixSuspend.Run`). On Windows the
  TUI registers nothing, so the handler is never called there by a signal.
- **`SessionLoaders`.** The two loader lambdas of `ShowSessionSelector` became an internal static method so that a case can cancel
  them.

## Existing expectation changed

`highlight.cli-theme-scopes-and-language-validation` (`tests/PiSharp.InteractiveParity.Tests/HighlightCases.cs`): text outside every
scope (` x = `, `;`) and text in a scope the theme does not map (`# Title`, `hljs-section`) is now wrapped in the theme's `text`
color. Upstream changed the same expectation in `syntax-highlight.test.ts` (`"after"` became `${theme.getFgAnsi("text")}after\x1b[39m`).
No other existing case changed, and none was removed.

## Upstream cases not ported one to one

| Upstream case | What was done |
| --- | --- |
| `interactive-mode-assistant-diagnostics.test.ts` (4 cases) call `maybeShowThinkingDropNotice` with a fake `this` | The text and the comparison are the static `InteractiveMode.ReasoningDropNotice` (4 cases with the upstream messages and expectations); the `showCacheMissNotices` gate and the chat line run through the real mode and the Anthropic transport in two e2e cases. There is no e2e case for the OpenAI notice because the provider that writes the diagnostic is not in this tree |
| `session-selector-path-delete.test.ts`: deletes real files with the default delete | The case uses the selector's `DeleteSessionFile` option, as the existing native delete cases do; the selection logic under test is the same |
| `model-scope-session-replacement.test.ts` (RPC, stderr) | The RPC case is `cli-session`'s (CliParity `sync160.diagnostics.unmatched-enabled-models-pattern-is-reported-once-not-again-on-new-session`). The interactive counterpart is `diagnostics.model-scope-warning-once-at-startup-and-a-new-problem-once-after-new` |
| `flush-stdout.test.ts`, `test/fixtures/flush-stdout-exit.ts` | Not ported: they test Node's stdout queue (`cli-session` recorded the same) |
| `auth-url-copy.test.ts`, `tool-renderer-examples.test.ts`, `7829-invalid-settings-warning.test.ts`, `5080-signal-shutdown-extension-cleanup.test.ts`, `config-schemas.test.ts` | Type-only or fixture-only changes (`readonly string[]`, the fake context gains `showRuntimeDiagnostics`, the resume line and the llama.cpp port that `cli-session` covered). `diagnostics.native-a-startup-problem-is-in-the-chat-once` covers the startup path of #7829 |

## Edits outside the owned paths

None in `src/`. `src/PiSharp.Cli/Interactive/**` and `tests/PiSharp.InteractiveParity.Tests/**` are the only directories changed, plus
this report. No version constant changed. No case was added to `tests/PiSharp.CliParity.Tests`: the three `cli/*.ts` pickers have no
behavior change natively.

Source headers moved to `faac5858...` for the files brought to v1.6.0 here or verified against it: `OAuthSelector.cs`,
`ScopedModelsSelector.cs`, `ModelSelector.cs`, `SessionSelector.cs`, `SettingsSubmenu.cs`, `ConfigSelector.cs`, `ThinkingSelector.cs`,
`ToolExecution.cs`, `AssistantMessage.cs`, `UserMessage.cs`, `BashExecution.cs`, `RadiusLoginSelector.cs`, `ThemedText.cs`,
`VisualTruncate.cs`, `Theme/SyntaxHighlight.cs`, `InteractiveMode.cs`, `InteractiveMode.Events.cs`, `InteractiveMode.Selectors.cs`,
`InteractiveModeHost.cs`. Several of them still named the v1.2.0 commit (`06a46693`) although the 1.5.x syncs had edited them; the
move relies on those syncs having ported the earlier deltas, which was not re-audited here. `InteractiveMode.Commands.cs`,
`StartupUi.cs` and `Theme/Theme.cs` have no 1.6.0 change of their own and keep their headers.

## Suites

Pinned SDK commands of the plan, Release, Windows 11, on the final sources (`PiSharp.Cli` at the default version).

| Suite | Cases | Passed | Failed | Skipped | Notes |
| --- | --- | --- | --- | --- | --- |
| `PiSharp.InteractiveParity.Tests` | 787 | 781 | 0 | 6 | 32 new `sync160.ui.*` cases; the six skips are the Unix-only and X11 cases. The flaky `e2e.autocomplete.slash-commands` passed in the three full runs made here |
| `PiSharp.CliParity.Tests` | 237 | 226 | 0 | 11 | the same counts as `cli-session`; no case added |
| `PiSharp.Tui.Tests` (`--dotnet-host`) | 84 | 84 | 0 | 0 | |
| `PiSharp.CliSync.Tests` | 73 | 72 | 1 | 0 | `sync150.kimi-coding-live-route-speaks-chat-completions` ("no developer role"); fails on the base too |
| `PiSharp.CodingAgent.Tests` (`--dotnet-host`, `--cli`) | 923 | 872 | 1 | 50 | `mistral CLI selects every pinned chat row without credential effects` ("Mistral live selection control failed."); fails on the base too. The 50 skips need `artifacts/extensions/published-fixtures` |
| `PiSharp.DurableCli.Tests` | 119 | 119 | 0 | 0 | not in the brief; run because its transcript cases render tool results |
| `PiSharp.SessionParity.Tests` | 139 | 139 | 0 | 0 | not in the brief; run because the HTML export renders highlighted tool output |

The two failures are not from this package:

- Both were run again with `src/PiSharp.Cli/Interactive` at the base commit `c0eb0bcd` (the only source directory this branch changes;
  the two test projects are unchanged): `PiSharp.CliSync.Tests` gave 72 passed and the same 1 failed, and
  `PiSharp.CodingAgent.Tests --filter "mistral CLI selects"` failed the same way.
- The Kimi case is the one `ai-catalog` reported on its branch: it waits for `ai-providers`' `openai-completions` developer-role
  default (#10741). The Mistral case is not in any earlier report: `cli-session` ran this suite on its own branch, without
  `ai-catalog` (922 cases, 0 failed), and `ai-catalog` did not run it. The case compares `LiveSessionSelection.Parse("mistral", id)`
  with every row of the embedded `PiSharp.Cli.Models.mistral.json` shard, which last changed for pi-ai 1.5.0 and still has its 34
  rows. I did not find out which row or field differs; it is outside this package and needs an owner (`ai-catalog` or root).

A mutation check on the new cases: with the seven selector files, `SyntaxHighlight.cs`, the two `SetSuspendSignalHandler` calls, the
two `ShowRuntimeDiagnostics(context.TakeUnreportedRuntimeDiagnostics())` calls and the token arguments reverted, 16 of the 31 cases
that existed then failed, and so did the updated `highlight.cli-theme-scopes-and-language-validation`. The 15 that still passed:
the seven notice cases (their code was not reverted, the cases need its API), the three cases for changes that were already in
place (OSC 133 markers, the large diff), four cases for behavior the old code also had (a cursor key keeping the selection in
`/login`, `/scoped-models` and `/resume`, which can only break once a query selects the top row, and an emptied `/logout` query
keeping its row) and `session.native-a-complete-list-drops-the-pending-target` (a guard against keeping a pending target forever,
which the old code had no way to do).

Not run: every other suite under `tests/`, among them `PiSharp.ExtensionParity.Tests` and the MCP suites, which reference
`PiSharp.Cli` but nothing this branch changes.

## Not verified

- **Real signals on Linux and macOS.** The suspend case replaces `TuiBase.SuspendSignalRegistrar` and calls the listener itself. On
  Windows `HandleCtrlZ` then shows `Suspend to background is not supported on Windows`; the branch that stops the TUI, sends
  `SIGTSTP` to the process group and redraws on `SIGCONT` did not run. A POSIX run should do `kill -TSTP <pid>` on a started
  `pisharp`, check that the shell prompt has no mouse or Kitty keyboard reports, and `fg`.
- **`PosixSuspend`'s resume runs on the signal thread** (`ui.Start()` outside the UI loop). That is the existing Ctrl+Z behavior and
  was not changed; with an external `SIGTSTP` it is now reachable without a key press.
- **The OpenAI notice end to end.** Only the reader was tested, against the upstream shape above.
- **`Could not restore model ...` after `/resume` or `/fork` in the chat.** `cli-session` produces it as a replacement diagnostic
  and tested it over RPC. Here the e2e cases use a settings problem and injected diagnostics of each type; the model fallback
  takes the same path and was not set up as its own interactive case.
- **Extension load failures after `/resume` into another project** (the upstream changelog example) cannot occur natively: a
  replacement keeps the run's extensions (`cli-session` recorded this). The display path is the same as for the settings problem
  the e2e case uses.

## For root and other packages

- **ai-providers:** write the diagnostic in the shape above. When it is merged, an e2e case for the OpenAI notice can be added
  next to `notice.e2e-anthropic-notice-...`.
- **Changelog (root).** The upstream 1.6.0 bullets that this package makes true natively: the OpenAI reasoning notice and the
  reasons in both notices; search highlighting the top match in `/login`, `/logout`, `/scoped-models`, `/resume`; cursor keys not
  moving the highlight in `/login`, `/logout`, `/model`, `/scoped-models`, `/resume`, `/settings`, the per-model model picker and
  `pisharp config`; `/resume` keeping its selection while the list reloads; replacement diagnostics in the chat (with
  `cli-session`); `kill -TSTP` (with `tui`); unscoped highlighted text in the theme's foreground. The #8036 and #12133 bullets
  describe Node failures that the native code did not have.
- **Observation, not changed:** the `--resume` startup picker (`StartupUi.SelectSessionAsync`) gets its all-sessions listing as a
  `Func<ImmutableArray<PiSessionInfo>>` from `PiSessionPlanner` (`PiHost.SelectSession`), so the selector's token still only
  reaches `Task.Run` there. Passing it on needs a signature change in `src/PiSharp.Cli/Pi`, outside this package's paths; a
  cancelled listing there only finishes unobserved on a pool thread.
