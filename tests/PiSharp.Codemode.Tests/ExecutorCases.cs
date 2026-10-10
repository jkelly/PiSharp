using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Codemode;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

// Upstream: packages/coding-agent/test/suite/agent-session-codemode.test.ts (output layout, failures, truncation, images, store,
// models), codemode-renderer.test.ts and extensions/codemode/tool.ts (description, loadout), against a fake session host and a
// fake model registry (ICodemodeHost, ICodemodeModelRuntime).
internal static partial class Program
{
    private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private sealed class FakeHost(ImmutableArray<CodemodeNestedTool> tools, Func<string, JsonData?, CodemodeNestedOutcome>? execute = null) : ICodemodeHost
    {
        public List<KeyValuePair<string, JsonData>> Store { get; } = [];
        public List<string> Appended { get; } = [];
        public List<string> Executed { get; } = [];
        public ICodemodeModelRuntime? Models { get; init; }
        public ImmutableArray<CodemodeNestedTool> Tools => tools;
        private int next;
        public ValueTask<CodemodeNestedOutcome> ExecuteToolAsync(string name, JsonData? arguments, CancellationToken cancellationToken)
        {
            lock (Executed) Executed.Add(name + ":" + Json(arguments));
            var outcome = execute?.Invoke(name, arguments) ?? new(null, Parse("""{"content":[{"type":"text","text":"done"}]}"""), false);
            return ValueTask.FromResult(outcome with { ToolCallId = "parent/" + Interlocked.Increment(ref next) });
        }
        public IReadOnlyList<KeyValuePair<string, JsonData>> ReadStore() => Store;
        public ValueTask AppendStoreEntryAsync(JsonData data, CancellationToken cancellationToken)
        {
            Appended.Add(data.ToString());
            foreach (var (key, value) in CodemodeStore.Read([(CodemodeToolDefinition.StoreEntryType, data.Value)]))
            { Store.RemoveAll(pair => pair.Key == key); Store.Add(new(key, value)); }
            foreach (var key in data.Value.GetProperty("delete").EnumerateArray()) Store.RemoveAll(pair => pair.Key == key.GetString());
            return ValueTask.CompletedTask;
        }
    }

    private static CodemodeNestedTool Nested(string name, string description = "A tool.", string? output = null, ToolNamespace? ns = null, params string[] guidelines) =>
        new(name, description, Parse("""{"type":"object","properties":{"text":{"type":"string"}}}""")) { OutputSchema = output is null ? null : Parse(output), Namespace = ns, PromptGuidelines = [.. guidelines] };

    private static JsonElement Content(JsonData result) => result.Value.GetProperty("content");
    /// <summary>The result's text after the "Script completed" header, with images as &lt;image&gt;.</summary>
    private static string ResultText(JsonData result) => string.Join("\n", Content(result).EnumerateArray().Skip(1)
        .Select(block => block.GetProperty("type").GetString() == "text" ? block.GetProperty("text").GetString() : "<image>"));
    private static string[] CallRows(JsonData result) => [.. result.Value.GetProperty("details").GetProperty("calls").EnumerateArray()
        .Select(call => call.GetProperty("name").GetString() + ":" + call.GetProperty("status").GetString())];

    private static async Task<JsonData> Execute(string code, ICodemodeHost? host = null, bool models = false, List<string>? updates = null) =>
        await CodemodeExecutor.ExecuteAsync("call-1", code, host, partial => { lock (updates ?? []) updates?.Add(partial.ToString()); return ValueTask.CompletedTask; }, models);

    private sealed class FakeModels : ICodemodeModelRuntime
    {
        public int Active, MaxActive, Classified;
        private static readonly JsonData Classifier = Parse("""{"id":"jev-latest","name":"Jev","api":"typesafe-system-one","provider":"typesafe","type":"classifier","input":["text"],"cost":{"input":1,"output":0,"cacheRead":0,"cacheWrite":0}}""");
        private static readonly JsonData Image = Parse("""{"id":"flux","name":"Flux","api":"openrouter-images","provider":"openrouter","type":"image","input":["text"]}""");
        public ImmutableArray<JsonData> GetModelsOfType(ModelType type, string? provider) => type == ModelType.Classifier ? [Classifier] : type == ModelType.Image ? [Image] : [];
        public Task<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(ModelType type, string? provider, CancellationToken cancellationToken) => Task.FromResult(GetModelsOfType(type, provider));
        public JsonData? GetModelOfType(ModelType type, string provider, string id) =>
            type == ModelType.Classifier && provider == "typesafe" && id == "jev-latest" ? Classifier : type == ModelType.Image && provider == "openrouter" && id == "flux" ? Image : null;
        public async Task<ClassifierResult> ClassifyAsync(JsonData model, ClassifierContext context, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref Active);
            lock (this) MaxActive = Math.Max(MaxActive, now);
            await Task.Delay(30, cancellationToken);
            Interlocked.Decrement(ref Active); Interlocked.Increment(ref Classified);
            return new("typesafe-system-one", "typesafe", "jev-latest", [new("ok", new ClassifierBoolAnswer(0.75))], ModelOperationStopReason.Stop, 1)
            { Usage = new(10, 0, 0, 0, 10, new(0.001m, 0, 0, 0, 0.001m)) };
        }
        public Task<AssistantImages> GenerateImagesAsync(JsonData model, ImagesContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AssistantImages("openrouter-images", "openrouter", "flux", [new ImageContent(TinyPng, "image/png")], ModelOperationStopReason.Stop, 1));
    }

    private static IEnumerable<(string, Func<Task>)> ExecutorCases()
    {
        var spill = Temp("spill"); Directory.CreateDirectory(spill);
        var files = 0;
        CodemodeExecutor.WriteOutputFile = async (prefix, extension, bytes, token) =>
        {
            var path = Path.Combine(spill, $"{prefix}-{Interlocked.Increment(ref files)}{extension}");
            await File.WriteAllBytesAsync(path, bytes, token); return path;
        };
        return
        [
            Case("executor.text-items-and-console-block", async () =>
            {
                var result = await Execute("text(\"one\\ntwo\");\nconsole.log(\"a\");\nconsole.log(\"b\");\ntext(\"three\\n\");\nreturn 4;");
                Equal(2, Content(result).GetArrayLength(), "one text block after the header");
                Equal("==> text 1/3 <==\none\ntwo\n==> text 2/3 <==\nthree\n==> text 3/3 <==\n4\n<console_output>\na\nb\n</console_output>", ResultText(result), "layout");
                Check(System.Text.RegularExpressions.Regex.IsMatch(Content(result)[0].GetProperty("text").GetString()!, "^Script completed\nWall time \\d+\\.\\d seconds\nOutput:\n$"), "header");
                Check(!result.Value.TryGetProperty("isError", out _), "not an error");
                Equal("1", ResultText(await Execute("return 1")), "single value without markers");
            }),
            Case("executor.nested-calls-text-structured-and-errors", async () =>
            {
                var host = new FakeHost([Nested("echo"), Nested("stats", output: """{"type":"object","properties":{"files":{"type":"number"}},"required":["files"]}"""), Nested("fail")],
                    (name, args) => name switch
                    {
                        "echo" => new(null, Parse($$"""{"content":[{"type":"text","text":"echo: {{args!.Value.GetProperty("text").GetString()}}"}]}"""), false),
                        "stats" => new(null, Parse("""{"content":[{"type":"text","text":"2 files"}],"structuredContent":{"files":2,"names":["a","b"]}}"""), false),
                        _ => new(null, Parse("""{"content":[{"type":"text","text":"blocked by policy"}]}"""), true)
                    });
                var updates = new List<string>();
                var result = await Execute("const [a, b, stats] = await Promise.all([tools.echo({ text: \"one\" }), tools.echo({ text: \"two\" }), tools.stats({})]);\nconsole.log(\"files\", stats.files);\nlet blocked; try { await tools.fail({}); } catch (error) { blocked = error.message; }\ntext(ALL_TOOLS.map((tool) => tool.name).join(\",\"));\nreturn { a, b, names: stats.names, blocked };", host, updates: updates);
                Equal("==> text 1/2 <==\necho,stats,fail\n==> text 2/2 <==\n{\"a\":\"echo: one\",\"b\":\"echo: two\",\"names\":[\"a\",\"b\"],\"blocked\":\"blocked by policy\"}\n<console_output>\nfiles 2\n</console_output>", ResultText(result), "result");
                Names(["echo:ok", "echo:ok", "stats:ok", "fail:error"], CallRows(result), "rows");
                var calls = result.Value.GetProperty("details").GetProperty("calls");
                Check(calls.EnumerateArray().All(call => call.GetProperty("id").GetString()!.StartsWith("parent/", StringComparison.Ordinal)), "ids from the pipeline");
                Equal("{\"text\":\"one\"}", calls[0].GetProperty("args").GetString(), "args preview");
                Equal("blocked by policy", calls[3].GetProperty("error").GetString(), "error preview");
                Check(updates.Count >= 8 && updates.All(update => update.Contains("\"calls\"", StringComparison.Ordinal)), "progress updates with call rows: " + updates.Count);
            }),
            Case("executor.failure-keeps-partial-output-and-calls", async () =>
            {
                var host = new FakeHost([Nested("echo")]);
                var result = await Execute("text(\"partial\");\nconsole.log(\"log\");\nawait tools.echo({ text: \"x\" });\nthrow new Error(\"boom\");", host);
                Check(result.Value.GetProperty("isError").GetBoolean(), "isError");
                Check(Content(result)[0].GetProperty("text").GetString()!.StartsWith("Script failed\n", StringComparison.Ordinal), "failed header");
                var text = ResultText(result);
                Check(text.StartsWith("partial\n<console_output>\nlog\n</console_output>\nScript error:\nError: boom\n", StringComparison.Ordinal), text);
                Check(text.Contains("codemode.js:4", StringComparison.Ordinal) && text.Contains("Tool calls made before the failure (they are not undone): echo (ok)", StringComparison.Ordinal), text);
                var timeout = ResultText(await Execute("// @options: {\"timeout_ms\": 200}\nwhile (true) {}"));
                Check(timeout.Contains("Script error:\nScript timed out: Execution timed out after 200 ms\n\nNo tool calls were made.", StringComparison.Ordinal), timeout);
                Throws<CodemodeSourceException>(() => Execute("// @options: {\"yield\": 1}\ntext(1)").GetAwaiter().GetResult(), "invalid options");
            }),
            Case("executor.token-budget-at-boundary-and-one-past", async () =>
            {
                // 30 tokens = 120 characters of text output.
                Equal("x".PadLeft(120, 'x'), ResultText(await Execute("// @options: {\"max_output_tokens\": 30}\ntext(\"x\".repeat(120))")), "exactly the budget");
                var over = await Execute("// @options: {\"max_output_tokens\": 30}\ntext(\"x\".repeat(121))");
                Check(ResultText(over).StartsWith("Warning: truncated output (original token count: 31)\nTotal output lines: 1\n\n" + new string('x', 60) + "…1 tokens truncated…" + new string('x', 60), StringComparison.Ordinal), ResultText(over));
                var result = await Execute($"// @options: {{\"max_output_tokens\": 30}}\nfor (let i = 0; i < 100; i++) text(\"row \" + i);\nimage(\"data:image/png;base64,{TinyPng}\");");
                var path = result.Value.GetProperty("details").GetProperty("fullOutputPath").GetString()!;
                var text = ResultText(result);
                Check(text.StartsWith("Warning: truncated output", StringComparison.Ordinal) && text.Contains("row 0\n", StringComparison.Ordinal) && text.Contains("tokens truncated", StringComparison.Ordinal) &&
                    text.Contains("row 99", StringComparison.Ordinal) && !text.Contains("row 50\n", StringComparison.Ordinal) && text.Contains($"[Full output: {path} (read with offset/limit)]", StringComparison.Ordinal), text);
                Equal(string.Join("\n", Enumerable.Range(0, 100).Select(i => $"==> text {i + 1}/100 <==\nrow {i}")), File.ReadAllText(path), "spilled full text");
                Equal("image", Content(result)[Content(result).GetArrayLength() - 1].GetProperty("type").GetString(), "image follows the text");
                Equal(10_000, CodemodeExecutor.DefaultMaxOutputTokens, "default budget");
                Check(!(await Execute("return { ok: true };")).Value.GetProperty("details").TryGetProperty("fullOutputPath", out _), "no spill for small output");
            }),
            Case("executor.images-saved-once-each-after-its-path", async () =>
            {
                var result = await Execute($"text(\"captured\");\nimage(\"data:image/png;base64,{TinyPng}\");\nimage(\"data:image/png;base64,{TinyPng}\");\ntext(\"after\");");
                var lines = ResultText(result).Split('\n');
                Check(lines.Length == 8 && lines[2].StartsWith("[Image saved to ", StringComparison.Ordinal) && lines[2].EndsWith(" (image/png, 70B)]", StringComparison.Ordinal) && lines[4] == lines[2], string.Join(" / ", lines));
                Names(["==> text 1/2 <==", "captured", lines[2], "<image>", lines[2], "<image>", "==> text 2/2 <==", "after"], lines, "layout");
                Equal(TinyPng, Content(result)[2].GetProperty("data").GetString(), "image block kept");
                var saved = lines[2]["[Image saved to ".Length..lines[2].IndexOf(" (image/png", StringComparison.Ordinal)];
                Equal(TinyPng, Convert.ToBase64String(File.ReadAllBytes(saved)), "saved bytes");
            }),
            Case("executor.store-appends-only-successful-writes", async () =>
            {
                var host = new FakeHost([]);
                const string increment = "const next = (load(\"count\") ?? 0) + 1;\nstore(\"count\", next);\nreturn next;";
                Equal("1", ResultText(await Execute(increment, host)), "first");
                Equal("2", ResultText(await Execute(increment, host)), "second");
                Names(["{\"set\":{\"count\":1},\"delete\":[]}", "{\"set\":{\"count\":2},\"delete\":[]}"], host.Appended, "entries");
                Equal("true", ResultText(await Execute("store(\"count\", undefined);\nreturn load(\"count\") === undefined;", host)), "delete");
                Equal("{\"set\":{},\"delete\":[\"count\"]}", host.Appended[^1], "delete entry");
                var failed = new FakeHost([]);
                Check((await Execute("store(\"count\", 5);\nthrow new Error(\"boom\");", failed)).Value.GetProperty("isError").GetBoolean(), "failed script");
                Equal("\"missing\"", ResultText(await Execute("return JSON.stringify(load(\"count\") ?? \"missing\");", failed)), "nothing stored");
                Check(failed.Appended.Count == 0, "no entries for failed scripts or scripts without writes");
                Equal("1", ResultText(await Execute(increment)), "without a session the store starts empty");
            }),
            Case("executor.store-folds-branch-entries", () =>
            {
                JsonElement E(string json) => Parse(json).Value;
                var store = CodemodeStore.Read([(CodemodeToolDefinition.StoreEntryType, E("""{"set":{"a":1,"b":{"c":2}},"delete":[]}""")),
                    (CodemodeToolDefinition.StoreEntryType, E("""{"set":{"a":3},"delete":["b"]}""")), (CodemodeToolDefinition.StoreEntryType, E("""{"set":{"z":1}}""")),
                    ("other-extension", E("""{"set":{"other":1},"delete":[]}"""))]);
                Names(["a=3"], store.Select(pair => pair.Key + "=" + pair.Value), "folded");
            }),
            Case("executor.discovery-globals", async () =>
            {
                var docs = new ToolNamespace("mcp__dev-radius", "Radius dev tools") { Instructions = "Use search first." };
                var host = new FakeHost([Nested("read", "Read a file.", guidelines: "Use read to examine files instead of cat or sed."),
                    Nested("mcp__dev_radius__search", "Search the radius documentation.", ns: docs), Nested("mcp__dev_radius__fetch", "Fetch a page.", ns: docs)]);
                var result = await Execute("return [await searchTools(\"documentation search\", { limit: 1 }), (await searchTools(\"page\", { namespace: \"dev-radius\" })).map((t) => t.name), await describeNamespace(\"dev_radius\"), await describeTool(\"nothing\")];", host);
                var value = JsonDocument.Parse(ResultText(result)).RootElement;
                Equal("mcp__dev_radius__search", value[0][0].GetProperty("name").GetString(), "ranked");
                Check(value[0][0].GetProperty("description").GetString()!.Contains("codemode tool declaration:", StringComparison.Ordinal), "sample");
                Equal("[\"mcp__dev_radius__fetch\"]", value[1].GetRawText(), "namespace filter");
                Equal("""{"name":"mcp__dev-radius","description":"Radius dev tools","instructions":"Use search first.","tools":["mcp__dev_radius__search","mcp__dev_radius__fetch"]}""", value[2].GetRawText(), "describeNamespace");
                Equal(JsonValueKind.Null, value[3].ValueKind, "unknown tool");
                var described = ResultText(await Execute("text(await describeTool(\"read\"))", host));
                Check(described.Contains("- Use read to examine files instead of cat or sed.", StringComparison.Ordinal), "guidelines in describeTool: " + described);
                var bad = ResultText(await Execute("try { await searchTools(\"x\", { limit: 0 }); } catch (e) { return e.message; }", host));
                Equal("searchTools() limit must be a positive integer", bad, "limit validation");
            }),
            Case("executor.models-bridge-usage-concurrency-and-errors", async () =>
            {
                var models = new FakeModels();
                var host = new FakeHost([]) { Models = models };
                var result = await Execute("const model = (await models.getAvailableOfType(\"classifier\"))[0];\nconst context = { state: { text: \"hi\" }, questions: { ok: { type: \"bool\", instructions: \"Is it ok?\", criteria: { true: \"yes\", false: \"no\" } } } };\nconst results = await Promise.all(Array.from({ length: 10 }, () => models.classify(model, context)));\nreturn [results.length, results[0].answers.ok.probability, results[0].stopReason, Object.keys(model).includes(\"headers\")];",
                    host, models: true);
                Equal("[10,0.75,\"stop\",false]", ResultText(result), "classified");
                Equal(10, models.Classified, "calls"); Check(models.MaxActive <= CodemodeExecutor.MaxConcurrentModelCalls && models.MaxActive > 1, "at most 4 at once: " + models.MaxActive);
                Names(Enumerable.Repeat("models.classify:ok", 10), CallRows(result), "model rows");
                var row = result.Value.GetProperty("details").GetProperty("calls")[0];
                Equal("typesafe/jev-latest", row.GetProperty("args").GetString(), "row shows only the model");
                Check(row.GetProperty("id").GetString()!.StartsWith("call-1/models.classify/", StringComparison.Ordinal), "row id");
                var usage = result.Value.GetProperty("usage");
                Equal(100, usage.GetProperty("input").GetInt32(), "usage input summed"); Equal(0.01, Math.Round(usage.GetProperty("cost").GetProperty("total").GetDouble(), 6), "usage cost summed");
                async Task<string> Error(string code) => ResultText(await Execute("try { " + code + " } catch (e) { return e.message; }", host, models: true));
                Check((await Error("await models.classify(undefined, {});")).StartsWith("models.classify() expects a classifier model as its first argument, got null. models.getModelOfType() returns undefined", StringComparison.Ordinal), "undefined model");
                Equal("\"openrouter/flux\" is an image model, not a classifier model. List the classifier models you can use with models.getAvailableOfType(\"classifier\").",
                    await Error("await models.classify({ provider: \"openrouter\", id: \"flux\" }, {});"), "wrong type");
                Check((await Error("await models.classify({ provider: \"typesafe\", id: \"jev-latest\" }, { state: {} });")).StartsWith("models.classify() context.questions must map question IDs to questions, got undefined. Expected context:", StringComparison.Ordinal), "context check");
                Check((await Error("await models.getModelOfType(\"classifier\", \"typesafe/jev-latest\");")).StartsWith("models.getModelOfType(type, provider, id) expects three strings, got (a string, a string, undefined)", StringComparison.Ordinal) ||
                    (await Error("await models.getModelOfType(\"classifier\", \"typesafe/jev-latest\");")).StartsWith("models.getModelOfType(type, provider, id) expects three strings, got (a string, a string)", StringComparison.Ordinal), "three strings");
                var images = ResultText(await Execute("const model = await models.getModelOfType(\"image\", \"openrouter\", \"flux\");\nconst out = await models.generateImages(model, { input: [{ type: \"text\", text: \"a cat\" }] });\nreturn out.output.length;", host, models: true));
                Check(images.Contains("Note: models.generateImages() returned 1 image that the script did not show.", StringComparison.Ordinal), images);
                Equal("\"undefined\"", ResultText(await Execute("return JSON.stringify(typeof models);", host)), "no models without the option");
            }),
            Case("tool.description-modes-and-inline-budget", () =>
            {
                ToolLoadoutTool T(string name, ToolExposure exposure, ToolNamespace? ns = null, params string[] guidelines) =>
                    new(Parse("{\"name\":\"" + name + "\",\"description\":\"The " + name + " tool.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}}"), exposure)
                    { Namespace = ns, PromptGuidelines = [.. guidelines] };
                var codemode = T("codemode", ToolExposure.ModelOnly);
                var read = T("read", ToolExposure.Direct, null, "Use read to examine files instead of cat or sed.");
                var echo = T("echo", ToolExposure.Direct);
                var hidden = T("stats", ToolExposure.Codemode);
                var mcp = T("mcp__docs__search", ToolExposure.Deferred, new("mcp__docs", "Docs."));
                var loadout = new ToolLoadout([read, echo, codemode], [read, echo, hidden, mcp], [read, echo, codemode, hidden, mcp]);
                var on = CodemodeToolDefinition.PrepareLoadout(loadout, CodemodeMode.On, true, null, tool => tool.Name == "read" ? Parse("""{"anyOf":[{"type":"string"},{"type":"object"}]}""") : null);
                Check(on.Descriptions["echo"].EndsWith("Codemode: `tools.echo(args)` resolves to a string.", StringComparison.Ordinal), on.Descriptions["echo"]);
                Check(on.Descriptions["read"].EndsWith("resolves to `string | { [key: string]: unknown; }`.", StringComparison.Ordinal), on.Descriptions["read"]);
                Check(on.Descriptions["codemode"].Contains("### `stats`", StringComparison.Ordinal) && !on.Descriptions["codemode"].Contains("### `echo`", StringComparison.Ordinal) &&
                    !on.Descriptions["codemode"].Contains("mcp__docs__search", StringComparison.Ordinal), on.Descriptions["codemode"]);
                Check(on.Descriptions["codemode"].Contains("- `models`: classifiers and image generation.", StringComparison.Ordinal), "models line");
                Check(on.HiddenDeclarations.IsEmpty, "on hides nothing");
                var only = CodemodeToolDefinition.PrepareLoadout(loadout, CodemodeMode.Only, false, null);
                Check(only.Descriptions["codemode"].Contains("### `echo`", StringComparison.Ordinal) && only.Descriptions["codemode"].Contains("- Use read to examine files instead of cat or sed.", StringComparison.Ordinal), "only lists every callable tool");
                Names(["read", "echo"], only.HiddenDeclarations, "only hides direct declarations");
                Check(!only.Descriptions.ContainsKey("echo"), "only keeps plain descriptions");
                var zero = CodemodeToolDefinition.PrepareLoadout(loadout, CodemodeMode.Only, false, 0);
                Check(!zero.Descriptions["codemode"].Contains("### `", StringComparison.Ordinal), "inline budget 0 lists nothing");
                Equal((CodemodeMode.Only, (int?)0), CodemodeToolDefinition.ReadSettings(Parse("""{"codemode":{"mode":"only","inlineBudget":0}}""").Value), "settings");
                Equal((CodemodeMode.On, (int?)null), CodemodeToolDefinition.ReadSettings(Parse("""{"codemode":{"mode":"other","inlineBudget":-1}}""").Value), "invalid settings");
                var description = CodemodeToolDefinition.CreateDescription([], false);
                Check(description.StartsWith("Run JavaScript that calls other tools. The input is raw JavaScript (not JSON, no code fence)", StringComparison.Ordinal) && !description.Contains("Nested tools:", StringComparison.Ordinal), description);
                var grouped = CodemodeToolDefinition.CreateDescription([Nested("plain"), Nested("mcp__a__x", ns: new("mcp__a", "A server.")), Nested("mcp__a__y", ns: new("mcp__a"))], inlineBudget: 300);
                Check(grouped.Contains("## mcp__a (some tools not listed)\nA server.", StringComparison.Ordinal) || grouped.Contains("## mcp__a\nA server.", StringComparison.Ordinal), grouped);
                Equal("""{"type":"grammar","variants":{"openai_lark":"\nstart: options_source | plain_source\noptions_source: OPTIONS_LINE NEWLINE SOURCE\nplain_source: SOURCE\n\nOPTIONS_LINE: /[ \\t]*\\/\\/ @options:[^\\r\\n]*/\nNEWLINE: /\\r?\\n/\nSOURCE: /[\\s\\S]+/\n"}}""",
                    CodemodeToolDefinition.ConstrainedSampling.ToString(), "constrained sampling");
            }),
            // tool.ts constrainedSampling: on routes that support OpenAI grammar tools the codemode declaration becomes a custom tool
            // whose input is the raw script (SupportsOpenAIGrammarTools); elsewhere it stays an ordinary function declaration.
            Case("tool.grammar-sampling-through-openai-grammar-tools", () =>
            {
                var declaration = "{\"name\":\"codemode\",\"description\":\"Run JavaScript.\",\"parameters\":" + CodemodeToolDefinition.Parameters +
                    ",\"constrainedSampling\":" + CodemodeToolDefinition.ConstrainedSampling + "}";
                var request = new PiSharp.AI.ChatRequest(new("gpt-x", "openai-completions", "openai"),
                    [new("system", Parse("{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[" + declaration + "],\"timestamp\":0}"))]);
                var grammar = new PiSharp.AI.Protocols.OpenAICompletions.CompletionsToolDeclarationProjector(new() { SupportsOpenAIGrammarTools = true }).Project(request).Value[0];
                Equal("custom", grammar.GetProperty("type").GetString(), "grammar tool: " + grammar);
                Check(grammar.ToString().Contains("options_source: OPTIONS_LINE NEWLINE SOURCE", StringComparison.Ordinal) && grammar.ToString().Contains("lark", StringComparison.Ordinal), "lark grammar: " + grammar);
                var plain = new PiSharp.AI.Protocols.OpenAICompletions.CompletionsToolDeclarationProjector().Project(request).Value[0];
                Equal("function", plain.GetProperty("type").GetString(), "without grammar support");
            }),
            Case("renderer.header-calls-costs-and-previews", () =>
            {
                var result = Parse("""{"content":[{"type":"text","text":"Script completed\nWall time 0.1 seconds\nOutput:\n"},{"type":"text","text":"l1\nl2\nl3\nl4\nl5\nl6\nl7"}],"details":{"calls":[{"id":"a","name":"models.classify","args":"typesafe/jev","status":"ok","durationMs":1500,"cost":0.0042},{"id":"b","name":"models.classify","args":"typesafe/jev","status":"error","durationMs":12,"error":"bad","cost":0.02}],"fullOutputPath":"/tmp/out.txt"}}""");
                var collapsed = CodemodeRenderer.RenderResult(result, false, false);
                Names(["", "✓ models.classify typesafe/jev 1.5s $0.0042", "✗ models.classify typesafe/jev 12ms $0.02", "Model calls: $0.02", "", "l1", "l2", "l3", "l4", "l5",
                    "... (2 more lines, ctrl+o to expand)", "Full output: /tmp/out.txt"], collapsed, "collapsed");
                var expanded = CodemodeRenderer.RenderResult(result, true, false);
                Check(expanded.Contains("    bad") && expanded.Contains("l7") && !expanded.Any(line => line.StartsWith("Full output", StringComparison.Ordinal)), "expanded");
                Names(["", "rejected"], CodemodeRenderer.RenderResult(Parse("""{"content":[{"type":"text","text":"rejected"}]}"""), false, false), "no header");
                Names(["codemode", "text(1)"], CodemodeRenderer.RenderCall(Parse("""{"code":"text(1)\n"}"""), false), "call");
                Names(["codemode [invalid arg]"], CodemodeRenderer.RenderCall(Parse("""{"code":5}"""), false), "invalid call");
                Equal("$0.0042", CodemodeRenderer.FormatCost(0.0042), "small cost"); Equal("$1.50", CodemodeRenderer.FormatCost(1.5), "cost");
            }),
        ];
    }
}
