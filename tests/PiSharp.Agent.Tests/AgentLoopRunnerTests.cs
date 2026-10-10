using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class AgentLoopRunnerTests
{
    private const long Clock = 1_700_000_000_000;
    private static readonly ModelDescriptor Model = new("oracle-model", "openai-responses", "synthetic-oracle");

    public static async Task FrozenContinuationProjection()
    {
        Check(!Same(JsonData.Parse("{\"a\":1}").Value, JsonData.Parse("{\"a\":1.0}").Value), "Comparator erased number tokens.");
        Check(!Same(JsonData.Parse("{\"a\":9007199254740993}").Value, JsonData.Parse("{\"a\":9007199254740992}").Value), "Comparator rounded wide numbers.");
        Check(!Same(JsonData.Parse("[1,2]").Value, JsonData.Parse("[2,1]").Value), "Comparator erased array order.");
        using var input = ReadFixture("core.input.json", "073669bd54ccc32b8b3a69e8a3099cc293f198b3a55c01602344cffcb2c6f34e");
        using var golden = ReadFixture("core.expected.json", "f714291fd9fb555e4932d4e8daefbf2842ed24380a33d3039efe7f6c8dd1bc2d");
        var scenarios = input.RootElement.GetProperty("scenarios");
        var observations = golden.RootElement.GetProperty("observations").GetProperty("scenarios");
        Equal(2, scenarios.GetArrayLength()); Equal(2, observations.GetArrayLength());
        for (var index = 0; index < scenarios.GetArrayLength(); index++)
        {
            var scenario = scenarios[index]; var expected = observations[index];
            Equal(scenario.GetProperty("scenarioId").GetString(), expected.GetProperty("scenarioId").GetString());
            var transcript = PreparedPrompts(input.RootElement, scenario);
            var transport = new ScriptTransport(scenario.GetProperty("providerTurns").EnumerateArray().Select(PiWireJson.ReadMessage).ToArray());
            var order = new List<string>();
            var queues = new List<JsonData>();
            var steering = ImmutableArray<TranscriptEntry>.Empty;
            var followUp = ImmutableArray<TranscriptEntry>.Empty;
            var snapshots = new List<AgentLoopSnapshot>();
            var tool = new Tool(ReadToolResult(scenario.GetProperty("toolResult")), order, gated: true);
            var barrierEntered = Gate(); var releaseBarrier = Gate();
            var sink = new Sink(async (observation, _) =>
            {
                if (EventKind(observation) is { } eventKind) order.Add("event." + eventKind);
                if (observation is AssistantMessageEnded && transport.Requests.Count == 1)
                { barrierEntered.TrySetResult(); await releaseBarrier.Task; }
            });
            var callbacks = new AgentLoopCallbacks((snapshot, _) =>
            {
                order.Add("hook.prepare_request"); snapshots.Add(snapshot);
                order.Add("request"); return ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript));
            }, (turn, _) =>
            { order.Add("hook.prepare_next_turn"); return ValueTask.FromResult(ImmutableArray<TranscriptEntry>.Empty); },
            _ => Poll("steering_poll", ref steering), _ => Poll("follow_up_poll", ref followUp),
            (turn, _) => { order.Add("hook.finish_turn"); return ValueTask.CompletedTask; });
            var runner = Runner(transport, tool);
            var running = runner.RunAsync(transcript, callbacks, sink);
            try
            {
                await barrierEntered.Task;
                Equal(1, transport.Requests.Count); Equal(0, tool.BeforeCount); Equal(0, tool.Executions);
                Equal(1, transport.Cleanups);
                steering = Entries(scenario.GetProperty("steeringMessages"));
                followUp = Entries(scenario.GetProperty("followUpMessages"));
                releaseBarrier.TrySetResult();
                await tool.Started.Task;
                Equal(1, transport.Requests.Count);
                Equal(1, queues.Count);
                tool.Release.TrySetResult();
                var result = await running;
                Equal(AgentLoopStopReason.Completed, result.Reason);
                Equal(expected.GetProperty("requests").GetArrayLength(), transport.Requests.Count);
                Equal(transport.Requests.Count, transport.Cleanups);
                Equal(1, tool.Executions); Equal(1, tool.BeforeCount); Equal(1, tool.AfterCount);
                var expectedRequests = expected.GetProperty("requests");
                for (var requestIndex = 0; requestIndex < transport.Requests.Count; requestIndex++)
                {
                    var actualRequest = transport.Requests[requestIndex];
                    var oracleRequest = expectedRequests[requestIndex];
                    Equal(oracleRequest.GetProperty("model").GetProperty("id").GetString(), actualRequest.Model.Id);
                    Equal(oracleRequest.GetProperty("model").GetProperty("api").GetString(), actualRequest.Model.Api);
                    Equal(oracleRequest.GetProperty("model").GetProperty("provider").GetString(), actualRequest.Model.Provider);
                    SameMessages(oracleRequest.GetProperty("context").GetProperty("messages"), actualRequest.Messages);
                }
                SameMessages(expected.GetProperty("finalResult"), result.Transcript);
                SameMessages(expected.GetProperty("finalContextMessages"), result.Transcript);
                Equal(transcript.Length, snapshots[0].Transcript.Length);
                SameMessages(expectedRequests[0].GetProperty("context").GetProperty("messages"), snapshots[0].Transcript);
                var expectedQueues = expected.GetProperty("queueTrace").EnumerateArray()
                    .Where(value => value.GetProperty("kind").GetString()!.EndsWith("_poll", StringComparison.Ordinal)).ToArray();
                Equal(expectedQueues.Length, queues.Count);
                for (var queueIndex = 0; queueIndex < queues.Count; queueIndex++)
                    Check(Same(expectedQueues[queueIndex], queues[queueIndex].Value), "Callback delivery changed.");
                Sequence(ProjectOrder(expected), order);
                Equal(transport.Requests.Count, sink.Events.OfType<AgentLoopTurnEnded>().Count());
                Equal(1, sink.Events.OfType<AgentLoopEnded>().Count());
                var call = (ToolCallContent)transport.Messages[0].Content.Single();
                Equal(call.Id, tool.LastInvocation!.Call.Id);
                Equal(call.Arguments.Value.GetRawText(), tool.LastInvocation.Call.Arguments.Value.GetRawText());
            }
            finally
            {
                releaseBarrier.TrySetResult(); tool.Release.TrySetResult();
                try { await running; } catch { /* Release gates before preserving assertion failures. */ }
            }

            ValueTask<ImmutableArray<TranscriptEntry>> Poll(string kind, ref ImmutableArray<TranscriptEntry> queue)
            {
                var messages = queue; queue = [];
                order.Add("queue." + kind);
                queues.Add(JsonData.Parse(JsonSerializer.Serialize(new { kind, messages = messages.Select(value => value.WireBody.Value) })));
                return ValueTask.FromResult(messages);
            }
        }
        await FrozenFinishDecisionProjection();
        await ContextAcrossCompletedGenerations();
    }

    private static async Task FrozenFinishDecisionProjection()
    {
        using var input = ReadFixture("core.input.json", "815203f5a198cc2ddf7cb1c7d6856adf46ef051127861391af9e711f7c7500ba", "finish-decisions");
        using var golden = ReadFixture("core.expected.json", "9f63f961256a9af071c90220adbca8861e6c813fcfdfcaa4ab9fffaa37169e23", "finish-decisions");
        var scenarios = input.RootElement.GetProperty("scenarios");
        var observations = golden.RootElement.GetProperty("observations").GetProperty("scenarios");
        Equal(7, scenarios.GetArrayLength()); Equal(7, observations.GetArrayLength());
        for (var index = 0; index < scenarios.GetArrayLength(); index++)
        {
            var scenario = scenarios[index]; var expected = observations[index];
            Equal(scenario.GetProperty("scenarioId").GetString(), expected.GetProperty("scenarioId").GetString());
            var transport = new ScriptTransport(scenario.GetProperty("providerTurns").EnumerateArray().Select(PiWireJson.ReadMessage).ToArray());
            var hasTools = transport.Messages.Any(message => message.Content.Any(content => content is ToolCallContent));
            var prompts = hasTools ? PreparedPrompts(input.RootElement, scenario) : Entries(scenario.GetProperty("prompts"));
            var order = new List<string>(); var queues = new List<JsonData>(); var finished = new List<AgentLoopTurn>();
            var steering = ImmutableArray<TranscriptEntry>.Empty; var followUp = ImmutableArray<TranscriptEntry>.Empty;
            var tool = new Tool(ReadToolResult(input.RootElement.GetProperty("toolResult")), order);
            var entered = Gate(); var release = Gate();
            var sink = new Sink((observation, _) =>
            { if (EventKind(observation) is { } kind) order.Add("event." + kind); return ValueTask.CompletedTask; });
            var callbacks = new AgentLoopCallbacks((snapshot, _) =>
            { order.Add("hook.prepare_request"); order.Add("request"); return ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript)); },
                (_, _) => { order.Add("hook.prepare_next_turn"); return ValueTask.FromResult(ImmutableArray<TranscriptEntry>.Empty); },
                _ => Poll("steering_poll", ref steering), _ => Poll("follow_up_poll", ref followUp),
                FinishTurnDecision: async (turn, token) =>
                {
                    order.Add("hook.finish_enter"); finished.Add(turn);
                    if (turn.TurnIndex == 0) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
                    order.Add("hook.finish_return");
                    return scenario.GetProperty("finishDecisions")[turn.TurnIndex].GetString() switch
                    {
                        "end" => AgentLoopFinishAction.End, "continue" => AgentLoopFinishAction.Continue,
                        "undefined" => AgentLoopFinishAction.Default, _ => throw new InvalidOperationException("Unexpected frozen decision.")
                    };
                });
            // Aborted source control is intentionally unmatched: this profile compares a provider-returned
            // aborted terminal with an uncanceled caller token. Actual caller cancellation is tested separately.
            var run = Runner(transport, tool).RunAsync(prompts, callbacks, sink);
            try
            {
                await entered.Task;
                var probe = expected.GetProperty("controls").EnumerateArray().Single(value => value.GetProperty("kind").GetString() == "finish_gate_probe");
                Equal(probe.GetProperty("requestCount").GetInt32(), transport.Requests.Count);
                Equal(probe.GetProperty("finishCount").GetInt32(), finished.Count);
                Equal(probe.GetProperty("turnEndCount").GetInt32(), sink.Events.OfType<AgentLoopTurnEnded>().Count());
                Equal(probe.GetProperty("agentEndCount").GetInt32(), sink.Events.OfType<AgentLoopEnded>().Count());
                Equal(probe.GetProperty("toolExecutionCount").GetInt32(), tool.Executions);
                Equal(probe.GetProperty("committedToolResultCount").GetInt32(), finished[0].ToolResults.Length);
                Equal(1, transport.Cleanups); Equal(1, queues.Count);
                steering = Entries(scenario.GetProperty("steeringMessages")); followUp = Entries(scenario.GetProperty("followUpMessages"));
                release.TrySetResult(); var result = await run;
                var failed = transport.Messages[0].StopReason is StopReason.Error or StopReason.Aborted;
                Equal(failed ? AgentLoopStopReason.ChatFailure : AgentLoopStopReason.Completed, result.Reason);
                var requests = expected.GetProperty("requests");
                Equal(requests.GetArrayLength(), transport.Requests.Count); Equal(transport.Requests.Count, transport.Cleanups);
                for (var requestIndex = 0; requestIndex < transport.Requests.Count; requestIndex++)
                {
                    var model = requests[requestIndex].GetProperty("model"); var actual = transport.Requests[requestIndex];
                    Equal(model.GetProperty("id").GetString(), actual.Model.Id); Equal(model.GetProperty("api").GetString(), actual.Model.Api);
                    Equal(model.GetProperty("provider").GetString(), actual.Model.Provider);
                    SameMessages(requests[requestIndex].GetProperty("context").GetProperty("messages"), actual.Messages);
                }
                SameMessages(expected.GetProperty("finalResult"), result.Transcript);
                SameMessages(expected.GetProperty("finalContextMessages"), result.Transcript);
                SameMessages(expected.GetProperty("remainingSteering"), steering); SameMessages(expected.GetProperty("remainingFollowUp"), followUp);
                var expectedQueues = expected.GetProperty("queues").EnumerateArray()
                    .Where(value => value.GetProperty("kind").GetString()!.EndsWith("_poll", StringComparison.Ordinal)).ToArray();
                Equal(expectedQueues.Length, queues.Count);
                for (var queueIndex = 0; queueIndex < queues.Count; queueIndex++) Check(Same(expectedQueues[queueIndex], queues[queueIndex].Value), "Finish-decision queue delivery differs.");
                var snapshots = expected.GetProperty("hooks").EnumerateArray().Where(value => value.GetProperty("kind").GetString() == "finish_enter").ToArray();
                Equal(snapshots.Length, finished.Count);
                for (var turnIndex = 0; turnIndex < finished.Count; turnIndex++)
                {
                    var snapshot = snapshots[turnIndex]; var turn = finished[turnIndex]; Equal(snapshot.GetProperty("turnIndex").GetInt32(), turn.TurnIndex);
                    Check(Same(snapshot.GetProperty("message"), PiWireJson.WriteMessage(turn.Result.Chat.Message).Value, rootMessage: true), "Finish assistant differs.");
                    SameMessages(snapshot.GetProperty("toolResults"), turn.ToolResults);
                    SameMessages(snapshot.GetProperty("contextMessages"), turn.Transcript); SameMessages(snapshot.GetProperty("newMessages"), turn.Transcript);
                }
                Sequence(ProjectOrder(expected, "hooks", "queues", "tools"), order.Where(value => value != "tool.execute_finish")
                    .Select(value => value == "tool.execute_start" ? "tool.execute" : value));
                var checks = expected.GetProperty("checks");
                Equal(checks.GetProperty("finishTurnCount").GetInt32(), finished.Count);
                Equal(checks.GetProperty("toolExecutionCount").GetInt32(), tool.Executions);
                Equal(checks.GetProperty("turnStartCount").GetInt32(), sink.Events.OfType<AgentLoopTurnStarted>().Count());
                Equal(checks.GetProperty("turnEndCount").GetInt32(), sink.Events.OfType<AgentLoopTurnEnded>().Count());
                Equal(checks.GetProperty("terminalAgentEndCount").GetInt32(), sink.Events.OfType<AgentLoopEnded>().Count());
                Equal(prompts.Length, transport.Requests[0].Messages.Length);
                if (scenario.TryGetProperty("abortOnProviderTerminal", out var abort) && abort.GetBoolean())
                    Check(checks.GetProperty("finalSignalAborted").GetBoolean(), "Source aborted-signal control must remain visible.");
            }
            finally { release.TrySetResult(); try { await run; } catch { } }

            ValueTask<ImmutableArray<TranscriptEntry>> Poll(string kind, ref ImmutableArray<TranscriptEntry> queue)
            {
                var messages = queue; queue = []; order.Add("queue." + kind);
                queues.Add(JsonData.Parse(JsonSerializer.Serialize(new { kind, messages = messages.Select(value => value.WireBody.Value) })));
                return ValueTask.FromResult(messages);
            }
        }
    }

    public static async Task AwaitedCallbacksBlockContinuation()
    {
        var transport = new ScriptTransport([Assistant(tool: true), Assistant(tool: false)]);
        var tool = new Tool(ToolResult.Success("lookup"));
        var finishEntered = Gate(); var releaseFinish = Gate(); var prepareEntered = Gate(); var releasePrepare = Gate();
        var steering = ImmutableArray<TranscriptEntry>.Empty;
        AgentLoopTurn? firstTurn = null;
        var snapshots = new List<AgentLoopSnapshot>();
        var callbacks = new AgentLoopCallbacks((snapshot, _) =>
        { snapshots.Add(snapshot); return ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript)); },
        async (turn, token) =>
        { prepareEntered.TrySetResult(); await releasePrepare.Task.WaitAsync(token); return []; },
        _ => { var messages = steering; steering = []; return ValueTask.FromResult(messages); },
        FinishTurn: async (turn, token) =>
        {
            if (turn.TurnIndex == 0) { firstTurn = turn; finishEntered.TrySetResult(); await releaseFinish.Task.WaitAsync(token); }
        });
        var run = Runner(transport, tool).RunAsync(Prompts(), callbacks, new Sink());
        try
        {
            await finishEntered.Task;
            Equal(1, transport.Requests.Count); Equal(1, transport.Cleanups);
            Check(firstTurn!.Transcript.Last().Role == "toolResult", "Finish callback lost committed tool results.");
            releaseFinish.TrySetResult(); await prepareEntered.Task;
            Equal(1, transport.Requests.Count);
            steering = [User("queued during preparation")];
            releasePrepare.TrySetResult();
            var result = await run;
            Equal(AgentLoopStopReason.Completed, result.Reason);
            Equal(2, transport.Requests.Count);
            Check(transport.Requests[1].Messages.Any(value => value.WireBody.Value.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String && content.GetString() == "queued during preparation"),
                "Empty earlier steering poll was not repeated after preparation.");
            Equal(2, snapshots[0].Transcript.Length);
            Equal(4, firstTurn.Transcript.Length);
        }
        finally
        {
            releaseFinish.TrySetResult(); releasePrepare.TrySetResult();
            try { await run; } catch { }
        }
        await FinishDecisionBarrier();
    }

    public static async Task CallbackFailureAndCancellation()
    {
        var before = new ScriptTransport([Assistant(tool: false)]);
        await ThrowsAsync<InvalidOperationException>(() => Runner(before, new Tool(ToolResult.Success("unused")))
            .RunAsync(Prompts(), new((_, _) => throw new InvalidOperationException("prepare failed")), new Sink()));
        Equal(0, before.Requests.Count);

        var sinkTransport = new ScriptTransport([Assistant(tool: true), Assistant(tool: false)]);
        var sinkTool = new Tool(ToolResult.Success("unused"));
        await ThrowsAsync<InvalidOperationException>(() => Runner(sinkTransport, sinkTool).RunAsync(Prompts(), new(Request),
            new Sink((observation, _) => observation is TurnStreamObserved ? throw new InvalidOperationException("sink failed") : ValueTask.CompletedTask)));
        Equal(1, sinkTransport.Requests.Count); Equal(1, sinkTransport.Cleanups); Equal(0, sinkTool.Executions);

        var finished = new ScriptTransport([Assistant(tool: true), Assistant(tool: false)]);
        AgentLoopTurn? captured = null;
        var finishFailure = new AgentLoopCallbacks(Request,
            FinishTurn: (turn, _) => { captured = turn; throw new InvalidOperationException("finish failed"); });
        await ThrowsAsync<InvalidOperationException>(() => Runner(finished, new Tool(ToolResult.Success("retained")))
            .RunAsync(Prompts(), finishFailure, new Sink()));
        Equal(1, finished.Requests.Count); Equal(1, finished.Cleanups);
        Check(captured!.Transcript.Last().Role == "toolResult" && captured.Result.Tools.Messages.Length == 1,
            "Callback failure lost the completed batch snapshot.");

        var transport = new ScriptTransport([Assistant(tool: true), Assistant(tool: false)]);
        var entered = Gate(); var release = Gate(); using var cancellation = new CancellationTokenSource();
        var callbacks = new AgentLoopCallbacks(Request, async (_, token) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(token); return []; });
        var running = Runner(transport, new Tool(ToolResult.Success("retained"))).RunAsync(Prompts(), callbacks, new Sink(), cancellation.Token);
        await entered.Task; Equal(1, transport.Cleanups); cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => running);
        Equal(1, transport.Requests.Count);
        await FinishDecisionFaults();
        await FailedTurnsIgnoreDecisions();
    }

    public static async Task LimitsPreserveResultsAndPending()
    {
        var oversizedTransport = new ScriptTransport([Assistant(tool: false)]);
        var oversizedTool = new Tool(ToolResult.Success("unused"));
        var oversizedSink = new Sink(); var preparations = 0;
        var oversizedCallbacks = new AgentLoopCallbacks((snapshot, token) =>
        { preparations++; return Request(snapshot, token); });
        await ThrowsAsync<ArgumentException>(() => Runner(oversizedTransport, oversizedTool, new(MaximumTranscriptMessages: 1))
            .RunAsync(Prompts(), oversizedCallbacks, oversizedSink));
        Equal(0, oversizedSink.Events.Count); Equal(0, preparations);
        Equal(0, oversizedTransport.Requests.Count); Equal(0, oversizedTransport.Cleanups);
        Equal(0, oversizedTool.BeforeCount); Equal(0, oversizedTool.Executions);

        var transport = new ScriptTransport([Assistant(tool: true), Assistant(tool: false)]);
        var polls = 0;
        var callbacks = new AgentLoopCallbacks(Request, GetSteeringMessages: _ =>
            ValueTask.FromResult(++polls == 2 ? ImmutableArray.Create(User("pending")) : ImmutableArray<TranscriptEntry>.Empty));
        var result = await Runner(transport, new Tool(ToolResult.Success("retained")), new(MaximumTurns: 1, MaximumTranscriptMessages: 4))
            .RunAsync(Prompts(), callbacks, new Sink());
        Equal(AgentLoopStopReason.TurnLimit, result.Reason);
        Equal(1, transport.Requests.Count); Equal(1, transport.Cleanups);
        Equal(4, result.Transcript.Length); Equal("toolResult", result.Transcript.Last().Role);
        Equal(1, result.PendingMessages.Length); Equal(1, result.Turns.Single().Result.Tools.Messages.Length);

        var rejectedTransport = new ScriptTransport([Assistant(tool: true)]);
        var rejectedTool = new Tool(ToolResult.Success("unused"));
        var rejected = await Runner(rejectedTransport, rejectedTool, new(MaximumTranscriptMessages: 3))
            .RunAsync(Prompts(), new(Request), new Sink());
        Equal(AgentLoopStopReason.TranscriptLimit, rejected.Reason);
        Equal(2, rejected.Transcript.Length); Equal(0, rejectedTool.Executions); Equal(0, rejectedTool.BeforeCount);
        Check(rejected.RejectedAssistant is not null, "Admission limit discarded the rejected final assistant observation.");
        Equal(1, rejectedTransport.Cleanups);

        var terminatedTransport = new ScriptTransport([Assistant(tool: true), Assistant(tool: false)]);
        var terminated = await Runner(terminatedTransport, new Tool(ToolResult.Success("stop", terminate: true)))
            .RunAsync(Prompts(), new(Request), new Sink());
        Equal(AgentLoopStopReason.Completed, terminated.Reason); Equal(1, terminatedTransport.Requests.Count);

        var explicitTransport = new ScriptTransport([Assistant(tool: false), Assistant(tool: false)]);
        var explicitDecisions = 0; var explicitPreparations = 0;
        var explicitCallbacks = new AgentLoopCallbacks(Request,
            PrepareNextTurn: (_, _) => { explicitPreparations++; return ValueTask.FromResult(ImmutableArray<TranscriptEntry>.Empty); },
            FinishTurnDecision: (_, _) => { explicitDecisions++; return ValueTask.FromResult(AgentLoopFinishAction.Continue); });
        var limited = await Runner(explicitTransport, new Tool(ToolResult.Success("unused")), new(MaximumTurns: 1))
            .RunAsync(Prompts(), explicitCallbacks, new Sink());
        Equal(AgentLoopStopReason.TurnLimit, limited.Reason); Equal(1, explicitTransport.Requests.Count);
        Equal(1, explicitTransport.Cleanups); Equal(1, explicitDecisions); Equal(0, explicitPreparations);
        Equal(3, limited.Transcript.Length); Equal(1, limited.Turns.Length);

        var endTransport = new ScriptTransport([Assistant(tool: true)]);
        var ended = await Runner(endTransport, new Tool(ToolResult.Success("retained")), new(MaximumTurns: 1))
            .RunAsync(Prompts(), new(Request, FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End)), new Sink());
        Equal(AgentLoopStopReason.Completed, ended.Reason); Equal(4, ended.Transcript.Length);
        Equal(1, ended.Turns.Single().Result.Tools.Messages.Length); Equal(1, endTransport.Cleanups);
        await ContextLimitsBeforeEffects();
    }

    private static async Task FinishDecisionBarrier()
    {
        var transport = new ScriptTransport([Assistant(tool: true), Assistant(tool: false)]);
        var tool = new Tool(ToolResult.Success("retained"));
        var observerEntered = Gate(); var releaseObserver = Gate(); var decisionEntered = Gate(); var releaseDecision = Gate();
        var observerCalls = 0; var decisionCalls = 0; var steeringPolls = 0; var followUpPolls = 0;
        var steering = ImmutableArray<TranscriptEntry>.Empty; var followUp = ImmutableArray<TranscriptEntry>.Empty;
        AgentLoopTurn? snapshot = null;
        var sink = new Sink();
        var callbacks = new AgentLoopCallbacks(Request,
            GetSteeringMessages: _ => { steeringPolls++; var messages = steering; steering = []; return ValueTask.FromResult(messages); },
            GetFollowUpMessages: _ => { followUpPolls++; var messages = followUp; followUp = []; return ValueTask.FromResult(messages); },
            FinishTurn: async (_, token) => { observerCalls++; observerEntered.TrySetResult(); await releaseObserver.Task.WaitAsync(token); },
            FinishTurnDecision: async (turn, token) =>
            { decisionCalls++; snapshot = turn; decisionEntered.TrySetResult(); await releaseDecision.Task.WaitAsync(token); return AgentLoopFinishAction.End; });
        var run = Runner(transport, tool).RunAsync(Prompts(), callbacks, sink);
        try
        {
            await observerEntered.Task;
            Equal(0, decisionCalls); Equal(1, transport.Cleanups); Equal(1, tool.Executions);
            steering = [User("remain in caller steering queue")]; followUp = [User("remain in caller follow-up queue")];
            releaseObserver.TrySetResult(); await decisionEntered.Task;
            Equal(1, observerCalls); Equal(1, decisionCalls); Equal(1, steeringPolls); Equal(0, followUpPolls);
            Equal(1, transport.Requests.Count); Equal(0, sink.Events.OfType<AgentLoopTurnEnded>().Count());
            Equal(0, sink.Events.OfType<AgentLoopEnded>().Count());
            Equal("toolResult", snapshot!.Transcript.Last().Role); Equal(1, snapshot.ToolResults.Length);
            releaseDecision.TrySetResult(); var result = await run;
            Equal(AgentLoopStopReason.Completed, result.Reason); Equal(1, result.Turns.Length);
            Equal(1, transport.Requests.Count); Equal(1, transport.Cleanups);
            Equal(1, sink.Events.OfType<AgentLoopTurnEnded>().Count()); Equal(1, sink.Events.OfType<AgentLoopEnded>().Count());
            Equal(1, steeringPolls); Equal(0, followUpPolls); Equal(1, steering.Length); Equal(1, followUp.Length);
        }
        finally { releaseObserver.TrySetResult(); releaseDecision.TrySetResult(); try { await run; } catch { } }
    }

    private static async Task FinishDecisionFaults()
    {
        foreach (var cancel in new[] { false, true })
        {
            var transport = new ScriptTransport([Assistant(tool: true), Assistant(tool: false)]);
            var tool = new Tool(ToolResult.Success("retained")); var sink = new Sink();
            var entered = Gate(); var release = Gate(); using var cancellation = new CancellationTokenSource();
            AgentLoopTurn? snapshot = null; var polls = 0;
            var callbacks = new AgentLoopCallbacks(Request,
                GetSteeringMessages: _ => { polls++; return ValueTask.FromResult(ImmutableArray<TranscriptEntry>.Empty); },
                FinishTurnDecision: async (turn, token) =>
                { snapshot = turn; entered.TrySetResult(); await release.Task.WaitAsync(token); throw new InvalidOperationException("decision failed"); });
            var run = Runner(transport, tool).RunAsync(Prompts(), callbacks, sink, cancellation.Token);
            try
            {
                await entered.Task;
                Equal(1, transport.Cleanups); Equal(1, tool.Executions); Equal(1, snapshot!.ToolResults.Length);
                Equal("toolResult", snapshot.Transcript.Last().Role); Equal(1, polls);
                if (cancel) cancellation.Cancel(); else release.TrySetResult();
                if (cancel) await ThrowsAsync<OperationCanceledException>(() => run);
                else await ThrowsAsync<InvalidOperationException>(() => run);
                Equal(1, transport.Requests.Count); Equal(1, transport.Cleanups); Equal(1, polls);
                Equal(0, sink.Events.OfType<AgentLoopTurnEnded>().Count()); Equal(0, sink.Events.OfType<AgentLoopEnded>().Count());
            }
            finally { release.TrySetResult(); try { await run; } catch { } }
        }
    }

    private static async Task FailedTurnsIgnoreDecisions()
    {
        foreach (var reason in new[] { StopReason.Error, StopReason.Aborted })
        foreach (var decision in new[] { AgentLoopFinishAction.End, AgentLoopFinishAction.Continue })
        {
            var transport = new ScriptTransport([Assistant(tool: true) with { StopReason = reason }, Assistant(tool: false)]);
            var tool = new Tool(ToolResult.Success("unused")); var sink = new Sink(); var decisions = 0; var polls = 0;
            var entered = Gate(); var release = Gate();
            var callbacks = new AgentLoopCallbacks(Request,
                GetSteeringMessages: _ => { polls++; return ValueTask.FromResult(ImmutableArray<TranscriptEntry>.Empty); },
                GetFollowUpMessages: _ => throw new InvalidOperationException("Failed turn polled follow-up."),
                FinishTurnDecision: async (turn, token) =>
                { decisions++; Equal(0, turn.ToolResults.Length); entered.TrySetResult(); await release.Task.WaitAsync(token); return decision; });
            var run = Runner(transport, tool).RunAsync(Prompts(), callbacks, sink);
            try
            {
                await entered.Task; Equal(1, transport.Cleanups); Equal(0, tool.BeforeCount); Equal(0, tool.Executions);
                Equal(0, sink.Events.OfType<AgentLoopTurnEnded>().Count()); release.TrySetResult();
                var result = await run; Equal(AgentLoopStopReason.ChatFailure, result.Reason);
                Equal(1, decisions); Equal(1, polls); Equal(1, transport.Requests.Count); Equal(1, transport.Cleanups);
                Equal(reason, result.Turns.Single().Result.Chat.Message.StopReason);
                Equal(1, sink.Events.OfType<AgentLoopTurnEnded>().Count()); Equal(1, sink.Events.OfType<AgentLoopEnded>().Count());
            }
            finally { release.TrySetResult(); try { await run; } catch { } }
        }
    }

    public static async Task RequestPreparationCannotRewriteHistory()
    {
        foreach (var prepare in new Func<AgentLoopSnapshot, ChatRequest>[]
        {
            snapshot => new(snapshot.Model with { Id = "changed" }, snapshot.Transcript),
            snapshot => new(snapshot.Model, []),
            snapshot => new(snapshot.Model, snapshot.Transcript.Reverse().ToImmutableArray()),
            snapshot => new(snapshot.Model, [snapshot.Transcript[0], User("replacement")])
        })
        {
            var transport = new ScriptTransport([Assistant(tool: false)]);
            await ThrowsAsync<ArgumentException>(() => Runner(transport, new Tool(ToolResult.Success("unused")))
                .RunAsync(Prompts(), new((snapshot, _) => ValueTask.FromResult(prepare(snapshot))), new Sink()));
            Equal(0, transport.Requests.Count);
        }
        await ContextContinuationAndAdmission();
        await ContextPreparationCannotRewriteHistory();
    }

    private static async Task ContextAcrossCompletedGenerations()
    {
        var prompts = Prompts().SetItem(1, new("user", JsonData.Parse("""{"role":"user","content":"lookup","timestamp":1700000000000,"opaque":{"wide":9007199254740993,"scaled":1.0,"nullable":null}}""")));
        var firstTransport = new ScriptTransport([Assistant(tool: true), Assistant(tool: false)]);
        var firstTool = new Tool(new([new TextContent("retained")], JsonData.Parse("""{"wide":9007199254740993,"scaled":1.0,"nullable":null}""")));
        var first = await Runner(firstTransport, firstTool).RunAsync(prompts, new(Request), new Sink());
        Equal(2, first.Turns.Length); Equal(1, firstTool.Executions);
        var firstSnapshot = first.Turns[0].Transcript;
        var history = first.Transcript;
        for (var generation = 2; generation <= 3; generation++)
        {
            var transport = new ScriptTransport([Assistant(tool: false)]);
            var tool = new Tool(ToolResult.Success("unused")); var sink = new Sink();
            var input = User("generation " + generation); AgentLoopSnapshot? prepared = null;
            var result = await Runner(transport, tool).RunWithContextAsync(history, [input],
                new((snapshot, token) => { prepared = snapshot; return Request(snapshot, token); }), sink);
            Equal(AgentLoopStopReason.Completed, result.Reason); Equal(1, result.Turns.Length);
            Equal(0, result.Turns[0].TurnIndex); Equal(1, transport.Requests.Count); Equal(1, transport.Cleanups);
            Equal(0, tool.BeforeCount); Equal(0, tool.Executions);
            HistoryPrefix(history.Add(input), transport.Requests[0].Messages);
            Equal(history.Length + 1, transport.Requests[0].Messages.Length);
            HistoryPrefix(history.Add(input), prepared!.Transcript);
            HistoryPrefix(history, result.Transcript);
            Equal(history.Length + 2, result.Transcript.Length);
            Equal(input, sink.Events.OfType<AgentLoopInputMessageStarted>().Single().Message);
            Equal(input, sink.Events.OfType<AgentLoopInputMessageEnded>().Single().Message);
            history = result.Transcript;
        }
        Equal(4, firstSnapshot.Length); Equal(5, first.Transcript.Length);
        Equal(2, firstTransport.Requests[0].Messages.Length);
        HistoryPrefix(prompts, firstTransport.Requests[0].Messages);
        Equal(prompts[1].WireBody.Value.GetRawText(), history[1].WireBody.Value.GetRawText());
        Equal(first.Transcript[3].WireBody.Value.GetRawText(), history[3].WireBody.Value.GetRawText());
    }

    private static async Task ContextLimitsBeforeEffects()
    {
        var priorTransport = new ScriptTransport([Assistant(tool: true)]);
        var prior = await Runner(priorTransport, new Tool(ToolResult.Success("retained", terminate: true)))
            .RunAsync(Prompts(), new(Request), new Sink());
        var transport = new ScriptTransport([Assistant(tool: true)]);
        var tool = new Tool(ToolResult.Success("unused")); var sink = new Sink(); var preparations = 0; var polls = 0;
        var callbacks = new AgentLoopCallbacks((snapshot, token) => { preparations++; return Request(snapshot, token); },
            GetSteeringMessages: _ => { polls++; return ValueTask.FromResult(ImmutableArray<TranscriptEntry>.Empty); });
        await ThrowsAsync<ArgumentException>(() => Runner(transport, tool, new(MaximumTranscriptMessages: prior.Transcript.Length))
            .RunWithContextAsync(prior.Transcript, [User("exceeds combined capacity")], callbacks, sink));
        Equal(0, sink.Events.Count); Equal(0, preparations); Equal(0, polls);
        Equal(0, transport.Requests.Count); Equal(0, transport.Cleanups); Equal(0, tool.BeforeCount); Equal(0, tool.Executions);

        var fullSink = new Sink();
        var full = await Runner(transport, tool, new(MaximumTranscriptMessages: prior.Transcript.Length))
            .RunWithContextAsync(prior.Transcript, [], callbacks, fullSink);
        Equal(AgentLoopStopReason.TranscriptLimit, full.Reason); HistoryPrefix(prior.Transcript, full.Transcript);
        Equal(prior.Transcript.Length, full.Transcript.Length); Equal(0, full.Turns.Length);
        Equal(0, transport.Requests.Count); Equal(0, preparations); Equal(0, tool.Executions);
        Equal(0, fullSink.Events.OfType<AgentLoopInputMessageEnded>().Count());

        var rejectedTransport = new ScriptTransport([Assistant(tool: true)]);
        var rejectedTool = new Tool(ToolResult.Success("unused"));
        var rejected = await Runner(rejectedTransport, rejectedTool, new(MaximumTranscriptMessages: prior.Transcript.Length + 1))
            .RunWithContextAsync(prior.Transcript, [], new(Request), new Sink());
        Equal(AgentLoopStopReason.TranscriptLimit, rejected.Reason); Equal(1, rejectedTransport.Cleanups);
        HistoryPrefix(prior.Transcript, rejected.Transcript); Equal(prior.Transcript.Length, rejected.Transcript.Length);
        Check(rejected.RejectedAssistant is not null, "Context admission discarded the rejected current assistant.");
        Equal(0, rejectedTool.BeforeCount); Equal(0, rejectedTool.Executions);
    }

    private static async Task ContextContinuationAndAdmission()
    {
        var priorTransport = new ScriptTransport([Assistant(tool: true)]);
        var prior = await Runner(priorTransport, new Tool(ToolResult.Success("retained", terminate: true)))
            .RunAsync(Prompts(), new(Request), new Sink());
        foreach (var history in new[] { Prompts(), prior.Transcript })
        {
            var transport = new ScriptTransport([Assistant(tool: false)]);
            var tool = new Tool(ToolResult.Success("unused")); var sink = new Sink();
            var result = await Runner(transport, tool).RunWithContextAsync(history, [], new(Request), sink);
            Equal(AgentLoopStopReason.Completed, result.Reason); Equal(1, transport.Requests.Count);
            Equal(history.Length, transport.Requests[0].Messages.Length); HistoryPrefix(history, transport.Requests[0].Messages);
            Equal(0, sink.Events.OfType<AgentLoopInputMessageStarted>().Count());
            Equal(0, sink.Events.OfType<AgentLoopInputMessageEnded>().Count());
            Equal(0, tool.BeforeCount); Equal(0, tool.Executions);
        }
        foreach (var reason in new[] { StopReason.Error, StopReason.Aborted })
        {
            var failedTransport = new ScriptTransport([Assistant(tool: true) with { StopReason = reason }]);
            var failedTool = new Tool(ToolResult.Success("unused"));
            var failed = await Runner(failedTransport, failedTool).RunAsync(Prompts(), new(Request), new Sink());
            Equal(AgentLoopStopReason.ChatFailure, failed.Reason); Equal(0, failedTool.Executions);
            var transport = new ScriptTransport([Assistant(tool: false)]);
            var tool = new Tool(ToolResult.Success("unused")); var sink = new Sink(); var retry = User("retry");
            var retried = await Runner(transport, tool).RunWithContextAsync(failed.Transcript, [retry], new(Request), sink);
            Equal(AgentLoopStopReason.Completed, retried.Reason); HistoryPrefix(failed.Transcript.Add(retry), transport.Requests[0].Messages);
            Equal(0, tool.BeforeCount); Equal(0, tool.Executions);
            Equal(retry, sink.Events.OfType<AgentLoopInputMessageEnded>().Single().Message);
        }

        var invalidHistories = new ImmutableArray<TranscriptEntry>[]
        {
            default, [null!], [new("custom", JsonData.Parse("""{"role":"custom"}"""))],
            [new("assistant", JsonData.Parse("""{"role":"user"}"""))], [new("assistant", JsonData.Null)],
            [new("assistant", JsonData.Parse("""{"role":"assistant"}"""))],
            [new("assistant", PiWireJson.WriteMessage(Assistant(tool: false) with { StopReason = StopReason.Pending }))],
            [new("assistant", PiWireJson.WriteMessage(Assistant(tool: false) with { StopReason = StopReason.Deferred }))],
            [new("assistant", PiWireJson.WriteMessage(Assistant(tool: true) with { Content = [Assistant(tool: true).Content[0], Assistant(tool: true).Content[0]] }))],
            [new("toolResult", JsonData.Parse("""{"role":"toolResult","toolCallId":"call","toolName":"lookup","timestamp":0,"isError":false,"details":{},"content":[{"type":"toolCall","id":"call","name":"lookup","arguments":{}}]}"""))],
            [new("toolResult", JsonData.Parse("""{"role":"toolResult","toolCallId":"call","toolName":"lookup","timestamp":0,"content":[]}"""))]
        };
        foreach (var history in invalidHistories) await Rejected(history, [User("new prompt")]);
        // The whole-source createToolResultMessage observation omits absent details. Preserve
        // this original token sequence as a positive continuation control; do not fabricate {}.
        var absentDetails = new TranscriptEntry("toolResult", JsonData.Parse("""{"role":"toolResult","toolCallId":"call","toolName":"lookup","timestamp":0,"isError":false,"content":[]}"""));
        var retainedTransport = new ScriptTransport([Assistant(tool: false)]);
        var retainedTool = new Tool(ToolResult.Success("unused"));
        var retainedResult = await Runner(retainedTransport, retainedTool).RunWithContextAsync([absentDetails], [User("valid source continuation")], new(Request), new Sink());
        Equal(AgentLoopStopReason.Completed, retainedResult.Reason);
        Equal(absentDetails, retainedTransport.Requests[0].Messages[0]);
        Check(!retainedTransport.Requests[0].Messages[0].WireBody.Value.TryGetProperty("details", out _), "Absent source details were fabricated.");
        Equal(0, retainedTool.Executions);
        foreach (var history in new ImmutableArray<TranscriptEntry>[] { [], [Prompts()[0]],
            Prompts().Add(new("assistant", PiWireJson.WriteMessage(Assistant(tool: false)))), Prompts().Add(Prompts()[0]) })
            await Rejected(history, []);
        await Rejected(prior.Transcript, default);
        await Rejected(prior.Transcript, [new("assistant", PiWireJson.WriteMessage(Assistant(tool: false)))]);

        // The original low-level entry point still permits an empty initial input batch.
        var emptyTransport = new ScriptTransport([Assistant(tool: false)]);
        var empty = await Runner(emptyTransport, new Tool(ToolResult.Success("unused"))).RunAsync([], new(Request), new Sink());
        Equal(AgentLoopStopReason.Completed, empty.Reason); Equal(0, emptyTransport.Requests[0].Messages.Length);

        static async Task Rejected(ImmutableArray<TranscriptEntry> history, ImmutableArray<TranscriptEntry> input)
        {
            var transport = new ScriptTransport([Assistant(tool: true)]); var tool = new Tool(ToolResult.Success("unused"));
            var sink = new Sink(); var preparations = 0; var polls = 0;
            var callbacks = new AgentLoopCallbacks((snapshot, token) => { preparations++; return Request(snapshot, token); },
                GetSteeringMessages: _ => { polls++; return ValueTask.FromResult(ImmutableArray<TranscriptEntry>.Empty); });
            await ThrowsAsync<ArgumentException>(() => Runner(transport, tool).RunWithContextAsync(history, input, callbacks, sink));
            Equal(0, sink.Events.Count); Equal(0, preparations); Equal(0, polls);
            Equal(0, transport.Requests.Count); Equal(0, transport.Cleanups); Equal(0, tool.BeforeCount); Equal(0, tool.Executions);
        }
    }

    private static async Task ContextPreparationCannotRewriteHistory()
    {
        var prior = Prompts().SetItem(1, new("user", JsonData.Parse("""{"role":"user","content":"lookup","timestamp":1700000000000,"opaque":1.0}""")))
            .Add(new("assistant", PiWireJson.WriteMessage(Assistant(tool: false))));
        foreach (var prepare in new Func<AgentLoopSnapshot, ChatRequest>[]
        {
            snapshot => new(snapshot.Model, snapshot.Transcript.RemoveAt(2)),
            snapshot => new(snapshot.Model, snapshot.Transcript.SetItem(2, snapshot.Transcript[1])),
            snapshot => new(snapshot.Model, snapshot.Transcript.Reverse().ToImmutableArray()),
            snapshot => new(snapshot.Model, snapshot.Transcript.SetItem(1, new("user",
                JsonData.Parse(snapshot.Transcript[1].WireBody.Value.GetRawText().Replace("1.0", "1", StringComparison.Ordinal))))),
            snapshot => new(snapshot.Model, snapshot.Transcript.SetItem(2, new("assistant", PiWireJson.WriteMessage(
                Assistant(tool: false) with { Content = [new TextContent("rewritten prior answer")] }))))
        })
        {
            var transport = new ScriptTransport([Assistant(tool: false)]); var sink = new Sink();
            await ThrowsAsync<ArgumentException>(() => Runner(transport, new Tool(ToolResult.Success("unused")))
                .RunWithContextAsync(prior, [User("next")], new((snapshot, _) => ValueTask.FromResult(prepare(snapshot))), sink));
            Equal(0, transport.Requests.Count); Equal(1, sink.Events.OfType<AgentLoopInputMessageEnded>().Count());
            Equal("done", PiWireJson.ReadMessage(prior[2].WireBody.Value).Content.OfType<TextContent>().Single().Text);
        }
    }

    private static void HistoryPrefix(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    {
        Check(actual.Length >= expected.Length, "Canonical history prefix was omitted.");
        for (var index = 0; index < expected.Length; index++)
        {
            Equal(expected[index].Role, actual[index].Role);
            Equal(expected[index].WireBody.Value.GetRawText(), actual[index].WireBody.Value.GetRawText());
        }
    }

    private static AgentLoopRunner Runner(ScriptTransport transport, Tool tool, AgentLoopOptions? options = null) => new(
        new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([new("lookup", tool)], tool)), Model, () => Clock, options);
    private static ValueTask<ChatRequest> Request(AgentLoopSnapshot snapshot, CancellationToken _) => ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript));
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = Clock })));
    private static ImmutableArray<TranscriptEntry> Prompts() => [new("system", JsonData.Parse("""{"role":"system","content":"synthetic","timestamp":0}""")), User("lookup")];
    private static AssistantMessage Assistant(bool tool) => new(Model.Api, Model.Provider, Model.Id, Clock,
        tool ? [new ToolCallContent("call", "lookup", JsonData.Parse("{\"value\":7}"))] : [new TextContent("done")], TokenUsage.Zero,
        tool ? StopReason.ToolUse : StopReason.Stop);
    private static ImmutableArray<TranscriptEntry> Entries(JsonElement messages) => messages.EnumerateArray()
        .Select(value => new TranscriptEntry(value.GetProperty("role").GetString()!, JsonData.FromElement(value))).ToImmutableArray();
    private static ImmutableArray<TranscriptEntry> PreparedPrompts(JsonElement input, JsonElement scenario)
    {
        var prompts = Entries(scenario.GetProperty("prompts")).ToBuilder();
        var system = JsonNode.Parse(prompts[0].WireBody.ToString())!.AsObject();
        system["toolsAdded"] = new JsonArray(JsonNode.Parse(input.GetProperty("tool").GetRawText()));
        prompts[0] = new("system", JsonData.Parse(system.ToJsonString()));
        return prompts.ToImmutable();
    }
    private static ToolResult ReadToolResult(JsonElement value) => new(
        value.GetProperty("content").EnumerateArray().Select(content => (TextContent)PiWireJson.ReadContent(content)).ToImmutableArray(),
        JsonData.FromElement(value.GetProperty("details")), Terminate: value.GetProperty("terminate").GetBoolean());

    private sealed class ScriptTransport(AssistantMessage[] messages) : IChatTransport
    {
        public AssistantMessage[] Messages { get; } = messages;
        public List<ChatRequest> Requests { get; } = [];
        public int Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var final = Messages[Requests.Count]; Requests.Add(request);
            try
            {
                yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
                for (var index = 0; index < final.Content.Length; index++)
                    if (final.Content[index] is ToolCallContent call)
                    {
                        yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject });
                        yield return new ToolCallDelta(index, call.Arguments.ToString());
                        yield return new ToolCallEnded(index, call);
                    }
                    else if (final.Content[index] is TextContent text)
                    {
                        yield return new TextStarted(index, new("")); yield return new TextDelta(index, text.Text);
                        yield return new TextEnded(index, text.Text);
                    }
                if (final.StopReason is StopReason.Error or StopReason.Aborted) yield return new StreamError(final.StopReason, final);
                else yield return new StreamDone(final.StopReason, final);
                await Task.CompletedTask;
            }
            finally { Cleanups++; }
        }
    }
    private sealed class Tool(ToolResult result, List<string>? order = null, bool gated = false) : IToolExecutor, IToolHooks
    {
        public readonly TaskCompletionSource Started = Gate(); public readonly TaskCompletionSource Release = Gate();
        public int Executions; public int BeforeCount; public int AfterCount; public ToolInvocation? LastInvocation;
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); BeforeCount++; order?.Add("tool.preflight"); return ValueTask.FromResult(ToolPreflightDecision.Allow); }
        public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        {
            Executions++; LastInvocation = invocation; order?.Add("tool.execute_start"); Started.TrySetResult();
            if (gated) await Release.Task.WaitAsync(token);
            order?.Add("tool.execute_finish"); return result;
        }
        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult value, CancellationToken token)
        { token.ThrowIfCancellationRequested(); AfterCount++; order?.Add("tool.after"); return ValueTask.FromResult(value); }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask>? emit = null) : IAgentEventSink
    {
        public readonly List<AgentEvent> Events = [];
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Events.Add(observation); return emit?.Invoke(observation, token) ?? ValueTask.CompletedTask; }
    }

    private static string? EventKind(AgentEvent value) => value switch
    {
        AgentLoopStarted => "agent_start", AgentLoopTurnStarted => "turn_start", AgentLoopInputMessageEnded input => "message_end:" + input.Message.Role,
        AssistantMessageEnded => "message_end:assistant", ToolExecutionStarted => "tool_execution_start", ToolExecutionEnded => "tool_execution_end",
        ToolResultMessageEnded => "message_end:toolResult", AgentLoopTurnEnded => "turn_end", AgentLoopEnded => "agent_end", _ => null
    };
    private static IEnumerable<string> ProjectOrder(JsonElement expected, string hookProperty = "hookTrace", string queueProperty = "queueTrace", string toolProperty = "toolTrace")
    {
        foreach (var entry in expected.GetProperty("order").EnumerateArray())
        {
            var kind = entry.GetProperty("kind").GetString(); var index = entry.GetProperty("index").GetInt32();
            if (kind == "request") yield return "request";
            else if (kind == "event")
            {
                var value = expected.GetProperty("events")[index]; var type = value.GetProperty("type").GetString();
                if (type == "message_end") yield return "event.message_end:" + value.GetProperty("message").GetProperty("role").GetString();
                else if (type is "agent_start" or "turn_start" or "tool_execution_start" or "tool_execution_end" or "turn_end" or "agent_end") yield return "event." + type;
            }
            else if (kind == "queue")
            {
                var operation = expected.GetProperty(queueProperty)[index].GetProperty("kind").GetString()!;
                if (operation.EndsWith("_poll", StringComparison.Ordinal)) yield return "queue." + operation;
            }
            else if (kind == "hook")
            {
                var operation = expected.GetProperty(hookProperty)[index].GetProperty("kind").GetString();
                if (operation != "convert_to_llm") yield return "hook." + operation;
            }
            else if (kind == "tool") yield return "tool." + expected.GetProperty(toolProperty)[index].GetProperty("kind").GetString();
        }
    }
    private static JsonDocument ReadFixture(string name, string hash, string family = "loop-continuation")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "fixtures", "pi-v0.99.1", family))) directory = directory.Parent;
        Check(directory is not null, "Frozen loop fixture directory unavailable.");
        var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "fixtures", "pi-v0.99.1", family, name));
        Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes))); return JsonDocument.Parse(bytes);
    }
    private static void SameMessages(JsonElement expected, ImmutableArray<TranscriptEntry> actual)
    {
        Equal(expected.GetArrayLength(), actual.Length);
        for (var index = 0; index < actual.Length; index++) Check(Same(expected[index], actual[index].WireBody.Value, rootMessage: true), $"Transcript message {index} differs.");
    }
    private static bool Same(JsonElement left, JsonElement right, bool rootMessage = false)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var assistant = rootMessage && left.TryGetProperty("role", out var role) && role.GetString() == "assistant";
            if (assistant && (!left.TryGetProperty("thinkingLevel", out var thinking) || thinking.GetString() != "off" || right.TryGetProperty("thinkingLevel", out _))) return false;
            // One disclosed root-message field is absent in the native request contract. Opaque nested objects stay exact.
            var a = left.EnumerateObject().Where(property => !(assistant && property.Name == "thinkingLevel")).ToDictionary(property => property.Name, property => property.Value);
            var b = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            return a.Count == b.Count && a.All(property => b.TryGetValue(property.Key, out var value) && Same(property.Value, value));
        }
        if (left.ValueKind == JsonValueKind.Array) return left.GetArrayLength() == right.GetArrayLength() &&
            left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Same(pair.First, pair.Second));
        return left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText();
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual),
        "Projected order differs. Expected: " + string.Join(",", expected) + "; actual: " + string.Join(",", actual));
    private static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
