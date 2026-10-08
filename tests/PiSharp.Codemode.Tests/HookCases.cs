using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Codemode;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

// Upstream: test/suite/agent-session-codemode.test.ts "routes nested calls through extension hooks" and "keeps structured
// content that tool_result handlers replace along with the content". A codemode tool runs in an extension registry whose
// tool_call and tool_result handlers see the nested calls, through the shared invoker (ToolInvoker.WithNestedCalls).
internal static partial class Program
{
    private static readonly ModelDescriptor HookModel = new("hook-fixture", "openai-responses", "authored-hook-provider");

    /// <summary>The codemode host over an extension tool invocation, as McpCodemode's host is over a session.</summary>
    private sealed class InvocationHost(IExtensionToolInvocationContext invocation, ImmutableArray<CodemodeNestedTool> declared) : ICodemodeHost
    {
        public ImmutableArray<CodemodeNestedTool> Tools => [.. declared.Where(tool => invocation.Tools.Contains(tool.Name))];
        public ICodemodeModelRuntime? Models => null;
        public async ValueTask<CodemodeNestedOutcome> ExecuteToolAsync(string name, JsonData? arguments, CancellationToken cancellationToken)
        {
            var outcome = await invocation.ExecuteToolAsync(name, arguments ?? JsonData.Null, new(cancellationToken));
            return new(outcome.ToolCallId, outcome.Result, outcome.IsError);
        }
        public IReadOnlyList<KeyValuePair<string, JsonData>> ReadStore() => [];
        public ValueTask AppendStoreEntryAsync(JsonData data, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class HookExtension(Func<IExtensionRegistry, ValueTask> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AllowAll : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(true));
    }

    private static JsonData HookResult(string json) => JsonData.Parse(json);

    private static async Task<string> RunHooked(string code, Action<IExtensionRegistry> handlers)
    {
        await using var registry = new ExtensionRegistry();
        ImmutableArray<CodemodeNestedTool> declared =
        [
            new("echo", "Echo text back.", JsonData.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}""")),
            new("stats", "Count files.", JsonData.Parse("""{"type":"object"}""")) { OutputSchema = JsonData.Parse("""{"type":"object","properties":{"files":{"type":"number"}}}""") }
        ];
        await registry.ActivateAsync("owner", new HookExtension(registrations =>
        {
            registrations.RegisterTool(new("codemode", "codemode", "Run JavaScript.", CodemodeToolDefinition.Parameters, async (arguments, context, token) =>
                await CodemodeExecutor.ExecuteAsync(((IExtensionToolInvocationContext)context).ToolCallId, arguments.Value.GetProperty("code").GetString()!,
                    new InvocationHost((IExtensionToolInvocationContext)context, declared), cancellationToken: token)) { Exposure = ToolExposure.ModelOnly });
            registrations.RegisterTool(new("echo", "echo", "Echo text back.", declared[0].Parameters, (arguments, _, _) =>
                ValueTask.FromResult(HookResult($$"""{"content":[{"type":"text","text":"echo: {{arguments.Value.GetProperty("text").GetString()}}"}]}"""))));
            registrations.RegisterTool(new("stats", "stats", "Count files.", declared[1].Parameters, (_, _, _) =>
                ValueTask.FromResult(HookResult("""{"content":[{"type":"text","text":"2 files"}],"structuredContent":{"files":2,"names":["a","b"]}}"""))));
            handlers(registrations);
            return ValueTask.CompletedTask;
        }), CancellationToken.None);
        var policy = new AllowAll();
        var binding = new ExtensionAgentBinding(registry, policy, (_, arguments, _) => ValueTask.FromResult(arguments.Value.ValueKind == JsonValueKind.Object),
            options: new() { InvocationScopes = new(41) { UncountedNestedCallTools = ["codemode"] } });
        var invoker = ToolInvoker.WithNestedCalls(binding.Adapters, policy, new(41) { UncountedNestedCallTools = ["codemode"] }, binding.PreparedHooks);
        var call = new ToolCallContent("root", "codemode", JsonData.Parse(JsonSerializer.Serialize(new { code })));
        ToolResult outcome = await invoker.ExecuteAsync(new(new(HookModel.Api, HookModel.Provider, HookModel.Id, 1, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0), default);
        return string.Join("\n", outcome.Content.Skip(1).Select(part => part.Text));
    }

    private static IEnumerable<(string, Func<Task>)> HookCases() =>
    [
        Case("hooks.nested-calls-route-through-tool-call-and-tool-result-handlers", async () =>
        {
            var seen = new List<string>();
            var text = await RunHooked("let blocked;\ntry { await tools.echo({ text: \"forbidden\" }); } catch (error) { blocked = error.message; }\nconst stats = await tools.stats({});\nreturn { blocked, stats };", registrations =>
            {
                registrations.RegisterToolCallHandler(new("block", (value, _, _) =>
                {
                    lock (seen) seen.Add($"call:{value.ToolName}:{value.ParentToolCallId}");
                    return ValueTask.FromResult<ExtensionToolCallPatch?>(value.ToolName == "echo" && value.Arguments.Value.GetProperty("text").GetString() == "forbidden"
                        ? new(Decision: JsonData.Parse("""{"block":true,"reason":"echo of forbidden text is blocked"}""")) : null);
                }));
                registrations.RegisterToolResultHandler(new("redact", (value, _, _) =>
                    ValueTask.FromResult<ExtensionToolResultPatch?>(value.ToolName == "stats" ? new(JsonData.Parse("""{"content":[{"type":"text","text":"redacted"}]}""")) : null)));
            });
            // Replacing content without replacing structured content drops the structured result.
            Equal("""{"blocked":"echo of forbidden text is blocked","stats":"redacted"}""", text, "hooked results");
            Check(seen.Contains("call:echo:root") && seen.Contains("call:stats:root"), "handlers saw the nested calls with their parent: " + string.Join(",", seen));
        }),
        Case("hooks.tool-result-handler-replacing-structured-content-keeps-it", async () =>
        {
            var text = await RunHooked("return await tools.stats({});", registrations =>
            {
                registrations.RegisterToolResultHandler(new("replace", (value, _, _) => ValueTask.FromResult<ExtensionToolResultPatch?>(value.ToolName == "stats"
                    ? new(JsonData.Parse("""{"content":[{"type":"text","text":"0 files"}],"structuredContent":{"files":0,"names":[]}}""")) : null)));
                registrations.RegisterToolResultHandler(new("details", (value, _, _) =>
                    ValueTask.FromResult<ExtensionToolResultPatch?>(value.ToolName == "stats" ? new(JsonData.Parse("""{"details":{"audited":true}}""")) : null)));
            });
            Equal("""{"files":0,"names":[]}""", text, "structured content kept");
        }),
    ];
}
