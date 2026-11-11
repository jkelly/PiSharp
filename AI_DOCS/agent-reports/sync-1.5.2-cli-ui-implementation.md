# CLI small UI implementation for Pi 1.5.2

Source inspected directly: frozen `upstream-v152`, commit `b4afdddf44e059c67ccf5097e5317d913521b470`; baseline `f9a26365568127461c47a07a37dcb42c140a3ac5`. No independent dotnet builds or test execution: root owns serialization. Effective requested worker service tier remains unverified.

## Implemented

- Extension selectors: ten-row centered/clamped window and clipped-list position label.
- Model selector: capture current selected provider/id on the UI-loop refresh completion before replacing the snapshot; preserve it through active-query filtering when present, retaining old fallback behavior otherwise.
- Hugging Face selector: reset navigation intent per changed query, preserve touched result identity on matching active async completion, otherwise select first result; stale/cancelled completion guard retained.
- Tree selector: focusable stored search line, width-zero handling, reserved cursor cell at width one and above using native fake-cursor markers; focus moves to label editor and back on cancel/save.
- Windows/WSL paste defaults: both Alt+V and Ctrl+V; user overrides unchanged.
- Diff: remove shared leading whitespace before word diffing and prepend it to both output lines, avoiding lost indentation with leading insertions.
- Theme: dart/nix/groovy/gradle path mappings. Inspected bundled `hljs-grammars.json.gz`: `languages` contains all three grammars, so no regeneration needed.
- New shared `Components/ProviderBrand.cs`: stable preferred-provider ranking (Radius, OpenRouter, remaining names); exact upstream Radius palette/sRGB smoothstep and OpenRouter theme palettes/OKLCH sinusoidal pulse; 50 ms redraw helper.
- Login/logout provider options and completion options use preferred ordering. Generic OAuth selector shows muted single-width partner marker and animates selected preferred row with optional TUI; application selectors pass TUI and dispose callbacks. Radius top-level selector shares the helper.
- LoginDialog already had no legacy OpenAI Codex label rewrite; no change required.
- ExtensionUi editor swaps now transfer GetExpandedText. The interface default implementation already falls back to GetText for editors without paste expansion.
- ExtensionUi reset now invokes ResetCustomComponents rather than hiding one overlay, coordinated with ui_flow helper ownership.

## Ownership coordination

ui_flow owns expanded-text transfer in ExtensionHost/Events plus overlay/pending-bash/cache/changelog work. cli_discovery confirmed no overlap with Login.cs, Llama.cs or small selectors. Radius MCP bootstrap wiring is routed to root (not owned here). Markdown and Mermaid are independent slices.

## Regression cases authored

`tests/PiSharp.InteractiveParity.Tests/Sync152SmallUiCases.cs`, discovered by existing reflection-based test runner; filter `sync152.ui`:

- Windowed extension options, selection identity, short and empty lists.
- Model refresh preserving navigation with/without active query and with/without selected identity in next snapshot.
- Tree search width 0/1, search cursor placement, label editing focus transitions and unfocused marker removal.
- Preferred ordering and stable auth order, partner markers and their terminal width, theme-specific OpenRouter palette/pulse, shared Radius style.
- Optional OAuth animation redraw eligibility and disposal.
- Both Windows/WSL clipboard keys dispatch through CustomEditor, explicit override wins.
- Diff shared indentation, tabs, changed indent and empty original line.
- Actual syntax highlighting using new path mappings and bundled grammars.
- Harness-based Hugging Face pending selection: preserved, removed, untouched and stale-query cases.
- Harness-based collapsed paste transfer through custom→custom and custom→default→custom editor swaps.

Static check: `git diff --check` passed for owned tracked production files (line-ending normalization warnings only). Runtime results must be appended by root after serialized build/test. Existing source-replay and historical goldens untouched.
