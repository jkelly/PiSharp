using System.Collections.Immutable;
using PiSharp.Codemode;
using PiSharp.Contracts;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SchemaReferenceCases()
    {
        yield return Case("refs.output-summaries", () =>
        {
            string Summary(string output)
            {
                var tool = new ToolLoadoutTool(Parse("""{"name":"lookup","description":"lookup","parameters":{"type":"object","properties":{}}}"""), ToolExposure.Direct) { OutputSchema = Parse(output) };
                ImmutableArray<ToolLoadoutTool> tools = [tool];
                return CodemodeToolDefinition.PrepareLoadout(new(tools, tools, tools), CodemodeMode.On, false, null, t => t.OutputSchema).Descriptions["lookup"];
            }
            var text = Summary("""
                {"$ref":"#/$defs/alias","$defs":{"alias":{"$ref":"#/$defs/shape"},
                 "shape":{"type":"object","properties":{"kind":{"$ref":"#/$defs/a~1b~0c"},"data":{"$ref":"#/$defs/row"}},"required":["kind"]},
                 "a/b~c":{"const":"found"},"row":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]},
                 "unused":{"type":"object","properties":{"secret":{"type":"string"}}}}}
                """);
            Check(text.EndsWith("resolves to `{ kind: \"found\", data?: { id } }`.", StringComparison.Ordinal), text);
            var union = Summary("""{"$ref":"#/$defs/union","$defs":{"union":{"anyOf":[{"$ref":"#/$defs/row"},{"type":"object","properties":{}}]},"row":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}}}""");
            Check(union.EndsWith("resolves to `{ id } | {  }`.", StringComparison.Ordinal), union);
            foreach (var reference in new[] { "#", "#/$defs/a", "#/$defs/%GG", "#/$defs/%FF", "https://invalid.example/schema" })
            {
                var schema = System.Text.Json.JsonSerializer.Serialize(new { @type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" }, @ref = reference });
                // Place a real $ref in the root; cyclic and missing references retain its own fields.
                schema = schema.Replace("\"ref\":", "\"$ref\":", StringComparison.Ordinal);
                Check(Summary(schema).EndsWith("resolves to `{ id }`.", StringComparison.Ordinal), reference);
            }
        });
    }
}
