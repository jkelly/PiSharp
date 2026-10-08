using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

internal static class AbortLifecycleTests
{
    private static readonly ModelDescriptor Model = new("abort-model", "openai-responses", "fixture-provider");
    public static object? DifferentialEvidence { get; private set; }
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("canceled high-level finish case matches genuine complete aborted lifecycle", FrozenAbortedLifecycle),
        ("abort before stream acquisition commits an honest fallback and preserves legacy propagation", BeforeStartAndLegacy),
        ("partial text/tool cancellation awaits cleanup and last end listener without effects", PartialAndCleanup),
        ("abort finalization hook faults retain committed assistant and do not invent end delivery", FinishFaults),
        ("canceled authoritative aborts retain normal stream bounds", AuthoritativeBounds),
        ("abort drains a full bounded progress channel before shared settlement", BoundedDrain)
    ];

    private static async Task FrozenAbortedLifecycle()
    {
        using var input = Fixture("core.input.json", "815203f5a198cc2ddf7cb1c7d6856adf46ef051127861391af9e711f7c7500ba");
        using var golden = Fixture("core.expected.json", "9f63f961256a9af071c90220adbca8861e6c813fcfdfcaa4ab9fffaa37169e23");
        var scenario = input.RootElement.GetProperty("scenarios").EnumerateArray()
            .Single(value => value.GetProperty("scenarioId").GetString() == "aborted-hard-exit-ignores-continue");
        var expected = golden.RootElement.GetProperty("observations").GetProperty("scenarios").EnumerateArray()
            .Single(value => value.GetProperty("scenarioId").GetString() == "aborted-hard-exit-ignores-continue");
        var modelJson = input.RootElement.GetProperty("model");
        var model = new ModelDescriptor(modelJson.GetProperty("id").GetString()!, modelJson.GetProperty("api").GetString()!,
            modelJson.GetProperty("provider").GetString()!);
        var final = PiWireJson.ReadMessage(scenario.GetProperty("providerTurns")[0]);
        var source = new Source(final, authoritative: true, gateCleanup: true);
        var finishEntered = Gate(); var finishRelease = Gate(); var endEntered = Gate(); var endRelease = Gate();
        var events = new List<AgentEvent>(); var calls = new CounterTool();
        var finishCount = 0; var finishCanceled = false;
        var hooks = new AgentHooks(FinishTurnDecision: async (turn, token) =>
        {
            finishCount++; finishCanceled = token.IsCancellationRequested;
            Check(finishCanceled, "Finish callback received an uncanceled work token.");
            Check(source.Cleanups == 1, "Finish ran before cleanup.");
            Same(expected.GetProperty("hooks").EnumerateArray().Single(value => value.GetProperty("kind").GetString() == "finish_enter")
                .GetProperty("message"), PiWireJson.WriteMessage(turn.Result.Chat.Message).Value);
            finishEntered.TrySetResult(); await finishRelease.Task;
            return AgentLoopFinishAction.Continue;
        });
        await using var agent = new NativeAgent(new(model, source, [new("lookup", calls)], Hooks: hooks), () => 1700000000000,
            new Sink(async (observation, token) =>
            {
                token.ThrowIfCancellationRequested(); // Models a durable append accepting only settlement tokens.
                events.Add(observation);
                if (observation is AgentLoopEnded) { endEntered.TrySetResult(); await endRelease.Task; }
            }));
        var prompts = scenario.GetProperty("prompts").EnumerateArray().Select(Entry).ToImmutableArray();
        var run = agent.PromptAsync(prompts);
        try
        {
            await source.Entered.Task;
            Check(agent.Abort() && source.WorkToken.IsCancellationRequested, "Actual native abort control was absent.");
            source.Release.TrySetResult(); await source.CleanupEntered.Task;
            Equal(0, finishCount); Equal(prompts.Length, agent.Snapshot.Messages.Length);
            Check(!run.IsCompleted && agent.Snapshot.IsRunning, "Cleanup was bypassed.");
            source.CleanupRelease.TrySetResult(); await finishEntered.Task;
            Equal(0, events.OfType<AgentLoopTurnEnded>().Count()); Equal(0, events.OfType<AgentLoopEnded>().Count());
            foreach (var message in scenario.GetProperty("steeringMessages").EnumerateArray()) agent.Steer(Entry(message));
            foreach (var message in scenario.GetProperty("followUpMessages").EnumerateArray()) agent.FollowUp(Entry(message));
            finishRelease.TrySetResult(); await endEntered.Task;
            var idle = agent.WaitForIdleAsync();
            Check(!idle.IsCompleted && !run.IsCompleted && agent.Snapshot.IsRunning, "End listener was excluded from settlement.");
            endRelease.TrySetResult(); var result = await run; await idle;
            Equal(AgentLoopStopReason.ChatFailure, result.Reason); Equal(1, finishCount); Equal(0, calls.Calls);
            Equal(1, source.Requests.Count); Equal(1, source.Cleanups);
            Check(agent.Snapshot.CancellationRequested && finishCanceled, "Native aborted signal fact disappeared.");
            Equal(AgentFailureKind.Canceled, agent.Snapshot.Failure);
            Equal(1, agent.Snapshot.SteeringCount); Equal(1, agent.Snapshot.FollowUpCount);
            Same(expected.GetProperty("events"), JsonSerializer.SerializeToElement(events.Select(Project).Where(value => value is not null)));
            Same(expected.GetProperty("finalResult"), JsonSerializer.SerializeToElement(result.Transcript.Select(value => value.WireBody.Value)));
            Same(expected.GetProperty("finalContextMessages"), JsonSerializer.SerializeToElement(agent.Snapshot.Messages.Select(value => value.WireBody.Value)));
            Same(expected.GetProperty("requests")[0].GetProperty("context").GetProperty("messages"),
                JsonSerializer.SerializeToElement(source.Requests[0].Messages.Select(value => value.WireBody.Value)));
            Equal(model, source.Requests[0].Model);
            Check(expected.GetProperty("checks").GetProperty("finalSignalAborted").GetBoolean(), "Frozen source control changed.");
            Check(!Equivalent(JsonData.Parse("1").Value, JsonData.Parse("1.0").Value), "Raw numeric tokens were normalized.");
            Check(!Equivalent(JsonData.Parse("""{"x":null}""").Value, JsonData.Parse("{}").Value), "Missing/null were normalized.");
            DifferentialEvidence = new
            {
                profile = "high-level-aborted-work-settlement-v1",
                sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
                scenarioId = "aborted-hard-exit-ignores-continue",
                sourceSignalAborted = true, nativeSignalAborted = agent.Snapshot.CancellationRequested,
                exactCompleteFinalMessage = true, exactSelectedLifecycleEvents = true,
                thinkingLevelStamped = "off", providerRequests = 1, effects = 0
            };
        }
        finally
        {
            source.Release.TrySetResult(); source.CleanupRelease.TrySetResult(); finishRelease.TrySetResult(); endRelease.TrySetResult();
            try { await run; } catch { }
        }
    }

    private static async Task BeforeStartAndLegacy()
    {
        var source = new Source(Message());
        NativeAgent? observed = null;
        await using var agent = observed = Create(source, new Sink((observation, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (observation is AgentLoopStarted) Check(observed!.Abort(), "Early admitted run could not abort.");
            return ValueTask.CompletedTask;
        }));
        var result = await agent.PromptAsync(Input("before acquisition"));
        Equal(0, source.Requests.Count); Equal(AgentLoopStopReason.ChatFailure, result.Reason);
        var aborted = result.Turns.Single().Result.Chat.Message;
        Equal(StopReason.Aborted, aborted.StopReason); Check(aborted.Content.IsEmpty, "Unobserved content was manufactured.");
        Equal(123L, aborted.Timestamp); Equal(Model.Api, aborted.Api); Equal(Model.Provider, aborted.Provider); Equal(Model.Id, aborted.Model);
        Check(aborted.ExtraProperties is not null && !aborted.ExtraProperties.TryGet("responseId", out _), "Fallback invented provider metadata.");
        Equal(2, agent.Snapshot.Messages.Length); Equal(AgentFailureKind.Canceled, agent.Snapshot.Failure);
        Check(agent.Snapshot.CancellationRequested, "Early cancellation flag was lost.");
        Equal("off", aborted.ExtraProperties!.Values["thinkingLevel"].Value.GetString());

        var legacySource = new Source(Message(), partial: true);
        await using var legacy = Create(legacySource, options: new(CancellationBehavior: AgentCancellationBehavior.Propagate));
        var running = legacy.PromptAsync(Input("legacy"));
        await legacySource.Entered.Task; legacy.Abort();
        await ThrowsAsync<OperationCanceledException>(() => running);
        Equal(1, legacySource.Cleanups); Equal(1, legacy.Snapshot.Messages.Length);
    }

    private static async Task PartialAndCleanup()
    {
        var observedDelta = Gate(); var endEntered = Gate(); var endRelease = Gate();
        var source = new Source(Message(), partial: true, gateCleanup: true);
        var effects = new CounterTool(); var events = new List<AgentEvent>(); var finishCanceled = false;
        await using var agent = Create(source, new Sink(async (observation, token) =>
        {
            token.ThrowIfCancellationRequested();
            events.Add(observation);
            if (observation is TurnStreamObserved { Event: ToolCallDelta }) observedDelta.TrySetResult();
            if (observation is AgentLoopEnded) { endEntered.TrySetResult(); await endRelease.Task; }
        }), new AgentHooks(FinishTurn: (_, token) =>
        { finishCanceled = token.IsCancellationRequested; return ValueTask.CompletedTask; }), [new("read", effects)], new(StreamCapacity: 1));
        var running = agent.PromptAsync(Input("partial"));
        try
        {
            await observedDelta.Task;
            agent.Steer(Input("retain steering")); agent.FollowUp(Input("retain follow-up"));
            agent.Abort(); await source.CleanupEntered.Task;
            Equal(1, agent.Snapshot.Messages.Length);
            Check(!running.IsCompleted && agent.Snapshot.IsRunning, "Canceled body cleanup did not own settlement.");
            source.CleanupRelease.TrySetResult(); await endEntered.Task;
            var idle = agent.WaitForIdleAsync(); Check(!idle.IsCompleted, "Final listener was not awaited.");
            var committed = PiWireJson.ReadMessage(agent.Snapshot.Messages[^1].WireBody.Value);
            Equal(StopReason.Aborted, committed.StopReason);
            Equal("partial\n\uD83D\uDE00", ((TextContent)committed.Content[0]).Text);
            Equal("signature", ((TextContent)committed.Content[0]).ExtraProperties!.Values["textSignature"].Value.GetString());
            var call = (ToolCallContent)committed.Content[1];
            Equal("call-partial", call.Id); Equal("read", call.Name);
            Equal("{}", call.Arguments.ToString()); // Raw preview remains display-only; no repair/execution is authorized.
            Equal("resp-partial", committed.ExtraProperties!.Values["responseId"].Value.GetString());
            Equal("1.0", committed.ExtraProperties.Values["future"].Value.GetProperty("scale").GetRawText());
            Check(committed.ExtraProperties.Values["future"].Value.GetProperty("opaque").ValueKind == JsonValueKind.Null, "Opaque null disappeared.");
            Equal(444L, committed.Timestamp);
            Check(finishCanceled, "Finish did not receive the original canceled work token.");
            Equal(1, events.OfType<AssistantMessageStarted>().Count());
            Equal(1, events.OfType<AssistantMessageEnded>().Count()); Equal(1, events.OfType<AgentLoopTurnEnded>().Count());
            Equal(1, events.OfType<AgentLoopEnded>().Count()); Equal(0, effects.Calls);
            endRelease.TrySetResult(); var result = await running; await idle;
            Equal(ChatFailureKind.Cancelled, result.Turns.Single().Result.Chat.Failure!.Kind);
            Equal(AgentLoopStopReason.ChatFailure, result.Reason); Equal(1, source.Cleanups);
            Equal(1, source.Requests.Count); Equal(1, agent.Snapshot.SteeringCount); Equal(1, agent.Snapshot.FollowUpCount);
            Check(agent.Snapshot.LatestStreamObservation is null && !agent.Snapshot.IsRunning, "Provisional state survived settlement.");
        }
        finally { source.CleanupRelease.TrySetResult(); endRelease.TrySetResult(); try { await running; } catch { } }
    }

    private static async Task FinishFaults()
    {
        foreach (var cancelThrow in new[] { false, true })
        {
            var source = new Source(Message(), partial: true);
            var events = new List<AgentEvent>();
            var hooks = new AgentHooks(FinishTurn: (_, token) =>
            {
                Check(token.IsCancellationRequested, "Cancellation finalization lost work token.");
                if (cancelThrow) throw new OperationCanceledException(token);
                throw new InvalidOperationException("private finish failure");
            });
            await using var agent = Create(source, new Sink((observation, token) =>
            { token.ThrowIfCancellationRequested(); events.Add(observation); return ValueTask.CompletedTask; }), hooks);
            var running = agent.PromptAsync(Input("finish failure"));
            await source.Entered.Task; agent.Abort();
            if (cancelThrow) await ThrowsAsync<OperationCanceledException>(() => running);
            else await ThrowsAsync<InvalidOperationException>(() => running);
            await agent.WaitForIdleAsync();
            Equal(1, source.Cleanups); Equal(2, agent.Snapshot.Messages.Length);
            Equal(StopReason.Aborted, PiWireJson.ReadMessage(agent.Snapshot.Messages[^1].WireBody.Value).StopReason);
            Equal(0, events.OfType<AgentLoopTurnEnded>().Count()); Equal(0, events.OfType<AgentLoopEnded>().Count());
            Equal(cancelThrow ? AgentFailureKind.Canceled : AgentFailureKind.RunFault, agent.Snapshot.Failure);
        }
    }

    private static async Task AuthoritativeBounds()
    {
        using var canceled = new CancellationTokenSource();
        var source = new Source(Message() with { Content = [new TextContent(new string('x', 512))] }, authoritative: true,
            beforeTerminal: () => canceled.Cancel());
        source.Release.TrySetResult();
        var client = new ChatClient(source, 1, new StreamLimits(MaximumBlocks: 1, MaximumCharacters: 200));
        await using var run = await client.StartWithAbortSettlementAsync(new(Model, []), canceled.Token);
        await foreach (var _ in run.ReadEventsAsync()) { }
        var result = await run.Completion;
        Equal(ChatFailureKind.ResourceLimit, result.Failure!.Kind); Equal(StopReason.Error, result.Message.StopReason);
        Check(result.Message.Content.IsEmpty, "Oversized authoritative payload bypassed bounds.");
        Equal(1, source.Cleanups);
    }

    private static async Task BoundedDrain()
    {
        var blocked = Gate(); var release = Gate(); var source = new Source(Message(), partial: true, gateCleanup: true);
        var events = new List<AgentEvent>();
        await using var agent = Create(source, new Sink(async (observation, token) =>
        {
            token.ThrowIfCancellationRequested(); events.Add(observation);
            if (observation is AssistantMessageStarted) { blocked.TrySetResult(); await release.Task; }
        }), options: new(StreamCapacity: 1));
        var running = agent.PromptAsync(Input("bounded"));
        try
        {
            await blocked.Task;
            agent.Abort(); await source.CleanupEntered.Task;
            Check(!running.IsCompleted && agent.Snapshot.IsRunning, "Full channel/canceled producer bypassed sink cleanup.");
            source.CleanupRelease.TrySetResult(); release.TrySetResult();
            var result = await running;
            Equal(AgentLoopStopReason.ChatFailure, result.Reason); Equal(StopReason.Aborted, result.Turns.Single().Result.Chat.Message.StopReason);
            Equal(1, source.Cleanups); Equal(1, events.OfType<AgentLoopEnded>().Count());
        }
        finally { source.CleanupRelease.TrySetResult(); release.TrySetResult(); try { await running; } catch { } }
    }

    private sealed class Source(AssistantMessage final, bool authoritative = false, bool partial = false,
        bool gateCleanup = false, Action? beforeTerminal = null) : IChatTransport
    {
        public readonly List<ChatRequest> Requests = [];
        public readonly TaskCompletionSource Entered = Gate(), Release = Gate(), CleanupEntered = Gate(), CleanupRelease = Gate();
        public CancellationToken WorkToken;
        public int Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests.Add(request); WorkToken = token;
            try
            {
                if (partial)
                {
                    var properties = JsonFields.Empty.Set("responseId", JsonData.Parse("\"resp-partial\""))
                        .Set("future", JsonData.Parse("""{"scale":1.0,"opaque":null}"""));
                    yield return new StreamStarted(final with { Timestamp = 444, Content = [], StopReason = StopReason.Pending, ExtraProperties = properties });
                    yield return new TextStarted(0, new("", JsonFields.Empty.Set("textSignature", JsonData.Parse("\"signature\""))));
                    yield return new TextDelta(0, "partial\n\uD83D\uDE00");
                    yield return new ToolCallStarted(1, new("call-partial", "read", JsonData.EmptyObject));
                    yield return new ToolCallDelta(1, """{"path":"/unfinished""");
                }
                Entered.TrySetResult();
                if (authoritative)
                {
                    await Release.Task;
                    beforeTerminal?.Invoke();
                    yield return new StreamError(StopReason.Aborted, final with { StopReason = StopReason.Aborted });
                }
                else await Gate().Task.WaitAsync(token);
            }
            finally
            {
                CleanupEntered.TrySetResult();
                if (gateCleanup) await CleanupRelease.Task;
                Cleanups++;
            }
        }
    }
    private sealed class CounterTool : IToolExecutor
    {
        public int Calls;
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        { Calls++; return ValueTask.FromResult(ToolResult.Success("effect")); }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> callback) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => callback(observation, token); }
    private static NativeAgent Create(Source source, IAgentEventSink? sink = null, AgentHooks? hooks = null,
        ImmutableArray<ToolDefinition> tools = default, AgentOptions? options = null) =>
        new(new(Model, source, tools.IsDefault ? [] : tools, Hooks: hooks), () => 123,
            sink ?? new Sink((_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }), options);
    private static AssistantMessage Message() => new(Model.Api, Model.Provider, Model.Id, 444, [], TokenUsage.Zero, StopReason.Aborted,
        JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"public abort\"")));
    private static TranscriptEntry Input(string text) => new("user", JsonData.FromElement(JsonSerializer.SerializeToElement(new { role = "user", content = text, timestamp = 123 })));
    private static TranscriptEntry Entry(JsonElement value) => new(value.GetProperty("role").GetString()!, JsonData.FromElement(value));
    private static object? Project(AgentEvent value) => value switch
    {
        AgentLoopStarted => new { type = "agent_start" },
        AgentLoopTurnStarted => new { type = "turn_start" },
        AgentLoopInputMessageStarted entry => new { type = "message_start", message = entry.Message.WireBody.Value },
        AgentLoopInputMessageEnded entry => new { type = "message_end", message = entry.Message.WireBody.Value },
        AssistantMessageStarted entry => new { type = "message_start", message = PiWireJson.WriteMessage(entry.Message).Value },
        AssistantMessageEnded entry => new { type = "message_end", message = PiWireJson.WriteMessage(entry.Message).Value },
        AgentLoopTurnEnded turn => new { type = "turn_end", message = PiWireJson.WriteMessage(turn.Turn.Result.Chat.Message).Value,
            toolResults = turn.Turn.ToolResults.Select(entry => entry.WireBody.Value) },
        AgentLoopEnded loop => new { type = "agent_end", messages = loop.Result.Transcript.Select(entry => entry.WireBody.Value) },
        TurnStreamObserved => null,
        _ => throw new InvalidOperationException("Unexpected abort lifecycle event.")
    };
    private static JsonDocument Fixture(string name, string hash)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "fixtures", "pi-v0.99.1", "finish-decisions")))
            directory = directory.Parent;
        Check(directory is not null, "Frozen abort fixture unavailable.");
        var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "fixtures", "pi-v0.99.1", "finish-decisions", name));
        Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return JsonDocument.Parse(bytes);
    }
    private static void Same(JsonElement expected, JsonElement actual) => Check(Equivalent(expected, actual), "Exact aborted lifecycle differs.");
    private static bool Equivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var a = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var b = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            return a.Count == b.Count && a.All(property => b.TryGetValue(property.Key, out var value) && Equivalent(property.Value, value));
        }
        if (left.ValueKind == JsonValueKind.Array) return left.GetArrayLength() == right.GetArrayLength() &&
            left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Equivalent(pair.First, pair.Second));
        return left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText();
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
