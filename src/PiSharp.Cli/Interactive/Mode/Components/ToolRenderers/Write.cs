// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/tools/renderers/write.ts.
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Presentation for the write tool.</summary>
internal static class WriteRenderers
{
    private sealed class WriteHighlightCache
    {
        public string? RawPath;
        public string Lang = "";
        public string RawContent = "";
        public List<string> NormalizedLines = [];
        public List<string> HighlightedLines = [];
    }

    private sealed class WriteCallRenderComponent() : Text("", 0, 0)
    {
        public WriteHighlightCache? Cache;
    }

    private const int WritePartialFullHighlightLines = 50;

    private static string HighlightSingleLine(string line, string lang)
    {
        var highlighted = Themes.HighlightCode(line, lang);
        return highlighted.Count > 0 ? highlighted[0] : "";
    }

    private static void RefreshWriteHighlightPrefix(WriteHighlightCache cache)
    {
        var prefixCount = Math.Min(WritePartialFullHighlightLines, cache.NormalizedLines.Count);
        if (prefixCount == 0) return;
        var prefixSource = string.Join("\n", cache.NormalizedLines.Take(prefixCount));
        var prefixHighlighted = Themes.HighlightCode(prefixSource, cache.Lang);
        while (cache.HighlightedLines.Count < prefixCount) cache.HighlightedLines.Add("");
        for (var i = 0; i < prefixCount; i++)
            cache.HighlightedLines[i] = i < prefixHighlighted.Count ? prefixHighlighted[i] : HighlightSingleLine(cache.NormalizedLines[i], cache.Lang);
    }

    private static WriteHighlightCache? RebuildWriteHighlightCacheFull(string? rawPath, string fileContent)
    {
        var lang = !string.IsNullOrEmpty(rawPath) ? Themes.GetLanguageFromPath(rawPath) : null;
        if (lang is null) return null;
        var displayContent = RenderUtils.NormalizeDisplayText(fileContent);
        var normalized = RenderUtils.ReplaceTabs(displayContent);
        return new WriteHighlightCache
        {
            RawPath = rawPath,
            Lang = lang,
            RawContent = fileContent,
            NormalizedLines = [.. normalized.Split('\n')],
            HighlightedLines = [.. Themes.HighlightCode(normalized, lang)],
        };
    }

    private static WriteHighlightCache? UpdateWriteHighlightCacheIncremental(WriteHighlightCache? cache, string? rawPath, string fileContent)
    {
        var lang = !string.IsNullOrEmpty(rawPath) ? Themes.GetLanguageFromPath(rawPath) : null;
        if (lang is null) return null;
        if (cache is null) return RebuildWriteHighlightCacheFull(rawPath, fileContent);
        if (cache.Lang != lang || cache.RawPath != rawPath) return RebuildWriteHighlightCacheFull(rawPath, fileContent);
        if (!fileContent.StartsWith(cache.RawContent, StringComparison.Ordinal)) return RebuildWriteHighlightCacheFull(rawPath, fileContent);
        if (fileContent.Length == cache.RawContent.Length) return cache;

        var deltaRaw = fileContent[cache.RawContent.Length..];
        var deltaDisplay = RenderUtils.NormalizeDisplayText(deltaRaw);
        var deltaNormalized = RenderUtils.ReplaceTabs(deltaDisplay);
        cache.RawContent = fileContent;
        if (cache.NormalizedLines.Count == 0)
        {
            cache.NormalizedLines.Add("");
            cache.HighlightedLines.Add("");
        }

        var segments = deltaNormalized.Split('\n');
        var lastIndex = cache.NormalizedLines.Count - 1;
        cache.NormalizedLines[lastIndex] += segments[0];
        // highlightedLines may be shorter than normalizedLines when highlighting dropped lines; JS assigns past the end.
        while (cache.HighlightedLines.Count <= lastIndex) cache.HighlightedLines.Add("");
        cache.HighlightedLines[lastIndex] = HighlightSingleLine(cache.NormalizedLines[lastIndex], cache.Lang);
        for (var i = 1; i < segments.Length; i++)
        {
            cache.NormalizedLines.Add(segments[i]);
            cache.HighlightedLines.Add(HighlightSingleLine(segments[i], cache.Lang));
        }
        RefreshWriteHighlightPrefix(cache);
        return cache;
    }

    private static List<string> TrimTrailingEmptyLines(IReadOnlyList<string> lines)
    {
        var end = lines.Count;
        while (end > 0 && lines[end - 1].Length == 0) end--;
        return lines.Take(end).ToList();
    }

    private static string FormatWriteCall(JsonNode? args, ToolRenderResultOptions options, Theme theme, WriteHighlightCache? cache, string cwd)
    {
        var rawPath = RenderUtils.Str(ToolJson.Get(args, "file_path") ?? ToolJson.Get(args, "path"));
        var fileContent = RenderUtils.Str(ToolJson.Get(args, "content"));
        var pathDisplay = RenderUtils.RenderToolPath(rawPath, theme, cwd);
        var text = theme.Fg("toolTitle", theme.Bold("write")) + " " + pathDisplay;

        if (fileContent is null)
            text += "\n\n" + theme.Fg("error", "[invalid content arg - expected string]");
        else if (fileContent.Length > 0)
        {
            var lang = !string.IsNullOrEmpty(rawPath) ? Themes.GetLanguageFromPath(rawPath) : null;
            IReadOnlyList<string> renderedLines = lang is not null
                ? (IReadOnlyList<string>?)cache?.HighlightedLines ?? Themes.HighlightCode(RenderUtils.ReplaceTabs(RenderUtils.NormalizeDisplayText(fileContent)), lang)
                : RenderUtils.NormalizeDisplayText(fileContent).Split('\n');
            var lines = TrimTrailingEmptyLines(renderedLines);
            var totalLines = lines.Count;
            var maxLines = options.Expanded ? lines.Count : 10;
            var displayLines = lines.Take(maxLines);
            var remaining = lines.Count - maxLines;
            text += "\n\n" + string.Join("\n", displayLines.Select(line => lang is not null ? line : theme.Fg("toolOutput", RenderUtils.ReplaceTabs(line))));
            if (remaining > 0) text += RenderUtils.MoreLinesHint(theme, $"\n... ({remaining} more lines, {totalLines} total,");
        }

        return text;
    }

    private static string? FormatWriteResult(JsonObject result, bool isError, Theme theme)
    {
        if (!isError) return null;
        var content = ToolJson.Get(result, "content") as JsonArray ?? [];
        var output = string.Join("\n", content.Where(c => ToolJson.GetString(c, "type") == "text").Select(c => ToolJson.TruthyString(ToolJson.Get(c, "text"))));
        if (output.Length == 0) return null;
        return "\n" + theme.Fg("error", output);
    }

    public static ToolRenderers Renderers { get; } = new(
        RenderCall: (args, theme, context) =>
        {
            var rawPath = RenderUtils.Str(ToolJson.Get(args, "file_path") ?? ToolJson.Get(args, "path"));
            var fileContent = RenderUtils.Str(ToolJson.Get(args, "content"));
            var component = context.LastComponent as WriteCallRenderComponent ?? new WriteCallRenderComponent();
            if (fileContent is not null)
            {
                component.Cache = context.ArgsComplete
                    ? RebuildWriteHighlightCacheFull(rawPath, fileContent)
                    : UpdateWriteHighlightCacheIncremental(component.Cache, rawPath, fileContent);
            }
            else component.Cache = null;
            component.SetText(FormatWriteCall(args, new ToolRenderResultOptions(context.Expanded, context.IsPartial), theme, component.Cache, context.Cwd));
            return component;
        },
        RenderResult: (result, _, theme, context) =>
        {
            var output = FormatWriteResult(result, context.IsError, theme);
            if (output is null)
            {
                var container = context.LastComponent as Container ?? new Container();
                container.Clear();
                return container;
            }
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(output);
            return text;
        });
}
