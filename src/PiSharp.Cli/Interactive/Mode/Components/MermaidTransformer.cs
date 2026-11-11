// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): coding-agent/src/modes/interactive/components/mermaid.ts.
using System.Text.RegularExpressions;
using PiSharp.Cli.Interactive.Mode.LovelyMermaid;
using PiSharp.Tui.Pi;
using LovelyRenderer = PiSharp.Cli.Interactive.Mode.LovelyMermaid.LovelyMermaid;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>createMermaidMarkdownTransformer: replaces top-level Mermaid code blocks with Unicode terminal diagrams (lovely-mermaid).</summary>
internal static partial class MermaidTransformer
{
    [GeneratedRegex("`+")] private static partial Regex BacktickRuns();
    private static bool IsMermaid(MarkdownToken token) =>
        token.Type == "code" && token.Lang is { } lang &&
        MermaidJs.Words(lang).FirstOrDefault()?.ToLowerInvariant() == "mermaid";

    /// <summary>Each diagram row as inline code so Markdown keeps its spacing; a fence longer than any backtick run, padded when the row
    /// starts or ends with a backtick; a blank row is a non-breaking space.</summary>
    internal static string CodeSpan(string line)
    {
        var content = line.Length == 0 ? " " : line;
        var longest = BacktickRuns().Matches(content).Select(match => match.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', longest + 1);
        var padding = content.StartsWith('`') || content.EndsWith('`') ? " " : "";
        return fence + padding + content + padding + fence;
    }

    private static IReadOnlyList<string> ThemedLines(MermaidArt art, Theme theme)
    {
        string Foreground(string color) => theme.GetFgAnsi(color)[2..^1];
        var ansiTheme = new Dictionary<string, string>
        {
            ["border"] = Foreground("borderMuted"),
            ["text"] = Foreground("text"),
            ["edge"] = Foreground("accent"),
            ["edgeLabel"] = Foreground("text"),
            ["title"] = "1;" + Foreground("accent"),
        };
        var hyperlinks = TerminalImage.GetCapabilities().Hyperlinks;
        var displayArt = art with
        {
            Styled = art.Styled.Select(row => (IReadOnlyList<MermaidSpan>)row.Select(span =>
                span.Href is null || hyperlinks && span.Role == "text" ? span : span with { Href = null }).ToList()).ToList(),
        };
        return LovelyRenderer.ToAnsi(displayArt, ansiTheme);
    }

    /// <param name="getMode">settingsManager.getMermaidRenderingMode(): "off", "final" or "streaming".</param>
    /// <param name="theme">The theme for diagram spans and warnings; null renders plain rows.</param>
    internal static MarkdownTransformer Create(Func<string> getMode, Func<Theme?>? theme = null) => (markdown, context) =>
    {
        var mode = getMode();
        if (mode == "off" || context.MessageType is not (MarkdownMessageType.User or MarkdownMessageType.Assistant or MarkdownMessageType.Summary) || context.IsStreaming && mode != "streaming")
            return markdown;
        var themed = theme?.Invoke();
        return string.Concat(MarkdownLexer.Lex(markdown).Select(token =>
        {
            if (!IsMermaid(token)) return token.Raw;
            var art = LovelyRenderer.Render(token.Text);
            if (art is null || art.Width > context.AvailableWidth) return token.Raw;
            if (!context.IsStreaming && art.Warnings.Count > 0)
            {
                var suffix = art.Warnings.Count > 1 ? $" (+{art.Warnings.Count - 1} more)" : "";
                var warning = $"Mermaid diagram not rendered: {art.Warnings[0]}{suffix}";
                return $"{token.Raw}\n{CodeSpan(themed is null ? warning : themed.Fg("warning", warning))}  \n";
            }
            var lines = themed is null ? art.Plain : ThemedLines(art, themed);
            // Markdown hard breaks keep every diagram row on its own line.
            return string.Join("  \n", lines.Select(CodeSpan)) + "\n";
        }));
    };
}
