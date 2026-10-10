// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/tools/renderers/grep.ts.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Presentation for the grep tool.</summary>
internal static class GrepRenderers
{
    private static string FormatGrepCall(JsonNode? args, Theme theme)
    {
        var pattern = RenderUtils.Str(ToolJson.Get(args, "pattern"));
        var rawPath = RenderUtils.Str(ToolJson.Get(args, "path"));
        var path = rawPath is not null ? RenderUtils.ShortenPath(rawPath.Length > 0 ? rawPath : ".") : null;
        var glob = RenderUtils.Str(ToolJson.Get(args, "glob"));
        var invalidArg = RenderUtils.InvalidArgText(theme);
        var text = theme.Fg("toolTitle", theme.Bold("grep")) + " " +
            (pattern is null ? invalidArg : theme.Fg("accent", $"/{pattern}/")) +
            theme.Fg("toolOutput", $" in {path ?? invalidArg}");
        if (!string.IsNullOrEmpty(glob)) text += theme.Fg("toolOutput", $" ({glob})");
        if (ToolJson.Has(args, "limit")) text += theme.Fg("toolOutput", $" limit {ToolJson.Template(ToolJson.Get(args, "limit"))}");
        return text;
    }

    private static string FormatGrepResult(JsonObject result, ToolRenderResultOptions options, Theme theme, bool showImages)
    {
        var output = TextUtils.JsTrim(RenderUtils.GetTextOutput(result, showImages));
        var text = "";
        if (output.Length > 0)
        {
            var lines = output.Split('\n');
            var maxLines = options.Expanded ? lines.Length : 15;
            var displayLines = lines.Take(maxLines);
            var remaining = lines.Length - maxLines;
            text += "\n" + string.Join("\n", displayLines.Select(line => theme.Fg("toolOutput", line)));
            if (remaining > 0) text += RenderUtils.MoreLinesHint(theme, $"\n... ({remaining} more lines,");
        }

        var details = ToolJson.Get(result, "details");
        var matchLimit = ToolJson.Get(details, "matchLimitReached");
        var truncation = ToolJson.Get(details, "truncation");
        var truncated = ToolJson.Truthy(ToolJson.Get(truncation, "truncated"));
        var linesTruncated = ToolJson.Truthy(ToolJson.Get(details, "linesTruncated"));
        if (ToolJson.Truthy(matchLimit) || truncated || linesTruncated)
        {
            var warnings = new List<string>();
            if (ToolJson.Truthy(matchLimit)) warnings.Add($"{ToolJson.Template(matchLimit)} matches limit");
            if (truncated) warnings.Add($"{ToolTruncate.FormatSize(ToolJson.GetNumber(truncation, "maxBytes") ?? ToolTruncate.DefaultMaxBytes)} limit");
            if (linesTruncated) warnings.Add("some lines truncated");
            text += "\n" + theme.Fg("warning", $"[Truncated: {string.Join(", ", warnings)}]");
        }
        return text;
    }

    public static ToolRenderers Renderers { get; } = new(
        RenderCall: (args, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatGrepCall(args, theme));
            return text;
        },
        RenderResult: (result, options, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatGrepResult(result, options, theme, context.ShowImages));
            return text;
        });
}
