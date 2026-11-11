// Port of lovely-mermaid 0.3.3 (Apache-2.0).
using System.Text.RegularExpressions;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidJs;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidLabels;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidStatements;

namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal static class MermaidClass
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
        if (HeaderKind(statements) is not ("classdiagram" or "classdiagram-v2")) return null;
        var graph = new MermaidGraph();
        int Declare(string token)
        {
            var (id, classes) = TakeTags(token);
            var idx = graph.NodeIndex(id, null, MermaidShape.Rect);
            if (idx != -1)
            {
                graph.Nodes[idx].Sections ??= [[DisplayGenerics(id)], [], []];
                foreach (var cls in classes) graph.AddClass(idx, cls);
            }
            return idx;
        }
        // -1: no body, -2: swallow the body of an unreadable declaration.
        var curClass = -1;
        var assignments = new List<(List<string>, List<string>)>();
        var hrefs = new List<(string, string)>();
        foreach (var st in statements.Skip(1))
        {
            if (curClass != -1)
            {
                if (st == "}") curClass = -1;
                else if (curClass != -2) PushMember(graph.Nodes[curClass], st);
                continue;
            }
            var first = AsciiLower(FirstWord(st));
            if (first == "direction")
            {
                var words = MermaidStatements.Words(st);
                graph.Dir = MermaidGraph.ParseDir(words.Count > 1 ? words[1] : "");
                continue;
            }
            if (first == "classdef")
            {
                if (ParseClassDef(st[FirstWord(st).Length..]) is { } def)
                    foreach (var name in def.Names) graph.ClassDefs[name] = def.Props;
                continue;
            }
            if (first is "note" or "callback" or "style" or "namespace" or "}") continue;
            if (first is "link" or "click")
            {
                if (ParseHref(st[FirstWord(st).Length..]) is { } target) hrefs.Add(target);
                continue;
            }
            if (first == "cssclass")
            {
                if (ParseClassAssign(st[FirstWord(st).Length..].Replace("\"", "", StringComparison.Ordinal)) is { } assign) assignments.Add(assign);
                else graph.Drop(st);
                continue;
            }
            if (first == "class")
            {
                var rest = Trim(st["class".Length..]);
                var open = rest.EndsWith('{');
                var name = open ? Trim(rest[..^1]) : rest;
                string? label = null;
                const string nonSpace = @"[^\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
                var labeled = Regex.Match(name, "^(" + nonSpace + @"+?)\[([^\r\n\u2028\u2029]+)\](:::" + nonSpace + "+)?$");
                if (labeled.Success)
                {
                    name = labeled.Groups[1].Value + labeled.Groups[3].Value;
                    label = NonEmpty(CleanLabel(labeled.Groups[2].Value));
                }
                if (!open && HasSpace(name))
                {
                    if (ParseClassAssign(name) is { } assign) assignments.Add(assign);
                    else graph.Drop(st);
                }
                else if (name == "" || HasSpace(name))
                {
                    graph.Drop(st);
                    if (open) curClass = -2;
                }
                else
                {
                    var idx = Declare(name);
                    if (idx != -1 && label is not null)
                    {
                        var node = graph.Nodes[idx];
                        node.Label = label;
                        var title = node.Sections?[0];
                        if (title is not null) title[^1] = label;
                    }
                    if (open) curClass = idx == -1 ? -2 : idx;
                }
            }
            else if (st.StartsWith("<<", StringComparison.Ordinal))
            {
                var split = SplitOnce(st[2..], ">>");
                var name = split is { } pair ? Trim(pair.Item2) : "";
                if (split is null || name == "" || HasSpace(name)) graph.Drop(st);
                else
                {
                    var idx = Declare(name);
                    if (idx != -1) SetAnnotation(graph.Nodes[idx], Trim(split.Value.Item1));
                }
            }
            else if (ParseClassRelation(st) is { } rel)
            {
                var f = Declare(rel.From);
                var t = f == -1 ? -1 : Declare(rel.To);
                if (f != -1 && t != -1)
                    graph.PushEdge(new(f, t, rel.Label, rel.HeadTo, rel.HeadFrom, rel.Line, rel.CardFrom, rel.CardTo));
            }
            else
            {
                var member = SplitColon(st);
                var id = member is { } pair ? Trim(pair.Item1) : "";
                var text = member is { } value ? Trim(value.Item2) : "";
                if (member is null || id == "" || HasSpace(id) || text == "") graph.Drop(st);
                else
                {
                    var idx = Declare(id);
                    if (idx != -1) PushMember(graph.Nodes[idx], text);
                }
            }
            if (graph.Truncated is not null)
            {
                graph.Warnings.Add($"diagram truncated: {graph.Truncated}");
                break;
            }
        }
        graph.ApplyClasses(assignments);
        graph.ApplyHrefs(hrefs);
        return graph.Nodes.Count == 0 ? null : graph;
    }

    private static void SetAnnotation(MermaidNode node, string annotation)
        => node.Sections![0] = [$"«{annotation}»", DisplayGenerics(node.Label)];

    public static void PushMember(MermaidNode node, string raw)
    {
        if (raw.StartsWith("<<", StringComparison.Ordinal))
        {
            if (SplitOnce(raw[2..], ">>") is { } split) SetAnnotation(node, Trim(split.Item1));
            return;
        }
        var member = DecodeHtmlEntities(DisplayGenerics(Trim(raw)));
        var list = node.Sections![member.Contains('(') ? 2 : 1];
        if (list.Count < MermaidGraph.MaxMembers) list.Add(member);
        else if (list.Count == MermaidGraph.MaxMembers) list.Add("…");
    }

    /// <summary>Relation operators, longest-first so <c>--|&gt;</c> wins over <c>--</c>.</summary>
    private static readonly (string Op, MermaidHead HeadFrom, MermaidHead HeadTo, MermaidLineKind Line)[] ClassOps =
    [
        ("<|--", MermaidHead.Triangle, MermaidHead.None, MermaidLineKind.Solid),
        ("--|>", MermaidHead.None, MermaidHead.Triangle, MermaidLineKind.Solid),
        ("<|..", MermaidHead.Triangle, MermaidHead.None, MermaidLineKind.Dotted),
        ("..|>", MermaidHead.None, MermaidHead.Triangle, MermaidLineKind.Dotted),
        ("*--", MermaidHead.DiamondFill, MermaidHead.None, MermaidLineKind.Solid),
        ("--*", MermaidHead.None, MermaidHead.DiamondFill, MermaidLineKind.Solid),
        ("o--", MermaidHead.DiamondOpen, MermaidHead.None, MermaidLineKind.Solid),
        ("--o", MermaidHead.None, MermaidHead.DiamondOpen, MermaidLineKind.Solid),
        ("<--", MermaidHead.Arrow, MermaidHead.None, MermaidLineKind.Solid),
        ("-->", MermaidHead.None, MermaidHead.Arrow, MermaidLineKind.Solid),
        ("<..", MermaidHead.Arrow, MermaidHead.None, MermaidLineKind.Dotted),
        ("..>", MermaidHead.None, MermaidHead.Arrow, MermaidLineKind.Dotted),
        ("--", MermaidHead.None, MermaidHead.None, MermaidLineKind.Solid),
        ("..", MermaidHead.None, MermaidHead.None, MermaidLineKind.Dotted),
    ];

    private const int MaxClassOp = 4;

    private sealed record ClassRelation(string From, string To, MermaidHead HeadFrom, MermaidHead HeadTo, MermaidLineKind Line, string? Label, string? CardFrom, string? CardTo);

    private static ClassRelation? ParseClassRelation(string st)
    {
        var chars = CodePoints(st);
        var quoted = QuoteMask(chars);
        (int Pos, string Op, MermaidHead HeadFrom, MermaidHead HeadTo, MermaidLineKind Line)? found = null;

        for (var pos = 0; pos < chars.Length && found is null; pos++)
        {
            if (quoted[pos] != 0) continue;
            var tail = Join(chars, pos, pos + MaxClassOp);
            foreach (var (op, headFrom, headTo, line) in ClassOps)
            {
                if (!tail.StartsWith(op, StringComparison.Ordinal)) continue;
                // `o` is also an identifier character: skip a match glued to a name.
                if (op.StartsWith('o') && pos > 0 && IsIdChar(chars[pos - 1])) continue;
                var after = At(chars, pos + op.Length);
                if (op.EndsWith('o') && after is not null && IsIdChar(after)) continue;
                found = (pos, op, headFrom, headTo, line);
                break;
            }
        }
        if (found is not { } hit) return null;

        var lhsRaw = Trim(Join(chars, 0, hit.Pos));
        var rhsRaw = Trim(Join(chars, hit.Pos + hit.Op.Length));

        var (lhs, cardFrom) = StripCardinalitySuffix(lhsRaw);
        var (rhs, cardTo) = StripCardinalityPrefix(rhsRaw);

        var split = SplitColon(rhs);
        var toId = Trim(split is { } s1 ? s1.Item1 : rhs);
        var relLabel = split is { } s2 ? NonEmpty(DecodeHtmlEntities(Trim(s2.Item2))) : null;

        if (lhs == "" || toId == "" || HasSpace(lhs) || HasSpace(toId)) return null;

        return new(lhs, toId, hit.HeadFrom, hit.HeadTo, hit.Line, relLabel, NonEmpty(cardFrom), NonEmpty(cardTo));
    }

    /// <summary><c>Class "1"</c> - a quoted cardinality trailing the left-hand name.</summary>
    private static (string, string) StripCardinalitySuffix(string s)
    {
        var t = TrimEnd(s);
        if (t.EndsWith('"'))
        {
            var rest = t[..^1];
            var q = rest.LastIndexOf('"');
            if (q != -1) return (TrimEnd(rest[..q]), rest[(q + 1)..]);
        }
        return (t, "");
    }

    /// <summary><c>"0..*" Class</c> - a quoted cardinality leading the right-hand name.</summary>
    private static (string, string) StripCardinalityPrefix(string s)
    {
        var t = TrimStart(s);
        if (t.StartsWith('"'))
        {
            var rest = t[1..];
            var q = rest.IndexOf('"');
            if (q != -1) return (TrimStart(rest[(q + 1)..]), rest[..q]);
        }
        return (t, "");
    }

}
