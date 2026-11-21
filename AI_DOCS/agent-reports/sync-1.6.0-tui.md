# Pi 1.6.0 sync: `tui` package

Branch `sync/1.6.0-tui`, from `8352a672`. Upstream `packages/tui`, `v1.5.2..v1.6.0` (`b4afdddf..faac5858`). Host: Windows 11, pinned SDK 10.0.401.

Every upstream change in `packages/tui` is ported. One native gap that the diff does not contain was fixed on the way, because an upstream 1.6.0 test needs it (the Markdown blank line after a paragraph, below). Nothing was pushed.

## Commits

| Commit | Subject |
| --- | --- |
| `26485df2` | Make TUI render output read-only and reuse it across frames |
| `2cd65917` | Slice ASCII and standalone characters without the grapheme segmenter |
| `e997eaa7` | Add a SIGTSTP handler that is registered only while the TUI owns the terminal |
| `b8125def` | Remove terminal images under an overlay while it is open |
| `13e9f0e2` | Keep uploaded Kitty images across a fullscreen full redraw |
| `2b8e5551` | Keep rows after a multi-row inline image in Markdown |
| `a79ff4fe` | Submit on Enter only from the slash command name menu |
| `cb19637d` | Keep the SettingsList selection when a cursor key leaves the query unchanged |
| `3c1c7081` | Port the Pi 1.6.0 TUI tests |

Each commit builds `PiSharp.Cli` and `PiSharp.InteractiveParity.Tests` with 0 errors and 0 warnings (checked from an export of each commit). The suites ran on the final tree only.

## Upstream changes

All test cases are in `tests/PiSharp.Tui.Tests/Sync160TuiTests.cs` and start with `sync160.`.

| Upstream | Status | C# | Cases |
| --- | --- | --- | --- |
| Breaking: `Component.render()` returns `readonly string[]` (`tui.ts`, `text.ts`, `image.ts`, `markdown.ts`, `mouse-region.ts`, `scroll-view.ts`, `settings-list.ts`, `tui-alt-screen.ts`) | ported | `IComponent.Render` returns `IReadOnlyList<string>` in `Pi/Tui.cs`; every implementation in `src/PiSharp.Tui` and `src/PiSharp.Cli` follows | `container.native-in-place-child-list-contract`, plus `PiSharp.Cli` and the 23 test projects below building |
| Breaking: `Container.render()` returns the same cached array while its children are unchanged; `renderChildLines`, `dispatchMouseToChildLines` (`tui.ts`) | ported | `ChildLines`, `Container` in `Pi/Tui.cs` | `container.same-lines-while-unchanged`, `container.rerenders-on-children-or-width`, `container.mouse-by-rendered-heights` |
| `Box` uses the shared child lines and an identity cache check (`components/box.ts`) | ported | `Box` in `Pi/Components.cs` | `container.box-reuses-output`, `container.mouse-by-rendered-heights` |
| Fullscreen scrolling re-rendered the whole transcript: `requestViewRender`, `contentChanged`, `LayoutFrame.renderCache`, `reusableOutput` (`tui.ts`, `tui-alt-screen.ts`, `layout.ts`) | ported | `TuiBase.RequestViewRender`, `ConsumeContentChanged` in `Pi/Tui.cs`; `LayoutRenderCache`, `LayoutFrame.RenderCache`, `LayoutEngine.RenderFrame(..., reusableOutput)` in `Pi/Layout.cs`; `Pi/TuiAltScreen.cs` | `layout.reuses-earlier-frame-output`, `alt-screen.scroll-reuses-component-output`, `alt-screen.search-current-across-view-frames` |
| Faster column slicing for ASCII and text without combining characters (`utils.ts`) | ported | `IsStandaloneGraphemeUnit`, `GraphemeRanges`, `SingleUnitWidth`, `SliceWithWidth`, `GetGraphemeCellRange` in `Pi/TextUtils.cs` | `slice-graphemes.box-arrows-cjk`, `slice-graphemes.joined-clusters`, `slice-graphemes.native-fast-path-matches-segmenter` |
| Added `TUI.setSuspendSignalHandler()` (`tui.ts`) | ported | `ITui.SetSuspendSignalHandler`, `TuiBase.SuspendSignalRegistrar` in `Pi/Tui.cs`; forwarded by `TuiReference` in `src/PiSharp.Cli/Interactive/Mode/TuiRenderer.cs` | the seven `suspend-signal.*` cases of `suspend-signal.test.ts`, `suspend-signal.native-default-registrar` |
| Kitty and iTerm2 images covered overlays: `hideImagesUnderOverlay`, `getImageLineExtent`, `stripImageSequences` (`tui.ts`, `terminal-image.ts`) | ported | `HideImagesUnderOverlay` in `Pi/Tui.cs`; `TerminalImage.GetImageLineExtent`, `StripImageSequences`, `ImageLineExtent` in `Pi/TerminalImage.cs` | the nine `overlay-image.*` cases |
| Fullscreen Kitty images disappeared after a full redraw (`tui-alt-screen.ts`) | ported | `TuiAltScreen.DoRender` | `alt-screen.kitty-uploads-survive-full-redraw` |
| Text after a multi-row inline image drew over the image (`components/markdown.ts`) | ported | `Markdown.RenderBlock` in `Pi/Markdown.cs`; `Pi/MarkdownLexer.cs` (see below) | `markdown.hard-breaks-after-inline-image`, `markdown.native-blank-line-after-paragraph` |
| Enter on an autocomplete menu submitted when the completed text started with `/` (`components/editor.ts`) | ported | `Editor.HandleInput`, `IsInSlashCommandNameContext` in `Pi/Editor.cs` | `editor.enter-on-tab-path-menu-completes`, `editor.enter-on-argument-menu-completes`, `editor.enter-on-slash-command-menu-submits` |
| `SettingsList` search moved the selection on cursor keys (`components/settings-list.ts`) | ported | `SettingsList.HandleInput` in `Pi/SettingsListAndImage.cs` | `settings-list.cursor-keys-keep-selection` |
| `README.md`: the render result is read-only | no-op | Upstream package README. The contract is on the `IComponent.Render` documentation comment. `website/src/content/docs/docs/embed/tui.md` still shows the old signature (root owns it). | |
| `package.json` version | no-op | Version metadata, root-owned. | |

Notes on choices that are not a line-by-line copy:

- **Read-only contract.** A component whose output changed must return a new list, not the earlier list with new contents: `Container` compares child output by identity first. I read every `Render` in `src/PiSharp.Cli` and `src/PiSharp.Tui` for a list that is returned and later changed in place and found none; the cached ones (`ArminComponent`, `BashExecutionComponent.CollapsedPreview`, `VisualLinePreview`, `ExtensionRowsComponent`, `Text`, `Markdown`, `Image`, `Box`) replace their list. `Container.Children` is a public list here, so `ChildLines.Render` compares the children on every render and a direct `Children.Insert` is seen.
- **Suspend handler.** The listener is a `PosixSignalRegistration` for `SIGTSTP` that sets `Cancel`, so, like a Node listener, it replaces the default stop. The handler is posted to the `UiLoop` because signals arrive on a runtime thread. On Windows nothing is registered: there is no `SIGTSTP`, and `PosixSignalRegistration` rejects it there. `TuiBase.SuspendSignalRegistrar` is the seam the tests replace.
- **Content-changed flag.** It is an `int` read with `Interlocked.Exchange`, so a `RequestRender` from another thread during a frame is kept for the next one.
- **Width cache.** `SingleUnitWidth` keeps one byte per UTF-16 unit in an array instead of upstream's map.
- **Experimental marking.** The new public members carry `[Experimental("PISHARP_TUI001")]`, as the TUI additions of the 1.5.0 sync do: `ChildLines`, `LayoutRenderCache`, `TuiBase.RequestViewRender`, `TuiBase.ConsumeContentChanged`, `ITui.SetSuspendSignalHandler`, `TuiBase.SetSuspendSignalHandler`, `TuiBase.SuspendSignalRegistrar`, `TerminalImage.ImageLineExtent`, `TerminalImage.GetImageLineExtent` and `TerminalImage.StripImageSequences`. That was my reading of the precedent, not an instruction; removing the attribute later is not a breaking change, adding it later would be. `tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj` suppresses the diagnostic. The changed signatures (`IComponent.Render`, `LayoutFrame`, `LayoutBox.Lines`, `ResolveFakeCursors`) cannot be marked.

## The Markdown lexer fix that is not in the diff

`markdown.test.ts` expects three empty rows between the image and `- one`. The native port gave two. The cause was not the 1.6.0 change: `MarkdownLexer` consumed the line break that ends a paragraph or text token, so a following blank line became a one-character match that was appended to the token instead of a `space` token. marked 18.0.11 stops the paragraph before the line break. Visible effects before the fix: no empty row between a paragraph and a list after a blank source line, no empty row for text that ends in a blank line, an indented line after a blank line joining the paragraph instead of starting a code block, and loose list items losing their empty rows.

`2b8e5551` makes the lexer stop before the line break. To check it I ran Pi v1.6.0's own `Markdown` (a copy of `packages/tui/src` at `faac5858`, with marked 18.0.11 from `upstream-v150/node_modules`, the version 1.6.0 pins) and the native `Markdown` over 2,910 sources built from 23 block kinds and 5 separators, at width 60 without styling. The script is not in the repository.

| | Renders that differ from Pi |
| --- | --- |
| Before the fix | 490 |
| After the fix | 195 |
| Fixed by it | 295 |
| New differences | 0 |

The expectations in `markdown.native-blank-line-after-paragraph` are Pi's output for 16 of those sources.

## Edits outside `src/PiSharp.Tui`

`cli-ui` ports the upstream interactive changes on top of these. Three of them are already the upstream change and can be marked already-ported there.

Behavior-preserving fixes for callers that changed render output in place:

| File | Edit | Upstream counterpart |
| --- | --- | --- |
| `src/PiSharp.Cli/Interactive/Mode/Components/AssistantMessage.cs` | `Render` keeps the OSC 133 zone lines in a `zoneLines` cache keyed by the source list instead of writing into `base.Render` output | `assistant-message.ts` 1.6.0, same change |
| `src/PiSharp.Cli/Interactive/Mode/Components/UserMessage.cs` | same | `user-message.ts` 1.6.0, same change |
| `src/PiSharp.Cli/Interactive/Mode/Components/RadiusLoginSelector.cs` | copies the selector's lines before it replaces the animated line | `radius-login-selector.ts` 1.6.0, same change |
| `src/PiSharp.Cli/Interactive/Mode/Components/VisualTruncate.cs` | `SliceLast` takes `IReadOnlyList<string>` | `visual-truncate.ts` type change |
| `src/PiSharp.Cli/Interactive/Mode/TuiRenderer.cs` | `TuiReference.SetSuspendSignalHandler` forwards to the current TUI | none (PiSharp's stable TUI reference) |

Return type only (`List<string> Render(int width)` to `IReadOnlyList<string> Render(int width)`), no other change:

- `src/PiSharp.Cli/Durable/DurableTuiInteraction.cs` (2)
- `src/PiSharp.Cli/Interactive/Mode/Components/`: `Armin.cs`, `AssistantMessage.cs`, `BashExecution.cs`, `ConfigSelector.cs` (2), `DynamicBorder.cs`, `EasterEgg3d.cs`, `Footer.cs`, `LoginDialog.cs`, `OAuthSelector.cs`, `RadiusLoginSelector.cs`, `SessionSelector.cs` (3), `SettingsSubmenu.cs`, `StatusIndicator.cs`, `ThemedText.cs`, `ToolExecution.cs`, `ToolRenderers/Index.cs`, `TreeSelector.cs` (5), `UserMessage.cs`, `UserMessageSelector.cs`, `VisualTruncate.cs`
- `src/PiSharp.Cli/Interactive/Mode/`: `InteractiveExtensionHost.cs`, `InteractiveMode.cs`, `InteractiveMode.Llama.cs`, `NodeEditorComponent.cs`

The extension bridge (`ExtensionRowsComponent`, `InteractiveExtensionHost`, `NodeEditorComponent`) needed nothing else: it already returns a new list per result.

Tests outside `tests/PiSharp.Tui.Tests`, all in `tests/PiSharp.InteractiveParity.Tests`, no expectation changed:

- Return type of test components: `GapOverlaySnapshotCases.cs`, `GapToolDisposalCases.cs`, `Sync150Cases.cs` (2), `TuiSyncCases.cs` (3).
- `ITui` test doubles gain an empty `SetSuspendSignalHandler`: `MessageComponentCases.cs`, `SettingsLoginFooterCases.cs`, `Sync150CliCases.cs`, `Sync152SmallUiCases.cs`.
- `MessageComponentCases.cs`: `Lines2` takes `IReadOnlyList<string>`. `ToolRenderingCases.cs`: one `.ToList()` before `FindIndex`, one local function return type.

In `tests/PiSharp.Tui.Tests`: `Sync152TuiTests.cs` (return type of its test component), `Program.cs` (registers `Sync160TuiTests`) and `PiSharp.Tui.Tests.csproj` (`NoWarn` for `PISHARP_TUI001`).

## Suites

Built with 0 errors: `src/PiSharp.Cli` (0 warnings) and these 23 test projects (the TUI suites, and every test project that references `PiSharp.Cli` or `PiSharp.Tui` directly and names a component, TUI or layout type): `PiSharp.Tui.Tests`, `PiSharp.Tui.Editor.Tests`, `PiSharp.Tui.SelectList.Tests`, `PiSharp.Tui.NativeModifiers.Tests`, `PiSharp.Tui.Focus.Tests`, `PiSharp.Tui.IndicConjunct.Tests`, `PiSharp.Tui.Keybindings.Tests`, `PiSharp.Tui.Keypad.Tests`, `PiSharp.Tui.KittyAlternate.Tests`, `PiSharp.Tui.LockModifiers.Tests`, `PiSharp.Tui.ModifyOtherKeys.Tests`, `PiSharp.Terminal.SelectList.Integration.Tests`, `PiSharp.InteractiveParity.Tests`, `PiSharp.CliSync.Tests`, `PiSharp.DurableCli.Tests`, `PiSharp.SessionParity.Tests`, `PiSharp.McpSync.Tests`, `PiSharp.McpParity.Tests`, `PiSharp.ProviderApis.Tests`, `PiSharp.Codemode.Tests`, `PiSharp.ExtensionParity.Tests`, `PiSharp.ToolSearch.Tests`, `PiSharp.CodingAgent.Tests`. The whole solution was not built.

Run on the final tree:

| Suite | Passed | Failed | Skipped |
| --- | --- | --- | --- |
| `PiSharp.Tui.Tests` (full, 35 new `sync160.` cases) | 84 | 0 | 0 |
| `PiSharp.Tui.Editor.Tests` (`--report`) | 85 | 0 | 0 |
| `PiSharp.Tui.NativeModifiers.Tests` | 6 | 0 | 0 |
| `PiSharp.InteractiveParity.Tests` (full, 750 cases) | 744 | 0 | 6 |
| `PiSharp.CliSync.Tests` | 68 | 0 | 0 |
| `PiSharp.DurableCli.Tests` | 119 | 0 | 0 |

- `InteractiveParity` has one intermittent case, `e2e.autocomplete.slash-commands` ("Timed out waiting for "Show all keyboard shortcuts""). It passed in the run above and failed in an earlier full run of this branch (743 passed, 1 failed, 6 skipped). It also fails on the base commit: in eight alternating single-case runs it failed 5 times on this branch and 5 times on a build of `8352a672`. It runs in regular mode, which none of the frame reuse touches. I did not look for its cause.
- The six skips are the Unix-only and X11 cases.

Not run:

- `PiSharp.Tui.SelectList.Tests` and `PiSharp.Terminal.SelectList.Integration.Tests` stop at "Fresh report, root, manifest/hash and complete permitted receipt/hash required". They cover the native `TerminalSelectList`, which this package did not change.
- `PiSharp.Tui.Focus`, `IndicConjunct`, `Keybindings`, `Keypad`, `KittyAlternate`, `LockModifiers` and `ModifyOtherKeys` need frozen source observation files as arguments. They cover key decoding and the segmenter, which this package did not change. Built only.
- `PiSharp.SessionParity`, `McpSync`, `McpParity`, `ProviderApis`, `Codemode`, `ExtensionParity`, `ToolSearch`, `CodingAgent`: built only.

## Deferred and not verified

- **SIGTSTP delivery on Linux and macOS was not exercised.** This host is Windows. The cases drive the registration through the seam; `suspend-signal.native-default-registrar` only checks that the real registrar registers nothing on Windows. A POSIX run should send a real `SIGTSTP` to a started TUI once.
- **195 Markdown renders still differ from Pi** in the corpus above, all present before this sync and none in the 1.6.0 diff. Counting a source under each block kind it contains: HTML blocks 78, indented code blocks 55, tables 32, setext headings 18, and 31 with none of those, such as a trailing blank line after a heading and two lists separated by two blank lines. Examples: `para\n| a | b |\n|---|---|\n| 1 | 2 |` (Pi starts a table, native keeps a paragraph), `    indented code\na` (Pi keeps an empty row inside the code block), `<div>html</div>\n\n\n    indented code` (Pi puts an empty row after the HTML block). This needs its own work package with the same differential run.
- **The flaky `e2e.autocomplete.slash-commands` case** above.

## For root and other packages

- **Public API of `PiSharp.Tui` changed**, so the changelog needs a breaking entry: `IComponent.Render` returns `IReadOnlyList<string>` (every external component must change its return type, and must copy render output before changing it); `ITui` gains `SetSuspendSignalHandler(Action?)` (external `ITui` implementations must add it); `LayoutFrame` gains the positional member `RenderCache`; `LayoutBox.Lines` and `LayoutBox.ScrollContentLines` are `IReadOnlyList<string>`; `TuiBase.ResolveFakeCursors` takes `IReadOnlyList<string>`. Additions, all marked `PISHARP_TUI001` (decide whether they stay experimental; if so the `PISHARP_TUI001` row of `website/src/content/docs/docs/embed/sdk.md` and the release notes need them): `ChildLines`, `LayoutRenderCache`, `TuiBase.RequestViewRender`, `TuiBase.ConsumeContentChanged`, `SetSuspendSignalHandler`, `TuiBase.SuspendSignalRegistrar`, `TerminalImage.ImageLineExtent`, `GetImageLineExtent`, `StripImageSequences`. `LayoutEngine.RenderFrame` gains the optional parameter `reusableOutput`.
- **`website/src/content/docs/docs/embed/tui.md`** shows `List<string> Render(int width)` in its `IComponent` row and its example component.
- **Changelog, fixed:** the upstream 1.6.0 `packages/tui` bullets apply as written. One more for PiSharp only: Markdown now leaves an empty row between a paragraph and a list that follows a blank line, as Pi does.
- **`cli-ui`:** wire `ui.SetSuspendSignalHandler(...)` at the two places `interactive-mode.ts` does (`InteractiveModeHost.Suspend` and `PosixSuspend` are the existing Ctrl+Z path). The handler is called on the UI loop. Still to port there: the search-query guards in the selectors (`config-selector`, `model-selector`, `oauth-selector`, `scoped-models-selector`, `settings-submenu`, `thinking-selector`), `session-selector.ts` and `interactive-mode.ts`. `tool-execution.ts`'s loop instead of a spread is a V8 argument limit and has no native counterpart.
- **Source headers** of `Pi/Tui.cs`, `Components.cs`, `Layout.cs`, `TuiAltScreen.cs`, `TerminalImage.cs`, `Markdown.cs`, `SettingsListAndImage.cs`, `Editor.cs` and `TextUtils.cs` now name `faac585884ae2c013a6b1400f3e9e77f10b34bac`. The other `Pi/*.cs` files have no 1.6.0 change and keep their header. No version constant changed.
