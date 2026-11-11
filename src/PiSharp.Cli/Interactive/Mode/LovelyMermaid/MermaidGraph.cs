// lovely-mermaid 0.3.3 (Apache-2.0): src/graph.ts.
namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal enum MermaidShape { Rect, Round, Diamond }

/// <summary>Decoration at one end of an edge.</summary>
internal enum MermaidHead { None, Arrow, Circle, Cross, Triangle, DiamondFill, DiamondOpen }

internal enum MermaidLineKind { Solid, Dotted, Thick }

internal enum MermaidDir { Down, Up, Right, Left }

internal sealed class MermaidNode(string label, MermaidShape shape)
{
    public string Label { get; set; } = label;
    public MermaidShape Shape { get; set; } = shape;
    public List<List<string>>? Sections { get; set; }
    public List<string>? Classes { get; set; }
    public string? Href { get; set; }
}

internal sealed record MermaidEdge(int From, int To, string? Label, MermaidHead HeadTo, MermaidHead HeadFrom, MermaidLineKind Line, string? CardFrom = null, string? CardTo = null);

/// <summary>A subgraph; <see cref="Parent"/> is -1 at the top level.</summary>
internal sealed record MermaidGroup(string Id, string Label, int Parent);

/// <summary>Extra compartment content for class and ER boxes.</summary>
internal sealed class MermaidClassInfo
{
    public string? Annotation { get; set; }
    public List<string> Attrs { get; } = [];
    public List<string> Methods { get; } = [];
}

/// <summary>
/// The shared diagram model. Flowchart, state, class and ER sources all parse into a graph; only sequence diagrams
/// have their own model. Group indices use -1 for "none" where the original uses <c>null</c>.
/// </summary>
internal sealed class MermaidGraph(MermaidDir dir = MermaidDir.Down)
{
    /// <summary>Caps that keep layout bounded; exceeding one drops the diagram to fallback.</summary>
    public const int MaxNodes = 128;
    public const int MaxEdges = 512;
    public const int MaxGroups = 24;
    public const int MaxGroupDepth = 6;
    /// <summary>Class members / ER attributes listed per box before eliding with <c>…</c>.</summary>
    public const int MaxMembers = 8;

    public List<MermaidNode> Nodes { get; set; } = [];
    public List<MermaidEdge> Edges { get; set; } = [];
    public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);
    public List<MermaidGroup> Groups { get; } = [];
    /// <summary>Innermost subgraph each node was declared in, parallel to <see cref="Nodes"/>.</summary>
    public List<int> NodeGroup { get; } = [];
    public int CurGroup { get; set; } = -1;
    /// <summary>Names a reached cap; the parser renders its prefix and warns about truncation.</summary>
    public string? Truncated { get; set; }
    /// <summary>Source statements that could not be read.</summary>
    public List<string> Warnings { get; } = [];
    public Dictionary<string, Dictionary<string, string>> ClassDefs { get; } = new(StringComparer.Ordinal);
    public MermaidDir Dir { get; set; } = dir;

    /// <summary><c>LR</c>/<c>RL</c>/<c>BT</c> as written in a header or <c>direction</c> statement; else <c>down</c>.</summary>
    public static MermaidDir ParseDir(string token) => MermaidLabels.AsciiUpper(token) switch
    {
        "LR" => MermaidDir.Right,
        "RL" => MermaidDir.Left,
        "BT" => MermaidDir.Up,
        _ => MermaidDir.Down,
    };

    /// <summary>Index of <paramref name="id"/>, creating the node if new; -1 once <see cref="MaxNodes"/> is reached.</summary>
    public int NodeIndex(string id, string? label, MermaidShape shape)
    {
        if (Index.TryGetValue(id, out var existing))
        {
            if (label is not null)
            {
                Nodes[existing].Label = label;
                Nodes[existing].Shape = shape;
            }
            return existing;
        }
        if (Nodes.Count >= MaxNodes)
        {
            Truncated ??= $"node cap ({MaxNodes}) reached";
            return -1;
        }
        Index[id] = Nodes.Count;
        Nodes.Add(new(label ?? id, shape));
        NodeGroup.Add(CurGroup);
        return Nodes.Count - 1;
    }

    /// <summary>Set a node's label without disturbing its shape, creating it if new.</summary>
    public int NodeLabel(string id, string label)
    {
        if (Index.TryGetValue(id, out var existing))
        {
            Nodes[existing].Label = label;
            return existing;
        }
        return NodeIndex(id, label, MermaidShape.Round);
    }

    public void AddClass(int idx, string name)
    {
        var classes = Nodes[idx].Classes ??= [];
        if (!classes.Contains(name, StringComparer.Ordinal)) classes.Add(name);
    }

    public void ApplyClasses(IEnumerable<(List<string> Ids, List<string> Names)> assignments)
    {
        foreach (var (ids, names) in assignments)
            foreach (var id in ids)
            {
                if (!Index.TryGetValue(MermaidJs.Trim(id), out var idx)) continue;
                foreach (var name in names)
                {
                    var trimmed = MermaidJs.Trim(name);
                    if (trimmed != "") AddClass(idx, trimmed);
                }
            }
    }

    public void ApplyHrefs(IEnumerable<(string Id, string Url)> hrefs)
    {
        foreach (var (id, url) in hrefs)
            if (Index.TryGetValue(MermaidJs.Trim(id), out var idx)) Nodes[idx].Href = url;
    }

    public void Drop(string st)
    {
        if (Truncated is null) Warnings.Add($"dropped, unreadable statement: \"{st}\"");
    }

    /// <summary>Append an edge, or flag truncation when <see cref="MaxEdges"/> is reached.</summary>
    public bool PushEdge(MermaidEdge edge)
    {
        if (Edges.Count >= MaxEdges)
        {
            Truncated ??= $"edge cap ({MaxEdges}) reached";
            return false;
        }
        Edges.Add(edge);
        return true;
    }
}
