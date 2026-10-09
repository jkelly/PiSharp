// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/tools/renderers/ls.ts.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Presentation for the ls tool.</summary>
internal static class LsRenderers
{
    private static string FormatLsCall(JsonNode? args, Theme theme, string cwd)
    {
        var pathDisplay = RenderUtils.RenderToolPath(RenderUtils.Str(ToolJson.Get(args, "path")), theme, cwd, emptyFallback: ".");
        var text = theme.Fg("toolTitle", theme.Bold("ls")) + " " + pathDisplay;
        if (ToolJson.Has(args, "limit")) text += theme.Fg("toolOutput", $" (limit {ToolJson.Template(ToolJson.Get(args, "limit"))})");
        return text;
    }

    private static string FormatLsResult(JsonObject result, ToolRenderResultOptions options, Theme theme, bool showImages)
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
        var entryLimit = ToolJson.Get(details, "entryLimitReached");
        var truncation = ToolJson.Get(details, "truncation");
        var truncated = ToolJson.Truthy(ToolJson.Get(truncation, "truncated"));
        if (ToolJson.Truthy(entryLimit) || truncated)
        {
            var warnings = new List<string>();
            if (ToolJson.Truthy(entryLimit)) warnings.Add($"{ToolJson.Template(entryLimit)} entries limit");
            if (truncated) warnings.Add($"{ToolTruncate.FormatSize(ToolJson.GetNumber(truncation, "maxBytes") ?? ToolTruncate.DefaultMaxBytes)} limit");
            text += "\n" + theme.Fg("warning", $"[Truncated: {string.Join(", ", warnings)}]");
        }
        return text;
    }

    public static ToolRenderers Renderers { get; } = new(
        RenderCall: (args, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatLsCall(args, theme, context.Cwd));
            return text;
        },
        RenderResult: (result, options, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatLsResult(result, options, theme, context.ShowImages));
            return text;
        });
}
