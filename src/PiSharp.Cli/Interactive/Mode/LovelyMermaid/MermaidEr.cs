// Port of lovely-mermaid 0.3.3 (Apache-2.0).
using System.Text;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidJs;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidLabels;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidStatements;

namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal static class MermaidEr
{
    public static MermaidDrawn? Render(string src)
    {
        var graph = Parse(src);
        if (graph is null) return null;
        var canvas = MermaidLayout.LayoutClass(graph);
        return canvas is null ? null : new(canvas, graph.Warnings, graph.ClassDefs);
    }

    public static MermaidGraph? Parse(string src)
    {
        var statements = StatementsOf(src);
        if (HeaderKind(statements) != "erdiagram") return null;
        var graph = new MermaidGraph();
        var curEntity = -1;
        foreach (var st in statements.Skip(1))
        {
            if (curEntity != -1)
            {
                if (st == "}") curEntity = -1;
                else if (curEntity != -2) PushErAttribute(graph.Nodes[curEntity], st);
                continue;
            }
            if (SplitErRelationship(st) is { } rel)
            {
                var tokens = ErTokens(rel.Rel);
                var op = tokens.Count == 3 ? ParseErOp(tokens[1]) : null;
                var f = op is null ? -1 : ErEntity(graph, tokens[0]);
                var t = f == -1 ? -1 : ErEntity(graph, tokens[2]);
                if (op is null || f == -1 || t == -1) graph.Drop(st);
                else
                {
                    var label = rel.Label is null ? "" : CleanLabel(rel.Label);
                    graph.PushEdge(new(f, t, NonEmpty(label), MermaidHead.None, MermaidHead.None, op.Value.Line, op.Value.CardL, op.Value.CardR));
                }
            }
            else
            {
                var open = st.EndsWith('{');
                var decl = open ? Trim(st[..^1]) : st;
                if (decl == "" || ErTokens(decl).Count != 1)
                {
                    graph.Drop(st);
                    if (open) curEntity = -2;
                }
                else
                {
                    var idx = ErEntity(graph, decl);
                    if (open) curEntity = idx == -1 ? -2 : idx;
                }
            }
            if (graph.Truncated is not null)
            {
                graph.Warnings.Add($"diagram truncated: {graph.Truncated}");
                break;
            }
        }
        return graph.Nodes.Count == 0 ? null : graph;
    }

    private static int ErEntity(MermaidGraph graph, string token)
    {
        var open = token.StartsWith('"') ? -1 : token.IndexOf('[');
        int idx;
        if (open != -1)
        {
            var id = token[..open];
            if (!token.EndsWith(']')) graph.Warnings.Add($"entity \"{id}\": alias is missing its closing `]`");
            var label = CleanLabel(token[(open + 1)..].TrimEnd(']'));
            if (id == "" || label == "") return -1;
            idx = graph.NodeLabel(id, label);
        }
        else if (token.StartsWith('"'))
        {
            var label = CleanLabel(token);
            if (label == "") return -1;
            idx = graph.NodeIndex(token, label, MermaidShape.Rect);
        }
        else idx = graph.NodeIndex(token, null, MermaidShape.Rect);
        if (idx == -1) return -1;
        var node = graph.Nodes[idx];
        node.Sections ??= [[], []];
        node.Sections[0] = [DisplayGenerics(node.Label)];
        return idx;
    }

    private static List<string> ErTokens(string s)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        foreach (var c in s)
        {
            if (c == '"') { inQuotes = !inQuotes; cur.Append(c); }
            else if (!inQuotes && IsSpace(c))
            {
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
            }
            else cur.Append(c);
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }

    private static (string Rel, string? Label)? SplitErRelationship(string st)
    {
        var split = SplitOnce(st, ":");
        var rel = split is { } pair ? pair.Item1 : st;
        var label = split is { } value ? Trim(value.Item2) : null;
        return ErTokens(rel).Any(t => ParseErOp(t) is not null) ? (rel, label) : null;
    }

    private static bool IsAscii(string s)
    {
        foreach (var c in s) if (c > 0x7f) return false;
        return true;
    }

    /// <summary>A crow's-foot operator: two cardinality glyphs around <c>--</c> or <c>..</c>.</summary>
    private static (string CardL, string CardR, MermaidLineKind Line)? ParseErOp(string tok)
    {
        if (tok.Length != 6 || !IsAscii(tok)) return null;
        var mid = tok[2..4];
        MermaidLineKind? line = mid == "--" ? MermaidLineKind.Solid : mid == ".." ? MermaidLineKind.Dotted : null;
        if (line is null) return null;
        var cardL = ErCard(tok[..2]);
        var cardR = ErCard(tok[4..6]);
        return cardL is null || cardR is null ? null : (cardL, cardR, line.Value);
    }

    private static string? ErCard(string tok) => tok switch
    {
        "|o" or "o|" => "0..1",
        "||" => "1",
        "}o" or "o{" => "*",
        "}|" or "|{" => "1..*",
        _ => null,
    };

    /// <summary>ER attributes are <c>type name</c>; a trailing quoted comment is dropped.</summary>
    public static void PushErAttribute(MermaidNode node, string raw)
    {
        var attrs = node.Sections![1];
        var parts = new List<string>();
        foreach (var tok in MermaidStatements.Words(raw))
        {
            if (tok.StartsWith('"')) break;
            parts.Add(tok);
        }
        if (parts.Count == 0) return;
        var line = DecodeHtmlEntities(string.Join(" ", parts));
        if (attrs.Count < MermaidGraph.MaxMembers) attrs.Add(line);
        else if (attrs.Count == MermaidGraph.MaxMembers) attrs.Add("…");
    }

}
