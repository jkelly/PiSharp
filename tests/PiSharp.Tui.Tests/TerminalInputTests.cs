using System.Collections.Immutable;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class TerminalInputTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("terminal-input.every-split-utf16-legacy-ss3-csi-kitty-and-reports", Fragmentation);
        yield return ("terminal-input.bracketed-paste-every-split-preserves-literal-controls-and-nesting", Paste);
        yield return ("terminal-input.injected-monotonic-time-escape-and-sequence-boundaries", Timeouts);
        yield return ("terminal-input.exact-resource-bounds-and-failure-closes-decoder", Bounds);
        yield return ("terminal-input.invalid-surrogates-completion-and-unknown-protocols", InvalidAndUnknown);
    }

    private static Task Fragmentation()
    {
        const string input = "a\u0301\u4e2d\ud83d\ude42\u001b[A\u001bOB\u001b[1;5C\u001b[3;2~\u001b[Z\u0003\u001bq\u001b\ud83d\ude42\u001b[97;3:2u\u001b[57352;5:3u\u001b[12;40R\u001b[?1;2c\u001b[I";
        TerminalInputEvent[] expected = [new TerminalText("a\u0301\u4e2d\ud83d\ude42"), new TerminalKey("Up"), new TerminalKey("Down"),
            new TerminalKey("Right", TerminalModifiers.Control), new TerminalKey("Delete", TerminalModifiers.Shift),
            new TerminalKey("Tab", TerminalModifiers.Shift), new TerminalKey("c", TerminalModifiers.Control),
            new TerminalKey("q", TerminalModifiers.Alt), new TerminalKey("\ud83d\ude42", TerminalModifiers.Alt),
            new TerminalKey("a", TerminalModifiers.Alt, TerminalKeyAction.Repeat), new TerminalKey("Up", TerminalModifiers.Control, TerminalKeyAction.Release),
            new TerminalProtocol("\u001b[12;40R"), new TerminalProtocol("\u001b[?1;2c"), new TerminalProtocol("\u001b[I")];
        EverySplit(input, expected); return Task.CompletedTask;
    }

    private static Task Paste()
    {
        const string payload = "line\r\n\u0003\u0000\ud83d\ude42\u001b[200~inner\u001b[20x\u001b\u001b[201x";
        EverySplit("before\u001b[200~" + payload + "\u001b[201~after", [new TerminalText("before"), new TerminalPaste(payload), new TerminalText("after")]);
        EverySplit("\u001b[200~\u001b[201~", [new TerminalPaste("")]);
        var decoder = new TerminalInputDecoder(new(MaximumPasteCharacters: 4));
        Equal(new TerminalPaste("\ud83d\ude42xy"), decoder.Feed("\u001b[200~\ud83d\ude42xy\u001b[201~").Single());
        return Task.CompletedTask;
    }

    private static Task Timeouts()
    {
        var clock = new Clock(); var decoder = new TerminalInputDecoder(new(EscapeTimeout: TimeSpan.FromMilliseconds(20), SequenceTimeout: TimeSpan.FromMilliseconds(50)), clock);
        Equal(0, decoder.Feed("\u001b").Length); clock.Advance(19); Equal(0, decoder.FlushTimeouts().Length);
        clock.Advance(1); Equal(new TerminalKey("Escape"), decoder.FlushTimeouts().Single());
        Equal(new TerminalText("x"), decoder.Feed("x").Single());
        decoder.Feed("\u001b["); clock.Advance(49); Equal(0, decoder.FlushTimeouts().Length);
        clock.Advance(1); Equal(new TerminalUnknownSequence("\u001b["), decoder.FlushTimeouts().Single());
        Equal(new TerminalText("A"), decoder.Feed("A").Single());
        decoder.Feed("\u001b"); clock.Advance(20);
        Sequence([new TerminalKey("Escape"), new TerminalText("[A")], decoder.Feed("[A"));
        decoder.Feed("\u001b[200~x"); clock.Advance(5000); Equal(0, decoder.FlushTimeouts().Length);
        Equal(new TerminalPaste("x"), decoder.Feed("\u001b[201~").Single());
        return Task.CompletedTask;
    }

    private static Task Bounds()
    {
        foreach (var options in new TerminalInputDecoderOptions[] { new(MaximumSequenceCharacters: 5), new(MaximumSequenceCharacters: 65_537),
            new(MaximumPasteCharacters: 0), new(MaximumPasteCharacters: 1_048_577), new(MaximumChunkCharacters: 0), new(MaximumChunkCharacters: 65_537),
            new(EscapeTimeout: TimeSpan.Zero), new(SequenceTimeout: TimeSpan.FromMilliseconds(1)), new(SequenceTimeout: TimeSpan.FromMinutes(2)) })
            Failure(TerminalInputFailure.InvalidOptions, () => new TerminalInputDecoder(options));
        var exact = new TerminalInputDecoder(new(MaximumSequenceCharacters: 6));
        Equal(new TerminalKey("Up", TerminalModifiers.Control), exact.Feed("\u001b[1;5A").Single());
        var sequence = new TerminalInputDecoder(new(MaximumSequenceCharacters: 6));
        sequence.Feed("\u001b[1234"); Failure(TerminalInputFailure.ResourceLimit, () => sequence.Feed("5"));
        Failure(TerminalInputFailure.Closed, () => sequence.Feed("x"));
        var paste = new TerminalInputDecoder(new(MaximumPasteCharacters: 3));
        paste.Feed("\u001b[200~abc"); Failure(TerminalInputFailure.ResourceLimit, () => paste.Feed("d"));
        Failure(TerminalInputFailure.Closed, () => paste.Complete());
        var chunk = new TerminalInputDecoder(new(MaximumChunkCharacters: 2));
        Failure(TerminalInputFailure.ResourceLimit, () => chunk.Feed("abc"));
        return Task.CompletedTask;
    }

    private static Task InvalidAndUnknown()
    {
        foreach (var value in new[] { "\udc00", "\ud800x", "\ud800\ud800" })
        { var decoder = new TerminalInputDecoder(); Failure(TerminalInputFailure.InvalidUnicode, () => decoder.Feed(value)); Failure(TerminalInputFailure.Closed, () => decoder.Complete()); }
        var high = new TerminalInputDecoder(); high.Feed("\ud800"); Failure(TerminalInputFailure.InvalidUnicode, () => high.Complete());
        var unfinished = new TerminalInputDecoder(); unfinished.Feed("\u001b[200~abc"); Failure(TerminalInputFailure.IncompleteInput, () => unfinished.Complete());
        var escape = new TerminalInputDecoder(); escape.Feed("\u001b"); Equal(new TerminalKey("Escape"), escape.Complete().Single());
        Failure(TerminalInputFailure.Closed, () => escape.FlushTimeouts());
        EverySplit("\u001b[999~\u001b[97;999u\u001b[55296u\u001b[1:2u\u001b[1:55296u\u001b[>4;2m\u001b]0;title\a\u001bPdata\u001b\\\u001b[\ud83d\ude42",
            [new TerminalUnknownSequence("\u001b[999~"), new TerminalUnknownSequence("\u001b[97;999u"), new TerminalUnknownSequence("\u001b[55296u"),
                new TerminalKey("\u0001"), new TerminalUnknownSequence("\u001b[1:55296u"), new TerminalUnknownSequence("\u001b[>4;2m"), new TerminalProtocol("\u001b]0;title\a"),
                new TerminalProtocol("\u001bPdata\u001b\\"), new TerminalUnknownSequence("\u001b[\ud83d\ude42")]);
        var partial = new TerminalInputDecoder(); partial.Feed("\u001b[12;"); Equal(new TerminalUnknownSequence("\u001b[12;"), partial.Complete().Single());
        return Task.CompletedTask;
    }

    private static void EverySplit(string input, TerminalInputEvent[] expected)
    {
        for (var split = 0; split <= input.Length; split++)
        {
            var decoder = new TerminalInputDecoder(timeProvider: new Clock()); var actual = decoder.Feed(input.AsSpan(0, split)).AddRange(decoder.Feed(input.AsSpan(split))).AddRange(decoder.Complete());
            Sequence(expected, Normalize(actual));
        }
        var tiny = new TerminalInputDecoder(timeProvider: new Clock()); var events = ImmutableArray.CreateBuilder<TerminalInputEvent>();
        foreach (var value in input) events.AddRange(tiny.Feed(value.ToString())); events.AddRange(tiny.Complete()); Sequence(expected, Normalize(events));
    }
    private static IEnumerable<TerminalInputEvent> Normalize(IEnumerable<TerminalInputEvent> values)
    {
        string? pending = null;
        foreach (var value in values)
        {
            if (value is TerminalText text) { pending = (pending ?? "") + text.Text; continue; }
            if (pending is not null) { yield return new TerminalText(pending); pending = null; } yield return value;
        }
        if (pending is not null) yield return new TerminalText(pending);
    }
    private static void Sequence(IEnumerable<TerminalInputEvent> expected, IEnumerable<TerminalInputEvent> actual)
    { if (!expected.SequenceEqual(actual)) throw new Exception("Terminal event sequence differs."); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
    private static void Failure(TerminalInputFailure failure, Action action)
    { try { action(); } catch (TerminalInputException error) { Equal(failure, error.Failure); return; } throw new Exception("Expected terminal input failure."); }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        public void Advance(long milliseconds) => ticks += milliseconds;
    }
}
