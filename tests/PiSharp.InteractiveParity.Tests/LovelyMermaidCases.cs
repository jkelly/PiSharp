// PiSharp-only: Authored integration for lovely-mermaid 0.3.3 (Apache-2.0); upstream package attribution remains in the ported LovelyMermaid files.
// Authored inputs compared with actual output captured from lovely-mermaid 0.3.3 under Node.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.LovelyMermaid;
using PiSharp.Tui.Pi;
using LovelyRenderer = PiSharp.Cli.Interactive.Mode.LovelyMermaid.LovelyMermaid;
using static Expect;

internal static class LovelyMermaidCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All() =>
    [
        ("mermaid.lovely-0.3.3-matches-actual-library", Sync(Differential)),
        ("mermaid.lovely-transformer-surfaces-styles-links", Sync(Transformer)),
    ];

    private static readonly Dictionary<string, string> CustomTheme = new()
    {
        ["border"] = "90", ["text"] = "37", ["edge"] = "36", ["edgeLabel"] = "37", ["title"] = "1;36",
    };

    private static void Differential()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Mermaid");
        var corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "lovely-0.3.3-corpus.json")))!.AsArray();
        var captured = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "lovely-0.3.3-goldens.json")))!.AsObject();
        Equal("lovely-mermaid@0.3.3", captured["library"]!.GetValue<string>(), "capture version");
        var cases = captured["cases"]!.AsObject();
        var errors = new List<string>();
        foreach (var item in corpus)
        {
            var id = item!["id"]!.GetValue<string>();
            var source = item["src"]!.GetValue<string>();
            try
            {
                var expected = cases[id]!.AsObject();
                var actual = new JsonObject { ["kind"] = LovelyRenderer.DiagramKind(source) };
                var art = LovelyRenderer.Render(source);
                actual["render"] = Art(art);
                if (expected.ContainsKey("ansi"))
                {
                    actual["ansi"] = art is null ? null : JsonSerializer.SerializeToNode(LovelyRenderer.ToAnsi(art));
                    actual["themed"] = art is null ? null : JsonSerializer.SerializeToNode(LovelyRenderer.ToAnsi(art, CustomTheme));
                    actual["box"] = Art(LovelyRenderer.SourceBox(source));
                    actual["box30"] = Art(LovelyRenderer.SourceBox(source, 30));
                }
                if (FirstDifference(expected, actual, "") is { } difference) errors.Add(id + ": " + difference);
            }
            catch (Exception error) { errors.Add(id + ": " + error.GetType().Name + " " + error.Message); }
        }
        Equal(541, corpus.Count, "complete corpus");
        foreach (var style in captured["styles"]!.AsArray())
        {
            var id = style!["id"]!.GetValue<string>();
            var classes = style["classes"]?.Deserialize<List<string>>();
            var defs = style["defs"]!.Deserialize<Dictionary<string, Dictionary<string, string>>>()!;
            var resolved = LovelyRenderer.ResolveClassStyle(classes, defs);
            JsonObject? actualStyle = null;
            if (resolved is not null)
            {
                actualStyle = new();
                if (resolved.Fill is not null) actualStyle["fill"] = resolved.Fill;
                if (resolved.Stroke is not null) actualStyle["stroke"] = resolved.Stroke;
                if (resolved.Color is not null) actualStyle["color"] = resolved.Color;
                if (resolved.Bold is not null) actualStyle["bold"] = resolved.Bold.Value;
            }
            if (FirstDifference(style["resolved"], actualStyle, "resolved") is { } resolvedDiff) errors.Add(id + ": " + resolvedDiff);
            var spans = new[] { "border", "text", "edge", "edgeLabel", "title", "none" }
                .Select(role => new MermaidSpan(role, role, classes, "https://style.example.test/")).ToList();
            var art = new MermaidArt(1, [], new List<IReadOnlyList<MermaidSpan>> { spans }, [], defs);
            if (FirstDifference(style["ansi"], JsonSerializer.SerializeToNode(LovelyRenderer.ToAnsi(art, CustomTheme)), "ansi") is { } ansiDiff)
                errors.Add(id + ": " + ansiDiff);
        }
        Check(errors.Count == 0, $"{errors.Count}/{corpus.Count} failed: " + string.Join(" | ", errors.Take(20)));
    }

    private static JsonNode? Art(MermaidArt? art)
    {
        if (art is null) return null;
        var rows = new JsonArray();
        foreach (var row in art.Styled)
        {
            var spans = new JsonArray();
            foreach (var span in row)
            {
                var value = new JsonObject { ["text"] = span.Text, ["role"] = span.Role };
                if (span.Classes is not null) value["classes"] = JsonSerializer.SerializeToNode(span.Classes);
                if (span.Href is not null) value["href"] = span.Href;
                spans.Add(value);
            }
            rows.Add(spans);
        }
        return new JsonObject
        {
            ["width"] = art.Width, ["plain"] = JsonSerializer.SerializeToNode(art.Plain),
            ["styled"] = rows, ["warnings"] = JsonSerializer.SerializeToNode(art.Warnings),
            ["classDefs"] = JsonSerializer.SerializeToNode(art.ClassDefs),
        };
    }

    private static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        if (JsonNode.DeepEquals(expected, actual)) return null;
        if (expected is JsonObject eo && actual is JsonObject ao)
        {
            foreach (var pair in eo)
            {
                if (!ao.ContainsKey(pair.Key)) return path + "." + pair.Key + " missing";
                if (FirstDifference(pair.Value, ao[pair.Key], path + "." + pair.Key) is { } diff) return diff;
            }
        }
        if (expected is JsonArray ea && actual is JsonArray aa && ea.Count == aa.Count)
            for (var i = 0; i < ea.Count; i++)
                if (FirstDifference(ea[i], aa[i], path + "[" + i + "]") is { } diff) return diff;
        string Short(JsonNode? n) { var text = n?.ToJsonString() ?? "null"; return text.Length > 220 ? text[..220] + "..." : text; }
        return path + ": expected " + Short(expected) + ", actual " + Short(actual);
    }

    private static void Transformer()
    {
        const string source = "flowchart LR\nA[Linked]:::hot -->|edge text| B\nclassDef hot fill:#f96,color:navy,font-weight:bold\nclick A \"https://example.test/\"";
        var markdown = "```mermaid\n" + source + "\n```";
        var mode = "streaming";
        var transform = MermaidTransformer.Create(() => mode, () => Themes.Current);
        MarkdownTransformContext Context(string type, bool streaming = false, int width = 120) => new(type, streaming, width);
        var capabilities = TerminalImage.GetCapabilities();
        try
        {
            TerminalImage.SetCapabilities(capabilities with { Hyperlinks = true });
            foreach (var type in new[] { MarkdownMessageType.User, MarkdownMessageType.Assistant, MarkdownMessageType.Summary })
            {
                var result = transform(markdown, Context(type))!;
                Check(result != markdown, type + " renders");
                Contains(result, "\u001b]8;;https://example.test/\u001b\\", "OSC8 text link");
                Contains(result, "38;2;0;0;128", "author navy foreground");
                Contains(result, "48;2;255;153;102", "author background");
                var art = LovelyRenderer.Render(source)!;
                var textSpans = art.Styled.SelectMany(row => row).Count(span => span.Href is not null && span.Role == "text");
                Equal(textSpans, result.Split("\u001b]8;;https://example.test/\u001b\\", StringSplitOptions.None).Length - 1,
                    "links only wrap text spans, never borders");
            }
            foreach (var type in new[] { MarkdownMessageType.AssistantThinking, MarkdownMessageType.Custom, MarkdownMessageType.Changelog, "unknown" })
                Equal(markdown, transform(markdown, Context(type)), type + " excluded");
            TerminalImage.SetCapabilities(capabilities with { Hyperlinks = false });
            Check(!transform(markdown, Context(MarkdownMessageType.Assistant))!.Contains("\u001b]8;", StringComparison.Ordinal), "disabled terminal links");
            Equal(markdown, transform(markdown, Context(MarkdownMessageType.Assistant, width: 1)), "too narrow");
            mode = "final";
            Equal(markdown, transform(markdown, Context(MarkdownMessageType.Assistant, streaming: true)), "final skips streaming");
            mode = "off";
            Equal(markdown, transform(markdown, Context(MarkdownMessageType.Assistant)), "off");
        }
        finally { TerminalImage.SetCapabilities(capabilities); }
    }
}
