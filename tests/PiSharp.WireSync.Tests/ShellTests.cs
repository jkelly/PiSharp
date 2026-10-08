using System.Text;
using PiSharp.Tools.Processes;
using static Assert;

// wire.rpc-bash-ansi (1.1.0): utils/ansi.ts splitIncompleteAnsiSuffix and bash-executor.ts streamed hold-back.
internal static class ShellTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("wire.bash-ansi.split-incomplete-suffix-cases", Split);
        yield return ("wire.bash-ansi.stream-holds-back-fragments-across-chunks", Stream);
    }

    private static void Pair(string input, string complete, string pending)
    {
        var (actualComplete, actualPending) = ShellAnsiText.SplitIncompleteAnsiSuffix(input);
        Equal(Escape(complete), Escape(actualComplete), "complete of " + Escape(input));
        Equal(Escape(pending), Escape(actualPending), "pending of " + Escape(input));
    }

    private static string Escape(string value) => string.Concat(value.Select(character =>
        character < ' ' || character is >= '\u007f' and <= '\u009f' ? "\\u" + ((int)character).ToString("x4") : character.ToString()));

    private static Task Split()
    {
        Pair("plain text", "plain text", "");
        Pair("abc\u001b[3", "abc", "\u001b[3");
        Pair("abc\u001b[31;1", "abc", "\u001b[31;1");
        Pair("abc\u001b[31m", "abc\u001b[31m", "");
        Pair("a\u001b", "a", "\u001b");
        Pair("a\u009b1;2", "a", "\u009b1;2");
        Pair("x\u001b]0;title", "x", "\u001b]0;title");
        Pair("x\u001b]0;title\u001b", "x", "\u001b]0;title\u001b");          // a trailing ESC may begin ESC \
        Pair("x\u001b]0;title\u0007y", "x\u001b]0;title\u0007y", "");
        Pair("x\u001b]8;;http://e\u001b\\y", "x\u001b]8;;http://e\u001b\\y", "");
        Pair("a\u001b[\n", "a\u001b[\n", "");                                 // ECMAScript $ does not match before a final newline
        // An unfinished sequence longer than the 256-character window is processed as it is.
        var longOsc = "\u001b]" + new string('a', 300);
        Pair(longOsc, longOsc, "");
        var shortOsc = "z\u001b]" + new string('a', 200);
        Pair(shortOsc, "z", shortOsc[1..]);
        Equal("ab", ShellAnsiText.SanitizeBinaryOutput("\0a\u000b￹b"));
        Equal("X\nY", ShellAnsiText.SanitizeShellText("\u001b[1mX\u001b[0m\r\nY\u001b]0;t\u0007"));
        return Task.CompletedTask;
    }

    private static Task Stream()
    {
        var chunks = new[] { "red:\u001b[3", "1mX\u001b[0m\r\nok\u001b", "]0;title\u0007done", "é" };
        var bytes = chunks.Select(Encoding.UTF8.GetBytes).ToList();
        // Split the two-byte UTF-8 character across chunks as well.
        bytes[3] = [bytes[3][0]]; bytes.Add([Encoding.UTF8.GetBytes("é")[1]]);
        var stream = new ShellTextStream(); var output = new List<string>();
        foreach (var chunk in bytes) output.Add(stream.Append(chunk));
        output.Add(stream.Flush());
        Equal("red:|X\nok|done||é|", string.Join("|", output));
        Equal("", stream.PendingAnsi);
        // Per-chunk stripping without hold-back leaks parameter fragments as text, which the source fix prevents.
        var naive = string.Concat(chunks.Take(3).Select(ShellAnsiText.SanitizeShellText));
        Check(naive.Contains("1mX", StringComparison.Ordinal), "authored control no longer demonstrates the leak");
        // An unfinished final sequence is flushed through stripping, as the source flushOutput does.
        var tail = new ShellTextStream();
        Equal("abc", tail.AppendText("abc\u001b[3")); Equal("\u001b[3", tail.PendingAnsi); Equal("", tail.Flush());
        var osc = new ShellTextStream();
        // At flush the source CSI pattern, whose intermediates include ']', removes "ESC ] 0 ; n" from an unterminated OSC.
        Equal("", osc.AppendText("\u001b]0;never terminated")); Equal("ever terminated", osc.Flush());
        Throws<InvalidOperationException>(() => osc.AppendText("late"));
        return Task.CompletedTask;
    }
}
