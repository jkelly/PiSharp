# I-TUI library implementation

Implemented approved library scope only. CLI components and bridge runtime remain with their other workers.

## Changes

- `src/PiSharp.Tui/Pi/Markdown.cs`: remove comment-only HTML blocks (including nested lists/quotes), hide comments from mixed HTML, suppress inline comments with adjacent-space normalization, preserve code literals and ordinary HTML.
- `src/PiSharp.Tui/Pi/Tui.cs`: selected cursor line strips all cursor markers; final text-line output strips every remaining cursor marker.
- `src/PiSharp.Tui/Pi/TextUtils.cs`: horizontal slicing does not carry APC sentinels preceding the requested range, preserving persistent styling.
- `src/PiSharp.Tui/Pi/TuiAltScreen.cs`: Kitty covered-row invalidation and image-after-text emission apply on every terminal, not just WezTerm.
- `src/PiSharp.Tui/Pi/TerminalImage.cs`: recognize Alacritty by TERM or ALACRITTY_WINDOW_ID in addition to TERM_PROGRAM, preserving terminal detection precedence.
- `src/PiSharp.Tui/Input/TerminalTextEditorPasteController.cs`: missing short marker suffix is empty instead of literal `undefined` during renumbering.
- Existing `Pi/Editor.cs` already has correct optional-suffix behavior; regression coverage added without unnecessary source changes.

## Regression coverage and execution

Added `tests/PiSharp.Tui.Tests/Sync152TuiTests.cs`, registered in that project's `Program.cs`. Five grouped cases have `sync152` prefix: Markdown comments with nested/streaming/code cases; Alacritty fallbacks and tmux precedence; horizontal cursor clipping and both screen renderers' output; Kitty initial placement ordering and covered-row change; short paste marker renumber/expansion through both editor implementations.

Root can build serially, then execute the existing test runner with `--dotnet-host <absolute dotnet path> --filter sync152` (and optional `--report <absolute path>`). Builds/tests were not run by this worker per work package instruction. `git diff --check -- src/PiSharp.Tui tests/PiSharp.Tui.Tests` passed. Historical snapshots and source pins were not edited.

Root's first focused validation compiled successfully and passed four groups. The cursor group initially called RenderNow on a stopped TUI, producing no text; the test now starts/stops each screen around rendering. This was a test-fixture correction, with no production change. Root will rerun the focused suite.

## Final coverage audit: standalone editor suite

Verified frozen `b4afdddf44e059c67ccf5097e5317d913521b470` `packages/tui/src/components/editor.ts` uses `suffixGroup ?? ""`, and its new `renumbers short paste markers without a size suffix` regression expects valid short markers and expanded submission. Updated the stale authored `PasteExpansion` expectation in `tests/PiSharp.Tui.Editor.Tests/Program.cs`: after deleting paste 1, the unknown short marker is `[paste #98]` and remains literal; the registered short marker is `[paste #1]` and expands to the remaining 1002-character paste. Previously it expected both markers to carry `undefined`.

This audit changed only that authored test and this report. Historical capture files and frozen source are untouched. `git diff --check` passed. Root should add standalone `PiSharp.Tui.Editor.Tests` to its serialized cohort, invoking its executable with mandatory `--report <absolute report path>` and no historical `--source-observations` arguments for the authored suite. No dotnet build/run performed by this worker.
