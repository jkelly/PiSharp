// lovely-mermaid 0.3.3 (Apache-2.0): src/diagrams/gitgraph.ts.
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal static class MermaidGitgraph
{
    private sealed record Commit(int Lane, string Id, string? Tag, int MergeFrom = -1);
    private sealed record Row(string Kind, int At = -1, int Parent = -1, int Lane = -1);

    public static MermaidDrawn? Render(string src)
    {
        var statements = MermaidStatements.StatementsOf(src);
        var kind = MermaidStatements.HeaderKind(statements);
        if (kind is not ("gitgraph" or "gitgraph:")) return null;
        var branches = new List<string> { "main" };
        var forkAt = new List<int> { -1 };
        var commits = new List<Commit>();
        var warnings = new List<string>();
        var heads = new List<int> { -1 };
        var cur = 0;
        var auto = 0;
        foreach (var st in statements.Skip(1))
        {
            if (commits.Count >= MermaidGraph.MaxEdges) { warnings.Add($"diagram truncated: commit cap ({MermaidGraph.MaxEdges}) reached"); break; }
            var word = MermaidStatements.Words(st).FirstOrDefault() ?? "";
            var first = word.ToLowerInvariant();
            var rest = MermaidJs.Trim(st[word.Length..]);
            switch (first)
            {
                case "commit":
                {
                    var attrs = CommitAttrs(rest);
                    heads[cur] = commits.Count;
                    commits.Add(new(cur, attrs.Id ?? $"c{auto++}", attrs.Tag));
                    break;
                }
                case "branch":
                {
                    var (name, _) = NameToken(rest);
                    var fork = heads[cur] >= 0 ? heads[cur] : forkAt[cur];
                    if (name is null || branches.Contains(name) || fork < 0) { Drop(st); break; }
                    branches.Add(name);
                    forkAt.Add(fork);
                    heads.Add(-1);
                    cur = branches.Count - 1;
                    break;
                }
                case "checkout":
                case "switch":
                {
                    var lane = branches.IndexOf(NameToken(rest).Name ?? "");
                    if (lane < 0) { Drop(st); break; }
                    cur = lane;
                    break;
                }
                case "merge":
                {
                    var (name, after) = NameToken(rest);
                    var lane = branches.IndexOf(name ?? "");
                    if (lane < 0 || lane == cur) { Drop(st); break; }
                    var attrs = CommitAttrs(after);
                    heads[cur] = commits.Count;
                    commits.Add(new(cur, attrs.Id ?? "", attrs.Tag, lane));
                    break;
                }
                case "cherry-pick":
                {
                    var attrs = CommitAttrs(rest);
                    heads[cur] = commits.Count;
                    commits.Add(new(cur, attrs.Id is null ? $"c{auto++}" : $"⟲ {attrs.Id}", attrs.Tag));
                    break;
                }
                default: Drop(st); break;
            }
        }
        if (commits.Count == 0) return null;
        var headOf = new Dictionary<int, int>();
        for (var i = 0; i < commits.Count; i++) headOf[commits[i].Lane] = i;
        var rows = new List<Row>();
        var used = Enumerable.Range(0, branches.Count).Select(lane => lane == 0 || commits.Any(c => c.Lane == lane || c.MergeFrom == lane)).ToArray();
        for (var i = commits.Count - 1; i >= 0; i--)
        {
            var c = commits[i];
            rows.Add(new("commit", At: i));
            if (c.MergeFrom >= 0) rows.Add(new("open", Parent: c.Lane, Lane: c.MergeFrom));
            for (var lane = branches.Count - 1; lane > 0; lane--)
                if (forkAt[lane] == i - 1 && used[lane]) rows.Add(new("close", Parent: commits[i - 1].Lane, Lane: lane));
        }
        var graphWidth = branches.Count * 2;
        var labels = rows.Select(row =>
        {
            var parts = new List<(string Text, string Role)>();
            if (row.Kind != "commit") return parts;
            var c = commits[row.At];
            if (c.Id != "") parts.Add((c.Id, MermaidCls.Text));
            if (headOf[c.Lane] == row.At) parts.Add(($"({branches[c.Lane]})", MermaidCls.EdgeLabel));
            if (c.Tag is not null) parts.Add(($"[{c.Tag}]", MermaidCls.EdgeLabel));
            if (c.MergeFrom >= 0) parts.Add(($"⇐ {branches[c.MergeFrom]}", MermaidCls.EdgeLabel));
            return parts;
        }).ToList();
        var width = graphWidth + Math.Max(1, labels.Max(parts => parts.Sum(p => MermaidWidth.StringWidth(p.Text) + 1) - 1));
        var canvas = new MermaidCanvas(width, rows.Count);
        var live = new HashSet<int>();
        for (var y = 0; y < rows.Count; y++)
        {
            var row = rows[y];
            if (row.Kind == "commit")
            {
                var c = commits[row.At];
                live.Add(c.Lane);
                foreach (var lane in live) if (lane != c.Lane) canvas.AddBits(lane * 2, y, MermaidCanvas.U | MermaidCanvas.D);
                canvas.Set(c.Lane * 2, y, "●", MermaidCls.Edge);
                var x = graphWidth;
                foreach (var (text, role) in labels[y])
                {
                    MermaidCanvas.DrawText(canvas, text, x, y, role);
                    x += MermaidWidth.StringWidth(text) + 1;
                }
                continue;
            }
            var parent = row.Parent;
            var child = row.Lane;
            var rejoins = row.Kind == "open" && live.Contains(child);
            if (row.Kind == "open") live.Add(child);
            foreach (var lane in live) if (lane != parent && lane != child) canvas.AddBits(lane * 2, y, MermaidCanvas.U | MermaidCanvas.D);
            if (row.Kind == "open") canvas.AddBits(parent * 2, y, MermaidCanvas.U | MermaidCanvas.D);
            else canvas.AddBits(parent * 2, y, MermaidCanvas.D | (live.Contains(parent) ? MermaidCanvas.U : 0));
            live.Add(parent);
            canvas.SegH(y, parent * 2, child * 2);
            canvas.AddBits(child * 2, y, (row.Kind == "open" ? MermaidCanvas.D : MermaidCanvas.U) | (rejoins ? MermaidCanvas.U : 0));
            if (row.Kind == "close") live.Remove(child);
        }
        canvas.FinalizeMask();
        return new(canvas, warnings, new());

        void Drop(string statement) => warnings.Add($"dropped, unreadable statement: \"{statement}\"");
    }

    private static (string? Name, string After) NameToken(string rest)
    {
        if (rest.StartsWith('"'))
        {
            var close = rest.IndexOf('"', 1);
            if (close >= 0) return (rest[1..close], rest[(close + 1)..]);
        }
        var word = MermaidStatements.Words(rest).FirstOrDefault();
        return (word, word is null ? rest : rest[word.Length..]);
    }

    private static (string? Id, string? Tag) CommitAttrs(string rest)
    {
        string? id = null, tag = null;
        // Explicit ECMAScript whitespace avoids .NET's extra whitespace code points.
        const string space = @"[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*";
        foreach (Match match in Regex.Matches(rest, "(id|tag)" + space + ":" + space + "\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var value = MermaidLabels.FitLabel(MermaidLabels.CleanLabel(match.Groups[2].Value), MermaidLabels.MaxLabel);
            if (match.Groups[1].Value.ToLowerInvariant() == "id") id = value;
            else tag = value;
        }
        return (id, tag);
    }
}
