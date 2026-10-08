using System.Collections.Immutable;
using PiSharp.Tui;
using PiSharp.Tui.Components.Text;
using PiSharp.Tui.Rendering;
using TerminalText = PiSharp.Tui.Components.Text.TerminalText;

// Authored expectations from pinned Source, not captured upstream observations or acceptance evidence.
internal static class TerminalTextComponentTests
{
    private const string Reset = "\u001b[0m";
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("terminal-text.empty-and-ecmascript-whitespace", Empty);
        yield return ("terminal-text.default-padding-and-narrow-margin-reduction", Padding);
        yield return ("terminal-text.crlf-cr-lf-tabs-and-trailing-empty-line", Newlines);
        yield return ("terminal-text.word-cjk-and-whole-grapheme-wrap", Wrapping);
        yield return ("terminal-text.underline-colors-and-literal-newline-state", Ansi);
        yield return ("terminal-text.osc8-bel-st-wrap-and-truncation", Hyperlinks);
        yield return ("terminal-text.cache-callback-order-and-shared-padding", Cache);
        yield return ("terminal-text.callback-fault-invalid-output-and-retry", CallbackFailure);
        yield return ("terminal-text.source-reentrant-cache-and-callback-replacement", Reentrant);
        yield return ("terminal-text.truncated-default-dots-and-narrow-ellipsis", Ellipsis);
        yield return ("terminal-text.truncated-first-lf-cr-tabs-and-unreduced-padding", TruncatedPadding);
        yield return ("terminal-text.truncated-wide-emoji-combining-and-ansi-split-ri", TruncatedUnicode);
        yield return ("terminal-text.source-ansi-recognition-and-exhaustion", AnsiRecognition);
        yield return ("terminal-text.frame-control-projection-before-wrap-and-callback-exclusion", SafeFrames);
        yield return ("terminal-text.frame-empty-clear-crop-and-whole-cluster-clip", FrameClipping);
        yield return ("terminal-text.component-and-frame-resource-limits", Bounds);
        yield return ("terminal-text.actual-held-write-cache-equality-and-borrowed-ownership", HeldWrite);
        yield return ("terminal-text.actual-physical-fault-and-full-recovery", FaultRecovery);
    }

    private static Task Empty()
    {
        foreach (var value in new[] { "", " \t\r\n", "\ufeff", "\u00a0\u1680\u2000\u2028\u202f\u3000" })
        {
            var callbacks = 0; var text = new TerminalText(value, customBackground: row => { callbacks++; return row; });
            Rows(text.Render(3)); Rows(text.Render(3)); Equal(0, callbacks);
        }
        Rows(new TerminalText("\u0085", 0, 0).Render(3), "\u0085   "); // .NET Trim would wrongly suppress this.
        Rows(new TerminalTruncatedText("").Render(3), "   "); // Source TruncatedText always has its content row.
        return Task.CompletedTask;
    }
    private static Task Padding()
    {
        Rows(new TerminalText("hello").Render(8), "        ", " hello  ", "        ");
        Rows(new TerminalText("ab").Render(1), " ", "a", "b", " ");
        Rows(new TerminalText("abc", 20, 0).Render(3), " a ", " b ", " c ");
        return Task.CompletedTask;
    }
    private static Task Newlines()
    {
        Rows(new TerminalText("a\tb\r\nc\rd\n", 0, 0).Render(6), "a   b ", "c     ", "d     ", "      ");
        Rows(new TerminalText("a  \nb", 0, 0).Render(3), "a  ", "b  ");
        return Task.CompletedTask;
    }
    private static Task Wrapping()
    {
        Rows(new TerminalText("ab cd", 0, 0).Render(4), "ab  ", "cd  ");
        Rows(new TerminalText("ab中cd", 0, 0).Render(4), "ab中", "cd  ");
        Rows(new TerminalText("Ae\u0301🙂B", 0, 0).Render(2), "Ae\u0301", "🙂", "B ");
        Rows(new TerminalText("中", 0, 0).Render(1), " ", "中"); // Source oversized grapheme is preserved in raw rows.
        const string family = "👩‍👩‍👧‍👦";
        Rows(new TerminalText("A" + family + "B", 0, 0).Render(2), "A ", family, "B ");
        return Task.CompletedTask;
    }
    private static Task Ansi()
    {
        const string underline = "\u001b[4;31m";
        Rows(new TerminalText(underline + "ab cd", 0, 0).Render(2),
            underline + "ab\u001b[24m", underline + "cd");
        const string colors = "\u001b[1;38;2;1;2;3;48;5;12m";
        Rows(new TerminalText(colors + "A\n\u001b[22;39mB\nC", 0, 0).Render(2),
            colors + "A ", colors + "\u001b[22;39mB ", "\u001b[48;5;12mC ");
        Rows(new TerminalText("\u001b[4mabcd", 0, 0).Render(2), "\u001b[4mab\u001b[24m", "\u001b[4mcd");
        return Task.CompletedTask;
    }
    private static Task Hyperlinks()
    {
        foreach (var terminator in new[] { "\u0007", "\u001b\\" })
        {
            var open = "\u001b]8;id=1;https://example.invalid/" + terminator;
            var close = "\u001b]8;;" + terminator;
            Rows(new TerminalText(open + "ab cd", 0, 0).Render(2), open + "ab" + close, open + "cd");
            Rows(new TerminalTruncatedText(open + "abcdef").Render(5), open + "ab" + close + Reset + "..." + Reset);
            Rows(new TerminalTruncatedText(open + "ab" + close).Render(5), open + "ab" + close + "   ");
            Rows(new TerminalText(open + "A" + Reset + "\nB", 0, 0).Render(2), open + "A" + Reset + " ", open + "B ");
        }
        return Task.CompletedTask;
    }
    private static Task Cache()
    {
        var inputs = new List<string>();
        var text = new TerminalText("ab cd", 0, 1, row => { inputs.Add(row); return inputs.Count + ":" + row; });
        var first = text.Render(2);
        Rows(first, "3:  ", "1:ab", "2:cd", "3:  "); Equal("ab|cd|  ", string.Join("|", inputs));
        True(first.Equals(text.Render(2))); Equal(3, inputs.Count);
        text.Invalidate(); text.Render(2); Equal(6, inputs.Count);
        text.SetText("ab cd"); text.Render(2); Equal(9, inputs.Count);
        text.Render(3); Equal(12, inputs.Count);
        text.SetCustomBackground(null); Rows(text.Render(2), "  ", "ab", "cd", "  "); Equal(12, inputs.Count);
        return Task.CompletedTask;
    }
    private static Task CallbackFailure()
    {
        var calls = 0;
        var text = new TerminalText("x", 0, 0, row => ++calls == 1 ? throw new IOException("background fault") : row);
        Throws<IOException>(() => text.Render(2)); Rows(text.Render(2), "x "); Equal(2, calls);
        text.SetCustomBackground(_ => "\ud800"); Failure(TerminalRenderFailure.InvalidUnicode, () => text.Render(2));
        text.SetCustomBackground(null); Rows(text.Render(2), "x ");
        Failure(TerminalRenderFailure.InvalidUnicode, () => text.SetText("\udc00")); Rows(text.Render(2), "x ");
        return Task.CompletedTask;
    }
    private static Task Reentrant()
    {
        var text = new TerminalText("old", 0, 0); var once = false;
        text.SetCustomBackground(row => { if (!once) { once = true; text.SetText("new"); } return row; });
        var old = text.Render(3); Rows(old, "old"); True(old.Equals(text.Render(3)));
        Equal("new", TerminalTextFrameFactory.Create(text, 1, 3).Rows[0].Text);
        True(old.Equals(text.Render(3))); // Safe layout neither reads nor repairs the Source reentrant cache.
        text.Invalidate(); Rows(text.Render(3), "new"); // Explicitly retain Source's post-callback cache key.
        var replacement = new TerminalText("a\nb", 0, 0);
        replacement.SetCustomBackground(row => { replacement.SetCustomBackground(next => "second:" + next); return "first:" + row; });
        Rows(replacement.Render(1), "first:a", "second:b");
        return Task.CompletedTask;
    }
    private static Task Ellipsis()
    {
        for (var width = 1; width <= 5; width++)
        {
            var prefix = width > 3 ? "abcdef"[..(width - 3)] : "";
            Rows(new TerminalTruncatedText("abcdef").Render(width), prefix + Reset + new string('.', Math.Min(3, width)) + Reset);
        }
        Rows(new TerminalTruncatedText("abcdef").Render(6), "abcdef");
        Rows(new TerminalTruncatedText("ab").Render(3), "ab ");
        return Task.CompletedTask;
    }
    private static Task TruncatedPadding()
    {
        Rows(new TerminalTruncatedText("a\rhidden\nignored").Render(20), "a\rhidden             ");
        Rows(new TerminalTruncatedText("a\tb\nignored").Render(6), "a\tb ");
        var text = new TerminalTruncatedText("abc", 2, 1);
        Rows(text.Render(3), "   ", "  " + Reset + "." + Reset + "  ", "   ");
        text.Invalidate(); Rows(text.Render(3), "   ", "  " + Reset + "." + Reset + "  ", "   ");
        return Task.CompletedTask;
    }
    private static Task TruncatedUnicode()
    {
        Rows(new TerminalTruncatedText("Ae\u0301🙂BC").Render(5), "Ae\u0301" + Reset + "..." + Reset);
        Rows(new TerminalTruncatedText("中abcdef").Render(4), Reset + "..." + Reset + " ");
        const string splitRegional = "🇺\u001b[31m🇸X";
        // Whole-string visibleWidth joins the flag after stripping ANSI (3 cells); Source truncation
        // measures the two RI spans separately (5 cells) and must overflow at width 4.
        Rows(new TerminalTruncatedText(splitRegional).Render(4), Reset + "..." + Reset + " ");
        Rows(new TerminalTruncatedText("\u001b[31mA\u001b[0m").Render(4), "\u001b[31mA\u001b[0m   ");
        return Task.CompletedTask;
    }
    private static Task SafeFrames()
    {
        var calls = 0;
        var text = new TerminalText("A\u001b[2J\tB\u009b", 0, 0, row => { calls++; return "\u001b[31m" + row; });
        var raw = text.Render(8); var afterRaw = calls;
        var frame = TerminalTextFrameFactory.Create(text, 8, 8);
        True(raw.Equals(text.Render(8))); Equal(afterRaw, calls); Equal("pisharp-text-source-safe-v1", frame.WidthPolicyId);
        var all = string.Concat(frame.Rows.Select(row => row.Text));
        True(all.Contains("\\u001b[2J", StringComparison.Ordinal)); True(all.Contains("\\u009b", StringComparison.Ordinal));
        True(frame.Rows.All(row => row.CellWidth <= 8 && !row.Text.Any(c => c < 32 || c is >= '\u007f' and <= '\u009f')));
        var callbackThrows = new TerminalText("abc", 0, 0, _ => throw new IOException("raw callback only"));
        Equal("abc", TerminalTextFrameFactory.Create(callbackThrows, 1, 3).Rows[0].Text);
        var truncated = TerminalTextFrameFactory.Create(new TerminalTruncatedText("\u001b[31mabcdef"), 1, 5);
        Equal("\\u...", truncated.Rows[0].Text); Equal(5, truncated.Rows[0].CellWidth);
        return Task.CompletedTask;
    }
    private static Task AnsiRecognition()
    {
        const string splitRegional = "🇺\u001b[31m🇸X";
        // At <=3 columns Source deliberately uses whole-string visibleWidth for ellipsis admission.
        Rows(new TerminalTruncatedText(splitRegional).Render(3), splitRegional);
        Rows(new TerminalTruncatedText("\u001b[2Jabc").Render(3), "\u001b[2Jabc");
        Rows(new TerminalTruncatedText("\u001b[2AX").Render(4), "\u001b[2AX"); // A is not a recognized CSI final.
        Rows(new TerminalTruncatedText("\u001b[2AX").Render(3), Reset + "..." + Reset);
        Rows(new TerminalTruncatedText("\u001b[31m\u001b[0m").Render(2), "\u001b[31m\u001b[0m  ");
        var frame = TerminalTextFrameFactory.Create(new TerminalText("\u001b[2Jabc", 0, 0), 1, 20);
        Equal("\\u001b[2Jabc        ", frame.Rows[0].Text);
        return Task.CompletedTask;
    }
    private static Task FrameClipping()
    {
        var empty = TerminalTextFrameFactory.Create(new TerminalText(""), 2, 3);
        Equal(2, empty.Rows.Length); True(empty.Rows.All(row => row.Text == "")); True(!empty.Cursor.Visible && !empty.IsClipped);
        var crop = TerminalTextFrameFactory.Create(new TerminalText("a\nb\nc", 0, 0), 2, 2);
        Equal("a ", crop.Rows[0].Text); Equal("b ", crop.Rows[1].Text); True(crop.IsClipped);
        var wide = TerminalTextFrameFactory.Create(new TerminalText("👩‍👩‍👧‍👦", 0, 0), 2, 1);
        Equal(" ", wide.Rows[0].Text); Equal("", wide.Rows[1].Text); True(wide.IsClipped);
        var margins = TerminalTextFrameFactory.Create(new TerminalTruncatedText("abc", 2), 1, 3);
        Equal("  .", margins.Rows[0].Text); True(margins.IsClipped);
        return Task.CompletedTask;
    }
    private static Task Bounds()
    {
        Throws<ArgumentOutOfRangeException>(() => new TerminalText("x", -1));
        Throws<ArgumentOutOfRangeException>(() => new TerminalTruncatedText("x", 0, 257));
        Failure(TerminalRenderFailure.ResourceLimit, () => new TerminalText(new string('x', 65_537)));
        Failure(TerminalRenderFailure.InvalidUnicode, () => new TerminalTruncatedText("\ud800"));
        var text = new TerminalText("x", 0, 0);
        foreach (var width in new[] { 0, 513 }) Failure(TerminalRenderFailure.ResourceLimit, () => text.Render(width));
        Failure(TerminalRenderFailure.ResourceLimit, () => new TerminalText(new string('x', 4097), 0, 0).Render(1));
        text.SetCustomBackground(_ => new string('x', 1_048_577));
        Failure(TerminalRenderFailure.ResourceLimit, () => text.Render(1));
        var smallInput = new TerminalRenderLimits(MaximumInputCharacters: 2);
        Failure(TerminalRenderFailure.ResourceLimit, () => TerminalTextFrameFactory.Create(new TerminalText("abc", 0, 0), 1, 3, smallInput));
        Failure(TerminalRenderFailure.ResourceLimit, () => TerminalTextFrameFactory.Create(new TerminalText("e\u0301", 0, 0), 1, 1,
            new(MaximumGraphemeCharacters: 1)));
        // Row text fits, but fixed VT framing must also fit the physical write admission budget.
        Failure(TerminalRenderFailure.ResourceLimit, () => TerminalTextFrameFactory.Create(new TerminalText(new string('x', 10), 0, 0),
            1, 10, new(MaximumFrameCharacters: 32)));
        Failure(TerminalRenderFailure.InvalidOptions, () => TerminalTextFrameFactory.Create(new TerminalText("x"), 1, 1,
            new(MaximumRows: 0)));
        return Task.CompletedTask;
    }

    private static async Task HeldWrite()
    {
        var sink = new PhysicalSink(); var text = new TerminalText("old", 0, 0);
        await using (var renderer = new VtRenderer(sink))
        {
            var hold = sink.HoldNext(); var active = renderer.RenderAsync(TerminalTextFrameFactory.Create(text, 2, 3)).AsTask();
            try
            {
                await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                True(!active.IsCompleted && !renderer.Snapshot.CacheKnown);
                text.SetText("new"); text.Render(3); // Component cache cannot commit the in-flight physical frame.
                True(!renderer.Snapshot.CacheKnown); Equal(1, sink.ActiveWrites);
            }
            finally { hold.Complete(); await active; }
            True(renderer.Snapshot.CacheKnown);
            Equal(TerminalRenderKind.Unchanged, (await renderer.RenderAsync(TerminalTextFrameFactory.Create(new TerminalText("old", 0, 0), 2, 3))).Kind);
            Equal(TerminalRenderKind.Diff, (await renderer.RenderAsync(TerminalTextFrameFactory.Create(text, 2, 3))).Kind);
            text.SetText(""); var cleared = TerminalTextFrameFactory.Create(text, 2, 3);
            Equal(TerminalRenderKind.Diff, (await renderer.RenderAsync(cleared)).Kind);
            True(sink.Attempts[^1].Contains("\u001b[2K", StringComparison.Ordinal));
            Equal(TerminalRenderKind.Unchanged, (await renderer.RenderAsync(cleared)).Kind);
            renderer.Invalidate(); Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(cleared)).Kind);
        }
        Equal(0, sink.DisposeCalls); Equal(0, sink.ActiveWrites);
    }
    private static async Task FaultRecovery()
    {
        var sink = new PhysicalSink(); await using var renderer = new VtRenderer(sink);
        var frame = TerminalTextFrameFactory.Create(new TerminalTruncatedText("abcdef"), 1, 5);
        var hold = sink.HoldNext(); var active = renderer.RenderAsync(frame).AsTask();
        try { await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); True(!renderer.Snapshot.CacheKnown); }
        finally
        {
            hold.Complete(new IOException("actual write failure"));
            await ThrowsAsync<IOException>(() => active);
        }
        True(!renderer.Snapshot.CacheKnown); Equal(0, sink.ActiveWrites);
        Equal(TerminalRenderKind.Full, (await renderer.RenderAsync(frame)).Kind);
        Equal(TerminalRenderKind.Unchanged, (await renderer.RenderAsync(frame)).Kind);
        True(!sink.Attempts.Last().Contains("\u001b[31m", StringComparison.Ordinal));
        Equal(2L, renderer.Snapshot.PhysicalWritesSettled);
    }
    private static void Rows(ImmutableArray<string> actual, params string[] expected)
    { if (!actual.SequenceEqual(expected)) throw new Exception("Authored Source rows differ: " + string.Join("|", actual)); }
    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
    private static void True(bool value) { if (!value) throw new Exception("Text component invariant differs."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Failure(TerminalRenderFailure expected, Action action)
    { try { action(); } catch (TerminalRenderException error) { Equal(expected, error.Failure); return; } throw new Exception("Expected render failure."); }

    private sealed class Hold
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Error { get; private set; }
        internal void Complete(Exception? error = null) { Error = error; Release.TrySetResult(); }
    }
    private sealed class PhysicalSink : IConsoleTerminal
    {
        private readonly Queue<Hold> holds = new();
        internal List<string> Attempts { get; } = [];
        internal int ActiveWrites { get; private set; }
        internal int DisposeCalls { get; private set; }
        private long started, settled;
        internal Hold HoldNext() { var hold = new Hold(); holds.Enqueue(hold); return hold; }
        public TerminalLeaseSnapshot Snapshot
        {
            get { var state = new TerminalConsoleState(0, 0, 0, 0, 1, true, 0, 0);
                return new(state, state, null, false, false, 0, ActiveWrites, 0, 0, started, settled); }
        }
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw new NotSupportedException();
        public async ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
        {
            if (ActiveWrites != 0) throw new Exception("Physical writes overlapped.");
            ActiveWrites++; started++; Attempts.Add(frame.ToString()); var hold = holds.Count > 0 ? holds.Dequeue() : null;
            try
            {
                if (hold is not null) { hold.Entered.TrySetResult(); await hold.Release.Task; if (hold.Error is not null) throw hold.Error; }
                token.ThrowIfCancellationRequested();
            }
            finally { ActiveWrites--; settled++; }
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
