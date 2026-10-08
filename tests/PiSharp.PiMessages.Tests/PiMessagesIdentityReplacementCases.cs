using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

// Successor controls; frozen predecessor rejection fixtures and phase gates stay intact.
internal static class PiMessagesIdentityReplacementCases
{
    internal sealed record Outcome(string Id, string Status, string? Error);
    private static readonly ModelDescriptor Model = new("identity-model", "pi-messages", "authored-provider");
    private static readonly JsonData Metadata = JsonData.Parse("""{"id":"identity-model","api":"pi-messages","provider":"authored-provider","baseUrl":"https://pi-messages.invalid"}""");
    private static PiMessagesOptions Options() => new(Metadata, "authored-inert-noncredential") { EnvironmentLookup = _ => null };
    private static ChatRequest Request() => new(Model, [], 123);
    private static AssistantMessage Initial(string api = "pi-messages") => new(api, Model.Provider, Model.Id, 123, [], TokenUsage.Zero, StopReason.Pending);
    private static JsonData Dto(object value) => JsonData.Parse(JsonSerializer.Serialize(value));
    private static JsonData Start() => Dto(new { type = "start" });
    private static JsonData ToolStart(int index = 0, string id = "old-id", string name = "provisional-tool") => Dto(new { type = "toolcall_start", contentIndex = index, id, toolName = name });
    private static JsonData End(string id = "final-id", string name = "final-tool", object? arguments = null, int index = 0) =>
        Dto(new { type = "toolcall_end", contentIndex = index, toolCall = new { type = "toolCall", id, name, arguments = arguments ?? new { target = "/allowed/raw" }, annotation = "retained" } });
    private static JsonData Done() => Dto(new { type = "done", reason = "toolUse", usage = PiWireJson.WriteMessage(Initial()).Value.GetProperty("usage") });
    private static PiMessagesEventMapper Started(PiMessagesOptions? options = null)
    { var mapper = new PiMessagesEventMapper(Request(), options ?? Options()); mapper.Convert(Start()); mapper.Convert(ToolStart()); return mapper; }
    private static void Check(bool condition, string message = "Identity replacement control failed.")
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is PiMessagesException or StreamProtocolException or StreamLimitException or JsonException or ArgumentException) { return; }
        throw new InvalidOperationException("Expected bounded identity/content validation rejection.");
    }
    internal static async Task<IReadOnlyList<Outcome>> RunAsync()
    {
        var outcomes = new List<Outcome>();
        foreach (var (id, run) in new (string, Func<Task>)[]
        {
            ("pi-identity.final-replacement-projection-immutable-progress", Replacement),
            ("pi-identity.reducer-opt-in-and-default-identity", ReducerAdmission),
            ("pi-identity.malformed-duplicate-type-mismatch-and-limits", InvalidEnds),
            ("pi-identity.agent-final-prepared-action-policy", AgentPolicy),
            ("pi-identity.agent-partial-cancel-cleanup-no-authority", AgentCancellation),
            ("pi-identity.agent-malformed-end-no-authority", AgentMalformed),
            ("pi-identity.agent-outer-iterator-join-no-authority", AgentOuterJoin)
        })
        {
            try { await run(); outcomes.Add(new(id, "PASS_AUTHORED_NATIVE_ONLY", null)); }
            catch (Exception error) { outcomes.Add(new(id, "FAIL", error.ToString())); }
        }
        return outcomes;
    }
    private static Task Replacement()
    {
        foreach (var (id, name) in new[] { ("final-id", "provisional-tool"), ("old-id", "final-tool"), ("final-id", "final-tool"), ("old-id", "provisional-tool") })
        {
            var mapper = new PiMessagesEventMapper(Request(), Options());
            var frames = new List<StreamEvent> { mapper.Convert(Start()), mapper.Convert(ToolStart()) };
            var before = mapper.Current.Value.ToString(); var sourceBefore = frames[1].SourceEmissionSnapshot!.ToString();
            frames.Add(mapper.Convert(Dto(new { type = "toolcall_delta", contentIndex = 0, delta = "{\"ignoredPreview\":true}" })));
            var ended = (ToolCallEnded)mapper.Convert(End(id, name)); frames.Add(ended);
            var done = (StreamDone)mapper.Convert(Done()); frames.Add(done);
            Check(ended.ToolCall.Id == id && ended.ToolCall.Name == name && ended.ToolCall.Arguments.Value.GetProperty("target").GetString() == "/allowed/raw");
            Check(!ended.ToolCall.Arguments.Value.TryGetProperty("ignoredPreview", out _));
            Check(JsonElement.DeepEquals(PiWireJson.WriteContent(done.Message.Content.OfType<ToolCallContent>().Single()).Value, PiWireJson.WriteContent(ended.ToolCall).Value));
            Check(((ToolCallStarted)frames[1]).ToolCall.Id == "old-id" && ((ToolCallStarted)frames[1]).ToolCall.Name == "provisional-tool");
            Check(frames[1].SourceEmissionSnapshot!.ToString() == sourceBefore && before.Contains("old-id", StringComparison.Ordinal));
            var source = ended.SourceEmissionSnapshot!.Value.GetProperty("value");
            Check(source.GetProperty("toolCall").GetProperty("id").GetString() == id && source.GetProperty("partial").GetProperty("content")[0].GetProperty("name").GetString() == name);
            Check(frames.Select(frame => frame.GetType()).SequenceEqual(new[] { typeof(StreamStarted), typeof(ToolCallStarted), typeof(ToolCallDelta), typeof(ToolCallEnded), typeof(StreamDone) }));
            // Existing native codec carries the full final call without an extra wire flag.
            var roundTrip = (ToolCallEnded)PiWireJson.ReadEvent(PiWireJson.WriteEvent(ended).Value);
            Check(roundTrip.ToolCall.Id == id && roundTrip.ToolCall.Name == name && JsonElement.DeepEquals(PiWireJson.WriteContent(roundTrip.ToolCall).Value, PiWireJson.WriteContent(ended.ToolCall).Value));
            var replay = new AssistantStreamReducer(Initial(), null, allowPiMessagesIdentityReplacement: true);
            foreach (var frame in frames) replay.Apply(frame is ToolCallEnded ? roundTrip : frame);
            Check(JsonElement.DeepEquals(PiWireJson.WriteMessage(replay.Snapshot()).Value, PiWireJson.WriteMessage(done.Message).Value));
        }
        return Task.CompletedTask;
    }
    private static Task ReducerAdmission()
    {
        var final = new ToolCallContent("final-id", "final-tool", JsonData.EmptyObject);
        foreach (var api in new[] { "pi-messages", "authored-other-api" })
        {
            var reducer = new AssistantStreamReducer(Initial(api)); reducer.Apply(new StreamStarted(Initial(api)));
            reducer.Apply(new ToolCallStarted(0, new("old-id", "provisional-tool", JsonData.EmptyObject)));
            Reject(() => reducer.Apply(new ToolCallEnded(0, final)));
        }
        Reject(() => new AssistantStreamReducer(Initial("authored-other-api"), null, allowPiMessagesIdentityReplacement: true));
        var wrongStart = new AssistantStreamReducer(Initial(), null, allowPiMessagesIdentityReplacement: true);
        wrongStart.Apply(new StreamStarted(Initial("authored-other-api"))); wrongStart.Apply(new ToolCallStarted(0, new("old-id", "provisional-tool", JsonData.EmptyObject)));
        Reject(() => wrongStart.Apply(new ToolCallEnded(0, final)));
        var accepted = new AssistantStreamReducer(Initial(), null, allowPiMessagesIdentityReplacement: true);
        accepted.Apply(new StreamStarted(Initial())); accepted.Apply(new ToolCallStarted(0, new("old-id", "provisional-tool", JsonData.EmptyObject)));
        accepted.Apply(new ToolCallEnded(0, final));
        Reject(() => accepted.Apply(new ToolCallEnded(0, final)));
        Reject(() => accepted.Apply(new StreamDone(StopReason.ToolUse, Initial() with { StopReason = StopReason.ToolUse, Content = [new ToolCallContent("old-id", "provisional-tool", JsonData.EmptyObject)] })));
        return Task.CompletedTask;
    }
    private static Task InvalidEnds()
    {
        foreach (var dto in new[]
        {
            End("", "final-tool"), End("final-id", " "), End(arguments: new[] { 1 }), End(index: 1),
            Dto(new { type = "toolcall_end", contentIndex = 0, toolCall = new { type = "text", id = "final-id", name = "final-tool", arguments = new { } } }),
            Dto(new { type = "toolcall_end", contentIndex = 0, toolCall = new { id = 123, name = "final-tool", arguments = new { } } }),
            Dto(new { type = "toolcall_end", contentIndex = 0, toolCall = new { id = "final-id", name = "final-tool" } }),
            Dto(new { type = "toolcall_end", contentIndex = 0, toolCall = "wrong-type" })
        })
        { var mapper = Started(); var before = mapper.Current.Value.ToString(); Reject(() => mapper.Convert(dto)); Check(mapper.Current.Value.ToString() == before); }
        var duplicate = Started(); duplicate.Convert(ToolStart(1, "other-id"));
        Reject(() => duplicate.Convert(End("other-id")));
        var duplicateFinal = Started(); duplicateFinal.Convert(End()); duplicateFinal.Convert(ToolStart(1, "other-id"));
        Reject(() => duplicateFinal.Convert(End("final-id", index: 1)));
        var text = new PiMessagesEventMapper(Request(), Options()); text.Convert(Start()); text.Convert(Dto(new { type = "text_start", contentIndex = 0 }));
        Reject(() => text.Convert(End()));
        var repeated = Started(); repeated.Convert(End()); Reject(() => repeated.Convert(End()));
        var limited = Started(Options() with { MaximumContentCharacters = 512 }); var limitedBefore = limited.Current.Value.ToString();
        Reject(() => limited.Convert(End(new string('x', 1024)))); Check(limited.Current.Value.ToString() == limitedBefore);
        var payloadLimited = Started(Options() with { MaximumPayloadBytes = 2048 }); Reject(() => payloadLimited.Convert(End(arguments: new { target = new string('x', 4096) })));
        var slots = Started(Options() with { MaximumContentSlots = 1 }); Reject(() => slots.Convert(ToolStart(1, "other-id")));
        return Task.CompletedTask;
    }
    private static async Task AgentPolicy()
    {
        var adapter = new Adapter(); var policy = new Policy(true); var invoker = new ToolInvoker([adapter], policy);
        var provisional = new ToolCallContent("old-id", "final-tool", Dto(new { target = "/allowed/raw" }));
        foreach (var reason in new[] { StopReason.Pending, StopReason.Error, StopReason.Aborted })
        {
            var result = await invoker.ExecuteAsync(new(Initial() with { Content = [provisional], StopReason = reason }, provisional, 0), default);
            Check(result.IsError && adapter.Prepares.Count == 0 && adapter.Executions.Count == 0 && policy.Actions.Count == 0);
        }
        foreach (var mode in new[] { "allow", "deny-policy", "deny-schema", "deny-exposure" }) await AgentSchedule(mode);
    }
    private static async Task AgentCancellation()
    { foreach (var mode in new[] { "cancel-preview", "cancel-after-end", "cancel-cleanup", "fail-cleanup" }) await AgentSchedule(mode); }
    private static async Task AgentMalformed()
    { foreach (var mode in new[] { "malformed", "duplicate-json", "mismatched-type", "malformed-arguments", "limit-end" }) await AgentSchedule(mode); }
    private static async Task AgentOuterJoin()
    {
        foreach (var (cancel, primaryError) in new[] { (false, false), (true, false), (false, true) })
        {
            var body = new HeldBody("", false, !cancel); var adapter = new Adapter(); var policy = new Policy(true);
            var invoker = new ToolInvoker([adapter], policy); StreamTerminalEvent? observed = null;
            await using var agent = new NativeAgent(new(Model, new JoinedTransport(body, primaryError), [new("final-tool", invoker)],
                Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))), () => 123,
                new Sink((value, _) => { if (value is TurnStreamObserved { Event: StreamTerminalEvent terminal }) observed = terminal; return ValueTask.CompletedTask; }), new(StreamCapacity: 1));
            Task<AgentLoopResult>? pending = null;
            try
            {
                pending = agent.PromptAsync([new("user", JsonData.Parse("""{"role":"user","content":"authored","timestamp":123}"""))]);
                await body.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(observed is null && !pending.IsCompleted && adapter.Prepares.Count == 0);
                if (cancel) agent.Abort(); body.ReleaseCleanup.TrySetResult();
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); var chat = result.Turns.Single().Result.Chat;
                Check(observed is StreamError && (primaryError || chat.Message.Content.IsEmpty) && chat.Failure is not null && policy.Actions.Count == 0 && adapter.Executions.Count == 0 && adapter.Prepares.Count == 0);
                Check(chat.Message.StopReason == (cancel ? StopReason.Aborted : StopReason.Error));
                Check(observed!.NativeDiagnostic?.Code == (cancel ? NativeChatFailureCode.Cancelled : primaryError ? NativeChatFailureCode.ProviderError : NativeChatFailureCode.CleanupFailed));
                if (!cancel) Check(observed!.NativeCleanupDiagnostic?.Code == NativeChatFailureCode.CleanupFailed);
            }
            finally
            {
                agent.Abort(); body.ReleaseCleanup.TrySetResult();
                if (pending is not null) { try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch when (pending.IsCompleted) { } }
            }
        }
    }
    private static string Wire(IEnumerable<JsonData> dtos) => string.Concat(dtos.Select(dto => "data: " + dto + "\n\n"));
    private static async Task AgentSchedule(string mode)
    {
        var values = new List<JsonData> { Start(), ToolStart(), Dto(new { type = "toolcall_delta", contentIndex = 0, delta = "{\"preview\":true}" }) };
        if (mode != "cancel-preview") values.Add(mode switch
        {
            "malformed" => End("", "final-tool"),
            "deny-schema" => End(arguments: new { target = 123 }),
            "malformed-arguments" => End(arguments: new[] { 1 }),
            "limit-end" => End(new string('x', 1024)),
            "mismatched-type" => Dto(new { type = "toolcall_end", contentIndex = 0, toolCall = new { type = "text", id = "final-id", name = "final-tool", arguments = new { } } }),
            _ => End()
        });
        if (mode is not ("cancel-preview" or "cancel-after-end")) values.Add(Done());
        var wire = mode == "duplicate-json"
            ? Wire(values.Take(3)) + "data: {\"type\":\"toolcall_end\",\"contentIndex\":0,\"toolCall\":{\"id\":\"final-id\",\"name\":\"final-tool\",\"arguments\":{\"target\":1,\"target\":2}}}\n\n" + Wire([Done()])
            : Wire(values);
        var body = new HeldBody(wire, mode is "cancel-preview" or "cancel-after-end", mode == "fail-cleanup");
        using var handler = new Handler(); using var client = new HttpClient(handler);
        var adapter = new Adapter(); var policy = new Policy(mode != "deny-policy");
        var invoker = new ToolInvoker([adapter], policy,
            [(_, action, _) => { adapter.Order.Add("transform"); return ValueTask.FromResult(action with { Target = "/allowed/transformed" }); }],
            options: new ToolInvokerOptions { AllowedRootTools = mode == "deny-exposure" ? ImmutableHashSet<string>.Empty : null });
        var events = new List<StreamEvent>(); var terminal = (StreamTerminalEvent?)null;
        var options = Options() with { BodyReaderFactory = (_, _) => ValueTask.FromResult<Stream?>(body), MaximumContentCharacters = mode == "limit-end" ? 512 : 1_048_576 };
        await using var agent = new NativeAgent(new(Model, new PiMessagesHttpSseTransport(client, Model, options), [new("final-tool", invoker)],
            Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))), () => 123,
            new Sink((observation, _) =>
            {
                if (observation is TurnStreamObserved stream) { events.Add(stream.Event); if (stream.Event is StreamTerminalEvent end) terminal = end; }
                return ValueTask.CompletedTask;
            }), new(StreamCapacity: 1));
        Task<AgentLoopResult>? pending = null;
        try
        {
            pending = agent.PromptAsync([new("user", JsonData.Parse("""{"role":"user","content":"authored","timestamp":123}"""))]);
            if (mode is "cancel-preview" or "cancel-after-end") { await body.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5)); agent.Abort(); }
            await body.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!pending.IsCompleted && adapter.Prepares.Count == 0 && adapter.Executions.Count == 0 && policy.Actions.Count == 0 && terminal is null,
                "Progress or unjoined cleanup authorized a provisional/final tool.");
            if (mode == "cancel-cleanup") agent.Abort();
            body.ReleaseCleanup.TrySetResult();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(handler.Sends == 1 && body.CleanupSettled && result.Turns.Length == 1);
            var chat = result.Turns.Single().Result.Chat;
            if (mode.StartsWith("cancel", StringComparison.Ordinal) || mode is "fail-cleanup" or "malformed" or "duplicate-json" or "mismatched-type" or "malformed-arguments" or "limit-end")
            {
                Check(terminal is StreamError && chat.Failure is not null && chat.Message.Content.IsEmpty && adapter.Prepares.Count == 0 && policy.Actions.Count == 0 && adapter.Executions.Count == 0);
                Check(chat.Message.StopReason == (mode.StartsWith("cancel", StringComparison.Ordinal) ? StopReason.Aborted : StopReason.Error));
                if (mode == "limit-end") Check(terminal!.NativeDiagnostic?.Code == NativeChatFailureCode.ResourceLimit);
            }
            else
            {
                Check(terminal is StreamDone && chat.Failure is null);
                var call = chat.Message.Content.OfType<ToolCallContent>().Single(); Check(call.Id == "final-id" && call.Name == "final-tool");
                Check(events.OfType<ToolCallStarted>().Single().ToolCall.Name == "provisional-tool");
                Check(JsonElement.DeepEquals(PiWireJson.WriteContent(events.OfType<ToolCallEnded>().Single().ToolCall).Value, PiWireJson.WriteContent(call).Value));
                if (mode == "deny-exposure") Check(adapter.Prepares.Count == 0 && policy.Actions.Count == 0 && adapter.Executions.Count == 0);
                else
                {
                    Check(adapter.Prepares.Single().Call.Id == "final-id" && adapter.Prepares.Single().AssistantMessage.StopReason == StopReason.ToolUse);
                    if (mode != "deny-schema") Check(adapter.Prepares.Single().Call.Arguments.Value.GetProperty("target").GetString() == "/allowed/raw" && !adapter.Prepares.Single().Call.Arguments.Value.TryGetProperty("preview", out _));
                    if (mode == "deny-schema") Check(policy.Actions.Count == 0 && adapter.Executions.Count == 0);
                    else
                    {
                        Check(policy.Actions.Single().Target == "/allowed/transformed" && policy.Invocations.Single().Call.Id == "final-id");
                        Check(adapter.Order.Take(4).SequenceEqual(new[] { "prepare", "validate", "transform", "validate" }));
                        Check(adapter.Executions.Count == (mode == "allow" ? 1 : 0));
                        if (mode == "allow") { Check(ReferenceEquals(policy.Actions.Single(), adapter.Executions.Single())); Check(result.Transcript.Any(entry => entry.Role == "toolResult" && entry.WireBody.Value.GetProperty("toolCallId").GetString() == "final-id")); }
                    }
                }
            }
        }
        finally
        {
            agent.Abort(); body.ReleaseCleanup.TrySetResult();
            if (pending is not null) { try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch when (pending.IsCompleted) { } }
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Sends;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Sends++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }); }
    }
    private sealed class JoinedTransport(HeldBody body, bool primaryError) : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            var mapper = new PiMessagesEventMapper(request, Options());
            var terminal = primaryError ? Dto(new { type = "error", reason = "error", errorMessage = "authored provider error", usage = PiWireJson.WriteMessage(Initial()).Value.GetProperty("usage") }) : Done();
            try { foreach (var dto in new[] { Start(), ToolStart(), End(), terminal }) { token.ThrowIfCancellationRequested(); yield return mapper.Convert(dto); } }
            finally { await body.DisposeAsync(); }
        }
    }
    private sealed class HeldBody(string wire, bool holdAtEof, bool failCleanup) : MemoryStream(Encoding.UTF8.GetBytes(wire))
    {
        internal readonly TaskCompletionSource ReadBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously),
            CleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CleanupSettled;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position == Length && holdAtEof) { ReadBlocked.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
        }
        public override async ValueTask DisposeAsync()
        {
            CleanupEntered.TrySetResult(); await ReleaseCleanup.Task; await base.DisposeAsync(); CleanupSettled = true;
            if (failCleanup) throw new IOException("authored cleanup fault");
        }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private sealed class Adapter : IPreparedToolAdapter
    {
        public string Name => "final-tool";
        internal readonly List<ToolInvocation> Prepares = [];
        internal readonly List<PreparedToolAction> Executions = [];
        internal readonly List<string> Order = [];
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Prepares.Add(invocation); Order.Add("prepare");
            var target = invocation.Call.Arguments.Value.GetProperty("target");
            return ValueTask.FromResult(new PreparedToolAction(Name, "inspect", PreparedToolActionKind.Path,
                target.ValueKind == JsonValueKind.String ? target.GetString()! : "/invalid", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Order.Add("validate"); return ValueTask.FromResult(action.Arguments.Value.GetProperty("target").ValueKind == JsonValueKind.String && action.Target.StartsWith("/allowed/", StringComparison.Ordinal)); }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Order.Add("execute"); Executions.Add(action); return ValueTask.FromResult(ToolResult.Success("authored")); }
    }
    private sealed class Policy(bool allow) : IToolActionPolicy
    {
        internal readonly List<PreparedToolAction> Actions = [];
        internal readonly List<ToolInvocation> Invocations = [];
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Invocations.Add(invocation); Actions.Add(action); return ValueTask.FromResult(new ToolActionAuthorization(allow)); }
    }
}
