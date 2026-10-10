using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class Program
{
    private static object? schedulerDifferentialEvidence;
    private static async Task<int> Main(string[] args)
    {
        string? report = null;
        if (args.Length == 2 && args[0] == "--report") report = Path.GetFullPath(args[1]);
        else if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: PiSharp.Agent.Tests [--report <path>]");
            return 2;
        }
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("assistant message barrier precedes preflight", AssistantBarrier),
            ("preflight is sequential before parallel execution", OrderedPreflight),
            ("C/A/B completion and A/B/C transcript with awaited sink", CompletionOrder),
            ("blocked B and failing A settle independently", BlockAndFailure),
            ("sequential override anywhere serializes batch", SequentialOverrides),
            ("blocked sequential tool still serializes batch", BlockedSequentialOverride),
            ("cancellation during preflight prevents execution", CancellationDuringPreflight),
            ("preflight deadline joins original batch and stops admission", PreflightDeadlineCleanup),
            ("preflight early hook failure joins original batch", PreflightEarlyHookFailureCleanup),
            ("cancellation before batch prevents execution", CancellationBeforeBatch),
            ("execution cancellation preserves completed outcomes", CancellationDuringExecution),
            ("sink cancellation remains a task cancellation", SinkCancellation),
            ("all termination hints suppress continuation; mixed continues", TerminationHints),
            ("invalid arguments, unknown tools and hook failures are results", PipelineFailures),
            ("truncated, failed and aborted assistants cannot execute tools", UnusableAssistants),
            ("last transcript subscriber is awaited", FinalMessageBarrier),
            ("sink failure cancels and settles admitted tools", SinkFailureSettlesTools),
            ("duplicate tool names and unfinished assistant are rejected", ContractValidation),
            ("bounded scheduler projection matches frozen awaited upstream oracle", FrozenSchedulerProjection),
            ("one turn drains text progress and commits assistant once", TurnTextSuccess),
            ("one turn composes genuine all-terminate tool scenario", TurnGenuineToolComposition),
            ("one turn returns provider failure without tool execution", TurnProviderFailure),
            ("one turn preserves partial EOF and prevents tool execution", TurnPartialEof),
            ("chat cancellation settles gated cleanup before propagation", TurnStreamCancellation),
            ("cleanup and assistant sink barriers precede turn effects", TurnAssistantBarrierAfterCleanup),
            ("progress sink failure waits for owned run cleanup", TurnSinkFailureSettlesCleanup),
            ("Responses authoritative tool arguments wait for cleanup and assistant barrier", ResponsesTurnTests.AuthoritativeArgumentsAfterBarriers),
            ("Responses truncated authoritative arguments finalize to {} and reach the tool", ResponsesTurnTests.TruncatedAuthoritativeArgumentsFinalizeEmpty),
            ("Responses DTOs with no open slot after a valid tool end are ignored", ResponsesTurnTests.UnmatchedDtosAfterToolEndAreIgnored),
            ("Responses error event after a valid tool end prevents tool effects", ResponsesTurnTests.ErrorAfterToolEndPreventsEffects),
            ("bounded loop projections match continuation and selected finish-decision cases", AgentLoopRunnerTests.FrozenContinuationProjection),
            ("awaited finish/preparation callbacks block continuation", AgentLoopRunnerTests.AwaitedCallbacksBlockContinuation),
            ("loop callback failures and cancellation prevent later requests", AgentLoopRunnerTests.CallbackFailureAndCancellation),
            ("loop limits preserve completed results and pending messages", AgentLoopRunnerTests.LimitsPreserveResultsAndPending),
            ("loop request preparation cannot rewrite canonical history", AgentLoopRunnerTests.RequestPreparationCannotRewriteHistory)
        }.Concat(AgentPendingInputQueueTests.Cases()).Concat(new (string Name, Func<Task> Run)[]
        {
            ("tool output head/tail matches248 genuine results with exact metadata", ToolOutputTruncatorTests.FrozenReferenceCorpus),
            ("tool output native budgets and lossless UTF16 policy", ToolOutputTruncatorTests.NativeInputPolicy)
        }).Concat(ToolInvokerTests.Cases()).Concat(ToolArgumentDataNulTests.Cases()).Concat(ToolLoneSurrogateTests.Cases()).Concat(FileMutationQueueTests.Cases()).Concat(AgentTests.Cases()).Concat(AbortLifecycleTests.Cases()).Concat(StructuredToolResultTests.Cases()).Concat(ToolProgressTests.Cases())
            .Concat(SourceProgressDifferentialTests.Cases()).Concat(SourceProgressOwnershipTests.Cases())
            .Concat(SourceToolResultValueTests.Cases()).Concat(BoundedToolProgressDeliveryTests.Cases())
            .Concat(CompletionsHttpTurnTests.Cases()).Concat(CompletionsImageProducingToolTests.Cases()).Concat(ToolImageContentValueTests.Cases()).Concat(ToolImageResumeTests.Cases()).Concat(FailedAssistantSettlementTests.Cases())
            .Concat(PiMessagesAgentIntegrationTests.Cases()).Concat(OriginalPiMessagesToolContinuationTests.Cases()).Concat(NestedToolInvocationTests.Cases())
            .Select(test => (Id: $"agent.case.{test.Name}", test.Name, test.Run)).ToArray();
        // Registration names include parameter values; method names can be shared by distinct cases.
        // Validate the entire inventory before admitting any case, using exact ordinal identity.
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in tests)
            if (!caseIds.Add(test.Id))
                throw new InvalidOperationException($"Duplicate Agent case ID before admission: {test.Id}");
        var failures = 0;
        var admitted = 0;
        var preflightDeadlineExceeded = false;
        var evidence = new List<object>();
        foreach (var test in tests)
        {
            admitted++;
            try
            {
                // This timeout only guards a hang. All ordering uses explicit task gates below.
                if (test.Run == CancellationDuringPreflight || test.Run == PreflightDeadlineCleanup ||
                    test.Run == PreflightEarlyHookFailureCleanup)
                    await test.Run(); // This fixture owns its deadline, gate release and original batch join.
                else if (test.Name.StartsWith(NestedToolInvocationTests.Prefix, StringComparison.Ordinal))
                    await test.Run(); // The case owns its gates and actual joins; elapsed time cannot detach it.
                else if (PiMessagesAgentIntegrationTests.OwnsCase(test.Name))
                    await PiMessagesAgentIntegrationTests.RunOwnedCaseAsync(test.Name, test.Run, report);
                else await test.Run().WaitAsync(TimeSpan.FromSeconds(20));
                Console.WriteLine($"PASS {test.Name}");
                evidence.Add(new { testId = test.Id, legacyTestId = $"agent.{test.Run.Method.Name}", name = test.Name, status = "passed" });
            }
            catch (Exception error)
            {
                preflightDeadlineExceeded |= error is PreflightDeadlineException;
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {error}");
                evidence.Add(new { testId = test.Id, legacyTestId = $"agent.{test.Run.Method.Name}", name = test.Name,
                    status = StopsCaseAdmission(error) ? "incomplete" : "failed", error = error.Message });
                if (StopsCaseAdmission(error)) break;
            }
        }
        foreach (var test in tests.Skip(admitted))
            evidence.Add(new { testId = test.Id, legacyTestId = $"agent.{test.Run.Method.Name}", name = test.Name, status = "unexecuted" });
        Console.WriteLine($"{admitted - failures}/{tests.Length} agent prototype tests passed; {tests.Length - admitted} unexecuted");
        if (report is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
                scope = "agent-ordering-prototype",
                testIdScheme = "agent.case.display-name.v1",
                turnRunnerScope = "one-explicit-turn-integration",
                preflightCancellation = new { status = preflightDeadlineExceeded ? "INCOMPLETE_NONPASSING" : "authored-regressions",
                    originalBatchOwned = true, runtimeAcceptance = false },
                schedulerDifferential = schedulerDifferentialEvidence,
                toolOutputDifferential = ToolOutputTruncatorTests.DifferentialEvidence,
                abortedWorkDifferential = AbortLifecycleTests.DifferentialEvidence,
                completionsImageProducingToolDifferential = CompletionsImageProducingToolTests.DifferentialEvidence,
                completionsToolImageResumeDifferential = ToolImageResumeTests.DifferentialEvidence,
                piMessagesIntegration = new { scope = "authored-offline-provider-registry-high-level-agent",
                    status = PiMessagesAgentIntegrationTests.DeadlineExceeded ? "INCOMPLETE_NONPASSING" : "authored-regressions",
                    incompleteReceipt = PiMessagesAgentIntegrationTests.IncompleteReceipt, runtimeAcceptance = false },
                originalPiMessagesToolContinuation = OriginalPiMessagesToolContinuationTests.Evidence,
                originalPiMessagesToolContinuationFailure = OriginalPiMessagesToolContinuationTests.FailureEvidence,
                originalPiMessagesToolContinuationFailureControl = OriginalPiMessagesToolContinuationTests.ControlledFailureEvidence,
                tests = evidence,
                passed = admitted - failures,
                unexecuted = tests.Length - admitted,
                failed = failures
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task TurnTextSuccess()
    {
        var final = Message() with { Content = [new TextContent("authoritative")], StopReason = StopReason.Stop };
        var frames = TurnFrames(final);
        var transport = new TestChatTransport(frames);
        var sink = new Sink((observation, _) =>
        {
            if (observation is AssistantMessageEnded)
                Check(transport.CleanupFinished.Task.IsCompleted, "Assistant commit preceded owned cleanup.");
            return ValueTask.CompletedTask;
        });
        var request = TurnRequest(final);
        var result = await new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([]))
            .RunAsync(request, sink);
        Equal(final, result.Chat.Message);
        Check(result.Chat.Failure is null && result.CleanupFailure is null, "Successful text turn gained a failure.");
        Equal(0, result.Tools.Messages.Length);
        Check(!result.Tools.ShouldContinue, "Text-only turn has no scheduler continuation hint.");
        Equal(1, transport.RequestCount);
        Check(ReferenceEquals(request, transport.LastRequest), "Runner changed caller request ownership.");
        var forwarded = sink.Events.OfType<TurnStreamObserved>().Select(observation => observation.Event).ToArray();
        Equal(frames.Length, forwarded.Length);
        for (var index = 0; index < frames.Length; index++)
            Check(ReferenceEquals(frames[index], forwarded[index]), "Runner replaced an owned immutable progress frame.");
        Equal(1, sink.Events.OfType<AssistantMessageEnded>().Count());
        Check(sink.Events.Last() is AssistantMessageEnded, "The sole assistant commit must follow chat progress.");
    }

    private static async Task TurnGenuineToolComposition()
    {
        using var input = ReadFrozenFixture("awaited-parallel.input.json", "8807a17dcfb167ffce1103a117ddcb495495e535180f5b7ee8dc236653aefc51");
        using var oracle = ReadFrozenFixture("awaited-parallel.expected.json", "fd21837b2427e24d2a6d565a554ac1fc91518aa152dac2dfcbcf8874062f814b");
        var final = PiWireJson.ReadMessage(input.RootElement.GetProperty("assistantMessage"));
        var calls = final.Content.Cast<ToolCallContent>().ToArray();
        var tools = calls.ToDictionary(call => call.Id, _ => new ControlledTool(), StringComparer.Ordinal);
        var observed = calls.ToDictionary(call => call.Id, _ => Gate(), StringComparer.Ordinal);
        var completionIds = new ConcurrentQueue<string>();
        var transport = new TestChatTransport(TurnFrames(final));
        var sink = new Sink((observation, _) =>
        {
            if (observation is AssistantMessageEnded)
                Check(transport.CleanupFinished.Task.IsCompleted, "Tool batch preceded stream cleanup.");
            if (observation is ToolExecutionEnded completion)
            {
                completionIds.Enqueue(completion.Outcome.Invocation.Call.Id);
                observed[completion.Outcome.Invocation.Call.Id].SetResult();
            }
            return ValueTask.CompletedTask;
        });
        var scheduler = new ToolBatchScheduler(calls.Select(call => new ToolDefinition(call.Name, tools[call.Id])));
        var run = new TurnRunner(new ChatClient(transport, capacity: 1), scheduler).RunAsync(TurnRequest(final), sink);
        await Task.WhenAll(tools.Values.Select(tool => tool.Started.Task));
        foreach (var id in OracleIds(input.RootElement.GetProperty("completionOrder")))
        {
            var call = calls.Single(call => call.Id == id);
            tools[id].Release.SetResult(new([new TextContent($"result:{id}")], call.Arguments, Terminate: true));
            await observed[id].Task;
        }
        var result = await run;
        var expected = oracle.RootElement.GetProperty("observations");
        Sequence(OracleIds(expected.GetProperty("checks").GetProperty("completionOrder")), completionIds);
        var expectedResults = expected.GetProperty("finalResult").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "toolResult").ToArray();
        Equal(expectedResults.Length, result.Tools.Messages.Length);
        for (var index = 0; index < expectedResults.Length; index++)
        {
            var actual = result.Tools.Messages[index]; var captured = expectedResults[index];
            Equal(captured.GetProperty("toolCallId").GetString(), actual.ToolCallId);
            Equal(captured.GetProperty("toolName").GetString(), actual.ToolName);
            Equal(captured.GetProperty("content")[0].GetProperty("text").GetString(), actual.Content[0].Text);
            Equal(captured.GetProperty("isError").GetBoolean(), actual.IsError);
            EquivalentJson(captured.GetProperty("details"), actual.Details.Value);
        }
        Check(result.Tools.Terminate && !result.Tools.ShouldContinue, "All-terminate composition requested continuation.");
        Check(result.Chat.Failure is null, "Synthetic native normalized stream failed.");
        Equal(1, transport.RequestCount);
        Equal(1, sink.Events.OfType<AssistantMessageEnded>().Count());
        Equal(1, sink.Events.OfType<TurnStreamObserved>().Count(observation => observation.Event is StreamTerminalEvent));
    }

    private static async Task TurnProviderFailure()
    {
        var tool = new ControlledTool();
        var failed = Message("A") with { StopReason = StopReason.Error };
        var transport = new TestChatTransport([new StreamError(StopReason.Error, failed)]);
        var sink = new Sink();
        var result = await new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([new("A", tool)]))
            .RunAsync(TurnRequest(failed), sink);
        Equal(ChatFailureKind.Provider, result.Chat.Failure!.Kind);
        Equal(StopReason.Error, result.Chat.Message.StopReason);
        Check(!tool.Started.Task.IsCompleted && !result.Tools.ShouldContinue, "Failed chat executed tools or continued.");
        Equal(0, result.Tools.Outcomes.Length);
        Equal(1, sink.Events.OfType<AssistantMessageEnded>().Count());
        Check(transport.CleanupFinished.Task.IsCompleted, "Failure result preceded transport cleanup.");
    }

    private static async Task TurnPartialEof()
    {
        var tool = new ControlledTool(); var header = Message() with { StopReason = StopReason.Pending };
        var transport = new TestChatTransport([new StreamStarted(header), new TextStarted(0, new TextContent("")),
            new TextDelta(0, "partial"), new ToolCallStarted(1, Call("A")), new ToolCallDelta(1, "{\"value\":")]);
        var sink = new Sink();
        var result = await new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([new("A", tool)]))
            .RunAsync(TurnRequest(header), sink);
        Equal(ChatFailureKind.UnexpectedEof, result.Chat.Failure!.Kind);
        Equal(StopReason.Error, result.Chat.Message.StopReason);
        Equal("partial", ((TextContent)result.Chat.Message.Content[0]).Text);
        Check(result.Chat.Message.Content[1] is ToolCallContent, "Failure lost its partial tool-call observation.");
        Check(!tool.Started.Task.IsCompleted, "Partial EOF authorized a tool call.");
        Equal(0, result.Tools.Outcomes.Length);
        Equal(1, sink.Events.OfType<TurnStreamObserved>().Count(observation => observation.Event is StreamError));
        Check(transport.CleanupFinished.Task.IsCompleted, "EOF result preceded cleanup.");
    }

    private static async Task TurnStreamCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var terminalRelease = Gate(); var cleanupRelease = Gate(); var deltaEntered = Gate(); var deltaRelease = Gate();
        var final = Message("A");
        var transport = new TestChatTransport(TurnFrames(final), cleanupRelease.Task, terminalRelease.Task);
        var tool = new ControlledTool();
        var sink = new Sink(async (observation, _) =>
        {
            if (observation is TurnStreamObserved { Event: ToolCallDelta })
            { deltaEntered.SetResult(); await deltaRelease.Task; }
        });
        var run = new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([new("A", tool)]))
            .RunAsync(TurnRequest(final), sink, cancellation.Token);
        await deltaEntered.Task;
        cancellation.Cancel();
        await transport.CleanupEntered.Task;
        Check(!run.IsCompleted && !tool.Started.Task.IsCompleted, "Cancellation skipped owned cleanup or admitted effects.");
        deltaRelease.SetResult(); cleanupRelease.SetResult();
        await ThrowsAsync<OperationCanceledException>(async () => await run);
        Check(transport.CleanupFinished.Task.IsCompleted, "Cancellation propagated before cleanup settled.");
        Check(!tool.Started.Task.IsCompleted, "Canceled chat executed tools.");
        Equal(0, sink.Events.OfType<AssistantMessageEnded>().Count());
        Equal(1, sink.Events.OfType<TurnStreamObserved>().Count(observation => observation.Event is StreamError { Reason: StopReason.Aborted }));
    }

    private static async Task TurnAssistantBarrierAfterCleanup()
    {
        var cleanupRelease = Gate(); var assistantEntered = Gate(); var assistantRelease = Gate();
        var final = Message("A"); var transport = new TestChatTransport(TurnFrames(final), cleanupRelease.Task);
        var tool = new ControlledTool(); var before = 0;
        var hooks = new Hooks(before: (_, _) => { before++; return ValueTask.FromResult(ToolPreflightDecision.Allow); });
        var sink = new Sink(async (observation, _) =>
        {
            if (observation is AssistantMessageEnded)
            {
                Check(transport.CleanupFinished.Task.IsCompleted, "Assistant barrier began before cleanup.");
                assistantEntered.SetResult(); await assistantRelease.Task;
            }
        });
        var run = new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([new("A", tool)], hooks))
            .RunAsync(TurnRequest(final), sink);
        await transport.CleanupEntered.Task;
        Check(!assistantEntered.Task.IsCompleted && !tool.Started.Task.IsCompleted, "Cleanup gate did not protect commit/effects.");
        cleanupRelease.SetResult();
        await assistantEntered.Task;
        Equal(0, before);
        Check(!tool.Started.Task.IsCompleted && !run.IsCompleted, "Assistant barrier failed to protect effects.");
        assistantRelease.SetResult();
        await tool.Started.Task;
        tool.Release.SetResult(ToolResult.Success("A"));
        var result = await run;
        Check(result.Tools.ShouldContinue, "Non-terminating batch lost its continuation hint.");
        Equal(1, before);
        Equal(1, transport.RequestCount); // A hint must never turn into an automatic second request here.
    }

    private static async Task TurnSinkFailureSettlesCleanup()
    {
        var cleanupRelease = Gate(); var final = Message("A");
        var transport = new TestChatTransport(TurnFrames(final), cleanupRelease.Task);
        var tool = new ControlledTool();
        var sink = new Sink((observation, _) =>
        { if (observation is TurnStreamObserved) throw new InvalidOperationException("turn sink failed"); return ValueTask.CompletedTask; });
        var run = new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([new("A", tool)]))
            .RunAsync(TurnRequest(final), sink);
        await transport.CleanupEntered.Task;
        Check(!run.IsCompleted, "Sink failure propagated before owned cleanup.");
        Check(!tool.Started.Task.IsCompleted, "Sink failure authorized tool effects.");
        cleanupRelease.SetResult();
        await ThrowsAsync<InvalidOperationException>(async () => await run, "turn sink failed");
        Check(transport.CleanupFinished.Task.IsCompleted, "Failed sink left transport cleanup unfinished.");
        Equal(0, sink.Events.OfType<AssistantMessageEnded>().Count());
    }

    private static ChatRequest TurnRequest(AssistantMessage message) => new(new(message.Model, message.Api, message.Provider), [], message.Timestamp);

    private static StreamEvent[] TurnFrames(AssistantMessage message)
    {
        var frames = new List<StreamEvent> { new StreamStarted(message with { Content = [], StopReason = StopReason.Pending }) };
        for (var index = 0; index < message.Content.Length; index++)
        {
            if (message.Content[index] is TextContent text)
            {
                frames.Add(new TextStarted(index, new TextContent("", text.ExtraProperties)));
                frames.Add(new TextDelta(index, text.Text));
                frames.Add(new TextEnded(index, text.Text, text.ExtraProperties));
            }
            else if (message.Content[index] is ToolCallContent call)
            {
                frames.Add(new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject }));
                frames.Add(new ToolCallDelta(index, call.Arguments.ToString()));
                frames.Add(new ToolCallEnded(index, call));
            }
            else throw new InvalidOperationException("Synthetic turn helper supports only its text/tool scenarios.");
        }
        frames.Add(new StreamDone(message.StopReason, message));
        return frames.ToArray();
    }

    private static async Task FrozenSchedulerProjection()
    {
        const string sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
        const string inputSha = "8807a17dcfb167ffce1103a117ddcb495495e535180f5b7ee8dc236653aefc51";
        const string expectedSha = "fd21837b2427e24d2a6d565a554ac1fc91518aa152dac2dfcbcf8874062f814b";
        using var inputDocument = ReadFrozenFixture("awaited-parallel.input.json", inputSha);
        using var oracleDocument = ReadFrozenFixture("awaited-parallel.expected.json", expectedSha);
        var input = inputDocument.RootElement;
        var oracle = oracleDocument.RootElement;
        Equal(sourceSha, input.GetProperty("sourceSha").GetString());
        Equal(sourceSha, oracle.GetProperty("sourceSha").GetString());
        Equal("captured-upstream-agent-oracle", oracle.GetProperty("kind").GetString());
        var observed = oracle.GetProperty("observations");
        var checks = observed.GetProperty("checks");
        var message = PiWireJson.ReadMessage(input.GetProperty("assistantMessage"));
        var calls = message.Content.Cast<ToolCallContent>().ToArray();
        var releases = calls.ToDictionary(call => call.Id, _ => Gate(), StringComparer.Ordinal);
        var started = calls.ToDictionary(call => call.Id, _ => Gate(), StringComparer.Ordinal);
        var ended = calls.ToDictionary(call => call.Id, _ => Gate(), StringComparer.Ordinal);
        var barrierEntered = Gate(); var barrierReleased = Gate();
        var barrierSettled = false;
        var preflight = new ConcurrentQueue<(string Id, JsonData Arguments)>();
        var executeStarts = new ConcurrentQueue<(string Id, JsonData Arguments)>();
        var completionOrder = new ConcurrentQueue<string>();
        var committedResults = new ConcurrentQueue<ToolResultMessage>();
        var active = 0; var maxConcurrent = 0;
        var executor = new Executor(async (invocation, token) =>
        {
            Check(barrierSettled, "Execution crossed the unsettled assistant barrier.");
            executeStarts.Enqueue((invocation.Call.Id, invocation.Call.Arguments));
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maxConcurrent); }
            while (current > previous && Interlocked.CompareExchange(ref maxConcurrent, current, previous) != previous);
            started[invocation.Call.Id].SetResult();
            try
            {
                await releases[invocation.Call.Id].Task.WaitAsync(token);
                // Independent replay of full-capture.mjs's synthetic tool, derived from native input.
                return new ToolResult([new TextContent($"result:{invocation.Call.Id}")],
                    invocation.Call.Arguments, Terminate: true);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var hooks = new Hooks(before: (invocation, _) =>
        {
            Check(barrierSettled, "Preflight crossed the unsettled assistant barrier.");
            preflight.Enqueue((invocation.Call.Id, invocation.Call.Arguments));
            return ValueTask.FromResult(ToolPreflightDecision.Allow);
        });
        var sink = new Sink(async (observation, _) =>
        {
            if (observation is AssistantMessageEnded)
            {
                barrierEntered.SetResult();
                await barrierReleased.Task;
                barrierSettled = true;
            }
            if (observation is ToolExecutionEnded completion)
            {
                completionOrder.Enqueue(completion.Outcome.Invocation.Call.Id);
                ended[completion.Outcome.Invocation.Call.Id].SetResult();
            }
            if (observation is ToolResultMessageEnded committed) committedResults.Enqueue(committed.Message);
        });
        var scheduler = new ToolBatchScheduler(calls.Select(call => new ToolDefinition(call.Name, executor)), hooks);
        var run = scheduler.RunAsync(message, sink);
        await barrierEntered.Task;
        var probe = observed.GetProperty("controls").EnumerateArray().Single(control => control.GetProperty("kind").GetString() == "barrier_probe");
        Equal(probe.GetProperty("preflightCount").GetInt32(), preflight.Count);
        Equal(probe.GetProperty("executionCount").GetInt32(), executeStarts.Count);
        var barrierBlockedTools = preflight.Count == 0 && executeStarts.Count == 0;
        Equal(checks.GetProperty("barrierBlockedTools").GetBoolean(), barrierBlockedTools);
        Check(!run.IsCompleted, "Oracle barrier probe requires an unsettled native batch.");
        barrierReleased.SetResult();
        await Task.WhenAll(started.Values.Select(gate => gate.Task));
        Equal(checks.GetProperty("maxConcurrentTools").GetInt32(), maxConcurrent);
        Sequence(OracleIds(checks.GetProperty("preflightOrder")), preflight.Select(entry => entry.Id));
        foreach (var id in OracleIds(input.GetProperty("completionOrder")))
        {
            releases[id].SetResult();
            await ended[id].Task;
        }
        var batch = await run;
        var toolTrace = observed.GetProperty("toolTrace").EnumerateArray().ToArray();
        CompareInvocationTrace("preflight", preflight);
        CompareInvocationTrace("execute_start", executeStarts);
        var oracleEnds = observed.GetProperty("events").EnumerateArray()
            .Where(observation => observation.GetProperty("type").GetString() == "tool_execution_end").ToArray();
        Sequence(oracleEnds.Select(observation => observation.GetProperty("toolCallId").GetString()!), completionOrder);
        Sequence(OracleIds(checks.GetProperty("completionOrder")), completionOrder);
        var oracleResults = observed.GetProperty("finalResult").EnumerateArray()
            .Where(result => result.GetProperty("role").GetString() == "toolResult").ToArray();
        Sequence(OracleIds(checks.GetProperty("resultOrder")), committedResults.Select(result => result.ToolCallId));
        Equal(oracleResults.Length, batch.Messages.Length);
        var nativeCommitted = committedResults.ToArray();
        for (var index = 0; index < oracleResults.Length; index++)
        {
            CompareResult(oracleResults[index], batch.Messages[index]);
            Equal(batch.Messages[index], nativeCommitted[index]);
        }
        var unanimousTermination = oracleEnds.Length > 0 && oracleEnds.All(observation =>
            observation.GetProperty("result").GetProperty("terminate").GetBoolean());
        Equal(unanimousTermination, batch.Terminate);
        Equal(!unanimousTermination, batch.ShouldContinue);
        Check(!batch.IsCanceled, "Successful frozen scenario became canceled.");
        schedulerDifferentialEvidence = new
        {
            scope = "bounded-scheduler-projection", fixtureId = "awaited-parallel", sourceSha,
            inputSha256 = inputSha, expectedSha256 = expectedSha,
            barrierBlockedTools, preflightOrder = preflight.Select(entry => entry.Id).ToArray(),
            executeStartOrder = executeStarts.Select(entry => entry.Id).ToArray(),
            completionOrder = completionOrder.ToArray(), resultOrder = batch.Messages.Select(result => result.ToolCallId).ToArray(),
            maxConcurrentTools = maxConcurrent, unanimousTermination = batch.Terminate,
            continuationSuppressed = !batch.ShouldContinue, fullAgentLoopParity = false
        };

        void CompareInvocationTrace(string kind, IEnumerable<(string Id, JsonData Arguments)> nativeTrace)
        {
            var expected = toolTrace.Where(entry => entry.GetProperty("kind").GetString() == kind).ToArray();
            var actual = nativeTrace.ToArray();
            Sequence(expected.Select(entry => entry.GetProperty("id").GetString()!), actual.Select(entry => entry.Id));
            for (var index = 0; index < expected.Length; index++)
                EquivalentJson(expected[index].GetProperty("args"), actual[index].Arguments.Value);
        }
        static void CompareResult(JsonElement expected, ToolResultMessage actual)
        {
            Equal(expected.GetProperty("toolCallId").GetString(), actual.ToolCallId);
            Equal(expected.GetProperty("toolName").GetString(), actual.ToolName);
            Equal(expected.GetProperty("isError").GetBoolean(), actual.IsError);
            var contents = expected.GetProperty("content").EnumerateArray().ToArray();
            Equal(contents.Length, actual.Content.Length);
            for (var index = 0; index < contents.Length; index++)
            {
                Equal("text", contents[index].GetProperty("type").GetString());
                Equal(contents[index].GetProperty("text").GetString(), actual.Content[index].Text);
            }
            EquivalentJson(expected.GetProperty("details"), actual.Details.Value);
        }
    }

    private static JsonDocument ReadFrozenFixture(string name, string sha)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        Equal(sha, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return JsonDocument.Parse(bytes);
    }

    private static IEnumerable<string> OracleIds(JsonElement values) => values.EnumerateArray().Select(value => value.GetString()!);

    // Object key order alone is ignored. Arrays, presence/null, strings and numeric lexemes remain exact.
    private static void EquivalentJson(JsonElement expected, JsonElement actual)
    {
        Equal(expected.ValueKind, actual.ValueKind);
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var properties = expected.EnumerateObject().ToArray();
            Equal(properties.Length, actual.EnumerateObject().Count());
            foreach (var property in properties)
            {
                Check(actual.TryGetProperty(property.Name, out var value), $"Missing JSON property {property.Name}.");
                EquivalentJson(property.Value, value);
            }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            Equal(expected.GetArrayLength(), actual.GetArrayLength());
            for (var index = 0; index < expected.GetArrayLength(); index++) EquivalentJson(expected[index], actual[index]);
        }
        else if (expected.ValueKind == JsonValueKind.String) Equal(expected.GetString(), actual.GetString());
        else Equal(expected.GetRawText(), actual.GetRawText());
    }

    private static async Task AssistantBarrier()
    {
        var entered = Gate();
        var release = Gate();
        var before = 0;
        var executed = 0;
        var committed = false;
        var hooks = new Hooks(before: (_, _) =>
        {
            Check(committed, "Preflight must see the committed assistant.");
            before++;
            return ValueTask.FromResult(ToolPreflightDecision.Allow);
        });
        var sink = new Sink(async (observation, _) =>
        {
            if (observation is AssistantMessageEnded)
            {
                entered.SetResult();
                await release.Task;
                committed = true;
            }
        });
        var tool = new Executor((_, _) => { executed++; return ValueTask.FromResult(ToolResult.Success("A")); });
        var run = new ToolBatchScheduler([new("A", tool)], hooks).RunAsync(Message("A"), sink);
        await entered.Task;
        Equal(0, before);
        Equal(0, executed);
        Check(!run.IsCompleted, "Assistant barrier must hold batch completion.");
        release.SetResult();
        await run;
        Equal(1, before);
        Equal(1, executed);
    }

    private static async Task OrderedPreflight()
    {
        var aEntered = Gate(); var bEntered = Gate();
        var releaseA = Gate(); var releaseB = Gate();
        var preflight = new List<string>();
        var executions = new ConcurrentQueue<string>();
        var hooks = new Hooks(before: async (invocation, _) =>
        {
            preflight.Add(invocation.Call.Name);
            if (invocation.Call.Name == "A") { aEntered.SetResult(); await releaseA.Task; }
            if (invocation.Call.Name == "B") { bEntered.SetResult(); await releaseB.Task; }
            return ToolPreflightDecision.Allow;
        });
        var executor = new Executor((invocation, _) =>
        { executions.Enqueue(invocation.Call.Name); return ValueTask.FromResult(ToolResult.Success("ok")); });
        var sink = new Sink();
        var run = new ToolBatchScheduler(Definitions(executor), hooks).RunAsync(Message("A", "B", "C"), sink);
        await aEntered.Task;
        Sequence(["A"], preflight);
        Equal(0, executions.Count);
        releaseA.SetResult();
        await bEntered.Task;
        Sequence(["A", "B"], preflight);
        Equal(0, executions.Count);
        releaseB.SetResult();
        await run;
        Sequence(["A", "B", "C"], preflight);
        Equal(3, executions.Count);
    }

    private static async Task CompletionOrder()
    {
        var a = new ControlledTool(); var b = new ControlledTool(); var c = new ControlledTool();
        var cSinkEntered = Gate(); var releaseCSink = Gate();
        var aObserved = Gate(); var bObserved = Gate();
        var sink = new Sink(async (observation, _) =>
        {
            if (observation is ToolExecutionEnded ended)
            {
                if (ended.Outcome.Invocation.Call.Name == "C") { cSinkEntered.SetResult(); await releaseCSink.Task; }
                if (ended.Outcome.Invocation.Call.Name == "A") aObserved.SetResult();
                if (ended.Outcome.Invocation.Call.Name == "B") bObserved.SetResult();
            }
        });
        var scheduler = new ToolBatchScheduler([new("A", a), new("B", b), new("C", c)]);
        var run = scheduler.RunAsync(Message("A", "B", "C"), sink);
        await Task.WhenAll(a.Started.Task, b.Started.Task, c.Started.Task);
        c.Release.SetResult(ToolResult.Success("C"));
        await cSinkEntered.Task;
        a.Release.SetResult(ToolResult.Success("A"));
        await a.Returned.Task;
        Sequence(["C"], sink.Completions);
        Equal(0, sink.Transcript.Count);
        releaseCSink.SetResult();
        await aObserved.Task;
        b.Release.SetResult(ToolResult.Success("B"));
        await bObserved.Task;
        var batch = await run;
        Sequence(["C", "A", "B"], sink.Completions);
        Sequence(["A", "B", "C"], sink.Transcript);
        Sequence(["A", "B", "C"], batch.Outcomes.Select(outcome => outcome.Invocation.Call.Name));
        Sequence(["A", "B", "C"], batch.Messages.Select(message => message.ToolName));
        Equal(1, sink.MaximumConcurrentCalls);
        Check(batch.ShouldContinue, "Successful non-terminating batch must permit continuation.");
    }

    private static async Task BlockAndFailure()
    {
        var a = new ControlledTool(); var b = new ControlledTool(); var c = new ControlledTool();
        var after = new ConcurrentQueue<string>();
        var cObserved = Gate();
        var hooks = new Hooks(before: (invocation, _) => ValueTask.FromResult(invocation.Call.Name == "B"
            ? new ToolPreflightDecision(true, "B blocked") : ToolPreflightDecision.Allow),
            after: (invocation, result, _) => { after.Enqueue(invocation.Call.Name); return ValueTask.FromResult(result); });
        var sink = new Sink((observation, _) =>
        { if (observation is ToolExecutionEnded { Outcome.Invocation.Call.Name: "C" }) cObserved.SetResult(); return ValueTask.CompletedTask; });
        var run = new ToolBatchScheduler([new("A", a), new("B", b), new("C", c)], hooks)
            .RunAsync(Message("A", "B", "C"), sink);
        await Task.WhenAll(a.Started.Task, c.Started.Task);
        Check(!b.Started.Task.IsCompleted, "Blocked B executed.");
        Sequence(["B"], sink.Completions);
        c.Release.SetResult(ToolResult.Success("C"));
        await cObserved.Task;
        a.Release.SetException(new InvalidOperationException("A failed"));
        var result = await run;
        Sequence(["B", "C", "A"], sink.Completions);
        Sequence(["A", "B", "C"], sink.Transcript);
        Equal(ToolFailureKind.ExecutionError, result.Outcomes[0].Result.Failure!.Kind);
        Equal(ToolFailureKind.Blocked, result.Outcomes[1].Result.Failure!.Kind);
        Check(!result.Messages[2].IsError && result.ShouldContinue, "Unrelated success/continuation lost.");
        Sequence(["C", "A"], after);
    }

    private static async Task SequentialOverrides()
    {
        for (var sequentialIndex = -1; sequentialIndex < 3; sequentialIndex++)
        {
            var tools = new[] { new ControlledTool(), new ControlledTool(), new ControlledTool() };
            var names = new[] { "A", "B", "C" };
            var definitions = names.Select((name, index) => new ToolDefinition(name, tools[index],
                index == sequentialIndex ? ToolExecutionMode.Sequential : ToolExecutionMode.Parallel));
            var sink = new Sink();
            var run = new ToolBatchScheduler(definitions, executionMode: sequentialIndex < 0
                ? ToolExecutionMode.Sequential : ToolExecutionMode.Parallel).RunAsync(Message(names), sink);
            for (var index = 0; index < tools.Length; index++)
            {
                await tools[index].Started.Task;
                for (var later = index + 1; later < tools.Length; later++)
                    Check(!tools[later].Started.Task.IsCompleted, $"{names[later]} started before {names[index]} settled.");
                Equal(index, sink.Transcript.Count);
                tools[index].Release.SetResult(ToolResult.Success(names[index]));
            }
            await run;
            Sequence(names, sink.Completions);
            Sequence(names, sink.Transcript);
        }
    }

    private static async Task BlockedSequentialOverride()
    {
        var a = new ControlledTool(); var b = new ControlledTool(); var c = new ControlledTool();
        var before = new ConcurrentQueue<string>();
        var hooks = new Hooks(before: (invocation, _) =>
        {
            before.Enqueue(invocation.Call.Name);
            return ValueTask.FromResult(invocation.Call.Name == "B" ? new ToolPreflightDecision(true) : ToolPreflightDecision.Allow);
        });
        var run = new ToolBatchScheduler([new("A", a), new("B", b, ToolExecutionMode.Sequential), new("C", c)], hooks)
            .RunAsync(Message("A", "B", "C"), new Sink());
        await a.Started.Task;
        Sequence(["A"], before);
        Check(!c.Started.Task.IsCompleted, "Blocked sequential override was ignored.");
        a.Release.SetResult(ToolResult.Success("A"));
        await c.Started.Task;
        Check(!b.Started.Task.IsCompleted, "Blocked tool executed.");
        c.Release.SetResult(ToolResult.Success("C"));
        await run;
        Sequence(["A", "B", "C"], before);
    }

    private static async Task CancellationDuringPreflight()
    {
        using var fixture = new PreflightCancellationFixture();
        await fixture.RunOwnedAsync();
        fixture.AssertCanceledOrdering();
    }

    private static bool StopsCaseAdmission(Exception error) =>
        error is PreflightDeadlineException || PiMessagesAgentIntegrationTests.DeadlineExceeded;

    private sealed class PreflightDeadlineException() : TimeoutException(
        "Preflight fixture exceeded its owned deadline; original batch joined; later case admission stopped.") { }

    private static async Task PreflightDeadlineCleanup()
    {
        using var fixture = new PreflightCancellationFixture();
        var forcedDeadline = Gate();
        try
        {
            await fixture.RunOwnedAsync(forcedDeadline.Task, () => forcedDeadline.SetResult());
            throw new InvalidOperationException("Forced deadline passed.");
        }
        catch (PreflightDeadlineException error)
        {
            Check(StopsCaseAdmission(error), "Deadline allowed later case admission.");
            fixture.AssertCanceledOrdering();
        }
    }

    private static async Task PreflightEarlyHookFailureCleanup()
    {
        using var fixture = new PreflightCancellationFixture(failEarlyHook: true);
        await ThrowsAsync<InvalidOperationException>(() => fixture.RunOwnedAsync(),
            "Batch completed before B entered preflight.");
        Check(fixture.OriginalJoined && fixture.Release.Task.IsCompleted, "Early failure abandoned the original batch or gate.");
        Check(!fixture.Entered.Task.IsCompleted, "Early failure admitted B.");
        Equal(0, fixture.Executed);
        Sequence(["A"], fixture.Before);
        Sequence(["A"], fixture.Sink.Completions);
        Sequence(["A"], fixture.Sink.Transcript);
        Equal(ToolFailureKind.HookError, fixture.Batch!.Outcomes.Single().Result.Failure!.Kind);
    }

    private sealed class PreflightCancellationFixture(bool failEarlyHook = false) : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private CancellationToken bToken;
        public TaskCompletionSource Entered { get; } = Gate();
        public TaskCompletionSource Release { get; } = Gate();
        public List<string> Before { get; } = [];
        public Sink Sink { get; } = new();
        public int Executed { get; private set; }
        public bool OriginalJoined { get; private set; }
        public ToolBatchResult? Batch { get; private set; }

        public async Task RunOwnedAsync(Task? forcedDeadline = null, Action? afterEntry = null)
        {
            using var deadlineLifetime = new CancellationTokenSource();
            var deadline = forcedDeadline ?? Task.Delay(TimeSpan.FromSeconds(20), deadlineLifetime.Token);
            var hooks = new Hooks(before: async (invocation, token) =>
            {
                Before.Add(invocation.Call.Name);
                if (failEarlyHook)
                {
                    cancellation.Cancel();
                    throw new InvalidOperationException("injected early hook failure");
                }
                if (invocation.Call.Name == "B")
                {
                    bToken = token;
                    Entered.TrySetResult();
                    await Release.Task;
                }
                return ToolPreflightDecision.Allow;
            });
            var executor = new Executor((_, _) =>
            {
                Executed++;
                return ValueTask.FromResult(ToolResult.Success("unexpected"));
            });
            var original = new ToolBatchScheduler(Definitions(executor), hooks)
                .RunAsync(Message("A", "B", "C"), Sink, cancellation.Token);
            Exception? failure = null;
            try
            {
                await Task.WhenAny(Entered.Task, original, deadline);
                if (deadline.IsCompleted) throw new PreflightDeadlineException();
                if (!Entered.Task.IsCompleted)
                {
                    Batch = await original;
                    throw new InvalidOperationException("Batch completed before B entered preflight.");
                }
                Equal(0, Executed);
                Sequence(["A", "B"], Before);
                Check(bToken.CanBeCanceled && !bToken.IsCancellationRequested, "B did not receive a live lifetime token.");
                afterEntry?.Invoke();
                if (deadline.IsCompleted) throw new PreflightDeadlineException();
                cancellation.Cancel();
                Check(bToken.IsCancellationRequested, "Caller cancellation did not reach B's hook token.");
                Release.TrySetResult();
                if (await Task.WhenAny(original, deadline) == deadline) throw new PreflightDeadlineException();
                Batch = await original;
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                // Every exit owns cancellation, gate release and a direct join of the actual operation.
                try { cancellation.Cancel(); }
                finally
                {
                    Release.TrySetResult();
                    try { Batch = await original; }
                    catch when (failure is not null) { /* Preserve the primary failure after observing the original. */ }
                    finally { OriginalJoined = true; deadlineLifetime.Cancel(); }
                }
            }
        }

        public void AssertCanceledOrdering()
        {
            Check(OriginalJoined && Release.Task.IsCompleted, "Original batch or gate was abandoned.");
            Check(bToken.IsCancellationRequested, "Caller cancellation did not reach B's captured hook token.");
            Equal(0, Executed);
            Sequence(["A", "B"], Before); // C must never be prepared.
            Sequence(["B", "A"], Sink.Completions);
            Sequence(["A", "B"], Sink.Transcript);
            var batch = Batch ?? throw new InvalidOperationException("Original batch result was not retained.");
            Equal(2, batch.Outcomes.Length);
            Check(batch.IsCanceled && !batch.ShouldContinue, "Cancellation permitted automatic continuation.");
            Check(batch.Outcomes.All(outcome => outcome.Result.Failure?.Kind == ToolFailureKind.Canceled),
                "Prepared calls lack cancellation outcomes.");
        }

        public void Dispose() => cancellation.Dispose();
    }

    private static async Task CancellationBeforeBatch()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var tool = new ControlledTool();
        var sink = new Sink();
        var batch = await new ToolBatchScheduler([new("A", tool)]).RunAsync(Message("A"), sink, cancellation.Token);
        Check(!tool.Started.Task.IsCompleted, "Pre-canceled tool started.");
        Equal(1, sink.Events.Count);
        Equal(0, batch.Outcomes.Length);
        Check(batch.IsCanceled && !batch.ShouldContinue, "Pre-cancellation was lost.");
    }

    private static async Task CancellationDuringExecution()
    {
        using var cancellation = new CancellationTokenSource();
        var a = new ControlledTool(); var b = new ControlledTool(); var aObserved = Gate();
        var sink = new Sink((observation, _) =>
        { if (observation is ToolExecutionEnded { Outcome.Invocation.Call.Name: "A" }) aObserved.SetResult(); return ValueTask.CompletedTask; });
        var run = new ToolBatchScheduler([new("A", a), new("B", b)])
            .RunAsync(Message("A", "B"), sink, cancellation.Token);
        await Task.WhenAll(a.Started.Task, b.Started.Task);
        a.Release.SetResult(ToolResult.Success("completed"));
        await aObserved.Task;
        cancellation.Cancel();
        var batch = await run;
        Check(!batch.Outcomes[0].Result.IsError, "Cancellation overwrote a settled successful effect.");
        Equal(ToolFailureKind.Canceled, batch.Outcomes[1].Result.Failure!.Kind);
        Check(b.Returned.Task.IsCompleted, "Canceled executor has not settled.");
        Check(batch.IsCanceled && !batch.ShouldContinue, "Execution cancellation continued automatically.");
        Sequence(["A", "B"], sink.Transcript);
    }

    private static async Task SinkCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var tool = new ControlledTool();
        var sink = new Sink((_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; });
        await ThrowsAsync<OperationCanceledException>(() => new ToolBatchScheduler([new("A", tool)])
            .RunAsync(Message("A"), sink, cancellation.Token));
        Check(!tool.Started.Task.IsCompleted, "Throwing canceled sink admitted execution.");
    }

    private static async Task TerminationHints()
    {
        foreach (var allTerminate in new[] { true, false })
        {
            var after = new List<string>();
            var hooks = new Hooks(before: (invocation, _) => ValueTask.FromResult(invocation.Call.Name == "B"
                ? new ToolPreflightDecision(true, "stop", true) : ToolPreflightDecision.Allow),
                after: (invocation, result, _) =>
                {
                    after.Add(invocation.Call.Name);
                    return ValueTask.FromResult(invocation.Call.Name == "C" ? result with { Terminate = allTerminate } : result);
                });
            var executor = new Executor((invocation, _) => ValueTask.FromResult(ToolResult.Success(invocation.Call.Name, invocation.Call.Name == "A")));
            var result = await new ToolBatchScheduler(Definitions(executor), hooks).RunAsync(Message("A", "B", "C"), new Sink());
            Equal(allTerminate, result.Terminate);
            Equal(!allTerminate, result.ShouldContinue);
            Sequence(["A", "C"], after);
            Equal(3, result.Messages.Length);
        }
    }

    private static async Task PipelineFailures()
    {
        var before = new List<string>(); var after = new List<string>(); var executed = new List<string>();
        var hooks = new Hooks(before: (invocation, _) =>
        {
            before.Add(invocation.Call.Name);
            if (invocation.Call.Name == "A") throw new InvalidOperationException("preflight hook failed");
            return ValueTask.FromResult(ToolPreflightDecision.Allow);
        }, after: (invocation, result, _) =>
        {
            after.Add(invocation.Call.Name);
            if (invocation.Call.Name == "C") throw new InvalidOperationException("result hook failed");
            Check(result.IsError, "Thrown executor failure must reach result hook as an error.");
            return ValueTask.FromResult(ToolResult.Success("recovered"));
        });
        var executor = new Executor((invocation, _) =>
        { executed.Add(invocation.Call.Name); throw new InvalidOperationException("executor failed"); });
        var message = Message("A", "B", "missing", "C", "D") with
        { Content = [Call("A"), new ToolCallContent("call-B", "B", JsonData.Parse("[]")), Call("missing"), Call("C"), Call("D")] };
        var batch = await new ToolBatchScheduler([new("A", executor), new("B", executor), new("C", executor), new("D", executor)], hooks)
            .RunAsync(message, new Sink());
        // The loop admits arguments of any JSON kind: packages/agent/src/agent-loop.ts:716-727 prepareToolCall only looks the tool up
        // and runs that tool's validateToolArguments. B's plain executor declares no schema, so its non-object [] reaches the
        // before hook and the executor; a schema-bearing tool rejects it with "root: must be object" (Tools ToolValidationTests).
        Sequence(["A", "B", "C", "D"], before);
        Sequence(["B", "C", "D"], executed);
        Sequence(["B", "C", "D"], after);
        Equal(ToolFailureKind.HookError, batch.Outcomes[0].Result.Failure!.Kind);
        Equal("[]", batch.Outcomes[1].Invocation.Call.Arguments.ToString());
        Check(!batch.Messages[1].IsError, "The after hook override of B's thrown executor failure did not reach the transcript.");
        Equal(ToolFailureKind.UnknownTool, batch.Outcomes[2].Result.Failure!.Kind);
        Equal(ToolFailureKind.HookError, batch.Outcomes[3].Result.Failure!.Kind);
        Check(!batch.Messages[4].IsError, "Finalized result override did not reach transcript.");
    }

    private static async Task UnusableAssistants()
    {
        var tool = new ControlledTool(); var before = 0;
        var hooks = new Hooks(before: (_, _) => { before++; return ValueTask.FromResult(ToolPreflightDecision.Allow); });
        var scheduler = new ToolBatchScheduler([new("A", tool)], hooks);
        var truncated = await scheduler.RunAsync(Message("A", "A") with { StopReason = StopReason.Length }, new Sink());
        Equal(2, truncated.Outcomes.Length);
        Check(truncated.Outcomes.All(outcome => outcome.Result.Failure?.Kind == ToolFailureKind.Truncated), "Truncated calls lack synthetic errors.");
        foreach (var stopReason in new[] { StopReason.Error, StopReason.Aborted })
        {
            var result = await scheduler.RunAsync(Message("A") with { StopReason = stopReason }, new Sink());
            Equal(0, result.Outcomes.Length);
            Check(!result.ShouldContinue, "Failed assistant continued.");
        }
        Equal(0, before);
        Check(!tool.Started.Task.IsCompleted, "Unusable assistant executed a call.");
    }

    private static async Task FinalMessageBarrier()
    {
        var entered = Gate(); var release = Gate();
        var sink = new Sink(async (observation, _) =>
        { if (observation is ToolResultMessageEnded) { entered.SetResult(); await release.Task; } });
        var executor = new Executor((_, _) => ValueTask.FromResult(ToolResult.Success("done")));
        var run = new ToolBatchScheduler([new("A", executor)]).RunAsync(Message("A"), sink);
        await entered.Task;
        Check(!run.IsCompleted, "Batch completion skipped last awaited subscriber.");
        release.SetResult();
        await run;
    }

    private static async Task SinkFailureSettlesTools()
    {
        var aStarted = Gate(); var bStarted = Gate(); var aSettled = Gate(); var bSettled = Gate();
        var never = Gate(); var c = new ControlledTool();
        var a = new Executor(async (_, token) =>
        { aStarted.SetResult(); try { await never.Task.WaitAsync(token); return ToolResult.Success("unexpected"); } finally { aSettled.SetResult(); } });
        var b = new Executor(async (_, token) =>
        { bStarted.SetResult(); try { await never.Task.WaitAsync(token); return ToolResult.Success("unexpected"); } finally { bSettled.SetResult(); } });
        var sink = new Sink((observation, _) =>
        { if (observation is ToolExecutionEnded { Outcome.Invocation.Call.Name: "C" }) throw new InvalidOperationException("sink failed"); return ValueTask.CompletedTask; });
        var run = new ToolBatchScheduler([new("A", a), new("B", b), new("C", c)]).RunAsync(Message("A", "B", "C"), sink);
        await Task.WhenAll(aStarted.Task, bStarted.Task, c.Started.Task);
        c.Release.SetResult(ToolResult.Success("C"));
        await ThrowsAsync<InvalidOperationException>(async () => await run, "sink failed");
        Check(aSettled.Task.IsCompleted && bSettled.Task.IsCompleted, "Admitted effects still running after failure settlement.");
        Equal(0, sink.Transcript.Count);
    }

    private static async Task ContractValidation()
    {
        var executor = new Executor((_, _) => ValueTask.FromResult(ToolResult.Success("ok")));
        Throws<ArgumentException>(() => new ToolBatchScheduler([new("A", executor), new("A", executor)]));
        var scheduler = new ToolBatchScheduler([new("A", executor)]);
        foreach (var stopReason in new[] { StopReason.Pending, StopReason.Deferred })
            await ThrowsAsync<ArgumentException>(() => scheduler.RunAsync(Message("A") with { StopReason = stopReason }, new Sink()));
        var duplicateSink = new Sink();
        await ThrowsAsync<ArgumentException>(() => scheduler.RunAsync(Message("A", "A") with { Content = [Call("A"), Call("A")] }, duplicateSink));
        Equal(0, duplicateSink.Events.Count);
        // Owner decision 13: agent-loop.ts runs every toolCall block whatever its id and name. Nameless calls find no tool ("Tool  not found")
        // and id-less calls share the id "".
        var nameless = await scheduler.RunAsync(Message() with { Content = [new ToolCallContent("", "", JsonData.EmptyObject), new ToolCallContent("", "", JsonData.EmptyObject)] }, new Sink());
        Equal(2, nameless.Messages.Length);
        Check(nameless.Messages.All(message => message.ToolCallId == "" && message.ToolName == "" && message.IsError &&
            message.Content.Single().Text == "Tool  not found"), "Nameless calls did not get upstream's unknown-tool result.");
        var noTools = await scheduler.RunAsync(Message() with { StopReason = StopReason.Stop }, new Sink());
        Check(!noTools.Terminate && !noTools.ShouldContinue, "Empty batch has a continuation hint.");
        var sourceIndexed = await scheduler.RunAsync(Message("A") with { Content = [new TextContent("before"), Call("A")] }, new Sink());
        Equal(1, sourceIndexed.Outcomes[0].Invocation.SourceIndex);
        Equal("call-A", sourceIndexed.Messages[0].ToolCallId);
    }

    private static IEnumerable<ToolDefinition> Definitions(IToolExecutor executor) => new[] { "A", "B", "C" }.Select(name => new ToolDefinition(name, executor));
    private static ToolCallContent Call(string name) => new($"call-{name}", name, JsonData.EmptyObject);
    private static AssistantMessage Message(params string[] names) => new("fake", "fake", "model", 0,
        names.Select((name, index) => (AssistantContent)new ToolCallContent($"call-{index}-{name}", name, JsonData.EmptyObject)).ToImmutableArray(), TokenUsage.Zero, StopReason.ToolUse);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedArray = expected.ToArray(); var actualArray = actual.ToArray();
        Check(expectedArray.SequenceEqual(actualArray), $"Expected [{string.Join(",", expectedArray)}]; actual [{string.Join(",", actualArray)}].");
    }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string? message = null) where T : Exception
    { try { await action(); } catch (T error) { if (message is not null) Equal(message, error.Message); return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

    private sealed class Executor(Func<ToolInvocation, CancellationToken, ValueTask<ToolResult>> execute) : IToolExecutor
    { public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) => execute(invocation, cancellationToken); }

    private sealed class ControlledTool : IToolExecutor
    {
        public TaskCompletionSource Started { get; } = Gate();
        public TaskCompletionSource Returned { get; } = Gate();
        public TaskCompletionSource<ToolResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        { Started.SetResult(); try { return await Release.Task.WaitAsync(cancellationToken); } finally { Returned.SetResult(); } }
    }

    private sealed class TestChatTransport(IEnumerable<StreamEvent> source, Task? cleanupRelease = null,
        Task? terminalRelease = null) : IChatTransport
    {
        private readonly ImmutableArray<StreamEvent> frames = source.ToImmutableArray();
        public int RequestCount { get; private set; }
        public ChatRequest? LastRequest { get; private set; }
        public TaskCompletionSource CleanupEntered { get; } = Gate();
        public TaskCompletionSource CleanupFinished { get; } = Gate();

        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestCount++; LastRequest = request;
            try
            {
                foreach (var frame in frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (frame is StreamTerminalEvent && terminalRelease is not null)
                        await terminalRelease.WaitAsync(cancellationToken);
                    yield return frame;
                    await Task.Yield();
                }
            }
            finally
            {
                CleanupEntered.TrySetResult();
                if (cleanupRelease is not null) await cleanupRelease;
                CleanupFinished.TrySetResult();
            }
        }
    }

    private sealed class Hooks(
        Func<ToolInvocation, CancellationToken, ValueTask<ToolPreflightDecision>>? before = null,
        Func<ToolInvocation, ToolResult, CancellationToken, ValueTask<ToolResult>>? after = null) : IToolHooks
    {
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
            before is null ? ValueTask.FromResult(ToolPreflightDecision.Allow) : before(invocation, cancellationToken);
        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken cancellationToken) =>
            after is null ? ValueTask.FromResult(result) : after(invocation, result, cancellationToken);
    }

    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask>? emit = null) : IAgentEventSink
    {
        private int concurrentCalls;
        public int MaximumConcurrentCalls { get; private set; }
        public ConcurrentQueue<AgentEvent> Events { get; } = new();
        public ConcurrentQueue<string> Completions { get; } = new();
        public ConcurrentQueue<string> Transcript { get; } = new();
        public async ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref concurrentCalls);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, current);
            try
            {
                Events.Enqueue(observation);
                if (observation is ToolExecutionEnded ended) Completions.Enqueue(ended.Outcome.Invocation.Call.Name);
                if (observation is ToolResultMessageEnded endedMessage) Transcript.Enqueue(endedMessage.Message.ToolName);
                if (emit is not null) await emit(observation, cancellationToken);
            }
            finally { Interlocked.Decrement(ref concurrentCalls); }
        }
    }
}
