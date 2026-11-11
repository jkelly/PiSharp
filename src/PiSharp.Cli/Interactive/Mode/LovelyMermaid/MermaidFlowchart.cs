// Port of lovely-mermaid 0.3.3 (Apache-2.0).
using System.Text;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidJs;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidLabels;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidStatements;

namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal static class MermaidFlowchart
{
    public static MermaidDrawn? Render(string src)
    {
        var graph = Parse(src);
        if (graph is null) return null;
        var canvas = graph.Groups.Count == 0 ? MermaidLayout.LayoutFlowchart(graph) : MermaidLayout.LayoutGrouped(graph);
        return canvas is null ? null : new(canvas, graph.Warnings, graph.ClassDefs);
    }

    public static MermaidGraph? Parse(string src)
    {
        var statements = StatementsOf(src);
        if (HeaderKind(statements) is not ("graph" or "flowchart")) return null;
        var header = MermaidStatements.Words(statements[0]);
        var graph = new MermaidGraph(MermaidGraph.ParseDir(header.Count > 1 ? header[1] : "TB"));
        var stack = new List<int>();
        var assignments = new List<(List<string>, List<string>)>();
        var hrefs = new List<(string, string)>();
        foreach (var st in statements.Skip(1))
        {
            var first = FirstWord(st);
            switch (AsciiLower(first))
            {
                case "subgraph":
                    if (graph.Groups.Count >= MermaidGraph.MaxGroups || stack.Count >= MermaidGraph.MaxGroupDepth)
                    {
                        graph.Truncated ??= $"subgraph cap ({MermaidGraph.MaxGroups} groups, depth {MermaidGraph.MaxGroupDepth}) reached";
                        break;
                    }
                    var (id, label) = ParseSubgraphDecl(Trim(st["subgraph".Length..]));
                    graph.Groups.Add(new(id, label, stack.Count > 0 ? stack[^1] : -1));
                    stack.Add(graph.Groups.Count - 1);
                    graph.CurGroup = stack[^1];
                    continue;
                case "end":
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    graph.CurGroup = stack.Count > 0 ? stack[^1] : -1;
                    continue;
                case "classdef":
                    if (ParseClassDef(st[first.Length..]) is { } def)
                        foreach (var name in def.Names) graph.ClassDefs[name] = def.Props;
                    continue;
                case "class":
                    if (ParseClassAssign(st[first.Length..]) is { } assign) assignments.Add(assign);
                    continue;
                case "click":
                    if (ParseHref(st[first.Length..]) is { } href) hrefs.Add(href);
                    continue;
                case "style": case "linkstyle": case "direction": continue;
            }
            if (graph.Truncated is null) ParseStatement(st, graph);
            if (graph.Truncated is not null) break;
        }
        graph.ApplyClasses(assignments);
        graph.ApplyHrefs(hrefs);
        if (graph.Truncated is not null) graph.Warnings.Add($"diagram truncated: {graph.Truncated}");
        return graph.Nodes.Count == 0 ? null : graph;
    }

    private static Shaped ReadAtShape(string[] chars, int start)
    {
        var i = start;
        var depth = 0;
        var inQuotes = false;
        for (; i < chars.Length; i++)
        {
            var c = chars[i];
            if (inQuotes) { if (c == "\"") inQuotes = false; }
            else if (c == "\"") inQuotes = true;
            else if (c == "{") depth++;
            else if (c == "}") { if (depth == 0) break; depth--; }
        }
        var body = Join(chars, start, i);
        var closed = At(chars, i) == "}";
        var shape = MermaidShape.Rect;
        string? label = null;
        foreach (var pair in SplitTop(body, c => c == ","))
        {
            if (SplitOnce(pair, ":") is not { } kv) continue;
            var key = AsciiLower(Trim(kv.Item1));
            if (key == "shape") shape = AtShape(AsciiLower(Trim(kv.Item2)));
            else if (key == "label") label = NonEmpty(CleanLabel(kv.Item2));
        }
        return new(shape, label, closed ? i + 1 : chars.Length, closed ? null : "}");
    }

    private static MermaidShape AtShape(string name) => name switch
    {
        "rounded" or "stadium" or "pill" or "terminal" or "cyl" or "cylinder" or "database" or "db" or
        "circle" or "circ" or "sm-circ" or "small-circle" or "dbl-circ" or "double-circle" or "fr-circ" or
        "framed-circle" or "start" or "stop" or "event" or "delay" or "cloud" or "bang" => MermaidShape.Round,
        "diam" or "diamond" or "decision" or "question" or "hex" or "hexagon" or "prepare" => MermaidShape.Diamond,
        _ => MermaidShape.Rect,
    };

    /// <summary><c>subgraph id[Title]</c>, <c>subgraph "Title"</c>, or a bare title.</summary>
    private static (string Id, string Label) ParseSubgraphDecl(string rest)
    {
        if (rest.StartsWith('"'))
        {
            var close = rest.IndexOf('"', 1);
            if (close != -1)
            {
                var label = rest[1..close];
                return (label, DecodeHtmlEntities(label));
            }
        }
        var open = rest.IndexOf('[');
        if (open != -1)
        {
            var id = Trim(rest[..open]);
            var label = CleanLabel(Trim(rest[(open + 1)..].TrimEnd(']')));
            if (id != "" && label != "") return (id, label);
        }
        return (rest, rest);
    }

    /// <summary>A chain of <c>node link node link node ...</c>, each link fanning out over <c>&amp;</c>.</summary>
    private static void ParseStatement(string st, MermaidGraph graph)
    {
        var chars = CodePoints(st);
        var i = 0;

        var head = ParseNodeGroup(chars, i, graph);
        if (head is null)
        {
            if (graph.Truncated is null) graph.Warnings.Add($"dropped, does not start with a node: \"{st}\"");
            return;
        }
        var prev = head.Value.Group;
        i = head.Value.Next;

        for (; ; )
        {
            i = SkipSpaces(chars, i);
            if (i >= chars.Length) break;
            var link = ParseLink(chars, i);
            if (link is null)
            {
                if (graph.Truncated is null) graph.Warnings.Add($"dropped, expected a link: \"{Join(chars, i)}\"");
                break;
            }
            i = SkipSpaces(chars, link.Next);
            var target = ParseNodeGroup(chars, i, graph);
            if (target is null)
            {
                if (graph.Truncated is null) graph.Warnings.Add($"dropped, link has no target: \"{st}\"");
                break;
            }
            i = target.Value.Next;
            foreach (var f in prev)
            {
                foreach (var t in target.Value.Group)
                {
                    // `A <-- B` reads right-to-left: swap the endpoints so the left arrow becomes a normal forward head.
                    var reversed = link.Left == MermaidHead.Arrow && link.Right != MermaidHead.Arrow;
                    var pushed = graph.PushEdge(new(
                        reversed ? t : f,
                        reversed ? f : t,
                        link.Label,
                        reversed ? MermaidHead.Arrow : link.Right,
                        reversed ? link.Right : link.Left,
                        link.Line));
                    if (!pushed) return;
                }
            }
            prev = target.Value.Group;
        }
    }

    /// <summary>One or more nodes joined by <c>&amp;</c>, which fan out into a cross product.</summary>
    private static (List<int> Group, int Next)? ParseNodeGroup(string[] chars, int start, MermaidGraph graph)
    {
        var first = ParseNode(chars, start, graph);
        if (first is null) return null;
        var group = new List<int> { first.Value.Index };
        var i = first.Value.Next;
        for (; ; )
        {
            var j = SkipSpaces(chars, i);
            if (At(chars, j) != "&") break;
            var next = ParseNode(chars, j + 1, graph);
            if (next is null) return null;
            group.Add(next.Value.Index);
            i = next.Value.Next;
        }
        return (group, i);
    }

    private static int SkipSpaces(string[] chars, int i)
    {
        while (i < chars.Length && (chars[i] == " " || chars[i] == "\t")) i++;
        return i;
    }

    private static (int Index, int Next)? ParseNode(string[] chars, int start, MermaidGraph graph)
    {
        var i = SkipSpaces(chars, start);
        var idStart = i;
        while (i < chars.Length && (IsIdChar(chars[i]) || (chars[i] == "-" && i + 1 < chars.Length && IsIdChar(chars[i + 1])))) i++;
        if (i == idStart) return null;
        var id = Join(chars, idStart, i);

        var shaped = At(chars, i) == "@" && At(chars, i + 1) == "{" ? ReadAtShape(chars, i + 2) : ReadShapeAt(chars, i);
        if (shaped.Unclosed is not null) graph.Warnings.Add($"node \"{id}\": label is missing its closing `{shaped.Unclosed}`");
        var index = graph.NodeIndex(id, shaped.Label, shaped.Shape);
        if (index == -1) return null;

        // `id:::name` (after any shape) attaches a style class; swallow it so the statement keeps parsing.
        var next = shaped.After;
        if (At(chars, next) == ":" && At(chars, next + 1) == ":" && At(chars, next + 2) == ":")
        {
            var k = next + 3;
            while (k < chars.Length && (IsIdChar(chars[k]) || chars[k] == "-")) k++;
            // A name never ends in `-`: back off so `A:::x-->B` keeps its link.
            while (k > next + 3 && chars[k - 1] == "-") k--;
            if (k > next + 3) { graph.AddClass(index, Join(chars, next + 3, k)); next = k; }
        }
        return (index, next);
    }

    /// <summary>What a shape bracket yielded; <see cref="Unclosed"/> is set when the bracket never closed.</summary>
    private readonly record struct Shaped(MermaidShape Shape, string? Label, int After, string? Unclosed = null);

    /// <summary>Dispatch on the bracket following an id to pick shape and closing token.</summary>
    private static Shaped ReadShapeAt(string[] chars, int i)
    {
        var c = At(chars, i);
        var n = At(chars, i + 1);
        if (c == "[")
        {
            if (n == "[") return ReadShape(chars, i + 2, "]]", MermaidShape.Rect);
            if (n == "(") return ReadShape(chars, i + 2, ")]", MermaidShape.Round);
            return ReadShape(chars, i + 1, "]", MermaidShape.Rect);
        }
        if (c == "(")
        {
            if (n == "(") return ReadShape(chars, i + 2, "))", MermaidShape.Round);
            if (n == "[") return ReadShape(chars, i + 2, "])", MermaidShape.Round);
            return ReadShape(chars, i + 1, ")", MermaidShape.Round);
        }
        if (c == "{")
        {
            if (n == "{") return ReadShape(chars, i + 2, "}}", MermaidShape.Diamond);
            return ReadShape(chars, i + 1, "}", MermaidShape.Diamond);
        }
        if (c == ">") return ReadShape(chars, i + 1, "]", MermaidShape.Rect);
        return new(MermaidShape.Rect, null, i);
    }

    /// <summary>Read label text up to <paramref name="closer"/>; quoting is decided by the first non-space character.</summary>
    private static Shaped ReadShape(string[] chars, int start, string closer, MermaidShape shape)
    {
        var j = start;
        while (At(chars, j) is " " or "\t") j++;
        var quoted = At(chars, j) == "\"";

        var i = start;
        var text = new StringBuilder();
        var inQuotes = false;
        while (i < chars.Length)
        {
            var c = chars[i];
            if (quoted && c == "\"")
            {
                inQuotes = !inQuotes;
                text.Append(c);
                i++;
                continue;
            }
            if (!inQuotes && Join(chars, i, i + closer.Length) == closer) return new(shape, CleanLabel(text.ToString()), i + closer.Length);
            text.Append(c);
            i++;
        }
        // Ran off the end still looking for the closer: everything after the opening bracket became label text.
        return new(shape, CleanLabel(text.ToString()), chars.Length, closer);
    }

    private static bool IsLinkChar(string? c) => c is "-" or "." or "=" or "<" or ">";

    private sealed record Link(MermaidHead Left, MermaidHead Right, MermaidLineKind Line, string? Label, int Next);

    /// <summary>Read a link operator and its label: <c>--&gt;|text|</c> or the inline <c>-- text --&gt;</c>.</summary>
    private static Link? ParseLink(string[] chars, int start)
    {
        var i = SkipSpaces(chars, start);
        var left = MermaidHead.None;
        // A leading `o`/`x` decorates the tail, but only directly before an operator.
        if (At(chars, i) is "o" or "x" && At(chars, i + 1) is "-" or "." or "=")
        {
            left = chars[i] == "o" ? MermaidHead.Circle : MermaidHead.Cross;
            i++;
        }

        var opStart = i;
        while (i < chars.Length && IsLinkChar(chars[i])) i++;
        if (i == opStart) return null;
        var op1 = Join(chars, opStart, i);
        if (left == MermaidHead.None && op1.StartsWith('<')) left = MermaidHead.Arrow;

        var line = LineKind(op1);
        var right = op1.Contains('>') ? MermaidHead.Arrow : MermaidHead.None;
        if (right == MermaidHead.None && TrailingHead(chars, i) is { } trailing)
        {
            right = trailing.Head;
            i = trailing.Next;
        }

        if (At(chars, i) == "|")
        {
            i++;
            var lStart = i;
            var inQuotes = false;
            while (i < chars.Length && (inQuotes || chars[i] != "|"))
            {
                if (chars[i] == "\"") inQuotes = !inQuotes;
                i++;
            }
            var label = CleanLabel(Join(chars, lStart, i));
            if (At(chars, i) == "|") i++;
            return new(left, right, line, NonEmpty(label), i);
        }

        if (right == MermaidHead.None)
        {
            var textStart = SkipSpaces(chars, i);
            var j = textStart;
            while (j < chars.Length)
            {
                if (chars[j] == "\"")
                {
                    j++;
                    while (j < chars.Length && chars[j] != "\"") j++;
                    if (j < chars.Length) j++;
                }
                else if (IsLinkChar(chars[j]) && IsLinkChar(At(chars, j + 1))) break;
                else j++;
            }
            if (j < chars.Length && j > textStart && chars[j] != "<")
            {
                var text = Join(chars, textStart, j);
                var op2Start = j;
                while (j < chars.Length && IsLinkChar(chars[j])) j++;
                var op2 = Join(chars, op2Start, j);
                if (op2.Contains('>'))
                {
                    right = MermaidHead.Arrow;
                }
                else if (TrailingHead(chars, j) is { } trailing2)
                {
                    right = trailing2.Head;
                    j = trailing2.Next;
                }
                if (line == MermaidLineKind.Solid) line = LineKind(op2);
                return new(left, right, line, NonEmpty(CleanLabel(text)), j);
            }
        }

        return new(left, right, line, null, i);
    }

    private static MermaidLineKind LineKind(string op) =>
        op.Contains('=') ? MermaidLineKind.Thick : op.Contains('.') ? MermaidLineKind.Dotted : MermaidLineKind.Solid;

    /// <summary>A trailing <c>o</c>/<c>x</c> head, only when followed by a statement boundary.</summary>
    private static (MermaidHead Head, int Next)? TrailingHead(string[] chars, int i)
    {
        var c = At(chars, i);
        MermaidHead? head = c == "o" ? MermaidHead.Circle : c == "x" ? MermaidHead.Cross : null;
        if (head is null) return null;
        var boundary = At(chars, i + 1) is null or " " or "\t" or "|" or "&" or ";";
        return boundary ? (head.Value, i + 1) : null;
    }

}
