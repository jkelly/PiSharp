// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/tools/renderers/read.ts.
// getReadmePath (coding-agent/src/config.ts) is PiSharp's documentation location (PiSystemPrompt.Documentation).
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Presentation for the read tool.</summary>
internal static class ReadRenderers
{
    private sealed record CompactReadClassification(string Kind, string Label);

    private static readonly HashSet<string> CompactResourceFileNames = new(StringComparer.Ordinal)
    { "AGENTS.override.md", "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD" };

    /// <summary>getReadmePath(): the README.md of the package (tests may replace it).</summary>
    public static Func<string> ReadmePath { get; set; } = () => PiSystemPrompt.Documentation().Readme;

    private static string FormatReadLineRange(JsonNode? args, Theme theme)
    {
        // Strict tool schemas make models send null for omitted optional fields.
        var offset = ToolJson.Get(args, "offset");
        var limit = ToolJson.Get(args, "limit");
        if (offset is null && limit is null) return "";
        var startLine = offset ?? JsonValue.Create(1);
        string endLine = "";
        if (limit is not null)
        {
            // startLine + limit - 1; non-numeric arguments (invalid per the schema) are shown as NaN.
            endLine = ToolJson.AsNumber(startLine) is { } start && ToolJson.AsNumber(limit) is { } count ? ToolJson.JsNumber(start + count - 1) : "NaN";
            if (endLine == "0") endLine = "";
        }
        return theme.Fg("warning", $":{ToolJson.Template(startLine)}{(endLine.Length > 0 ? "-" + endLine : "")}");
    }

    private static string FormatReadCall(JsonNode? args, Theme theme, string cwd)
    {
        var pathDisplay = RenderUtils.RenderToolPath(RenderUtils.Str(ToolJson.Get(args, "file_path") ?? ToolJson.Get(args, "path")), theme, cwd);
        return theme.Fg("toolTitle", theme.Bold("read")) + " " + pathDisplay + FormatReadLineRange(args, theme);
    }

    private static List<string> TrimTrailingEmptyLines(IReadOnlyList<string> lines)
    {
        var end = lines.Count;
        while (end > 0 && lines[end - 1].Length == 0) end--;
        return lines.Take(end).ToList();
    }

    private static string ToPosixPath(string filePath) => filePath.Replace(Path.DirectorySeparatorChar, '/');

    private static CompactReadClassification? GetPiDocsClassification(string absolutePath)
    {
        var packageRoot = Path.GetDirectoryName(ReadmePath()) ?? "";
        var relativePath = ToolPaths.Relative(Path.GetFullPath(packageRoot), Path.GetFullPath(absolutePath));
        if (relativePath.Length == 0 || relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
            return null;

        var label = ToPosixPath(relativePath);
        if (label == "README.md" || label.StartsWith("docs/", StringComparison.Ordinal) || label.StartsWith("examples/", StringComparison.Ordinal))
            return new("docs", label);
        return null;
    }

    private static CompactReadClassification? GetCompactReadClassification(JsonNode? args, string cwd)
    {
        var rawPath = RenderUtils.Str(ToolJson.Get(args, "file_path") ?? ToolJson.Get(args, "path"));
        if (string.IsNullOrEmpty(rawPath)) return null;

        var absolutePath = ToolPaths.ResolveToCwd(rawPath, cwd);
        var fileName = Path.GetFileName(absolutePath);
        if (fileName == "SKILL.md")
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(absolutePath) ?? "");
            return new("skill", string.IsNullOrEmpty(parent) ? fileName : parent);
        }

        var docsClassification = GetPiDocsClassification(absolutePath);
        if (docsClassification is not null) return docsClassification;

        if (CompactResourceFileNames.Contains(fileName))
            return new("resource", ToolPaths.FormatPathRelativeToCwdOrAbsolute(absolutePath, cwd));

        return null;
    }

    private static string FormatCompactReadCall(CompactReadClassification classification, JsonNode? args, Theme theme)
    {
        var expandHint = theme.Fg("dim", $" ({KeybindingHints.KeyText("app.tools.expand")} to expand)");
        if (classification.Kind == "skill")
        {
            return theme.Fg("customMessageLabel", "\u001b[1m[skill]\u001b[22m ") +
                theme.Fg("customMessageText", classification.Label) +
                FormatReadLineRange(args, theme) +
                expandHint;
        }

        return theme.Fg("toolTitle", theme.Bold($"read {classification.Kind}")) + " " +
            theme.Fg("accent", classification.Label) +
            FormatReadLineRange(args, theme) +
            expandHint;
    }

    private static string FormatReadResult(JsonNode? args, JsonObject result, ToolRenderResultOptions options, Theme theme, bool showImages, bool isError)
    {
        if (!options.Expanded && !isError) return "";

        var rawPath = RenderUtils.Str(ToolJson.Get(args, "file_path") ?? ToolJson.Get(args, "path"));
        var output = RenderUtils.GetTextOutput(result, showImages);
        var lang = !isError && !string.IsNullOrEmpty(rawPath) ? Themes.GetLanguageFromPath(rawPath) : null;
        var renderedLines = lang is not null ? Themes.HighlightCode(RenderUtils.ReplaceTabs(output), lang) : output.Split('\n');
        var lines = TrimTrailingEmptyLines(renderedLines);
        var maxLines = options.Expanded ? lines.Count : 10;
        var displayLines = lines.Take(maxLines);
        var remaining = lines.Count - maxLines;
        var text = "\n" + string.Join("\n", displayLines.Select(line => lang is not null ? RenderUtils.ReplaceTabs(line) : theme.Fg("toolOutput", RenderUtils.ReplaceTabs(line))));
        if (remaining > 0) text += RenderUtils.MoreLinesHint(theme, $"\n... ({remaining} more lines,");

        var truncation = ToolJson.Get(ToolJson.Get(result, "details"), "truncation");
        if (ToolJson.Truthy(ToolJson.Get(truncation, "truncated")))
        {
            var maxBytes = ToolTruncate.FormatSize(ToolJson.GetNumber(truncation, "maxBytes") ?? ToolTruncate.DefaultMaxBytes);
            if (ToolJson.Truthy(ToolJson.Get(truncation, "firstLineExceedsLimit")))
                text += "\n" + theme.Fg("warning", $"[First line exceeds {maxBytes} limit]");
            else if (ToolJson.GetString(truncation, "truncatedBy") == "lines")
                text += "\n" + theme.Fg("warning", $"[Truncated: showing {ToolJson.Template(ToolJson.Get(truncation, "outputLines"))} of {ToolJson.Template(ToolJson.Get(truncation, "totalLines"))} lines ({ToolJson.Template(ToolJson.Get(truncation, "maxLines") ?? JsonValue.Create(ToolTruncate.DefaultMaxLines))} line limit)]");
            else
                text += "\n" + theme.Fg("warning", $"[Truncated: {ToolJson.Template(ToolJson.Get(truncation, "outputLines"))} lines shown ({maxBytes} limit)]");
        }
        return text;
    }

    public static ToolRenderers Renderers { get; } = new(
        RenderCall: (args, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            var classification = !context.Expanded ? GetCompactReadClassification(args, context.Cwd) : null;
            text.SetText(classification is not null ? FormatCompactReadCall(classification, args, theme) : FormatReadCall(args, theme, context.Cwd));
            return text;
        },
        RenderResult: (result, options, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatReadResult(context.Args, result, options, theme, context.ShowImages, context.IsError));
            return text;
        });
}
