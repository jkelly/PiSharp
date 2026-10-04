using System.Text;

namespace PiSharp.Tools.Files;

public sealed record TextEdit(string OldText, string NewText);
public sealed record EditMatch(bool Found, int Index, int MatchLength, bool UsedFuzzyMatch, string ContentForReplacement);

/// <summary>Source-ordered exact then fuzzy matching in UTF-16 offset space. Performs no file effects.</summary>
public static class EditMatcher
{
    public static string NormalizeToLF(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    public static string DetectLineEnding(string text)
    {
        var crlf = text.IndexOf("\r\n", StringComparison.Ordinal); var lf = text.IndexOf('\n');
        return crlf >= 0 && lf >= 0 && crlf < lf ? "\r\n" : "\n";
    }
    public static string RestoreLineEndings(string text, string ending) => ending == "\r\n" ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;
    public static string NormalizeForFuzzyMatch(string text)
    {
        var lines = text.Normalize(NormalizationForm.FormKC).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var end = lines[index].Length;
            while (end > 0 && IsEcmaWhitespace(lines[index][end - 1])) end--;
            lines[index] = lines[index][..end];
        }
        var result = string.Join('\n', lines).ToCharArray();
        for (var index = 0; index < result.Length; index++) result[index] = result[index] switch
        {
            '\u2018' or '\u2019' or '\u201a' or '\u201b' => '\'',
            '\u201c' or '\u201d' or '\u201e' or '\u201f' => '"',
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
            '\u00a0' or >= '\u2002' and <= '\u200a' or '\u202f' or '\u205f' or '\u3000' => ' ',
            var character => character
        };
        return new(result);
    }
    private static bool IsEcmaWhitespace(char value) => value is '\t' or '\v' or '\f' or ' ' or '\u00a0' or '\ufeff' or '\n' or '\r' or
        '\u2028' or '\u2029' or '\u1680' or >= '\u2000' and <= '\u200a' or '\u202f' or '\u205f' or '\u3000';

    public static EditMatch Find(string content, string oldText)
    {
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        if (index >= 0) return new(true, index, oldText.Length, false, content);
        var normalized = NormalizeForFuzzyMatch(content); var pattern = NormalizeForFuzzyMatch(oldText);
        index = normalized.IndexOf(pattern, StringComparison.Ordinal);
        return index < 0 ? new(false, -1, 0, false, content) : new(true, index, pattern.Length, true, normalized);
    }
    public static int CountOccurrences(string content, string oldText)
    {
        var normalized = NormalizeForFuzzyMatch(content); var pattern = NormalizeForFuzzyMatch(oldText);
        // JS split("") returns UTF-16 units without boundary empty strings; retain the source quirk.
        if (pattern.Length == 0) return normalized.Length - 1;
        var count = 0; var offset = 0;
        while ((offset = normalized.IndexOf(pattern, offset, StringComparison.Ordinal)) >= 0) { count++; offset += pattern.Length; }
        return count;
    }
}
