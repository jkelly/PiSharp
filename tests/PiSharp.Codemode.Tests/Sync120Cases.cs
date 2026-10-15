using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Mcp;
using PiSharp.Codemode;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Discovery;
using PiSharp.Extensions.Runtime;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): the v1.1.0..v1.2.0 changes of packages/coding-agent/test/
// {codemode-renderer,mcp-extension}.test.ts and test/suite/agent-session-{codemode,mcp}.test.ts, and of the codemode and tool-search
// extensions (reachesTools, namespace aliases, row timer, linear text join, the misplaced options line), ported by reading.
internal static partial class Program
{
    private sealed class FrozenTime : TimeProvider
    {
        public long NowMs;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(NowMs);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static string Plain(IEnumerable<string> lines) =>
        System.Text.RegularExpressions.Regex.Replace(string.Join("\n", lines), "\u001b\\[[0-9;]*m|\u001b\\]8;;[^\u0007]*\u0007", "");

    private static IEnumerable<(string, Func<Task>)> Sync120Cases() =>
    [
        // execute.ts isNamespaceName: a namespace matches its name or an alias, as written or as a script identifier; the old match on
        // the part after the last `__` is gone (agent-session-mcp.test.ts "slow-docs").
        Case("sync120.namespace-aliases", async () =>
        {
            var docs = new ToolNamespace("mcp__slow_docs", "Slow docs") { Aliases = ["slow-docs"] };
            var plain = new ToolNamespace("mcp__other_docs");
            var host = new FakeHost([Nested("mcp__slow_docs__search", "Search slow docs.", ns: docs), Nested("mcp__other_docs__fetch", "Fetch other docs.", ns: plain)]);
            var names = ResultText(await Execute("""
                const names = ["slow-docs", "slow_docs", "mcp__slow-docs", "mcp__slow_docs"];
                return [...new Set(await Promise.all(names.map(async (name) => (await describeNamespace(name))?.name)))];
                """, host));
            Equal("[\"mcp__slow_docs\"]", names, "every form of the name and alias");
            // Without an alias, the part after `__` no longer names the namespace.
            Equal("undefined", ResultText(await Execute("return String(await describeNamespace(\"other_docs\"));", host)), "no suffix match");
            Equal("[\"mcp__other_docs__fetch\"]", ResultText(await Execute("return (await searchTools(\"fetch docs\", { namespace: \"mcp__other-docs\" })).map((t) => t.name);", host)), "name as identifier");
            Equal("[]", ResultText(await Execute("return (await searchTools(\"fetch docs\", { namespace: \"other_docs\" })).map((t) => t.name);", host)), "searchTools has no suffix match");
            Equal("[\"mcp__slow_docs__search\"]", ResultText(await Execute("return (await searchTools(\"search\", { namespace: \"slow_docs\" })).map((t) => t.name);", host)), "searchTools alias");
        }),
        // agent-session-codemode.test.ts #10631: a misplaced options line fails before the script runs.
        Case("sync120.misplaced-options-line-fails-the-call", async () =>
        {
            var host = new FakeHost([Nested("echo")]);
            var error = await Failure(Execute("// count\n// @options: {\"timeout_ms\": 200}\nwhile (true) {}", host));
            Check(error is CodemodeSourceException, "source error: " + error);
            Equal("The // @options: line must be the first line of the input; found one on line 2", error.Message, "message");
            Equal(0, host.Executed.Count, "nothing ran");
        }),
        // agent-session-codemode.test.ts #10678: output at the item limit is formatted in linear time.
        Case("sync120.output-at-the-item-limit-in-linear-time", async () =>
        {
            var started = Stopwatch.StartNew();
            var result = await Execute("for (let i = 0; i < 100000; i++) text(\"\");");
            var elapsed = started.Elapsed;
            if (result.Value.GetProperty("details").TryGetProperty("fullOutputPath", out var path)) File.Delete(path.GetString()!);
            Check(!result.Value.TryGetProperty("isError", out _), "not an error");
            Check(ResultText(result).Contains("==> text 100000/100000 <==", StringComparison.Ordinal), "last item header");
            Check(elapsed < TimeSpan.FromSeconds(5), "linear: " + elapsed);
            // Each part starts on its own line; a part that already ends with a line break gets no second one.
            Equal("==> text 1/2 <==\na\n==> text 2/2 <==\nb", ResultText(await Execute("text(\"a\\n\"); text(\"b\");")), "joined after a line break");
            Equal("==> text 1/2 <==\na\n==> text 2/2 <==\nb", ResultText(await Execute("text(\"a\"); text(\"b\");")), "joined with a line break");
        }),
        // execute.ts: the script is shown as running before its first nested call, so the row's timer starts.
        Case("sync120.publishes-before-the-first-call", async () =>
        {
            var updates = new List<string>();
            await Execute("return 1", new FakeHost([Nested("echo")]), updates: updates);
            Check(updates.Count >= 1, "an update before any call");
            Equal("""{"content":[],"details":{"calls":[]}}""", updates[0], "first update");
        }),
        // agent-session-codemode.test.ts "codemode models": arrays are not objects for the models API checks.
        Case("sync120.models-api-rejects-arrays", async () =>
        {
            var host = new FakeHost([]) { Models = new FakeModels() };
            async Task<string> Error(string code) => ResultText(await Execute("const model = { provider: \"typesafe\", id: \"jev-latest\" };\ntry { " + code + " } catch (e) { return e.message; }", host, models: true));
            const string Questions = "{ kind: { type: \"bool\", instructions: \"Ok?\", criteria: { true: \"y\", false: \"n\" } } }";
            Contains(await Error("await models.classify([\"scorer\", \"judge\"], {});"), "models.classify() expects a classifier model as its first argument, got an array.");
            Contains(await Error("await models.classify(model, []);"), "models.classify() expects a context object as its second argument, got an empty array.");
            Contains(await Error($"await models.classify(model, {{ state: [], questions: {Questions} }});"), "models.classify() context.state must be an object, got an empty array.");
            Contains(await Error("await models.classify(model, { state: {}, questions: [] });"), "models.classify() context.questions must map question IDs to questions, got an empty array.");
            Contains(await Error("await models.classify(model, { state: {}, questions: { kind: [] } });"), "models.classify() context.questions.kind must be a question object, got an empty array.");
            Contains(await Error("await models.classify(model, { state: {}, questions: { kind: { type: \"bool\", criteria: { true: \"y\", false: \"n\" } } } });"),
                "models.classify() context.questions.kind.instructions must be a string.");
            Contains(await Error("await models.classify(model, { state: {}, questions: { kind: { type: \"bool\", instructions: \"Ok?\", criteria: [\"y\", \"n\"] } } });"),
                "context.questions.kind is a \"bool\" question, so criteria must be { true: string, false: string }.");
            Contains(await Error("await models.classify(model, { state: {}, questions: { kind: { type: \"rank\", instructions: \"Rank?\" } } });"),
                "context.questions.kind.type must be \"choice\", \"score\", or \"bool\", got \"rank\".");
            Contains(await Error("await models.generateImages({ provider: \"openrouter\", id: \"flux\" }, []);"),
                "models.generateImages() expects a context object as its second argument, got an empty array.");
            Contains(await Error("await models.generateImages({ provider: \"openrouter\", id: \"flux\" }, { input: [{ type: \"text\", text: \"a fox\" }, { type: \"image\", data: \"aW1hZ2U=\" }] });"),
                "models.generateImages() context.input[1] must be a text or image block, got { type, data }.");
        }),
        // codemode-renderer.test.ts #10708: Elapsed while the script runs, Took once it finished.
        Case("sync120.row-timer", () =>
        {
            var previous = RenderUtils.Time; var time = new FrozenTime { NowMs = 0 }; RenderUtils.Time = time;
            try
            {
                var state = new Dictionary<string, object?>(StringComparer.Ordinal);
                ToolRenderContext Context(bool partial, double? duration = null) => new()
                {
                    Args = new JsonObject { ["code"] = "" }, ToolCallId = "call", Cwd = "/", ExecutionStarted = true, ArgsComplete = true, IsPartial = partial,
                    State = state, DurationMs = duration
                };
                PiSharp.Cli.Interactive.Mode.Themes.InitTheme("dark");
                var theme = PiSharp.Cli.Interactive.Mode.Themes.Current;
                CodemodeRenderers.Renderers.RenderCall!(new JsonObject { ["code"] = "" }, theme, Context(true));
                var running = new JsonObject { ["content"] = new JsonArray(), ["details"] = new JsonObject { ["calls"] = new JsonArray() } };
                time.NowMs = 3_000;
                var partial = Plain(CodemodeRenderers.Renderers.RenderResult!(running, new(false, true), theme, Context(true)).Render(120));
                Contains(partial, "Elapsed 3.0s");
                var final = new JsonObject { ["content"] = new JsonArray(), ["details"] = new JsonObject { ["calls"] = new JsonArray() } };
                var done = Plain(CodemodeRenderers.Renderers.RenderResult!(final, new(false, false), theme, Context(false, 5_100)).Render(120));
                Contains(done, "Took 5.1s");
                Check(!done.Contains("Elapsed", StringComparison.Ordinal), "no Elapsed once finished: " + done);
                // A stored result without a recorded duration and no start time shows no timing line.
                var stored = Plain(CodemodeRenderers.Renderers.RenderResult!(final, new(false, false), theme, new ToolRenderContext { State = new(StringComparer.Ordinal) }).Render(120));
                Check(!stored.Contains("Took", StringComparison.Ordinal), "no timing line: " + stored);
                // Bash rows share the timer (render-utils.ts startToolTimer/toolTimerText).
                Equal("Took 1m 30s", RenderUtils.ToolTimerText(new ToolRenderContext { DurationMs = 90_000 }, false), "shared format");
                Equal("2h 3m 4s", RenderUtils.FormatToolDuration(7_384_000), "hours");
                // The native (unthemed) renderer shows the recorded duration of a final result.
                Names(["", "Took 5.1s"], CodemodeRenderer.RenderResult(Parse("""{"content":[],"details":{"calls":[]}}"""), false, false, durationMs: 5_100), "plain renderer");
                Names([], CodemodeRenderer.RenderResult(Parse("""{"content":[],"details":{"calls":[]}}"""), false, true, durationMs: 5_100), "no Took while partial");
            }
            finally { RenderUtils.Time = previous; }
        }),
        // tool.ts DESCRIPTION_INTRO (#10682 wording): a tool whose declaration returns an object resolves on failure too.
        Case("sync120.description-says-object-results-resolve-on-failure", () =>
        {
            var description = CodemodeToolDefinition.CreateDescription([]);
            Contains(description, "and rejects with an Error on failure. A tool whose declaration returns an object resolves on failure too when it returned that object; check its fields. Calls still running when the script ends are cancelled.");
            Check(!description.Contains("Shared MCP Types", StringComparison.Ordinal), "no shared MCP types");
        }),
        // mcp-extension.test.ts "declares the CallToolResult scripts receive, keeping references in the server's output schema".
        Case("sync120.mcp-result-type", () =>
        {
            const string Content = "content: Array<{ data?: string; mimeType?: string; text?: string; type: \"text\" | \"image\" | \"audio\" | \"resource_link\" | \"resource\"; [key: string]: unknown; }>; isError?: boolean;";
            Equal($"{{ {Content} structuredContent?: unknown; [key: string]: unknown; }}", CodemodeDeclarations.RenderToolOutputType(McpToolResults.ResultSchema(null).Value), "no output schema");
            var schema = McpToolResults.ResultSchema(Parse("""
                {"type":"object","properties":{"hits":{"type":"array","items":{"$ref":"#/$defs/Hit~1v1"}},"again":{"$ref":"#/properties/hits"},
                "children":{"type":"array","items":{"$ref":"#"}},"default":{"$ref":"#/definitions/Hit"},"remote":{"$ref":"https://example.com/schema.json"}},
                "required":["hits"],"$defs":{"Hit/v1":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"default":{"$ref":"#/stays"}}},
                "definitions":{"Hit":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}}}
                """).Value);
            Equal("""{"$ref":"#/stays"}""", schema.Value.GetProperty("properties").GetProperty("structuredContent").GetProperty("$defs").GetProperty("Hit/v1").GetProperty("default").GetRawText(), "data kept");
            Equal($"{{ {Content} structuredContent?: {{ again?: Array<{{ id: string; }}>; children?: Array<{{ again?: Array<{{ id: string; }}>; children?: Array<unknown>; default?: {{ id: string; }}; hits: Array<{{ id: string; }}>; remote?: unknown; }}>; default?: {{ id: string; }}; hits: Array<{{ id: string; }}>; remote?: unknown; }}; [key: string]: unknown; }}",
                CodemodeDeclarations.RenderToolOutputType(schema.Value), "relocated references");
        }),
        // agent-session-mcp.test.ts "tells scripts what directly exposed MCP tools resolve to".
        Case("sync120.direct-mcp-tools-describe-their-result", () =>
        {
            ToolLoadoutTool Tool(string name, JsonData output) => new(JsonData.Parse(new JsonObject { ["name"] = name, ["description"] = "Docs " + name + ".",
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }.ToJsonString()), ToolExposure.Direct)
            { Namespace = new("mcp__docs") { Aliases = ["docs"] }, OutputSchema = output };
            ImmutableArray<ToolLoadoutTool> tools = [Tool("mcp__docs__search", McpToolResults.ResultSchema(Parse("""{"type":"object","properties":{"hits":{"type":"array"}},"required":["hits"]}""").Value)),
                Tool("mcp__docs__fail", McpToolResults.ResultSchema(null))];
            var changes = CodemodeToolDefinition.PrepareLoadout(new(tools, tools, tools), CodemodeMode.On, false, null, tool => tool.OutputSchema);
            Contains(changes.Descriptions["mcp__docs__search"], "Codemode: `tools.mcp__docs__search(args)` resolves to `{ content, structuredContent?: { hits }, isError? }`.");
            Contains(changes.Descriptions["mcp__docs__fail"], "Codemode: `tools.mcp__docs__fail(args)` resolves to `{ content, structuredContent?, isError? }`.");
        }),
        // mcp-extension.test.ts #10416: array structured content reaches the model as JSON and scripts as sent.
        Case("sync120.mcp-array-structured-content", async () =>
        {
            var converted = await McpToolResults.ConvertAsync("docs", "t", Parse("""{"content":[],"structuredContent":[{"id":1}]}"""), false,
                (_, _, _) => throw new InvalidOperationException("nothing to save"), CancellationToken.None);
            Check(JsonNode.DeepEquals(JsonNode.Parse("""{"content":[{"type":"text","text":"[\n  {\n    \"id\": 1\n  }\n]"}],"details":{"server":"docs","tool":"t"},"structuredContent":{"content":[],"structuredContent":[{"id":1}]}}"""),
                JsonNode.Parse(converted.ToString())), "converted: " + converted);
        }),
        // types.ts reachesTools: codemode reaches the words of its script or every tool, tool_search every tool; the registry carries
        // the function to the session's registered tools (getAllTools, the MCP integration).
        Case("sync120.reaches-tools-on-descriptors-and-registrations", async () =>
        {
            Check(McpCodemode.Create(CodemodeMode.On, null, () => null).Descriptor.ReachesTools is { } codemode &&
                codemode(Parse("""{"code":"await tools.mcp__docs__search({})"}""")).Words.Contains("mcp__docs__search"), "codemode reach");
            Check(McpToolSearch.Create().Descriptor.ReachesTools is { } search && search(Parse("""{"query":"x"}""")).IsAll, "tool_search reach");
            Check(ToolReach.Of(["mcp__docsearch__x"]).Reaches("mcp__docs") && !ToolReach.Of(["return", "1"]).Reaches("mcp__docs") && ToolReach.All.Reaches("anything"), "reaches");
            await using var registry = new ExtensionRegistry();
            await registry.ActivateAsync("owner", new HookExtension(registrations =>
            {
                registrations.RegisterTool(new("codemode", "codemode", "Another codemode.", CodemodeToolDefinition.Parameters, (_, _, _) => ValueTask.FromResult(JsonData.Null))
                { Exposure = ToolExposure.ModelOnly, ReachesTools = _ => ToolReach.All });
                registrations.RegisterTool(new("plain", "plain", "No reach.", JsonData.Parse("""{"type":"object"}"""), (_, _, _) => ValueTask.FromResult(JsonData.Null)));
                return ValueTask.CompletedTask;
            }), CancellationToken.None);
            var snapshot = registry.CaptureSnapshot();
            Check(snapshot.Tools.Single(tool => tool.Name == "codemode").ReachesTools is not null, "declared reach carried");
            Check(snapshot.Tools.Single(tool => tool.Name == "plain").ReachesTools is null, "no reach");
        }),
        // mcp/index.ts tool_call: the MCP extension is a built-in, loaded after the other extensions, so its handler waits for the
        // servers once their tool_call handlers allowed the call, with the input they leave; a blocked call waits for nothing.
        Case("sync120.mcp-tool-call-waits-after-the-extensions-tool-call-handlers", async () =>
        {
            var waits = new List<string>();
            Func<string, JsonData, CancellationToken, Task>? handler = (tool, input, _) => { lock (waits) waits.Add(tool + " " + input); return Task.CompletedTask; };
            static (ToolInvocation, PreparedToolAction) Call(string name, string arguments)
            {
                var call = new ToolCallContent("call-" + name, name, Parse(arguments));
                var message = new AssistantMessage("anthropic-messages", "anthropic", "claude-sonnet-4-5", 0, [call], TokenUsage.Zero, StopReason.ToolUse);
                return (new ToolInvocation(message, call, 0), new PreparedToolAction(name, "invoke", PreparedToolActionKind.Path, name, Parse(arguments), [],
                    null, ImmutableDictionary<string, string>.Empty));
            }
            var (invocation, action) = Call("codemode", """{"code":"tools.mcp__docs__search()"}""");
            // No extension handlers: the validated input.
            var allowed = await new McpToolCallHooks(null, () => handler).BeforeAsync(invocation, action, CancellationToken.None);
            Check(!allowed.Block && allowed.Arguments is null, "allowed unchanged");
            // A handler that replaces the input: the MCP handler sees the replacement.
            var replacing = new McpToolCallHooks(new Hooks(_ => new(Parse("""{"code":"tools.mcp__wiki__get()"}"""))), () => handler);
            Check((await replacing.BeforeAsync(invocation, action, CancellationToken.None)).Arguments is { } replaced && replaced.ToString().Contains("wiki", StringComparison.Ordinal), "replacement kept");
            // A blocking handler: no wait, and the block is returned as is.
            var blocking = new McpToolCallHooks(new Hooks(_ => new(null, true) { Reason = "no" }), () => handler);
            var blocked = await blocking.BeforeAsync(invocation, action, CancellationToken.None);
            Check(blocked.Block && blocked.Reason == "no", "blocked");
            // Before a generation binds its handler nothing waits.
            await new McpToolCallHooks(null, () => null).BeforeAsync(invocation, action, CancellationToken.None);
            Names(["codemode {\"code\":\"tools.mcp__docs__search()\"}", "codemode {\"code\":\"tools.mcp__wiki__get()\"}"], [.. waits], "waits");
            // The handler's cancellation is the call's.
            using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
            Func<string, JsonData, CancellationToken, Task> observe = (_, _, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
            Check(await Failure(new McpToolCallHooks(null, () => observe).BeforeAsync(invocation, action, cancelled.Token).AsTask()) is OperationCanceledException, "cancelled");
            // tool_result handlers are the extensions' own.
            Check(await new McpToolCallHooks(new Hooks(_ => new()), () => handler).AfterAsync(invocation, action, ToolResult.Success("x"), false, CancellationToken.None) is { } patch &&
                patch.ToString() == "{\"after\":true}", "after delegated");
        }),
    ];

    private sealed class Hooks(Func<PreparedToolAction, PreparedToolCallHookResult> before) : IPreparedToolHooks
    {
        public ValueTask<PreparedToolCallHookResult> BeforeAsync(ToolInvocation invocation, PreparedToolAction validatedAction, CancellationToken cancellationToken) =>
            ValueTask.FromResult(before(validatedAction));
        public ValueTask<JsonData?> AfterAsync(ToolInvocation invocation, PreparedToolAction finalAction, ToolResult result, bool isError, CancellationToken cancellationToken) =>
            ValueTask.FromResult<JsonData?>(Parse("""{"after":true}"""));
    }

    private static void Contains(string text, string expected) =>
        Check(text.Contains(expected, StringComparison.Ordinal), $"expected <{expected}> in <{text}>");

    private static async Task<Exception> Failure(Task task)
    {
        try { await task; }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected a failure.");
    }
}
