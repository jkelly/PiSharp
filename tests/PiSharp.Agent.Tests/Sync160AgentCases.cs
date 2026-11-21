// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/agent/src/agent-loop.ts and packages/agent/test/agent-loop.test.ts.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.AI;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

// Authored expectations derived by reading the cited Pi 1.6.0 source and its two added agent-loop cases (#9986); nothing here runs
// upstream code or is an upstream capture. agent-loop.ts no longer leaves a batch when its signal aborts: executeToolCallsSequential
// and executeToolCallsParallel walk every call, and prepareToolCall answers "Operation aborted" first, before it looks the tool up or
// calls beforeToolCall. Every call of the assistant message therefore has a tool result, a start and an end.
internal static class Sync160AgentCases
{
    private static readonly ModelDescriptor Model = new("sync160-model", "openai-responses", "fixture-provider");
    private static readonly string[] Ids = ["tool-1", "tool-2", "tool-3"];

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("sync160.sequential-batch-aborted-mid-batch-answers-every-call", SequentialBatch),
        ("sync160.parallel-batch-aborted-during-preparation-answers-every-call (NativeAwaited)", () => ParallelBatch(ToolProgressDeliveryMode.NativeAwaited)),
        ("sync160.parallel-batch-aborted-during-preparation-answers-every-call (SourceCompatible)", () => ParallelBatch(ToolProgressDeliveryMode.SourceCompatible)),
        ("sync160.aborted-signal-answers-before-tool-lookup-and-before-hook", AbortedBeforeLookupAndHook),
        ("sync160.agent-sequential-abort-mid-batch-leaves-no-call-without-a-result", AgentSequentialAbort),
        ("sync160.agent-parallel-abort-during-preparation-leaves-no-call-without-a-result", AgentParallelAbort),
    ];

    // agent-loop.test.ts "answers every tool call of a sequential batch aborted mid-batch".
    private static async Task SequentialBatch()
    {
        using var controller = new CancellationTokenSource();
        var executed = new List<string>();
        var tool = new Executor((invocation, _) =>
        {
            executed.Add(Value(invocation));
            // The user aborts while the first call runs.
            controller.Cancel();
            throw new InvalidOperationException("interrupted");
        });
        var sink = new Sink();
        var batch = await new ToolBatchScheduler([new("slow", tool, ToolExecutionMode.Sequential)])
            .RunAsync(Batch(), sink, controller.Token);

        Sequence(["first"], executed);
        Sequence(["tool-1=interrupted", "tool-2=Operation aborted", "tool-3=Operation aborted"], ResultTexts(batch.Messages));
        Sequence(Ids, sink.Starts); Sequence(Ids, sink.Ends); Sequence(Ids, sink.Transcript);
        Check(batch.Messages.All(message => message.IsError), "An interrupted or skipped call was not an error result.");
        Equal(ToolFailureKind.ExecutionError, batch.Outcomes[0].Result.Failure!.Kind);
        Check(batch.Outcomes.Skip(1).All(outcome => outcome.Result.Failure?.Kind == ToolFailureKind.Canceled), "Skipped calls are not canceled outcomes.");
        Check(batch.IsCanceled && !batch.ShouldContinue, "An aborted batch asked for another request.");
    }

    // agent-loop.test.ts "answers every tool call of a parallel batch aborted while calls are prepared".
    private static async Task ParallelBatch(ToolProgressDeliveryMode mode)
    {
        using var controller = new CancellationTokenSource();
        var executed = new List<string>(); var hookCalls = new List<string>();
        var tool = new Executor((invocation, _) =>
        {
            lock (executed) executed.Add(Value(invocation));
            return ValueTask.FromResult(ToolResult.Success(Value(invocation)));
        });
        // The user aborts while a hook decides about the second call, for example at a permission prompt.
        var hooks = new Hooks((invocation, _) =>
        {
            hookCalls.Add(invocation.Call.Id);
            if (invocation.Call.Id == "tool-2") controller.Cancel();
            return ValueTask.FromResult(ToolPreflightDecision.Allow);
        });
        var sink = new Sink();
        var batch = await new ToolBatchScheduler([new("slow", tool)], hooks, progressOptions: new(mode))
            .RunAsync(Batch(), sink, controller.Token);

        Sequence([], executed);
        // The hook is not asked about a call that will not run.
        Sequence(["tool-1", "tool-2"], hookCalls);
        Sequence(Ids.Select(id => id + "=Operation aborted"), ResultTexts(batch.Messages));
        Sequence(Ids, sink.Starts); Sequence(Ids, sink.Ends.Order(StringComparer.Ordinal)); Sequence(Ids, sink.Transcript);
        Equal(3, batch.Outcomes.Length);
        Check(batch.Outcomes.All(outcome => outcome.Result.Failure?.Kind == ToolFailureKind.Canceled && outcome.IsError), "A call lacks its canceled outcome.");
        Check(batch.IsCanceled && !batch.ShouldContinue, "An aborted batch asked for another request.");
    }

    // agent-loop.ts prepareToolCall: `if (signal?.aborted)` is the first statement, so an aborted signal answers an unknown tool and a
    // hooked tool alike, in either execution mode, and the batch is entered whatever the signal (executeToolCalls has no abort check).
    private static async Task AbortedBeforeLookupAndHook()
    {
        foreach (var mode in new[] { ToolExecutionMode.Parallel, ToolExecutionMode.Sequential })
            foreach (var progress in new[] { ToolProgressDeliveryMode.NativeAwaited, ToolProgressDeliveryMode.SourceCompatible })
            {
                using var controller = new CancellationTokenSource(); controller.Cancel();
                var executed = 0; var hookCalls = 0;
                var tool = new Executor((_, _) => { executed++; return ValueTask.FromResult(ToolResult.Success("unexpected")); });
                var hooks = new Hooks((_, _) => { hookCalls++; return ValueTask.FromResult(ToolPreflightDecision.Allow); });
                var message = Assistant([new ToolCallContent("tool-1", "missing", JsonData.EmptyObject), new ToolCallContent("tool-2", "slow", JsonData.EmptyObject)]);
                var sink = new Sink();
                var batch = await new ToolBatchScheduler([new("slow", tool)], hooks, mode, new(progress)).RunAsync(message, sink, controller.Token);
                Equal(0, executed); Equal(0, hookCalls);
                Sequence(["tool-1=Operation aborted", "tool-2=Operation aborted"], ResultTexts(batch.Messages));
                Sequence(["missing", "slow"], batch.Messages.Select(result => result.ToolName));
                Sequence(["tool-1", "tool-2"], sink.Starts); Sequence(["tool-1", "tool-2"], sink.Ends);
                Check(batch.Outcomes.All(outcome => outcome.Result.Failure?.Kind == ToolFailureKind.Canceled), "An aborted signal reached the tool lookup.");
                Check(batch.IsCanceled && !batch.ShouldContinue, "An aborted batch asked for another request.");
            }
        // A truncated message still wins: agent-loop.ts fails its calls in failToolCallsFromTruncatedMessage, without prepareToolCall.
        using var aborted = new CancellationTokenSource(); aborted.Cancel();
        var truncated = await new ToolBatchScheduler([new("slow", new Executor((_, _) => throw new InvalidOperationException("ran")))])
            .RunAsync(Batch() with { StopReason = StopReason.Length }, new Sink(), aborted.Token);
        Equal(3, truncated.Outcomes.Length);
        Check(truncated.Outcomes.All(outcome => outcome.Result.Failure?.Kind == ToolFailureKind.Truncated), "Abort replaced the truncated-message result.");
    }

    // The same two scenarios through the whole agent (Agent.Abort while a batch runs): the committed history holds a result for each
    // call of the assistant message, so the next request never carries a tool call without its result.
    private static async Task AgentSequentialAbort()
    {
        var transport = new Transport(Batch()); var executed = new List<string>(); var events = new Sink();
        NativeAgent? running = null;
        var tool = new Executor((invocation, _) =>
        {
            executed.Add(Value(invocation));
            Check(running!.Abort(), "The running agent could not be aborted from its tool.");
            throw new InvalidOperationException("interrupted");
        });
        await using var agent = running = new(new(Model, transport, [new("slow", tool, ToolExecutionMode.Sequential)]), () => 123, events);
        var result = await agent.PromptAsync(Input("run three"));

        Sequence(["first"], executed);
        Equal(AgentLoopStopReason.ChatFailure, result.Reason); Equal(AgentFailureKind.Canceled, agent.Snapshot.Failure);
        Equal(1, transport.Requests);
        Sequence(["user", "assistant", "toolResult", "toolResult", "toolResult"], agent.Snapshot.Messages.Select(entry => entry.Role));
        Sequence(["tool-1=interrupted", "tool-2=Operation aborted", "tool-3=Operation aborted"], ResultTexts(agent.Snapshot.Messages));
        Sequence(Ids, events.Starts); Sequence(Ids, events.Ends);
        NoCallWithoutResult(agent.Snapshot.Messages);
    }

    private static async Task AgentParallelAbort()
    {
        var transport = new Transport(Batch()); var executed = new List<string>(); var hookCalls = new List<string>(); var events = new Sink();
        NativeAgent? running = null;
        var tool = new Executor((invocation, _) => { lock (executed) executed.Add(Value(invocation)); return ValueTask.FromResult(ToolResult.Success("ran")); });
        var hooks = new Hooks((invocation, _) =>
        {
            hookCalls.Add(invocation.Call.Id);
            if (invocation.Call.Id == "tool-2") Check(running!.Abort(), "The running agent could not be aborted from its hook.");
            return ValueTask.FromResult(ToolPreflightDecision.Allow);
        });
        await using var agent = running = new(new(Model, transport, [new("slow", tool)], hooks), () => 123, events);
        var result = await agent.PromptAsync(Input("run three"));

        Sequence([], executed); Sequence(["tool-1", "tool-2"], hookCalls);
        Equal(AgentLoopStopReason.ChatFailure, result.Reason); Equal(AgentFailureKind.Canceled, agent.Snapshot.Failure);
        Equal(1, transport.Requests);
        Sequence(["user", "assistant", "toolResult", "toolResult", "toolResult"], agent.Snapshot.Messages.Select(entry => entry.Role));
        Sequence(Ids.Select(id => id + "=Operation aborted"), ResultTexts(agent.Snapshot.Messages));
        Sequence(Ids, events.Starts); Sequence(Ids, events.Ends.Order(StringComparer.Ordinal));
        NoCallWithoutResult(agent.Snapshot.Messages);
    }

    /// <summary>One assistant message that calls <c>slow</c> three times (agent-loop.test.ts runToolBatch).</summary>
    private static AssistantMessage Batch() => Assistant(new[] { "first", "second", "third" }.Select((value, index) =>
        (AssistantContent)new ToolCallContent(Ids[index], "slow", JsonData.FromElement(JsonSerializer.SerializeToElement(new { value }))))
        .ToImmutableArray());
    private static AssistantMessage Assistant(ImmutableArray<AssistantContent> content) =>
        new(Model.Api, Model.Provider, Model.Id, 0, content, TokenUsage.Zero, StopReason.ToolUse);
    private static string Value(ToolInvocation invocation) => invocation.Call.Arguments.Value.GetProperty("value").GetString()!;
    private static TranscriptEntry Input(string text) =>
        new("user", JsonData.FromElement(JsonSerializer.SerializeToElement(new { role = "user", content = text, timestamp = 123 })));
    private static IEnumerable<string> ResultTexts(IEnumerable<ToolResultMessage> messages) =>
        messages.Select(message => message.ToolCallId + "=" + string.Concat(message.Content.Select(block => block.Text)));
    private static IEnumerable<string> ResultTexts(IEnumerable<TranscriptEntry> messages) =>
        messages.Where(entry => entry.Role == "toolResult").Select(entry => entry.WireBody.Value).Select(result =>
        {
            Check(result.GetProperty("isError").GetBoolean(), "A committed result of the aborted batch is not an error.");
            return result.GetProperty("toolCallId").GetString() + "=" + string.Concat(result.GetProperty("content").EnumerateArray()
                .Select(block => block.GetProperty("type").GetString() == "text" ? block.GetProperty("text").GetString() : ""));
        });
    private static void NoCallWithoutResult(ImmutableArray<TranscriptEntry> messages)
    {
        var calls = messages.Where(entry => entry.Role == "assistant").SelectMany(entry => entry.WireBody.Value.GetProperty("content").EnumerateArray())
            .Where(block => block.GetProperty("type").GetString() == "toolCall").Select(block => block.GetProperty("id").GetString()!).ToArray();
        var results = messages.Where(entry => entry.Role == "toolResult").Select(entry => entry.WireBody.Value.GetProperty("toolCallId").GetString()!).ToArray();
        Sequence(Ids, calls); Sequence(calls, results);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedArray = expected.ToArray(); var actualArray = actual.ToArray();
        Check(expectedArray.SequenceEqual(actualArray), $"Expected [{string.Join(",", expectedArray)}]; actual [{string.Join(",", actualArray)}].");
    }

    private sealed class Executor(Func<ToolInvocation, CancellationToken, ValueTask<ToolResult>> execute) : IToolExecutor
    { public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) => execute(invocation, cancellationToken); }

    private sealed class Hooks(Func<ToolInvocation, CancellationToken, ValueTask<ToolPreflightDecision>> before) : IToolHooks
    {
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken cancellationToken) => before(invocation, cancellationToken);
        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    /// <summary>Records tool_execution_start, tool_execution_end and tool result message_end ids in delivery order.</summary>
    private sealed class Sink : IAgentEventSink
    {
        private readonly object gate = new();
        private readonly List<string> starts = [], ends = [], transcript = [];
        public string[] Starts { get { lock (gate) return [.. starts]; } }
        public string[] Ends { get { lock (gate) return [.. ends]; } }
        public string[] Transcript { get { lock (gate) return [.. transcript]; } }
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (observation is ToolExecutionStarted started) starts.Add(started.Invocation.Call.Id);
                else if (observation is ToolExecutionEnded ended) ends.Add(ended.Outcome.Invocation.Call.Id);
                else if (observation is ToolResultMessageEnded message) transcript.Add(message.Message.ToolCallId);
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Answers its one request with the given finished assistant message.</summary>
    private sealed class Transport(AssistantMessage final) : IChatTransport
    {
        public int Requests;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests++;
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            for (var index = 0; index < final.Content.Length; index++)
            {
                var call = (ToolCallContent)final.Content[index];
                yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject });
                yield return new ToolCallEnded(index, call);
            }
            yield return new StreamDone(final.StopReason, final);
            await Task.CompletedTask;
        }
    }
}
