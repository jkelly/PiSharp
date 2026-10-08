// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/codemode/renderer.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Codemode;

/// <summary>Presentation of the codemode tool: the call shows the script; the result lists the nested calls with their status
/// and the cost of model calls, followed by the output without the "Script completed" header. Rows are plain text: the
/// native renderer contract carries no theme, so colours and syntax highlighting are left to the host.</summary>
public static partial class CodemodeRenderer
{
    private const int CodePreviewLines = 10;
    private const int CallPreviewCount = 8;
    private const int OutputPreviewLines = 5;
    private const int CollapsedArgsChars = 80;
    private const string ExpandKey = "ctrl+o";
    [GeneratedRegex(@"^Script (completed|failed)\nWall time [\d.]+ seconds\nOutput:\n$")] private static partial Regex ScriptHeader();

    public static ExtensionToolRenderers Renderers { get; } = new(null,
        (context, width, _) => Task.FromResult(Rows(RenderCall(context.Arguments, context.Expanded), width)),
        (result, context, width, _) => Task.FromResult(Rows(RenderResult(result, context.Expanded, context.IsPartial, context.ShowImages), width)));

    private static string ExpandHint(int hidden, string noun) => $"... ({hidden} more {noun}, {ExpandKey} to expand)";

    internal static string FormatDuration(double? ms) => ms is not { } value ? "" :
        value < 1000 ? $"{Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}ms" : $"{(value / 1000).ToString("F1", CultureInfo.InvariantCulture)}s";

    /// <summary>Cents for larger amounts, two significant digits for fractions of a cent.</summary>
    internal static string FormatCost(double cost) => "$" + (cost >= 0.01 ? cost.ToString("F2", CultureInfo.InvariantCulture) : ToPrecision2(cost));

    private static string ToPrecision2(double value)
    {
        if (value == 0) return "0.0";
        var exponent = (int)Math.Floor(Math.Log10(Math.Abs(value)));
        if (exponent < -6) return value.ToString("0.0e+0", CultureInfo.InvariantCulture);
        var decimals = Math.Max(0, 1 - exponent);
        return Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    private static string Icon(string status) => status switch { "running" => "…", "ok" => "✓", "error" => "✗", _ => "⊘" };

    private static string FormatCall(JsonElement call, bool expanded)
    {
        string Text(string name) => call.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        double? Number(string name) => call.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        var args = Text("args");
        if (!expanded && args.Length > CollapsedArgsChars) args = args[..(CollapsedArgsChars - 3)] + "...";
        var line = $"{Icon(Text("status"))} {Text("name")}";
        if (args.Length > 0) line += " " + args;
        var duration = FormatDuration(Number("durationMs"));
        if (duration.Length > 0) line += " " + duration;
        if (Number("cost") is { } cost && cost != 0) line += " " + FormatCost(cost);
        if (expanded && Text("error") is { Length: > 0 } error) line += "\n    " + error.Replace("\n", "\n    ", StringComparison.Ordinal);
        return line;
    }

    /// <summary>The call rows: the title and the script (with its options line), the first lines unless expanded.</summary>
    public static IReadOnlyList<string> RenderCall(JsonData arguments, bool expanded)
    {
        var lines = new List<string>();
        if (arguments.Value.ValueKind != JsonValueKind.Object || !arguments.Value.TryGetProperty("code", out var code) ||
            code.ValueKind is not (JsonValueKind.String or JsonValueKind.Undefined))
        { lines.Add("codemode [invalid arg]"); return lines; }
        lines.Add("codemode");
        var source = code.ValueKind == JsonValueKind.String ? code.GetString()!.Replace("\r", "", StringComparison.Ordinal).TrimEnd().Replace("\t", "   ", StringComparison.Ordinal) : "";
        if (source.Length == 0) return lines;
        var codeLines = source.Split('\n');
        if (expanded || codeLines.Length <= CodePreviewLines) lines.AddRange(codeLines);
        else { lines.AddRange(codeLines.Take(CodePreviewLines)); lines.Add(ExpandHint(codeLines.Length - CodePreviewLines, "lines")); }
        return lines;
    }

    /// <summary>The result rows: nested calls (the last eight unless expanded), the total cost of model calls, and the output.</summary>
    public static IReadOnlyList<string> RenderResult(JsonData result, bool expanded, bool isPartial, bool showImages = false)
    {
        var lines = new List<string>();
        var value = result.Value;
        var calls = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("calls", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];
        if (calls.Count > 0)
        {
            var shown = expanded ? calls : calls.Skip(Math.Max(0, calls.Count - CallPreviewCount)).ToList();
            lines.Add("");
            if (shown.Count < calls.Count) lines.Add($"... ({calls.Count - shown.Count} earlier calls, {ExpandKey} to expand)");
            lines.AddRange(shown.SelectMany(call => FormatCall(call, expanded).Split('\n')));
            var priced = calls.Where(call => call.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number && cost.GetDouble() != 0).ToList();
            if (priced.Count > 1) lines.Add("Model calls: " + FormatCost(priced.Sum(call => call.GetProperty("cost").GetDouble())));
        }
        if (isPartial) return lines;
        var blocks = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().ToList() : [];
        if (blocks.Count > 0 && Block(blocks[0], out var first) && ScriptHeader().IsMatch(first)) blocks.RemoveAt(0);
        var text = new StringBuilder();
        foreach (var block in blocks)
        {
            if (Block(block, out var part)) { if (text.Length > 0) text.Append('\n'); text.Append(part); }
            else if (!showImages && block.TryGetProperty("type", out var type) && type.GetString() == "image")
            { if (text.Length > 0) text.Append('\n'); text.Append($"[Image: {(block.TryGetProperty("mimeType", out var mime) ? mime.GetString() : "image")}]"); }
        }
        var output = Js.Trim(text.ToString());
        if (output.Length == 0) return lines;
        lines.Add("");
        var outputLines = output.Replace("\t", "   ", StringComparison.Ordinal).Split('\n');
        if (expanded || outputLines.Length <= OutputPreviewLines) lines.AddRange(outputLines);
        else
        {
            lines.AddRange(outputLines.Take(OutputPreviewLines)); lines.Add(ExpandHint(outputLines.Length - OutputPreviewLines, "lines"));
            if (value.TryGetProperty("details", out var resultDetails) && resultDetails.ValueKind == JsonValueKind.Object &&
                resultDetails.TryGetProperty("fullOutputPath", out var path) && path.ValueKind == JsonValueKind.String)
                lines.Add("Full output: " + path.GetString());
        }
        return lines;
        static bool Block(JsonElement block, out string text)
        {
            text = "";
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type) || type.GetString() != "text" ||
                !block.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String) return false;
            text = value.GetString()!; return true;
        }
    }

    /// <summary>Lines wrapped to <paramref name="width"/> terminal cells (one cell per code point, two for wide characters).</summary>
    private static ExtensionCustomComponentRows Rows(IReadOnlyList<string> lines, int width)
    {
        width = Math.Max(1, width);
        var rows = ImmutableArray.CreateBuilder<string>(); var widths = ImmutableArray.CreateBuilder<int>();
        foreach (var line in lines)
        {
            var row = new StringBuilder(); var cells = 0;
            foreach (var rune in line.EnumerateRunes())
            {
                var cell = Wide(rune) ? 2 : 1;
                if (cells + cell > width && cells > 0) { rows.Add(row.ToString()); widths.Add(cells); row.Clear(); cells = 0; }
                row.Append(rune.ToString()); cells += cell;
            }
            rows.Add(row.ToString()); widths.Add(cells);
        }
        return new(rows.ToImmutable(), widths.ToImmutable());
        static bool Wide(Rune rune) => rune.Value is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or
            >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD;
    }
}
