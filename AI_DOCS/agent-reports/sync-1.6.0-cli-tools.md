# cli-tools: Pi 1.6.0 sync

Branch `sync/1.6.0-cli-tools`, from `8352a672`. Upstream range v1.5.2 (`b4afdddf`) to v1.6.0 (`faac5858`), paths
`packages/coding-agent/src/core/tools`, `core/exec.ts`, `core/export-html` and their tests. Everything ran on Windows 11 with the
pinned SDK; nothing ran on Linux or macOS (see "Not run").

## Status by upstream change

| Upstream | Status | C# | Cases |
| --- | --- | --- | --- |
| `tools/find.ts`: relative path patterns are anchored at the search directory; `**/x` matches at any depth; no match through folders above the search path (#12066) | ported | `src/PiSharp.Tools/Files/PiFindTool.cs` (`PatternArguments`, `EffectivePattern`, `IsAbsolutePattern`, `EscapeGlob`; Windows matches the path relative to the search path) | Tools: `sync160.find-path-pattern-does-not-match-above-search-path`, `sync160.find-path-patterns-anchor-at-search-path`, `sync160.find-search-path-glob-characters-smart-case-and-link`, `sync160.find-fd-arguments-anchor-and-escape-search-path` |
| `tools/find.ts`: a leading `./` returned nothing (#12020) | ported | `PiFindTool.EffectivePattern` | Tools: `sync160.find-leading-dot-slash` |
| `tools/find.ts`: `pattern` description | ported | `PiFindTool.Declaration` | Tools: `sync160.find-and-grep-pattern-descriptions`, `validation built-in declarations ...` (1.6.0 overlay) |
| `tools/grep.ts`: `glob` description (the one-line change) | ported | `src/PiSharp.Tools/Files/PiGrepTool.cs` (the text was still the v1.5.0 one, not the v1.5.2 one; it is now the 1.6.0 text) | same two cases; CliParity `session-format.alltools` (authored `Pi160TextChanges` for both descriptions) |
| `tools/read.ts`: no stored note that the model does not support images; `details.imageUnsupportedByModel` (#12131) | ported | `src/PiSharp.Tools/Files/ReadWriteTools.cs`, `FileOperations.cs` (`ReadWriteToolOptions.CurrentModelId`), `src/PiSharp.Cli/Commands/OfflineSessionProfile.cs` (passes the model id) | Tools: `sync160.read-image-keeps-text-only-model-warning-out-of-content`; updated `Read ... non-vision` expectation in `ToolParityTests.cs` |
| `tools/renderers/read.ts`: the warning from the detail, also collapsed | ported | `src/PiSharp.Cli/Interactive/Mode/Components/ToolRenderers/Read.cs` | InteractiveParity: `sync160.read-image-warning.collapsed-and-expanded`, `.only-with-the-detail`, `.renderer-text` |
| `tools/read.ts`: uncaught AbortError when cancelled while the file is being opened (#11956) | ported | `ReadWriteTools.ReadAsync` (wrapper around `ReadCoreAsync`) | Tools: `sync160.read-abort-while-opening-is-a-clean-cancelled-result`, `sync160.read-abort-mid-read-stops-reading` |
| `tools/output-accumulator.ts`: output ending in one over-long line returned nothing (#12062) | ported | `src/PiSharp.Tools/Processes/ShellOutputAccumulator.cs` | Tools: `sync160.bash-over-long-last-line-shows-its-end` |
| `core/exec.ts`: 128 + signal number instead of 0 (#12088) | ported | `src/PiSharp.Cli/Extensions/Execution/NativeExtensionExecHost.cs` (`ExitCode`), `tools/NodeBridge/pi-host/runtime.mjs` (`execCommand`), doc comment in `src/PiSharp.Extensions.Abstractions/Facade/Execution/ExtensionExecContracts.cs` | ExtensionParity: `sync160.bridge-exec-signal-terminated-exit-codes`, `sync160.native-exec-signal-terminated-exit-codes`, `sync160.native-exec-exit-code-mapping`; updated `sync150.bridge-exec-max-buffer-utf8-and-spawn-errors` |
| `export-html/ansi-to-html.ts`: OSC 8 links become anchors; other CSI, OSC, APC sequences and stray ESC are dropped; `stripAnsiEscapes` (#12003) | ported | `src/PiSharp.CodingAgent/Export/AnsiToHtml.cs` | SessionParity: `sync160.export-html.osc8-hyperlinks-become-anchors`, `.osc8-style-nests-inside-the-anchor-and-an-open-link-is-closed`, `.unsafe-links-and-other-escape-sequences-are-dropped` |
| `export-html/tool-renderer.ts`: blank-line trimming uses `stripAnsiEscapes` | ported | `src/PiSharp.CodingAgent/Export/ToolHtmlRenderer.cs` | SessionParity: `sync160.export-html.rendered-result-lines-of-escapes-only-are-trimmed` |
| `export-html/template.js`: a click on a link does not toggle expandable output | ported | `src/PiSharp.CodingAgent/Export/Assets/template.js` is the v1.6.0 blob `f7961ffb`; `SessionHtmlExport.AssetBlobs` | SessionParity: `sync160.export-html.template-leaves-link-clicks-to-the-link`, `export-share.assets.byte-identical-to-upstream-blobs`, the three whole-page cases |
| `test/tool-renderer-examples.test.ts` | no-op | type-only (`readonly string[]`) | none |
| `export-html/ansi-to-html.ts`, `tool-renderer.ts`: `readonly string[]` parameters | no-op | TypeScript-only; the C# methods take `IEnumerable<string>` / `IReadOnlyList<string>` | none |
| find through the explicit tool policy (`FindTool` + `FdFindExecutor`) | deferred | see "Deferred" | none |

## How each change was mapped

### find

- A relative path pattern is matched against the path relative to the search directory. On Linux and macOS fd gets
  `--full-path` and the pattern behind the glob-escaped search path, plus `--ignore-case` when the user's pattern is all lower
  case (fd's smart case would otherwise look at the search path's letters). On Windows fd still lists every entry and
  `WindowsFindGlob` matches the relativized result; absolute patterns are matched against fd's absolute path, as upstream.
- One fix beyond the diff, needed for the upstream rule "a trailing `/**` lists what is inside the folder": on Windows
  `WindowsFindGlob` let a wildcard portion match the empty part after a folder's trailing slash, so `src/**` returned `src/`
  itself. minimatch 11.0.3, the version upstream uses, does not; checked by running that package. The fix is one line
  in `WindowsFindGlob.cs` and has a case.
- The tool validation goldens are a 1.1.0 capture and compare descriptions too. The capture file is untouched;
  `ToolValidationTests.cs` lays the two 1.6.0 descriptions over it.

### read

- C# shape of the detail, for `cli-ui`: the read tool's `ToolResult.Details` is the JSON object
  `{"imageUnsupportedByModel":"<model id>"}`; it is absent (`details` undefined) for every other image read. The name is
  `ReadWriteTools.ImageUnsupportedByModelProperty`. A host that reports a model without image input but no id gets
  `ReadWriteTools.UnnamedModel` ("The current model") as the value, so the warning still shows. The id comes from the new
  `ReadWriteToolOptions.CurrentModelId` (`Func<string?>`), next to the existing `CurrentModelSupportsImages`.
  `ReadWriteTools.NonVisionImageNote` is kept but marked `[Obsolete]`; nothing writes it.
- The renderer reads it as `ToolJson.GetString(ToolJson.Get(result, "details"), "imageUnsupportedByModel")` and returns
  `"\n" + theme.Fg("warning", "[<id> does not support images; it saw a placeholder instead]")`: alone when collapsed, appended
  when expanded. `ToolExecutionComponent` needs no change to show it: the new InteractiveParity case renders the component
  collapsed and expanded. Upstream's 1.6.0 change to `tool-execution.ts` is unrelated to this (readonly render type, a loop
  instead of a spread).
- Cancellation: .NET has no unhandled stream `error` event, so PiSharp did not crash. What changed: the read adapter now
  returns its own cancelled result (`ToolFailureKind.Canceled`, "Operation aborted", upstream's rejection text) once the token
  is cancelled, whatever the file operation was doing (also when an adapter's open fails with an I/O error because of the
  abort, which used to read "EIO"), the opened stream is closed, and nothing is read after an abort during open.

### bash output

`Snapshot()` falls back to the whole retained tail when clipping the partial first line leaves nothing. The footer then reads
`[Showing last 50.0KB of line 2 (line is ...)]`. The size in that footer is the open line's byte count, which is `0B` when
the output ends with a newline; that is upstream's `getLastLineBytes()` too and was not changed.

### exec

Two implementations exist, and each reports what Pi reports for the way the process actually ended.

| Case | Pi (Node) | PiSharp bridge `pi.exec` (TypeScript extensions) | PiSharp native `pi.exec` (C# extensions) |
| --- | --- | --- | --- |
| timeout, abort, output over `maxBuffer`; Linux, macOS | 143 (SIGTERM), 137 if the child ignored SIGTERM for 5 s | same as Pi (it is the same Node code) | 137: the runner kills the process group with SIGKILL at once, with no SIGTERM grace period |
| timeout, abort, output over `maxBuffer`; Windows | 143: Node ends the child with `TerminateProcess` and reports the SIGTERM it was asked to send | 143 | 143: the job is terminated with exit code 1, which is mapped to 128 + SIGTERM. Before: 1 |
| killed by someone else; Linux, macOS | 128 + signal (137 SIGKILL, 143 SIGTERM), `killed: false` | same | same: the receipt holds `waitpid`'s 128 + signal |
| ended by someone else; Windows | the exit code Windows recorded (1 for `TerminateProcess`), no signal | same | same |
| command exits by itself | its code | same | its code, also 1 on Windows when not stopped; a code other than 1 seen after a stop is kept (the command ended first) |
| no exit code and no signal | 1 | 1 | never 0: 143 (Windows) or 137 (POSIX) if stopped, otherwise the host already fails the call |

- The bridge is where the upstream bug existed (`code ?? 0`); it is now upstream's expression.
- Native never reported 0 for a stopped command in practice (Windows gave the job's 1, POSIX gave 137); the mapping function
  `NativeExtensionExecHost.ExitCode(receiptCode, killed, windows, endedBeforeStop)` makes the rule explicit, removes the `?? 0`
  fallback and changes Windows from 1 to 143.
- Known limit on Windows native: a command that exits with code 1 by itself in the instant a stop is under way reads 143.
  When the runner saw the exit before a timeout (`ShellExitBeforeTimeout`), the 1 is kept.
- Expectation change: `sync150.bridge-exec-max-buffer-utf8-and-spawn-errors` pinned `code: 1` for commands stopped by the
  output cap. Those are now 143 when SIGTERM ended the child and 1 when it had already exited with 0, so three records accept
  either (upstream asserts only "not 0") and the flooding command is pinned to 143.

### HTML export

- `AnsiToHtml.Convert` handles OSC 8 with ST or BEL terminators, keeps style spans nested inside the anchor, closes an
  unterminated link, and accepts only `http:`, `https:` and `mailto:` after trimming and removing C0 controls. The scheme
  check compares ASCII letters only, as JavaScript's non-Unicode `/i` does (a .NET case-insensitive match would accept the
  Latin long s; there is a case for it).
- `template.js` is embedded byte-for-byte, so the file was replaced with the v1.6.0 blob and the pins moved: git blob
  `f7961ffb1ac9fc4dc6659ac6df97dc2bc777c494`, sha256 `f04404c8...`. The other seven assets already equal their v1.6.0 blobs.
- The five whole-page digests and the page length in `ExportShareCases.cs` are authored pins of PiSharp's own output and
  were recomputed. For the four pages written to files I checked that putting the one changed line back gives the 1.5.0 pin.

## Suites

All runs are on Windows with the pinned SDK commands of the plan, on the final source. Tools, InteractiveParity,
ExtensionParity and CodingAgent ran before the last commit (`71395995`), which changes only CliParity's test file; the others
ran after it.

| Suite | Result | Notes |
| --- | --- | --- |
| `PiSharp.Tools.Tests` | 183 passed, 0 failed, 0 skipped | 173 before; ten new `sync160.*` groups. The find groups need `fd` on PATH (fd 10.4.2 here), as the existing `gap files rg ...` group does |
| `PiSharp.SessionParity.Tests` | 132 cases, 0 failed, 0 skipped | five new `sync160.export-html.*` cases |
| `PiSharp.InteractiveParity.Tests` | 753 cases, 747 passed, 0 failed, 6 skipped | three new `sync160.read-image-warning.*` cases; the skips are four Unix-only and two X11 cases |
| `PiSharp.ExtensionParity.Tests` | 161 cases, 149 passed, 0 failed, 12 skipped | three new `sync160.*exec*` cases; the skips need the Pi runtime fixture (`SKIP_NO_PI_RUNTIME` 9, `SKIP_NO_PRODUCTION_RUNTIME` 1, `SKIP_NO_RUNTIME_FIXTURE` 1, `SKIP_ANCESTOR_SKILLS` 1) and are not mine |
| `PiSharp.CliParity.Tests` | 226 cases, 215 passed, 0 failed, 11 skipped | `session-format.alltools` failed after the description change (the recorded Pi session carries the old texts) and passes with the authored `Pi160TextChanges` overlay; the skips are platform cases |
| `PiSharp.CodingAgent.Tests` (`--dotnet-host`, `--cli`) | 865 passed, 0 failed, 50 skipped | the 50 skips need the published native extension fixtures, as the plan says |
| `PiSharp.Rpc.Tests` | 137 passed, 0 failed | holds the RPC HTML export cases |
| `PiSharp.WireSync.Tests` | 20 passed, 0 failed | uses `ReadWriteTools` |
| `PiSharp.WebAgent.Tests` `--only bench.` and `--only playground.` | 9 and 10 cases, 0 failed | the two groups that use the built-in tool declarations as templates; the rest of that suite was not run |

Not run: every other suite. One of them touches the changed types: `PiSharp.Compatibility.Node.ProcessTests` builds but needs
`--oracle`, `--jiti` and `--reference-repo` paths I do not have (it reads text files through `ReadWriteTools`). No other suite
references `PiFindTool`, `ReadWriteTools`, `ShellOutputAccumulator`, `NativeExtensionExecHost`, `AnsiToHtml` or
`TuiToolHtmlRenderer`.

Two things found while running:

- `tests/PiSharp.CodingAgent.Tests/NativeExtensionExecHostTests.cs` (14 `exec.*` cases), `NativeExtensionExecContextBridgeTests.cs`
  and `NativeExtensionExecInstalledEntryTests.cs` are not registered in that suite's `Program.cs`, so no suite runs them. They
  do not assert an exit code for a stopped command, so this change does not conflict with them, but they did not run.
- The scratchpad directory is shared by the sibling agents of this session. I wrote files with generic names there
  (`run_suites.sh`, `repl.py`, `msg1.txt` to `msg6.txt`, `PiSharp.<Suite>.Tests.out`); `run_suites.sh` already existed when I
  first wrote it, so I replaced another agent's script, and another agent later replaced `msg1.txt`. My commits were made
  before that and carry the right messages. Later files went to a private subfolder.

## Not run

- Linux and macOS. The POSIX fd arguments of find are checked as values (`sync160.find-fd-arguments-...`) and the POSIX exit
  code mapping as a pure function, but no POSIX process ran: the 137 for a native stop, 128 + signal for a command killed by
  someone else, and fd's anchored `--full-path` matching are unverified on a real POSIX system. The POSIX branches of the new
  ExtensionParity cases assert them and will run in CI.
- The linked-search-path find case uses a junction on Windows; it ran. The symlink variant did not.

## Deferred

- **Explicit-policy find** (`src/PiSharp.Tools/Files/FindTool.cs`, `FdFindExecutor.cs`): still prefixes path patterns with `**/`
  and has the old description, so it can match through folders above the search path. No PiSharp host constructs
  `FdFindExecutor` (the CLI uses `PiFindTool`); its fd arguments are pinned by `FdFindExecutorTests` and the 1.5.2 sync left
  it at its capture as well. Anchoring it means choosing new fd arguments for a pinned, hash-admitted fd on Windows, which
  upstream itself gave up on (it matches in JavaScript there). Needs an owner decision: port the Windows matcher into the
  explicit executor, or retire the profile.
- **SIGTERM before SIGKILL in the native runner**: Pi gives a command five seconds to exit on SIGTERM; PiSharp's runner kills
  the group at once, which is why a native stop reads 137 on POSIX where Pi reads 143. Changing it touches the runner the
  bash tool shares and was not part of this diff.

## Observed, outside this range, not changed

- `find`'s `details.truncation.totalLines` / `totalBytes` are those of the stored results, not of all results within the
  limit. Upstream overrides both (`totalLines: resultCount, totalBytes: outputBytes - 1`) since v1.5.1. Only the details of a
  result over 50KB differ; the text is the same.

## Needed from root and other packages

- **root**: integrity re-seal (changed: `tools/NodeBridge/pi-host/runtime.mjs`, `src/PiSharp.CodingAgent/Export/Assets/template.js`,
  the sources above; new: four `Sync160*` test files). Website text that describes the old behavior:
  `website/src/content/docs/docs/reference/tools.md` lines 44 (read note for models without images), 126 (grep `glob`), 140 and
  144 (find pattern and the `--full-path` description) and `website/src/content/docs/docs/embed/coding-agent.md` line 527
  (`CurrentModelSupportsImages` "adds a note"; `CurrentModelId` is new). Changelog entries for the six changes; the
  Windows `pi.exec` code for a stopped command changing from 1 to 143 is visible to C# extensions.
- **meta**: `tools/NodeBridge/pi-host/virtual/coding-agent/tools.mjs` lines 305 and 344 hold the find `pattern` and grep `glob`
  descriptions of an older Pi; the 1.6.0 texts are in `PiFindTool.cs` and `PiGrepTool.cs`. Execution there already goes to the
  native tools.
- **cli-session**: `tests/PiSharp.CliParity.Tests/SessionFormatCases.cs` gained a `Pi160TextChanges` array (two description
  texts) that `FollowPi120` applies; if that package adds 1.6.0 prompt texts there, the arrays merge by union. I also changed
  three lines of `src/PiSharp.Cli/Commands/OfflineSessionProfile.cs` (`ReadOptions` takes and passes
  the model id; the file has mixed line endings, which are preserved), the `execCommand` exit code and one import in
  `tools/NodeBridge/pi-host/runtime.mjs` (its header still names the older commit: only the `exec.ts` part is at 1.6.0), and a
  doc comment in `ExtensionExecContracts.cs`.
- **cli-ui**: I changed `src/PiSharp.Cli/Interactive/Mode/Components/ToolRenderers/Read.cs`, where the port of
  `core/tools/renderers/read.ts` lives, and added `tests/PiSharp.InteractiveParity.Tests/Sync160ReadImageCases.cs` (class
  `Sync160ReadImageCases` plus a partial `ToolRenderingCases.Sync160ReadImage()`). No component change is needed for the
  warning.
- **durable-edge**: `src/PiSharp.Durable.Tools/Tools/Read.cs` is a separate port of `packages/durable/src/tools/read.ts` and
  shares nothing with `ReadWriteTools`; it still adds "The current model does not support images; it sees a placeholder
  instead." to its diagnostic.

## Commits

| Commit | Subject |
| --- | --- |
| `93c8d7c4` | Anchor find path patterns at the search path (Pi 1.6.0) |
| `ba04dfa2` | Keep the text-only model warning out of stored read results (Pi 1.6.0) |
| `1fe055e6` | Show the end of an over-long last line of bash output (Pi 1.6.0) |
| `98b65c67` | Add the Pi 1.6.0 find, read and bash regression cases |
| `3eaf9b36` | Report 128 + signal for pi.exec commands ended by a signal (Pi 1.6.0) |
| `55d85f8c` | Make terminal hyperlinks clickable in the HTML export (Pi 1.6.0) |
| `71395995` | Follow the Pi 1.6.0 find and grep descriptions in the session format case |

This report is the commit after them.
