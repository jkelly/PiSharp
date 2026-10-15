using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Mcp;
using PiSharp.Codemode;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Discovery;

// Differential against the real upstream code: Declarations/declaration-goldens.json is recorded by
// Declarations/gen-declaration-goldens.mjs from @earendil-works/pi-codemode 1.2.0 (schemaToType, renderToolSignature,
// renderToolSample) and @earendil-works/pi-coding-agent 1.2.0 (createMcpResultSchema, the codemode tool's prepareLoadout and
// reachesTools, tool_search's reachesTools) over a fixed-seed corpus. Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT).
internal static partial class Program
{
    private static JsonElement Goldens()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Declarations", "declaration-goldens.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement.Clone();
        Equal("1.2.0", root.GetProperty("piCodemode").GetString(), "pi-codemode version");
        Equal("1.2.0", root.GetProperty("piCodingAgent").GetString(), "pi-coding-agent version");
        return root;
    }

    private static void NoMismatches(List<string> mismatches, int total, string what) =>
        Check(mismatches.Count == 0, $"{what}: {mismatches.Count} of {total} differ from upstream; first:\n{string.Join("\n---\n", mismatches.Take(3))}");

    private static IEnumerable<(string, Func<Task>)> DeclarationDifferentialCases() =>
    [
        Case("differential.schema-to-type", () =>
        {
            var cases = Goldens().GetProperty("schemaCases").EnumerateArray().ToList();
            Check(cases.Count > 1000, "corpus size");
            var mismatches = new List<string>();
            foreach (var item in cases)
            {
                int? maxChars = item.TryGetProperty("maxChars", out var max) ? max.GetInt32() : null;
                var expected = item.GetProperty("expected").GetString()!;
                string actual;
                try { actual = CodemodeDeclarations.SchemaToType(item.GetProperty("schema"), maxChars); }
                catch (Exception error) { actual = "<threw " + error.GetType().Name + ": " + error.Message + ">"; }
                if (actual != expected) mismatches.Add($"schema {item.GetProperty("schema").GetRawText()} maxChars {maxChars}\nexpected:\n{expected}\nactual:\n{actual}");
            }
            NoMismatches(mismatches, cases.Count, "schemaToType");
        }),
        Case("differential.mcp-tool-declarations", () =>
        {
            var cases = Goldens().GetProperty("mcpCases").EnumerateArray().ToList();
            var inputs = new[] { """{"type":"object","properties":{}}""", """{"type":"object","properties":{"query":{"type":"string","description":"What to find.","minLength":1}},"required":["query"]}""" };
            var mismatches = new List<string>();
            for (var index = 0; index < cases.Count; index++)
            {
                var item = cases[index];
                var given = item.GetProperty("outputSchema");
                JsonElement? output = given.TryGetProperty("value", out var value) ? value : null;
                var schema = McpToolResults.ResultSchema(output);
                if (!JsonNode.DeepEquals(JsonNode.Parse(schema.ToString()), JsonNode.Parse(item.GetProperty("resultSchema").GetRawText())))
                    mismatches.Add($"result schema of {given.GetRawText()}:\nexpected {item.GetProperty("resultSchema").GetRawText()}\nactual   {schema}");
                var tool = new CodemodeTool($"mcp__docs__tool_{index}") { Description = $"Tool {index}.", InputSchema = JsonData.Parse(inputs[index % 2]), OutputSchema = schema };
                var signature = CodemodeDeclarations.RenderToolSignature(tool);
                if (signature != item.GetProperty("signature").GetString()) mismatches.Add($"signature {index}:\nexpected:\n{item.GetProperty("signature").GetString()}\nactual:\n{signature}");
                var sample = CodemodeDeclarations.RenderToolSample(tool);
                if (sample != item.GetProperty("sample").GetString()) mismatches.Add($"sample {index}:\nexpected:\n{item.GetProperty("sample").GetString()}\nactual:\n{sample}");
            }
            NoMismatches(mismatches, cases.Count, "MCP tool declarations");
        }),
        Case("differential.codemode-loadout-descriptions", () =>
        {
            var cases = Goldens().GetProperty("loadoutCases").EnumerateArray().ToList();
            var mismatches = new List<string>();
            foreach (var item in cases)
            {
                var rows = item.GetProperty("tools").EnumerateArray().Select(row =>
                {
                    var declaration = new JsonObject
                    {
                        ["name"] = row.GetProperty("name").GetString(), ["description"] = row.GetProperty("description").GetString(),
                        ["parameters"] = JsonNode.Parse(row.GetProperty("parameters").GetRawText())
                    };
                    var exposure = row.GetProperty("exposure").GetString() switch
                    { "direct" => ToolExposure.Direct, "codemode" => ToolExposure.Codemode, "deferred" => ToolExposure.Deferred, var other => throw new InvalidDataException(other) };
                    ToolNamespace? ns = row.TryGetProperty("namespace", out var group)
                        ? new(group.GetProperty("name").GetString()!, group.TryGetProperty("description", out var description) ? description.GetString() : null)
                        { Aliases = group.TryGetProperty("aliases", out var aliases) ? [.. aliases.EnumerateArray().Select(alias => alias.GetString()!)] : [] }
                        : null;
                    return new ToolLoadoutTool(JsonData.Parse(declaration.ToJsonString()), exposure)
                    {
                        Namespace = ns,
                        PromptGuidelines = row.TryGetProperty("guidelines", out var guidelines) ? [.. guidelines.EnumerateArray().Select(line => line.GetString()!)] : [],
                        OutputSchema = row.TryGetProperty("outputSchema", out var outputSchema) ? JsonData.FromElement(outputSchema) : null
                    };
                }).ToImmutableArray();
                var loadout = new ToolLoadout([.. rows.Where(row => row.Exposure == ToolExposure.Direct)], rows, rows);
                var mode = item.GetProperty("mode").GetString() == "only" ? CodemodeMode.Only : CodemodeMode.On;
                int? budget = item.TryGetProperty("inlineBudget", out var inline) ? inline.GetInt32() : null;
                var changes = CodemodeToolDefinition.PrepareLoadout(loadout, mode, models: false, budget, tool => tool.OutputSchema);
                var expected = item.GetProperty("changes");
                var expectedDescriptions = expected.GetProperty("descriptions").EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!);
                foreach (var (name, text) in expectedDescriptions)
                    if (!changes.Descriptions.TryGetValue(name, out var actual) || actual != text)
                        mismatches.Add($"{mode}/{budget} description of {name}:\nexpected:\n{text}\nactual:\n{actual}");
                if (changes.Descriptions.Count != expectedDescriptions.Count)
                    mismatches.Add($"{mode}/{budget} described tools: expected {string.Join(",", expectedDescriptions.Keys)}, actual {string.Join(",", changes.Descriptions.Keys)}");
                var hidden = expected.GetProperty("hiddenDeclarations").EnumerateArray().Select(name => name.GetString()!).ToArray();
                if (!hidden.SequenceEqual(changes.HiddenDeclarations)) mismatches.Add($"{mode}/{budget} hidden: expected {string.Join(",", hidden)}, actual {string.Join(",", changes.HiddenDeclarations)}");
            }
            NoMismatches(mismatches, cases.Count, "codemode loadout");
        }),
        Case("differential.reaches-tools", () =>
        {
            var goldens = Goldens();
            foreach (var item in goldens.GetProperty("reachCases").EnumerateArray())
            {
                var reach = McpDiscoveryToolIdentity.CodemodeReach(JsonData.FromElement(item.GetProperty("input")));
                var expected = item.GetProperty("reach");
                if (expected.ValueKind == JsonValueKind.String) Check(reach.IsAll && expected.GetString() == "all", "all: " + item.GetRawText());
                else Names(expected.EnumerateArray().Select(word => word.GetString()!), reach.Words, "words of " + item.GetProperty("input").GetRawText());
            }
            Equal("all", goldens.GetProperty("toolSearchReach").GetString(), "tool_search reach golden");
            Check(McpDiscoveryToolIdentity.ToolSearchReach(JsonData.Parse("""{"query":"docs"}""")).IsAll, "tool_search reaches every tool");
        }),
    ];
}
