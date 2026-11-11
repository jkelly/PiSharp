using System.Text.Json;
using PiSharp.Codemode;

// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/codemode/test/{declarations,sandbox}.test.ts.
// Authored from the v1.5.2 source; not captured upstream output. Additional native cases cover the public helper,
// every declaration entrypoint and both native hosts, in ordinary and deterministic execution modes.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync152Cases()
    {
        yield return Case("sync152.refs.strict-public-helper", () =>
        {
            var root = Parse("""
                {"$defs":{"item":{"type":"string"},"a/b":{"type":"number"},"c~d":false,
                  "e f":true,"€":{"type":"boolean"},"%GG":{"const":"bad-escape"},
                  "%E0":{"const":"bad-utf8"},"%E0%A4%A":{"const":"truncated"},
                  "%FF":{"const":"invalid-utf8"},"%C0%AF":{"const":"overlong"},
                  "%ED%A0%80":{"const":"surrogate"},"%":{"const":"percent"},
                  "scalar":1,"array":[],"nil":null}}
                """).Value;
            Equal(root, CodemodeDeclarations.ResolveSchemaRef("#", root)!.Value, "root");
            foreach (var (reference, expected) in new[]
            {
                ("#/$defs/item", "{\"type\":\"string\"}"),
                ("#/$defs/a~1b", "{\"type\":\"number\"}"),
                ("#/$defs/a%2Fb", "{\"type\":\"number\"}"),
                ("#/$defs/c~0d", "false"), ("#/$defs/e%20f", "true"),
                ("#/$defs/%E2%82%AC", "{\"type\":\"boolean\"}"),
                // A literal percent is valid when escaped, even when its following text is not an escape.
                ("#/$defs/%25GG", "{\"const\":\"bad-escape\"}")
            })
                Equal(expected, CodemodeDeclarations.ResolveSchemaRef(reference, root)!.Value.GetRawText(), reference);
            foreach (var reference in new[]
            {
                "other.json#/$defs/item", "https://example.test/schema", "", "#item", "#/$defs/missing",
                "#/$defs/%GG", "#/$defs/%E0", "#/$defs/%E0%A4%A", "#/$defs/%FF", "#/$defs/%",
                "#/$defs/%C0%AF", "#/$defs/%ED%A0%80", "#/$defs/scalar", "#/$defs/array", "#/$defs/nil"
            })
                Check(CodemodeDeclarations.ResolveSchemaRef(reference, root) is null, reference + " must resolve to nothing");
            Check(CodemodeDeclarations.ResolveSchemaRef("#", Parse("[]").Value) is null, "array root");
            Equal(JsonValueKind.False, CodemodeDeclarations.ResolveSchemaRef("#", Parse("false").Value)!.Value.ValueKind, "boolean root");
        });

        yield return Case("sync152.refs.codemode-empty-segment-compatibility", () =>
        {
            var root = Parse("""{"":{"const":"empty"},"$defs":{"":{"const":"nested-empty"},"item":{"type":"string"}}}""").Value;
            foreach (var reference in new[] { "#/", "#///" })
                Equal(root, CodemodeDeclarations.ResolveSchemaRef(reference, root)!.Value, reference);
            foreach (var reference in new[] { "#//$defs//item/", "#/$defs/item//" })
                Equal("string", CodemodeDeclarations.ResolveSchemaRef(reference, root)!.Value.GetProperty("type").GetString(), reference);
            // General JSON Pointer readers retain empty keys; the compatibility adapter must not change them.
            Check(PiSharp.Contracts.LocalSchemaReferences.TryResolve("#/", root, out var empty), "shared empty key");
            Equal("empty", empty.GetProperty("const").GetString(), "shared resolver remains strict JSON Pointer");
        });

        yield return Case("sync152.refs.all-declaration-entrypoints", () =>
        {
            foreach (var reference in new[] { "#/$defs/%GG", "#/$defs/%E0", "#/$defs/missing", "other.json#/$defs/item", "#/$defs/cycle" })
            {
                var schema = Parse("""
                    {"$ref":"REFERENCE","$defs":{"%GG":{"type":"string"},"%E0":{"type":"number"},
                      "cycle":{"$ref":"#/$defs/cycle"}}}
                    """.Replace("REFERENCE", reference, StringComparison.Ordinal));
                var tool = new CodemodeTool("lookup") { InputSchema = schema, OutputSchema = schema };
                Equal("unknown", CodemodeDeclarations.SchemaToType(schema.Value), reference + " type");
                Equal("unknown", CodemodeDeclarations.SchemaToType(schema.Value, 1), reference + " bounded type");
                Equal("unknown", CodemodeDeclarations.RenderToolOutputType(schema.Value), reference + " output");
                Equal("lookup(args: unknown): Promise<unknown>;", CodemodeDeclarations.RenderToolSignature(tool), reference + " signature");
                Check(CodemodeDeclarations.RenderToolSample(tool).Contains("lookup(args: unknown): Promise<unknown>;", StringComparison.Ordinal), reference + " sample");
                Equal("declare const tools: {\n  lookup(args: unknown): Promise<unknown>;\n};", CodemodeDeclarations.RenderDeclarations([tool]), reference + " tool declaration");
                Equal("declare function lookup(args: unknown): Promise<unknown>;", CodemodeDeclarations.RenderDeclarations(globals: [tool]), reference + " global declaration");
                Equal("declare const ns: {\n  lookup(args: unknown): Promise<unknown>;\n};", CodemodeDeclarations.RenderDeclarations(globals: [tool with { Name = "ns.lookup" }]), reference + " namespace declaration");
            }
            var valid = Parse("""{"$ref":"#//$defs//a%20b/","$defs":{"a b":{"type":"string"}}}""");
            Equal("string", CodemodeDeclarations.SchemaToType(valid.Value), "valid escaped reference still renders");
            var chain = Parse("""{"$ref":"#/$defs/a","$defs":{"a":{"$ref":"#/$defs/b"},"b":{"$ref":"#/$defs/a"}}}""");
            Equal("unknown", CodemodeDeclarations.SchemaToType(chain.Value), "mutual cycle terminates");
        });

        foreach (var host in Hosts)
        foreach (var deterministic in new[] { false, true })
        {
            var capturedHost = host;
            var capturedDeterministic = deterministic;
            yield return Case($"sync152.nonfinite.{host.Name}.{(deterministic ? "start" : "execute")}", async () =>
            {
                await using var sandbox = new CodemodeSandbox(capturedHost.Host(new() { TimeoutMs = 20_000 }));
                Task<CodemodeResult> Execute(string code) => capturedDeterministic
                    ? sandbox.Start(code, new() { StartedAt = StartedAt, Seed = 1, OnCall = _ => { } }).Result
                    : sandbox.ExecuteAsync(code);
                var result = await Execute("""
                    for (const level of ["log", "info", "warn", "error", "debug"])
                        console[level]("avg", 0/0, 1/0, -1/0, {a:1});
                    text(0/0); text(1/0); text(-1/0);
                    console.log({a:NaN, b:Infinity, c:-Infinity});
                    """);
                Ok(result, "undefined", "console nonfinite");
                Equal(9, result.Output.Length, "output count");
                foreach (var item in result.Output.Take(5))
                    Equal(new CodemodeTextOutput("avg NaN Infinity -Infinity {\"a\":1}", true), item, "console level");
                Names(["NaN", "Infinity", "-Infinity"], result.Output.Skip(5).Take(3).Cast<CodemodeTextOutput>().Select(item => item.Text), "text primitives");
                Equal(new CodemodeTextOutput("{\"a\":null,\"b\":null,\"c\":null}", true), result.Output[^1], "object JSON semantics");
                foreach (var (expression, expected) in new[] { ("NaN", "NaN"), ("Infinity", "Infinity"), ("-Infinity", "-Infinity") })
                {
                    var thrown = await Execute("throw " + expression);
                    Failed(thrown, CodemodeErrorKind.Script, "thrown " + expression);
                    Equal(expected, thrown.Error!.Message, "thrown primitive message");
                }
            });
        }
    }
}
