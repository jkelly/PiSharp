using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;
using NativeAgent = PiSharp.Agent.Agent;

internal static class ExtensionAgentBindingTests
{
    private static readonly ModelDescriptor Model = new("extension-model", "openai-responses", "offline-authored");
    private static readonly JsonData Schema = JsonData.Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"enum\":[\"before\",\"after\"]}},\"required\":[\"value\"],\"additionalProperties\":false}");
    private static readonly JsonData Arguments = JsonData.Parse("{ \"value\":\"before\" }");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("registered extension supplies owned result through actual two-turn Agent and provider projection", TwoTurns);
        yield return ("extension arguments revalidate and final policy rejects altered identity, denied input and unsupported validators", FinalActionAdmission);
        yield return ("captured extension bindings reject removal and old owner generations without replacing callback authority", GenerationAndRemoval);
        yield return ("extension Agent abort, session cancellation and registry disposal join actual callbacks and shared cleanup", CancellationAndDisposal);
        yield return ("extension result presence, termination, malformed output and binding quotas pass real invocation boundaries", ResultAndBindingBounds);
    }

    private static async Task TwoTurns()
    {
        await using var registry = new ExtensionRegistry();
        JsonData? received = null; IExtensionToolContext? context = null;
        var raw = JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"extension answer\"}],\"usage\":{\"cost\":0.005,\"nil\":null},\"structuredContent\":{\"programOnly\":1.0},\"opaqueResult\":{\"wide\":9007199254740993,\"nil\":null},\"isError\":false}");
        var scope = await registry.ActivateAsync("owner", new Extension((entries, _) =>
        {
            entries.RegisterTool(Descriptor((input, toolContext, _) =>
            { received = input; context = toolContext; return ValueTask.FromResult(raw); }));
            return ValueTask.CompletedTask;
        }));
        var validated = new List<JsonData>(); PreparedToolAction? transformed = null; PreparedToolAction? authorized = null;
        var finalArguments = JsonData.Parse("{ \"value\":\"after\" }");
        var binding = new ExtensionAgentBinding(registry, new Policy((_, action, _) =>
        { authorized = action; return ValueTask.FromResult(new ToolActionAuthorization(true)); }),
            (tool, input, token) => { validated.Add(input); return Validate(tool, input, token); },
            transforms: [(invocation, action, token) =>
            { var finalAction = action with { Arguments = finalArguments }; transformed = finalAction; return ValueTask.FromResult(finalAction); }]);
        Equal(scope.OwnerGeneration, binding.Registrations.Single().OwnerGeneration);
        Check(ReferenceEquals(Schema, binding.Registrations.Single().Parameters), "Snapshot copied or rewrote owned schema.");
        var declaration = binding.CreateDeclarationMessage("native extension tools", 123);
        Equal(Schema.ToString(), declaration.WireBody.Value.GetProperty("toolsAdded")[0].GetProperty("parameters").GetRawText());
        var source = new Source([Message(), Message(tool: false)]); var events = new List<AgentEvent>();
        await using var agent = Agent(binding, source, events);
        var result = await agent.PromptAsync([declaration, Input()]);
        Equal(AgentLoopStopReason.Completed, result.Reason); Equal(2, source.Requests.Count);
        Check(ReferenceEquals(transformed, authorized) && ReferenceEquals(received, authorized!.Arguments),
            "Authorization and extension execution lost the identical final action/owned arguments.");
        Equal(PreparedToolActionKind.Extension, authorized!.Kind); Equal("owner/" + scope.OwnerGeneration + "/extension", authorized.Target);
        Equal(2, validated.Count); Check(ReferenceEquals(Arguments, validated[0]) && ReferenceEquals(finalArguments, validated[1]), "Validation did not surround transformation.");
        Equal(scope.OwnerGeneration, context!.OwnerGeneration); Check(context is not IExtensionCommandContext, "Tool gained command context.");
        Check(context.OperationCancellationToken != context.ExtensionLifetimeCancellationToken, "Operation/lifetime ownership collapsed.");
        Equal("{ \"value\":\"before\" }", Arguments.ToString());
        var outcome = events.OfType<ToolExecutionEnded>().Single().Outcome;
        Equal("9007199254740993", outcome.Result.Property("opaqueResult")!.Value.GetProperty("wide").GetRawText());
        Equal("1.0", outcome.Result.StructuredContent!.Value.GetProperty("programOnly").GetRawText());
        Check(!outcome.Result.HasProperty("details") && !outcome.IsError, "Binding inserted details or error into source result.");
        var canonical = agent.Snapshot.Messages.Single(value => value.Role == "toolResult");
        Equal("0.005", canonical.WireBody.Value.GetProperty("usage").GetProperty("cost").GetRawText());
        Check(!canonical.WireBody.Value.TryGetProperty("details", out _) && !canonical.WireBody.Value.TryGetProperty("structuredContent", out _) &&
            !canonical.WireBody.Value.TryGetProperty("opaqueResult", out _), "Canonical model message copied execution-only properties.");
        Equal(canonical.WireBody.ToString(), source.Requests[1].Messages.Single(value => value.Role == "toolResult").WireBody.ToString());
        for (var index = 0; index < source.Requests[0].Messages.Length; index++)
            Equal(source.Requests[0].Messages[index].WireBody.ToString(), source.Requests[1].Messages[index].WireBody.ToString());
        using var request = new ResponsesKeyAuthRequestFactory(new Uri("https://offline.invalid/v1/responses"), Model,
            new(false, AllowedToolCallProviders: ImmutableHashSet.Create(Model.Provider))).Create(source.Requests[1], "inert-key");
        var payload = JsonData.Parse(await request.Content!.ReadAsStringAsync()).Value;
        Equal("native_echo", payload.GetProperty("tools")[0].GetProperty("name").GetString());
        Equal(Schema.ToString(), payload.GetProperty("tools")[0].GetProperty("parameters").GetRawText());
        var output = payload.GetProperty("input").EnumerateArray().Single(value => value.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        Equal("extension answer", output.GetProperty("output").GetString());
        Check(!payload.GetRawText().Contains("opaqueResult", StringComparison.Ordinal) && !payload.GetRawText().Contains("programOnly", StringComparison.Ordinal), "Provider copied execution metadata.");
        Check(events.FindIndex(value => value is ToolExecutionEnded) < events.FindIndex(value => value is ToolResultMessageEnded), "Execution/message end order changed.");
    }

    private static async Task FinalActionAdmission()
    {
        await using var registry = new ExtensionRegistry(); var effects = 0;
        await registry.ActivateAsync("owner", new Extension((entries, _) =>
        { entries.RegisterTool(Descriptor((_, _, _) => { effects++; return ValueTask.FromResult(JsonData.EmptyObject); })); return ValueTask.CompletedTask; }));
        Throws<ArgumentNullException>(() => new ExtensionAgentBinding(registry, Allow(), null!));
        var cases = new (JsonData Input, Func<PreparedToolAction, PreparedToolAction>? Transform, bool Allow, ToolFailureKind Failure)[]
        {
            (Arguments, action => action with { Target = "other-owner/1/tool" }, true, ToolFailureKind.InvalidArguments),
            (Arguments, action => action with { Kind = PreparedToolActionKind.Path }, true, ToolFailureKind.InvalidArguments),
            (Arguments, action => action with { Kind = PreparedToolActionKind.Command, WorkingDirectory = "/authored" }, true, ToolFailureKind.InvalidArguments),
            (Arguments, action => action with { CommandArguments = ["host-effect"] }, true, ToolFailureKind.InvalidArguments),
            (Arguments, action => action with { Environment = ImmutableDictionary<string, string>.Empty.Add("EFFECT", "1") }, true, ToolFailureKind.InvalidArguments),
            (Arguments, action => action with { Arguments = JsonData.Parse("{\"value\":\"outside-schema\"}") }, true, ToolFailureKind.InvalidArguments),
            (JsonData.Parse("{\"value\":\"before\",\"extra\":null}"), null, true, ToolFailureKind.InvalidArguments),
            (JsonData.Parse("{\"value\":null}"), null, true, ToolFailureKind.InvalidArguments),
            (JsonData.EmptyObject, null, true, ToolFailureKind.InvalidArguments),
            (Arguments, action => action with { Arguments = JsonData.Parse("{\"value\":\"after\"}") }, false, ToolFailureKind.Blocked)
        };
        foreach (var row in cases)
        {
            var policyCalls = 0;
            var binding = new ExtensionAgentBinding(registry, new Policy((_, action, _) =>
            { policyCalls++; Equal("after", action.Arguments.Value.GetProperty("value").GetString()); return ValueTask.FromResult(new ToolActionAuthorization(row.Allow)); }),
                Validate, transforms: row.Transform is null ? null : [(invocation, action, token) => ValueTask.FromResult(row.Transform!(action))]);
            var result = await Execute(binding, row.Input);
            Equal(row.Failure, result.Failure!.Kind); Equal(row.Allow ? 0 : 1, policyCalls); Equal(0, effects);
        }
        var unsupported = new ExtensionAgentBinding(registry, Allow(), (_, _, _) => ValueTask.FromResult(false));
        Equal(ToolFailureKind.InvalidArguments, (await Execute(unsupported, Arguments)).Failure!.Kind); Equal(0, effects);
        var thrown = new ExtensionAgentBinding(registry, Allow(), (_, _, _) => throw new NotSupportedException("schema evaluator does not support this schema"));
        Equal(ToolFailureKind.InvalidArguments, (await Execute(thrown, Arguments)).Failure!.Kind); Equal(0, effects);
        var valid = new ExtensionAgentBinding(registry, Allow(), Validate);
        Check((await Execute(valid, Arguments)).Failure is null, "Valid declared input did not execute."); Equal(1, effects);
    }

    private static async Task GenerationAndRemoval()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var registry = new ExtensionRegistry(); IExtensionRegistration? handle = null; var oldCalls = 0; var newCalls = 0;
        var entered = Gate(); var release = Gate();
        var first = await registry.ActivateAsync("owner", new Extension((entries, _) =>
        { handle = entries.RegisterTool(Descriptor(async (_, _, _) => { oldCalls++; entered.TrySetResult(); await release.Task; return JsonData.EmptyObject; })); return ValueTask.CompletedTask; }));
        var old = new ExtensionAgentBinding(registry, Allow(), Validate);
        var admitted = Execute(old, Arguments);
        try
        {
            await Stage(entered.Task, admitted, "captured tool lease", deadline.Token);
            handle!.Dispose();
            Equal(ToolFailureKind.ExecutionError, (await Execute(old, Arguments)).Failure!.Kind); Equal(1, oldCalls);
            Equal(0, registry.CaptureSnapshot().Tools.Length); Equal(1, old.Registrations.Length);
            Check(!admitted.IsCompleted, "Registration removal completed an already admitted callback.");
            release.TrySetResult(); Check((await admitted.WaitAsync(deadline.Token)).Failure is null, "Retired admitted lease lost result authority.");
        }
        finally { release.TrySetResult(); try { await admitted; } catch { } }
        await first.DisposeAsync();
        var next = await registry.ActivateAsync("owner", new Extension((entries, _) =>
        { entries.RegisterTool(Descriptor((_, _, _) => { newCalls++; return ValueTask.FromResult(JsonData.Parse("{\"content\":[],\"details\":null}")); })); return ValueTask.CompletedTask; }));
        Check(next.OwnerGeneration > first.OwnerGeneration, "Owner generation was reused.");
        Equal(ToolFailureKind.ExecutionError, (await Execute(old, Arguments)).Failure!.Kind); Equal(0, newCalls);
        var current = new ExtensionAgentBinding(registry, Allow(), Validate);
        var result = await Execute(current, Arguments); Check(!result.IsError && result.HasProperty("details"), "New registration failed.");
        Equal(JsonValueKind.Null, result.Details.Value.ValueKind); Equal(1, newCalls); Equal(1, oldCalls);
        Equal(first.OwnerGeneration, old.Registrations.Single().OwnerGeneration);
        Equal(next.OwnerGeneration, current.Registrations.Single().OwnerGeneration);
        // Dynamic metadata changes leave the captured binding unchanged; the host must publish new declarations explicitly.
        Equal(first.OwnerGeneration, old.Snapshot.Tools.Single().OwnerGeneration);
    }

    private static async Task CancellationAndDisposal()
    {
        foreach (var mode in new[] { "abort", "caller", "session", "registry" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var caller = new CancellationTokenSource(); using var session = new CancellationTokenSource();
            var entered = Gate(); var cleanupEntered = Gate(); var cleanupRelease = Gate(); var shutdownEntered = Gate(); var shutdownRelease = Gate();
            var cleanupFinished = false; var canceledAtCleanup = false; var effects = 0; var shutdowns = 0;
            var registry = new ExtensionRegistry(); IExtensionToolContext? capturedContext = null;
            var scope = await registry.ActivateAsync("owner", new Extension((entries, _) =>
            {
                entries.RegisterTool(Descriptor(async (_, context, token) =>
                {
                    capturedContext = context; entered.TrySetResult();
                    try { await Gate().Task.WaitAsync(token); effects++; return JsonData.EmptyObject; }
                    finally { canceledAtCleanup = token.IsCancellationRequested; cleanupEntered.TrySetResult(); await cleanupRelease.Task; cleanupFinished = true; }
                }));
                return ValueTask.CompletedTask;
            }, async () => { shutdowns++; shutdownEntered.TrySetResult(); await shutdownRelease.Task; }));
            var binding = new ExtensionAgentBinding(registry, Allow(), Validate, sessionCancellationToken: session.Token);
            var source = new Source([Message(), Message(tool: false)]); var events = new List<AgentEvent>();
            await using var agent = Agent(binding, source, events, endAfterTurn: true);
            var run = agent.PromptAsync(Input(), caller.Token); Task? disposeOne = null; Task? disposeTwo = null;
            try
            {
                await Stage(entered.Task, run, "extension callback admitted", deadline.Token);
                Equal(session.Token, capturedContext!.SessionCancellationToken);
                if (mode == "abort") Check(agent.Abort(), "Actual Agent abort was not admitted.");
                else if (mode == "caller") caller.Cancel();
                else if (mode == "session") session.Cancel();
                else
                {
                    disposeOne = registry.DisposeAsync().AsTask(); disposeTwo = registry.DisposeAsync().AsTask();
                    Check(ReferenceEquals(disposeOne, disposeTwo), "Registry disposal did not share actual settlement.");
                    Equal(0, registry.CaptureSnapshot().Tools.Length);
                    Equal(ToolFailureKind.ExecutionError, (await Execute(binding, Arguments)).Failure!.Kind);
                }
                await Stage(cleanupEntered.Task, run, "actual canceled callback finally", deadline.Token);
                Check(canceledAtCleanup && !cleanupFinished && !run.IsCompleted, "Cancellation did not reach actual callback or bypassed its cleanup.");
                Check(!events.OfType<ToolExecutionEnded>().Any() && shutdowns == 0, "Terminal event or extension shutdown overtook callback cleanup.");
                if (disposeOne is not null) Check(!disposeOne.IsCompleted && !disposeTwo!.IsCompleted, "Registry disposal bypassed callback join.");
                cleanupRelease.TrySetResult(); await run.WaitAsync(deadline.Token); await agent.WaitForIdleAsync().WaitAsync(deadline.Token);
                Check(cleanupFinished, "Agent completed before callback finally."); Equal(0, effects); Equal(1, source.Requests.Count);
                Equal(ToolFailureKind.Canceled, events.OfType<ToolExecutionEnded>().Single().Outcome.Result.Failure!.Kind);
                if (mode is "abort" or "caller") Check(agent.Snapshot.CancellationRequested, "Public Agent lost actual run cancellation.");
                if (mode == "registry")
                {
                    await Stage(shutdownEntered.Task, disposeOne!, "extension shutdown after callback join", deadline.Token);
                    Check(!disposeOne!.IsCompleted, "Registry skipped extension shutdown."); shutdownRelease.TrySetResult();
                    await Task.WhenAll(disposeOne, disposeTwo!).WaitAsync(deadline.Token); Equal(1, shutdowns);
                }
            }
            finally
            {
                cleanupRelease.TrySetResult(); shutdownRelease.TrySetResult(); agent.Abort(); caller.Cancel(); session.Cancel();
                try { await run; } catch { }
                await registry.DisposeAsync();
            }
            Check(scope.ExtensionLifetimeCancellationToken.IsCancellationRequested, "Registry lifetime was not canceled.");
        }
    }

    private static async Task ResultAndBindingBounds()
    {
        foreach (var raw in new[] { "{}", "{\"content\":null,\"details\":null,\"usage\":null}", "{\"content\":[],\"usage\":{\"n\":1.0}}", "{\"content\":[{\"type\":\"text\",\"text\":\"stop\"}],\"terminate\":true}" })
        {
            await using var registry = new ExtensionRegistry();
            await registry.ActivateAsync("owner", new Extension((entries, _) =>
            { entries.RegisterTool(Descriptor((_, _, _) => ValueTask.FromResult(JsonData.Parse(raw)))); return ValueTask.CompletedTask; }));
            var binding = new ExtensionAgentBinding(registry, Allow(), Validate);
            var source = new Source([Message(), Message(tool: false)]); var events = new List<AgentEvent>();
            var terminates = raw.Contains("terminate", StringComparison.Ordinal);
            await using var agent = Agent(binding, source, events, endAfterTurn: !terminates);
            await agent.PromptAsync(Input()); Equal(1, source.Requests.Count);
            var owned = events.OfType<ToolExecutionEnded>().Single().Outcome.Result;
            var expected = JsonData.Parse(raw).Value;
            Equal(expected.TryGetProperty("content", out _), owned.HasProperty("content"));
            Equal(expected.TryGetProperty("details", out _), owned.HasProperty("details"));
            Equal(expected.TryGetProperty("usage", out _), owned.HasProperty("usage"));
            var canonical = agent.Snapshot.Messages.Single(value => value.Role == "toolResult").WireBody.Value;
            Equal(expected.TryGetProperty("details", out _), canonical.TryGetProperty("details", out _));
            Equal(expected.TryGetProperty("usage", out _), canonical.TryGetProperty("usage", out _));
            Equal(JsonValueKind.Array, canonical.GetProperty("content").ValueKind);
            Check(!canonical.GetProperty("isError").GetBoolean(), "Valid nullable/absent result became an error.");
            Equal(terminates, owned.Terminate);
        }
        await using (var registry = new ExtensionRegistry())
        {
            var effects = 0;
            var scope = await registry.ActivateAsync("owner", new Extension((entries, _) =>
            { entries.RegisterTool(Descriptor((_, _, _) => { effects++; return ValueTask.FromResult(JsonData.Parse("{\"opaqueResult\":\"too large\"}")); })); return ValueTask.CompletedTask; }));
            Throws<InvalidOperationException>(() => new ExtensionAgentBinding(registry, Allow(), Validate,
                options: new(MaximumDeclarationCharacters: 2)));
            Throws<InvalidOperationException>(() => new ExtensionAgentBinding(registry, Allow(), Validate,
                options: new(MaximumDeclarationBytes: 2)));
            Equal(0, effects);
            var binding = new ExtensionAgentBinding(registry, Allow(), Validate,
                options: new(MaximumDeclarationCharacters: 1_000) { ResultValues = new(MaximumCharacters: 2) });
            Equal(ToolFailureKind.ExecutionError, (await Execute(binding, Arguments)).Failure!.Kind); Equal(1, effects);
            Throws<InvalidOperationException>(() => binding.CreateDeclarationMessage(new string('x', 1_001), 123));
            Throws<ArgumentException>(() => binding.CreateDeclarationMessage("\ud800", 123));
            scope.RegisterTool(Descriptor((_, _, _) => { effects++; return ValueTask.FromResult(JsonData.EmptyObject); }) with { RegistrationId = "other", Name = "other_tool" });
            Throws<InvalidOperationException>(() => new ExtensionAgentBinding(registry, Allow(), Validate, options: new(MaximumTools: 1)));
            Equal(1, effects);
            await scope.DisposeAsync();
        }
        foreach (var raw in new[] { "null", "{\"content\":42}", "{\"opaqueResult\":1e400}", "{\"usage\":{\"x\":1,\"\\u0078\":2}}", "{\"details\":\"\\ud800\"}" })
        {
            await using var registry = new ExtensionRegistry();
            await registry.ActivateAsync("owner", new Extension((entries, _) =>
            { entries.RegisterTool(Descriptor((_, _, _) => ValueTask.FromResult(JsonData.Parse(raw)))); return ValueTask.CompletedTask; }));
            var binding = new ExtensionAgentBinding(registry, Allow(), Validate);
            var failure = await Execute(binding, Arguments);
            Equal(ToolFailureKind.ExecutionError, failure.Failure!.Kind);
            Check(!failure.ToJson().ToString().Contains("opaqueResult", StringComparison.Ordinal), "Unadmitted callback value gained result authority.");
        }
    }

    // This validator implements this fixture's ENTIRE schema. Every other schema is explicitly rejected.
    private static ValueTask<bool> Validate(ExtensionToolRegistrationInfo tool, JsonData arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var value = arguments.Value;
        return ValueTask.FromResult(tool.Parameters.ToString() == Schema.ToString() && value.ValueKind == JsonValueKind.Object &&
            value.EnumerateObject().Count() == 1 && value.TryGetProperty("value", out var field) && field.ValueKind == JsonValueKind.String &&
            field.GetString() is "before" or "after");
    }
    private static ExtensionToolDescriptor Descriptor(ExtensionToolCallback callback) => new("extension", "native_echo", "native callback", Schema, callback);
    private static Policy Allow() => new((_, _, _) => ValueTask.FromResult(new ToolActionAuthorization(true)));
    private static NativeAgent Agent(ExtensionAgentBinding binding, Source source, List<AgentEvent> events, bool endAfterTurn = false) =>
        new(new(Model, source, binding.Tools, Hooks: endAfterTurn ? new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End)) : null),
            () => 123, new Sink(events));
    private static async Task<ToolResult> Execute(ExtensionAgentBinding binding, JsonData arguments)
    {
        var message = Message(arguments: arguments);
        return await binding.Tools.Single().Executor.ExecuteAsync(new(message, (ToolCallContent)message.Content[0], 0), CancellationToken.None);
    }
    private static AssistantMessage Message(bool tool = true, JsonData? arguments = null) => new(Model.Api, Model.Provider, Model.Id, 123,
        tool ? [new ToolCallContent("extension-call", "native_echo", arguments ?? Arguments)] : [new TextContent("done")], TokenUsage.Zero, tool ? StopReason.ToolUse : StopReason.Stop);
    private static TranscriptEntry Input() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"run native tool\",\"timestamp\":123}"));
    private sealed class Policy(Func<ToolInvocation, PreparedToolAction, CancellationToken, ValueTask<ToolActionAuthorization>> callback) : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => callback(invocation, action, token); }
    private sealed class Extension(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken) => initialize(registry, cancellationToken);
        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
    private sealed class Sink(List<AgentEvent> events) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) { events.Add(observation); return ValueTask.CompletedTask; } }
    private sealed class Source(AssistantMessage[] messages) : IChatTransport
    {
        public List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = messages[Requests.Count]; Requests.Add(request);
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
    private static async Task Stage(Task witness, Task run, string stage, CancellationToken token)
    {
        await Task.WhenAny(witness, run).WaitAsync(token);
        if (!witness.IsCompleted) { await run; throw new InvalidOperationException("Actual execution settled before " + stage); }
        await witness;
    }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, actual {actual}.");
}
