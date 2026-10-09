using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class ResponsesTurnTests
{
    private const string ToolId = "call-turn|fc-turn";
    // The authoritative output_item.done arguments, as the wire carries them.
    private const string FinalArguments = """{"value":7,"note":"authoritative","exact":9007199254740993,"scale":1.0}""";
    // pi-ai finalizes them with parseStreamingJson (packages/ai/src/api/openai-responses-shared.ts:716,
    // packages/ai/src/utils/json-parse.ts:104-110): JSON.parse rounds 9007199254740993 to the binary64 9007199254740992 and
    // 1.0 is the number 1, so the finalized call carries the JavaScript value.
    private const string FinalizedArguments = """{"value":7,"note":"authoritative","exact":9007199254740992,"scale":1}""";
    private const string Start = """{"type":"response.output_item.added","output_index":9,"item":{"type":"function_call","id":"fc-turn","call_id":"call-turn","name":"inspect","arguments":""}}""";
    private const string Delta = """{"type":"response.function_call_arguments.delta","output_index":9,"item_id":"fc-turn","delta":"{\"value\":999"}""";
    private const string ArgumentsDone = """{"type":"response.function_call_arguments.done","output_index":9,"item_id":"fc-turn","arguments":"{\"value\":888}"}""";
    private const string Completed = """{"type":"response.completed","response":{"id":"response-turn","status":"completed","output":[]}}""";

    public static async Task AuthoritativeArgumentsAfterBarriers()
    {
        var source = new DtoSource([Start, Delta, ArgumentsDone, End(FinalArguments), Completed]);
        var sink = new Sink(source, blockAssistant: true);
        var effects = new Effects(source, sink);
        var request = Request();
        var run = Runner(source, effects).RunAsync(request, sink);
        try
        {
            await source.CleanupEntered.Task;
            Check(!run.IsCompleted && !sink.AssistantEntered.Task.IsCompleted,
                "The turn committed or settled while DTO cleanup was blocked.");
            NoEffects(effects);
            Check(sink.Events.OfType<TurnStreamObserved>().All(value => value.Event is not StreamDone),
                "Successful terminal escaped before DTO cleanup.");

            source.ReleaseCleanup.TrySetResult();
            await sink.AssistantEntered.Task;
            Check(sink.CleanupBeforeAssistant, "Assistant commit preceded DTO cleanup.");
            Check(!run.IsCompleted, "The assistant sink was not awaited.");
            NoEffects(effects);

            sink.ReleaseAssistant.TrySetResult();
            var result = await run;
            Check(result.Chat.Failure is null && result.CleanupFailure is null, "Successful composition gained a failure.");
            Equal(StopReason.ToolUse, result.Chat.Message.StopReason);
            var final = (ToolCallContent)result.Chat.Message.Content.Single();
            CheckFinalCall(final);
            var invocation = effects.Invocations.Single();
            CheckFinalCall(invocation.Call);
            Check(ReferenceEquals(result.Chat.Message, invocation.AssistantMessage),
                "The executor did not receive the finalized assistant owned by the turn.");
            Equal(0, invocation.SourceIndex);
            Equal(1, effects.BeforeCount);
            Equal(1, effects.ExecuteCount);
            Equal(1, effects.AfterCount);
            Check(effects.PreflightAfterBarriers && effects.ExecutionAfterBarriers,
                "Preflight or execution preceded cleanup/assistant barrier settlement.");
            Equal(ToolId, result.Tools.Messages.Single().ToolCallId);
            Check(result.Tools.Terminate && !result.Tools.ShouldContinue,
                "The finalized terminating result did not suppress the continuation hint.");
            Equal(1, sink.Events.OfType<AssistantMessageEnded>().Count());
            Equal(1, sink.Events.OfType<TurnStreamObserved>().Count(value => value.Event is StreamDone));
            CheckFinalCall(sink.Events.OfType<TurnStreamObserved>().Select(value => value.Event)
                .OfType<ToolCallEnded>().Single().ToolCall);
            Equal(1, source.RequestCount);
            Check(ReferenceEquals(request, source.LastRequest), "DTO acquisition changed the caller's request.");
            Equal(1, source.CleanupCount);
        }
        finally
        {
            source.ReleaseCleanup.TrySetResult();
            sink.ReleaseAssistant.TrySetResult();
            try { await run; } catch { /* Preserve the assertion failure after releasing owned gates. */ }
        }
    }

    // Truncated authoritative item-end arguments are not a stream failure: openai-responses-shared.ts:716
    // parseStreamingJson("{\"value\":") is {} (packages/ai/src/utils/json-parse.ts:112-114, partial-json), so the completed
    // turn is toolUse and the call runs with {} (pi-ai 1.1.0 processResponsesStream gives exactly this content).
    public static Task TruncatedAuthoritativeArgumentsFinalizeEmpty() => SucceedsAfterCleanup(
        [Start, Delta, ArgumentsDone, End("{\"value\":"), Completed], "{}", expectedReads: 5, toolDeltas: ["{\"value\":999"]);

    // Slot events with no open slot of their type do nothing: openai-responses-shared.ts:605-682 getSlot(...) then
    // "if (!slot) continue;", and output_item.done deletes the slot (:727). A reasoning delta without an output_index
    // and late deltas for the ended tool slot are ignored, as are deltas of another slot type for the open tool slot, a
    // delta matched by output_index whatever its item_id, and an item type createSlot does not open (:464-530); the tool end
    // stays authoritative.
    public static Task UnmatchedDtosAfterToolEndAreIgnored() => SucceedsAfterCleanup(
        [Start, Delta,
            """{"type":"response.output_text.delta","output_index":9,"item_id":"fc-turn","delta":"wrong slot type"}""",
            """{"type":"response.custom_tool_call_input.delta","output_index":9,"item_id":"fc-turn","delta":"wrong slot type"}""",
            """{"type":"response.function_call_arguments.delta","output_index":9,"item_id":"another-item","delta":",\"other\":1"}""",
            """{"type":"response.output_item.added","output_index":4,"item":{"type":"web_search_call","id":"ws-turn","status":"in_progress"}}""",
            """{"type":"response.output_item.done","output_index":4,"item":{"type":"web_search_call","id":"ws-turn","status":"completed"}}""",
            ArgumentsDone, End(FinalArguments),
            """{"type":"response.reasoning_text.delta","delta":"unmatched"}""",
            """{"type":"response.function_call_arguments.delta","output_index":9,"item_id":"fc-turn","delta":"late"}""",
            """{"type":"response.output_text.delta","output_index":9,"item_id":"fc-turn","delta":"late"}""", Completed],
        FinalizedArguments, expectedReads: 13, toolDeltas: ["{\"value\":999", ",\"other\":1"]);

    // An error event after a valid tool end fails the turn: openai-responses-shared.ts:745-746 throws and
    // openai-responses.ts:214-221 keeps the content with stopReason "error", so no call runs.
    public static Task ErrorAfterToolEndPreventsEffects() => FailurePreventsEffects(
        [Start, Delta, ArgumentsDone, End(FinalArguments), """{"type":"error","code":"server_error","message":"failed"}"""],
        expectedReads: 5, hasFinalToolEnd: true);

    private static async Task SucceedsAfterCleanup(string[] script, string arguments, int expectedReads, string[] toolDeltas)
    {
        var source = new DtoSource(script);
        var sink = new Sink(source, blockAssistant: false);
        var effects = new Effects(source, sink);
        var run = Runner(source, effects).RunAsync(Request(), sink);
        try
        {
            await source.CleanupEntered.Task;
            Check(!run.IsCompleted && !sink.AssistantEntered.Task.IsCompleted,
                "The turn settled or committed before owned DTO cleanup.");
            NoEffects(effects);
            source.ReleaseCleanup.TrySetResult();
            var result = await run;
            Check(result.Chat.Failure is null && result.CleanupFailure is null, "The completed turn gained a failure.");
            Equal(StopReason.ToolUse, result.Chat.Message.StopReason);
            Equal(expectedReads, source.ReadCount);
            Equal(1, source.CleanupCount);
            var call = (ToolCallContent)result.Chat.Message.Content.Single();
            Equal(ToolId, call.Id);
            Equal(arguments, call.Arguments.Value.GetRawText());
            Equal(arguments, effects.Invocations.Single().Call.Arguments.Value.GetRawText());
            Equal(1, effects.BeforeCount);
            Equal(1, effects.ExecuteCount);
            Equal(1, effects.AfterCount);
            Check(effects.PreflightAfterBarriers && effects.ExecutionAfterBarriers, "Tool effects preceded cleanup/assistant settlement.");
            Equal(1, sink.Events.OfType<TurnStreamObserved>().Select(value => value.Event).OfType<ToolCallEnded>().Count());
            Check(toolDeltas.SequenceEqual(sink.Events.OfType<TurnStreamObserved>().Select(value => value.Event).OfType<ToolCallDelta>().Select(value => value.Delta)),
                "Tool argument deltas differ from pi-ai's toolcall_delta events.");
            Equal(1, sink.Events.OfType<TurnStreamObserved>().Count(value => value.Event is StreamDone));
            Equal(0, sink.Events.OfType<TurnStreamObserved>().Count(value => value.Event is StreamError));
        }
        finally
        {
            source.ReleaseCleanup.TrySetResult();
            try { await run; } catch { /* Preserve the assertion failure after releasing owned cleanup. */ }
        }
    }

    private static async Task FailurePreventsEffects(string[] script, int expectedReads, bool hasFinalToolEnd)
    {
        var source = new DtoSource(script);
        var sink = new Sink(source, blockAssistant: false);
        var effects = new Effects(source, sink);
        var run = Runner(source, effects).RunAsync(Request(), sink);
        try
        {
            await source.CleanupEntered.Task;
            Check(!run.IsCompleted && !sink.AssistantEntered.Task.IsCompleted,
                "Failed turn skipped owned DTO cleanup before settlement/assistant commit.");
            NoEffects(effects);
            source.ReleaseCleanup.TrySetResult();
            var result = await run;
            Equal(ChatFailureKind.Provider, result.Chat.Failure!.Kind);
            Equal(StopReason.Error, result.Chat.Message.StopReason);
            Check(result.CleanupFailure is null && sink.CleanupBeforeAssistant,
                "Protocol failure did not settle cleanly before assistant commit.");
            Equal(1, source.CleanupCount);
            Equal(1, source.RequestCount);
            Equal(expectedReads, source.ReadCount);
            NoEffects(effects);
            Equal(0, result.Tools.Outcomes.Length);
            Equal(0, result.Tools.Messages.Length);
            Check(!result.Tools.ShouldContinue, "A failed Responses turn requested continuation.");
            Equal(1, sink.Events.OfType<AssistantMessageEnded>().Count());
            Equal(1, sink.Events.OfType<TurnStreamObserved>().Count(value => value.Event is StreamError));
            Equal(0, sink.Events.OfType<TurnStreamObserved>().Count(value => value.Event is StreamDone));
            Check(!sink.Events.Any(value => value is ToolExecutionStarted or ToolExecutionEnded
                or ToolResultMessageStarted or ToolResultMessageEnded), "Failure admitted a tool lifecycle event.");
            var partial = (ToolCallContent)result.Chat.Message.Content.Single();
            Equal(ToolId, partial.Id);
            var toolEnds = sink.Events.OfType<TurnStreamObserved>().Select(value => value.Event)
                .OfType<ToolCallEnded>().ToArray();
            Equal(hasFinalToolEnd ? 1 : 0, toolEnds.Length);
            if (hasFinalToolEnd)
            {
                CheckFinalCall(partial);
                CheckFinalCall(toolEnds.Single().ToolCall);
            }
        }
        finally
        {
            source.ReleaseCleanup.TrySetResult();
            try { await run; } catch { /* Preserve the assertion failure after releasing owned cleanup. */ }
        }
    }

    private static TurnRunner Runner(DtoSource source, Effects effects) => new(
        new ChatClient(new ResponsesTextToolTransport(source.Open), capacity: 1),
        new ToolBatchScheduler([new("inspect", effects)], effects));

    private static ChatRequest Request() => new(new("synthetic-turn-model", "openai-responses", "fixture-provider"), [], 123);

    private static string End(string arguments) => JsonSerializer.Serialize(new
    {
        type = "response.output_item.done", output_index = 9,
        item = new { type = "function_call", id = "fc-turn", call_id = "call-turn", name = "inspect", arguments }
    });

    private static void CheckFinalCall(ToolCallContent call)
    {
        Equal(ToolId, call.Id);
        Equal("inspect", call.Name);
        Equal(FinalizedArguments, call.Arguments.Value.GetRawText());
        Equal(7, call.Arguments.Value.GetProperty("value").GetInt32());
        Equal("9007199254740992", call.Arguments.Value.GetProperty("exact").GetRawText());
        Equal("1", call.Arguments.Value.GetProperty("scale").GetRawText());
    }

    private static void NoEffects(Effects effects)
    {
        Equal(0, effects.BeforeCount);
        Equal(0, effects.ExecuteCount);
        Equal(0, effects.AfterCount);
    }

    private sealed class DtoSource(string[] script)
    {
        private readonly ImmutableArray<JsonData> dtos = script.Select(JsonData.Parse).ToImmutableArray();
        public readonly TaskCompletionSource CleanupEntered = Gate();
        public readonly TaskCompletionSource ReleaseCleanup = Gate();
        public readonly TaskCompletionSource CleanupFinished = Gate();
        public int RequestCount;
        public int ReadCount;
        public int CleanupCount;
        public ChatRequest? LastRequest;

        public IAsyncEnumerable<JsonData> Open(ChatRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref RequestCount);
            LastRequest = request;
            return Read(token);
        }

        private async IAsyncEnumerable<JsonData> Read([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                foreach (var dto in dtos)
                {
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref ReadCount);
                    yield return dto;
                }
            }
            finally
            {
                CleanupEntered.TrySetResult();
                await ReleaseCleanup.Task.ConfigureAwait(false);
                Interlocked.Increment(ref CleanupCount);
                CleanupFinished.TrySetResult();
            }
        }
    }

    private sealed class Sink(DtoSource source, bool blockAssistant) : IAgentEventSink
    {
        public readonly ConcurrentQueue<AgentEvent> Events = new();
        public readonly TaskCompletionSource AssistantEntered = Gate();
        public readonly TaskCompletionSource ReleaseAssistant = Gate();
        public readonly TaskCompletionSource AssistantFinished = Gate();
        public bool CleanupBeforeAssistant;

        public async ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Enqueue(observation);
            if (observation is not AssistantMessageEnded) return;
            CleanupBeforeAssistant = source.CleanupFinished.Task.IsCompleted;
            AssistantEntered.TrySetResult();
            if (blockAssistant) await ReleaseAssistant.Task.ConfigureAwait(false);
            AssistantFinished.TrySetResult();
        }
    }

    private sealed class Effects(DtoSource source, Sink sink) : IToolExecutor, IToolHooks
    {
        public readonly ConcurrentQueue<ToolInvocation> Invocations = new();
        public int BeforeCount;
        public int ExecuteCount;
        public int AfterCount;
        public bool PreflightAfterBarriers;
        public bool ExecutionAfterBarriers;

        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref BeforeCount);
            PreflightAfterBarriers = source.CleanupFinished.Task.IsCompleted && sink.AssistantFinished.Task.IsCompleted;
            return ValueTask.FromResult(ToolPreflightDecision.Allow);
        }

        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref ExecuteCount);
            Invocations.Enqueue(invocation);
            ExecutionAfterBarriers = source.CleanupFinished.Task.IsCompleted && sink.AssistantFinished.Task.IsCompleted;
            return ValueTask.FromResult(ToolResult.Success("inspected", terminate: true));
        }

        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref AfterCount);
            return ValueTask.FromResult(result);
        }
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
