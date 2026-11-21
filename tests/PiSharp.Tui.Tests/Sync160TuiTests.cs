// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/tui/test/container.test.ts, editor.test.ts, layout.test.ts,
// markdown.test.ts, overlay-terminal-image.test.ts, settings-list.test.ts, slice-graphemes.test.ts, suspend-signal.test.ts,
// tui-alt-screen.test.ts.
// Ported expectations of the Pi v1.6.0 additions to those files, authored against the native port; not an upstream execution
// capture. Cases marked "native" have no upstream counterpart.
using PiSharp.Tui.Pi;

internal static class Sync160TuiTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("sync160.container.same-lines-while-unchanged", Run(ContainerSameLines));
        yield return ("sync160.container.rerenders-on-children-or-width", Run(ContainerRerenders));
        yield return ("sync160.container.box-reuses-output", Run(BoxReusesOutput));
        yield return ("sync160.container.mouse-by-rendered-heights", Run(ContainerMouse));
        yield return ("sync160.container.native-in-place-child-list-contract", Run(ContainerReadOnlyContract));
        yield return ("sync160.editor.enter-on-tab-path-menu-completes", Run(EditorPathMenu));
        yield return ("sync160.editor.enter-on-argument-menu-completes", Run(EditorArgumentMenu));
        yield return ("sync160.editor.enter-on-slash-command-menu-submits", Run(EditorCommandMenu));
        yield return ("sync160.layout.reuses-earlier-frame-output", Run(LayoutReuse));
        yield return ("sync160.markdown.hard-breaks-after-inline-image", Run(MarkdownImageHardBreaks));
        yield return ("sync160.markdown.native-blank-line-after-paragraph", Run(MarkdownBlankLineAfterParagraph));
        yield return ("sync160.settings-list.cursor-keys-keep-selection", Run(SettingsListCursorKeys));
        yield return ("sync160.slice-graphemes.box-arrows-cjk", Run(SliceStandalone));
        yield return ("sync160.slice-graphemes.joined-clusters", Run(SliceClusters));
        yield return ("sync160.slice-graphemes.native-fast-path-matches-segmenter", Run(SliceFastPathMatchesSegmenter));
        yield return ("sync160.suspend-signal.only-while-started", Run(SuspendOnlyWhileStarted));
        yield return ("sync160.suspend-signal.not-before-first-start", Run(SuspendNotBeforeStart));
        yield return ("sync160.suspend-signal.kept-until-terminal-restored", Run(SuspendKeptUntilRestored));
        yield return ("sync160.suspend-signal.not-removed-while-needed", Run(SuspendNotRemovedWhileNeeded));
        yield return ("sync160.suspend-signal.not-registered-twice", Run(SuspendNotRegisteredTwice));
        yield return ("sync160.suspend-signal.set-or-cleared-while-started", Run(SuspendSetOrClearedWhileStarted));
        yield return ("sync160.suspend-signal.not-registered-while-stopped", Run(SuspendNotRegisteredWhileStopped));
        yield return ("sync160.suspend-signal.native-default-registrar", Run(SuspendDefaultRegistrar));
        yield return ("sync160.alt-screen.scroll-reuses-component-output", Run(AltScreenScrollReuse));
        yield return ("sync160.alt-screen.search-current-across-view-frames", Run(AltScreenSearchAcrossViewFrames));
        yield return ("sync160.alt-screen.kitty-uploads-survive-full-redraw", Run(AltScreenKittyFullRedraw));
        yield return ("sync160.overlay-image.kitty-deleted-and-redrawn", Run(OverlayDeletesKittyImage));
        yield return ("sync160.overlay-image.untouched-rows-keep-image", Run(OverlayKeepsOtherRows));
        yield return ("sync160.overlay-image.image-beside-overlay-stays", Run(OverlayBesideImage));
        yield return ("sync160.overlay-image.fullscreen-image-beside-overlay-stays", Run(OverlayBesideFullscreenImage));
        yield return ("sync160.overlay-image.unknown-width-hidden", Run(OverlayUnknownWidth));
        yield return ("sync160.overlay-image.image-line-extent", Run(ImageLineExtents));
        yield return ("sync160.overlay-image.image-inside-lower-overlay-hidden", Run(OverlayOverOverlayImage));
        yield return ("sync160.overlay-image.iterm2-rows-above-image-line", Run(OverlayITerm2));
        yield return ("sync160.overlay-image.fullscreen-placements-removed", Run(OverlayFullscreenPlacements));
    }

    private static Func<Task> Run(Action action) => () => { action(); return Task.CompletedTask; };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Same<T>(T expected, T actual, string message)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected <{expected}>, got <{actual}>"); }
    private static void SameLines(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        var e = expected.ToArray(); var a = actual.ToArray();
        if (!e.SequenceEqual(a, StringComparer.Ordinal)) throw new Exception($"{message}: expected [{string.Join(" | ", e)}], got [{string.Join(" | ", a)}]");
    }
    private static string Identity(string value) => value;
    private static string Plain(string line) => TextUtils.StripTerminalSequences(line).TrimEnd();

    // ------------------------------------------------------------------ fixtures
    private sealed class RecordingTerminal(int columns = 20, int rows = 6) : ITerminal
    {
        private Action<string>? input; private Action? resize;
        public List<string> Writes { get; } = [];
        public string Output => string.Concat(Writes);
        public int Columns { get; private set; } = columns;
        public int Rows { get; private set; } = rows;
        public bool KittyProtocolActive => false;
        public Action? OnStop { get; set; }
        public void Send(string data) => input?.Invoke(data);
        public void Resize(int newColumns, int newRows) { Columns = newColumns; Rows = newRows; resize?.Invoke(); }
        public void Start(Action<string> onInput, Action onResize) { input = onInput; resize = onResize; }
        public void Stop() { OnStop?.Invoke(); input = null; resize = null; }
        public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50) => Task.CompletedTask;
        public void Write(string data) => Writes.Add(data);
        public void MoveBy(int lines) { }
        public void HideCursor() { }
        public void ShowCursor() { }
        public void ClearLine() { }
        public void ClearFromCursor() { }
        public void ClearScreen() { }
        public void SetTitle(string title) { }
        public void SetProgress(bool active) { }
        public void SetProgramStatus(ProgramStatus status) { }
    }

    /// <summary>Returns the same list on every render, like upstream's staticComponent.</summary>
    private sealed class Static(params string[] lines) : IComponent
    {
        private readonly string[] values = lines;
        public IReadOnlyList<string> Render(int width) => values;
        public void Invalidate() { }
    }

    private sealed class Counted(IComponent inner) : IComponent
    {
        public int Renders;
        public IReadOnlyList<string> Render(int width) { Renders++; return inner.Render(width); }
        public void Invalidate() => inner.Invalidate();
    }

    private sealed class WidthRecorder : IComponent
    {
        public List<int> Widths { get; } = [];
        public IReadOnlyList<string> Render(int width) { Widths.Add(width); return ["line"]; }
        public void Invalidate() { }
    }

    private sealed class MouseTarget(string name, int lines, List<string> hits) : IComponent, IMouseHandler
    {
        public IReadOnlyList<string> Render(int width) => Enumerable.Repeat(name, lines).ToList();
        public void Invalidate() { }
        public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent) { hits.Add(name); return new TuiMouseEventResult(Handled: true); }
    }

    private static readonly IComponent Panel = new Static("PANEL");

    private static TuiMainScreen MainScreen(RecordingTerminal terminal, UiLoop? loop = null) => new(terminal, loop ?? UiLoop.CreateManual(), environment: _ => null);
    private static TuiAltScreen AltScreen(RecordingTerminal terminal, UiLoop? loop = null) => new(terminal, loop ?? UiLoop.CreateManual(), options: new() { Environment = _ => null });
    private static string[] Viewport(TuiMainScreen tui) => [.. tui.CaptureRenderState().PreviousLines.Select(Plain)];
    private static string[] Viewport(TuiAltScreen tui) => [.. tui.GetScreenLines().Select(Plain)];

    /// <summary>Runs the loop until the scheduled frame is written (the manual stand-in for upstream's terminal.waitForRender()).</summary>
    private static void WaitForRender(UiLoop loop, RecordingTerminal terminal)
    {
        var before = terminal.Writes.Count;
        var deadline = Environment.TickCount64 + 5000;
        loop.RunPending();
        while (terminal.Writes.Count == before && Environment.TickCount64 < deadline) { Thread.Sleep(2); loop.RunPending(); }
        Check(terminal.Writes.Count > before, "no frame was rendered");
        // Let follow-up frames (a scroll view that settled its position, a second throttled frame) finish too.
        var quietSince = Environment.TickCount64; var seen = terminal.Writes.Count;
        while (Environment.TickCount64 - quietSince < 40 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(2); loop.RunPending();
            if (terminal.Writes.Count != seen) { seen = terminal.Writes.Count; quietSince = Environment.TickCount64; }
        }
    }

    private static void WithCapabilities(ImageProtocol images, Action body)
    {
        var previous = TerminalImage.GetCapabilities();
        TerminalImage.SetCapabilities(new(images, true, true));
        try { body(); }
        finally { TerminalImage.SetCapabilities(previous); TerminalImage.ResetCapabilitiesCache(); }
    }

    private static TuiMouseEvent WheelAt(int y, int width, int height) =>
        new(TuiMouseEventType.Wheel, TuiMouseButton.None, 0, y, 0, y, width, height, WheelDelta: 1);

    // ------------------------------------------------------------------ container.test.ts (#9549)
    private static void ContainerSameLines()
    {
        var text = new Text("hello", 0, 0);
        var inner = new Container(); inner.AddChild(text); inner.AddChild(new Spacer(1));
        var outer = new Container(); outer.AddChild(inner);
        var first = outer.Render(20);
        Check(ReferenceEquals(outer.Render(20), first), "unchanged children must return the same list");
        text.SetText("changed");
        var changed = outer.Render(20);
        Check(!ReferenceEquals(changed, first), "changed child must produce a new list");
        SameLines(["changed", ""], changed.Select(line => line.TrimEnd()), "changed output");
        SameLines(["hello", ""], first.Select(line => line.TrimEnd()), "earlier output is left intact");
    }

    private static void ContainerRerenders()
    {
        var container = new Container();
        container.AddChild(new Text("a", 0, 0));
        var first = container.Render(20);
        container.AddChild(new Text("b", 0, 0));
        SameLines(["a", "b"], container.Render(20).Select(line => line.TrimEnd()), "added child");
        Check(!ReferenceEquals(container.Render(10), first), "width change must produce a new list");
        // Native: Children is a public list here, so a direct change must be seen too.
        var before = container.Render(20);
        container.Children.Insert(0, new Text("z", 0, 0));
        SameLines(["z", "a", "b"], container.Render(20).Select(line => line.TrimEnd()), "directly inserted child");
        SameLines(["a", "b"], before.Select(line => line.TrimEnd()), "earlier output is left intact");
        container.Clear();
        Same(0, container.Render(20).Count, "cleared container");
    }

    private static void BoxReusesOutput()
    {
        var text = new Text("boxed", 0, 0);
        var box = new Box(1, 0); box.AddChild(text);
        var first = box.Render(20);
        Check(ReferenceEquals(box.Render(20), first), "unchanged box must return the same list");
        text.SetText("next");
        Check(!ReferenceEquals(box.Render(20), first), "changed child must produce a new box list");
    }

    private static void ContainerMouse()
    {
        var hits = new List<string>();
        var container = new Container();
        container.AddChild(new MouseTarget("first", 2, hits));
        container.AddChild(new MouseTarget("second", 1, hits));
        container.Render(20);
        container.HandleMouse(WheelAt(2, 20, 3));
        container.HandleMouse(WheelAt(1, 20, 3));
        SameLines(["second", "first"], hits, "mouse targets");
        // Same dispatch through a Box with padding, and without a cached layout for the event width.
        hits.Clear();
        var box = new Box(1, 1);
        box.AddChild(new MouseTarget("first", 2, hits)); box.AddChild(new MouseTarget("second", 1, hits));
        box.HandleMouse(new(TuiMouseEventType.Wheel, TuiMouseButton.None, 1, 3, 1, 3, 20, 5, WheelDelta: 1));
        box.Render(20);
        box.HandleMouse(new(TuiMouseEventType.Wheel, TuiMouseButton.None, 1, 1, 1, 1, 20, 5, WheelDelta: 1));
        SameLines(["second", "first"], hits, "box mouse targets");
    }

    // Native: the read-only contract. A component that changes its output returns a new list; the cached container output of an
    // earlier frame is never written to by a later one.
    private static void ContainerReadOnlyContract()
    {
        var lines = new List<string> { "one" };
        var child = new ListChild(() => lines);
        var container = new Container(); container.AddChild(child);
        var first = container.Render(10);
        lines = ["one", "two"];
        var second = container.Render(10);
        SameLines(["one"], first, "earlier frame");
        SameLines(["one", "two"], second, "later frame");
        Check(first is not List<string>, "container output must not be a mutable list");
    }
    private sealed class ListChild(Func<List<string>> lines) : IComponent
    {
        public IReadOnlyList<string> Render(int width) => lines();
        public void Invalidate() { }
    }

    // ------------------------------------------------------------------ editor.test.ts (#12309)
    private static readonly EditorTheme EditorIdentity = new(Identity, new(Identity, Identity, Identity, Identity, Identity));

    private static CompletionResult ApplyCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix)
    {
        var line = cursorLine < lines.Count ? lines[cursorLine] : "";
        var before = line[..(cursorCol - prefix.Length)]; var after = line[cursorCol..];
        var updated = lines.ToList(); updated[cursorLine] = before + item.Value + after;
        return new(updated, cursorLine, cursorCol - prefix.Length + item.Value.Length);
    }

    private sealed class PathProvider : IAutocompleteProvider
    {
        public Task<AutocompleteSuggestions?> GetSuggestionsAsync(IReadOnlyList<string> lines, int cursorLine, int cursorCol, bool force, CancellationToken cancellationToken) =>
            Task.FromResult<AutocompleteSuggestions?>(new([new("/tmp/x/file-one.md", "file-one.md"), new("/tmp/x/file-two.md", "file-two.md")], "/tmp/x/fi"));
        public CompletionResult ApplyCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix) =>
            Sync160TuiTests.ApplyCompletion(lines, cursorLine, cursorCol, item, prefix);
    }

    private static (Editor Editor, List<string> Submitted, UiLoop Loop) NewEditor()
    {
        var loop = UiLoop.CreateManual();
        var editor = new Editor(MainScreen(new RecordingTerminal(80, 24), loop), EditorIdentity);
        var submitted = new List<string>();
        editor.OnSubmit = submitted.Add;
        return (editor, submitted, loop);
    }

    /// <summary>upstream flushAutocomplete(): lets debounced and awaited suggestion requests finish.</summary>
    private static void FlushAutocomplete(UiLoop loop, Editor editor)
    {
        var deadline = Environment.TickCount64 + 5000;
        loop.RunPending();
        while (!editor.IsShowingAutocomplete() && Environment.TickCount64 < deadline) { Thread.Sleep(2); loop.RunPending(); }
    }

    private static void EditorPathMenu()
    {
        var (editor, submitted, loop) = NewEditor();
        editor.SetAutocompleteProvider(new PathProvider());
        editor.SetText("look at /tmp/x/fi");
        editor.HandleInput("\t");
        FlushAutocomplete(loop, editor);
        Check(editor.IsShowingAutocomplete(), "Tab path menu must show");
        editor.HandleInput("\r");
        SameLines([], submitted, "Enter on a path menu must not submit");
        Same("look at /tmp/x/file-one.md", editor.GetText(), "completed path");
    }

    private static void EditorArgumentMenu()
    {
        var (editor, submitted, loop) = NewEditor();
        editor.SetAutocompleteProvider(new CombinedAutocompleteProvider(
            [new SlashCommand("open", GetArgumentCompletions: prefix =>
                Task.FromResult<List<AutocompleteItem>?>(prefix.StartsWith('/') ? [new("/tmp/alpha", "alpha")] : null))],
            Environment.CurrentDirectory));
        foreach (var character in "/open /tm") editor.HandleInput(character.ToString());
        FlushAutocomplete(loop, editor);
        Check(editor.IsShowingAutocomplete(), "argument menu must show");
        editor.HandleInput("\r");
        SameLines([], submitted, "Enter on an argument menu must not submit");
        Same("/open /tmp/alpha", editor.GetText(), "completed argument");
    }

    private static void EditorCommandMenu()
    {
        var (editor, submitted, loop) = NewEditor();
        editor.SetAutocompleteProvider(new CombinedAutocompleteProvider([new SlashCommand("model"), new SlashCommand("session")], Environment.CurrentDirectory));
        editor.SetText("/mo");
        editor.HandleInput("\t");
        FlushAutocomplete(loop, editor);
        Check(editor.IsShowingAutocomplete(), "slash command menu must show");
        editor.HandleInput("\r");
        SameLines(["/model"], submitted, "Enter on the command-name menu submits the command");
    }

    // ------------------------------------------------------------------ layout.test.ts (#9549)
    private static void LayoutReuse()
    {
        var component = new WidthRecorder();
        var first = LayoutEngine.RenderFrame(component, 10, 2, () => { });
        var reused = LayoutEngine.RenderFrame(component, 10, 2, () => { }, first.RenderCache);
        SameLines(["10"], component.Widths.Select(width => width.ToString()), "reused frame must not render");
        SameLines(["line", ""], reused.Lines.Select(Plain), "reused frame output");
        var narrower = LayoutEngine.RenderFrame(component, 8, 2, () => { }, reused.RenderCache);
        SameLines(["10", "8"], component.Widths.Select(width => width.ToString()), "new width renders");
        SameLines(["8"], narrower.RenderCache[component].Keys.Select(width => width.ToString()), "a frame keeps only the widths it used");
        LayoutEngine.RenderFrame(component, 10, 2, () => { }, narrower.RenderCache);
        SameLines(["10", "8", "10"], component.Widths.Select(width => width.ToString()), "dropped width renders again");
    }

    // ------------------------------------------------------------------ markdown.test.ts (#12017)
    private static readonly MarkdownTheme MarkdownIdentity = new()
    {
        Heading = Identity, Link = Identity, LinkUrl = Identity, Code = Identity, CodeBlock = Identity,
        CodeBlockBorder = Identity, Quote = Identity, QuoteBorder = Identity, Hr = Identity,
        ListBullet = Identity, Bold = Identity, Italic = Identity, Strikethrough = Identity, Underline = Identity
    };

    private static void MarkdownImageHardBreaks() => WithCapabilities(ImageProtocol.Kitty, () =>
    {
        const string image = "\u001b_Ga=T,f=100,q=2,c=20,r=3,C=1;AAAA\u001b\\";
        var markdown = new Markdown("`" + image + "`  \n` `  \n` `\n\n- one", 0, 0, MarkdownIdentity);
        var lines = markdown.Render(80);
        Check(lines.All(line => !line.Contains('\n')), "no rendered line may contain a newline");
        Check(lines[0].Contains(image, StringComparison.Ordinal), "the image stays on the first row");
        SameLines(["", "", "", "- one"], lines.Skip(1).Select(line => TextUtils.StripTerminalSequences(line).Trim()), "rows after the image");
    });

    // Native: the upstream case above also needs the empty row marked puts between a paragraph and a list after a blank source line.
    // The native lexer consumed the paragraph's line break and lost that row. The expected lines are what Pi v1.6.0's Markdown
    // (marked 18.0.11) renders at width 60 for the same sources, without styling.
    private static void MarkdownBlankLineAfterParagraph()
    {
        (string Source, string[] Lines)[] cases =
        [
            ("para\n\n- one\n- two", ["para", "", "- one", "- two"]),
            ("para\n- one\n- two", ["para", "- one", "- two"]),
            ("para\n\n1. one\n2. two", ["para", "", "1. one", "2. two"]),
            ("para\n\n", ["para", ""]),
            ("para\n\npara", ["para", "", "para"]),
            ("para\n\n    indented code", ["para", "", "```", "  indented code", "```"]),
            ("para\n\n```js\ncode\n```", ["para", "", "```js", "  code", "```"]),
            ("para\n\n> quote", ["para", "", "│ quote"]),
            ("> quote\n\n- one\n- two", ["│ quote", "", "- one", "- two"]),
            ("- one\n\n  second paragraph\n- two", ["- one", "", "  second paragraph", "", "- two"]),
            ("1. one\n\n   para\n\n2. two\n\nafter", ["1. one", "", "   para", "", "2. two", "", "after"]),
            ("intro:\n\n- one\n- two\n\noutro", ["intro:", "", "- one", "- two", "", "outro"]),
            ("a\n\n- b\n\n\nc", ["a", "", "- b", "", "c"]),
            ("para one\npara two\n\n- one\n\n- two", ["para one", "para two", "", "- one", "", "- two"]),
            ("`X`  \n`y`  \n`z`\n\n- one", ["X", "y", "z", "", "- one"]),
            ("line one  \nline two  \nline three\n\n1. first", ["line one", "line two", "line three", "", "1. first"]),
        ];
        foreach (var (source, expected) in cases)
            SameLines(expected, new Markdown(source, 0, 0, MarkdownIdentity).Render(60).Select(Plain), System.Text.Json.JsonSerializer.Serialize(source));
    }

    // ------------------------------------------------------------------ settings-list.test.ts (#12245)
    private static void SettingsListCursorKeys()
    {
        var changes = new List<(string Id, string Value)>();
        var list = new SettingsList(
            new[] { "Alpha mode", "Beta mode", "Other" }.Select(label => new SettingItem { Id = label, Label = label, CurrentValue = "off", Values = ["off", "on"] }),
            10, new SettingsListTheme((text, _) => text, (text, _) => text, Identity, "> ", Identity),
            (id, value) => changes.Add((id, value)), () => { }, enableSearch: true);
        // Line 0 is the search input; the selected setting is the row with the cursor below it.
        string? SelectedRow() => list.Render(80).Skip(1).FirstOrDefault(line => line.StartsWith("> ", StringComparison.Ordinal));
        foreach (var character in "mode") list.HandleInput(character.ToString());
        list.HandleInput("\u001b[B");
        var browsed = SelectedRow();
        list.HandleInput("\u001b[D"); // Left
        list.HandleInput("\u001b[H"); // Home
        Check(browsed is not null, "a row must be selected");
        Same(browsed, SelectedRow(), "cursor keys must keep the selection");
        list.HandleInput("\r");
        Same(1, changes.Count, "one change");
        Check(browsed!.Contains(changes[0].Id, StringComparison.Ordinal), "the browsed row is the one changed");
        // Native: a key that does change the query still moves the selection to the first match.
        list.HandleInput("\u001b[C"); // Right: the cursor is at the start after Home
        list.HandleInput("\u007f"); // Backspace: "mode" -> "ode"
        // Line 1 is the blank row under the search input, line 2 the first match.
        Check(list.Render(80)[2].StartsWith("> ", StringComparison.Ordinal) && SelectedRow() != browsed, "a changed query selects the first match: " + SelectedRow());
    }

    // ------------------------------------------------------------------ slice-graphemes.test.ts (#9549)
    private static void SliceStandalone()
    {
        const string line = "│ a → 中文 ✓ │";
        Same(("│ a → ", 6), TextUtils.SliceWithWidth(line, 0, 6, true), "box, arrow and ASCII");
        Same(("中文", 4), TextUtils.SliceWithWidth(line, 6, 4, true), "CJK");
        Same(("文", 2), TextUtils.SliceWithWidth(line, 7, 3, true), "strict slice starting inside a wide cell");
        Same<(int, int)?>((6, 8), TextUtils.GetGraphemeCellRange(line, 7), "cell range of a wide character");
        // The ASCII path keeps styling and positional markers exactly like the cluster path.
        Same(("\u001b[31mcd", 2), TextUtils.SliceWithWidth("ab\u001b[31mcdef", 2, 2, true), "ASCII slice carries the style");
        Same(("\u001b[31mcd", 2), TextUtils.SliceWithWidth("\u001b[31mabcdef", 2, 2, true), "ASCII slice carries a style set before the range");
        Same<(int, int)?>((3, 4), TextUtils.GetGraphemeCellRange("ab\u001b[31mcdef", 3), "ASCII cell range");
        Check(TextUtils.GetGraphemeCellRange("abc", 3) is null, "column past the end");
    }

    private static void SliceClusters()
    {
        // A ZWJ joins the heart to the preceding emoji; the combining accent joins its base letter.
        const string family = "x👨‍❤y";
        Same("👨‍❤", TextUtils.SliceWithWidth(family, 1, 2, true).Text, "ZWJ cluster");
        const string accented = "→é→";
        Same(("é", 1), TextUtils.SliceWithWidth(accented, 1, 1, true), "combining accent");
        Same<(int, int)?>((2, 3), TextUtils.GetGraphemeCellRange(accented, 2), "cell after the accented letter");
    }

    // Native: the per-character path must agree with the segmenter it bypasses, for every unit it treats as standalone.
    private static void SliceFastPathMatchesSegmenter()
    {
        static (string Text, int Width) Reference(string line, int start, int length)
        {
            var end = start + length; var column = 0; var text = new System.Text.StringBuilder(); var width = 0;
            foreach (var segment in TextUtils.Graphemes(line))
            {
                var w = TextUtils.GraphemeWidth(segment);
                if (column >= start && column < end && column + w <= end) { text.Append(segment); width += w; }
                column += w;
                if (column >= end) break;
            }
            return (text.ToString(), width);
        }
        var samples = new List<string>
        {
            "│ a → 中文 ✓ │", "x👨‍❤y", "→é→", "áb", "中‍文", "ｶﾞ→", "한글→a", "é→ñ", "a⃣b", "❤️→", "→😀←", "かな カナ ａｂ"
        };
        // Every BMP unit between two neighbours: standalone units must be their own cluster, others must keep joining.
        for (var code = 0x20; code <= 0xffff; code++)
        {
            if (code is >= 0xd800 and <= 0xdfff) continue;
            samples.Add("a" + (char)code + "中");
            if (code % 7 == 0) samples.Add("→" + (char)code + (char)code + "́z");
        }
        foreach (var sample in samples)
        {
            var total = TextUtils.Graphemes(sample).Sum(TextUtils.GraphemeWidth);
            for (var start = 0; start <= total; start++)
                foreach (var length in new[] { 1, 2, total })
                {
                    var expected = Reference(sample, start, length); var actual = TextUtils.SliceWithWidth(sample, start, length, true);
                    if (expected != actual) throw new Exception($"slice {start}+{length} of U+{string.Join(" U+", sample.Select(c => ((int)c).ToString("X4")))}: expected <{expected}>, got <{actual}>");
                }
        }
    }

    // ------------------------------------------------------------------ suspend-signal.test.ts (#12272)
    /// <summary>Stands in for the process's SIGTSTP listener list.</summary>
    private sealed class SignalSource
    {
        private readonly List<Action> listeners = [];
        public int Count => listeners.Count;
        public int Removals { get; private set; }
        public IDisposable? Register(Action listener) { listeners.Add(listener); return new Registration(this, listener); }
        public void Emit() { foreach (var listener in listeners.ToArray()) listener(); }
        private sealed class Registration(SignalSource owner, Action listener) : IDisposable
        {
            public void Dispose() { if (owner.listeners.Remove(listener)) owner.Removals++; }
        }
    }

    private static void WithSignals(Action<SignalSource, UiLoop, RecordingTerminal, TuiMainScreen> body)
    {
        var previous = TuiBase.SuspendSignalRegistrar;
        var signals = new SignalSource();
        TuiBase.SuspendSignalRegistrar = signals.Register;
        var loop = UiLoop.CreateManual(); var terminal = new RecordingTerminal(40, 10);
        var tui = MainScreen(terminal, loop);
        try { body(signals, loop, terminal, tui); }
        finally { tui.Stop(); TuiBase.SuspendSignalRegistrar = previous; }
    }

    private static void SuspendOnlyWhileStarted() => WithSignals((signals, loop, _, tui) =>
    {
        var calls = 0;
        tui.SetSuspendSignalHandler(() => calls++);
        tui.Start();
        Same(1, signals.Count, "listener while started");
        signals.Emit(); loop.RunPending();
        Same(1, calls, "handler called once");
        tui.Stop();
        Same(0, signals.Count, "listener removed on stop");
        signals.Emit(); loop.RunPending();
        Same(1, calls, "no call after stop");
        tui.Start();
        Same(1, signals.Count, "listener after restart");
    });

    private static void SuspendNotBeforeStart() => WithSignals((signals, _, _, tui) =>
    {
        tui.SetSuspendSignalHandler(() => { });
        Same(0, signals.Count, "no listener before the first start");
        tui.Start();
        Same(1, signals.Count, "listener after start");
    });

    private static void SuspendKeptUntilRestored() => WithSignals((signals, _, terminal, tui) =>
    {
        var counts = new List<int>();
        terminal.OnStop = () => counts.Add(signals.Count);
        tui.SetSuspendSignalHandler(() => { });
        tui.Start();
        tui.Stop();
        terminal.OnStop = null;
        SameLines(["1"], counts.Select(count => count.ToString()), "listener still present while the terminal is restored");
        Same(0, signals.Count, "listener removed after stop");
    });

    // Removing the last listener restores the default action, so a remove/add pair would let a SIGTSTP in between stop the
    // process while the TUI runs.
    private static void SuspendNotRemovedWhileNeeded() => WithSignals((signals, _, _, tui) =>
    {
        tui.SetSuspendSignalHandler(() => { });
        tui.Start();
        var removals = signals.Removals;
        tui.SetSuspendSignalHandler(() => { });
        tui.Start();
        Same(removals, signals.Removals, "no removal while the listener stays needed");
        Same(1, signals.Count, "one listener");
    });

    private static void SuspendNotRegisteredTwice() => WithSignals((signals, _, _, tui) =>
    {
        tui.SetSuspendSignalHandler(() => { });
        tui.Start();
        tui.Start();
        Same(1, signals.Count, "one listener after two starts");
    });

    private static void SuspendSetOrClearedWhileStarted() => WithSignals((signals, loop, _, tui) =>
    {
        tui.Start();
        Same(0, signals.Count, "no listener without a handler");
        var calls = 0;
        tui.SetSuspendSignalHandler(() => calls++);
        Same(1, signals.Count, "handler set while started registers");
        // Native: a signal already queued to the loop when the handler is cleared is dropped.
        signals.Emit();
        tui.SetSuspendSignalHandler(null);
        Same(0, signals.Count, "handler cleared while started unregisters");
        loop.RunPending();
        Same(0, calls, "a queued signal is dropped once the listener is gone");
    });

    private static void SuspendNotRegisteredWhileStopped() => WithSignals((signals, _, _, tui) =>
    {
        tui.Start();
        tui.Stop();
        tui.SetSuspendSignalHandler(() => { });
        Same(0, signals.Count, "no listener while stopped");
    });

    // Native: the real registrar. Windows has no SIGTSTP, so it registers nothing there; elsewhere it returns a registration that
    // is disposed on stop. The signal itself is not raised: without a listener it would stop the test process.
    private static void SuspendDefaultRegistrar()
    {
        var registrar = TuiBase.SuspendSignalRegistrar;
        var registration = registrar(() => { });
        try { Same(!OperatingSystem.IsWindows(), registration is not null, "default registrar registers only where SIGTSTP exists"); }
        finally { registration?.Dispose(); }
        var tui = MainScreen(new RecordingTerminal(40, 10));
        tui.SetSuspendSignalHandler(() => { });
        tui.Start();
        tui.Stop();
    }

    // ------------------------------------------------------------------ tui-alt-screen.test.ts (#9549, #12010)
    private static void AltScreenScrollReuse()
    {
        var loop = UiLoop.CreateManual(); var terminal = new RecordingTerminal(20, 4);
        var tui = AltScreen(terminal, loop);
        var text = new Text(string.Join("\n", Enumerable.Range(1, 10).Select(index => $"line {index}")), 0, 0);
        var counted = new Counted(text);
        tui.SetLayoutRoot(new ScrollView(counted, new(ScrollViewFollow.End, Primary: true)));
        tui.Start();
        try
        {
            WaitForRender(loop, terminal);
            var initial = counted.Renders;
            Check(initial > 0, "content rendered once");
            terminal.Send("\u001b[<64;1;1M");
            WaitForRender(loop, terminal);
            tui.ScrollBy(-1);
            WaitForRender(loop, terminal);
            Same(initial, counted.Renders, "scroll frames must not render components");
            SameLines(["line 5", "line 6", "line 7", "line 8"], Viewport(tui), "scrolled viewport");
            // Page keys go through the same view-only path.
            terminal.Send("\u001b[5~");
            WaitForRender(loop, terminal);
            Same(initial, counted.Renders, "key scrolling must not render components");
            text.SetText("changed");
            tui.RequestRender();
            WaitForRender(loop, terminal);
            Check(counted.Renders > initial, "content change renders components");
            Same("changed", Viewport(tui)[0], "changed content shown");
        }
        finally { tui.Stop(); }
    }

    private static void AltScreenSearchAcrossViewFrames()
    {
        var loop = UiLoop.CreateManual(); var terminal = new RecordingTerminal(60, 8);
        var tui = AltScreen(terminal, loop);
        var lines = Enumerable.Range(0, 12).Select(index => index == 4 ? "line 5 needle one" : $"line {index + 1}").ToArray();
        var transcriptText = new Text(string.Join("\n", lines), 0, 0);
        var transcript = new ScrollView(transcriptText, new(ScrollViewFollow.End, Primary: true));
        tui.SetLayoutRoot(transcript);
        tui.Start();
        try
        {
            WaitForRender(loop, terminal);
            terminal.Send("\u001b[102;6u");
            terminal.Send("needle");
            WaitForRender(loop, terminal);
            Check(Viewport(tui).Any(line => line.Contains("1/1", StringComparison.Ordinal)), "one match: " + string.Join(" | ", Viewport(tui)));
            for (var index = 0; index < 3; index++) terminal.Send("\u001b[<65;1;4M");
            WaitForRender(loop, terminal);
            Check(transcript.ScrollTop > 0, "scrolled away from the top");
            lines[10] = "line 11 needle two";
            transcriptText.SetText(string.Join("\n", lines));
            tui.RequestRender();
            WaitForRender(loop, terminal);
            Check(Viewport(tui).Any(line => line.Contains("1/2", StringComparison.Ordinal)), "second match found after a content change: " + string.Join(" | ", Viewport(tui)));
            terminal.Send("\u001b[103;6u");
            WaitForRender(loop, terminal);
            Check(Viewport(tui).Any(line => line.Contains("2/2", StringComparison.Ordinal)), "previous match wraps to the new one: " + string.Join(" | ", Viewport(tui)));
            Check(Viewport(tui).Any(line => line.Contains("line 11 needle two", StringComparison.Ordinal)), "new match revealed");
        }
        finally { tui.Stop(); }
    }

    // Kitty frees image data without placements on ED 2, so a full redraw that deleted placements and then cleared the screen left
    // placement-only reuse pointing at nothing.
    private static void AltScreenKittyFullRedraw() => WithCapabilities(ImageProtocol.Kitty, () =>
    {
        var terminal = new RecordingTerminal(20, 6);
        const int imageId = 12010;
        var imageLine = TerminalImage.EncodeKitty("AAAA", 2, 3, imageId, false);
        TerminalImage.RegisterKittyImageMetadata(new(imageId, 2, 3, 100, 100));
        var tui = AltScreen(terminal);
        tui.SetLayoutRoot(new Static("before", imageLine, "", "", "after"));
        tui.Start();
        try
        {
            tui.RenderNow();
            Check(terminal.Output.Contains("\u001b_Ga=T", StringComparison.Ordinal), "first frame uploads the image");
            terminal.Writes.Clear();
            var redraws = tui.FullRedraws;
            terminal.Resize(20, 8);
            tui.RenderNow();
            var writes = terminal.Output;
            Same(redraws + 1, tui.FullRedraws, "the resize is a full redraw");
            Check(writes.Contains("\u001b_Ga=p,q=2", StringComparison.Ordinal), "the uploaded image is placed again");
            Check(!writes.Contains("\u001b_Ga=T", StringComparison.Ordinal), "the image data is not uploaded again");
            Check(!writes.Contains("\u001b[2J", StringComparison.Ordinal), "the screen is not cleared with ED 2");
            Check(writes.Contains("\u001b[8;1H\u001b[2K", StringComparison.Ordinal), "every row is cleared with EL 2 instead");
            Check(Viewport(tui).Any(line => line == "after"), "text is drawn");
        }
        finally { tui.Stop(); }
    });

    // ------------------------------------------------------------------ overlay-terminal-image.test.ts (#11584)
    private static void OverlayDeletesKittyImage() => WithCapabilities(ImageProtocol.Kitty, () =>
    {
        var terminal = new RecordingTerminal(20, 6); var tui = MainScreen(terminal);
        try
        {
            var image = TerminalImage.EncodeKitty("AAAA", 4, 4, 11584, false);
            tui.AddChild(new Static("top", image, "", "", "", "bottom"));
            tui.Start(); tui.RenderNow();
            terminal.Writes.Clear();
            // Row 3 is the third image row, below the image line itself.
            var overlay = tui.ShowOverlay(Panel, new OverlayOptions { Row = 3, Col = 2, Width = 8 });
            tui.RenderNow();
            Check(terminal.Output.Contains(TerminalImage.DeleteKittyImage(11584), StringComparison.Ordinal), "the covered image is deleted");
            Check(!terminal.Output.Contains("\u001b_Ga=T", StringComparison.Ordinal), "the covered image is not drawn again");
            Same("  PANEL", Viewport(tui)[3], "overlay row");
            terminal.Writes.Clear();
            overlay.Hide();
            tui.RenderNow();
            Check(terminal.Output.Contains(image, StringComparison.Ordinal), "the image is drawn again once uncovered");
        }
        finally { tui.Stop(); }
    });

    private static void OverlayKeepsOtherRows() => WithCapabilities(ImageProtocol.Kitty, () =>
    {
        var terminal = new RecordingTerminal(20, 6); var tui = MainScreen(terminal);
        try
        {
            var image = TerminalImage.EncodeKitty("AAAA", 4, 2, 11585, false);
            tui.AddChild(new Static(image, "", "text", "", "", ""));
            tui.Start(); tui.RenderNow();
            terminal.Writes.Clear();
            tui.ShowOverlay(Panel, new OverlayOptions { Row = 4, Col = 0, Width = 8 });
            tui.RenderNow();
            Check(!terminal.Output.Contains(TerminalImage.DeleteKittyImage(11585), StringComparison.Ordinal), "an image on other rows is kept");
            Same("PANEL", Viewport(tui)[4], "overlay row");
        }
        finally { tui.Stop(); }
    });

    private static void OverlayBesideImage() => WithCapabilities(ImageProtocol.Kitty, () =>
    {
        var terminal = new RecordingTerminal(20, 6); var tui = MainScreen(terminal);
        try
        {
            var image = TerminalImage.EncodeKitty("AAAA", 4, 4, 11588, false);
            tui.AddChild(new Static("top", image, "", "", "", "bottom"));
            tui.Start(); tui.RenderNow();
            terminal.Writes.Clear();
            // A sidebar at columns 10-17 next to the image at columns 0-3.
            tui.ShowOverlay(new Static("SIDE", "SIDE", "SIDE", "SIDE", "SIDE", "SIDE"), new OverlayOptions { Row = 0, Col = 10, Width = 8 });
            tui.RenderNow();
            // The main screen redraws an image whenever a row it covers changes, so it may delete it first.
            var writes = terminal.Output;
            Check(writes.LastIndexOf(image, StringComparison.Ordinal) > writes.LastIndexOf(TerminalImage.DeleteKittyImage(11588), StringComparison.Ordinal),
                "the image beside the sidebar is drawn");
            var viewport = Viewport(tui);
            Same("SIDE", viewport[2][10..].TrimEnd(), "rows below the image line show the sidebar");
            // The image line itself still passes through, so the sidebar skips that row.
            Same("", viewport[1].Trim(), "image line row");
        }
        finally { tui.Stop(); }
    });

    private static void OverlayBesideFullscreenImage() => WithCapabilities(ImageProtocol.Kitty, () =>
    {
        var terminal = new RecordingTerminal(20, 6); var tui = AltScreen(terminal);
        try
        {
            const int imageId = 11591;
            var image = TerminalImage.EncodeKitty("AAAA", 4, 3, imageId, false);
            TerminalImage.RegisterKittyImageMetadata(new(imageId, 4, 3, 100, 100));
            tui.SetLayoutRoot(new Static("top", image, "", "", "after", ""));
            tui.Start(); tui.RenderNow();
            terminal.Writes.Clear();
            tui.ShowOverlay(new Static("SIDE", "SIDE", "SIDE", "SIDE"), new OverlayOptions { Row = 1, Col = 10, Width = 8 });
            tui.RenderNow();
            Check(terminal.Output.Contains("\u001b_Ga=p,q=2", StringComparison.Ordinal), "the image is placed again after the redraw");
            Check(terminal.Output.Contains($"i={imageId}", StringComparison.Ordinal), "the placement names the image");
            Same("SIDE", Viewport(tui)[2][10..].TrimEnd(), "sidebar row");
        }
        finally { tui.Stop(); }
    });

    private static void OverlayUnknownWidth() => WithCapabilities(ImageProtocol.Kitty, () =>
    {
        var terminal = new RecordingTerminal(20, 6); var tui = MainScreen(terminal);
        try
        {
            // No `c=` and no registered metadata: the width cannot be read.
            var image = TerminalImage.EncodeKitty("AAAA", rows: 2, imageId: 11589, moveCursor: false);
            tui.AddChild(new Static("top", image, "", "", "", ""));
            tui.Start(); tui.RenderNow();
            terminal.Writes.Clear();
            tui.ShowOverlay(Panel, new OverlayOptions { Row = 2, Col = 15, Width = 5 });
            tui.RenderNow();
            Check(terminal.Output.Contains(TerminalImage.DeleteKittyImage(11589), StringComparison.Ordinal), "an image of unknown width is hidden");
        }
        finally { tui.Stop(); }
    });

    private static void ImageLineExtents()
    {
        var kitty = "  " + TerminalImage.EncodeKitty("AAAA", 6, 3, 11590, false);
        Same<TerminalImage.ImageLineExtent?>(new(0, 2, 2, 8), TerminalImage.GetImageLineExtent(kitty), "Kitty extent");
        var iterm2 = "\u001b[2A" + TerminalImage.EncodeITerm2("AAAA", "5", "auto");
        Same<TerminalImage.ImageLineExtent?>(new(-2, 0, 0, 5), TerminalImage.GetImageLineExtent(iterm2), "iTerm2 extent");
        var autoWidth = TerminalImage.EncodeITerm2("AAAA", "50%", "auto");
        Same(int.MaxValue, TerminalImage.GetImageLineExtent(autoWidth)?.EndCol, "a percent width is unknown");
        Check(TerminalImage.GetImageLineExtent("plain text") is null, "plain text has no extent");
        Same("  text", TerminalImage.StripImageSequences(kitty + "text"), "Kitty sequence stripped");
        Same("ab", TerminalImage.StripImageSequences("a" + iterm2 + "b"), "iTerm2 sequence and its cursor-up prefix stripped");
    }

    private static void OverlayOverOverlayImage() => WithCapabilities(ImageProtocol.Kitty, () =>
    {
        var terminal = new RecordingTerminal(20, 6); var tui = MainScreen(terminal);
        try
        {
            var image = TerminalImage.EncodeKitty("AAAA", 4, 3, 11587, false);
            tui.AddChild(new Static("", "", "", "", "", ""));
            tui.Start();
            tui.ShowOverlay(new Static(image, "", ""), new OverlayOptions { Row = 0, Col = 0, Width = 10 });
            tui.RenderNow();
            Check(terminal.Output.Contains(image, StringComparison.Ordinal), "the overlay's own image is drawn");
            terminal.Writes.Clear();
            tui.ShowOverlay(Panel, new OverlayOptions { Row = 2, Col = 0, Width = 8 });
            tui.RenderNow();
            Check(terminal.Output.Contains(TerminalImage.DeleteKittyImage(11587), StringComparison.Ordinal), "the image under the top overlay is deleted");
            Same("PANEL", Viewport(tui)[2], "top overlay row");
        }
        finally { tui.Stop(); }
    });

    private static void OverlayITerm2() => WithCapabilities(ImageProtocol.ITerm2, () =>
    {
        var terminal = new RecordingTerminal(20, 6); var tui = MainScreen(terminal);
        try
        {
            var image = "\u001b[2A" + TerminalImage.EncodeITerm2("AAAA", "4", "auto");
            tui.AddChild(new Static("top", "", "", image, "", "bottom"));
            tui.Start(); tui.RenderNow();
            Check(terminal.Output.Contains(image, StringComparison.Ordinal), "the image is drawn first");
            terminal.Writes.Clear();
            var overlay = tui.ShowOverlay(Panel, new OverlayOptions { Row = 1, Col = 0, Width = 8 });
            tui.RenderNow();
            Check(!terminal.Output.Contains("\u001b]1337;File=", StringComparison.Ordinal), "the covered image is not drawn again");
            Check(terminal.Output.Contains("PANEL", StringComparison.Ordinal), "the overlay is drawn");
            terminal.Writes.Clear();
            overlay.Hide();
            tui.RenderNow();
            Check(terminal.Output.Contains(image, StringComparison.Ordinal), "the image is drawn again once uncovered");
        }
        finally { tui.Stop(); }
    });

    private static void OverlayFullscreenPlacements() => WithCapabilities(ImageProtocol.Kitty, () =>
    {
        var terminal = new RecordingTerminal(20, 6); var tui = AltScreen(terminal);
        try
        {
            const int imageId = 11586;
            var image = TerminalImage.EncodeKitty("AAAA", 4, 3, imageId, false);
            TerminalImage.RegisterKittyImageMetadata(new(imageId, 4, 3, 100, 100));
            tui.SetLayoutRoot(new Static("top", image, "", "", "after", ""));
            tui.Start(); tui.RenderNow();
            terminal.Writes.Clear();
            var overlay = tui.ShowOverlay(Panel, new OverlayOptions { Row = 2, Col = 1, Width = 8 });
            tui.RenderNow();
            Check(terminal.Output.Contains("\u001b_Ga=d,d=a,q=2\u001b\\", StringComparison.Ordinal), "visible placements are deleted");
            Check(!terminal.Output.Contains($"i={imageId}", StringComparison.Ordinal), "the covered image is not placed again");
            Same(" PANEL", Viewport(tui)[2], "overlay row");
            terminal.Writes.Clear();
            overlay.Hide();
            tui.RenderNow();
            Check(terminal.Output.Contains("\u001b_Ga=p,q=2", StringComparison.Ordinal), "the uploaded image is placed again");
            Check(!terminal.Output.Contains("\u001b_Ga=T", StringComparison.Ordinal), "the image data is not uploaded again");
        }
        finally { tui.Stop(); }
    });
}
