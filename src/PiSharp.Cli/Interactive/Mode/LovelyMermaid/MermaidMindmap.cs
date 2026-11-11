// lovely-mermaid 0.3.3 (Apache-2.0): src/diagrams/mindmap.ts.
namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal static class MermaidMindmap
{
    private sealed record MindNode(string Text)
    {
        public List<MindNode> Children { get; } = [];
    }
    private static readonly (string Open, string Close)[] Shapes = [("((", "))"), ("))", "(("), ("(-", "-)"), ("{{", "}}"), ("[", "]"), ("(", ")")];

    public static MermaidDrawn? Render(string src)
    {
        var sourceLines = MermaidLabels.SrcLines(src);
        var lines = sourceLines.Skip(MermaidStatements.FrontmatterEnd(sourceLines)).ToList();
        var headerAt = lines.FindIndex(l => MermaidJs.Trim(l) != "");
        if (headerAt < 0 || MermaidJs.Trim(lines[headerAt]).ToLowerInvariant() != "mindmap") return null;
        var roots = new List<MindNode>();
        var warnings = new List<string>();
        var stack = new List<(int Indent, MindNode Node)>();
        var count = 0;
        foreach (var raw in lines.Skip(headerAt + 1))
        {
            var comment = raw.IndexOf("%%", StringComparison.Ordinal);
            var noComment = comment < 0 ? raw : raw[..comment];
            if (MermaidJs.Trim(noComment) == "") continue;
            var indent = noComment.Length - MermaidJs.TrimStart(noComment).Length;
            var body = MermaidJs.Trim(noComment);
            if (body.StartsWith("::icon", StringComparison.Ordinal) || body.StartsWith(":::", StringComparison.Ordinal)) continue;
            var text = NodeText(body);
            if (text == "") { warnings.Add($"dropped, unreadable statement: \"{body}\""); continue; }
            if (count >= MermaidGraph.MaxNodes) { warnings.Add($"diagram truncated: node cap ({MermaidGraph.MaxNodes}) reached"); break; }
            count++;
            var node = new MindNode(MermaidLabels.FitLabel(text, MermaidLabels.WrapWidth));
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) roots.Add(node);
            else stack[^1].Node.Children.Add(node);
            stack.Add((indent, node));
        }
        if (roots.Count == 0) return null;
        var rows = new List<(string Prefix, string Text)>();
        void Walk(MindNode node, string prefix, string childPrefix)
        {
            rows.Add((prefix, node.Text));
            for (var i = 0; i < node.Children.Count; i++)
            {
                var last = i == node.Children.Count - 1;
                Walk(node.Children[i], childPrefix + (last ? "└── " : "├── "), childPrefix + (last ? "    " : "│   "));
            }
        }
        foreach (var root in roots) Walk(root, "", "");
        var width = rows.Max(r => MermaidWidth.StringWidth(r.Prefix) + MermaidWidth.StringWidth(r.Text));
        var canvas = new MermaidCanvas(width, rows.Count);
        for (var y = 0; y < rows.Count; y++)
        {
            MermaidCanvas.DrawText(canvas, rows[y].Prefix, 0, y, MermaidCls.Edge);
            MermaidCanvas.DrawText(canvas, rows[y].Text, MermaidWidth.StringWidth(rows[y].Prefix), y, MermaidCls.Text);
        }
        return new(canvas, warnings, new());
    }

    private static string NodeText(string body)
    {
        foreach (var (open, close) in Shapes)
        {
            var at = body.IndexOf(open, StringComparison.Ordinal);
            if (at >= 0 && body.EndsWith(close, StringComparison.Ordinal) && body.Length > at + open.Length + close.Length - 1)
                return MermaidLabels.CleanLabel(body.Substring(at + open.Length, body.Length - close.Length - at - open.Length));
        }
        return MermaidLabels.CleanLabel(body);
    }
}
