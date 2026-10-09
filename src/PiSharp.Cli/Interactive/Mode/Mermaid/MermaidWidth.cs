// grok-mermaid 0.2.3 (Apache-2.0): src/width.ts — used by Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/mermaid.ts.
namespace PiSharp.Cli.Interactive.Mode.Mermaid;

/// <summary>
/// Display width, measured in grapheme clusters. The original clusters with <c>Intl.Segmenter</c> (UAX #29); this
/// port runs the same extended grapheme cluster rules over classes generated from that segmenter
/// (<see cref="MermaidUnicodeData.Graphemes"/>), so it does not depend on the .NET runtime's Unicode version.
/// </summary>
internal static class MermaidWidth
{
    private const int Vs16 = 0xfe0f;

    private static bool IsRegionalIndicator(int cp) => cp is >= 0x1f1e6 and <= 0x1f1ff;

    /// <summary>Width of one code point; the table covers the whole code point space.</summary>
    private static int CodePointWidth(int cp)
    {
        var t = MermaidUnicodeData.Widths;
        var lo = 0;
        var hi = t.Length / 3 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (cp < t[mid * 3]) hi = mid - 1;
            else if (cp > t[mid * 3 + 1]) lo = mid + 1;
            else return t[mid * 3 + 2];
        }
        return 1;
    }

    /// <summary>Columns occupied by one grapheme cluster: the widest code point, forced to two by VS16 or a flag.</summary>
    public static int ClusterWidth(string cluster)
    {
        var w = 0;
        var vs16 = false;
        var regional = 0;
        for (var i = 0; i < cluster.Length;)
        {
            var cp = MermaidJs.CodePointAt(cluster, i);
            i += cp > 0xffff ? 2 : 1;
            if (cp == Vs16) vs16 = true;
            if (IsRegionalIndicator(cp)) regional++;
            var cw = CodePointWidth(cp);
            if (cw > w) w = cw;
        }
        return vs16 || regional >= 2 ? 2 : w;
    }

    /// <summary>Iterate grapheme clusters, so no loop can split one.</summary>
    public static IEnumerable<string> Clusters(string s) => MermaidGraphemes.Segment(s);

    /// <summary>Iterate clusters paired with their display width.</summary>
    public static IEnumerable<(string Cluster, int Width)> Measured(string s)
    {
        foreach (var c in MermaidGraphemes.Segment(s)) yield return (c, ClusterWidth(c));
    }

    /// <summary>Display columns of a string.</summary>
    public static int StringWidth(string s)
    {
        var w = 0;
        foreach (var c in MermaidGraphemes.Segment(s)) w += ClusterWidth(c);
        return w;
    }
}

/// <summary>UAX #29 extended grapheme clusters (GB3–GB13 including GB9c), as <c>Intl.Segmenter('en', { granularity: 'grapheme' })</c>.</summary>
internal static class MermaidGraphemes
{
    // Grapheme_Cluster_Break (low nibble of the table value); keep in sync with gen-unicode-data.mjs.
    private const int Other = 0, CR = 1, LF = 2, Control = 3, Extend = 4, Zwj = 5, RI = 6, Prepend = 7, SpacingMark = 8;
    private const int L = 9, V = 10, T = 11, LV = 12, LVT = 13, ExtPict = 14;
    // Indic_Conjunct_Break (high nibble).
    private const int InCbConsonant = 1, InCbExtend = 2, InCbLinker = 3;

    private static int Lookup(int cp)
    {
        var t = MermaidUnicodeData.Graphemes;
        var lo = 0;
        var hi = t.Length / 3 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (cp < t[mid * 3]) hi = mid - 1;
            else if (cp > t[mid * 3 + 1]) lo = mid + 1;
            else return t[mid * 3 + 2];
        }
        return Other;
    }

    public static IEnumerable<string> Segment(string s)
    {
        if (s.Length == 0) yield break;
        var start = 0;
        var prev = -1;
        var emoji = 0; // 1: ExtPict Extend*, 2: ExtPict Extend* ZWJ
        var riRun = 0; // consecutive RI ending at prev
        var conjunct = 0; // 1: Consonant [Extend Linker]*, 2: ... with at least one Linker
        for (var i = 0; i < s.Length;)
        {
            var cp = MermaidJs.CodePointAt(s, i);
            var len = cp > 0xffff ? 2 : 1;
            var v = Lookup(cp);
            var g = v & 15;
            var incb = v >> 4;
            if (prev >= 0 && Breaks(prev, g, incb, emoji, riRun, conjunct))
            {
                yield return s[start..i];
                start = i;
            }
            emoji = g == ExtPict ? 1 : emoji == 1 && g == Extend ? 1 : emoji == 1 && g == Zwj ? 2 : 0;
            riRun = g == RI ? riRun + 1 : 0;
            conjunct = incb == InCbConsonant ? 1 : conjunct != 0 && incb == InCbLinker ? 2 : conjunct != 0 && incb == InCbExtend ? conjunct : 0;
            prev = g;
            i += len;
        }
        yield return s[start..];
    }

    private static bool Breaks(int prev, int g, int incb, int emoji, int riRun, int conjunct)
    {
        if (prev == CR && g == LF) return false; // GB3
        if (prev is Control or CR or LF) return true; // GB4
        if (g is Control or CR or LF) return true; // GB5
        if (prev == L && g is L or V or LV or LVT) return false; // GB6
        if (prev is LV or V && g is V or T) return false; // GB7
        if (prev is LVT or T && g == T) return false; // GB8
        if (g is Extend or Zwj or SpacingMark) return false; // GB9, GB9a
        if (prev == Prepend) return false; // GB9b
        if (conjunct == 2 && incb == InCbConsonant) return false; // GB9c
        if (emoji == 2 && g == ExtPict) return false; // GB11
        if (prev == RI && g == RI && riRun % 2 == 1) return false; // GB12, GB13
        return true; // GB999
    }
}
