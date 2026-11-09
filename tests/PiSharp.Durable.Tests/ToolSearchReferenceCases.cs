using System.Text.Json;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;
using static Program;

internal static partial class ToolSearchTests
{
    private static void ReferenceMetadata()
    {
        const string json = """
            {"$ref":"#/$defs/alias","$defs":{
              "alias":{"$ref":"#/$defs/shape"},
              "shape":{"description":"reachable","properties":{
                "first":{"$ref":"#/$defs/a~1b~0c"},"second":{"$ref":"#/$defs/a~1b~0c"},
                "empty":{"$ref":"#/$defs/"},"percent":{"$ref":"#/$defs/space%20name"},
                "cycle":{"$ref":"#"},"remote":{"$ref":"https://invalid.example/schema"},
                "bad":{"$ref":"#/$defs/%GG"},"utf8":{"$ref":"#/$defs/%FF"},"missing":{"$ref":"#/$defs/missing"}}},
              "a/b~c":{"description":"saffron","properties":{"uniqueField":{"type":"string"}}},
              "":{"description":"emptykey"},"space name":{"description":"percentkey"},
              "unused":{"description":"unusedsecret"}}}
            """;
        var tool = new ToolRegistration { Name = "sample", Description = "base", Parameters = (JsonObjectValue)JsonValue.Parse(json),
            Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult()) };
        const string expected = "base reachable first saffron uniqueField second empty emptykey percent percentkey cycle remote bad utf8 missing";
        Equal(expected, ToolSearch.SearchText(tool), "Durable reference graph");
        var shared = JsonValue.Object(KeyValuePair.Create("description", (JsonValue)"same"));
        var properties = JsonValue.Object(KeyValuePair.Create("a", (JsonValue)shared), KeyValuePair.Create("b", (JsonValue)shared));
        var nonrefs = tool with { Parameters = JsonValue.Object(KeyValuePair.Create("properties", (JsonValue)properties)) };
        Equal("base a same b same", ToolSearch.SearchText(nonrefs), "shared inline nodes keep non-reference term frequencies");
        var inlineThenReference = tool with { Parameters = (JsonObjectValue)JsonValue.Parse("""{"properties":{"a":{"description":"needle"},"b":{"$ref":"#/properties/a"},"c":{"$ref":"#/properties/a"}}}""") };
        Equal("base a needle b needle c", ToolSearch.SearchText(inlineThenReference),
            "inline occurrence and first reference both retain term frequency; subsequent references deduplicate");
        using var schema = JsonDocument.Parse(json);
        Equal(PiSharp.Extensions.Mcp.Discovery.ToolSearch.CreateDocument("sample", "base", schema.RootElement).Text,
            ToolSearch.SearchText(tool), "both discovery paths agree");
    }
}
