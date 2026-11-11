// lovely-mermaid 0.3.3 (Apache-2.0): src/diagrams/state.ts.
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidJs;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidLabels;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidStatements;

namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal static class MermaidState
{
    private sealed class Scope(int basis)
    {
        public int Base { get; } = basis;
        public int Region { get; set; } = -1;
    }

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
        if (HeaderKind(statements) is not ("statediagram" or "statediagram-v2")) return null;
        var graph = new MermaidGraph();
        var inNote = false;
        var assignments = new List<(List<string> Ids, List<string> Names)>();
        var stack = new List<Scope>();
        int NewGroup(string id, string label)
        {
            if (graph.Groups.Count >= MermaidGraph.MaxGroups || stack.Count >= MermaidGraph.MaxGroupDepth)
            {
                graph.Truncated ??= $"subgraph cap ({MermaidGraph.MaxGroups} groups, depth {MermaidGraph.MaxGroupDepth}) reached";
                return -1;
            }
            graph.Groups.Add(new(id, label, graph.CurGroup));
            return graph.Groups.Count - 1;
        }
        int ScopeOf() => stack.Count == 0 ? -1 : stack[^1].Region != -1 ? stack[^1].Region : stack[^1].Base;
        foreach (var st in statements.Skip(1))
        {
            if (inNote)
            {
                if (AsciiLower(st) == "end note") inNote = false;
                continue;
            }
            var first = AsciiLower(FirstWord(st));
            if (first == "direction")
            {
                var words = MermaidStatements.Words(st);
                graph.Dir = MermaidGraph.ParseDir(words.Count > 1 ? words[1] : "");
            }
            else if (first == "note")
            {
                if (!st.Contains(':')) inNote = true;
            }
            else if (first == "state")
            {
                var rest = Trim(st[FirstWord(st).Length..]);
                var open = rest.EndsWith('{');
                var body = open ? Trim(rest[..^1]) : rest;
                if (!open)
                {
                    if (!ParseStateDecl(body, graph)) graph.Drop(st);
                }
                else
                {
                    var named = CompositeName(body);
                    if (named is null) graph.Drop(st);
                    var gi = NewGroup(named?.Id ?? $"anon {graph.Groups.Count}", named?.Label ?? "");
                    if (gi != -1) { stack.Add(new(gi)); graph.CurGroup = gi; }
                }
            }
            else if (first == "}")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                graph.CurGroup = ScopeOf();
            }
            else if (first == "--")
            {
                if (stack.Count > 0)
                {
                    var top = stack[^1];
                    if (top.Region == -1)
                    {
                        var r1 = NewGroup($"region {graph.Groups.Count}", "");
                        if (r1 != -1)
                        {
                            for (var i = 0; i < graph.NodeGroup.Count; i++)
                                if (graph.NodeGroup[i] == top.Base) graph.NodeGroup[i] = r1;
                            for (var i = 0; i < graph.Groups.Count; i++)
                                if (i != r1 && graph.Groups[i].Parent == top.Base)
                                    graph.Groups[i] = graph.Groups[i] with { Parent = r1 };
                        }
                    }
                    graph.CurGroup = top.Base;
                    top.Region = NewGroup($"region {graph.Groups.Count}", "");
                    graph.CurGroup = top.Region == -1 ? top.Base : top.Region;
                }
            }
            else if (first == "classdef")
            {
                if (ParseClassDef(st[FirstWord(st).Length..]) is {} def)
                    foreach (var name in def.Names) graph.ClassDefs[name] = def.Props;
            }
            else if (first == "class")
            {
                if (ParseClassAssign(st[FirstWord(st).Length..]) is {} assign) assignments.Add(assign);
            }
            else if (first is "hide" or "scale") { }
            else if (st.Contains("-->", StringComparison.Ordinal))
            {
                if (!ParseTransition(st, graph)) graph.Drop(st);
            }
            else if (!ParseStateDesc(st, graph)) graph.Drop(st);
            if (graph.Truncated is not null)
            {
                graph.Warnings.Add($"diagram truncated: {graph.Truncated}");
                break;
            }
        }
        graph.ApplyClasses(assignments);
        return graph.Nodes.Count == 0 ? null : graph;
    }

    private static (string Id, string Label)? CompositeName(string body)
    {
        if (body.StartsWith('"'))
        {
            var close = body.IndexOf('"', 1);
            if (close == -1) return null;
            var label = DecodeHtmlEntities(body[1..close]);
            var after = Trim(body[(close + 1)..]);
            var id = TakeTags(after.StartsWith("as", StringComparison.Ordinal) ? Trim(after[2..]) : label).Id;
            return id == "" ? null : (id, label);
        }
        var stereo = body.IndexOf("<<", StringComparison.Ordinal);
        var bareId = TakeTags(Trim(stereo == -1 ? body : body[..stereo])).Id;
        return bareId == "" || HasSpace(bareId) ? null : (bareId, bareId);
    }

    private static bool ParseStateDecl(string rest, MermaidGraph graph)
    {
        if (rest == "") return true;
        if (rest.StartsWith('"'))
        {
            var close = rest.IndexOf('"', 1);
            if (close == -1) return false;
            var label = rest[1..close];
            var after = Trim(rest[(close + 1)..]);
            var tok = TakeTags(after.StartsWith("as", StringComparison.Ordinal) ? Trim(after[2..]) : label);
            var idx = graph.NodeLabel(tok.Id, DecodeHtmlEntities(label));
            if (idx == -1) return false;
            foreach (var cls in tok.Classes) graph.AddClass(idx, cls);
            return true;
        }
        var shape = MermaidShape.Round;
        var token = rest;
        var pos = rest.IndexOf("<<", StringComparison.Ordinal);
        if (pos != -1)
        {
            var stereo = rest[(pos + 2)..];
            if (stereo.EndsWith(">>", StringComparison.Ordinal)) stereo = stereo[..^2];
            if (Trim(stereo) == "choice") shape = MermaidShape.Diamond;
            token = Trim(rest[..pos]);
        }
        var tags = TakeTags(token);
        if (tags.Id == "" || HasSpace(tags.Id)) return false;
        var node = graph.NodeIndex(tags.Id, pos != -1 ? tags.Id : null, shape);
        if (node == -1) return false;
        foreach (var cls in tags.Classes) graph.AddClass(node, cls);
        return true;
    }

    private static bool ParseTransition(string st, MermaidGraph graph)
    {
        var rest = st;
        var prev = -1;
        while (SplitOnce(rest, "-->") is {} split)
        {
            var fromTok = TakeTags(Trim(TrimEnd(split.Item1).TrimEnd('-')));
            int from;
            if (prev != -1)
            {
                if (fromTok.Id != "") return false;
                from = prev;
            }
            else
            {
                if (fromTok.Id == "") return false;
                from = StateEndpoint(graph, fromTok.Id, true);
                if (from == -1) return false;
                foreach (var cls in fromTok.Classes) graph.AddClass(from, cls);
            }
            var rhs = split.Item2;
            var nextArrow = rhs.IndexOf("-->", StringComparison.Ordinal);
            var colon = SplitColon(rhs);
            var labelFirst = colon is {} col && (nextArrow == -1 || col.Item1.Length < nextArrow);
            var toPart = labelFirst ? colon!.Value.Item1 : nextArrow == -1 ? rhs : rhs[..nextArrow];
            var label = labelFirst ? NonEmpty(DecodeHtmlEntities(Trim(colon!.Value.Item2))) : null;
            var tail = labelFirst || nextArrow == -1 ? "" : rhs[nextArrow..];
            var toTok = TakeTags(Trim(TrimEnd(TrimStart(toPart).TrimStart('>')).TrimEnd('-')));
            if (toTok.Id == "") return false;
            var to = StateEndpoint(graph, toTok.Id, false);
            if (to == -1) return false;
            foreach (var cls in toTok.Classes) graph.AddClass(to, cls);
            if (!graph.PushEdge(new(from, to, label, MermaidHead.Arrow, MermaidHead.None, MermaidLineKind.Solid))) return true;
            prev = to;
            rest = tail;
        }
        return true;
    }

    private static int StateEndpoint(MermaidGraph graph, string id, bool isSource)
    {
        if (id != "[*]") return graph.NodeIndex(id, null, MermaidShape.Round);
        var scope = graph.CurGroup == -1 ? "" : $" g{graph.CurGroup}";
        return graph.NodeIndex($"[*]{(isSource ? "start" : "end")}{scope}", "●", MermaidShape.Round);
    }

    private static bool ParseStateDesc(string st, MermaidGraph graph)
    {
        var split = SplitColon(st);
        var tags = TakeTags(Trim(split is {} col ? col.Item1 : st));
        int idx;
        if (split is {} desc)
        {
            var text = Trim(desc.Item2);
            if (tags.Id == "" || HasSpace(tags.Id) || text == "") return false;
            var before = graph.Index.TryGetValue(tags.Id, out var previous) ? graph.Nodes[previous].Label : null;
            text = DecodeHtmlEntities(text);
            idx = graph.NodeLabel(tags.Id, before is not null && before != tags.Id ? $"{before} {text}" : text);
        }
        else
        {
            if (tags.Id == "" || HasSpace(tags.Id)) return false;
            idx = graph.NodeIndex(tags.Id, null, MermaidShape.Round);
        }
        if (idx == -1) return false;
        foreach (var cls in tags.Classes) graph.AddClass(idx, cls);
        return true;
    }
}
