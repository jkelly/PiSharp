// grok-mermaid 0.2.3 (Apache-2.0): src/parse.ts — used by Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/mermaid.ts.
using System.Text;
using static PiSharp.Cli.Interactive.Mode.Mermaid.MermaidJs;
using static PiSharp.Cli.Interactive.Mode.Mermaid.MermaidLabels;

namespace PiSharp.Cli.Interactive.Mode.Mermaid;

internal enum MermaidSeqHead { Arrow, Cross }

internal enum MermaidNoteKind { Over, Left, Right }

/// <summary>Where a note sits; <c>left</c>/<c>right</c> keep their participant in <see cref="From"/>.</summary>
internal sealed record MermaidNoteAnchor(MermaidNoteKind Kind, int From, int To);

internal enum MermaidSeqItemKind { Message, Note, Divider }

internal sealed record MermaidSeqItem(
    MermaidSeqItemKind Kind,
    int From = 0,
    int To = 0,
    string? Text = null,
    bool Dashed = false,
    MermaidSeqHead Head = MermaidSeqHead.Arrow,
    MermaidNoteAnchor? Anchor = null);

internal sealed class MermaidSequence
{
    public List<string> Labels { get; } = [];
    public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);
    public List<MermaidSeqItem> Items { get; } = [];

    /// <summary>Index of participant <paramref name="id"/>, creating it if new; -1 past the node cap.</summary>
    public int Participant(string id, string? label)
    {
        if (Index.TryGetValue(id, out var existing))
        {
            if (label is not null) Labels[existing] = label;
            return existing;
        }
        if (Labels.Count >= MermaidGraph.MaxNodes) return -1;
        Index[id] = Labels.Count;
        Labels.Add(label ?? id);
        return Labels.Count - 1;
    }
}

/// <summary>
/// Source text to diagram model. Every <c>ParseX</c> returns <c>null</c> when the source is not that kind of diagram,
/// or when it exceeds a cap. Node, participant and edge indices use -1 where the original returns <c>null</c>.
/// </summary>
internal static class MermaidParse
{
    // ---------------------------------------------------------------- statements

    private static void FlushStatement(StringBuilder cur, List<string> statements)
    {
        var trimmed = Trim(cur.ToString());
        if (trimmed != "") statements.Add(trimmed);
        cur.Clear();
    }

    /// <summary>Split one source line into statements on <c>;</c>, stopping at a <c>%%</c> comment.</summary>
    public static void SplitStatements(string line, List<string> statements)
    {
        var chars = CodePoints(line);
        var cur = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (inQuotes)
            {
                if (c == "\"") inQuotes = false;
                cur.Append(c);
            }
            else if (c == "\"")
            {
                inQuotes = true;
                cur.Append(c);
            }
            else if (c == "%" && At(chars, i + 1) == "%")
            {
                break;
            }
            else if (c == ";")
            {
                FlushStatement(cur, statements);
            }
            else
            {
                cur.Append(c);
            }
        }
        FlushStatement(cur, statements);
    }

    /// <summary>All statements in a source block, in order.</summary>
    public static List<string> StatementsOf(string src)
    {
        var statements = new List<string>();
        foreach (var line in SrcLines(src)) SplitStatements(line, statements);
        return statements;
    }

    private static string FirstWord(string s)
    {
        var w = Words(s);
        return w.Count > 0 ? w[0] : "";
    }

    /// <summary>Split on the first occurrence of <paramref name="sep"/>, Rust's <c>split_once</c>.</summary>
    private static (string Left, string Right)? SplitOnce(string s, string sep)
    {
        var i = s.IndexOf(sep, StringComparison.Ordinal);
        return i == -1 ? null : (s[..i], s[(i + sep.Length)..]);
    }

    private static string? NonEmpty(string s) => s == "" ? null : s;

    /// <summary>Diagram kind from the header statement, lowercased.</summary>
    private static string? HeaderKind(List<string> statements)
    {
        if (statements.Count == 0) return null;
        var kind = FirstWord(statements[0]);
        return kind == "" ? null : AsciiLower(kind);
    }

    /// <summary>The kind of diagram <paramref name="src"/> declares, or <c>null</c> if its header names no type this renderer draws.</summary>
    public static string? DiagramKind(string src)
    {
        var kind = HeaderKind(StatementsOf(src));
        if (kind is null) return null;
        if (kind is "graph" or "flowchart") return "flowchart";
        if (kind.StartsWith("statediagram", StringComparison.Ordinal)) return "state";
        if (kind.StartsWith("classdiagram", StringComparison.Ordinal)) return "class";
        if (kind == "erdiagram") return "er";
        if (kind == "sequencediagram") return "sequence";
        return null;
    }

    // ----------------------------------------------------------------- flowchart

    public static MermaidGraph? ParseGraph(string src)
    {
        var statements = StatementsOf(src);
        var kind = HeaderKind(statements);
        if (kind is not ("graph" or "flowchart")) return null;

        var headerWords = Words(statements[0]);
        var graph = new MermaidGraph(MermaidGraph.ParseDir(headerWords.Count > 1 ? headerWords[1] : "TB"));
        var stack = new List<int>();

        foreach (var st in statements.Skip(1))
        {
            switch (AsciiLower(FirstWord(st)))
            {
                case "subgraph":
                    {
                        if (graph.Groups.Count >= MermaidGraph.MaxGroups || stack.Count >= MermaidGraph.MaxGroupDepth) return null;
                        var (id, label) = ParseSubgraphDecl(Trim(st["subgraph".Length..]));
                        graph.Groups.Add(new(id, label, stack.Count > 0 ? stack[^1] : -1));
                        stack.Add(graph.Groups.Count - 1);
                        graph.CurGroup = stack[^1];
                        continue;
                    }
                case "end":
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    graph.CurGroup = stack.Count > 0 ? stack[^1] : -1;
                    continue;
                case "classdef":
                case "class":
                case "style":
                case "linkstyle":
                case "click":
                case "direction":
                    continue;
            }
            ParseStatement(st, graph);
            if (graph.OverCap) return null;
        }

        return graph.Nodes.Count == 0 ? null : graph;
    }

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
            graph.Warnings.Add($"dropped, does not start with a node: \"{st}\"");
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
                graph.Warnings.Add($"dropped, expected a link: \"{Join(chars, i)}\"");
                break;
            }
            i = SkipSpaces(chars, link.Next);
            var target = ParseNodeGroup(chars, i, graph);
            if (target is null)
            {
                graph.Warnings.Add($"dropped, link has no target: \"{st}\"");
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
        while (i < chars.Length && IsIdChar(chars[i])) i++;
        if (i == idStart) return null;
        var id = Join(chars, idStart, i);

        var shaped = ReadShapeAt(chars, i);
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
            if (k > next + 3) next = k;
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
            while (i < chars.Length && chars[i] != "|") i++;
            var label = CleanLabel(Join(chars, lStart, i));
            if (At(chars, i) == "|") i++;
            return new(left, right, line, NonEmpty(label), i);
        }

        if (right == MermaidHead.None)
        {
            var textStart = SkipSpaces(chars, i);
            var j = textStart;
            while (j < chars.Length && !IsLinkChar(chars[j])) j++;
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

    // --------------------------------------------------------------------- state

    private static readonly string[] StateSkipped = ["classdef", "class", "hide", "scale", "}", "--"];

    public static MermaidGraph? ParseState(string src)
    {
        var statements = StatementsOf(src);
        var kind = HeaderKind(statements);
        if (kind is null || !kind.StartsWith("statediagram", StringComparison.Ordinal)) return null;

        var graph = new MermaidGraph();
        var inNote = false;

        foreach (var raw in statements.Skip(1))
        {
            if (inNote)
            {
                if (AsciiLower(raw) == "end note") inNote = false;
                continue;
            }
            var st = DropStyleTags(raw);
            var first = AsciiLower(FirstWord(st));
            if (first == "direction")
            {
                var w = Words(st);
                graph.Dir = MermaidGraph.ParseDir(w.Count > 1 ? w[1] : "");
            }
            else if (first == "note")
            {
                // A single-line `note ... : text` needs no terminator.
                if (!st.Contains(':')) inNote = true;
            }
            else if (first == "state")
            {
                if (!ParseStateDecl(st, graph)) return null;
            }
            else if (StateSkipped.Contains(first))
            {
                // Styling and composite-state punctuation carry no layout meaning.
            }
            else if (st.Contains("-->", StringComparison.Ordinal))
            {
                if (!ParseTransition(st, graph)) return null;
            }
            else if (!ParseStateDesc(st, graph))
            {
                return null;
            }
            if (graph.OverCap) return null;
        }

        return graph.Nodes.Count == 0 ? null : graph;
    }

    /// <summary><c>state "Label" as id</c>, <c>state id &lt;&lt;choice&gt;&gt;</c>, or <c>state id {</c>.</summary>
    private static bool ParseStateDecl(string st, MermaidGraph graph)
    {
        var decl = Trim(st["state".Length..]);
        var rest = Trim(decl.EndsWith('{') ? decl[..^1] : decl);
        if (rest == "") return true;

        if (rest.StartsWith('"'))
        {
            var close = rest.IndexOf('"', 1);
            if (close == -1) return false;
            var label = rest[1..close];
            var after = Trim(rest[(close + 1)..]);
            var id = after.StartsWith("as", StringComparison.Ordinal) ? Trim(after[2..]) : label;
            return graph.NodeLabel(id, DecodeHtmlEntities(label)) != -1;
        }

        var shape = MermaidShape.Round;
        var sid = rest;
        var stereotyped = false;
        var pos = rest.IndexOf("<<", StringComparison.Ordinal);
        if (pos != -1)
        {
            var s = rest[(pos + 2)..];
            var stereo = Trim(s.EndsWith(">>", StringComparison.Ordinal) ? s[..^2] : s);
            if (stereo == "choice") shape = MermaidShape.Diamond;
            sid = Trim(rest[..pos]);
            stereotyped = true;
        }
        if (sid == "" || HasSpace(sid)) return false;
        return graph.NodeIndex(sid, stereotyped ? sid : null, shape) != -1;
    }

    /// <summary><c>A --&gt; B: label</c>, including chains <c>A --&gt; B --&gt; C</c>.</summary>
    private static bool ParseTransition(string st, MermaidGraph graph)
    {
        var rest = st;
        var prev = -1;

        for (; ; )
        {
            if (SplitOnce(rest, "-->") is not (var lhs, var rhs)) break;

            var fromId = Trim(TrimEnd(lhs).TrimEnd('-'));
            int from;
            if (prev != -1)
            {
                // Mid-chain: the source is the previous target, so nothing may precede.
                if (fromId != "") return false;
                from = prev;
            }
            else
            {
                if (fromId == "") return false;
                from = StateEndpoint(graph, fromId, true);
                if (from == -1) return false;
            }

            var nextArrow = rhs.IndexOf("-->", StringComparison.Ordinal);
            var toPartRaw = nextArrow == -1 ? rhs : rhs[..nextArrow];
            var tail = nextArrow == -1 ? "" : rhs[nextArrow..];

            var colon = SplitOnce(toPartRaw, ":");
            var toPart = colon is { } c1 ? c1.Left : toPartRaw;
            var label = colon is { } c2 ? NonEmpty(DecodeHtmlEntities(Trim(c2.Right))) : null;

            var toId = Trim(TrimEnd(TrimStart(toPart).TrimStart('>')).TrimEnd('-'));
            if (toId == "") return false;
            var to = StateEndpoint(graph, toId, false);
            if (to == -1) return false;

            if (!graph.PushEdge(new(from, to, label, MermaidHead.Arrow, MermaidHead.None, MermaidLineKind.Solid))) return true;
            prev = to;
            rest = tail;
        }
        return true;
    }

    /// <summary>Remove every <c>:::name</c> style tag from a statement, parsed with the flowchart shorthand's name scan.</summary>
    private static string DropStyleTags(string st)
    {
        var chars = CodePoints(st);
        var sb = new StringBuilder();
        for (var i = 0; i < chars.Length;)
        {
            if (chars[i] == ":" && At(chars, i + 1) == ":" && At(chars, i + 2) == ":")
            {
                var k = i + 3;
                while (k < chars.Length && (IsIdChar(chars[k]) || chars[k] == "-")) k++;
                while (k > i + 3 && chars[k - 1] == "-") k--;
                if (k > i + 3)
                {
                    i = k;
                    continue;
                }
            }
            sb.Append(chars[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary><c>[*]</c> is start or end depending on which side of the arrow it sits.</summary>
    private static int StateEndpoint(MermaidGraph graph, string id, bool isSource) =>
        id == "[*]" ? graph.NodeIndex(isSource ? "[*]start" : "[*]end", "●", MermaidShape.Round) : graph.NodeIndex(id, null, MermaidShape.Round);

    /// <summary><c>id: description</c>, or a bare state name.</summary>
    private static bool ParseStateDesc(string st, MermaidGraph graph)
    {
        if (SplitOnce(st, ":") is (var left, var right))
        {
            var id = Trim(left);
            var desc = Trim(right);
            if (id == "" || HasSpace(id) || desc == "") return false;
            return graph.NodeLabel(id, DecodeHtmlEntities(desc)) != -1;
        }
        if (HasSpace(st)) return false;
        return graph.NodeIndex(st, null, MermaidShape.Round) != -1;
    }

    // --------------------------------------------------------------------- class

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

    private static readonly string[] ClassSkipped = ["note", "callback", "click", "link", "style", "cssclass", "classdef", "namespace", "}"];

    public static (MermaidGraph Graph, List<MermaidClassInfo> Infos)? ParseClass(string src)
    {
        var statements = StatementsOf(src);
        var kind = HeaderKind(statements);
        if (kind is null || !kind.StartsWith("classdiagram", StringComparison.Ordinal)) return null;

        var graph = new MermaidGraph();
        var infos = new List<MermaidClassInfo>();
        void Sync()
        {
            while (infos.Count < graph.Nodes.Count) infos.Add(new());
        }
        // Declare a class, keeping `infos` aligned with `graph.nodes`.
        int Declare(string name)
        {
            var idx = graph.NodeIndex(name, null, MermaidShape.Rect);
            Sync();
            return idx;
        }
        var curClass = -1;

        foreach (var raw in statements.Skip(1))
        {
            if (curClass != -1)
            {
                if (raw == "}") curClass = -1;
                else PushMember(infos[curClass], raw);
                continue;
            }
            var st = DropStyleTags(raw);

            var first = AsciiLower(FirstWord(st));
            if (first == "direction")
            {
                var w = Words(st);
                graph.Dir = MermaidGraph.ParseDir(w.Count > 1 ? w[1] : "");
                continue;
            }
            if (ClassSkipped.Contains(first)) continue;
            if (first == "class")
            {
                var rest = Trim(st["class".Length..]);
                var open = rest.EndsWith('{');
                var name = open ? Trim(rest[..^1]) : rest;
                if (name == "" || HasSpace(name)) return null;
                var idx = Declare(name);
                if (idx == -1) return null;
                if (open) curClass = idx;
                continue;
            }

            if (st.StartsWith("<<", StringComparison.Ordinal))
            {
                if (SplitOnce(st[2..], ">>") is not (var annotation, var after)) return null;
                var name = Trim(after);
                if (name == "" || HasSpace(name)) return null;
                var idx = Declare(name);
                if (idx == -1) return null;
                infos[idx].Annotation = Trim(annotation);
                continue;
            }

            if (ParseClassRelation(st) is { } rel)
            {
                var f = Declare(rel.From);
                if (f == -1) return null;
                var t = Declare(rel.To);
                if (t == -1) return null;
                if (graph.Edges.Count >= MermaidGraph.MaxEdges) return null;
                graph.Edges.Add(new(f, t, rel.Label, rel.HeadTo, rel.HeadFrom, rel.Line));
                continue;
            }

            if (SplitOnce(st, ":") is (var memberId, var memberText))
            {
                var id = Trim(memberId);
                var text = Trim(memberText);
                if (id == "" || HasSpace(id) || text == "") return null;
                var idx = Declare(id);
                if (idx == -1) return null;
                PushMember(infos[idx], text);
                continue;
            }
            return null;
        }

        if (graph.Nodes.Count == 0) return null;
        Sync();
        return (graph, infos);
    }

    /// <summary>Add a member to the attribute or method compartment, eliding past the cap.</summary>
    public static void PushMember(MermaidClassInfo info, string raw)
    {
        if (raw.StartsWith("<<", StringComparison.Ordinal))
        {
            if (SplitOnce(raw[2..], ">>") is (var annotation, _)) info.Annotation = Trim(annotation);
            return;
        }
        var member = DecodeHtmlEntities(DisplayGenerics(Trim(raw)));
        var list = member.Contains('(') ? info.Methods : info.Attrs;
        if (list.Count < MermaidGraph.MaxMembers) list.Add(member);
        else if (list.Count == MermaidGraph.MaxMembers) list.Add("…");
    }

    private sealed record ClassRelation(string From, string To, MermaidHead HeadFrom, MermaidHead HeadTo, MermaidLineKind Line, string? Label);

    private static ClassRelation? ParseClassRelation(string st)
    {
        var chars = CodePoints(st);
        (int Pos, string Op, MermaidHead HeadFrom, MermaidHead HeadTo, MermaidLineKind Line)? found = null;

        for (var pos = 0; pos < chars.Length && found is null; pos++)
        {
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

        var split = SplitOnce(rhs, ":");
        var toId = Trim(split is { } s1 ? s1.Left : rhs);
        var relLabel = split is { } s2 ? NonEmpty(DecodeHtmlEntities(Trim(s2.Right))) : null;

        if (lhs == "" || toId == "" || HasSpace(lhs) || HasSpace(toId)) return null;

        var label = NonEmpty(string.Join(" ", new[] { cardFrom, relLabel ?? "", cardTo }.Where(x => x != "")));
        return new(lhs, toId, hit.HeadFrom, hit.HeadTo, hit.Line, label);
    }

    /// <summary><c>Class "1"</c> — a quoted cardinality trailing the left-hand name.</summary>
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

    /// <summary><c>"0..*" Class</c> — a quoted cardinality leading the right-hand name.</summary>
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

    /// <summary>Mermaid writes generics as <c>List~T~</c>; show them as <c>List&lt;T&gt;</c>.</summary>
    private static string DisplayGenerics(string s)
    {
        var sb = new StringBuilder();
        var open = false;
        foreach (var c in CodePoints(s))
        {
            if (c == "~")
            {
                sb.Append(open ? '>' : '<');
                open = !open;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------------ ER

    public static (MermaidGraph Graph, List<MermaidClassInfo> Infos)? ParseEr(string src)
    {
        var statements = StatementsOf(src);
        if (HeaderKind(statements) != "erdiagram") return null;

        var graph = new MermaidGraph();
        var infos = new List<MermaidClassInfo>();
        var curEntity = -1;

        foreach (var st in statements.Skip(1))
        {
            if (curEntity != -1)
            {
                if (st == "}") curEntity = -1;
                else PushErAttribute(infos[curEntity], st);
                continue;
            }

            if (SplitErRelationship(st) is { } rel)
            {
                var tokens = Words(rel.Rel);
                if (tokens.Count != 3) return null;
                if (ParseErOp(tokens[1]) is not { } op) return null;
                var f = ErEntity(graph, infos, tokens[0]);
                if (f == -1) return null;
                var t = ErEntity(graph, infos, tokens[2]);
                if (t == -1) return null;
                if (graph.Edges.Count >= MermaidGraph.MaxEdges) return null;
                var relLabel = rel.Label is null ? "" : CleanLabel(rel.Label);
                graph.Edges.Add(new(
                    f,
                    t,
                    NonEmpty(string.Join(" ", new[] { op.CardL, relLabel, op.CardR }.Where(x => x != ""))),
                    MermaidHead.None,
                    MermaidHead.None,
                    op.Line));
                continue;
            }

            var open = st.EndsWith('{');
            var decl = open ? Trim(st[..^1]) : st;
            if (decl == "" || Words(decl).Count != 1) return null;
            var idx = ErEntity(graph, infos, decl);
            if (idx == -1) return null;
            if (open) curEntity = idx;
        }

        if (graph.Nodes.Count == 0) return null;
        while (infos.Count < graph.Nodes.Count) infos.Add(new());
        return (graph, infos);
    }

    private static int ErEntity(MermaidGraph graph, List<MermaidClassInfo> infos, string token)
    {
        var open = token.IndexOf('[');
        int idx;
        if (open != -1)
        {
            var id = token[..open];
            var label = CleanLabel(token[(open + 1)..].TrimEnd(']'));
            if (id == "" || label == "") return -1;
            idx = graph.NodeLabel(id, label);
        }
        else
        {
            idx = graph.NodeIndex(token, null, MermaidShape.Rect);
        }
        if (idx == -1) return -1;
        while (infos.Count < graph.Nodes.Count) infos.Add(new());
        return idx;
    }

    private static (string Rel, string? Label)? SplitErRelationship(string st)
    {
        var split = SplitOnce(st, ":");
        var rel = split is { } s1 ? s1.Left : st;
        var label = split is { } s2 ? Trim(s2.Right) : null;
        return Words(rel).Any(t => ParseErOp(t) is not null) ? (rel, label) : null;
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
        "}o" or "o{" => "0..*",
        "}|" or "|{" => "1..*",
        _ => null,
    };

    /// <summary>ER attributes are <c>type name</c>; a trailing quoted comment is dropped.</summary>
    public static void PushErAttribute(MermaidClassInfo info, string raw)
    {
        var parts = new List<string>();
        foreach (var tok in Words(raw))
        {
            if (tok.StartsWith('"')) break;
            parts.Add(tok);
        }
        if (parts.Count == 0) return;
        var line = DecodeHtmlEntities(string.Join(" ", parts));
        if (info.Attrs.Count < MermaidGraph.MaxMembers) info.Attrs.Add(line);
        else if (info.Attrs.Count == MermaidGraph.MaxMembers) info.Attrs.Add("…");
    }

    // ------------------------------------------------------------------ sequence

    /// <summary>Message operators, longest-first so <c>--&gt;&gt;</c> wins over <c>--&gt;</c>.</summary>
    private static readonly (string Op, bool Dashed, MermaidSeqHead Head)[] SeqOps =
    [
        ("-->>", true, MermaidSeqHead.Arrow),
        ("->>", false, MermaidSeqHead.Arrow),
        ("--x", true, MermaidSeqHead.Cross),
        ("-x", false, MermaidSeqHead.Cross),
        ("--)", true, MermaidSeqHead.Arrow),
        ("-)", false, MermaidSeqHead.Arrow),
        ("-->", true, MermaidSeqHead.Arrow),
        ("->", false, MermaidSeqHead.Arrow),
    ];

    private const int MaxSeqOp = 4;

    private static readonly string[] SeqSkipped = ["activate", "deactivate", "create", "destroy", "title", "acctitle", "accdescr", "links", "link", "properties"];
    private static readonly string[] SeqBlocks = ["loop", "alt", "opt", "par", "critical", "break", "else", "and", "option"];
    private static readonly string[] SeqContinuations = ["else", "and", "option"];

    public static MermaidSequence? ParseSequence(string src)
    {
        var statements = StatementsOf(src);
        if (HeaderKind(statements) != "sequencediagram") return null;

        var seq = new MermaidSequence();
        var autonumber = false;
        var msgCount = 0;
        // One entry per open block; `true` when it draws a divider on `end`.
        var blocks = new List<bool>();

        foreach (var st in statements.Skip(1))
        {
            var first = FirstWord(st);
            var lower = AsciiLower(first);

            if (lower is "participant" or "actor")
            {
                var rest = Trim(st[first.Length..]);
                if (rest == "") return null;
                var as_ = SplitOnce(rest, " as ");
                if (seq.Participant(as_ is { } a1 ? Trim(a1.Left) : rest, as_ is { } a2 ? CleanLabel(a2.Right) : null) == -1) return null;
                continue;
            }
            if (lower == "autonumber")
            {
                autonumber = true;
                continue;
            }
            if (SeqSkipped.Contains(lower)) continue;
            if (lower == "note")
            {
                if (ParseNoteAnchor(Trim(st[first.Length..]), seq) is not { } note) return null;
                if (seq.Items.Count >= MermaidGraph.MaxEdges) return null;
                seq.Items.Add(new(MermaidSeqItemKind.Note, Text: note.Text, Anchor: note.Anchor));
                continue;
            }
            if (SeqBlocks.Contains(lower))
            {
                if (SeqContinuations.Contains(lower))
                {
                    // A continuation only divides a block that opened one.
                    if (blocks.Count == 0 || !blocks[^1]) continue;
                }
                else
                {
                    blocks.Add(true);
                }
                if (seq.Items.Count >= MermaidGraph.MaxEdges) return null;
                seq.Items.Add(new(MermaidSeqItemKind.Divider, Text: DecodeHtmlEntities(st)));
                continue;
            }
            if (lower is "rect" or "box")
            {
                blocks.Add(false);
                continue;
            }
            if (lower == "end")
            {
                if (blocks.Count > 0)
                {
                    var opened = blocks[^1];
                    blocks.RemoveAt(blocks.Count - 1);
                    if (opened)
                    {
                        if (seq.Items.Count >= MermaidGraph.MaxEdges) return null;
                        seq.Items.Add(new(MermaidSeqItemKind.Divider, Text: "end"));
                    }
                }
                continue;
            }

            if (ParseSeqMessage(st, seq) is not { } msg) return null;
            var text = msg.Text;
            if (autonumber)
            {
                msgCount++;
                text = text is null ? $"{msgCount}." : $"{msgCount}. {text}";
            }
            if (seq.Items.Count >= MermaidGraph.MaxEdges) return null;
            seq.Items.Add(new(MermaidSeqItemKind.Message, msg.From, msg.To, text, msg.Dashed, msg.Head));
        }

        return seq.Labels.Count == 0 ? null : seq;
    }

    private static (string Text, MermaidNoteAnchor Anchor)? ParseNoteAnchor(string rest, MermaidSequence seq)
    {
        var lower = AsciiLower(rest);
        MermaidNoteKind kind;
        string idsAndText;
        if (lower.StartsWith("over ", StringComparison.Ordinal))
        {
            kind = MermaidNoteKind.Over;
            idsAndText = rest["over ".Length..];
        }
        else if (lower.StartsWith("left of ", StringComparison.Ordinal))
        {
            kind = MermaidNoteKind.Left;
            idsAndText = rest["left of ".Length..];
        }
        else if (lower.StartsWith("right of ", StringComparison.Ordinal))
        {
            kind = MermaidNoteKind.Right;
            idsAndText = rest["right of ".Length..];
        }
        else
        {
            return null;
        }

        if (SplitOnce(idsAndText, ":") is not (var ids, var body)) return null;
        var text = DecodeHtmlEntities(Trim(body));
        var parts = ids.Split(',').Select(Trim).Where(s => s != "").ToList();
        if (parts.Count == 0) return null;
        var a = seq.Participant(parts[0], null);
        if (a == -1) return null;

        if (kind != MermaidNoteKind.Over) return (text, new(kind, a, a));
        var b = a;
        if (parts.Count > 1)
        {
            var second = seq.Participant(parts[1], null);
            if (second == -1) return null;
            b = second;
        }
        return (text, new(MermaidNoteKind.Over, Math.Min(a, b), Math.Max(a, b)));
    }

    private static (int From, int To, string? Text, bool Dashed, MermaidSeqHead Head)? ParseSeqMessage(string st, MermaidSequence seq)
    {
        var chars = CodePoints(st);
        (int Pos, string Op, bool Dashed, MermaidSeqHead Head)? found = null;
        for (var pos = 0; pos < chars.Length && found is null; pos++)
        {
            var tail = Join(chars, pos, pos + MaxSeqOp);
            foreach (var (op, dashed, head) in SeqOps)
            {
                if (tail.StartsWith(op, StringComparison.Ordinal))
                {
                    found = (pos, op, dashed, head);
                    break;
                }
            }
        }
        if (found is not { } hit) return null;

        var fromId = Trim(Join(chars, 0, hit.Pos));
        if (fromId == "") return null;
        // `+`/`-` activate and deactivate the target; they carry no layout meaning.
        var rest = TrimStart(Join(chars, hit.Pos + hit.Op.Length)).TrimStart('+', '-');

        var split = SplitOnce(rest, ":");
        var toId = Trim(split is { } s1 ? s1.Left : rest);
        var text = split is { } s2 ? NonEmpty(DecodeHtmlEntities(Trim(s2.Right))) : null;
        if (toId == "") return null;

        var from = seq.Participant(fromId, null);
        if (from == -1) return null;
        var to = seq.Participant(toId, null);
        if (to == -1) return null;
        return (from, to, text, hit.Dashed, hit.Head);
    }
}
