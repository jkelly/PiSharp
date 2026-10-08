using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using NativeAgent = PiSharp.Agent.Agent;

internal static class NestedToolBrokerTests
{
    internal const string Prefix = "nested-broker-sdk.";
    private static readonly ModelDescriptor Model = new("nested-fixture", "openai-responses", "authored-nested-provider");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "registered hooks retain parent IDs and final native target denial", NativeFinalAuthorization),
        (Prefix + "actual two-turn Agent keeps nested calls out of transcript and delivers updates", TwoTurns),
        (Prefix + "owner retirement joins nested native finally before extension disposal", Retirement),
        (Prefix + "old generation refuses effects while replacement gets fresh metadata", Generations),
        (Prefix + "same registered tool can reenter with bounded depth", RepeatedTool),
        (Prefix + "ignored nested call and update receipt retain callback lease", IgnoredReceipts),
        (Prefix + "legacy dispatch has no invented broker or invocation ID", Legacy)
    ];

    private static async Task NativeFinalAuthorization()
    {
        await using var registry = new ExtensionRegistry();
        var events = new List<string>(); var effects = 0; ExtensionToolCallOutcome? denied = null;
        await registry.ActivateAsync("outer-owner", new Extension((registrations, initializationToken) =>
        {
            registrations.RegisterTool(Tool("outer", async (_, context, _) =>
            { denied = await context.ExecuteToolAsync("inner", JsonData.Parse("{\"path\":\"before\"}")); return Result("handled denial"); }));
            registrations.RegisterToolCallHandler(new("before", (value, _, _) =>
            {
                if (value.ToolName != "inner") return ValueTask.FromResult<ExtensionToolCallPatch?>(null);
                Equal("root", value.ParentToolCallId); Equal("root/1", value.ToolCallId); events.Add("before");
                return ValueTask.FromResult<ExtensionToolCallPatch?>(new(JsonData.Parse("{\"path\":\"after-hook\"}")));
            }));
            return ValueTask.CompletedTask;
        }));
        var native = new Adapter("inner", (_, _, _, _) => { effects++; return ValueTask.FromResult(ToolResult.Success("effect")); });
        var policy = new Policy((invocation, action, _) =>
        {
            if (invocation.Call.Name == "inner")
            { Equal("root", invocation.ParentToolCallId); Equal("denied", action.Target); Equal("after-hook", action.Arguments.Value.GetProperty("path").GetString()); events.Add("policy"); }
            return ValueTask.FromResult(new ToolActionAuthorization(action.Target != "denied"));
        });
        var binding = Binding(registry, policy);
        var executor = ToolInvoker.WithNestedCalls(binding.Adapters.Add(native), policy, new(31), binding.PreparedHooks,
            transforms: [(invocation, action, _) => ValueTask.FromResult(invocation.Call.Name == "inner" ? action with { Target = "denied" } : action)]);
        await executor.ExecuteAsync(Call(), default);
        Check(denied!.IsError); Equal("root/1", denied.ToolCallId); Equal("root", denied.ParentToolCallId);
        Equal(0, effects); Equal("before|policy", string.Join("|", events));
    }

    private static async Task TwoTurns()
    {
        await using var registry = new ExtensionRegistry();
        var updates = 0; var before = new List<string>(); var after = new List<string>();
        await registry.ActivateAsync("owner", new Extension((registrations, initializationToken) =>
        {
            registrations.RegisterTool(Tool("outer", async (_, context, token) =>
            {
                var actual = (IExtensionToolInvocationContext)context;
                Equal(32L, actual.SessionGeneration); Equal(0, actual.CallDepth); Check(context.Tools.Contains("inner"));
                ExtensionToolUpdateCallback multicast = static (_, _) => ValueTask.CompletedTask;
                multicast += multicast;
                Check((await context.ExecuteToolAsync("inner", JsonData.EmptyObject, new(token, multicast))).IsError);
                var outcome = await context.ExecuteToolAsync("inner", JsonData.EmptyObject, new(token, (partial, _) =>
                { Equal("partial", partial.Value.GetProperty("content")[0].GetProperty("text").GetString()); updates++; return ValueTask.CompletedTask; }));
                Equal("root/1", outcome.ToolCallId); Check(!outcome.IsError); return Result("outer:" + outcome.ToolCallId);
            }));
            registrations.RegisterTool(Tool("inner", async (_, context, token) =>
            {
                var actual = (IExtensionToolInvocationContext)context;
                Equal("root", actual.ParentToolCallId); Equal(1, actual.CallDepth); Equal(32L, actual.SessionGeneration);
                await actual.ReportUpdateAsync(Result("partial"), token); return Result("inner result");
            }));
            registrations.RegisterToolCallHandler(new("before", (value, _, _) =>
            { before.Add(value.ToolCallId + ":" + value.ParentToolCallId); return ValueTask.FromResult<ExtensionToolCallPatch?>(null); }));
            registrations.RegisterToolResultHandler(new("after", (value, _, _) =>
            { after.Add(value.ToolCallId + ":" + value.ParentToolCallId); return ValueTask.FromResult<ExtensionToolResultPatch?>(null); }));
            return ValueTask.CompletedTask;
        }));
        var binding = Binding(registry, scopes: new(32)); var sink = new Sink();
        var source = new Source([Call().AssistantMessage, new(Model.Api, Model.Provider, Model.Id, 2, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop)]);
        await using var agent = new NativeAgent(new(Model, source, binding.Tools), () => 1, sink);
        var run = await agent.PromptAsync([new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"invoke\",\"timestamp\":0}"))]);
        Equal(AgentLoopStopReason.Completed, run.Reason); Equal(2, source.Requests.Count); Equal(1, updates);
        var messages = source.Requests[1].Messages.Where(value => value.Role == "toolResult").ToArray();
        Equal(1, messages.Length); Equal("root", messages[0].WireBody.Value.GetProperty("toolCallId").GetString());
        Equal("root/1", messages[0].WireBody.Value.GetProperty("nestedCalls").GetProperty("calls")[0].GetProperty("id").GetString());
        Check(messages[0].WireBody.Value.GetProperty("nestedCalls").GetProperty("complete").GetBoolean());
        Equal("root:|root/1:root", string.Join("|", before)); Equal("root/1:root|root:", string.Join("|", after));
        Equal(1, sink.Events.OfType<ToolExecutionUpdated>().Count(value => value.Invocation.ParentToolCallId == "root"));
        Equal(1, sink.Events.OfType<ToolResultMessageEnded>().Count());
    }

    private static async Task Retirement()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var cleanupEntered = Gate(); var cleanup = Gate(); var nativeClosed = false; var disposed = false;
        await registry.ActivateAsync("owner", new Extension((registrations, initializationToken) =>
        {
            registrations.RegisterTool(Tool("outer", async (_, context, token) =>
            { var child = await context.ExecuteToolAsync("inner", JsonData.EmptyObject, new(token)); return child.Result; }));
            return ValueTask.CompletedTask;
        }, () => { Check(nativeClosed); disposed = true; return ValueTask.CompletedTask; }));
        var inner = new Adapter("inner", async (_, _, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return ToolResult.Success("unexpected"); }
            finally { cleanupEntered.TrySetResult(); await cleanup.Task; nativeClosed = true; }
        });
        var binding = Binding(registry); var executor = ToolInvoker.WithNestedCalls(binding.Adapters.Add(inner), new Policy(), new(33), binding.PreparedHooks);
        var work = executor.ExecuteAsync(Call(), default).AsTask(); Task? retirement = null;
        try
        {
            await Reach(entered.Task, work); retirement = registry.DisposeAsync().AsTask(); await Reach(cleanupEntered.Task, work);
            Check(!work.IsCompleted && !retirement.IsCompleted && !disposed);
        }
        finally { cleanup.TrySetResult(); await work; if (retirement is not null) await retirement; }
        Check(nativeClosed && disposed); Equal(ToolFailureKind.Canceled, work.Result.Failure!.Kind);
    }

    private static async Task Generations()
    {
        await using var registry = new ExtensionRegistry(); using var retired = new CancellationTokenSource();
        var generations = new List<long>(); IExtensionToolContext? retained = null;
        await registry.ActivateAsync("owner", new Extension((registrations, initializationToken) =>
        {
            registrations.RegisterTool(Tool("outer", (_, context, _) =>
            { retained = context; generations.Add(((IExtensionToolInvocationContext)context).SessionGeneration); return ValueTask.FromResult(Result("ok")); }));
            return ValueTask.CompletedTask;
        }));
        var old = Binding(registry, scopes: new(34, retired.Token)); await old.Tools.Single().Executor.ExecuteAsync(Call(), default);
        retired.Cancel(); Check((await retained!.ExecuteToolAsync("outer", JsonData.EmptyObject)).IsError);
        Equal(ToolFailureKind.Canceled, (await old.Tools.Single().Executor.ExecuteAsync(Call(), default)).Failure!.Kind);
        var replacement = Binding(registry, scopes: new(35)); await replacement.Tools.Single().Executor.ExecuteAsync(Call(), default);
        Equal("34|35", string.Join("|", generations));
    }

    private static async Task RepeatedTool()
    {
        await using var registry = new ExtensionRegistry(); var calls = new List<string>(); ExtensionToolCallOutcome? limit = null;
        await registry.ActivateAsync("owner", new Extension((registrations, initializationToken) =>
        {
            registrations.RegisterTool(Tool("outer", async (_, context, _) =>
            {
                calls.Add(((IExtensionToolInvocationContext)context).ToolCallId);
                var child = await context.ExecuteToolAsync("outer", JsonData.EmptyObject);
                if (child.IsError) limit = child;
                return Result("returned");
            })); return ValueTask.CompletedTask;
        }));
        var binding = Binding(registry, scopes: new(36, MaximumDepth: 2) { ExecutionMode = ToolExecutionMode.Sequential });
        await binding.Tools.Single().Executor.ExecuteAsync(Call(), default);
        Equal("root|root/1|root/1/1", string.Join("|", calls)); Check(limit!.IsError); Equal("root/1/1/1", limit.ToolCallId);
    }

    private static async Task IgnoredReceipts()
    {
        await using var registry = new ExtensionRegistry(); var entered = Gate(); var release = Gate(); var callbackReturned = Gate();
        IExtensionToolContext? retained = null; var updates = 0;
        await registry.ActivateAsync("owner", new Extension((registrations, initializationToken) =>
        {
            registrations.RegisterTool(Tool("outer", (input, context, token) =>
            {
                retained = context;
                _ = context.ExecuteToolAsync("inner", JsonData.EmptyObject, new(token, async (_, _) =>
                { updates++; entered.TrySetResult(); await release.Task; })).AsTask();
                callbackReturned.TrySetResult(); return ValueTask.FromResult(Result("outer returned"));
            })); return ValueTask.CompletedTask;
        }));
        var inner = new Adapter("inner", (invocation, action, progress, token) =>
        { _ = progress(ToolResult.Success("partial"), token).AsTask(); return ValueTask.FromResult(ToolResult.Success("inner returned")); });
        var binding = Binding(registry); var executor = ToolInvoker.WithNestedCalls(binding.Adapters.Add(inner), new Policy(), new(37), binding.PreparedHooks);
        var work = executor.ExecuteAsync(Call(), default).AsTask();
        try
        {
            await Reach(entered.Task, work); await callbackReturned.Task; Check(!work.IsCompleted);
            Check((await retained!.ExecuteToolAsync("inner", JsonData.EmptyObject)).IsError); Equal(1, updates);
        }
        finally { release.TrySetResult(); await work; }
        Check(!work.Result.IsError);
    }

    private static async Task Legacy()
    {
        await using var registry = new ExtensionRegistry(); ExtensionToolCallOutcome? outcome = null; IExtensionToolContext? captured = null;
        await registry.ActivateAsync("owner", new Extension((registrations, initializationToken) =>
        {
            registrations.RegisterTool(Tool("outer", async (_, context, _) =>
            { captured = context; outcome = await context.ExecuteToolAsync("inner", JsonData.EmptyObject); return Result("legacy"); }));
            return ValueTask.CompletedTask;
        }));
        await registry.InvokeToolAsync(registry.CaptureSnapshot(), "outer", JsonData.EmptyObject);
        Check(captured is not IExtensionToolInvocationContext); Check(captured!.Tools.IsEmpty);
        Check(outcome!.IsError && outcome.ToolCallId is null);
    }

    private static ExtensionAgentBinding Binding(ExtensionRegistry registry, IToolActionPolicy? policy = null, ToolInvocationScopeOptions? scopes = null) =>
        new(registry, policy ?? new Policy(), (_, arguments, _) => ValueTask.FromResult(arguments.Value.ValueKind == System.Text.Json.JsonValueKind.Object),
            options: new() { InvocationScopes = scopes });
    private static ExtensionToolDescriptor Tool(string name, ExtensionToolCallback execute) => new(name, name, "Nested broker probe",
        JsonData.Parse("{\"type\":\"object\"}"), execute);
    private static JsonData Result(string text) => ToolResult.Success(text).ToJson();
    private static ToolInvocation Call()
    { var call = new ToolCallContent("root", "outer", JsonData.EmptyObject); return new(new(Model.Api, Model.Provider, Model.Id, 1, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0); }
    private sealed class Extension(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask; }
    private sealed class Policy(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<ToolActionAuthorization>>? authorize = null) : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => authorize?.Invoke(invocation, action, token) ?? ValueTask.FromResult(new ToolActionAuthorization(true)); }
    private sealed class Adapter(string name, Func<ToolInvocation, PreparedToolAction, ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> execute) : IInvocationPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(Name,
            "invoke", PreparedToolActionKind.Path, invocation.Call.Arguments.Value.TryGetProperty("path", out var path) ? path.GetString()! : "allowed",
            invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Normal invocation pipeline was bypassed.");
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, PreparedToolAction action, ToolProgressCallback progress, CancellationToken token) => execute(invocation, action, progress, token);
    }
    private sealed class Sink : IAgentEventSink
    { internal ConcurrentQueue<AgentEvent> Events { get; } = new(); public ValueTask EmitAsync(AgentEvent value, CancellationToken token) { Events.Enqueue(value); return ValueTask.CompletedTask; } }
    private sealed class Source(AssistantMessage[] messages) : IChatTransport
    {
        internal List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var message = messages[Requests.Count]; Requests.Add(request);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            for (var index = 0; index < message.Content.Length; index++)
                if (message.Content[index] is ToolCallContent call)
                { yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(index, call); }
                else if (message.Content[index] is TextContent text)
                { yield return new TextStarted(index, new("")); yield return new TextEnded(index, text.Text); }
            yield return new StreamDone(message.StopReason, message); await Task.CompletedTask;
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Reach(Task entered, Task work) { if (await Task.WhenAny(entered, work) == work && !entered.IsCompleted) { await work; throw new InvalidOperationException("Work settled before the required boundary."); } await entered; }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Native nested broker assertion failed."); }
}
