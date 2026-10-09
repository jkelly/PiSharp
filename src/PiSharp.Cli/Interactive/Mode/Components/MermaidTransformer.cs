// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/mermaid.ts.
using System.Text.RegularExpressions;
using PiSharp.Cli.Interactive.Mode.Mermaid;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>createMermaidMarkdownTransformer: replaces top-level Mermaid code blocks with Unicode terminal diagrams (grok-mermaid).</summary>
internal static partial class MermaidTransformer
{
    [GeneratedRegex("`+")] private static partial Regex BacktickRuns();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    private static bool IsMermaid(MarkdownToken token) =>
        token.Type == "code" && token.Lang is { } lang && Whitespace().Split(lang.Trim(), 2)[0].ToLowerInvariant() == "mermaid";

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

    private static string StyleSpan(MermaidSpan span, Theme theme) => span.Cls switch
    {
        "border" => theme.Fg("borderMuted", span.Text),
        "text" => theme.Fg("text", span.Text),
        "edge" => theme.Fg("accent", span.Text),
        "edgeLabel" => theme.Fg("muted", span.Text),
        "title" => theme.Fg("accent", theme.Bold(span.Text)),
        _ => span.Text
    };

    /// <param name="getMode">settingsManager.getMermaidRenderingMode(): "off", "final" or "streaming".</param>
    /// <param name="theme">The theme for diagram spans and warnings; null renders plain rows.</param>
    internal static MarkdownTransformer Create(Func<string> getMode, Func<Theme?>? theme = null) => (markdown, context) =>
    {
        var mode = getMode();
        if (mode == "off" || context.MessageType == MarkdownMessageType.AssistantThinking || context.IsStreaming && mode != "streaming")
            return markdown;
        var themed = theme?.Invoke();
        return string.Concat(MarkdownLexer.Lex(markdown).Select(token =>
        {
            if (!IsMermaid(token)) return token.Raw;
            var art = GrokMermaid.Render(token.Text);
            if (art is null || art.Width > context.AvailableWidth) return token.Raw;
            if (!context.IsStreaming && art.Warnings.Count > 0)
            {
                var suffix = art.Warnings.Count > 1 ? $" (+{art.Warnings.Count - 1} more)" : "";
                var warning = $"Mermaid diagram not rendered: {art.Warnings[0]}{suffix}";
                return $"{token.Raw}\n{CodeSpan(themed is null ? warning : themed.Fg("warning", warning))}  \n";
            }
            var lines = themed is null ? art.Plain : art.Styled.Select(row => string.Concat(row.Select(span => StyleSpan(span, themed)))).ToList();
            // Markdown hard breaks keep every diagram row on its own line.
            return string.Join("  \n", lines.Select(CodeSpan)) + "\n";
        }));
    };
}
