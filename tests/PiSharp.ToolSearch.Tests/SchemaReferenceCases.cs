using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Discovery;

internal static partial class Program
{
    private static void ReferencedSearchMetadata()
    {
        using var schema = JsonDocument.Parse("""
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
            """);
        var doc = ToolSearch.CreateDocument("sample", "base", schema.RootElement);
        Equal("base reachable first saffron uniqueField second empty emptykey percent percentkey cycle remote bad utf8 missing", doc.Text, "reference graph text");
        var ranker = new Bm25Ranker();
        Equal(1, ranker.Rank("saffron", [doc], 8).Count, "referenced description searchable");
        Equal(0, ranker.Rank("unusedsecret", [doc], 8).Count, "unused definition excluded");
        using var inline = JsonDocument.Parse("""{"description":"root","properties":{"x":{"description":"leaf"}},"items":{"description":"item"},"allOf":[{"description":"union"}]}""");
        Equal("base root x leaf item union", ToolSearch.CreateDocument("sample", "base", inline.RootElement).Text, "non-reference text unchanged");
        using var inlineThenReference = JsonDocument.Parse("""{"properties":{"a":{"description":"needle"},"b":{"$ref":"#/properties/a"},"c":{"$ref":"#/properties/a"}}}""");
        Equal("base a needle b needle c", ToolSearch.CreateDocument("sample", "base", inlineThenReference.RootElement).Text,
            "inline occurrence and first reference both retain term frequency; subsequent references deduplicate");
        using var boolean = JsonDocument.Parse("""{"$defs":{"yes":true},"$ref":"#/$defs/yes"}""");
        Check(LocalSchemaReferences.TryResolve("#/$defs/yes", boolean.RootElement, out var target) && target.ValueKind == JsonValueKind.True, "boolean schema target");
        Check(!LocalSchemaReferences.TryResolve("#/$defs/%E0%A4", schema.RootElement, out _), "truncated UTF-8 pointer rejected");
        Check(!LocalSchemaReferences.TryResolve("#/$defs/% 1", schema.RootElement, out _), "whitespace percent escape rejected");
    }
}
