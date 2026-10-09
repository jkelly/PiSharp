// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/visual-truncate.ts.
// Shared utility for truncating text to visual lines (accounting for line wrapping).
// Used by tool renderers and bash-execution.ts for consistent behavior.
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Which visual lines to keep: "start" | "end".</summary>
internal enum VisualTruncateKeep { Start, End }

/// <param name="VisualLines">The visual lines to display</param>
/// <param name="SkippedCount">Number of visual lines that were skipped (hidden)</param>
internal sealed record VisualTruncateResult(List<string> VisualLines, int SkippedCount);

internal static class VisualTruncate
{
    /// <summary>
    /// Truncate text to a maximum number of visual lines.
    /// This accounts for line wrapping based on terminal width.
    /// </summary>
    /// <param name="text">The text content (may contain newlines)</param>
    /// <param name="maxVisualLines">Maximum number of visual lines to show</param>
    /// <param name="width">Terminal/render width</param>
    /// <param name="paddingX">Horizontal padding for Text component (default 0).
    /// Use 0 when result will be placed in a Box (Box adds its own padding).
    /// Use 1 when result will be placed in a plain Container.</param>
    /// <param name="keep">Which visual lines to keep: the last ones (default) or the first ones.</param>
    /// <returns>The truncated visual lines and count of skipped lines</returns>
    public static VisualTruncateResult TruncateToVisualLines(string text, int maxVisualLines, int width, int paddingX = 0,
        VisualTruncateKeep keep = VisualTruncateKeep.End)
    {
        if (string.IsNullOrEmpty(text)) return new([], 0);

        // Create a temporary Text component to render and get visual lines
        var tempText = new Text(text, paddingX, 0);
        var allVisualLines = tempText.Render(width);

        if (allVisualLines.Count <= maxVisualLines) return new([.. allVisualLines], 0);

        var truncatedLines = keep == VisualTruncateKeep.Start
            ? allVisualLines.Take(maxVisualLines >= 0 ? maxVisualLines : Math.Max(0, allVisualLines.Count + maxVisualLines)).ToList()
            : SliceLast(allVisualLines, maxVisualLines);
        var skippedCount = allVisualLines.Count - maxVisualLines;

        return new(truncatedLines, skippedCount);
    }

    // JS Array.slice(-n): n == 0 gives slice(-0) == slice(0), i.e. every line; a negative n slices from the start.
    private static List<string> SliceLast(List<string> lines, int count)
    {
        if (count == 0) return [.. lines];
        if (count < 0) return lines.Skip(Math.Min(lines.Count, -count)).ToList();
        return lines.Skip(Math.Max(0, lines.Count - count)).ToList();
    }
}

/// <param name="Text">Styled text; may contain newlines.</param>
/// <param name="MaxVisualLines">Maximum visual lines.</param>
/// <param name="Keep">Which visual lines to keep. The hint goes before kept end lines and after kept start lines.</param>
/// <param name="FormatHint">Styled hint line for the given number of hidden visual lines.</param>
internal sealed record VisualLinePreviewOptions(string Text, int MaxVisualLines, VisualTruncateKeep Keep, Func<int, string> FormatHint);

/// <summary>
/// Collapsed tool output limited to a number of visual lines, like bash output. Limiting logical
/// lines instead lets a single long line (such as minified JSON) wrap across the whole screen.
/// Caches its lines per width, since it renders on every frame for every result in the transcript.
/// </summary>
internal sealed class VisualLinePreview(VisualLinePreviewOptions options) : IComponent
{
    private int? cachedWidth;
    private List<string>? cachedLines;

    public List<string> Render(int width)
    {
        if (cachedLines is null || cachedWidth != width)
        {
            var preview = VisualTruncate.TruncateToVisualLines(options.Text, options.MaxVisualLines, width, 0, options.Keep);
            var lines = preview.VisualLines;
            if (preview.SkippedCount > 0)
            {
                var hint = TextUtils.TruncateToWidth(options.FormatHint(preview.SkippedCount), width, "...");
                cachedLines = options.Keep == VisualTruncateKeep.Start ? [.. lines, hint] : [hint, .. lines];
            }
            else
            {
                cachedLines = lines;
            }
            cachedWidth = width;
        }
        return cachedLines;
    }

    public void Invalidate()
    {
        cachedWidth = null;
        cachedLines = null;
    }
}
