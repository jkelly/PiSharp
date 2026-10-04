using System.Text;
using PiSharp.Tui.Components.SelectList;
using PiSharp.Tui.Input;

namespace PiSharp.Tui.Components.Text;

// Translation of text.ts, truncated-text.ts and their utils.ts operations at
// d86654abb8862e201933517d6f1fce9f88dd117f. Copyright (c) 2025 Mario Zechner.
// Retained MIT notice: UPSTREAM-LICENSE. No execution of upstream JavaScript is required.
internal static class TerminalTextSource
{
    internal static int Width(string value) => TerminalSelectListText.Width(value);
    internal static string Pad(string value, int width) => value + new string(' ', Math.Max(0, width - Width(value)));
    private static bool JsWhitespace(char c) => c is '\u0009' or '\u000a' or '\u000b' or '\u000c' or
        '\u000d' or '\u0020' or '\u00a0' or '\u1680' or >= '\u2000' and <= '\u200a' or
        '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';
    internal static bool IsBlank(string value) => value.All(JsWhitespace);
    private static string TrimEnd(string value)
    { var end = value.Length; while (end > 0 && JsWhitespace(value[end - 1])) end--; return value[..end]; }
    internal static string SafeRow(string value) => TerminalSelectListText.Safe(value);
    internal static string SafeMultiline(string value)
    {
        var result = new StringBuilder(); var start = 0;
        for (var at = 0; at < value.Length; at++)
        {
            if (value[at] is not ('\r' or '\n')) continue;
            result.Append(SafeRow(value[start..at])).Append(value[at]); start = at + 1;
        }
        return result.Append(SafeRow(value[start..])).ToString();
    }

    internal static string[] Wrap(string text, int width)
    {
        var rows = new TerminalTextComponentBounds.Rows(); var tracker = new AnsiState();
        var first = true; var start = 0;
        for (var at = 0; at <= text.Length; at++)
        {
            if (at < text.Length && text[at] is not ('\r' or '\n')) continue;
            var input = text[start..at];
            foreach (var row in WrapLine((first ? "" : tracker.Active()) + input, width)) rows.Add(row);
            tracker.Update(input); first = false;
            if (at < text.Length && text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n') at++;
            start = at + 1;
        }
        return rows.ToArray();
    }

    private static string[] WrapLine(string line, int width)
    {
        if (line.Length == 0 || Width(line) <= width) return [line]; // Source retains trailing space in this shortcut.
        var rows = new TerminalTextComponentBounds.Rows(); var tracker = new AnsiState();
        var current = new StringBuilder(); var visible = 0;
        foreach (var token in Tokens(line))
        {
            var tokenWidth = Width(token); var whitespace = IsBlank(token);
            if (tokenWidth > width && !whitespace)
            {
                if (current.Length > 0)
                {
                    rows.Add(TrimEnd(current.Append(tracker.LineEnd()).ToString())); current.Clear(); visible = 0;
                }
                var broken = BreakWord(token, width, tracker);
                for (var i = 0; i < broken.Length - 1; i++) rows.Add(TrimEnd(broken[i]));
                current.Append(broken[^1]); visible = Width(broken[^1]); continue;
            }
            if (visible + tokenWidth > width && visible > 0)
            {
                rows.Add(TrimEnd(TrimEnd(current.ToString()) + tracker.LineEnd()));
                current.Clear().Append(tracker.Active()); visible = 0;
                if (!whitespace) { current.Append(token); visible = tokenWidth; }
            }
            else { current.Append(token); visible += tokenWidth; }
            tracker.Update(token);
            if (current.Length > TerminalTextComponentBounds.MaximumRenderedCharacters) throw TerminalTextComponentBounds.Limit();
        }
        if (current.Length > 0) rows.Add(TrimEnd(current.ToString()));
        var result = rows.ToArray(); return result.Length > 0 ? result : [""];
    }

    private static string[] Tokens(string text)
    {
        var tokens = new List<string>(); var current = new StringBuilder(); var pending = new StringBuilder();
        bool? space = null;
        foreach (var segment in Segments(text))
        {
            if (segment.Ansi) { pending.Append(segment.Value); continue; }
            var isSpace = segment.Value == " ";
            if (!isSpace && TerminalEditorSourceWidth.Cjk(segment.Value))
            { Flush(); tokens.Add(pending.ToString() + segment.Value); pending.Clear(); continue; }
            if (current.Length > 0 && space != isSpace) Flush();
            current.Append(pending).Append(segment.Value); pending.Clear(); space = isSpace;
        }
        if (pending.Length > 0)
        {
            if (current.Length > 0) current.Append(pending);
            else if (tokens.Count > 0) tokens[^1] += pending.ToString();
            else current.Append(pending);
        }
        Flush(); return tokens.ToArray();
        void Flush() { if (current.Length == 0) return; tokens.Add(current.ToString()); current.Clear(); space = null; }
    }

    private static string[] BreakWord(string word, int width, AnsiState tracker)
    {
        var rows = new TerminalTextComponentBounds.Rows(); var current = new StringBuilder(tracker.Active());
        var visible = 0;
        foreach (var segment in Segments(word))
        {
            if (segment.Ansi) { current.Append(segment.Value); tracker.Process(segment.Value); continue; }
            var cells = Width(segment.Value);
            if (visible + cells > width)
            {
                rows.Add(current.Append(tracker.LineEnd()).ToString()); // Source can emit an empty row before an oversized cluster.
                current.Clear().Append(tracker.Active()); visible = 0;
            }
            current.Append(segment.Value); visible += cells;
        }
        if (current.Length > 0) rows.Add(current.ToString());
        var result = rows.ToArray(); return result.Length > 0 ? result : [""];
    }

    internal static string Truncate(string text, int width, bool sourceReset)
    {
        var ellipsis = new string('.', Math.Min(3, width));
        if (width <= 3) return Width(text) <= width ? text : Finish("", ellipsis, sourceReset);
        if (text.All(c => c is >= ' ' and <= '~'))
            return text.Length <= width ? text : Finish(text[..(width - 3)], ellipsis, sourceReset);
        var prefix = new StringBuilder(); var pending = new StringBuilder();
        var keptWidth = 0; var measured = 0; var contiguous = true;
        foreach (var segment in Segments(text))
        {
            if (segment.Ansi) { pending.Append(segment.Value); continue; }
            var cells = Width(segment.Value);
            if (contiguous && keptWidth + cells <= width - 3)
            { prefix.Append(pending).Append(segment.Value); pending.Clear(); keptWidth += cells; }
            else { contiguous = false; pending.Clear(); }
            measured += cells;
            if (measured > width) return Finish(prefix.ToString(), ellipsis, sourceReset);
        }
        return text; // Source preserves all original ANSI if independent-span measurement exhausts without overflow.
    }

    private static string Finish(string prefix, string ellipsis, bool sourceReset)
    {
        if (!sourceReset) return prefix + ellipsis;
        var tracker = new AnsiState(); tracker.Update(prefix);
        return prefix + tracker.HyperlinkClose() + "\u001b[0m" + ellipsis + "\u001b[0m";
    }

    private readonly record struct Segment(string Value, bool Ansi);
    private static IEnumerable<Segment> Segments(string text)
    {
        for (var at = 0; at < text.Length;)
        {
            var ansi = AnsiLength(text, at);
            if (ansi > 0) { yield return new(text.Substring(at, ansi), true); at += ansi; continue; }
            var end = at;
            while (end < text.Length && AnsiLength(text, end) == 0) end++;
            foreach (var element in TerminalSourceGraphemeSegmenter.Elements(text[at..end])) yield return new(element, false);
            at = end;
        }
    }
    private static int AnsiLength(string text, int at)
    {
        if (at + 1 >= text.Length || text[at] != '\u001b') return 0;
        if (text[at + 1] == '[')
        {
            for (var end = at + 2; end < text.Length; end++)
                if (text[end] is 'm' or 'G' or 'K' or 'H' or 'J') return end + 1 - at;
        }
        else if (text[at + 1] is ']' or '_')
        {
            for (var end = at + 2; end < text.Length; end++)
            {
                if (text[end] == '\u0007') return end + 1 - at;
                if (text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\') return end + 2 - at;
            }
        }
        return 0;
    }

    private sealed class AnsiState
    {
        private bool bold, dim, italic, underline, blink, inverse, hidden, strike;
        private string? foreground, background, hyperlink, terminator;
        internal void Update(string text)
        {
            for (var at = text.IndexOf('\u001b'); at >= 0 && at < text.Length; at = text.IndexOf('\u001b', at))
            {
                var length = AnsiLength(text, at);
                if (length > 0) Process(text.Substring(at, length));
                at += Math.Max(1, length);
            }
        }
        internal void Process(string code)
        {
            if (code.StartsWith("\u001b]8;", StringComparison.Ordinal))
            {
                var ending = code.EndsWith('\u0007') ? "\u0007" : "\u001b\\";
                var body = code[4..^ending.Length]; var separator = body.IndexOf(';');
                if (separator < 0) return;
                if (separator == body.Length - 1) { hyperlink = null; terminator = null; }
                else { hyperlink = code; terminator = ending; }
                return;
            }
            if (!code.EndsWith('m')) return;
            // Source regex is unanchored; skip non-numeric CSI prefixes until a valid SGR substring.
            string? parameters = null;
            for (var at = code.IndexOf("\u001b[", StringComparison.Ordinal); at >= 0;
                at = code.IndexOf("\u001b[", at + 2, StringComparison.Ordinal))
            {
                var end = at + 2; while (end < code.Length && (code[end] is >= '0' and <= '9' || code[end] == ';')) end++;
                if (end < code.Length && code[end] == 'm') { parameters = code[(at + 2)..end]; break; }
            }
            if (parameters is null) return;
            if (parameters is "" or "0") { Reset(); return; }
            var parts = parameters.Split(';');
            for (var i = 0; i < parts.Length; i++)
            {
                // Numeric values beyond the recognized SGR range have no effect, like Source parseInt.
                if (!int.TryParse(parts[i], out var number)) continue;
                if (number is 38 or 48)
                {
                    var count = i + 1 < parts.Length && parts[i + 1] == "5" ? 3 :
                        i + 1 < parts.Length && parts[i + 1] == "2" ? 5 : 0;
                    if (count > 0 && i + count <= parts.Length)
                    {
                        var color = string.Join(";", parts.Skip(i).Take(count));
                        if (number == 38) foreground = color; else background = color;
                        i += count - 1; continue;
                    }
                }
                switch (number)
                {
                    case 0: Reset(); break;
                    case 1: bold = true; break;
                    case 2: dim = true; break;
                    case 3: italic = true; break;
                    case 4: underline = true; break;
                    case 5: blink = true; break;
                    case 7: inverse = true; break;
                    case 8: hidden = true; break;
                    case 9: strike = true; break;
                    case 21: bold = false; break;
                    case 22: bold = dim = false; break;
                    case 23: italic = false; break;
                    case 24: underline = false; break;
                    case 25: blink = false; break;
                    case 27: inverse = false; break;
                    case 28: hidden = false; break;
                    case 29: strike = false; break;
                    case 39: foreground = null; break;
                    case 49: background = null; break;
                    default:
                        if (number is >= 30 and <= 37 or >= 90 and <= 97) foreground = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        else if (number is >= 40 and <= 47 or >= 100 and <= 107) background = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
            }
        }
        private void Reset()
        { bold = dim = italic = underline = blink = inverse = hidden = strike = false; foreground = background = null; }
        internal string Active()
        {
            var codes = new List<string>();
            if (bold) codes.Add("1"); if (dim) codes.Add("2"); if (italic) codes.Add("3");
            if (underline) codes.Add("4"); if (blink) codes.Add("5"); if (inverse) codes.Add("7");
            if (hidden) codes.Add("8"); if (strike) codes.Add("9");
            if (foreground is not null) codes.Add(foreground); if (background is not null) codes.Add(background);
            return (codes.Count > 0 ? "\u001b[" + string.Join(";", codes) + "m" : "") + hyperlink;
        }
        internal string HyperlinkClose() => hyperlink is null ? "" : "\u001b]8;;" + terminator;
        internal string LineEnd() => (underline ? "\u001b[24m" : "") + HyperlinkClose();
    }
}
