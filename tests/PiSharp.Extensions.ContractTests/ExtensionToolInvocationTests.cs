using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;
using NativeAgent = PiSharp.Agent.Agent;

internal static class ExtensionToolInvocationTests
{
    private static readonly ModelDescriptor Model = new("invocation-model", "openai-responses", "authored-invocation-provider");
    private static readonly JsonData Schema = JsonData.Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"],\"additionalProperties\":false}");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("actual two-turn Agent forwards original invocation identity and identical authorized final arguments with progress", TwoTurns),
        ("invocation-aware execution follows final denial and preserves legacy contexts and prepared adapters", AuthorityAndLegacy),
        ("concurrent registered tool invocations retain separate real IDs, arguments, contexts and update receipts", ConcurrentInvocations),
        ("cancellation and registry disposal join ignored update receipts and callback finally before revoking stale contexts", CancellationAndDisposal),
        ("invocation progress rejects malformed data, configured bounds, overlapping delivery and exhausted source quotas", ProgressBounds)
    ];

    private static async Task TwoTurns()
    {
        await using var registry = new ExtensionRegistry();
        JsonData? received = null; IExtensionToolInvocationContext? context = null; PreparedToolAction? authorized = null;
        await registry.ActivateAsync("owner", Extension(async (input, toolContext, token) =>
        {
            received = input; context = Invocation(toolContext);
            await context.ReportUpdateAsync(Partial("native update", context.ToolCallId), token);
            return Result(context.ToolCallId);
        }));
        var original = Args("before"); var final = Args("after"); var events = new List<AgentEvent>();
        var binding = new ExtensionAgentBinding(registry, Policy((invocation, action, token) =>
        {
            Equal("provider-call|fc-item", invocation.Call.Id); authorized = action;
            return ValueTask.FromResult(new ToolActionAuthorization(true));
        }), Validate, transforms: [(invocation, action, token) => ValueTask.FromResult(action with { Arguments = final })]);
        var source = new Source([Message("provider-call|fc-item", original), Finished()]);
        await using var agent = Agent(binding, source, events);
        var settled = await agent.PromptAsync([binding.CreateDeclarationMessage("actual native tool", 1), User()]);
        Equal(AgentLoopStopReason.Completed, settled.Reason); Equal(2, source.Requests.Count);
        True(ReferenceEquals(received, final) && ReferenceEquals(received, authorized!.Arguments), "Execution lost the identical authorized arguments.");
        Equal("provider-call|fc-item", context!.ToolCallId); Equal("before", original.Value.GetProperty("value").GetString());
        var update = events.OfType<ToolExecutionUpdated>().Single();
        Equal(context.ToolCallId, update.Invocation.Call.Id); Equal(context.ToolCallId, update.PartialResult.Details.Value.GetProperty("id").GetString());
        Equal("native update", update.PartialResult.Content.Single().Text);
        True(events.FindIndex(value => value is ToolExecutionUpdated) < events.FindIndex(value => value is ToolExecutionEnded));
        var wire = source.Requests[1].Messages.Single(value => value.Role == "toolResult").WireBody.Value;
        Equal(context.ToolCallId, wire.GetProperty("toolCallId").GetString());
        Equal("result:" + context.ToolCallId, wire.GetProperty("content")[0].GetProperty("text").GetString());
        var stale = await Throws<ExtensionRegistrationException>(() => context.ReportUpdateAsync(Partial("late", "late")).AsTask());
        Equal(ExtensionRegistrationFailure.InactiveScope, stale.Failure); Equal(1, events.OfType<ToolExecutionUpdated>().Count());
    }

    private static async Task AuthorityAndLegacy()
    {
        await using var registry = new ExtensionRegistry(); var calls = 0; IExtensionToolContext? captured = null;
        var scope = await registry.ActivateAsync("owner", Extension((input, context, token) =>
        { calls++; captured = context; return ValueTask.FromResult(Result("legacy")); }));
        True(scope.Features.Contains("tool-invocation-context"));
        var denied = Binding(registry, allow: false);
        Equal(ToolFailureKind.Blocked, (await Execute(denied, "deny-id", Args("valid"))).Failure!.Kind); Equal(0, calls);
        var invalid = new ExtensionAgentBinding(registry, Policy((invocation, action, token) => throw new InvalidOperationException("Invalid final action reached authorization.")),
            Validate, transforms: [(invocation, action, token) => ValueTask.FromResult(action with { Target = "other/1/tool" })]);
        Equal(ToolFailureKind.InvalidArguments, (await Execute(invalid, "invalid-id", Args("valid"))).Failure!.Kind); Equal(0, calls);
        await registry.InvokeToolAsync(registry.CaptureSnapshot(), "native_invocation", Args("legacy"));
        Equal(1, calls); True(captured is not IExtensionToolInvocationContext, "Original overload fabricated an invocation capability.");
        var old = new LegacyAdapter(); var executor = new ToolInvoker([old], Policy((invocation, action, token) => ValueTask.FromResult(new ToolActionAuthorization(true))));
        var message = Message("legacy-real-id", Args("legacy"), "legacy");
        Equal("legacy adapter", (await executor.ExecuteAsync(Call(message), CancellationToken.None)).Content.Single().Text); Equal(1, old.Calls);
    }

    private static async Task ConcurrentInvocations()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var release = Gate(); var count = 0;
        var contexts = new ConcurrentDictionary<string, IExtensionToolInvocationContext>(StringComparer.Ordinal);
        var arguments = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await registry.ActivateAsync("owner", Extension(async (input, context, token) =>
        {
            var actual = Invocation(context); True(contexts.TryAdd(actual.ToolCallId, actual));
            arguments[actual.ToolCallId] = input.Value.GetProperty("value").GetString()!;
            if (Interlocked.Increment(ref count) == 2) entered.TrySetResult();
            await release.Task;
            await actual.ReportUpdateAsync(Partial(arguments[actual.ToolCallId], actual.ToolCallId), token);
            return Result(actual.ToolCallId);
        }));
        var binding = Binding(registry); var first = Message("first|fc-one", Args("A")); var second = Message("second|fc-two", Args("B"));
        var updates = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        ValueTask Report(string id, ToolResult partial, CancellationToken token)
        { Equal(id, partial.Details.Value.GetProperty("id").GetString()); updates[id] = partial.Content.Single().Text; return ValueTask.CompletedTask; }
        var one = binding.Tools.Single().Executor.ExecuteAsync(Call(first), (partial, token) => Report(first.Content.OfType<ToolCallContent>().Single().Id, partial, token), CancellationToken.None).AsTask();
        var two = binding.Tools.Single().Executor.ExecuteAsync(Call(second), (partial, token) => Report(second.Content.OfType<ToolCallContent>().Single().Id, partial, token), CancellationToken.None).AsTask();
        try
        {
            await Reach(entered.Task, Task.WhenAll(one, two));
            True(!ReferenceEquals(contexts["first|fc-one"], contexts["second|fc-two"]));
            Equal("A", arguments["first|fc-one"]); Equal("B", arguments["second|fc-two"]);
        }
        finally { release.TrySetResult(); await Task.WhenAll(one, two); }
        Equal("A", updates["first|fc-one"]); Equal("B", updates["second|fc-two"]);
        Equal("result:first|fc-one", one.Result.Content.Single().Text); Equal("result:second|fc-two", two.Result.Content.Single().Text);
    }

    private static async Task CancellationAndDisposal()
    {
        foreach (var closeOwner in new[] { false, true })
        {
            await using var registry = new ExtensionRegistry(); using var cancellation = new CancellationTokenSource();
            var callbackEntered = Gate(); var callbackRelease = Gate(); var updateEntered = Gate(); var updateRelease = Gate();
            var callbackFinally = Gate(); var disposed = 0; IExtensionToolInvocationContext? context = null;
            await registry.ActivateAsync("owner", Extension(async (input, toolContext, token) =>
            {
                context = Invocation(toolContext);
                _ = context.ReportUpdateAsync(Partial("held", context.ToolCallId), token); // Trusted callback deliberately neglects await.
                callbackEntered.TrySetResult();
                try { await callbackRelease.Task; token.ThrowIfCancellationRequested(); return Result("unused"); }
                finally { callbackFinally.TrySetResult(); }
            }, () => { disposed++; return ValueTask.CompletedTask; }));
            var captured = registry.CaptureSnapshot();
            var dispatch = registry.InvokeToolAsync(captured, "native_invocation", Args("held"), "held-real-id", async (partial, token) =>
            { updateEntered.TrySetResult(); await updateRelease.Task; }, cancellation.Token).AsTask();
            Task? closing = null;
            try
            {
                await Reach(callbackEntered.Task, dispatch); await Reach(updateEntered.Task, dispatch);
                cancellation.Cancel(); if (closeOwner) closing = registry.DisposeAsync().AsTask();
                True(!dispatch.IsCompleted); Equal(0, disposed);
                var canceled = await Throws<OperationCanceledException>(() => context!.ReportUpdateAsync(Partial("late", "late")).AsTask());
                True(canceled.CancellationToken.IsCancellationRequested);
                callbackRelease.TrySetResult();
                // A callback-finally witness must precede release of its admitted progress delivery.
                await Reach(callbackFinally.Task, dispatch);
                True(!dispatch.IsCompleted); if (closing is not null) True(!closing.IsCompleted);
                Equal(0, disposed);
                updateRelease.TrySetResult(); await Throws<OperationCanceledException>(() => dispatch);
                if (closing is not null)
                {
                    var same = registry.DisposeAsync().AsTask(); True(ReferenceEquals(closing, same)); await closing; await same; Equal(1, disposed);
                    await Throws<ExtensionRegistrationException>(() => registry.InvokeToolAsync(captured, "native_invocation", Args("stale"), "stale-real-id", (partial, token) => ValueTask.CompletedTask).AsTask());
                }
                Equal(ExtensionRegistrationFailure.InactiveScope, (await Throws<ExtensionRegistrationException>(() => context!.ReportUpdateAsync(Partial("closed", "closed")).AsTask())).Failure);
            }
            finally
            {
                callbackRelease.TrySetResult(); updateRelease.TrySetResult();
                try { await dispatch; } catch (OperationCanceledException) { }
                if (closing is not null) await closing;
            }
        }
        await using var removedRegistry = new ExtensionRegistry(); IExtensionRegistration? handle = null; var effects = 0;
        await removedRegistry.ActivateAsync("owner", new ExtensionEntry((entries, token) =>
        { handle = entries.RegisterTool(Descriptor((input, context, cancellation) => { effects++; return ValueTask.FromResult(Result("unexpected")); })); return ValueTask.CompletedTask; }));
        var snapshot = removedRegistry.CaptureSnapshot(); handle!.Dispose();
        Equal(ExtensionRegistrationFailure.StaleSnapshot, (await Throws<ExtensionRegistrationException>(() => removedRegistry.InvokeToolAsync(snapshot,
            "native_invocation", Args("stale"), "removed-real-id", (partial, token) => ValueTask.CompletedTask).AsTask())).Failure); Equal(0, effects);
    }

    private static async Task ProgressBounds()
    {
        var duplicateRejected = false;
        try { _ = JsonData.Parse("{\"content\":[],\"content\":[]}"); } catch (JsonException) { duplicateRejected = true; }
        True(duplicateRejected, "Duplicate progress fields must already fail at owned JSON construction.");
        foreach (var partial in new[] { JsonData.Parse("[]"), JsonData.Parse("\"not-result\""), JsonData.Parse("{\"content\":17}"), Partial(new string('x', 1024), "oversized") })
        {
            await using var registry = new ExtensionRegistry(new() { MaximumJsonCharacters = 256 }); var updates = 0;
            await registry.ActivateAsync("owner", Extension((input, context, token) =>
            { try { _ = Invocation(context).ReportUpdateAsync(partial, token); } catch (Exception) { } return ValueTask.FromResult(Result("bounded")); }));
            var binding = Binding(registry);
            var result = await binding.Tools.Single().Executor.ExecuteAsync(Call(Message("bounded-real-id", Args("ok"))),
                (value, token) => { updates++; return ValueTask.CompletedTask; }, CancellationToken.None);
            Equal(ToolFailureKind.ExecutionError, result.Failure!.Kind); Equal(0, updates);
        }
        await using (var registry = new ExtensionRegistry())
        {
            var updates = 0;
            await registry.ActivateAsync("owner", Extension(async (input, context, token) =>
            { await Invocation(context).ReportUpdateAsync(Partial(new string('x', 64), "large"), token); return Result("unused"); }));
            var binding = new ExtensionAgentBinding(registry, Policy((invocation, action, token) => ValueTask.FromResult(new ToolActionAuthorization(true))), Validate,
                options: new() { ResultValues = new(MaximumCharacters: 16) });
            var result = await binding.Tools.Single().Executor.ExecuteAsync(Call(Message("result-limit-id", Args("ok"))),
                (partial, token) => { updates++; return ValueTask.CompletedTask; }, CancellationToken.None);
            Equal(ToolFailureKind.ExecutionError, result.Failure!.Kind); Equal(0, updates);
        }
        await OverlappingProgress();
        await using (var registry = new ExtensionRegistry())
        {
            await registry.ActivateAsync("owner", Extension(async (input, context, token) =>
            {
                var actual = Invocation(context); await actual.ReportUpdateAsync(Partial("one", actual.ToolCallId), token);
                await actual.ReportUpdateAsync(Partial("two", actual.ToolCallId), token); return Result("unused");
            }));
            var binding = Binding(registry); var events = new List<AgentEvent>(); var source = new Source([Message("quota-real-id", Args("ok")), Finished()]);
            await using var agent = Agent(binding, source, events, new(ProgressDelivery: new(ToolProgressDeliveryMode.SourceCompatible,
                MaximumUpdates: 1, MaximumPendingUpdates: 1, MaximumRetainedCharacters: 1024, MaximumContentBlocks: 4)));
            await agent.PromptAsync([User()]);
            Equal(1, events.OfType<ToolExecutionUpdated>().Count()); Equal(2, source.Requests.Count);
            Equal(ToolFailureKind.ExecutionError, events.OfType<ToolExecutionEnded>().Single().Outcome.Result.Failure!.Kind);
        }
        await using (var registry = new ExtensionRegistry(new() { MaximumJsonCharacters = 256 }))
        {
            var calls = 0; await registry.ActivateAsync("owner", Extension((input, context, token) => { calls++; return ValueTask.FromResult(Result("unused")); }));
            foreach (var id in new[] { "", " ", "id\0bad", "\ud800", new string('i', 257) })
                Equal(ExtensionRegistrationFailure.InvalidDescriptor, (await Throws<ExtensionRegistrationException>(() => registry.InvokeToolAsync(registry.CaptureSnapshot(),
                    "native_invocation", Args("ok"), id, (partial, token) => ValueTask.CompletedTask).AsTask())).Failure);
            ExtensionToolUpdateCallback first = (partial, token) => ValueTask.CompletedTask;
            ExtensionToolUpdateCallback second = (partial, token) => ValueTask.CompletedTask;
            await Throws<ExtensionRegistrationException>(() => registry.InvokeToolAsync(registry.CaptureSnapshot(), "native_invocation", Args("ok"), "real-id", first + second).AsTask());
            Equal(0, calls);
        }
    }

    private static async Task OverlappingProgress()
    {
        await using var registry = new ExtensionRegistry(); var entered = Gate(); var release = Gate(); var calls = 0;
        await registry.ActivateAsync("owner", Extension((input, context, token) =>
        {
            var actual = Invocation(context); _ = actual.ReportUpdateAsync(Partial("first", actual.ToolCallId), token);
            try { _ = actual.ReportUpdateAsync(Partial("overlap", actual.ToolCallId), token); } catch (ExtensionRegistrationException) { }
            return ValueTask.FromResult(Result("unused"));
        }));
        var invocation = registry.InvokeToolAsync(registry.CaptureSnapshot(), "native_invocation", Args("ok"), "overlap-real-id", async (partial, token) =>
        { calls++; entered.TrySetResult(); await release.Task; }).AsTask();
        try { await Reach(entered.Task, invocation); True(!invocation.IsCompleted); Equal(1, calls); }
        finally { release.TrySetResult(); }
        Equal(ExtensionRegistrationFailure.LimitExceeded, (await Throws<ExtensionRegistrationException>(() => invocation)).Failure); Equal(1, calls);
    }

    private static IExtensionToolInvocationContext Invocation(IExtensionToolContext context) => context as IExtensionToolInvocationContext
        ?? throw new InvalidOperationException("The actual invocation capability is absent.");
    private static JsonData Args(string value) => JsonData.Parse(JsonSerializer.Serialize(new { value }));
    private static JsonData Partial(string text, string id) => JsonData.Parse(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } }, details = new { id } }));
    private static JsonData Result(string id) => JsonData.Parse(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = "result:" + id } }, details = new { id } }));
    private static ExtensionToolDescriptor Descriptor(ExtensionToolCallback callback) => new("invocation", "native_invocation", "Native invocation probe", Schema, callback);
    private static ExtensionEntry Extension(ExtensionToolCallback callback, Func<ValueTask>? dispose = null) => new((registry, token) =>
    { registry.RegisterTool(Descriptor(callback)); return ValueTask.CompletedTask; }, dispose);
    private static ExtensionAgentBinding Binding(ExtensionRegistry registry, bool allow = true) => new(registry,
        Policy((invocation, action, token) => ValueTask.FromResult(new ToolActionAuthorization(allow))), Validate);
    private static ValueTask<bool> Validate(ExtensionToolRegistrationInfo tool, JsonData input, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(input.Value.ValueKind == JsonValueKind.Object && input.Value.EnumerateObject().Count() == 1 && input.Value.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String); }
    private static Task<ToolResult> Execute(ExtensionAgentBinding binding, string id, JsonData input) => binding.Tools.Single().Executor.ExecuteAsync(Call(Message(id, input)), CancellationToken.None).AsTask();
    private static ToolInvocation Call(AssistantMessage message) => new(message, (ToolCallContent)message.Content[0], 0);
    private static AssistantMessage Message(string id, JsonData input, string name = "native_invocation") => new(Model.Api, Model.Provider, Model.Id, 1,
        [new ToolCallContent(id, name, input)], TokenUsage.Zero, StopReason.ToolUse);
    private static AssistantMessage Finished() => new(Model.Api, Model.Provider, Model.Id, 2, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
    private static TranscriptEntry User() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"invoke\",\"timestamp\":0}"));
    private static NativeAgent Agent(ExtensionAgentBinding binding, Source source, List<AgentEvent> events, AgentOptions? options = null) =>
        new(new(Model, source, binding.Tools), () => 1, new Sink(events), options);
    private static PolicyEntry Policy(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<ToolActionAuthorization>> callback) => new(callback);
    private sealed class PolicyEntry(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<ToolActionAuthorization>> callback) : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => callback(invocation, action, token); }
    private sealed class ExtensionEntry(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask; }
    private sealed class LegacyAdapter : IPreparedToolAdapter
    {
        internal int Calls;
        public string Name => "legacy";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(Name, "invoke", PreparedToolActionKind.Extension,
            "legacy/1/tool", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) { Calls++; return ValueTask.FromResult(ToolResult.Success("legacy adapter")); }
    }
    private sealed class Sink(List<AgentEvent> events) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent value, CancellationToken token) { events.Add(value); return ValueTask.CompletedTask; } }
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
    private static async Task Reach(Task gate, Task operation)
    { await Task.WhenAny(gate, operation).WaitAsync(TimeSpan.FromSeconds(10)); if (!gate.IsCompleted) { await operation; throw new InvalidOperationException("Actual invocation settled before its gate."); } await gate; }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }
    private static void True(bool value, string message = "Expected true.") { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => True(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
