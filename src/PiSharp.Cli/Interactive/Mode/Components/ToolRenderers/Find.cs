// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/tools/renderers/find.ts.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Presentation for the find tool.</summary>
internal static class FindRenderers
{
    private static string FormatFindCall(JsonNode? args, Theme theme)
    {
        var pattern = RenderUtils.Str(ToolJson.Get(args, "pattern"));
        var rawPath = RenderUtils.Str(ToolJson.Get(args, "path"));
        var path = rawPath is not null ? RenderUtils.ShortenPath(rawPath.Length > 0 ? rawPath : ".") : null;
        var invalidArg = RenderUtils.InvalidArgText(theme);
        var text = theme.Fg("toolTitle", theme.Bold("find")) + " " +
            (pattern is null ? invalidArg : theme.Fg("accent", pattern)) +
            theme.Fg("toolOutput", $" in {path ?? invalidArg}");
        if (ToolJson.Has(args, "limit")) text += theme.Fg("toolOutput", $" (limit {ToolJson.Template(ToolJson.Get(args, "limit"))})");
        return text;
    }

    private static string FormatFindResult(JsonObject result, ToolRenderResultOptions options, Theme theme, bool showImages)
    {
        var output = TextUtils.JsTrim(RenderUtils.GetTextOutput(result, showImages));
        var text = "";
        if (output.Length > 0)
        {
            var lines = output.Split('\n');
            var maxLines = options.Expanded ? lines.Length : 20;
            var displayLines = lines.Take(maxLines);
            var remaining = lines.Length - maxLines;
            text += "\n" + string.Join("\n", displayLines.Select(line => theme.Fg("toolOutput", line)));
            if (remaining > 0) text += RenderUtils.MoreLinesHint(theme, $"\n... ({remaining} more lines,");
        }

        var details = ToolJson.Get(result, "details");
        var resultLimit = ToolJson.Get(details, "resultLimitReached");
        var truncation = ToolJson.Get(details, "truncation");
        var truncated = ToolJson.Truthy(ToolJson.Get(truncation, "truncated"));
        if (ToolJson.Truthy(resultLimit) || truncated)
        {
            var warnings = new List<string>();
            if (ToolJson.Truthy(resultLimit)) warnings.Add($"{ToolJson.Template(resultLimit)} results limit");
            if (truncated) warnings.Add($"{ToolTruncate.FormatSize(ToolJson.GetNumber(truncation, "maxBytes") ?? ToolTruncate.DefaultMaxBytes)} limit");
            text += "\n" + theme.Fg("warning", $"[Truncated: {string.Join(", ", warnings)}]");
        }
        return text;
    }

    public static ToolRenderers Renderers { get; } = new(
        RenderCall: (args, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatFindCall(args, theme));
            return text;
        },
        RenderResult: (result, options, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatFindResult(result, options, theme, context.ShowImages));
            return text;
        });
}
