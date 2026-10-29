---
title: Terminal UI
description: Build terminal interfaces with PiSharp.Tui's components, layout and terminals.
---

`PiSharp.Tui` is the terminal toolkit that `pisharp`'s interactive mode and `pisharp durable` are built on. It is a C# port of Pi's TUI library ([`packages/tui`](https://github.com/earendil-works/pi/tree/v1.2.0/packages/tui)), so its components, key binding ids and rendering rules are Pi's. You build a tree of components that render to lines of styled text. A TUI draws those lines with differential updates, routes keys to the focused component and draws overlays on top.

## Install

```powershell
dotnet add package PiSharp.Tui --version 1.2.0.1
```

The package targets net10.0 and has no dependencies. Its main API is in the `PiSharp.Tui.Pi` namespace; every type on this page is there unless the section says otherwise.

The components you see in `pisharp` itself (message views, tool cards, the footer, the themes, the custom editor) are internal to `PiSharp.Cli`. The package gives you the building blocks they are made from.

## How it fits together

| Piece | Type | Role |
| --- | --- | --- |
| Event loop | `UiLoop` | One thread that owns every component, all rendering and all input. |
| Terminal | `ITerminal`, `ProcessTerminal` | Raw mode, input, output and size over the process's console or tty. |
| Renderer | `TuiMainScreen` or `TuiAltScreen` | The `ITui`: focus, overlays, input routing and render scheduling. |
| Components | `IComponent` and the built-ins | Render lines for a width and handle input. |
| Key bindings | `KeybindingsManager` | Maps binding ids such as `tui.select.up` to keys. |

`TuiMainScreen` renders into the terminal's normal screen, so output stays in the scrollback, as Pi's default mode does. `TuiAltScreen` takes over the alternate screen with a scrollable viewport, mouse selection and search, as Pi's fullscreen mode does.

## A first app

This program keeps a list of notes. The editor adds a note on Enter, the notes render as Markdown, and Ctrl+D quits:

```csharp
using PiSharp.Tui.Pi;

var mode = Colors.GetTerminalColorMode();
var blue = Colors.ParseColor("#5fafff");
string Style(string text, TextStyle style) => Colors.StyleText(text, style, mode);
string Accent(string text) => Style(text, new TextStyle(Fg: blue));
string Dim(string text) => Style(text, new TextStyle(Dim: true));
string Bold(string text) => Style(text, new TextStyle(Bold: true));

var markdownTheme = new MarkdownTheme
{
    Heading = Bold, Bold = Bold, Link = Accent, LinkUrl = Dim, Code = Accent,
    CodeBlock = text => text, CodeBlockBorder = Dim, Quote = Dim, QuoteBorder = Dim,
    Hr = Dim, ListBullet = Accent,
    Italic = text => Style(text, new TextStyle(Italic: true)),
    Strikethrough = text => Style(text, new TextStyle(Strikethrough: true)),
    Underline = text => Style(text, new TextStyle(Underline: true)),
};
var editorTheme = new EditorTheme(Dim, new SelectListTheme(Accent, Accent, Dim, Dim, Dim));

using var loop = new UiLoop("notes");
var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
loop.UnhandledException += error => exited.TrySetException(error);
var ui = new TuiMainScreen(new ProcessTerminal(loop), loop);

await loop.InvokeAsync(() =>
{
    var notes = new Container();
    var editor = new Editor(ui, editorTheme, new EditorOptions(PaddingX: 1));
    editor.OnSubmit = text =>
    {
        if (text.Length == 0) return;
        editor.AddToHistory(text);
        notes.AddChild(new Markdown(text, 1, 0, markdownTheme));
        ui.RequestRender();
    };
    ui.AddChild(new Text(Bold("Notes") + Dim("  Enter adds a note, Ctrl+D quits"), 1, 0));
    ui.AddChild(notes);
    ui.AddChild(editor);
    ui.SetFocus(editor);
    ui.AddInputListener(data =>
    {
        if (!Keys.Matches(data, "ctrl+d")) return null;
        exited.TrySetResult();
        return new TuiInputListenerResult(Consume: true);
    });
    ui.Start();
});

try { await exited.Task; }
finally
{
    await loop.InvokeAsync(() => ui.Stop());
    loop.Stop();
}
```

The shape is the one `pisharp` uses for its own startup prompts: build and start the UI on the loop, wait for a result, then stop the UI on the loop and stop the loop.

Themes are plain `Func<string, string>` functions that wrap text in escape codes. `Colors.StyleText` builds them from a `TextStyle`, and `Colors.GetTerminalColorMode` picks 24-bit or 256-color output from the terminal's capabilities.

:::note[Raw mode]
`Start` puts the terminal in raw mode, so Ctrl+C arrives as input instead of ending the process. Handle it yourself if your app should quit on it. `Start` throws `IOException` when standard input and output are not a console or terminal.
:::

## The UI loop

Pi's TUI assumes Node's single-threaded event loop. `UiLoop` is its stand-in: a thread that runs posted work in order. Components, rendering and input handling only run on that thread. The loop is also the thread's `SynchronizationContext`, so an `await` in a handler resumes on the loop.

| Member | What it does |
| --- | --- |
| `new UiLoop(string name = "pi-tui")` | Starts the loop on its own background thread. |
| `UiLoop.CreateManual()` | A loop with no thread, driven by `RunPending` (for tests). |
| `Post(Action)` | Queues work. Posts after `Stop` or `Dispose` are dropped. |
| `InvokeAsync(Action)`, `InvokeAsync<T>(Func<T>)`, `InvokeAsync(Func<Task>)` | Runs work on the loop and returns a task for its result or exception. |
| `SetTimeout(Action, double ms)`, `SetInterval(Action, double ms)` | Timers whose callbacks run on the loop. Dispose the handle to cancel. |
| `NextTick(Action)` | Runs after the current work item. |
| `IsLoopThread` | True on the loop thread. |
| `UnhandledException` | Receives exceptions thrown by posted work. Without a handler, the exception ends the process. |
| `Completion` | Completes when the loop thread exits. |
| `RunPending(int maximum = 100_000)` | Runs queued work on the calling thread (manual loops). |
| `Stop()` | Stops taking work and lets queued work finish. |
| `Dispose()` | Stops the loop and waits up to five seconds for its thread. |

To change the UI from another thread, post to the loop and request a render. Here `status` is a `Text` in the UI:

```csharp
_ = Task.Run(async () =>
{
    await Task.Delay(1000);
    loop.Post(() =>
    {
        status.SetText("Done");
        ui.RequestRender();
    });
});
```

`InvokeAsync` reports exceptions through its task, not through `UnhandledException`. Input handlers, timers and other posted work report through `UnhandledException`.

## Components

A component is anything that implements `IComponent`:

| Interface | Members | Purpose |
| --- | --- | --- |
| `IComponent` | `List<string> Render(int width)`, `void Invalidate()` | Render lines for a width; drop cached output. |
| `IInputHandler` | `void HandleInput(string data)`, `bool WantsKeyRelease` (default false) | Receive raw key input while focused. Kitty key-release events reach it only when `WantsKeyRelease` is true. |
| `IFocusable` | `bool Focused { get; set; }` | Told when it gains or loses focus. Focused components emit `TuiBase.CursorMarker` where the cursor is. |
| `IMouseHandler` | `TuiMouseEventResult? HandleMouse(TuiMouseEvent)` | Receive mouse events (fullscreen only). |
| `IDisposableComponent` | `void Dispose()` | Stop timers. The TUI never calls it: dispose components you remove. |

### Write a component

```csharp
internal sealed class Counter : IComponent, IInputHandler
{
    private int count;

    public void HandleInput(string data)
    {
        if (Keys.Matches(data, "up")) count++;
        else if (Keys.Matches(data, "down")) count--;
    }

    public List<string> Render(int width) =>
        [TextUtils.TruncateToWidth($"Count: {count}  (up and down change it)", width)];

    public void Invalidate() { }
}
```

The rules come from Pi:

- Every line must fit `width` terminal cells. Measure with `TextUtils.VisibleWidth`, which ignores escape codes and counts wide characters as two cells, and cut with `TextUtils.TruncateToWidth` or wrap with `TextUtils.WrapTextWithAnsi`. When `TuiMainScreen` redraws a changed line that is too wide, it stops the TUI, writes all rendered lines to `pi-tui-crash.log` (in the log directory, or the temp folder) and throws `InvalidOperationException`. `TuiAltScreen` cuts such lines instead.
- The TUI renders again after each key it routes to the focused component. When state changes for another reason (a timer, a background result), call `ui.RequestRender()`.
- Cache output by width if rendering is expensive, and clear the cache in `Invalidate`. The TUI invalidates every component when the terminal reports a new cell size. Call `ui.Invalidate()` yourself after you change the theme.
- The TUI ends each line with a style reset, so a style left open does not leak into the next line. It does not leak sideways into an overlay either.

### Built-in components

| Component | Constructor | Notes |
| --- | --- | --- |
| `Container` | `new Container()` | Stacks `Children` vertically. `AddChild`, `RemoveChild`, `Clear`. Routes mouse events to the child under the pointer. |
| `Text` | `Text(string text = "", int paddingX = 1, int paddingY = 1, Func<string, string>? customBg = null)` | Word-wrapped text with padding and an optional background. `SetText`, `SetCustomBgFn`, `SetPaddingX`, `Value`. Renders nothing for blank text. |
| `TruncatedText` | `TruncatedText(string text, int paddingX = 0, int paddingY = 0)` | The first line of `text`, truncated to the width. |
| `Box` | `Box(int paddingX = 1, int paddingY = 1, Func<string, string>? bg = null)` | A container that pads its children and fills a background. `SetBgFn`, `SetPaddingX`. |
| `Spacer` | `Spacer(int lines = 1)` | Empty lines. `SetLines`. |
| `Loader` | `Loader(ITui ui, Func<string, string> spinnerColor, Func<string, string> messageColor, string message = "Loading...", LoaderIndicatorOptions? indicator = null)` | A message with a spinner. It starts spinning when constructed. `Start`, `Stop`, `SetMessage`, `SetIndicator`, `Dispose`. |
| `CancellableLoader` | Same as `Loader` | A loader that `tui.select.cancel` (Escape) aborts: `OnAbort`, `Signal` (a `CancellationToken`), `Aborted`. |
| `Input` | `Input(string prompt = "> ", string placeholder = "", Func<string, string>? placeholderStyle = null)` | One-line input with horizontal scrolling, kill ring and undo. `OnSubmit`, `OnEscape`, `GetValue`, `SetValue`. Pasted newlines are removed. |
| `Editor` | `Editor(ITui tui, EditorTheme theme, EditorOptions? options = null)` | The multi-line prompt editor. See [Editor](#editor). |
| `Markdown` | `Markdown(string text, int paddingX, int paddingY, MarkdownTheme theme, DefaultTextStyle? defaultStyle = null, MarkdownOptions? options = null)` | Markdown rendered to styled lines. See [Markdown](#markdown). |
| `SelectList` | `SelectList(IEnumerable<SelectItem> items, int maxVisible, SelectListTheme theme, SelectListLayoutOptions? layout = null)` | A scrolling pick list. See [Lists](#lists). |
| `SettingsList` | `SettingsList(IEnumerable<SettingItem> items, int maxVisible, SettingsListTheme theme, Action<string, string> onChange, Action onCancel, bool enableSearch = false)` | Settings with values and submenus. See [Lists](#lists). |
| `Image` | `Image(string base64, string mimeType, ImageTheme theme, ImageOptions? options = null, ImageDimensions? dimensions = null)` | An inline image on terminals with the Kitty or iTerm2 protocol, a text placeholder elsewhere. See [Images](#colors-and-images). |
| `VStack`, `HStack` | `VStack(IEnumerable<StackEntry> children, int gap = 0, StackAlign align = StackAlign.Stretch)` | Flex stacks. See [Fullscreen](#fullscreen-tuialtscreen). |
| `ScrollView` | `ScrollView(IComponent component, ScrollViewOptions? options = null)` | A scrolling viewport over one child. |
| `MouseRegion` | `MouseRegion(IComponent child, Func<TuiMouseEvent, TuiMouseEventResult?> onMouse)` | Adds mouse handling to a component without changing how it renders. |

### Text helpers

`TextUtils` holds the measuring and cutting functions components need. They understand ANSI styles, OSC 8 links and graphemes.

| Method | What it does |
| --- | --- |
| `VisibleWidth(string)` | Width in terminal cells, ignoring escape sequences. |
| `TruncateToWidth(string text, int maxWidth, string ellipsis = "...", bool pad = false)` | Cuts styled text to a width, with an ellipsis; `pad` fills to exactly `maxWidth`. |
| `WrapTextWithAnsi(string text, int width)` | Word-wraps styled text, carrying styles across lines. |
| `SliceByColumn(string line, int startCol, int length, bool strict = false)` | The cells from `startCol`, keeping styles. `SliceWithWidth` also returns the width. |
| `ApplyBackgroundToLine(string line, int width, Func<string, string> bg)` | Pads a line to the width and applies a background. |
| `StripTerminalSequences(string)` | Removes ANSI, OSC and APC sequences, including cursor markers. |
| `Graphemes(string)`, `GraphemeWidth(string)` | Grapheme clusters and the width of one. |
| `Repeat(string, int)` | A repeated string. |

`Fuzzy.Filter(items, query, getText)` and `Fuzzy.Match(query, text)` give the fuzzy matching that `SettingsList` uses. `TerminalImage.Hyperlink(text, url)` makes an OSC 8 link.

## Editor

`Editor` is the prompt editor from Pi: word wrap, history, a kill ring, undo, large-paste markers and autocomplete. It draws a border line above and below the text.

| Member | Description |
| --- | --- |
| `EditorOptions(int PaddingX = 0, int AutocompleteMaxVisible = 5)` | Horizontal padding, and the autocomplete list height (clamped to 3 to 20). |
| `EditorTheme(Func<string, string> BorderColor, SelectListTheme SelectList)` | Border style and the autocomplete list's theme. |
| `OnSubmit` | Called with the text when `tui.input.submit` (Enter) is pressed. The text is trimmed, paste markers are expanded, and the editor is cleared first. |
| `OnChange` | Called with the text after each change. |
| `DisableSubmit` | When true, Enter does nothing. |
| `GetText()`, `SetText(string)` | The text, with paste markers as typed. `SetText` normalizes line endings and tabs. |
| `GetExpandedText()` | The text with paste markers expanded. |
| `GetLines()`, `GetCursor()` | The lines, and the cursor as `(Line, Col)`. |
| `InsertTextAtCursor(string)` | Inserts text at the cursor. |
| `AddToHistory(string)` | Adds a prompt to the history (100 entries, consecutive duplicates skipped). Up on the first line browses it when the editor is empty or the cursor is at column 0. |
| `SetAutocompleteProvider(IAutocompleteProvider)` | Turns on autocomplete. `IsShowingAutocomplete()` reports whether the list is open. |
| `SetPaddingX`, `GetPaddingX`, `SetAutocompleteMaxVisible`, `GetAutocompleteMaxVisible`, `BorderColor` | Change layout and style after construction. |
| `Editor.WordWrapLine(string line, int maxWidth, ...)` | The editor's wrapping, as a static helper. |

A few behaviors to know:

- `tui.input.newLine` (Shift+Enter, Ctrl+J) inserts a line break. A backslash just before the cursor also turns Enter into a line break.
- A paste of more than 10 lines or 1,000 characters becomes a marker such as `[paste #1 +42 lines]`. `OnSubmit` and `GetExpandedText` give the pasted text.
- `Editor` is not sealed. `HandleInput`, `Render`, `RenderTopBorder` and `RenderBottomBorder` are virtual, so you can add keys or change the border, as `pisharp`'s own editor does. `IEditorComponent` is the contract for a replacement editor.

### Autocomplete

An `IAutocompleteProvider` returns suggestions for the text before the cursor and applies the one the user picks:

| Member | Description |
| --- | --- |
| `GetSuggestionsAsync(lines, cursorLine, cursorCol, force, cancellationToken)` | Suggestions (`AutocompleteSuggestions(Items, Prefix)`), or null. `force` is true when the user pressed Tab; if Tab finds exactly one suggestion, the editor applies it at once. |
| `ApplyCompletion(lines, cursorLine, cursorCol, item, prefix)` | The new lines and cursor (`CompletionResult`). |
| `TriggerCharacters` | Extra characters that open suggestions at the start of a token. The editor always has `@` and `#`. |
| `ShouldTriggerFileCompletion(lines, cursorLine, cursorCol)` | Whether Tab asks for suggestions here. Default true. |

`CombinedAutocompleteProvider(IReadOnlyList<SlashCommand> commands, string basePath, string? fdPath = null)` is Pi's provider: `/` commands at the start of the message (with `SlashCommand.GetArgumentCompletions` for their arguments), paths on Tab, and `@` fuzzy file search through `fd`. Without `fdPath`, `@` finds nothing.

## Markdown

`Markdown` renders headings, emphasis, links, lists, block quotes, code blocks, tables, rules and basic LaTeX math. Every style comes from the theme:

| `MarkdownTheme` property | Styles |
| --- | --- |
| `Heading`, `Bold`, `Italic`, `Strikethrough`, `Underline` | Text styles. All required. |
| `Link`, `LinkUrl` | Link text and the URL shown after it. Required. |
| `Code`, `CodeBlock`, `CodeBlockBorder` | Inline code, code block text and the fences. Required. |
| `Quote`, `QuoteBorder`, `Hr`, `ListBullet` | Block quotes, rules and bullets. Required. |
| `HighlightCode` | Optional `Func<string, string?, List<string>>`: takes a code block and its language and returns highlighted lines. |
| `CodeBlockIndent` | Optional indent for code blocks. Default two spaces. |

| `MarkdownOptions` | Default | Effect |
| --- | --- | --- |
| `PreserveOrderedListMarkers` | false | Keep the source's list markers instead of renumbering and using `-`. |
| `PreserveBackslashEscapes` | false | Show backslash escapes as written. |
| `Transform` | null | `Func<string, int, string>`: takes the source and the content width and returns the source to parse. |
| `RenderLatex` | true | Render math in `$...$`, `$$...$$`, `\(...\)` and `\[...\]` as Unicode text. |

`DefaultTextStyle(Color, BgColor, Bold, Italic, Strikethrough, Underline)` styles all text that has no other style. `SetText` replaces the source; the component caches its lines per width.

## Lists

`SelectList` shows `SelectItem(Value, Label, Description)` rows with an arrow on the selected one and the description in a second column when there is room. `tui.select.up` and `tui.select.down` move (wrapping at the ends), `tui.select.confirm` calls `OnSelect`, and `tui.select.cancel` calls `OnCancel`. `OnSelectionChange` follows the selection. `SetFilter(prefix)` keeps items whose value starts with the prefix, ignoring case. `SetSelectedIndex` and `GetSelectedItem` read and set the selection.

| Type | Fields |
| --- | --- |
| `SelectListTheme` | `SelectedPrefix`, `SelectedText`, `Description`, `ScrollInfo`, `NoMatch`, all `Func<string, string>` |
| `SelectListLayoutOptions` | `MinPrimaryColumnWidth`, `MaxPrimaryColumnWidth` (both default 32), `TruncatePrimary` |

`SettingsList` lists `SettingItem` rows with aligned values. Enter, or Space while the search is empty, cycles an item through its `Values` or opens its `Submenu`. Each change calls `onChange(id, value)`; Escape calls `onCancel`. With `enableSearch`, typing filters the labels with fuzzy matching.

| `SettingItem` | Description |
| --- | --- |
| `Id`, `Label`, `CurrentValue` | Required. `CurrentValue` changes as the user cycles it. |
| `Description` | Shown below the list when the item is selected. |
| `Values` | The values Enter cycles through. |
| `Submenu` | `Func<string, Action<string?, string?>, IComponent>`: takes the current value and a `done(selected, navigateTo)` callback and returns a component shown in place of the list until it calls `done`. A non-null `selected` becomes the value; `navigateTo` opens another item next. |

`UpdateValue(id, value)` and `SelectItem(id)` change the list from code. This one opens in an overlay; `accent` and `dim` are style functions like the first app's, and `save` receives each change:

```csharp
IOverlayHandle? overlay = null;
var settings = new SettingsList(
    [
        new SettingItem { Id = "theme", Label = "Theme", CurrentValue = "dark", Values = ["dark", "light"] },
        new SettingItem { Id = "wrap", Label = "Word wrap", CurrentValue = "on", Values = ["on", "off"], Description = "Wrap long lines." },
    ],
    maxVisible: 10,
    theme: new SettingsListTheme(
        Label: (text, selected) => selected ? accent(text) : text,
        Value: (text, selected) => selected ? accent(text) : dim(text),
        Description: dim,
        Cursor: "→ ",
        Hint: dim),
    onChange: save,
    onCancel: () => overlay?.Hide(),
    enableSearch: true);
overlay = ui.ShowOverlay(settings, new OverlayOptions { Width = SizeValue.Pct(60) });
```

## Overlays

`ShowOverlay` draws a component over the rest of the UI and, unless the overlay is non-capturing, moves focus to it. Hiding it returns focus to whatever had it before. This picker calls `picked` with the chosen value:

```csharp
var list = new SelectList(
    [new SelectItem("red", "Red"), new SelectItem("green", "Green", "The default"), new SelectItem("blue", "Blue")],
    5, theme);
IOverlayHandle? overlay = null;
list.OnSelect = item =>
{
    overlay?.Hide();
    picked(item.Value);
};
list.OnCancel = () => overlay?.Hide();
overlay = ui.ShowOverlay(list, new OverlayOptions { Width = SizeValue.Pct(40), Anchor = OverlayAnchor.Center });
```

| `OverlayOptions` | Default | Description |
| --- | --- | --- |
| `Width` | 80 cells, or less to fit | A `SizeValue`: cells (an `int` converts) or `SizeValue.Pct(percent)` of the terminal width. |
| `MinWidth` | none | Minimum width in cells. |
| `MaxHeight` | none | Cells or a percentage of the terminal height. Longer content is cut. |
| `Anchor` | `Center` | `Center`, `TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`, `TopCenter`, `BottomCenter`, `LeftCenter` or `RightCenter`. |
| `Row`, `Col` | from `Anchor` | An absolute row or column, or a percentage of the free space. Override `Anchor`. |
| `OffsetX`, `OffsetY` | 0 | Moves the overlay after placement. |
| `Margin` | none | `OverlayMargin(Top, Right, Bottom, Left)`, or `OverlayMargin.All(n)`. The overlay stays inside it. |
| `Visible` | always | `Func<int, int, bool>` of the terminal's columns and rows: hides the overlay at some sizes. |
| `NonCapturing` | false | Show without taking focus. |

The `IOverlayHandle` it returns has `Hide()`, `SetHidden(bool)`, `IsHidden`, `Focus()`, `Unfocus(target, hasTarget)`, `IsFocused` and `GetBounds()`. `ui.HideOverlay()` hides the newest overlay and `HasOverlay()` says whether one is visible.

## Fullscreen: TuiAltScreen

`TuiAltScreen` owns the alternate screen. With no layout root, its children form one document that scrolls and follows new output. Give it a layout root for a fixed layout: here, a transcript that takes the free height and an editor docked below it. `editorTheme` is the first app's.

```csharp
using var loop = new UiLoop("viewer");
var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
loop.UnhandledException += error => exited.TrySetException(error);
var ui = new TuiAltScreen(new ProcessTerminal(loop), loop, options: new TuiAltScreenOptions { WheelScrollLines = null });

await loop.InvokeAsync(() =>
{
    var document = new Container();
    var transcript = new ScrollView(document, new ScrollViewOptions(ScrollViewFollow.End, Primary: true,
        Scrollbar: ScrollViewScrollbar.Auto, Loop: loop));
    var editor = new Editor(ui, editorTheme, new EditorOptions(PaddingX: 1));
    editor.OnSubmit = text =>
    {
        document.AddChild(new Text(text, 1, 0));
        ui.RequestRender();
    };
    ui.SetLayoutRoot(new VStack([
        new StackEntry(transcript, Basis: 0, Grow: 1, Shrink: 1, MinSize: 1),
        new StackEntry(editor, MinSize: 3),
    ]));
    ui.SetFocus(editor);
    ui.AddInputListener(data =>
    {
        if (!Keys.Matches(data, "ctrl+d")) return null;
        exited.TrySetResult();
        return new TuiInputListenerResult(Consume: true);
    });
    ui.Start();
});

try { await exited.Task; }
finally
{
    await loop.InvokeAsync(() => ui.Stop());
    loop.Stop();
}
```

The `tui.altScreen.*` keys (page up and down, top, bottom, search, previous and next prompt) go to the viewport before the focused component, unless an overlay has focus. The prompt keys jump between lines that start with an OSC 133 `A` mark. With mouse support on, the TUI handles the wheel, scroll bar drags, text selection (a double click selects a word, a triple click a line) and clicks on OSC 8 links. `Stop()` leaves the alternate screen and prints the document to the normal screen; `Stop(preserveScreen: true)` does not.

| `TuiAltScreenOptions` | Default | Description |
| --- | --- | --- |
| `Mouse` | true | Turn on mouse reporting. |
| `WheelScrollLines` | 1 | Lines per wheel step. Null accelerates fast wheel movement. |
| `CopyOnSelect` | true | Copy a selection when the mouse is released. |
| `CopySelection` | OSC 52 | `Func<string, Task<(bool Ok, string? Error)>>` that copies text. Without it, the TUI writes an OSC 52 clipboard sequence. |
| `OpenUrl` | none | Called with the URL of a clicked link. |
| `OnRightClickPaste` | none | Called on a right click on Windows (not in VS Code's terminal). |
| `ScrollToEndIndicator` | none | Text of a "jump to latest" label, shown while a primary view that follows the end is scrolled up. |
| `SearchMatchStyle`, `SearchCurrentMatchStyle`, `SearchNavigationButtonStyle` | underline; bold reverse; unchanged | Styles for search results. |
| `Environment` | process environment | Environment lookup, for tests. |

`TuiAltScreen` also has `ScrollBy(lines)`, `ScrollToTop()`, `ScrollToBottom()`, `ViewportTop`, `IsFollowingOutput`, `SetWheelScrollLines`, `Flash(message, durationMs = 1000)`, `HasActiveSelection()`, `CopyActiveSelectionToClipboard()`, `ResetTextSelection()` and `GetScreenLines()`. It implements `IViewportTui`, whose one extra member is `SetLayoutRoot`.

### Scroll views

| `ScrollViewOptions` | Default | Description |
| --- | --- | --- |
| `Follow` | `None` | `End` keeps the view at the bottom as content grows, until the user scrolls up. |
| `Primary` | false | The view that the page keys, search and selection use. Without one, the first scroll view laid out is primary. |
| `Overscroll` | `Chain` | `Chain` passes leftover wheel scrolling to the enclosing view; `Contain` keeps it. |
| `Scrollbar` | `Hidden` | `Auto` shows a scroll bar while scrolling; `Always` reserves a column for it. |
| `ScrollbarTrackStyle`, `ScrollbarThumbStyle` | gray; white | Scroll bar styles. |
| `ScrollbarHideDelayMs` | 1000 | How long an `Auto` scroll bar stays after scrolling. |
| `Loop` | none | The loop for the hide timer. Without it, an `Auto` bar does not hide by itself. |

`ScrollTo`, `ScrollBy` (which returns the lines it could not scroll), `ScrollToStart`, `ScrollToEnd`, `ScrollTop`, `ViewportHeight`, `IsFollowingEnd` and `SetScrollbar` control a view from code. A `ScrollView` has exactly one child; `AddChild` throws.

### Stacks and the layout engine

`VStack` and `HStack` take `StackEntry` items with flex options:

| `StackEntry` | Default | Description |
| --- | --- | --- |
| `Component` | | The child. |
| `Basis` | auto | Starting size: lines in a `VStack`, cells in an `HStack`. Auto is the rendered height, or the widest line. |
| `Grow` | 0 | Share of the free space. |
| `Shrink` | 1 | Share of the shrinking when space is short, weighted by size. |
| `MinSize`, `MaxSize` | 0, none | Size limits. |
| `Visible` | always | `Func<LayoutViewport, bool>`: hides the entry at some sizes. |

The stack's `gap` puts empty cells between entries. `HStack`'s `align` (`Stretch`, `Start`, `Center`, `End`) places shorter children vertically.

Heights only exist in fullscreen. There, `LayoutEngine.RenderFrame(root, width, height, requestRender)` gives the root the terminal's size, splits it among stacks and scroll views, and returns a `LayoutFrame` of boxes and lines. `GetBoxesAt`, `GetScrollViewsAt` and `GetScrollViewBox` find boxes for hit testing. On the main screen there is no height to fill: a `VStack` stacks its children at their natural height, a `ScrollView` renders its whole child, and an `HStack` still splits the width.

## The ITui interface

`TuiMainScreen` and `TuiAltScreen` derive from `TuiBase`, which implements `ITui`. Components that need the UI, such as `Editor` and `Loader`, take an `ITui`.

| Member | Description |
| --- | --- |
| `Children`, `AddChild`, `RemoveChild`, `Clear` | The root components, rendered top to bottom. |
| `SetFocus(IComponent?)`, `FocusedComponent` | The component that gets key input. |
| `ShowOverlay`, `HideOverlay`, `HasOverlay` | Overlays. |
| `Start()`, `Stop(bool preserveScreen = false)` | Take and release the terminal. |
| `RequestRender(bool force = false)` | Schedules a render, at most one every 16 ms. `force` redraws everything. |
| `RenderNow(bool force = false)` | Renders at once. |
| `AddInputListener(Func<string, TuiInputListenerResult?>)` | Sees input before the focused component. Return `Consume: true` to stop it, `Data` to replace it, or null to pass it on. Returns an action that removes the listener. |
| `ShowHardwareCursor` | Show the terminal's cursor at the focused component's cursor. Otherwise components draw their own cursor in reverse video. |
| `ClearOnShrink` | Main screen: redraw fully when the content gets shorter, so no old lines stay below it. |
| `QueryTerminalColors(timeoutMs, onLateReply)` | Asks the terminal for its foreground, background and palette. |
| `OnTerminalColorSchemeChange`, `SetTerminalColorSchemeNotifications` | Follow the terminal's dark and light mode reports. |
| `OnDebug` | Called on Shift+Ctrl+D. |
| `Mode`, `Terminal`, `Loop`, `FullRedraws`, `Invalidate()` | The renderer kind, its terminal and loop, a redraw counter, and invalidation of every component. |

Both renderers take `(ITerminal terminal, UiLoop loop, bool? showHardwareCursor = null, string? logDirectory = null, ...)`. The log directory is where crash and debug logs go. `TuiMainScreen` also takes an environment lookup, and `TuiAltScreen` its options.

Input goes through the TUI in this order: replies to the TUI's color queries, input listeners, Shift+Ctrl+D when `OnDebug` is set, then the focused component.

## Key bindings

Components never test raw keys for their actions. They ask `KeybindingsManager.Global` whether input matches a binding id, so users can rebind keys. `Keys.Matches(data, "ctrl+shift+f")` matches one key id against raw input, across legacy sequences, the Kitty keyboard protocol and xterm's modifyOtherKeys. A key id is modifiers (`shift`, `alt`, `ctrl`, `super`) joined with `+` to a key: a letter, digit or symbol, `enter`, `escape`, `tab`, `space`, `backspace`, `delete`, `insert`, `home`, `end`, `pageUp`, `pageDown`, an arrow (`up`, `down`, `left`, `right`) or `f1` to `f12`. `Keys.Parse(data)` goes the other way.

Build a manager from definitions and the user's overrides, then make it the global manager that components read:

```csharp
var keybindings = new KeybindingsManager(
    [.. KeybindingsManager.TuiKeybindings, KeyValuePair.Create("notes.save", new KeybindingDefinition("ctrl+s", "Save notes"))],
    new Dictionary<string, IReadOnlyList<string>?>
    {
        ["tui.editor.undo"] = ["ctrl+z"],
        ["tui.select.cancel"] = ["escape"],
    });
KeybindingsManager.SetGlobal(keybindings);
```

Your components then test `KeybindingsManager.Global.Matches(data, "notes.save")`. An override replaces a binding's keys; an empty list unbinds it. `GetConflicts()` lists keys that the overrides give to more than one binding. `GetKeys`, `GetDefinition`, `Definitions`, `GetUserBindings`, `GetResolvedBindings` and `SetUserBindings` read and change the bindings. The library does not read `keybindings.json`; load it yourself if you want the user's file.

The library's defaults:

| Binding | Keys | Binding | Keys |
| --- | --- | --- | --- |
| `tui.editor.cursorUp` | up | `tui.editor.deleteWordForward` | alt+d, alt+delete |
| `tui.editor.cursorDown` | down | `tui.editor.deleteToLineStart` | ctrl+u |
| `tui.editor.cursorLeft` | left, ctrl+b | `tui.editor.deleteToLineEnd` | ctrl+k |
| `tui.editor.cursorRight` | right, ctrl+f | `tui.editor.yank` | ctrl+y |
| `tui.editor.cursorWordLeft` | alt+left, ctrl+left, alt+b | `tui.editor.yankPop` | alt+y |
| `tui.editor.cursorWordRight` | alt+right, ctrl+right, alt+f | `tui.editor.undo` | ctrl+- |
| `tui.editor.cursorLineStart` | home, ctrl+a | `tui.input.newLine` | shift+enter, ctrl+j |
| `tui.editor.cursorLineEnd` | end, ctrl+e | `tui.input.submit` | enter |
| `tui.editor.jumpForward` | ctrl+] | `tui.input.tab` | tab |
| `tui.editor.jumpBackward` | ctrl+alt+] | `tui.input.copy` | ctrl+c |
| `tui.editor.pageUp` | pageUp, ctrl+pageUp | `tui.select.up`, `tui.select.down` | up, down |
| `tui.editor.pageDown` | pageDown, ctrl+pageDown | `tui.select.pageUp`, `tui.select.pageDown` | pageUp, pageDown |
| `tui.editor.historyPrevious`, `tui.editor.historyNext` | none | `tui.select.confirm` | enter |
| `tui.editor.deleteCharBackward` | backspace | `tui.select.cancel` | escape, ctrl+c |
| `tui.editor.deleteCharForward` | delete, ctrl+d | `tui.altScreen.pageUp`, `tui.altScreen.pageDown` | pageUp, pageDown |
| `tui.editor.deleteWordBackward` | ctrl+w, alt+backspace | `tui.altScreen.top`, `tui.altScreen.bottom` | ctrl+home, ctrl+end |
| `tui.altScreen.halfPageUp`, `halfPageDown`, `lineUp`, `lineDown` | none | `tui.altScreen.previousPrompt` | ctrl+shift+up, ctrl+up |
| `tui.altScreen.search` | ctrl+shift+f | `tui.altScreen.nextPrompt` | ctrl+shift+down, ctrl+down |
| `tui.altScreen.searchNext` | enter, ctrl+g | `tui.altScreen.searchPrevious` | shift+enter, ctrl+shift+g |
| `tui.altScreen.searchClose` | escape | | |

`pisharp` starts from these, changes a few per platform (undo is Ctrl+Z on Windows, for example), adds its `app.*` bindings and applies `keybindings.json`. [Key bindings](/docs/guides/interactive/#key-bindings) covers the CLI's set.

## Terminals

`ITerminal` is the small terminal the TUI drives:

| Member | Description |
| --- | --- |
| `Start(Action<string> onInput, Action onResize)`, `Stop()` | Begin and end raw I/O. |
| `Write(string)` | Writes output. |
| `Columns`, `Rows` | The size. |
| `KittyProtocolActive` | Whether the Kitty keyboard protocol is on. |
| `DrainInputAsync(int maxMs = 1000, int idleMs = 50)` | Turns off the keyboard protocols, then discards input until none arrives for `idleMs` (at most `maxMs`). |
| `MoveBy`, `HideCursor`, `ShowCursor`, `ClearLine`, `ClearFromCursor`, `ClearScreen` | Cursor and clearing. |
| `SetTitle(string)` | The window title. |
| `SetProgress(bool)` | Turns an indeterminate OSC 9;4 progress indicator on or off. |
| `SetProgramStatus(ProgramStatus)` | An OSC 7501 status report (`Idle`, `Working`, `Blocked`, `Done`, `Error`, `Clear`), sent only if the terminal supports it. |

`ProcessTerminal(UiLoop loop, IRawTerminalIo? io = null, Func<string, string?>? environment = null)` is the real one. It uses the Windows console with VT input and output, or a POSIX tty in raw mode, and posts every callback to the loop. On start it turns on bracketed paste, asks for the Kitty keyboard protocol and falls back to modifyOtherKeys, and checks for program status support. On Windows it polls the window size, since console hosts have no resize signal, and reports Enter pressed with Shift as Shift+Enter. Implement `IRawTerminalIo` to run it over other streams, or implement `ITerminal` yourself.

| Variable | Effect |
| --- | --- |
| `PI_TUI_ESC_TIMEOUT` | Milliseconds to wait after a lone Escape before treating it as the Escape key. Default 10, or 100 over SSH. |
| `PI_TUI_WRITE_LOG` | A file, or a folder for `tui-<time>-<pid>.log`, that receives a copy of everything written to the terminal. |
| `PI_TUI_DEBUG_REDRAW` | `1` logs the reason for each full redraw to `pi-tui-debug.log` in the TUI's log directory (main screen, when it has a log directory). |
| `PI_PROGRAM_STATUS` | `1` or `0` forces program status reports on or off instead of asking the terminal. |
| `PI_IMAGE_PROTOCOL` | `kitty`, `iterm2` or `none` overrides image detection. |
| `PI_TRUE_COLOR`, `PI_HYPERLINKS` | `1` or `0` override 24-bit color and link detection. |
| `COLUMNS`, `LINES` | Used when the size cannot be read. The fallback is 80 by 24. |

The [environment reference](/docs/reference/environment/) lists every variable PiSharp reads.

### Colors and images

`TerminalImage.GetCapabilities()` detects the terminal's image protocol (`Kitty`, `ITerm2` or `None`), 24-bit color and hyperlink support from its environment. `TerminalImage.SetCapabilityOverrides(new TerminalCapabilityOverrides(...))` overrides detection, as `pisharp`'s terminal settings do.

`Colors` builds styles: `Colors.ParseColor` takes `#rgb`, `#rrggbb`, `oklch(...)` or `okhsl(...)` (or a 0 to 255 palette index), `Color.Rgb`, `Color.Indexed` and `Color.Oklch` make colors directly, and `Colors.StyleText(text, TextStyle, TerminalColorMode)` applies a `TextStyle(Fg, Bg, Bold, Dim, Italic, Underline, Inverse, Strikethrough)`. In 256-color mode, colors are mapped to the nearest palette entry. `Colors.Mix` blends two colors.

`Image` draws base64 image data inline. `ImageOptions(MaxWidthCells, MaxHeightCells, Filename, ImageId)` limits its size (60 cells wide by default), and `ImageTheme(FallbackColor)` styles the text shown where images are not supported. Kitty accepts only PNG: set `Image.Transcoder` to a function that converts other formats to base64 PNG, or they show as text there. `pisharp` sets it to the SkiaSharp conversion from `PiSharp.Tools.Skia`.

## Testing components

Render a component directly and strip the escape codes:

```csharp
var counter = new Counter();
counter.HandleInput("\u001b[A");
var line = TextUtils.StripTerminalSequences(counter.Render(40)[0]);
```

To test a whole TUI, give it a terminal that records what it writes and a manual loop, then render on demand:

```csharp
using var loop = UiLoop.CreateManual();
var terminal = new RecordingTerminal(40, 10);
var ui = new TuiMainScreen(terminal, loop);
ui.AddChild(new Text("hello", 0, 0));
ui.Start();
ui.RenderNow();
ui.Stop();
var output = string.Concat(terminal.Writes);
```

`RecordingTerminal` implements `ITerminal`: `Write` appends to a list, `Columns` and `Rows` return fixed numbers, and the other members do nothing. `RenderNow` renders synchronously, so the test needs no running loop. Strip the markers with `TextUtils.StripTerminalSequences` whenever you render focused components outside a TUI.

## The console lease layer

Besides the Pi port, the package has a smaller, bounded set of types in `PiSharp.Tui`, `PiSharp.Tui.Input`, `PiSharp.Tui.Rendering` and `PiSharp.Tui.Components.*`. They back the Windows console host of the older `pisharp session terminal` command. For a new app, use the `PiSharp.Tui.Pi` types above.

| Type | What it is |
| --- | --- |
| `WindowsConsoleTerminal` | An exclusive lease on the Windows console (`IConsoleTerminal`). `OpenAsync(TerminalLeaseOptions?)` switches the console to VT input and output; `DisposeAsync` restores its exact original modes. One lease per process. Off Windows, opening throws `TerminalException` with `UnsupportedPlatform`. |
| `TerminalLeaseOptions` | `MaximumReadCharacters` (4096), `MaximumWriteCharacters` (65,536), `FollowActiveScreenBuffer`. |
| `WindowsConsoleCloseScope` | Holds a console close signal open so cleanup can finish, since Windows may end the process once the close handler returns. |
| `TerminalInputDecoder` | Turns input into `TerminalText`, `TerminalKey`, `TerminalPaste`, `TerminalProtocol` and `TerminalUnknownSequence` events. `MatchesKey(input, keyId)` uses the same key ids. |
| `VtRenderer` | Writes immutable `TerminalFrame`s to an `IConsoleTerminal`, with differential updates and hard size limits (`TerminalRenderLimits`). |
| `TerminalKeybindings` | The key binding manager for this layer. |
| `TerminalTextEditor`, `TerminalSelectList`, `TerminalText`, `TerminalTruncatedText` | Editor, list and text components that produce frames through their frame factories. |

```csharp
using PiSharp.Tui;
using PiSharp.Tui.Input;

await using var console = await WindowsConsoleTerminal.OpenAsync();
var viewport = console.ReadViewport();
await console.WriteAsync($"{viewport.Columns}x{viewport.Rows}. Press Ctrl+C to quit.\r\n".AsMemory());
var decoder = new TerminalInputDecoder();
var buffer = new char[4096];
while (true)
{
    var count = await console.ReadAsync(buffer);
    foreach (var input in decoder.Feed(buffer.AsSpan(0, count)))
        if (TerminalInputDecoder.MatchesKey(input, "ctrl+c")) return;
}
```

## Compared with Pi

- `UiLoop` stands in for Node's event loop, and work from other threads must be posted to it.
- Names follow .NET: `HandleInput`, `RequestRender`, `SetFocus`. Percent sizes are `SizeValue.Pct(50)` instead of `"50%"`.
- Themes are `Func<string, string>` delegates and records instead of objects of functions.
- `Image.Transcoder` is how you supply PNG conversion for Kitty.
