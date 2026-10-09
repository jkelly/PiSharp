// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/tools/renderers/bash.ts.
// Presentation for the shell tools. Date.now() and setInterval go through Time (a TimeProvider) so tests can drive the clock; the
// interval's invalidate is posted to the synchronization context of the render that started it (the TUI loop).
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal static class BashRenderers
{
    private const int BashPreviewLines = 5;
    public const int BashUpdateThrottleMs = 100;

    /// <summary>The clock and timers of the elapsed-time display.</summary>
    public static TimeProvider Time { get; set; } = TimeProvider.System;

    private static double Now() => Time.GetUtcNow().ToUnixTimeMilliseconds();

    internal static string FormatDuration(double ms)
    {
        var seconds = ms / 1000;
        if (seconds < 60) return seconds.ToString("F1", CultureInfo.InvariantCulture) + "s";

        var totalSeconds = (long)Math.Floor(seconds);
        var minutes = totalSeconds / 60;
        var remainder = totalSeconds % 60;
        if (minutes < 60) return $"{minutes}m {remainder}s";

        return $"{minutes / 60}h {minutes % 60}m {remainder}s";
    }

    private static string FormatShellCall(JsonNode? args, string prompt)
    {
        var command = RenderUtils.Str(ToolJson.Get(args, "command"));
        var timeout = ToolJson.Get(args, "timeout");
        var timeoutSuffix = ToolJson.Truthy(timeout) ? theme.Fg("muted", $" (timeout {ToolJson.Template(timeout)}s)") : "";
        var commandDisplay = command is null ? RenderUtils.InvalidArgText(theme) : command.Length > 0 ? command : theme.Fg("toolOutput", "...");
        return theme.Fg("toolTitle", theme.Bold($"{prompt} {commandDisplay}")) + timeoutSuffix;
    }

    private static void RebuildBashResultRenderComponent(Container component, JsonObject result, ToolRenderResultOptions options, bool showImages,
        double? startedAt, double? endedAt, double? durationMs)
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

        // A final result's recorded duration wins: it is monotonic and survives reloads. The renderer's own clock is the
        // fallback for live progress and for results stored without one.
        if (!options.IsPartial && durationMs is { } recorded)
            component.AddChild(new Text("\n" + theme.Fg("muted", $"Took {FormatDuration(recorded)}"), 0, 0));
        else if (startedAt is { } started)
        {
            var label = options.IsPartial ? "Elapsed" : "Took";
            var endTime = endedAt ?? Now();
            component.AddChild(new Text("\n" + theme.Fg("muted", $"{label} {FormatDuration(endTime - started)}"), 0, 0));
        }
    }

    /// <summary>Shell renderers are shared by bash and powershell, which differ only in the prompt they display.</summary>
    public static ToolRenderers CreateShellRenderers(string prompt) => new(
        RenderCall: (args, _, context) =>
        {
            var state = context.State;
            if (context.ExecutionStarted && state.GetValueOrDefault("startedAt") is null)
            {
                state["startedAt"] = Now();
                state["endedAt"] = null;
            }
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatShellCall(args, prompt));
            return text;
        },
        RenderResult: (result, options, _, context) =>
        {
            var state = context.State;
            if (state.GetValueOrDefault("startedAt") is not null && options.IsPartial && state.GetValueOrDefault("interval") is null)
                state["interval"] = SetInterval(context.Invalidate, 1000);
            if (!options.IsPartial || context.IsError)
            {
                if (state.GetValueOrDefault("endedAt") is null) state["endedAt"] = Now();
                if (state.GetValueOrDefault("interval") is IDisposable interval)
                {
                    interval.Dispose();
                    state["interval"] = null;
                }
            }
            var component = context.LastComponent as Container ?? new Container();
            RebuildBashResultRenderComponent(component, result, options, context.ShowImages, state.GetValueOrDefault("startedAt") as double?,
                state.GetValueOrDefault("endedAt") as double?, context.DurationMs);
            component.Invalidate();
            return component;
        });

    /// <summary>setInterval: the callback runs on the synchronization context current at the call (the TUI loop), if any.</summary>
    private static IDisposable SetInterval(Action callback, double milliseconds)
    {
        var context = SynchronizationContext.Current;
        var period = TimeSpan.FromMilliseconds(milliseconds);
        return Time.CreateTimer(_ =>
        {
            if (context is not null) context.Post(_ => callback(), null);
            else callback();
        }, null, period, period);
    }
}

/// <summary>JavaScript String#trim/trimEnd.</summary>
internal static class TuiTextJs
{
    public static string Trim(string value) => TextUtils.JsTrim(value);
    public static string TrimEnd(string value) => TextUtils.JsTrimEnd(value);
}
