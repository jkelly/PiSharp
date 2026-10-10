// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/extensions/codemode/renderer.ts.
// The themed presentation of the codemode tool (PiSharp.Codemode.CodemodeRenderer is the unthemed row version for the native
// extension renderer contract). Details are CodemodeToolDetails: {calls: [{id, name, args, status, durationMs?, cost?, error?}],
// fullOutputPath?}.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>
/// Presentation for the codemode tool.
/// The call shows the script; the result lists the nested tool calls with their status as they run and the cost of its model
/// calls, followed by the script output without the "Script completed" header. Nested calls are not separate tool rows because
/// they never reach the model as tool calls.
/// </summary>
internal static partial class CodemodeRenderers
{
    public const string ToolName = "codemode";
    private const int CodePreviewLines = 10;
    private const int CallPreviewCount = 8;
    private const int OutputPreviewLines = 5;
    private const int CollapsedArgsChars = 80;

    // ECMAScript: \d is ASCII and $ (without the m flag) is the end of input.
    [GeneratedRegex(@"^Script (completed|failed)\nWall time [0-9.]+ seconds\nOutput:\n\z", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptHeader();

    private static string ExpandHint(Theme theme, int hidden, string noun) =>
        theme.Fg("muted", $"... ({hidden} more {noun},") + " " + KeybindingHints.KeyHint("app.tools.expand", "to expand") + theme.Fg("muted", ")");

    internal static string FormatDuration(double? ms)
    {
        if (ms is not { } value) return "";
        return value < 1000 ? ToolJson.JsNumber(Math.Floor(value + 0.5)) + "ms" : (value / 1000).ToString("F1", CultureInfo.InvariantCulture) + "s";
    }

    /// <summary>Cents for larger amounts, two significant digits for the fractions of a cent classifier calls cost.</summary>
    internal static string FormatCost(double cost) => "$" + (cost >= 0.01 ? cost.ToString("F2", CultureInfo.InvariantCulture) : ToPrecision2(cost));

    /// <summary>Number.prototype.toPrecision(2).</summary>
    private static string ToPrecision2(double value)
    {
        if (value == 0) return "0.0";
        var exponential = value.ToString("E1", CultureInfo.InvariantCulture);
        var e = exponential.IndexOf('E');
        var mantissa = exponential[..e];
        var exponent = int.Parse(exponential[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (exponent < -6 || exponent >= 2) return mantissa + "e" + (exponent >= 0 ? "+" : "-") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        return value.ToString("F" + (1 - exponent).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static string StatusIcon(JsonNode? call, Theme theme) => ToolJson.GetString(call, "status") switch
    {
        "running" => theme.Fg("warning", "…"),
        "ok" => theme.Fg("success", "✓"),
        "error" => theme.Fg("error", "✗"),
        "cancelled" => theme.Fg("muted", "⊘"),
        _ => "undefined"
    };

    private static string FormatCall(JsonNode? call, Theme theme, bool expanded)
    {
        var rawArgs = ToolJson.GetString(call, "args") ?? "";
        var args = !expanded && rawArgs.Length > CollapsedArgsChars ? rawArgs[..(CollapsedArgsChars - 3)] + "..." : rawArgs;
        var duration = FormatDuration(ToolJson.GetNumber(call, "durationMs"));
        var line = StatusIcon(call, theme) + " " + theme.Fg("toolTitle", ToolJson.GetString(call, "name") ?? "");
        if (args.Length > 0) line += " " + theme.Fg("muted", args);
        if (duration.Length > 0) line += " " + theme.Fg("dim", duration);
        if (ToolJson.Truthy(ToolJson.Get(call, "cost"))) line += " " + theme.Fg("dim", FormatCost(ToolJson.GetNumber(call, "cost")!.Value));
        if (expanded && ToolJson.Truthy(ToolJson.Get(call, "error")))
            line += "\n    " + theme.Fg("error", ToolJson.Template(ToolJson.Get(call, "error")).Replace("\n", "\n    ", StringComparison.Ordinal));
        return line;
    }

    public static ToolRenderers Renderers { get; } = new(
        RenderCall: (args, theme, context) =>
        {
            // The code includes the `// @options:` line, so options show as part of the script.
            var code = RenderUtils.Str(ToolJson.Get(args, "code"));
            var title = theme.Fg("toolTitle", theme.Bold("codemode"));
            var component = context.LastComponent as Container ?? new Container();
            component.Clear();
            if (code is null)
            {
                component.AddChild(new Text(title + " " + theme.Fg("error", "[invalid arg]"), 0, 0));
                return component;
            }
            component.AddChild(new Text(title, 0, 0));
            if (code.Length > 0)
            {
                var highlighted = string.Join("\n", Themes.HighlightCode(RenderUtils.ReplaceTabs(TextUtils.JsTrimEnd(code.Replace("\r", "", StringComparison.Ordinal))), "javascript"));
                component.AddChild(context.Expanded
                    ? new Text(highlighted, 0, 0)
                    : new VisualLinePreview(new VisualLinePreviewOptions(highlighted, CodePreviewLines, VisualTruncateKeep.Start, hidden => ExpandHint(theme, hidden, "lines"))));
            }
            return component;
        },
        RenderResult: (result, options, theme, context) =>
        {
            var component = context.LastComponent as Container ?? new Container();
            component.Clear();
            var details = ToolJson.Get(result, "details");
            var calls = (ToolJson.Get(details, "calls") as JsonArray)?.ToList() ?? [];
            if (calls.Count > 0)
            {
                var shown = options.Expanded ? calls : calls.Skip(Math.Max(0, calls.Count - CallPreviewCount)).ToList();
                var lines = shown.Select(call => FormatCall(call, theme, options.Expanded)).ToList();
                if (shown.Count < calls.Count)
                    lines.Insert(0, theme.Fg("muted", $"... ({calls.Count - shown.Count} earlier calls,") + " " + KeybindingHints.KeyHint("app.tools.expand", "to expand") + theme.Fg("muted", ")"));
                // Collapsed rows hide earlier calls, so the total covers every call.
                var priced = calls.Where(call => ToolJson.Truthy(ToolJson.Get(call, "cost"))).ToList();
                if (priced.Count > 1)
                {
                    var total = priced.Sum(call => ToolJson.GetNumber(call, "cost") ?? 0);
                    lines.Add(theme.Fg("muted", $"Model calls: {FormatCost(total)}"));
                }
                component.AddChild(new Spacer(1));
                component.AddChild(new Text(string.Join("\n", lines), 0, 0));
            }

            // Drop the "Script completed\nWall time ...\nOutput:\n" header. Rejected input (invalid options) has no header.
            var content = ToolJson.Get(result, "content") as JsonArray ?? [];
            var first = content.Count > 0 ? content[0] : null;
            var hasHeader = ToolJson.GetString(first, "type") == "text" && ScriptHeader().IsMatch(ToolJson.Template(ToolJson.Get(first, "text")));
            var outputSource = new JsonObject
            {
                ["content"] = new JsonArray([.. (hasHeader ? content.Skip(1) : content).Select(block => block?.DeepClone())]),
            };
            var output = options.IsPartial ? "" : TextUtils.JsTrim(RenderUtils.GetTextOutput(outputSource, context.ShowImages));
            if (output.Length > 0)
            {
                var color = context.IsError ? "error" : "toolOutput";
                var styled = string.Join("\n", RenderUtils.ReplaceTabs(output).Split('\n').Select(line => theme.Fg(color, line)));
                component.AddChild(new Spacer(1));
                if (options.Expanded) component.AddChild(new Text(styled, 0, 0));
                else
                {
                    // Limit wrapped lines, not logical ones: script output is often one long JSON line.
                    component.AddChild(new VisualLinePreview(new VisualLinePreviewOptions(styled, OutputPreviewLines, VisualTruncateKeep.Start,
                        hidden => ExpandHint(theme, hidden, "lines"))));
                    // The collapsed preview hides the truncation notice at the end, so name the file here.
                    var fullOutputPath = ToolJson.Get(details, "fullOutputPath");
                    if (ToolJson.Truthy(fullOutputPath))
                        component.AddChild(new Text(theme.Fg("muted", $"Full output: {ToolJson.Template(fullOutputPath)}"), 0, 0));
                }
            }
            return component;
        });
}
