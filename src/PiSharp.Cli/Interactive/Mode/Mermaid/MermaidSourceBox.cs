// grok-mermaid 0.2.3 (Apache-2.0): src/source-box.ts — used by Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/mermaid.ts.
using System.Text;
using static PiSharp.Cli.Interactive.Mode.Mermaid.MermaidJs;

namespace PiSharp.Cli.Interactive.Mode.Mermaid;

/// <summary>The raw source in a framed box: what to show when the art is missing or too wide.</summary>
internal static class MermaidSourceBox
{
    /// <summary>Frame <paramref name="src"/> in a titled box, hard-wrapping its lines to <paramref name="maxWidth"/> columns.</summary>
    public static MermaidArt SourceBox(string src, int? maxWidth = null)
    {
        src = MermaidLabels.StripControls(src);
        var headerWords = Words(src);
        var header = headerWords.Count > 0 ? headerWords[0] : "diagram";
        var title = $" mermaid: {header} ";
        int? limit = maxWidth is { } mw ? Math.Max(8, Sat(mw, 4)) : null;

        var body = new List<string>();
        var started = false;
        foreach (var raw in MermaidLabels.SrcLines(src))
        {
            var l = TrimEnd(raw);
            if (!started && l == "") continue;
            started = true;
            body.AddRange(ChunkLine(l, limit));
        }

        var contentW = Math.Max(Math.Max(MermaidWidth.StringWidth(title), body.Count == 0 ? 0 : body.Max(MermaidWidth.StringWidth)), 0);
        var inner = contentW + 2;

        var plain = new List<string>();
        var styled = new List<IReadOnlyList<MermaidSpan>>();

        var rule = new string('─', Sat(inner, MermaidWidth.StringWidth(title)));
        plain.Add($"╭{title}{rule}╮");
        styled.Add([new("╭", MermaidCls.Border), new(title, MermaidCls.Title), new($"{rule}╮", MermaidCls.Border)]);

        foreach (var line in body)
        {
            var pad = new string(' ', Sat(contentW, MermaidWidth.StringWidth(line)));
            plain.Add($"│ {line}{pad} │");
            styled.Add([new("│ ", MermaidCls.Border), new(line, MermaidCls.Text), new($"{pad} │", MermaidCls.Border)]);
        }

        var bottom = $"╰{new string('─', inner)}╯";
        plain.Add(bottom);
        styled.Add([new(bottom, MermaidCls.Border)]);

        return new(inner + 2, plain, styled, []);
    }

    /// <summary>Hard-break a line at <paramref name="limit"/> columns, never splitting a wide glyph.</summary>
    private static List<string> ChunkLine(string line, int? limit)
    {
        if (limit is not { } max || MermaidWidth.StringWidth(line) <= max) return [line];
        var chunks = new List<string>();
        var cur = new StringBuilder();
        var curW = 0;
        foreach (var (c, cw) in MermaidWidth.Measured(line))
        {
            if (curW + cw > max && cur.Length > 0)
            {
                chunks.Add(cur.ToString());
                cur.Clear();
                curW = 0;
            }
            cur.Append(c);
            curW += cw;
        }
        if (cur.Length > 0) chunks.Add(cur.ToString());
        return chunks;
    }
}
