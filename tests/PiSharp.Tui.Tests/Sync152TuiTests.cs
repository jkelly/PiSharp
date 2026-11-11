// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/tui/src/components/markdown.ts, packages/tui/src/tui-main-screen.ts, packages/tui/src/utils.ts, packages/tui/src/terminal-image.ts.
// Authored native regression; not an upstream execution capture.
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Pi;

// Authored regressions corresponding to Pi v1.5.1/v1.5.2; historical replay captures remain unchanged.
internal static class Sync152TuiTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("sync152.markdown-hidden-comments", Run(MarkdownComments));
        yield return ("sync152.alacritty-environment", Run(Alacritty));
        yield return ("sync152.cursor-markers-and-horizontal-slices", Run(CursorMarkers));
        yield return ("sync152.kitty-images-after-covered-row-writes", Run(KittyImages));
        yield return ("sync152.paste-short-marker-renumbering", Run(PasteMarkers));
    }
    private static Func<Task> Run(Action action) => () => { action(); return Task.CompletedTask; };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Identity(string value) => value;
    private static readonly MarkdownTheme Theme = new()
    {
        Heading = Identity, Link = Identity, LinkUrl = Identity, Code = Identity, CodeBlock = Identity,
        CodeBlockBorder = Identity, Quote = Identity, QuoteBorder = Identity, Hr = Identity,
        ListBullet = Identity, Bold = Identity, Italic = Identity, Strikethrough = Identity, Underline = Identity
    };
    private static string Plain(string source) => string.Join("\n", new Markdown(source, 0, 0, Theme).Render(60).Select(line => line.TrimEnd()));
    private static void SameMarkdown(string withComments, string expected) =>
        Check(Plain(withComments) == Plain(expected), $"Markdown mismatch: {Plain(withComments)} != {Plain(expected)}");
    private static void MarkdownComments()
    {
        SameMarkdown("## [1.0.0]\n\n<!-- release-notes:start -->\nFixed a bug.\n\nSee <!-- todo --> the docs.", "## [1.0.0]\n\nFixed a bug.\n\nSee the docs.");
        SameMarkdown("<!-- start -->\n\n## A\n\n<!-- a -->\n\n- item\n\n<!-- b -->\n\n## B\n\n<!-- end -->", "## A\n\n- item\n\n## B");
        SameMarkdown("<!--\nmulti\nline\n-->\ntext", "text");
        SameMarkdown("text\n\n<!-- streaming", "text");
        SameMarkdown("> q\n>\n> <!-- c -->\n>\n> r\n\n- a\n\n  <!-- c -->\n\n  b\n- <!-- c --> item", "> q\n>\n> r\n\n- a\n\n  b\n- item");
        Check(Plain("with <tag> markers") == "with <tag> markers", "ordinary HTML retained");
        Check(Plain("<!-- c --> trailing text") == "trailing text", "following text retained");
        Check(Plain("<div>\n<!-- c -->a</div>") == "<div>\na</div>", "HTML block comment hidden");
        Check(Plain("`<!-- literal -->`").Contains("<!-- literal -->", StringComparison.Ordinal), "inline code retained");
        Check(Plain("```text\n<!-- literal -->\n```").Contains("<!-- literal -->", StringComparison.Ordinal), "code fence retained");
    }
    private static void Alacritty()
    {
        var previous = TerminalImage.Environment;
        try
        {
            foreach (var environment in new[]
            {
                new Dictionary<string, string> { ["TERM"] = "alacritty" },
                new Dictionary<string, string> { ["TERM"] = "alacritty-direct" },
                new Dictionary<string, string> { ["TERM"] = "xterm-256color", ["ALACRITTY_WINDOW_ID"] = "123" }
            })
            {
                TerminalImage.Environment = key => environment.GetValueOrDefault(key);
                var actual = TerminalImage.DetectCapabilities(() => false);
                Check(actual == new TerminalCapabilities(ImageProtocol.None, true, true), "Alacritty capability fallback");
                environment["TMUX"] = "1";
                Check(!TerminalImage.DetectCapabilities(() => false).Hyperlinks, "tmux capability precedence");
            }
        }
        finally { TerminalImage.Environment = previous; TerminalImage.ResetCapabilitiesCache(); }
    }
    private static void CursorMarkers()
    {
        var marker = TuiBase.CursorMarker;
        Check(TextUtils.SliceWithWidth("a" + marker + "bc", 2, 1, true).Text == "c", "pre-slice APC must not carry");
        Check(TextUtils.SliceWithWidth("a\u001b[31mbc", 2, 1, true).Text == "\u001b[31mc", "persistent SGR must carry");
        foreach (var fullscreen in new[] { false, true })
        {
            var terminal = new RecordingTerminal();
            TuiBase tui = fullscreen ? new TuiAltScreen(terminal, UiLoop.CreateManual()) : new TuiMainScreen(terminal, UiLoop.CreateManual());
            tui.AddChild(new Lines("offscreen" + marker, "other" + marker, "ab" + marker + "c" + marker));
            tui.Start();
            try
            {
                tui.RenderNow();
                Check(!terminal.Output.Contains(marker, StringComparison.Ordinal), "no cursor sentinels may reach terminal");
                Check(terminal.Output.Contains("abc", StringComparison.Ordinal), "marker cleanup preserves visible text");
            }
            finally { tui.Stop(); }
        }
    }
    private static void KittyImages()
    {
        var previous = TerminalImage.GetCapabilities();
        try
        {
            TerminalImage.SetCapabilities(new(ImageProtocol.Kitty, true, true));
            var terminal = new RecordingTerminal();
            var tui = new TuiAltScreen(terminal, UiLoop.CreateManual(), options: new() { Environment = _ => null });
            const int id = 11835;
            var image = TerminalImage.EncodeKitty("AAAA", 2, 3, id, false);
            TerminalImage.RegisterKittyImageMetadata(new(id, 2, 3, 100, 100));
            var lines = new Lines("before", image, "", "", "after");
            tui.SetLayoutRoot(lines);
            tui.Start();
            try
            {
                tui.RenderNow();
                var output = terminal.Output;
                var transmission = output.LastIndexOf("\u001b_Ga=T", StringComparison.Ordinal);
                Check(transmission > output.LastIndexOf("\u001b[4;1H\u001b[2K", StringComparison.Ordinal), "covered rows cleared before image");
                Check(transmission > output.LastIndexOf("after", StringComparison.Ordinal), "text written before image");
                terminal.Writes.Clear();
                lines.Values[2] = "changed";
                tui.RenderNow();
                output = terminal.Output;
                Check(output.Contains("\u001b_Ga=p", StringComparison.Ordinal), "covered-row change redraws cached placement");
                Check(output.LastIndexOf("\u001b_Ga=p", StringComparison.Ordinal) > output.IndexOf("changed", StringComparison.Ordinal), "replacement image follows row write");
            }
            finally { tui.Stop(); }
        }
        finally { TerminalImage.SetCapabilities(previous); }
    }
    private static void PasteMarkers()
    {
        var a = new string('a', 1001); var b = new string('b', 1001);
        var native = new TerminalTextEditorPasteController();
        native.HandleInput(new TerminalPaste(a)); native.HandleInput(new TerminalPaste(b));
        native.HandleInput(new TerminalKey("u", TerminalModifiers.Control));
        native.InsertText("[paste #1][paste #2]");
        native.HandleInput(new TerminalKey("Left")); native.HandleInput(new TerminalKey("Backspace"));
        Check(native.Snapshot.Text == "[paste #1]", "native short marker remains valid after renumbering");
        Check(native.GetExpandedText() == b, "native renumbered marker expands to second paste");
        var terminal = new RecordingTerminal();
        var editor = new Editor(new TuiMainScreen(terminal, UiLoop.CreateManual()), new(Identity, new(Identity, Identity, Identity, Identity, Identity)));
        editor.HandleInput("\u001b[200~" + a + "\u001b[201~"); editor.HandleInput("\u001b[200~" + b + "\u001b[201~");
        editor.HandleInput("\u0015");
        foreach (var character in "[paste #1][paste #2]") editor.HandleInput(character.ToString());
        editor.HandleInput("\u001b[D"); editor.HandleInput("\u007f");
        Check(editor.GetText() == "[paste #1]" && editor.GetExpandedText() == b, "legacy editor short marker parity");
    }
    private sealed class Lines(params string[] lines) : IComponent
    {
        public List<string> Values { get; } = [.. lines];
        public List<string> Render(int width) => [.. Values];
        public void Invalidate() { }
    }
    private sealed class RecordingTerminal : ITerminal
    {
        public List<string> Writes { get; } = [];
        public string Output => string.Concat(Writes);
        public int Columns => 60;
        public int Rows => 5;
        public bool KittyProtocolActive => false;
        public void Start(Action<string> onInput, Action onResize) { }
        public void Stop() { }
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
}
