// lovely-mermaid 0.3.3 (Apache-2.0): src/diagrams/timeline.ts.
namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal static class MermaidTimeline
{
    private sealed record Row(string Period, string Event, bool Section = false);

    public static MermaidDrawn? Render(string src)
    {
        var statements = MermaidStatements.StatementsOf(src);
        if (MermaidStatements.HeaderKind(statements) != "timeline") return null;
        string? title = null;
        var rows = new List<Row>();
        var warnings = new List<string>();
        var lastPeriod = false;
        foreach (var st in statements.Skip(1))
        {
            // The upstream cap is checked per statement, so a multi-event statement may exceed it.
            if (rows.Count >= MermaidGraph.MaxEdges) { warnings.Add($"diagram truncated: event cap ({MermaidGraph.MaxEdges}) reached"); break; }
            var first = MermaidStatements.Words(st).FirstOrDefault()?.ToLowerInvariant();
            if (first == "title")
            {
                title = MermaidStatements.NonEmpty(MermaidJs.Trim(st[(st.ToLowerInvariant().IndexOf("title", StringComparison.Ordinal) + 5)..]));
                continue;
            }
            if (first == "section")
            {
                var name = MermaidJs.Trim(st[(st.ToLowerInvariant().IndexOf("section", StringComparison.Ordinal) + 7)..]);
                rows.Add(new("", Clean(name), true));
                lastPeriod = false;
                continue;
            }
            var parts = st.Split(':').Select(Clean).ToList();
            var period = parts[0];
            parts.RemoveAt(0);
            if (period == "" && parts.Count > 0 && lastPeriod)
            {
                foreach (var ev in parts) rows.Add(new("", ev));
                continue;
            }
            if (period != "" && parts.Count == 0)
            {
                rows.Add(new(period, ""));
                lastPeriod = true;
                continue;
            }
            if (period == "" || parts.Any(p => p == ""))
            {
                warnings.Add($"dropped, unreadable statement: \"{st}\"");
                continue;
            }
            for (var i = 0; i < parts.Count; i++) rows.Add(new(i == 0 ? period : "", parts[i]));
            lastPeriod = true;
        }
        if (rows.Count == 0) return null;
        var periodWidth = rows.Max(r => r.Section ? 0 : MermaidWidth.StringWidth(r.Period));
        var top = title is null ? 0 : 1;
        var width = Math.Max(title is null ? 0 : MermaidWidth.StringWidth(title) + 6,
            rows.Max(r => r.Section ? MermaidWidth.StringWidth(r.Event) : periodWidth + 3 + MermaidWidth.StringWidth(r.Event)));
        var canvas = new MermaidCanvas(width, top + rows.Count);
        if (title is not null)
        {
            var t = $" {title} ";
            var x = Math.Max(0, MermaidJs.Half(width - MermaidWidth.StringWidth(t) - 4));
            MermaidCanvas.DrawText(canvas, "──", x, 0, MermaidCls.Edge);
            MermaidCanvas.DrawText(canvas, t, x + 2, 0, MermaidCls.Title);
            MermaidCanvas.DrawText(canvas, "──", x + 2 + MermaidWidth.StringWidth(t), 0, MermaidCls.Edge);
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var y = top + i;
            if (row.Section) { MermaidCanvas.DrawText(canvas, row.Event, 0, y, MermaidCls.Title); continue; }
            MermaidCanvas.DrawText(canvas, row.Period, 0, y, MermaidCls.Text);
            if (row.Event == "") continue;
            MermaidCanvas.DrawText(canvas, "─", periodWidth + 1, y, MermaidCls.Edge);
            MermaidCanvas.DrawText(canvas, row.Event, periodWidth + 3, y, MermaidCls.EdgeLabel);
        }
        return new(canvas, warnings, new());
    }

    private static string Clean(string text) => MermaidLabels.FitLabel(MermaidLabels.DecodeHtmlEntities(MermaidJs.Trim(text)), MermaidLabels.MaxLabel);
}
