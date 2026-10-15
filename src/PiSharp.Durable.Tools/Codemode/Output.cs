// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/codemode/output.ts.
// - Text and image items are pi-ai content blocks as durable wire JSON: {type: "text", text} and {type: "image", data, mimeType}.
// - A script's result is PiSharp.Codemode's CodemodeResult: its value is JSON (null for undefined) and its output items CodemodeTextOutput and
//   CodemodeImageOutput. `JSON.stringify(value)` of the returned value is the durable JsonValue's JSON text, which prints JSON as
//   JavaScript does.
// - String lengths, slices and splits count UTF-16 code units, as JavaScript's do. `atob` is Convert.FromBase64String; both reject data that
//   is not base64.
// - An OutputFile writes bytes; the full output text is written as UTF-8, as the environment writes a string.
// - saveImages labels images in parallel with Promise.all; the labels and their numbers depend only on the order of first appearance, so
//   here they are written one after another in that order.
using System.Globalization;
using System.Text;
using PiSharp.Codemode;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Tools;

/// <summary>Writes a file of the result under a fixed name: its path, or why it could not be written (upstream <c>OutputFile</c>).</summary>
internal delegate Task<(string? Path, string? Error)> CodemodeOutputFile(string name, byte[] data);

/// <summary>
/// A script's result as the model reads it, copied from the coding agent's code mode (upstream <c>output.ts</c>): a "Script completed/failed"
/// header with the wall time, the script's text items (<c>==&gt; text N/M &lt;==</c> when there are several), its <c>console</c> lines in one
/// <c>&lt;console_output&gt;</c> block, the returned value, and on failure the error with the calls already made. Text past the token budget
/// is cut to its start and end, with the complete text in a temp file; images are saved to temp files too, with their path next to them,
/// because a script has no other way to hand their bytes on.
/// </summary>
internal static class CodemodeOutput
{
    /// <summary>Lines of the result besides the script's text: the header, markers, notes.</summary>
    private const int LineMargin = 100;
    /// <summary>Default token budget for script output (upstream <c>DEFAULT_MAX_OUTPUT_TOKENS</c>).</summary>
    public const int DefaultMaxOutputTokens = 10_000;
    /// <summary>Characters per token when estimating.</summary>
    private const int CharsPerToken = 4;

    /// <summary>File extensions of the image types <c>image()</c> accepts.</summary>
    private static readonly Dictionary<string, string> ImageExtensions = new(StringComparer.Ordinal)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
    };

    private abstract record Item;
    private sealed record TextItem(string Text) : Item;
    private sealed record ImageItem(string Data, string MimeType) : Item;

    /// <summary>The tool's output limits, which the Harness enforces by cutting without a spill file: results stay inside them.</summary>
    public sealed record Limits(int MaxBytes, int MaxLines);

    /// <summary>Like the script's <c>text()</c>: strings as is, other values as compact JSON.</summary>
    private static string ValueText(JsonData value) =>
        value.Value.ValueKind == System.Text.Json.JsonValueKind.String ? value.Value.GetString()! : JsonValue.Parse(value.ToString()).ToJson();

    /// <summary>
    /// Lay out the script's output so the model can tell items apart: with more than one text item, each starts with a <c>==&gt; text N/M
    /// &lt;==</c> line; <c>console</c> lines follow all other output in one <c>&lt;console_output&gt;</c> block.
    /// </summary>
    private static List<Item> FormatItems(IReadOnlyList<CodemodeOutputItem> output)
    {
        var total = output.Count(item => item is CodemodeTextOutput { Console: false });
        var items = new List<Item>();
        var consoleLines = new List<string>();
        var index = 0;
        foreach (var item in output)
        {
            switch (item)
            {
                case CodemodeImageOutput image:
                    items.Add(new ImageItem(image.Data, image.MimeType));
                    break;
                case CodemodeTextOutput { Console: true } line:
                    consoleLines.Add(line.Text);
                    break;
                case CodemodeTextOutput text:
                    index++;
                    items.Add(new TextItem(total > 1 ? $"==> text {index}/{total} <==\n{text.Text}" : text.Text));
                    break;
            }
        }
        if (consoleLines.Count > 0) items.Add(new TextItem($"<console_output>\n{string.Join("\n", consoleLines)}\n</console_output>"));
        return items;
    }

    /// <summary>Join adjacent text items into one, each part starting on its own line.</summary>
    private static List<Item> JoinAdjacentText(IReadOnlyList<Item> items)
    {
        var joined = new List<Item>();
        var parts = new StringBuilder();
        var any = false;
        var lastPart = "";
        void Flush()
        {
            if (any) joined.Add(new TextItem(parts.ToString()));
            parts.Clear();
            any = false;
            lastPart = "";
        }
        foreach (var item in items)
        {
            if (item is not TextItem text)
            {
                Flush();
                joined.Add(item);
                continue;
            }
            if (lastPart.Length != 0 && !lastPart.EndsWith('\n'))
            {
                parts.Append('\n');
                lastPart = "\n";
            }
            parts.Append(text.Text);
            any = true;
            if (text.Text.Length != 0) lastPart = text.Text;
        }
        Flush();
        return joined;
    }

    private static int LineCount(string text)
    {
        var count = 1;
        foreach (var c in text) if (c == '\n') count++;
        return count;
    }

    /// <summary>
    /// Apply the token budget: when the combined text exceeds it, the text items become one item that keeps the start and end of the text,
    /// and images follow it. The complete text is written to a temp file.
    /// </summary>
    private static async Task<(List<Item> Items, string? FullOutputPath)> Truncate(IReadOnlyList<Item> items, long maxTokens, Limits limits,
        CodemodeOutputFile write)
    {
        var texts = items.OfType<TextItem>().Select(item => item.Text).ToList();
        var combined = string.Join("\n", texts);
        // Within the limits too: a UTF-16 unit is at most 3 UTF-8 bytes, and the rest of the result is a few short lines.
        var budget = (int)Math.Min(maxTokens * CharsPerToken, limits.MaxBytes / 4);
        var lineBudget = (limits.MaxLines - LineMargin) / 2;
        var lines = LineCount(combined);
        if (texts.Count == 0 || (combined.Length <= budget && lines <= 2 * lineBudget)) return ([.. items], null);
        var headChars = budget / 2;
        var tailChars = budget - headChars;
        var head = string.Join("\n", combined[..Math.Min(headChars, combined.Length)].Split('\n').Take(lineBudget));
        var tail = tailChars > 0 ? string.Join("\n", TakeLast(combined[Math.Max(0, combined.Length - tailChars)..].Split('\n'), lineBudget)) : "";
        var removed = combined.Length - head.Length - tail.Length;
        var text = $"Warning: truncated output (original token count: {Ceil(combined.Length)})\nTotal output lines: {lines}\n\n{head}…{Ceil(removed)} tokens truncated…{tail}";
        var spilled = await write("output.txt", Encoding.UTF8.GetBytes(combined)).ConfigureAwait(false);
        text += spilled.Path is { } path
            ? $"\n\n[Full output: {path} (read with offset/limit)]"
            : $"\n\n[Could not save the full output: {spilled.Error}]";
        return ([new TextItem(text), .. items.OfType<ImageItem>()], spilled.Path);
    }

    private static string Ceil(int chars) => ((long)Math.Ceiling(chars / (double)CharsPerToken)).ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<string> TakeLast(string[] parts, int count) => count <= 0 ? [] : parts.Skip(Math.Max(0, parts.Length - count));

    /// <summary>Save each image to a file and put a text item with its path before it; an image shown twice is saved once.</summary>
    private static async Task<List<Item>> SaveImages(IReadOnlyList<Item> items, CodemodeOutputFile write)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<Item>();
        foreach (var item in items)
        {
            if (item is not ImageItem image)
            {
                result.Add(item);
                continue;
            }
            if (!labels.TryGetValue(image.Data, out var label))
            {
                var index = labels.Count + 1;
                var bytes = Convert.FromBase64String(image.Data);
                var kind = $"{image.MimeType}, {bytes.Length} bytes";
                // A failed write must not discard the result of a script whose calls already ran: it becomes part of the label.
                var saved = await write($"image-{index}{ImageExtensions.GetValueOrDefault(image.MimeType, ".bin")}", bytes).ConfigureAwait(false);
                label = saved.Path is { } path ? $"[Image saved to {path} ({kind})]" : $"[Image ({kind}) could not be saved: {saved.Error}]";
                labels[image.Data] = label;
            }
            result.Add(new TextItem(label));
            result.Add(item);
        }
        return result;
    }

    private static string FormatError(CodemodeError error, IReadOnlyList<(string Name, string Status)> calls)
    {
        var head = error.Kind switch
        {
            CodemodeErrorKind.Script => error.Stack ?? $"{error.Name ?? "Error"}: {error.Message}",
            CodemodeErrorKind.Timeout => $"Script timed out: {error.Message}",
            CodemodeErrorKind.Aborted => $"Script aborted: {error.Message}",
            _ => $"Script sandbox failed: {error.Message}",
        };
        var summary = calls.Count == 0
            ? "No tool calls were made."
            : $"Tool calls made before the failure (they are not undone): {string.Join(", ", calls.Select(call => $"{call.Name} ({call.Status})"))}";
        return $"{head}\n\n{summary}";
    }

    /// <summary>pi-ai content JSON of an item.</summary>
    private static JsonValue ToJson(Item item) => item switch
    {
        TextItem text => JsonValue.Object([KeyValuePair.Create("type", (JsonValue)"text"), KeyValuePair.Create("text", (JsonValue)text.Text)]),
        ImageItem image => JsonValue.Object([KeyValuePair.Create("type", (JsonValue)"image"), KeyValuePair.Create("data", (JsonValue)image.Data),
            KeyValuePair.Create("mimeType", (JsonValue)image.MimeType)]),
        _ => throw new InvalidOperationException("unknown item"),
    };

    /// <summary>The tool result's content for a script's result, and the path of the complete text when it was cut (upstream
    /// <c>formatResult</c>). <paramref name="generatedImages"/>: images <c>models.generateImages()</c> returned; when the script showed none,
    /// the result says so.</summary>
    public static async Task<(JsonArrayValue Content, string? FullOutputPath)> FormatResult(CodemodeResult result, int generatedImages, double wallMs,
        long maxOutputTokens, IReadOnlyList<(string Name, string Status)> calls, Limits limits, CodemodeOutputFile write)
    {
        var output = new List<CodemodeOutputItem>(result.Output.IsDefault ? [] : result.Output);
        if (result.Ok && result.Value is { } value) output.Add(new CodemodeTextOutput(ValueText(value)));
        var items = FormatItems(output);
        if (!result.Ok) items.Add(new TextItem($"Script error:\n{FormatError(result.Error ?? new CodemodeError(CodemodeErrorKind.Sandbox, "unknown error"), calls)}"));
        if (generatedImages > 0 && !items.Any(item => item is ImageItem))
        {
            items.Add(new TextItem($"Note: models.generateImages() returned {generatedImages} image{(generatedImages == 1 ? "" : "s")} that the script did not show. Show each image block of result.output with image(block)."));
        }
        var truncated = await Truncate(JoinAdjacentText(items), maxOutputTokens, limits, write).ConfigureAwait(false);
        // After truncation, which joins the text items and moves images after them, so each path stays next to its image.
        var content = JoinAdjacentText(await SaveImages(truncated.Items, write).ConfigureAwait(false));
        var header = $"{(result.Ok ? "Script completed" : "Script failed")}\nWall time {(wallMs / 1000).ToString("F1", CultureInfo.InvariantCulture)} seconds\nOutput:\n";
        return (JsonValue.Array([JsonValue.Object([KeyValuePair.Create("type", (JsonValue)"text"), KeyValuePair.Create("text", (JsonValue)header)]),
            .. content.Select(ToJson)]), truncated.FullOutputPath);
    }
}
