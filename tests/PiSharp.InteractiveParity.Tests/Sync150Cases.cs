using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using static Expect;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/test/read-line-range-display.test.ts
// (tools.read-line-range-labels): formatReadLineRange and the read range in the TUI renderer and the session tree. The cases are the
// upstream test's, read from the source and not captured from a run; the HTML export's copy stays on the pinned Pi 1.2.0 template.
internal static class Sync150Cases
{
    private static IEnumerable<(string Id, Func<Task> Run)> All() =>
    [
        ("sync150.read-line-range-labels.format", Sync(() =>
        {
            (JsonNode? Offset, JsonNode? Limit, string Range)[] cases =
            [
                (25, 13, ":25-37"),
                ("25", "13", ":25-37"),
                (null, "13", ":1-13"),
                ("25", null, ":25"),
                (null, null, ""),
                ("abc", 13, ""),
                (25, "", ""),
                (25, "Infinity", ""),
                // Authored: Number() semantics for numeric strings (whitespace, hex, exponent), and a zero end shows no end.
                (" 7 ", "0x2", ":7-8"),
                ("1e1", 1, ":10-10"),
                (1, 0, ":1"),
                (true, 2, ""),
            ];
            foreach (var (offset, limit, range) in cases)
                Equal(range, RenderUtils.FormatReadLineRange(offset, limit), $"{offset?.ToJsonString() ?? "null"}, {limit?.ToJsonString() ?? "null"}");
        })),
        ("sync150.read-line-range-labels.session-tree", Sync(() =>
        {
            Equal("[read: notes.txt:25-37]", TreeList.FormatToolCall("read", new JsonObject { ["path"] = "notes.txt", ["offset"] = "25", ["limit"] = "13" }), "string range");
            Equal("[read: notes.txt]", TreeList.FormatToolCall("read", new JsonObject { ["path"] = "notes.txt", ["offset"] = "abc", ["limit"] = 13 }), "malformed range");
            Equal("[read: notes.txt:1-13]", TreeList.FormatToolCall("read", new JsonObject { ["path"] = "notes.txt", ["offset"] = null, ["limit"] = 13 }), "null offset");
        })),
        .. ToolRenderingCases.Sync150()
    ];
}

internal static partial class ToolRenderingCases
{
    internal static IEnumerable<(string Id, Func<Task> Run)> Sync150() =>
    [
        ("sync150.read-line-range-labels.tool-renderer", Sync(() =>
        {
            var rendered = Render(Tool("read", """{"path":"notes.txt","offset":"25","limit":"13"}""", BuiltInToolRenderers.ForToolDefinition("read"), Environment.CurrentDirectory));
            Contains(rendered, "read notes.txt:25-37", "string range");
            var malformed = Render(Tool("read", """{"path":"notes.txt","offset":"abc","limit":13}""", BuiltInToolRenderers.ForToolDefinition("read"), Environment.CurrentDirectory));
            Contains(malformed, "read notes.txt", "header");
            Check(!malformed.Contains("notes.txt:", StringComparison.Ordinal), "a malformed range shows no label: " + malformed);
        }))
    ];
}
