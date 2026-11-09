using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.Schema;
using PiSharp.CodingAgent.Resources;
using PiSharp.Extensions.Mcp.Discovery;
using PiSharp.Tools.Files;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

var observations = new List<object>();
void Observe(string id, object expected, Func<object> act)
{
    try { observations.Add(new { id, expected, actual = act(), exception = (string?)null }); }
    catch (Exception e) { observations.Add(new { id, expected, actual = (object?)null, exception = e.GetType().Name + ": " + e.Message }); }
}

Observe("schema-no-id-control", true, () => JsonSchemaValidator.Compile(JsonNode.Parse("""{"type":"object","properties":{"value":{"$ref":"#/$defs/value"}},"$defs":{"value":{"type":"string"}},"required":["value"]}""")).Check(JsonNode.Parse("""{"value":"ok"}""")));
Observe("schema-relative-id", true, () => JsonSchemaValidator.Compile(JsonNode.Parse("""{"$id":"schemas/tool.json","type":"object","properties":{"value":{"$ref":"#/$defs/value"}},"$defs":{"value":{"type":"string"}},"required":["value"]}""")).Check(JsonNode.Parse("""{"value":"ok"}""")));
Observe("prompt-apostrophe", new[] { "doesn't", "work" }, () => PromptTemplateExpander.ParseCommandArgs("doesn't work").ToArray());
Observe("prompt-unmatched-quote", new[] { "one", "\"two" }, () => PromptTemplateExpander.ParseCommandArgs("one \"two").ToArray());
Observe("prompt-quoted-control", new[] { "two words", "next" }, () => PromptTemplateExpander.ParseCommandArgs("\"two words\" next").ToArray());
using var direct = JsonDocument.Parse("""{"type":"object","properties":{"needle":{"type":"string","description":"searchableuniquevalue"}}}""");
using var indirect = JsonDocument.Parse("""{"type":"object","properties":{"query":{"$ref":"#/$defs/Options"}},"$defs":{"Options":{"type":"object","properties":{"needle":{"type":"string","description":"searchableuniquevalue"}}}}}""");
Observe("tool-search-direct-control", true, () => ToolSearch.CreateDocument("sample", "plain", direct.RootElement).Text.Contains("searchableuniquevalue"));
Observe("tool-search-ref", true, () => ToolSearch.CreateDocument("sample", "plain", indirect.RootElement).Text.Contains("searchableuniquevalue"));
Observe("edit-mixed-line-endings", "first\r\nSECOND\nthird\r", () => EditPlan.Create("first\r\nsecond\nthird\r", [new TextEdit("second", "SECOND")], "mixed.txt").Content);
Observe("anthropic-nonstrict-defs", true, () =>
{
    var declaration = JsonData.Parse("""{"role":"system","toolsAdded":[{"name":"lookup","description":"D","parameters":{"type":"object","properties":{"settings":{"$ref":"#/$defs/Settings"}},"required":["settings"],"$defs":{"Settings":{"type":"object","properties":{"verbose":{"type":"boolean"}}}}}}]}""");
    var model = new ModelDescriptor("fixture", "anthropic-messages", "fixture");
    var payload = new AnthropicMessagesRequestProjector(new(128, CacheRetention: AnthropicCacheRetention.None)).Project(new(model, [new TranscriptEntry("system", declaration)], 1));
    using var parsed = JsonDocument.Parse(payload.ToString());
    return parsed.RootElement.GetProperty("tools")[0].GetProperty("input_schema").TryGetProperty("$defs", out _);
});
Console.WriteLine(JsonSerializer.Serialize(new { productCommit = "14dd34b369fba6d236684da6dfc8a39a03263321", observations }, new JsonSerializerOptions { WriteIndented = true }));
