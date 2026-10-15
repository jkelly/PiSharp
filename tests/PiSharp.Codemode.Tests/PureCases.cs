using System.Security.Cryptography;
using System.Text;
using PiSharp.Codemode;
using PiSharp.Contracts;

// Upstream: packages/codemode/test/source.test.ts, declarations.test.ts, identifier.ts and the "embedded sources" case of
// sandbox.test.ts (the prelude is upstream's, byte for byte, and parses and runs under Jint).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> PureCases() =>
    [
        Case("source.plain-code-unchanged", () =>
        {
            Equal("text('hi')", CodemodeSource.Parse("text('hi')").Code, "plain");
            Equal(new CodemodeSourceOptions(), CodemodeSource.Parse("// just a comment\nreturn 1").Options, "comment options");
            Equal("// just a comment\nreturn 1", CodemodeSource.Parse("// just a comment\nreturn 1").Code, "comment code");
        }),
        // Pi 1.2.0 source.test.ts (#10631): an options line after the first line used to be ignored, so its timeout never applied.
        Case("source.options-line-must-be-first", () =>
        {
            Equal(new CodemodeSourceOptions(), CodemodeSource.Parse("// @optionsx {}\ntext(1)").Options, "prefix only");
            Equal(new CodemodeSourceOptions(), CodemodeSource.Parse("text(\"// @options: {}\")").Options, "inside a string");
            foreach (var (input, line) in new[] { ("// count things\n// @options: {\"timeout_ms\": 1000}\nwhile (true) {}", 2),
                ("\n  // @options: {\"timeout_ms\": 1000}\nwhile (true) {}", 2), ("text(1)\r\n// @options: {\"timeout_ms\": 1}", 2),
                ("// @options: {\"timeout_ms\": 1}\ntext(1)\n// @options: {\"timeout_ms\": 2}", 3) })
            {
                var error = Throws<CodemodeSourceException>(() => CodemodeSource.Parse(input), input);
                Equal($"The // @options: line must be the first line of the input; found one on line {line}", error.Message, input);
                Equal("CodemodeSourceError", error.Name, "error name");
            }
        }),
        Case("source.options-line-keeps-line-numbers", () =>
        {
            var parsed = CodemodeSource.Parse("// @options: {\"timeout_ms\": 10}\nconst a = 1;\ntext(a)");
            Equal("\nconst a = 1;\ntext(a)", parsed.Code, "code"); Equal(new CodemodeSourceOptions(null, 10), parsed.Options, "options");
            Equal(new CodemodeSourceOptions(0, 1500), CodemodeSource.Parse("  // @options:{\"max_output_tokens\":0,\"timeout_ms\":1500}\r\ntext(1)").Options, "crlf options");
            Equal("\ntext(1)", CodemodeSource.Parse("// @options: {}\ntext(1)").Code, "empty options");
        }),
        Case("source.only-first-line", () =>
        {
            // Pi 1.2.0: a later options line is no longer ignored but rejected (source.options-line-must-be-first).
            Equal(new CodemodeSourceOptions(), CodemodeSource.Parse("// @optionsx {}\ntext(1)").Options, "optionsx");
            Equal(new CodemodeSourceOptions(), CodemodeSource.Parse("text(\"// @options: {}\")").Options, "inside a string");
        }),
        Case("source.rejects-empty-and-invalid-options", () =>
        {
            foreach (var (input, message) in new[]
            {
                ("", "Expected JavaScript source text (non-empty)"), ("  \n", "Expected JavaScript source text (non-empty)"),
                ("// @options:\ntext(1)", "@options must be a JSON object with supported fields"),
                ("// @options: {timeout_ms: 1}\ntext(1)", "@options must be valid JSON with supported fields"),
                ("// @options: [1]\ntext(1)", "@options must be a JSON object with supported fields"),
                ("// @options: {\"yield\": 1}\ntext(1)", "@options only supports `max_output_tokens` and `timeout_ms`; got `yield`"),
                ("// @options: {\"max_output_tokens\": 1.5}\ntext(1)", "@options field `max_output_tokens` must be a non-negative safe integer"),
                ("// @options: {\"timeout_ms\": 0}\ntext(1)", "@options field `timeout_ms` must be a positive integer"),
                ("// @options: {\"timeout_ms\": 2147483648}\ntext(1)", "@options field `timeout_ms` must be a positive integer up to 2147483647"),
                ("// @options: {\"timeout_ms\": 1}", "The @options line must be followed by JavaScript source on subsequent lines"),
                ("// @options: {\"timeout_ms\": 1}\n  \n", "The @options line must be followed by JavaScript source on subsequent lines"),
            })
            {
                var error = Throws<CodemodeSourceException>(() => CodemodeSource.Parse(input), input);
                Check(error.Message.Contains(message, StringComparison.Ordinal), $"{input}: {error.Message}");
            }
            Equal(new CodemodeSourceOptions(null, 2147483647), CodemodeSource.Parse("// @options: {\"timeout_ms\": 2147483647}\nx").Options, "timeout boundary");
        }),
        Case("source.grammar-is-upstream-lark", () => Equal(
            "\nstart: options_source | plain_source\noptions_source: OPTIONS_LINE NEWLINE SOURCE\nplain_source: SOURCE\n\nOPTIONS_LINE: /[ \\t]*\\/\\/ @options:[^\\r\\n]*/\nNEWLINE: /\\r?\\n/\nSOURCE: /[\\s\\S]+/\n",
            CodemodeSource.Grammar, "grammar")),
        Case("identifier.normalizes-names", () =>
        {
            Names(["mcp__docs__search", "my_tool", "_x", "_", "a_b", "_"], new[] { "mcp__docs__search", "my-tool", "1x", "", "a.b", "😀" }.Select(CodemodeIdentifier.ToIdentifier), "identifiers");
        }),
        Case("declarations.primitives-literals-unions", () =>
        {
            string T(string schema) => CodemodeDeclarations.SchemaToType(Parse(schema).Value);
            Equal("string", T("""{"type":"string"}"""), "string"); Equal("number", T("""{"type":"integer"}"""), "integer");
            Equal("string | null", T("""{"type":["string","null"]}"""), "type array"); Equal("\"a\"", T("""{"const":"a"}"""), "const");
            Equal("\"a\" | 1 | null", T("""{"enum":["a",1,null]}"""), "enum");
            Equal("string | number", T("""{"anyOf":[{"type":"string"},{"type":"number"}]}"""), "anyOf");
            Equal("unknown", T("""{"anyOf":[{"type":"string"},{}]}"""), "anyOf unknown");
            Equal("(string | number) & 1", T("""{"allOf":[{"anyOf":[{"type":"string"},{"type":"number"}]},{"const":1}]}"""), "allOf");
            Equal("unknown", T("""{"$ref":"#/defs/x"}"""), "missing ref"); Equal("unknown", T("true"), "true"); Equal("never", T("false"), "false");
        }),
        Case("declarations.objects-sorted-one-line", () =>
        {
            string T(string schema) => CodemodeDeclarations.SchemaToType(Parse(schema).Value);
            Equal("{ city: string; \"max-lines\"?: number; }", T("""{"type":"object","properties":{"city":{"type":"string"},"max-lines":{"type":"number"}},"required":["city"],"additionalProperties":false}"""), "object");
            Equal("{ [key: string]: number; }", T("""{"type":"object","additionalProperties":{"type":"number"}}"""), "record");
            Equal("{ [key: string]: unknown; }", T("""{"type":"object"}"""), "bare");
            Equal("{}", T("""{"type":"object","properties":{},"additionalProperties":false}"""), "empty");
        }),
        Case("declarations.property-description-comments", () =>
        {
            string T(string schema) => CodemodeDeclarations.SchemaToType(Parse(schema).Value);
            Equal("{\n  // look up weather for a given list of locations\n  weather: Array<{ location: string; }>;\n}",
                T("""{"type":"object","properties":{"weather":{"type":"array","description":"look up weather for a given list of locations","items":{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}}},"required":["weather"]}"""), "array description");
            Equal("{\n  // Outer\n  outer?: {\n    // Inner\n    inner?: string;\n  };\n}",
                T("""{"type":"object","properties":{"outer":{"type":"object","description":"Outer","properties":{"inner":{"type":"string","description":"Inner"}}}}}"""), "nested");
        }),
        Case("declarations.local-and-recursive-refs", () => Equal("{ item: { id: string; parent?: unknown; }; legacy?: \"a\" | \"b\"; remote?: unknown; }",
            CodemodeDeclarations.SchemaToType(Parse("""{"type":"object","properties":{"item":{"$ref":"#/$defs/Item"},"legacy":{"$ref":"#/definitions/Legacy"},"remote":{"$ref":"https://example.com/schema.json"}},"required":["item"],"$defs":{"Item":{"type":"object","properties":{"id":{"type":"string"},"parent":{"$ref":"#/$defs/Item"}},"required":["id"]}},"definitions":{"Legacy":{"enum":["a","b"]}}}""").Value), "refs")),
        Case("declarations.arrays-tuples-budget", () =>
        {
            string T(string schema) => CodemodeDeclarations.SchemaToType(Parse(schema).Value);
            Equal("Array<string>", T("""{"type":"array","items":{"type":"string"}}"""), "array"); Equal("unknown[]", T("""{"type":"array"}"""), "bare array");
            Equal("[string, number]", T("""{"type":"array","prefixItems":[{"type":"string"},{"type":"number"}]}"""), "tuple");
            var wide = Parse("{\"type\":\"object\",\"properties\":{" + string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"field{i}\":{{\"type\":\"string\"}}")) + "}}");
            Equal("unknown", CodemodeDeclarations.SchemaToType(wide.Value, 100), "over budget");
            Check(CodemodeDeclarations.SchemaToType(wide.Value).Contains("field49?: string;", StringComparison.Ordinal), "within budget");
            // The 16000-character input cap: exactly at the cap renders, one past becomes unknown.
            var type = CodemodeDeclarations.SchemaToType(wide.Value);
            Equal(type, CodemodeDeclarations.SchemaToType(wide.Value, type.Length), "at cap"); Equal("unknown", CodemodeDeclarations.SchemaToType(wide.Value, type.Length - 1), "one past cap");
            Equal(16_000, CodemodeDeclarations.DefaultInputSchemaMaxChars, "default cap");
            var huge = Parse("{\"type\":\"object\",\"properties\":{" + string.Join(",", Enumerable.Range(0, 1200).Select(i => $"\"property_number_{i}\":{{\"type\":\"string\"}}")) + "}}");
            Equal("big(args: unknown): Promise<unknown>;", CodemodeDeclarations.RenderToolSignature(new("big") { InputSchema = huge }), "signature over 16000 chars");
        }),
        Case("declarations.signatures-and-plain-output-types", () =>
        {
            Equal("hidden_dynamic_tool(args: { city: string; }): Promise<{ ok: boolean; }>;", CodemodeDeclarations.RenderToolSignature(new("hidden-dynamic-tool")
            {
                InputSchema = Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}"""),
                OutputSchema = Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""")
            }), "signature");
            Equal("free(args: unknown): Promise<unknown>;", CodemodeDeclarations.RenderToolSignature(new("free")), "free");
            // Pi 1.2.0 declarations.test.ts: CallToolResult<T> and the shared MCP types are gone; an output schema shaped like an MCP
            // CallToolResult renders as its own type.
            Equal("plain(args: { [key: string]: unknown; }): Promise<{ _meta?: { [key: string]: unknown; }; content: Array<{ [key: string]: unknown; }>; isError?: boolean; }>;",
                CodemodeDeclarations.RenderToolSignature(new("plain")
                {
                    InputSchema = Parse("""{"type":"object","properties":{}}"""),
                    OutputSchema = Parse("""{"type":"object","properties":{"content":{"type":"array","items":{"type":"object"}},"isError":{"type":"boolean"},"_meta":{"type":"object"}},"required":["content"]}""")
                }), "CallToolResult-shaped output schema");
            Equal("bar\n\ncodemode tool declaration:\n```ts\ndeclare const tools: { foo(args: string): Promise<unknown>; };\n```",
                CodemodeDeclarations.RenderToolSample(new("foo") { Description = "bar", InputSchema = Parse("""{"type":"string"}""") }), "sample");
        }),
        // Pi 1.2.0 declarations.test.ts (#10707): constraints TypeScript cannot express become @ tags on the property comment line.
        Case("declarations.constraint-tags", () =>
        {
            Equal(string.Join("\n", "{", "  // @maxLength 8 @pattern ^[a-z]+$", "  id: string;", "  name?: string;", "  // @maxItems 5 @uniqueItems @deprecated",
                "  tags?: Array<string>;", "  // Wait in milliseconds.", "  // @integer @minimum 0 @maximum 30000 @default 0", "  timeoutMs?: number;", "}"),
                CodemodeDeclarations.SchemaToType(Parse("""{"type":"object","properties":{"timeoutMs":{"type":"integer","minimum":0,"maximum":30000,"default":0,"description":"Wait in milliseconds."},"id":{"type":"string","pattern":"^[a-z]+$","maxLength":8},"tags":{"type":"array","items":{"type":"string"},"maxItems":5,"uniqueItems":true,"deprecated":true},"name":{"type":"string"}},"required":["id"]}""").Value), "tags");
        }),
        Case("declarations.constraint-tags-nullable-branch", () =>
        {
            // Pydantic's `limit: int | None = Field(None, ge=1, le=100)`.
            Equal("{\n  // @integer @minimum 1 @maximum 100\n  limit?: number | null;\n}", CodemodeDeclarations.SchemaToType(Parse(
                """{"type":"object","properties":{"limit":{"anyOf":[{"type":"integer","minimum":1,"maximum":100},{"type":"null"}],"default":null,"title":"Limit"}}}""").Value), "pydantic optional");
            // Constraints of the property and the branch both apply.
            Equal("{\n  // @integer @minimum 10 @maximum 20 @format port @format int32\n  n?: number | null;\n}", CodemodeDeclarations.SchemaToType(Parse(
                """{"type":"object","properties":{"n":{"anyOf":[{"type":"integer","minimum":10,"maximum":50,"format":"int32"},{"type":"null"}],"minimum":0,"maximum":20,"format":"port"}}}""").Value), "both apply");
        }),
        Case("declarations.constraint-tags-draft04-exclusive", () =>
        {
            Equal(string.Join("\n", "{", "  // @maximum 8 @exclusiveMaximum 5", "  m?: number | null;", "  // @integer @exclusiveMinimum 0 @maximum 10", "  n?: number;", "}"),
                CodemodeDeclarations.SchemaToType(Parse("""{"type":"object","properties":{"n":{"type":"integer","minimum":0,"exclusiveMinimum":true,"maximum":10,"exclusiveMaximum":false},"m":{"anyOf":[{"type":"number","maximum":5,"exclusiveMaximum":true},{"type":"null"}],"maximum":8}}}""").Value), "draft-04");
        }),
        Case("declarations.constraint-tags-long-and-line-terminators", () =>
        {
            Equal("{ config?: { [key: string]: unknown; }; }", CodemodeDeclarations.SchemaToType(Parse(
                "{\"type\":\"object\",\"properties\":{\"config\":{\"type\":\"object\",\"default\":{\"text\":\"" + new string('x', 100) + "\"}}}}").Value), "long default left out");
            Equal(string.Join("\n", "{", "  // a", "  // b", "  // @pattern ^[\\u000d\\u000a\\u2028\\u2029]$ @default \"\\u2028\"", "  sep?: string;", "}"),
                CodemodeDeclarations.SchemaToType(Parse("""{"type":"object","properties":{"sep":{"type":"string","pattern":"^[\r\n\u2028\u2029]$","default":"\u2028","description":"a\u2028b"}}}""").Value), "escaped terminators");
        }),
        Case("declarations.constraint-tags-dropped-before-unknown", () =>
        {
            var schema = Parse("""{"type":"object","properties":{"n":{"type":"integer","minimum":1}}}""").Value;
            Equal("{\n  // @integer @minimum 1\n  n?: number;\n}", CodemodeDeclarations.SchemaToType(schema), "tagged");
            Equal("{ n?: number; }", CodemodeDeclarations.SchemaToType(schema, 20), "untagged within budget");
            Equal("unknown", CodemodeDeclarations.SchemaToType(schema, 10), "unknown");
        }),
        Case("declarations.render-tools-globals-namespaces", () =>
        {
            Equal(string.Join("\n", "declare const tools: {", "  /**", "   * Read a file.", "   * Second line.", "   */", "  read(args: { path: string; }): Promise<string>;",
                "  remote_api(args: unknown): Promise<unknown>;", "};", "", "/** Attach it. */", "declare function attach(args: string): Promise<unknown>;"),
                CodemodeDeclarations.RenderDeclarations([
                    new("read") { Description = "Read a file.\nSecond line.", InputSchema = Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""), OutputSchema = Parse("""{"type":"string"}""") },
                    new("remote-api")], [new("attach") { Description = "Attach it.", InputSchema = Parse("""{"type":"string"}""") }]), "tools and globals");
            Equal(string.Join("\n", "declare function plain(): void;", "", "declare const models: {", "  /** List models. */", "  list(type: string): Promise<string[]>;",
                "  get(args: string): Promise<unknown>;", "};"), CodemodeDeclarations.RenderDeclarations(null, [
                    new("models.list") { Description = "List models.", Signature = "(type: string): Promise<string[]>" },
                    new("models.get") { InputSchema = Parse("""{"type":"string"}""") }, new("plain") { Signature = "(): void" }]), "namespaces");
            Check(CodemodeDeclarations.RenderDeclarations([new("x") { Description = "a */ b" }]).Contains("/** a *\\/ b */", StringComparison.Ordinal), "comment terminator");
        }),
        Case("prelude.embedded-upstream-bytes-run-under-jint", async () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PiSharp.Codemode", "Runtime", "codemode-prelude.js");
            var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            Check(text.StartsWith("// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/codemode/src/runtime/prelude-source.ts", StringComparison.Ordinal), "attribution line");
            var body = text[(text.IndexOf('\n') + 1)..];
            // Re-evaluated from @earendil-works/pi-codemode 1.2.0 dist/runtime/prelude-source.js (image() errors, settle propertiesJson).
            Equal("57c8dc8c0e812117d82167fa751bc368f249d5aaee56d321066fc5280c785281", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), "evaluated upstream PRELUDE_SOURCE hash");
            foreach (var constant in new[] { "262144", "1048576", "16777216", "100000" }) Check(body.Contains(constant, StringComparison.Ordinal), "limit constant " + constant);
            await using var sandbox = new CodemodeSandbox();
            var result = await sandbox.ExecuteAsync("return [typeof tools, typeof ALL_TOOLS, typeof text, typeof image, typeof exit, typeof store, typeof load, typeof console.log, Object.isFrozen(Object.prototype)]");
            Equal("[\"object\",\"object\",\"function\",\"function\",\"function\",\"function\",\"function\",\"function\",true]", Json(result.Value), "prelude API under Jint");
        }),
    ];
}
