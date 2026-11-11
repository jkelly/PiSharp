// lovely-mermaid 0.3.3 (Apache-2.0): src/index.ts, registry.ts, types.ts, ansi.ts, class-style.ts.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal sealed record MermaidSpan(string Text, string Role, IReadOnlyList<string>? Classes = null, string? Href = null)
{
    public string Cls => Role;
}

internal sealed record MermaidArt(int Width, IReadOnlyList<string> Plain, IReadOnlyList<IReadOnlyList<MermaidSpan>> Styled,
    IReadOnlyList<string> Warnings, Dictionary<string, Dictionary<string, string>> ClassDefs);

internal sealed record MermaidDrawn(MermaidCanvas Canvas, List<string> Warnings, Dictionary<string, Dictionary<string, string>> ClassDefs);

internal sealed record MermaidClassStyle(string? Fill, string? Stroke, string? Color, bool? Bold);

/// <summary>The native port of the exact lovely-mermaid 0.3.3 package used by Pi 1.5.2.</summary>
internal static partial class LovelyMermaid
{
    public static string? DiagramKind(string source) => MermaidStatements.HeaderKind(MermaidStatements.StatementsOf(MermaidLabels.StripControls(source))) switch
    {
        "flowchart" or "graph" => "flowchart",
        "statediagram" or "statediagram-v2" => "state",
        "classdiagram" or "classdiagram-v2" => "class",
        "erdiagram" => "er",
        "sequencediagram" => "sequence",
        "pie" => "pie",
        "mindmap" => "mindmap",
        "timeline" => "timeline",
        "gitgraph" or "gitgraph:" => "gitgraph",
        _ => null,
    };

    public static MermaidArt? Render(string source)
    {
        source = MermaidLabels.StripControls(source);
        if (MermaidJs.Trim(source).Length == 0) return null;
        var drawn = DiagramKind(source) switch
        {
            "flowchart" => MermaidFlowchart.Render(source),
            "state" => MermaidState.Render(source),
            "class" => MermaidClass.Render(source),
            "er" => MermaidEr.Render(source),
            "sequence" => MermaidSequenceParser.Render(source),
            "pie" => MermaidPie.Render(source),
            "mindmap" => MermaidMindmap.Render(source),
            "timeline" => MermaidTimeline.Render(source),
            "gitgraph" => MermaidGitgraph.Render(source),
            _ => null,
        };
        if (drawn is null) return null;
        var (plain, styled, width) = drawn.Canvas.ToLines();
        if (MermaidStatements.FrontmatterTitle(source) is { } title)
        {
            var titleWidth = MermaidWidth.StringWidth(title);
            width = Math.Max(width, titleWidth);
            var padding = new string(' ', (width - titleWidth) / 2);
            plain.InsertRange(0, [padding + title, ""]);
            styled.InsertRange(0, [padding.Length == 0
                ? new List<MermaidSpan> { new(title, "title") }
                : new List<MermaidSpan> { new(padding, "none"), new(title, "title") }, new List<MermaidSpan>()]);
        }
        return new(width, plain, styled, drawn.Warnings, drawn.ClassDefs);
    }

    public static MermaidArt SourceBox(string source, int? maxWidth = null) => MermaidSourceBox.SourceBox(source, maxWidth);

    public static readonly IReadOnlyDictionary<string, string> DefaultTheme = new Dictionary<string, string>
    {
        ["border"] = "2", ["edge"] = "36", ["edgeLabel"] = "2;36", ["title"] = "1",
    };

    public static List<string> ToAnsi(MermaidArt art, IReadOnlyDictionary<string, string>? theme = null)
    {
        theme ??= DefaultTheme;
        return art.Styled.Select(row =>
        {
            var text = new StringBuilder();
            foreach (var span in row)
            {
                theme.TryGetValue(span.Role, out var fallback);
                var style = ResolveClassStyle(span.Classes, art.ClassDefs);
                var sgr = style is null ? fallback : ClassSgr(style, span.Role, fallback);
                if (span.Href is not null) text.Append("\u001b]8;;").Append(span.Href).Append("\u001b\\");
                if (sgr is not null) text.Append("\u001b[").Append(sgr).Append('m');
                text.Append(span.Text);
                if (sgr is not null) text.Append("\u001b[0m");
                if (span.Href is not null) text.Append("\u001b]8;;\u001b\\");
            }
            return text.ToString();
        }).ToList();
    }

    public static MermaidClassStyle? ResolveClassStyle(IReadOnlyList<string>? classes, IReadOnlyDictionary<string, Dictionary<string, string>> definitions)
    {
        if (classes is null) return null;
        string? fill = null, stroke = null, color = null;
        bool? bold = null;
        foreach (var name in classes)
        {
            if (!definitions.TryGetValue(name, out var properties)) continue;
            foreach (var (key, value) in properties)
            {
                if (key is "fill" or "stroke" or "color")
                {
                    if (NormalizeColor(value) is not { } normalized) continue;
                    if (key == "fill") fill = normalized;
                    else if (key == "stroke") stroke = normalized;
                    else color = normalized;
                }
                else if (key == "font-weight") bold = MermaidJs.Trim(value) is "bold" or "bolder";
            }
        }
        return fill is null && stroke is null && color is null && bold is null ? null : new(fill, stroke, color, bold);
    }

    public static string? ClassSgr(MermaidClassStyle style, string role, string? fallback = null)
    {
        var parameters = new List<string>();
        var fg = role == "border" ? style.Stroke ?? style.Color : role == "edge" ? null : style.Color;
        var backed = fg ?? (style.Fill is { } fill ? ContrastOn(fill) : null);
        if (backed is not null) parameters.Add(Rgb(backed, 38));
        if (style.Fill is not null) parameters.Add(Rgb(style.Fill, 48));
        if (parameters.Count == 0 && fallback is not null) parameters.Add(fallback);
        if (style.Bold == true) parameters.Insert(0, "1");
        return parameters.Count == 0 ? null : string.Join(';', parameters);
    }

    private static int Channel(string hex, int offset) => int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static string Rgb(string hex, int sgr) => $"{sgr};2;{Channel(hex, 1)};{Channel(hex, 3)};{Channel(hex, 5)}";
    public static string ContrastOn(string fill) => (Channel(fill, 1) * 299 + Channel(fill, 3) * 587 + Channel(fill, 5) * 114) / 1000.0 >= 128
        ? "#000000" : "#ffffff";

    [GeneratedRegex(@"^#[0-9a-f]{6}$")] private static partial Regex FullHex();
    [GeneratedRegex(@"^#[0-9a-f]{3}$")] private static partial Regex ShortHex();
    [GeneratedRegex(@"^rgba?\([\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*([0-9]+)[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*,[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*([0-9]+)[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*,[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*([0-9]+)")]
    private static partial Regex RgbColor();

    private static string? NormalizeColor(string value)
    {
        var s = MermaidJs.Trim(value).ToLowerInvariant();
        if (FullHex().IsMatch(s)) return s;
        if (ShortHex().IsMatch(s)) return $"#{s[1]}{s[1]}{s[2]}{s[2]}{s[3]}{s[3]}";
        var rgb = RgbColor().Match(s);
        if (rgb.Success)
            return "#" + string.Concat(Enumerable.Range(1, 3).Select(i => ((int)Math.Min(255, double.Parse(rgb.Groups[i].Value, CultureInfo.InvariantCulture))).ToString("x2", CultureInfo.InvariantCulture)));
        return MermaidCssColors.Named.GetValueOrDefault(s);
    }
}
