// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): coding-agent/src/core/tools/renderers/bash.ts.
// Presentation for the shell tools. The elapsed-time display is the shared tool timer of render-utils.ts (RenderUtils.StartToolTimer
// and ToolTimerText); its clock is RenderUtils.Time, so tests can drive it.
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal static class BashRenderers
{
    private const int BashPreviewLines = 5;
    public const int BashUpdateThrottleMs = 100;

    /// <summary>The clock and timers of the elapsed-time display (the shared <see cref="RenderUtils.Time"/>).</summary>
    public static TimeProvider Time { get => RenderUtils.Time; set => RenderUtils.Time = value; }

    private static string FormatShellCall(JsonNode? args, string prompt)
    {
        var command = RenderUtils.Str(ToolJson.Get(args, "command"));
        var timeout = ToolJson.Get(args, "timeout");
        var timeoutSuffix = ToolJson.Truthy(timeout) ? theme.Fg("muted", $" (timeout {ToolJson.Template(timeout)}s)") : "";
        var commandDisplay = command is null ? RenderUtils.InvalidArgText(theme) : command.Length > 0 ? command : theme.Fg("toolOutput", "...");
        return theme.Fg("toolTitle", theme.Bold($"{prompt} {commandDisplay}")) + timeoutSuffix;
    }

    private static void RebuildBashResultRenderComponent(Container component, JsonObject result, ToolRenderResultOptions options, bool showImages,
        string? timerText)
    {
        component.Clear();

        var output = TuiTextJs.Trim(RenderUtils.GetTextOutput(result, showImages));
        var details = ToolJson.Get(result, "details");
        var truncation = ToolJson.Get(details, "truncation");
        var truncated = ToolJson.Truthy(ToolJson.Get(truncation, "truncated"));
        var fullOutputPath = ToolJson.Get(details, "fullOutputPath");
        var fullOutputPathText = ToolJson.Truthy(fullOutputPath) ? ToolJson.Template(fullOutputPath) : null;
        if (!options.IsPartial && truncated && fullOutputPathText is not null && output.EndsWith(']'))
        {
            var footerStart = output.LastIndexOf("\n\n[", StringComparison.Ordinal);
            if (footerStart != -1 && output[footerStart..].Contains(fullOutputPathText, StringComparison.Ordinal))
                output = TuiTextJs.TrimEnd(output[..footerStart]);
        }

        if (output.Length > 0)
        {
            var styledOutput = string.Join("\n", output.Split('\n').Select(line => theme.Fg("toolOutput", line)));

            if (options.Expanded) component.AddChild(new Text("\n" + styledOutput, 0, 0));
            else
            {
                component.AddChild(new Spacer(1));
                component.AddChild(new VisualLinePreview(new VisualLinePreviewOptions(styledOutput, BashPreviewLines, VisualTruncateKeep.End,
                    hidden => theme.Fg("muted", $"... ({hidden} earlier lines,") + " " + KeybindingHints.KeyHint("app.tools.expand", "to expand") + theme.Fg("muted", ")"))));
            }
        }

        if (truncated || fullOutputPathText is not null)
        {
            var warnings = new List<string>();
            if (fullOutputPathText is not null) warnings.Add($"Full output: {fullOutputPathText}");
            if (truncated)
            {
                if (ToolJson.GetString(truncation, "truncatedBy") == "lines")
                    warnings.Add($"Truncated: showing {ToolJson.Template(ToolJson.Get(truncation, "outputLines"))} of {ToolJson.Template(ToolJson.Get(truncation, "totalLines"))} lines");
                else
                    warnings.Add($"Truncated: {ToolJson.Template(ToolJson.Get(truncation, "outputLines"))} lines shown ({ToolTruncate.FormatSize(ToolJson.GetNumber(truncation, "maxBytes") ?? ToolTruncate.DefaultMaxBytes)} limit)");
            }
            component.AddChild(new Text("\n" + theme.Fg("warning", $"[{string.Join(". ", warnings)}]"), 0, 0));
        }

        if (timerText is not null) component.AddChild(new Text("\n" + theme.Fg("muted", timerText), 0, 0));
    }

    /// <summary>Shell renderers are shared by bash and powershell, which differ only in the prompt they display.</summary>
    public static ToolRenderers CreateShellRenderers(string prompt) => new(
        RenderCall: (args, _, context) =>
        {
            RenderUtils.StartToolTimer(context);
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatShellCall(args, prompt));
            return text;
        },
        RenderResult: (result, options, _, context) =>
        {
            var timerText = RenderUtils.ToolTimerText(context, options.IsPartial);
            var component = context.LastComponent as Container ?? new Container();
            RebuildBashResultRenderComponent(component, result, options, context.ShowImages, timerText);
            component.Invalidate();
            return component;
        });
}

/// <summary>JavaScript String#trim/trimEnd.</summary>
internal static class TuiTextJs
{
    public static string Trim(string value) => TextUtils.JsTrim(value);
    public static string TrimEnd(string value) => TextUtils.JsTrimEnd(value);
}
