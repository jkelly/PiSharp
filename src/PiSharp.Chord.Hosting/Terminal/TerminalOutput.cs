// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/tui/filter.ts, packages/chord/src/tui/frames.ts.
// Native adapters retain explicit trusted-code and terminal ownership boundaries.
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Chord.Hosting.Terminal;

/// <summary>Frame-local terminal output boundary. No parser state survives an untrusted frame.</summary>
public static partial class TerminalOutput
{
    public const int MaximumFrameBytes = 64 * 1024;
    public const int FrameUnits = 16 * 1024;
    public const string ResetModes = "\u001b[?2026l\u001b[?1000l\u001b[?1002l\u001b[?1006l\u001b[?1004l\u001b[?2004l\u001b[?25h\u001b7\u001b[r\u001b8\u001b[?7h\u001b[?1049l\u001b[0m";
    private static readonly HashSet<string> Modes = ["25", "2004", "2026", "1049", "7", "1000", "1002", "1006", "1004"];

    public static string Filter(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Length > MaximumFrameBytes || Encoding.UTF8.GetByteCount(output) > MaximumFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(output), "terminal: a frame over 64 KiB");
        var kept = new StringBuilder(output.Length);
        for (var index = 0; index < output.Length; index++)
        {
            var code = output[index];
            if (code == '\x1b' || code is >= '\x80' and <= '\x9f')
            {
                var start = index;
                var introducer = code == '\x1b' ? (++index < output.Length ? output[index] : '\0') : code;
                if (introducer == '[' || code == '\x9b')
                {
                    var body = index + 1;
                    while (index + 1 < output.Length && output[index + 1] is >= '\x20' and <= '\x3f') index++;
                    var final = ++index < output.Length ? output[index] : '\0';
                    if (code == '\x1b' && final != '\0' && AllowedCsi(output[body..index], final))
                        kept.Append(output, start, index - start + 1);
                }
                else if ((introducer != '\0' && "]PX^_".Contains(introducer)) || code is '\x90' or '\x98' or '\x9d' or '\x9e' or '\x9f')
                {
                    while (++index < output.Length)
                    {
                        if (output[index] is '\x9c' or '\a') break;
                        if (output[index] == '\x1b' && index + 1 < output.Length && output[index + 1] == '\\') { index++; break; }
                    }
                }
                else while (index < output.Length && output[index] is >= '\x20' and <= '\x2f') index++;
            }
            else if (code is '\t' or '\n' or '\r' || code >= ' ' && code != '\x7f') kept.Append(code);
        }
        return kept.ToString();
    }

    public static IReadOnlyList<string> SplitFrames(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length <= FrameUnits) return [data];
        var frames = new List<string>(); var start = 0; var safe = 0; var index = 0;
        while (index < data.Length)
        {
            var end = data[index] == '\x1b' ? SequenceEnd(data, index) :
                char.IsHighSurrogate(data[index]) && index + 1 < data.Length ? index + 2 : index + 1;
            if (end - start > FrameUnits && safe > start) { frames.Add(data[start..safe]); start = safe; }
            index = safe = end;
        }
        frames.Add(data[start..]); return frames;
    }

    private static int SequenceEnd(string data, int index)
    {
        if (index + 1 >= data.Length) return data.Length;
        var introducer = data[index + 1];
        if (introducer == '[')
        {
            var at = index + 2;
            while (at < data.Length && data[at] is >= '\x20' and <= '\x3f') at++;
            return Math.Min(at + 1, data.Length);
        }
        if (!"]PX^_".Contains(introducer)) return Math.Min(index + 2, data.Length);
        for (var at = index + 2; at < data.Length; at++)
        {
            if (data[at] == '\a') return at + 1;
            if (data[at] == '\x1b' && at + 1 < data.Length && data[at + 1] == '\\') return at + 2;
        }
        return data.Length;
    }

    private static bool AllowedCsi(string body, char final)
    {
        if (body.Length > 128) return false;
        if (final is 'h' or 'l') return body.StartsWith('?') && Modes.Contains(body[1..]);
        if (FiveDigits().IsMatch(body)) return false;
        if (final == 'm') return SgrParameters().IsMatch(body);
        if (!Parameters().IsMatch(body)) return false;
        if ("ABCDEFGHfd".Contains(final)) return body.Count(c => c == ';') < (final is 'H' or 'f' ? 2 : 1);
        if (final is 'J' or 'K') return body is "" or "0" or "1" or "2";
        if (final == 'r') return ScrollRegion().IsMatch(body);
        return final is 's' or 'u' && body.Length == 0;
    }
    [GeneratedRegex("[0-9]{5}", RegexOptions.CultureInvariant)] private static partial Regex FiveDigits();
    [GeneratedRegex("^[0-9;:]*$", RegexOptions.CultureInvariant)] private static partial Regex SgrParameters();
    [GeneratedRegex("^[0-9;]*$", RegexOptions.CultureInvariant)] private static partial Regex Parameters();
    [GeneratedRegex("^(?:[0-9]{1,4};[0-9]{1,4})?$", RegexOptions.CultureInvariant)] private static partial Regex ScrollRegion();
}
