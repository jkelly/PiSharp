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
        Case("source.options-line-keeps-line-numbers", () =>
        {
            var parsed = CodemodeSource.Parse("// @options: {\"timeout_ms\": 10}\nconst a = 1;\ntext(a)");
            Equal("\nconst a = 1;\ntext(a)", parsed.Code, "code"); Equal(new CodemodeSourceOptions(null, 10), parsed.Options, "options");
            Equal(new CodemodeSourceOptions(0, 1500), CodemodeSource.Parse("  // @options:{\"max_output_tokens\":0,\"timeout_ms\":1500}\r\ntext(1)").Options, "crlf options");
            Equal("\ntext(1)", CodemodeSource.Parse("// @options: {}\ntext(1)").Code, "empty options");
        }),
        Case("source.only-first-line", () =>
        {
            var input = "text(1)\n// @options: {\"timeout_ms\": 1}";
            Equal(input, CodemodeSource.Parse(input).Code, "later line");
            Equal(new CodemodeSourceOptions(), CodemodeSource.Parse("// @optionsx {}\ntext(1)").Options, "optionsx");
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
        Case("declarations.signatures-and-mcp-results", () =>
        {
            Equal("hidden_dynamic_tool(args: { city: string; }): Promise<{ ok: boolean; }>;", CodemodeDeclarations.RenderToolSignature(new("hidden-dynamic-tool")
            {
                InputSchema = Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}"""),
                OutputSchema = Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""")
            }), "signature");
            Equal("free(args: unknown): Promise<unknown>;", CodemodeDeclarations.RenderToolSignature(new("free")), "free");
            var input = Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
            Equal("mcp__sample__search(args: {}): Promise<CallToolResult<{ results: Array<{ id: string; score: number; }>; }>>;", CodemodeDeclarations.RenderToolSignature(new("mcp__sample__search")
            {
                InputSchema = input,
                OutputSchema = Parse("""{"type":"object","properties":{"content":{"type":"array","items":{"type":"object"}},"structuredContent":{"type":"object","properties":{"results":{"type":"array","items":{"$ref":"#/definitions/Result~1item~0v1"}}},"required":["results"],"additionalProperties":false,"definitions":{"Result/item~v1":{"type":"object","properties":{"id":{"type":"string"},"score":{"type":"number"}},"required":["id","score"],"additionalProperties":false}}},"isError":{"type":"boolean"},"_meta":{"type":"object"}},"required":["content"]}""")
            }), "mcp structured");
            Equal("plain(args: {}): Promise<CallToolResult>;", CodemodeDeclarations.RenderToolSignature(new("plain")
            { InputSchema = input, OutputSchema = Parse("""{"type":"object","properties":{"content":{"type":"array","items":{"type":"object"}},"isError":{"type":"boolean"},"_meta":{"type":"object"}},"required":["content"]}""") }), "mcp plain");
            Check(CodemodeDeclarations.McpStructuredContentSchema(Parse("""{"type":"object","properties":{"content":{"type":"array"}}}""").Value) is null, "not a CallToolResult");
            Equal("bar\n\ncodemode tool declaration:\n```ts\ndeclare const tools: { foo(args: string): Promise<unknown>; };\n```",
                CodemodeDeclarations.RenderToolSample(new("foo") { Description = "bar", InputSchema = Parse("""{"type":"string"}""") }), "sample");
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
            Check(text.StartsWith("// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/runtime/prelude-source.ts", StringComparison.Ordinal), "attribution line");
            var body = text[(text.IndexOf('\n') + 1)..];
            Equal("224cd74082a03a57105af78e1fe205bda692f096252ff4202121016dd78f6228", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), "evaluated upstream PRELUDE_SOURCE hash");
            foreach (var constant in new[] { "262144", "1048576", "16777216", "100000" }) Check(body.Contains(constant, StringComparison.Ordinal), "limit constant " + constant);
            await using var sandbox = new CodemodeSandbox();
            var result = await sandbox.ExecuteAsync("return [typeof tools, typeof ALL_TOOLS, typeof text, typeof image, typeof exit, typeof store, typeof load, typeof console.log, Object.isFrozen(Object.prototype)]");
            Equal("[\"object\",\"object\",\"function\",\"function\",\"function\",\"function\",\"function\",\"function\",true]", Json(result.Value), "prelude API under Jint");
        }),
    ];
}
