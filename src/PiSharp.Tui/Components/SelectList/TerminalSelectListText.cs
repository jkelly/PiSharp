using System.Globalization;
using System.Text;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

namespace PiSharp.Tui.Components.SelectList;

// Translation of the pinned Pi utils.ts empty-ellipsis path, under the retained upstream MIT notice.
// Width and grapheme tables are the existing pinned TUI Source profile; no extra dependency or I/O.
internal static class TerminalSelectListText
{
    internal static void Validate(string value, int maximum)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > maximum) throw new TerminalRenderException(TerminalRenderFailure.ResourceLimit);
        for (var at = 0; at < value.Length; at++)
        {
            if (!char.IsSurrogate(value[at])) continue;
            if (!char.IsHighSurrogate(value[at]) || at + 1 >= value.Length || !char.IsLowSurrogate(value[++at]))
                throw new TerminalRenderException(TerminalRenderFailure.InvalidUnicode);
        }
    }

    internal static string SingleLine(string value)
    {
        var text = new StringBuilder(); var newline = false;
        foreach (var c in value)
        {
            if (c is '\r' or '\n') { if (!newline) text.Append(' '); newline = true; }
            else { text.Append(c); newline = false; }
        }
        var result = text.ToString(); var start = 0; var end = result.Length;
        while (start < end && JsWhitespace(result[start])) start++;
        while (end > start && JsWhitespace(result[end - 1])) end--;
        return result[start..end];
    }

    private static bool JsWhitespace(char c) => c is '\u0009' or '\u000a' or '\u000b' or '\u000c' or
        '\u000d' or '\u0020' or '\u00a0' or '\u1680' or >= '\u2000' and <= '\u200a' or
        '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff';

    // ECMAScript default lowercasing includes the dotted-I expansion and contextual final sigma.
    // Other scalar mappings use .NET's invariant Unicode tables; exhaustive version parity remains unqualified.
    internal static string Lower(string value)
    {
        var runes = value.EnumerateRunes().ToArray(); var text = new StringBuilder(value.Length);
        for (var at = 0; at < runes.Length; at++)
        {
            var rune = runes[at];
            if (rune.Value == 0x130) text.Append("i\u0307");
            else if (rune.Value == 0x3a3 && FinalSigma(runes, at)) text.Append('\u03c2');
            else text.Append(Rune.ToLowerInvariant(rune).ToString());
        }
        return text.ToString();
    }

    private static bool FinalSigma(Rune[] runes, int at)
    {
        var before = at - 1; while (before >= 0 && CaseIgnorable(runes[before])) before--;
        if (before < 0 || !Cased(runes[before])) return false;
        var after = at + 1; while (after < runes.Length && CaseIgnorable(runes[after])) after++;
        return after == runes.Length || !Cased(runes[after]);
    }
    private static bool Cased(Rune r) => Rune.ToUpperInvariant(r) != Rune.ToLowerInvariant(r) ||
        Rune.GetUnicodeCategory(r) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter;
    private static bool CaseIgnorable(Rune r) => Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or
        UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol ||
        r.Value is 0x27 or 0x3a or 0xb7 or 0x387 or 0x55f or 0x5f4 or 0x2019 or 0x2027;

    internal static int Width(string value)
    {
        // SelectList's utility recognizes a narrower CSI set than the editor's shared width cleaner.
        // Remove exactly those tokens; retain unknown ESC as a zero-width control placeholder so
        // the shared cleaner cannot accidentally swallow its following visible characters.
        var clean = new StringBuilder(value.Length);
        for (var at = 0; at < value.Length;)
        {
            var length = AnsiLength(value, at);
            if (length > 0) { at += length; continue; }
            clean.Append(value[at] == '\u001b' ? '\0' : value[at]); at++;
        }
        return TerminalEditorSourceWidth.Visible(clean.ToString());
    }
    internal static string Safe(string value) => TerminalTextLayout.EscapeSourceSpan(value);

    internal static string Truncate(string text, int maximum, bool sourceReset)
    {
        if (maximum <= 0) return "";
        // Source's only width shortcut here is printable ASCII. Whole-string visibleWidth strips
        // ANSI before segmentation and can join graphemes across an ANSI boundary; truncation
        // instead measures the independently segmented spans on either side of that boundary.
        if (text.All(c => c is >= '\u0020' and <= '\u007e'))
            return text.Length <= maximum ? text : text[..maximum] + (sourceReset ? "\u001b[0m" : "");
        var result = new StringBuilder(); var pending = new StringBuilder();
        var keptWidth = 0; var visibleSoFar = 0; var keepContiguousPrefix = true; var overflowed = false;
        var activeLinkTerminator = "";
        var at = 0;
        while (at < text.Length)
        {
            var ansi = AnsiLength(text, at);
            if (ansi > 0) { pending.Append(text, at, ansi); at += ansi; continue; }
            if (text[at] == '\t')
            {
                Consume("\t", 3);
                if (overflowed) break;
                at++; continue;
            }
            var end = at;
            while (end < text.Length && text[end] != '\t' && AnsiLength(text, end) == 0) end++;
            foreach (var segment in TerminalSourceGraphemeSegmenter.Elements(text[at..end]))
            {
                Consume(segment, Width(segment));
                if (overflowed) break;
            }
            if (overflowed) break;
            at = end;
        }
        // Preserve the original bytes, including trailing pending ANSI, only after exhausting the
        // independently measured input without overflow. Do not substitute whole-string width.
        var exhaustedInput = at >= text.Length;
        return !overflowed && exhaustedInput ? text : Finish();

        void Consume(string segment, int cells)
        {
            if (keepContiguousPrefix && cells <= maximum - keptWidth)
            { AcceptPending(); result.Append(segment); keptWidth += cells; }
            else { keepContiguousPrefix = false; pending.Clear(); }
            visibleSoFar += cells;
            if (visibleSoFar > maximum) overflowed = true;
        }

        void AcceptPending()
        {
            if (pending.Length == 0) return;
            var codes = pending.ToString();
            for (var codeAt = 0; codeAt < codes.Length;)
            {
                var length = AnsiLength(codes, codeAt); if (length == 0) { codeAt++; continue; }
                var code = codes.Substring(codeAt, length);
                if (code.StartsWith("\u001b]8;", StringComparison.Ordinal))
                {
                    var terminator = code.EndsWith('\a') ? "\a" : "\u001b\\";
                    var body = code[4..^terminator.Length]; var separator = body.IndexOf(';');
                    if (separator >= 0) activeLinkTerminator = separator == body.Length - 1 ? "" : terminator;
                }
                codeAt += length;
            }
            result.Append(codes); pending.Clear();
        }
        string Finish() => result.ToString() + (sourceReset ?
            (activeLinkTerminator.Length == 0 ? "" : "\u001b]8;;" + activeLinkTerminator) + "\u001b[0m" : "");
    }

    // Exact recognized terminators from pinned utils.ts, deliberately distinct from a generic VT parser.
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
                if (text[end] == '\a') return end + 1 - at;
                if (text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\') return end + 2 - at;
            }
        }
        return 0;
    }
}
