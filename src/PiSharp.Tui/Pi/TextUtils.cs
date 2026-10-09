// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/utils.ts.
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Tui.Input;

namespace PiSharp.Tui.Pi;

/// <summary>Terminal text measurement and manipulation that keeps ANSI, OSC 8 and APC sequences intact.</summary>
public static partial class TextUtils
{
    /// <summary>Grapheme clusters of plain text (Intl.Segmenter "grapheme" with the pinned Indic conjunct rule).</summary>
    public static IEnumerable<string> Graphemes(string text) => text.Length == 0 ? [] : TerminalSourceGraphemeSegmenter.Elements(text);

    private static bool IsPrintableAscii(string value)
    {
        foreach (var c in value) if (c < 0x20 || c > 0x7e) return false;
        return true;
    }

    /// <summary>The terminal width of one grapheme cluster.</summary>
    public static int GraphemeWidth(string segment)
    {
        if (segment.Length == 1)
        {
            var code = segment[0];
            if (code is >= ' ' and <= '~') return 1;
            if (code == '\t') return 3;
        }
        return segment.Length == 0 ? 0 : TerminalEditorSourceWidth.Grapheme(segment);
    }

    private static readonly Dictionary<string, int> WidthCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> WidthOrder = new();
    private static readonly object WidthGate = new();

    /// <summary>The visible width of a string in terminal columns (tabs count three).</summary>
    public static int VisibleWidth(string str)
    {
        if (str.Length == 0) return 0;
        var ascii = AsciiVisibleWidth(str);
        if (ascii != -1) return ascii;
        lock (WidthGate) if (WidthCache.TryGetValue(str, out var cached)) return cached;
        var clean = str.Contains('\t') ? str.Replace("\t", "   ", StringComparison.Ordinal) : str;
        if (clean.Contains('\u001b'))
        {
            var stripped = new StringBuilder(clean.Length); var copyFrom = 0; var escape = clean.IndexOf('\u001b');
            while (escape != -1)
            {
                var length = AnsiCodeLength(clean, escape);
                if (length > 0) { stripped.Append(clean, copyFrom, escape - copyFrom); escape += length; copyFrom = escape; }
                else escape++;
                escape = escape < clean.Length ? clean.IndexOf('\u001b', escape) : -1;
            }
            clean = stripped.Append(clean, copyFrom, clean.Length - copyFrom).ToString();
        }
        var width = 0;
        foreach (var segment in Graphemes(clean)) width += GraphemeWidth(segment);
        lock (WidthGate)
        {
            if (WidthCache.Count >= 512 && WidthOrder.TryDequeue(out var oldest)) WidthCache.Remove(oldest);
            if (WidthCache.TryAdd(str, width)) WidthOrder.Enqueue(str);
        }
        return width;
    }

    private static int AsciiVisibleWidth(string str)
    {
        var width = 0; var i = 0;
        while (i < str.Length)
        {
            var code = str[i];
            if (code is >= ' ' and <= '~') { width++; i++; }
            else if (code == '\t') { width += 3; i++; }
            else if (code == '\u001b')
            {
                var length = AnsiCodeLength(str, i);
                if (length == 0) return -1;
                i += length;
            }
            else return -1;
        }
        return width;
    }

    /// <summary>Length of the CSI (m/G/K/H/J), OSC or APC sequence at <paramref name="pos"/>, or 0.</summary>
    public static int AnsiCodeLength(string str, int pos)
    {
        if (pos >= str.Length || str[pos] != '\u001b' || pos + 1 >= str.Length) return 0;
        var next = str[pos + 1];
        if (next == '[')
        {
            for (var j = pos + 2; j < str.Length; j++)
                if (str[j] is 'm' or 'G' or 'K' or 'H' or 'J') return j + 1 - pos;
            return 0;
        }
        if (next is ']' or '_')
        {
            for (var j = pos + 2; j < str.Length; j++)
            {
                if (str[j] == '\u0007') return j + 1 - pos;
                if (str[j] == '\u001b' && j + 1 < str.Length && str[j + 1] == '\\') return j + 2 - pos;
            }
            return 0;
        }
        return 0;
    }

    public static string? ExtractAnsiCode(string str, int pos)
    {
        var length = AnsiCodeLength(str, pos);
        return length > 0 ? str.Substring(pos, length) : null;
    }

    /// <summary>Removes ANSI, OSC and APC sequences.</summary>
    public static string StripTerminalSequences(string str)
    {
        if (!str.Contains('\u001b')) return str;
        var result = new StringBuilder(str.Length); var i = 0;
        while (i < str.Length)
        {
            var length = AnsiCodeLength(str, i);
            if (length > 0) { i += length; continue; }
            result.Append(str[i]); i++;
        }
        return result.ToString();
    }

    /// <summary>Iterates text runs between escape sequences: (isAnsi, value).</summary>
    private static IEnumerable<(bool Ansi, string Value)> Runs(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            var length = AnsiCodeLength(line, i);
            if (length > 0) { yield return (true, line.Substring(i, length)); i += length; continue; }
            var end = i;
            while (end < line.Length && AnsiCodeLength(line, end) == 0) end++;
            yield return (false, line[i..end]);
            i = end;
        }
    }

    /// <summary>The terminal-cell range occupied by the grapheme at a visible column.</summary>
    public static (int Start, int End)? GetGraphemeCellRange(string line, int column)
    {
        var current = 0;
        foreach (var (ansi, value) in Runs(line))
        {
            if (ansi) continue;
            foreach (var segment in Graphemes(value))
            {
                var width = GraphemeWidth(segment);
                if (width > 0 && column >= current && column < current + width) return (current, current + width);
                current += width;
            }
        }
        return null;
    }

    [GeneratedRegex(@"^\x1b\]8;[^;]*;([^\x07\x1b]*)(?:\x07|\x1b\\)$")] private static partial Regex Osc8Link();
    /// <summary>The OSC 8 hyperlink covering a visible column.</summary>
    public static string? GetOsc8LinkAtColumn(string line, int column)
    {
        string? active = null; var current = 0;
        foreach (var (ansi, value) in Runs(line))
        {
            if (ansi)
            {
                var match = Osc8Link().Match(value);
                if (match.Success) active = match.Groups[1].Value.Length == 0 ? null : match.Groups[1].Value;
                continue;
            }
            foreach (var segment in Graphemes(value))
            {
                var width = segment == "\t" ? 3 : GraphemeWidth(segment);
                if (column >= current && column < current + width) return active;
                current += width;
            }
        }
        return null;
    }

    /// <summary>Output normalization: Thai/Lao AM vowels decomposed, visible tabs expanded to three spaces.</summary>
    public static string NormalizeTerminalOutput(string str)
    {
        var normalized = str;
        if (normalized.IndexOfAny(['ำ', 'ຳ']) >= 0)
            normalized = normalized.Replace("ำ", "ํา", StringComparison.Ordinal).Replace("ຳ", "ໍາ", StringComparison.Ordinal);
        if (!normalized.Contains('\t')) return normalized;
        var result = new StringBuilder(normalized.Length + 8); var i = 0;
        while (i < normalized.Length)
        {
            var length = AnsiCodeLength(normalized, i);
            if (length > 0) { result.Append(normalized, i, length); i += length; continue; }
            if (normalized[i] == '\t') result.Append("   "); else result.Append(normalized[i]);
            i++;
        }
        return result.ToString();
    }

    private sealed record Osc8(string Params, string Url, string Terminator);
    private static (bool IsOsc8, Osc8? Link) ParseOsc8(string code)
    {
        if (!code.StartsWith("\u001b]8;", StringComparison.Ordinal)) return (false, null);
        var terminator = code.EndsWith('\u0007') ? "\u0007" : "\u001b\\";
        var body = code[4..^terminator.Length];
        var separator = body.IndexOf(';');
        if (separator == -1) return (false, null);
        var url = body[(separator + 1)..];
        return url.Length == 0 ? (true, null) : (true, new(body[..separator], url, terminator));
    }
    private static string FormatOsc8(Osc8 link) => "\u001b]8;" + link.Params + ";" + link.Url + link.Terminator;
    private static string ActiveOsc8Close(string prefix)
    {
        if (!prefix.Contains("\u001b]8;", StringComparison.Ordinal)) return "";
        Osc8? active = null;
        foreach (var (ansi, value) in Runs(prefix))
            if (ansi) { var (isOsc8, link) = ParseOsc8(value); if (isOsc8) active = link; }
        return active is null ? "" : "\u001b]8;;" + active.Terminator;
    }

    /// <summary>Tracks active SGR attributes and the open OSC 8 link so styles survive line breaks.</summary>
    public sealed class AnsiCodeTracker
    {
        private bool bold, dim, italic, underline, blink, inverse, hidden, strikethrough;
        private string? fg, bg;
        private Osc8? link;
        public void Process(string code)
        {
            var (isOsc8, parsed) = ParseOsc8(code);
            if (isOsc8) { link = parsed; return; }
            if (!code.EndsWith('m') || code.Length < 3 || code[1] != '[') return;
            var parameters = code[2..^1];
            if (parameters.Any(c => c is not (>= '0' and <= '9' or ';'))) return;
            if (parameters is "" or "0") { Reset(); return; }
            var parts = parameters.Split(';');
            var i = 0;
            while (i < parts.Length)
            {
                var code0 = int.TryParse(parts[i], out var parsedCode) ? parsedCode : -1;
                if (code0 is 38 or 48)
                {
                    if (i + 2 < parts.Length && parts[i + 1] == "5")
                    {
                        var color = parts[i] + ";" + parts[i + 1] + ";" + parts[i + 2];
                        if (code0 == 38) fg = color; else bg = color;
                        i += 3; continue;
                    }
                    if (i + 4 < parts.Length && parts[i + 1] == "2")
                    {
                        var color = string.Join(';', parts[i..(i + 5)]);
                        if (code0 == 38) fg = color; else bg = color;
                        i += 5; continue;
                    }
                }
                switch (code0)
                {
                    case 0: Reset(); break;
                    case 1: bold = true; break;
                    case 2: dim = true; break;
                    case 3: italic = true; break;
                    case 4: underline = true; break;
                    case 5: blink = true; break;
                    case 7: inverse = true; break;
                    case 8: hidden = true; break;
                    case 9: strikethrough = true; break;
                    case 21: bold = false; break;
                    case 22: bold = false; dim = false; break;
                    case 23: italic = false; break;
                    case 24: underline = false; break;
                    case 25: blink = false; break;
                    case 27: inverse = false; break;
                    case 28: hidden = false; break;
                    case 29: strikethrough = false; break;
                    case 39: fg = null; break;
                    case 49: bg = null; break;
                    default:
                        if (code0 is >= 30 and <= 37 or >= 90 and <= 97) fg = code0.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        else if (code0 is >= 40 and <= 47 or >= 100 and <= 107) bg = code0.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
                i++;
            }
        }
        private void Reset() { bold = dim = italic = underline = blink = inverse = hidden = strikethrough = false; fg = bg = null; }
        public void Clear() { Reset(); link = null; }
        public string GetActiveCodes()
        {
            var codes = new List<string>();
            if (bold) codes.Add("1");
            if (dim) codes.Add("2");
            if (italic) codes.Add("3");
            if (underline) codes.Add("4");
            if (blink) codes.Add("5");
            if (inverse) codes.Add("7");
            if (hidden) codes.Add("8");
            if (strikethrough) codes.Add("9");
            if (fg is not null) codes.Add(fg);
            if (bg is not null) codes.Add(bg);
            var result = codes.Count > 0 ? "\u001b[" + string.Join(';', codes) + "m" : "";
            if (link is not null) result += FormatOsc8(link);
            return result;
        }
        public string GetActiveBackgroundCode() => bg is null ? "" : "\u001b[" + bg + "m";
        public bool HasActiveCodes => bold || dim || italic || underline || blink || inverse || hidden || strikethrough || fg is not null || bg is not null || link is not null;
        public string GetLineEndReset() => (underline ? "\u001b[24m" : "") + (link is null ? "" : "\u001b]8;;" + link.Terminator);
    }

    private static void UpdateTracker(string text, AnsiCodeTracker tracker)
    {
        var i = text.IndexOf('\u001b');
        while (i != -1)
        {
            var length = AnsiCodeLength(text, i);
            if (length > 0) { tracker.Process(text.Substring(i, length)); i += length; }
            else i++;
            i = i < text.Length ? text.IndexOf('\u001b', i) : -1;
        }
    }

    /// <summary>The background color active at the end of a styled string.</summary>
    public static string GetActiveBackgroundAnsi(string text)
    {
        var tracker = new AnsiCodeTracker(); UpdateTracker(text, tracker);
        return tracker.GetActiveBackgroundCode();
    }

    /// <summary><c>cjkBreakRegex</c>: Han, Hiragana, Katakana, Hangul and Bopomofo script extensions (the pinned ranges).</summary>
    public static bool IsCjkBreak(string segment) => TerminalEditorSourceWidth.Cjk(segment);

    private static readonly HashSet<char> CjkFullPunctuation = ['，', '．', '：', '；', '！', '？', '（', '）', '［', '］', '｛', '｝', '“', '”', '‘', '’', '…', '—'];
    /// <summary>CJK punctuation separates prose from completions (<c>cjkPunctuationRegex</c>).</summary>
    public static bool IsCjkPunctuation(string ch) => ch.Length > 0 && (CjkFullPunctuation.Contains(ch[0]) ||
        System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch, 0) is System.Globalization.UnicodeCategory.ConnectorPunctuation or
            System.Globalization.UnicodeCategory.DashPunctuation or System.Globalization.UnicodeCategory.OpenPunctuation or System.Globalization.UnicodeCategory.ClosePunctuation or
            System.Globalization.UnicodeCategory.InitialQuotePunctuation or System.Globalization.UnicodeCategory.FinalQuotePunctuation or
            System.Globalization.UnicodeCategory.OtherPunctuation && IsCjkBreak(ch));
    /// <summary>Whitespace or CJK punctuation (<c>autocompleteSeparatorRegex</c>).</summary>
    public static bool IsAutocompleteSeparator(string ch) => ch.Length > 0 && (IsJsWhitespace(ch[0]) || IsCjkPunctuation(ch));

    /// <summary>ECMAScript <c>\s</c>.</summary>
    public static bool IsJsWhitespace(char c) => c is '\x09' or '\x0a' or '\x0b' or '\x0c' or '\x0d' or '\x20' or '\xa0' or '\x1680' or
        >= '\x2000' and <= '\x200a' or '\x2028' or '\x2029' or '\x202f' or '\x205f' or '\x3000' or '\xfeff';
    public static string JsTrim(string value) { var s = 0; var e = value.Length; while (s < e && IsJsWhitespace(value[s])) s++; while (e > s && IsJsWhitespace(value[e - 1])) e--; return value[s..e]; }
    public static string JsTrimEnd(string value) { var e = value.Length; while (e > 0 && IsJsWhitespace(value[e - 1])) e--; return value[..e]; }
    public static string JsTrimStart(string value) { var s = 0; while (s < value.Length && IsJsWhitespace(value[s])) s++; return value[s..]; }

    private static List<string> SplitIntoTokensWithAnsi(string text)
    {
        var tokens = new List<string>(); var current = new StringBuilder(); var pending = new StringBuilder();
        int? currentKind = null; // 0 space, 1 word
        void Flush() { if (current.Length == 0) return; tokens.Add(current.ToString()); current.Clear(); currentKind = null; }
        foreach (var (ansi, chunk) in Runs(text))
        {
            if (ansi) { pending.Append(chunk); continue; }
            var asciiChunk = IsPrintableAscii(chunk);
            IEnumerable<string> segments = asciiChunk ? chunk.Select(c => c.ToString()) : Graphemes(chunk);
            foreach (var segment in segments)
            {
                var isSpace = segment == " ";
                if (!asciiChunk && !isSpace && IsCjkBreak(segment))
                {
                    Flush();
                    tokens.Add(pending + segment); pending.Clear();
                    continue;
                }
                var kind = isSpace ? 0 : 1;
                if (current.Length > 0 && currentKind != kind) Flush();
                if (pending.Length > 0) { current.Append(pending); pending.Clear(); }
                currentKind = kind; current.Append(segment);
            }
        }
        if (pending.Length > 0)
        {
            if (current.Length > 0) current.Append(pending);
            else if (tokens.Count > 0) tokens[^1] += pending.ToString();
            else current.Append(pending);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Word-wraps styled text to <paramref name="width"/> columns; styles carry across lines. No padding.</summary>
    public static List<string> WrapTextWithAnsi(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) return [""];
        var inputLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var result = new List<string>(); var tracker = new AnsiCodeTracker();
        foreach (var inputLine in inputLines)
        {
            var prefix = result.Count > 0 ? tracker.GetActiveCodes() : "";
            result.AddRange(WrapSingleLine(prefix + inputLine, width));
            UpdateTracker(inputLine, tracker);
        }
        return result.Count > 0 ? result : [""];
    }

    private static List<string> WrapSingleLine(string line, int width)
    {
        if (line.Length == 0) return [""];
        if (VisibleWidth(line) <= width) return [line];
        var wrapped = new List<string>(); var tracker = new AnsiCodeTracker();
        var currentLine = ""; var currentWidth = 0;
        foreach (var token in SplitIntoTokensWithAnsi(line))
        {
            var tokenWidth = VisibleWidth(token);
            var whitespace = JsTrim(token).Length == 0;
            if (tokenWidth > width && !whitespace)
            {
                if (currentLine.Length > 0)
                {
                    currentLine += tracker.GetLineEndReset();
                    wrapped.Add(currentLine); currentLine = ""; currentWidth = 0;
                }
                var broken = BreakLongWord(token, width, tracker);
                for (var i = 0; i < broken.Count - 1; i++) wrapped.Add(broken[i]);
                currentLine = broken[^1]; currentWidth = VisibleWidth(currentLine);
                continue;
            }
            if (currentWidth + tokenWidth > width && currentWidth > 0)
            {
                var lineToWrap = JsTrimEnd(currentLine) + tracker.GetLineEndReset();
                wrapped.Add(lineToWrap);
                if (whitespace) { currentLine = tracker.GetActiveCodes(); currentWidth = 0; }
                else { currentLine = tracker.GetActiveCodes() + token; currentWidth = tokenWidth; }
            }
            else { currentLine += token; currentWidth += tokenWidth; }
            UpdateTracker(token, tracker);
        }
        if (currentLine.Length > 0) wrapped.Add(currentLine);
        return wrapped.Count > 0 ? wrapped.Select(JsTrimEnd).ToList() : [""];
    }

    private static List<string> BreakLongWord(string word, int width, AnsiCodeTracker tracker)
    {
        var lines = new List<string>(); var currentLine = new StringBuilder(tracker.GetActiveCodes()); var currentWidth = 0;
        foreach (var (ansi, value) in Runs(word))
        {
            if (ansi) { currentLine.Append(value); tracker.Process(value); continue; }
            foreach (var grapheme in Graphemes(value))
            {
                if (grapheme.Length == 0) continue;
                var w = VisibleWidth(grapheme);
                if (currentWidth + w > width)
                {
                    currentLine.Append(tracker.GetLineEndReset());
                    lines.Add(currentLine.ToString());
                    currentLine.Clear().Append(tracker.GetActiveCodes()); currentWidth = 0;
                }
                currentLine.Append(grapheme); currentWidth += w;
            }
        }
        if (currentLine.Length > 0) lines.Add(currentLine.ToString());
        return lines.Count > 0 ? lines : [""];
    }

    public const string PunctuationChars = "(){}[]<>.,;:'\"!?+-=*/\\|&%^$#@~`";
    public static bool IsWhitespaceChar(string ch) => ch.Length > 0 && IsJsWhitespace(ch[0]);
    public static bool IsPunctuationChar(string ch) => ch.Length > 0 && PunctuationChars.Contains(ch[0]);

    /// <summary>Pads a line to <paramref name="width"/> and applies a background function to it.</summary>
    public static string ApplyBackgroundToLine(string line, int width, Func<string, string> bg) =>
        bg(line + new string(' ', Math.Max(0, width - VisibleWidth(line))));

    private static (string Text, int Width) TruncateFragment(string text, int maxWidth)
    {
        if (maxWidth <= 0 || text.Length == 0) return ("", 0);
        if (IsPrintableAscii(text)) { var clipped = text[..Math.Min(text.Length, maxWidth)]; return (clipped, clipped.Length); }
        var result = new StringBuilder(); var width = 0; var pending = new StringBuilder();
        foreach (var (ansi, value) in Runs(text))
        {
            if (ansi) { pending.Append(value); continue; }
            foreach (var segment in Graphemes(value))
            {
                var w = GraphemeWidth(segment);
                if (width + w > maxWidth) return (result.ToString(), width);
                if (pending.Length > 0) { result.Append(pending); pending.Clear(); }
                result.Append(segment); width += w;
            }
        }
        return (result.ToString(), width);
    }

    private static string FinalizeTruncated(string prefix, int prefixWidth, string ellipsis, int ellipsisWidth, int maxWidth, bool pad)
    {
        const string reset = "\u001b[0m";
        var close = ActiveOsc8Close(prefix);
        var result = ellipsis.Length > 0 ? prefix + close + reset + ellipsis + reset : prefix + close + reset;
        return pad ? result + new string(' ', Math.Max(0, maxWidth - (prefixWidth + ellipsisWidth))) : result;
    }

    /// <summary>Truncates styled text to <paramref name="maxWidth"/> columns with an ellipsis; optionally pads to exactly that width.</summary>
    public static string TruncateToWidth(string text, int maxWidth, string ellipsis = "...", bool pad = false)
    {
        if (maxWidth <= 0) return "";
        if (text.Length == 0) return pad ? new string(' ', maxWidth) : "";
        var ellipsisWidth = VisibleWidth(ellipsis);
        if (ellipsisWidth >= maxWidth)
        {
            var textWidth = VisibleWidth(text);
            if (textWidth <= maxWidth) return pad ? text + new string(' ', maxWidth - textWidth) : text;
            var clipped = TruncateFragment(ellipsis, maxWidth);
            if (clipped.Width == 0) return pad ? new string(' ', maxWidth) : "";
            return FinalizeTruncated("", 0, clipped.Text, clipped.Width, maxWidth, pad);
        }
        if (IsPrintableAscii(text))
        {
            if (text.Length <= maxWidth) return pad ? text + new string(' ', maxWidth - text.Length) : text;
            var target = maxWidth - ellipsisWidth;
            return FinalizeTruncated(text[..target], target, ellipsis, ellipsisWidth, maxWidth, pad);
        }
        var targetWidth = maxWidth - ellipsisWidth;
        var kept = new StringBuilder(); var pendingAnsi = new StringBuilder();
        int visibleSoFar = 0, keptWidth = 0; var keepPrefix = true; var overflowed = false; var exhausted = false;
        var i = 0;
        while (i < text.Length)
        {
            var length = AnsiCodeLength(text, i);
            if (length > 0) { pendingAnsi.Append(text, i, length); i += length; continue; }
            if (text[i] == '\t')
            {
                if (keepPrefix && keptWidth + 3 <= targetWidth) { kept.Append(pendingAnsi).Append('\t'); pendingAnsi.Clear(); keptWidth += 3; }
                else { keepPrefix = false; pendingAnsi.Clear(); }
                visibleSoFar += 3;
                if (visibleSoFar > maxWidth) { overflowed = true; break; }
                i++; continue;
            }
            var end = i;
            while (end < text.Length && text[end] != '\t' && AnsiCodeLength(text, end) == 0) end++;
            foreach (var segment in Graphemes(text[i..end]))
            {
                var w = GraphemeWidth(segment);
                if (keepPrefix && keptWidth + w <= targetWidth) { kept.Append(pendingAnsi).Append(segment); pendingAnsi.Clear(); keptWidth += w; }
                else { keepPrefix = false; pendingAnsi.Clear(); }
                visibleSoFar += w;
                if (visibleSoFar > maxWidth) { overflowed = true; break; }
            }
            if (overflowed) break;
            i = end;
        }
        exhausted = i >= text.Length;
        if (!overflowed && exhausted) return pad ? text + new string(' ', Math.Max(0, maxWidth - visibleSoFar)) : text;
        return FinalizeTruncated(kept.ToString(), keptWidth, ellipsis, ellipsisWidth, maxWidth, pad);
    }

    /// <summary>Extracts visible columns [start, start+length) of a styled line.</summary>
    public static string SliceByColumn(string line, int startCol, int length, bool strict = false) => SliceWithWidth(line, startCol, length, strict).Text;

    public static (string Text, int Width) SliceWithWidth(string line, int startCol, int length, bool strict = false)
    {
        if (length <= 0) return ("", 0);
        var endCol = startCol + length;
        var result = new StringBuilder(); var resultWidth = 0; var currentCol = 0; var pending = new StringBuilder();
        foreach (var (ansi, value) in Runs(line))
        {
            if (ansi)
            {
                if (currentCol >= startCol && currentCol < endCol) { result.Append(pending).Append(value); pending.Clear(); }
                else if (currentCol < startCol) pending.Append(value);
                continue;
            }
            foreach (var segment in Graphemes(value))
            {
                var w = GraphemeWidth(segment);
                var inRange = currentCol >= startCol && currentCol < endCol;
                var fits = !strict || currentCol + w <= endCol;
                if (inRange && fits) { result.Append(pending).Append(segment); pending.Clear(); resultWidth += w; }
                currentCol += w;
                if (currentCol >= endCol) break;
            }
            if (currentCol >= endCol) break;
        }
        return (result.ToString(), resultWidth);
    }

    /// <summary>The "before" and "after" segments around an overlay region; "after" inherits styles active before it.</summary>
    public static (string Before, int BeforeWidth, string After, int AfterWidth) ExtractSegments(string line, int beforeEnd, int afterStart, int afterLen, bool strictAfter = false)
    {
        var before = new StringBuilder(); var after = new StringBuilder(); int beforeWidth = 0, afterWidth = 0, currentCol = 0;
        var pendingBefore = new StringBuilder(); var afterStarted = false; var afterEnd = afterStart + afterLen;
        var tracker = new AnsiCodeTracker();
        bool Done() => afterLen <= 0 ? currentCol >= beforeEnd : currentCol >= afterEnd;
        foreach (var (ansi, value) in Runs(line))
        {
            if (ansi)
            {
                tracker.Process(value);
                if (currentCol < beforeEnd) pendingBefore.Append(value);
                else if (currentCol >= afterStart && currentCol < afterEnd && afterStarted) after.Append(value);
                continue;
            }
            foreach (var segment in Graphemes(value))
            {
                var w = GraphemeWidth(segment);
                if (currentCol < beforeEnd && currentCol + w <= beforeEnd)
                {
                    before.Append(pendingBefore).Append(segment); pendingBefore.Clear(); beforeWidth += w;
                }
                else if (currentCol >= afterStart && currentCol < afterEnd)
                {
                    if (!strictAfter || currentCol + w <= afterEnd)
                    {
                        if (!afterStarted) { after.Append(tracker.GetActiveCodes()); afterStarted = true; }
                        after.Append(segment); afterWidth += w;
                    }
                }
                currentCol += w;
                if (Done()) break;
            }
            if (Done()) break;
        }
        return (before.ToString(), beforeWidth, after.ToString(), afterWidth);
    }

    /// <summary>JavaScript <c>String.prototype.repeat</c> for a non-negative count.</summary>
    public static string Repeat(string value, int count) => count <= 0 ? "" : new StringBuilder(value.Length * count).Insert(0, value, count).ToString();
}
