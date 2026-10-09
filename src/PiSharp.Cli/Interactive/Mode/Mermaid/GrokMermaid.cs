// grok-mermaid 0.2.3 (Apache-2.0): src/index.ts, src/types.ts, src/ansi.ts — used by Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/mermaid.ts.
using System.Text;

namespace PiSharp.Cli.Interactive.Mode.Mermaid;

/// <summary>A run of adjacent cells sharing one semantic class: border, text, edge, edgeLabel, title or none.</summary>
internal sealed record MermaidSpan(string Text, string Cls);

/// <summary>
/// A rendered diagram (types.ts <c>MermaidArt</c>). <see cref="Plain"/>[i] and <see cref="Styled"/>[i] describe the
/// same row; <see cref="Width"/> is the display columns the widest row needs. <see cref="Warnings"/> lists source the
/// grammar could not read and dropped; advisory only.
/// </summary>
internal sealed record MermaidArt(int Width, IReadOnlyList<string> Plain, IReadOnlyList<IReadOnlyList<MermaidSpan>> Styled, IReadOnlyList<string> Warnings);

/// <summary>Port of grok-mermaid's public API: <c>render</c>, <c>diagramKind</c>, <c>sourceBox</c>, <c>toAnsi</c>.</summary>
internal static class GrokMermaid
{
    /// <summary>
    /// Render a Mermaid source block as Unicode box-drawing art at whatever size it needs. <c>null</c> means there is
    /// no art to show: blank input, a syntax error, a diagram type this renderer does not draw, or one large enough
    /// that laying it out is refused. Rendering is best-effort; everything given up on is listed in the warnings.
    /// </summary>
    public static MermaidArt? Render(string text)
    {
        var src = MermaidLabels.StripControls(text);
        if (MermaidJs.Trim(src) == "") return null;
        if (Attempt(src) is not { } drawn) return null;
        var (plain, styled, width) = drawn.Canvas.ToLines();
        return new(width, plain, styled, drawn.Warnings);
    }

    /// <summary>
    /// The kind of diagram <paramref name="src"/> declares — <c>flowchart</c>, <c>state</c>, <c>class</c>, <c>er</c> or
    /// <c>sequence</c> — or <c>null</c> if its header names no type this renderer draws.
    /// </summary>
    public static string? DiagramKind(string src) => MermaidParse.DiagramKind(src);

    /// <summary>Frame <paramref name="src"/> in a titled box, hard-wrapping its lines to <paramref name="maxWidth"/> columns.</summary>
    public static MermaidArt SourceBox(string src, int? maxWidth = null) => MermaidSourceBox.SourceBox(src, maxWidth);

    /// <summary>Dim frame, plain labels, cyan connectors. Readable on light and dark.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultTheme = new Dictionary<string, string>
    {
        [MermaidCls.Border] = "2",
        [MermaidCls.Edge] = "36",
        [MermaidCls.EdgeLabel] = "2;36",
        [MermaidCls.Title] = "1",
    };

    /// <summary>Render art to ANSI-coloured lines; a class missing from <paramref name="theme"/> is printed unstyled.</summary>
    public static List<string> ToAnsi(MermaidArt art, IReadOnlyDictionary<string, string>? theme = null)
    {
        theme ??= DefaultTheme;
        const char esc = (char)27;
        return art.Styled.Select(row =>
        {
            var sb = new StringBuilder();
            foreach (var span in row)
            {
                if (theme.TryGetValue(span.Cls, out var sgr)) sb.Append(esc).Append('[').Append(sgr).Append('m').Append(span.Text).Append(esc).Append("[0m");
                else sb.Append(span.Text);
            }
            return sb.ToString();
        }).ToList();
    }

    private sealed record Drawn(MermaidCanvas Canvas, List<string> Warnings);

    /// <summary>Draw <paramref name="src"/>, retrying once without its last line if the grammar rejects it.</summary>
    private static Drawn? Attempt(string src)
    {
        if (Draw(src) is { } drawn) return drawn;

        var body = MermaidJs.TrimEnd(src);
        var cut = body.LastIndexOf('\n');
        if (cut == -1) return null;
        if (Draw(body[..cut]) is not { } salvaged) return null;

        var dropped = MermaidJs.Trim(body[(cut + 1)..]);
        return new(salvaged.Canvas, [.. salvaged.Warnings, $"dropped, unreadable final line: \"{dropped}\""]);
    }

    /// <summary>Dispatch on the declared diagram type; <c>null</c> means nothing was drawn.</summary>
    private static Drawn? Draw(string src)
    {
        static Drawn? Plain(MermaidCanvas? canvas) => canvas is null ? null : new(canvas, []);

        switch (MermaidParse.DiagramKind(src))
        {
            case "flowchart":
                {
                    var graph = MermaidParse.ParseGraph(src);
                    if (graph is null) return null;
                    var canvas = graph.Groups.Count == 0 ? MermaidLayout.LayoutFlowchart(graph) : MermaidLayout.LayoutGrouped(graph);
                    return canvas is null ? null : new(canvas, graph.Warnings);
                }
            case "state":
                {
                    var state = MermaidParse.ParseState(src);
                    return state is null ? null : Plain(MermaidLayout.LayoutFlowchart(state));
                }
            case "class":
                {
                    var cls = MermaidParse.ParseClass(src);
                    return cls is not { } c ? null : Plain(MermaidLayout.LayoutClass(c.Graph, c.Infos));
                }
            case "er":
                {
                    var er = MermaidParse.ParseEr(src);
                    return er is not { } e ? null : Plain(MermaidLayout.LayoutClass(e.Graph, e.Infos));
                }
            case "sequence":
                {
                    var seq = MermaidParse.ParseSequence(src);
                    return seq is null ? null : Plain(MermaidLayoutSeq.LayoutSequence(seq));
                }
            default:
                return null;
        }
    }
}
