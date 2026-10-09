using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RpcSessionDispatcherTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly AgentOptions NativeProgress = new(ProgressDelivery: new(Mode: ToolProgressDeliveryMode.NativeAwaited));
    private static readonly ModelDescriptor Model = new("rpc-model", "openai-responses", "authored-provider");
    private static readonly JsonData ModelWire = JsonData.Parse("""{"id":"rpc-model","api":"openai-responses","provider":"authored-provider","name":"Authored RPC model","baseUrl":"https://offline.invalid","reasoning":false,"input":["text","image"],"contextWindow":131072,"maxTokens":8192,"cost":{"input":0.5,"output":1.0,"cacheRead":0,"cacheWrite":0},"opaque":{"huge":1e400,"nil":null}}""");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("rpc.dispatch-real-async-prompt-state-raw-messages-and-generated-only-events", PromptAndState);
        yield return ("rpc.dispatch-real-queues-modes-abort-retention-clear-and-late-continuation", QueuesAndAbort);
        yield return ("rpc.dispatch-durable-checkpoints-precede-end-events-tools-and-next-request", DurableEvents);
        yield return ("rpc.dispatch-errors-bounds-parse-recovery-backpressure-and-awaited-owned-cleanup", AdmissionAndCleanup);
        yield return ("rpc.dispatch-tool-progress-flush-backpressure-correlation-and-durable-exclusion", ProgressWireAndDurability);
        yield return ("rpc.dispatch-tool-progress-write-flush-and-composed-budget-faults-join-cleanup", ProgressOutputFaults);
        yield return ("rpc.dispatch-tool-progress-execution-failures-and-abort-settle-admitted-updates", ProgressFailureAndAbort);
        yield return ("rpc.shutdown-settlement-operation-only", () => ShutdownSettlementFailures(operation: true, cleanup: false));
        yield return ("rpc.shutdown-settlement-cleanup-only", () => ShutdownSettlementFailures(operation: false, cleanup: true));
        yield return ("rpc.shutdown-settlement-combined-causes-in-order", () => ShutdownSettlementFailures(operation: true, cleanup: true));
        yield return ("rpc.shutdown-settlement-repeated-shutdown-joins-one-original", RepeatedShutdownSettlement);
        yield return ("rpc.shutdown-settlement-late-operation-failure-before-cleanup-settles", LateShutdownFailure);
    }

    private static async Task ShutdownSettlementFailures(bool operation, bool cleanup)
    {
        var operationCause = new IOException("authored-original-operation");
        var cleanupCause = new IOException("authored-new-cleanup");
        var startupEntered = Gate(); var startupRelease = Gate();
        var output = new Capture { HoldDispose = true, DisposeFailure = cleanup ? cleanupCause : null };
        var fixture = await Fixture.Create(new Script([]), output: output, ownedOutput: true,
            startup: operation ? async _ =>
            {
                startupEntered.TrySetResult(); await startupRelease.Task; throw operationCause;
            } : null);
        Task? run = null; Task<RpcSessionDispatcher.ShutdownSettlement>? settlement = null;
        try
        {
            if (operation)
            {
                run = fixture.Dispatcher.RunAsync(new JsonlReader(new MemoryStream()));
                await startupEntered.Task.WaitAsync(Deadline);
                startupRelease.TrySetResult();
            }
            settlement = fixture.Dispatcher.SettleAsync().AsTask();
            await output.DisposeEntered.Task.WaitAsync(Deadline);
            Check(!settlement.IsCompleted && !fixture.Dispatcher.Completion.IsCompleted,
                "Settlement abandoned held original output disposal.");
            output.DisposeRelease.TrySetResult();
            var result = await settlement.WaitAsync(Deadline);
            Equal(cleanup ? 1 : 0, result.CleanupFailures.Length);
            if (cleanup) Check(ReferenceEquals(cleanupCause, result.CleanupFailures[0]), "Cleanup cause identity changed.");
            if (operation)
            {
                Check(result.OperationFailure is not null && ReferenceEquals(operationCause, result.OperationFailure.InnerException),
                    "Startup operation cause was lost or counted again as cleanup.");
                Equal(RpcDispatchFailure.SessionRunFailed, result.OperationFailure!.Failure);
            }
            else Check(result.OperationFailure is null, "Cleanup-only failure became an operation failure.");
            Check(result.CompletionFailure is not null, "Failing original disposal was reported as successful.");
            Equal(operation ? RpcDispatchFailure.SessionRunFailed : RpcDispatchFailure.CleanupFailed, result.CompletionFailure!.Failure);
            var originalFailure = await Throws<RpcDispatchException>(() => fixture.Dispatcher.DisposeAsync().AsTask());
            Check(ReferenceEquals(result.CompletionFailure, originalFailure), "Settlement replaced the original disposal exception.");
            Check(ReferenceEquals(result, await fixture.Dispatcher.SettleAsync()), "Repeated failing shutdown changed retained causes.");
            if (operation && cleanup)
            {
                var causes = (result.CompletionFailure!.InnerException as AggregateException)?.InnerExceptions;
                Check(causes is { Count: 2 } && ReferenceEquals(causes[0], result.OperationFailure) && ReferenceEquals(causes[1], cleanupCause),
                    "Combined settlement changed operation-first cleanup order or original causes.");
            }
            if (run is not null)
                Check(ReferenceEquals(result.CompletionFailure, await Throws<RpcDispatchException>(() => run)),
                    "Run did not directly join the same original disposal failure.");
            Check(fixture.Session.Snapshot.IsDisposed && output.Disposals == 1, "Settlement returned before owned cleanup.");
        }
        finally
        {
            startupRelease.TrySetResult(); output.DisposeRelease.TrySetResult();
            if (run is not null) try { await run; } catch (RpcDispatchException) { }
            if (settlement is not null) await settlement;
            await fixture.DisposeAsync();
        }
    }

    private static async Task RepeatedShutdownSettlement()
    {
        var output = new Capture { HoldDispose = true };
        var fixture = await Fixture.Create(new Script([]), output: output, ownedOutput: true);
        var original = fixture.Dispatcher.DisposeAsync().AsTask();
        var first = fixture.Dispatcher.SettleAsync().AsTask(); var second = fixture.Dispatcher.SettleAsync().AsTask();
        try
        {
            await output.DisposeEntered.Task.WaitAsync(Deadline);
            Check(ReferenceEquals(original, fixture.Dispatcher.DisposeAsync().AsTask()) &&
                !original.IsCompleted && !first.IsCompleted && !second.IsCompleted, "Repeated shutdown did not retain the held original.");
            output.DisposeRelease.TrySetResult(); await original;
            var one = await first; var two = await second;
            Check(ReferenceEquals(one, two) && ReferenceEquals(one, await fixture.Dispatcher.SettleAsync()), "Repeated settlement was recomputed.");
            Check(one.OperationFailure is null && one.CleanupFailures.IsEmpty && one.CompletionFailure is null && output.Disposals == 1,
                "Successful repeated shutdown fabricated failures or repeated disposal.");
        }
        finally { output.DisposeRelease.TrySetResult(); await original; await first; await second; await fixture.DisposeAsync(); }
    }

    private static async Task LateShutdownFailure()
    {
        var cause = new IOException("authored-late-output-failure");
        var entered = Gate(); var release = Gate();
        var output = new Capture { BeforeFlush = async (_, _) => { entered.TrySetResult(); await release.Task; throw cause; } };
        var fixture = await Fixture.Create(new Script([]), output: output);
        var command = fixture.Send("get_state", "late-output");
        Task<RpcSessionDispatcher.ShutdownSettlement>? settlement = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            settlement = fixture.Dispatcher.SettleAsync().AsTask();
            Check(!settlement.IsCompleted, "Shutdown abandoned the admitted late-failing command.");
            release.TrySetResult(); await Throws<RpcDispatchException>(() => command);
            var result = await settlement.WaitAsync(Deadline);
            Check(result.OperationFailure is not null && ReferenceEquals(cause, result.OperationFailure.InnerException), "Late original cause was discarded.");
            Equal(RpcDispatchFailure.OutputFailed, result.OperationFailure!.Failure);
            Check(result.CleanupFailures.IsEmpty && ReferenceEquals(result.OperationFailure, result.CompletionFailure),
                "Late output operation failure was counted as new cleanup.");
            Check(ReferenceEquals(result, await fixture.Dispatcher.SettleAsync()), "Late failure changed after repeated shutdown.");
        }
        finally
        {
            release.TrySetResult(); try { await command; } catch (RpcDispatchException) { }
            if (settlement is not null) await settlement;
            await fixture.DisposeAsync();
        }
    }

    private static async Task PromptAndState()
    {
        var transport = new Script([new(Message("first"), Pause: true), new(Message("second")), new(Message(error: true))]);
        await using var fixture = await Fixture.Create(transport);
        const string identity = "id\n\u2028\u2029\U0001F642";
        await fixture.Send("prompt", identity, "hello"); await transport.Steps[0].Entered.Task.WaitAsync(Deadline);
        Equal("started", fixture.Response(identity).Value.GetProperty("data").GetProperty("disposition").GetString());
        Check(fixture.Session.Snapshot.Agent.IsRunning && !fixture.Dispatcher.WaitForIdleAsync().IsCompleted, "Prompt response incorrectly meant completion.");
        await fixture.Send("get_state", "state"); var state = fixture.Response("state").Value.GetProperty("data");
        Check(state.GetProperty("isStreaming").GetBoolean(), "Concurrent state did not observe active work.");
        Equal(1, state.GetProperty("messageCount").GetInt32()); Equal(0, state.GetProperty("pendingMessageCount").GetInt32());
        Equal("1e400", state.GetProperty("model").GetProperty("opaque").GetProperty("huge").GetRawText());
        Equal("1.0", state.GetProperty("model").GetProperty("cost").GetProperty("output").GetRawText());
        Check(!state.TryGetProperty("sessionName", out _), "Absent session name became null.");
        await fixture.Send("get_last_assistant_text", "none");
        Check(!fixture.Response("none").Value.GetProperty("data").TryGetProperty("text", out _), "Pinned undefined assistant text became a JSON null.");
        transport.Steps[0].Release.TrySetResult(); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        Equal(1, fixture.Output.Records().Count(value => Type(value) == "agent_settled"));
        var updates = fixture.Output.Records().Where(value => Type(value) == "message_update").ToArray();
        Check(updates.Length > 0 && updates.All(value => !value.Value.TryGetProperty("message", out _) &&
            !value.Value.GetProperty("assistantMessageEvent").TryGetProperty("partial", out _)), "Cumulative streaming snapshots entered RPC output.");
        var firstEnd = fixture.Output.Records().Single(value => Type(value) == "agent_end").Value;
        Equal(2, firstEnd.GetProperty("messages").GetArrayLength()); Check(!firstEnd.GetProperty("willRetry").GetBoolean(), "Invented retry capability.");
        Equal(1, fixture.Output.Records().Count(value => IsResponse(value, identity))); // Authoritative acceptance stays singular.

        var prior = fixture.Session.Snapshot.Context.Messages;
        await fixture.Send("prompt", "next", "again"); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        Prefix(prior, transport.Requests()[1].Messages);
        Equal(2, fixture.Output.Records().Where(value => Type(value) == "agent_end").Last().Value.GetProperty("messages").GetArrayLength());
        await fixture.Send("get_messages", "messages"); var messages = fixture.Response("messages").Value.GetProperty("data").GetProperty("messages");
        Equal(4, messages.GetArrayLength());
        for (var index = 0; index < messages.GetArrayLength(); index++)
            Equal(fixture.Session.Snapshot.Context.Messages[index].WireBody.ToString(), messages[index].GetRawText());
        await fixture.Send("get_last_assistant_text", "text"); Equal("second", fixture.Response("text").Value.GetProperty("data").GetProperty("text").GetString());
        var entries = fixture.Session.Snapshot.Log.Entries;
        await fixture.Send("get_entries", "cursor", since: entries[1].Id);
        var data = fixture.Response("cursor").Value.GetProperty("data"); Equal(4, data.GetProperty("entries").GetArrayLength());
        Equal(fixture.Session.Snapshot.Context.LeafId, data.GetProperty("leafId").GetString());
        Equal(entries[2].WireBody.ToString(), data.GetProperty("entries")[0].GetRawText());
        await fixture.Send("get_entries", "bad-cursor", since: ""); Check(!fixture.Response("bad-cursor").Value.GetProperty("success").GetBoolean(), "Empty present cursor was ignored.");
        await fixture.Send("prompt", "provider-error", "fail"); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        Equal(1, fixture.Output.Records().Count(value => IsResponse(value, "provider-error")));
        Check(fixture.Output.Records().Any(value => Type(value) == "message_end" &&
            value.Value.GetProperty("message").GetProperty("role").GetString() == "assistant" &&
            value.Value.GetProperty("message").GetProperty("stopReason").GetString() == "error"), "Accepted provider failure did not settle through messages.");
        Equal(3, fixture.Output.Records().Count(value => Type(value) == "agent_settled"));
    }

    private static async Task QueuesAndAbort()
    {
        foreach (var mode in new[] { "one-at-a-time", "all" })
        {
            var transport = new Script(Enumerable.Range(0, 6).Select(index => new Step(Message("turn-" + index), Pause: index == 0)).ToArray());
            await using var fixture = await Fixture.Create(transport);
            await fixture.Send("set_steering_mode", "sm", mode: mode); await fixture.Send("set_follow_up_mode", "fm", mode: mode);
            Check(!fixture.Response("sm").Value.TryGetProperty("data", out _), "No-data mode response gained data:null.");
            await fixture.Send("prompt", "initial", "begin"); await transport.Steps[0].Entered.Task.WaitAsync(Deadline);
            await fixture.Send("prompt", "busy", "reject"); Check(!fixture.Response("busy").Value.GetProperty("success").GetBoolean(), "Busy prompt without behavior was accepted.");
            await fixture.Send("steer", "s1", "steer-one"); await fixture.Send("prompt", "s2", "steer-two", behavior: "steer");
            await fixture.Send("follow_up", "f1", "follow-one"); await fixture.Send("prompt", "f2", "follow-two", behavior: "followUp");
            foreach (var id in new[] { "s1", "s2", "f1", "f2" }) Equal("queued", fixture.Response(id).Value.GetProperty("data").GetProperty("disposition").GetString());
            var queued = fixture.Session.GetPendingInputQueueSnapshot(); Equal(2, queued.SteeringMessages.Length); Equal(2, queued.FollowUpMessages.Length);
            await fixture.Send("get_state", "queued-state"); Equal(4, fixture.Response("queued-state").Value.GetProperty("data").GetProperty("pendingMessageCount").GetInt32());
            transport.Steps[0].Release.TrySetResult(); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            var inputs = fixture.Session.Snapshot.Context.Messages.Where(message => message.Role == "user").Select(Text).ToArray();
            Equal("begin|steer-one|steer-two|follow-one|follow-two", string.Join("|", inputs));
            Equal(mode == "all" ? 3 : 5, transport.Requests().Length);
            if (mode == "all") Equal("steer-one|steer-two", string.Join("|", transport.Requests()[1].Messages.TakeLast(2).Select(Text)));
            Equal(0, fixture.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length);
        }
        var aborting = new Script([new(Message(), Pause: true, HoldCleanup: true)]);
        await using (var fixture = await Fixture.Create(aborting))
        {
            await fixture.Send("prompt", "run", "begin"); await aborting.Steps[0].Entered.Task.WaitAsync(Deadline);
            await fixture.Send("steer", "keep", "preserved");
            var abort = fixture.Send("abort", "abort"); await aborting.Steps[0].CleanupEntered.Task.WaitAsync(Deadline);
            Check(!abort.IsCompleted && !fixture.Output.Records().Any(value => IsResponse(value, "abort")), "Abort replied before provider cleanup.");
            aborting.Steps[0].CleanupRelease.TrySetResult(); await abort.WaitAsync(Deadline);
            Equal("preserved", Text(fixture.Session.GetPendingInputQueueSnapshot().SteeringMessages.Single()));
            Equal(1, aborting.Requests().Length); Equal(1, fixture.Output.Records().Count(value => Type(value) == "agent_settled"));
            await fixture.Send("clear_queue", "clear"); var cleared = fixture.Response("clear").Value.GetProperty("data");
            Equal("preserved", cleared.GetProperty("steering")[0].GetString()); Equal(0, cleared.GetProperty("followUp").GetArrayLength());
            Equal(0, fixture.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length);
        }
        var late = new Script([new(Message("first")), new(Message("late"))]);
        await using (var fixture = await Fixture.Create(late))
        {
            Task? submitted = null;
            fixture.Output.OnFlush = record =>
            {
                if (Type(record) == "agent_end" && submitted is null) submitted = fixture.Send("follow_up", "late-queue", "late-input");
            };
            await fixture.Send("prompt", "late-start", "begin"); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            await submitted!.WaitAsync(Deadline); Equal(2, late.Requests().Length);
            Equal(2, fixture.Output.Records().Count(value => Type(value) == "agent_end"));
            Equal(1, fixture.Output.Records().Count(value => Type(value) == "agent_settled"));
            Equal("late-input", Text(fixture.Session.Snapshot.Context.Messages[^2]));
        }
    }

    private static async Task DurableEvents()
    {
        var storage = new ObservedFactory(); var transport = new Script([new(Message(tool: true)), new(Message("tool-done"))]);
        var tool = new Executor(); await using var fixture = await Fixture.Create(transport, storage: storage, tools: [new("read", tool)]);
        var input = storage.Storage!.Arm();
        var prompt = fixture.Send("prompt", "durable", "tools"); await prompt.WaitAsync(Deadline); await input.Entered.Task.WaitAsync(Deadline);
        await fixture.Send("get_messages", "unacknowledged"); Equal(0, fixture.Response("unacknowledged").Value.GetProperty("data").GetProperty("messages").GetArrayLength());
        Check(!fixture.Output.Records().Any(value => Type(value) == "message_end"), "Unacknowledged input appeared as final output."); Equal(0, transport.Requests().Length);
        var assistant = storage.Storage.Arm(); input.Release.TrySetResult(); await assistant.Entered.Task.WaitAsync(Deadline);
        Equal(0, tool.Calls); Equal(1, transport.Requests().Length);
        Check(!fixture.Output.Records().Any(value => Type(value) == "message_end" && value.Value.GetProperty("message").GetProperty("role").GetString() == "assistant"), "Assistant end preceded durability.");
        var result = storage.Storage.Arm(); assistant.Release.TrySetResult(); await result.Entered.Task.WaitAsync(Deadline);
        Equal(1, tool.Calls); Equal(1, transport.Requests().Length);
        Check(!fixture.Output.Records().Any(value => Type(value) is "message_start" or "message_end" &&
            value.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult"), "Invented/undurable tool message escaped its canonical timestamp barrier.");
        result.Release.TrySetResult(); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        Equal(2, transport.Requests().Length); Equal("toolResult", transport.Requests()[1].Messages[^1].Role);
        var toolMessages = fixture.Output.Records().Where(value => Type(value) is "message_start" or "message_end" &&
            value.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult").ToArray();
        Equal(2, toolMessages.Length); Equal("message_start", Type(toolMessages[0])); Equal("message_end", Type(toolMessages[1]));
        var durable = fixture.Session.Snapshot.Context.Messages.Single(message => message.Role == "toolResult");
        foreach (var value in toolMessages) Equal(durable.WireBody.ToString(), value.Value.GetProperty("message").GetRawText());
        Equal("1e300", durable.WireBody.Value.GetProperty("details").GetProperty("huge").GetRawText());
        var execution = fixture.Output.Records().Single(value => Type(value) == "tool_execution_end").Value;
        Check(!execution.GetProperty("result").TryGetProperty("terminate", out _) && !execution.GetProperty("result").TryGetProperty("failure", out _), "Runtime tool fields leaked into the wire result.");
        Equal("1e300", execution.GetProperty("result").GetProperty("structuredContent").GetProperty("huge").GetRawText());
        Check(!durable.WireBody.Value.TryGetProperty("structuredContent", out _), "Model-facing tool message gained structured event-only content.");
        foreach (var structured in new JsonData?[] { null, JsonData.Null })
        {
            await using var presence = await Fixture.Create(new Script([new(Message(tool: true)), new(Message())]),
                tools: [new("read", new Executor { StructuredContent = structured })]);
            await presence.Send("prompt", "structured-presence", "tools"); await presence.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            var envelope = presence.Output.Records().Single(value => Type(value) == "tool_execution_end").Value.GetProperty("result");
            Equal(structured is not null, envelope.TryGetProperty("structuredContent", out var field));
            if (structured is not null) Equal(JsonValueKind.Null, field.ValueKind);
            Check(!presence.Session.Snapshot.Context.Messages.Single(message => message.Role == "toolResult").WireBody.Value.TryGetProperty("structuredContent", out _),
                "Structured presence changed the durable tool message contract.");
        }

        // Preserve the original direct-executor 1e400 case as strict result admission,
        // through the same actual durable/RPC path; model metadata remains an opaque separate boundary.
        await using (var nonfinite = await Fixture.Create(new Script([new(Message(tool: true)), new(Message())]),
            tools: [new("read", new Executor
            {
                Details = JsonData.Parse("{\"scale\":1.0,\"huge\":1e400,\"nil\":null}"),
                StructuredContent = JsonData.Parse("{\"huge\":1e400,\"fraction\":0.5,\"nil\":null}")
            })]))
        {
            await nonfinite.Send("prompt", "nonfinite-result", "tools"); await nonfinite.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            var rejected = nonfinite.Output.Records().Single(value => Type(value) == "tool_execution_end").Value;
            Check(rejected.GetProperty("isError").GetBoolean() && !rejected.ToString().Contains("1e400", StringComparison.Ordinal), "Direct nonfinite result bypassed strict admission or leaked rejected metadata.");
            var acknowledged = nonfinite.Session.Snapshot.Context.Messages.Single(value => value.Role == "toolResult").WireBody;
            Check(acknowledged.Value.GetProperty("isError").GetBoolean() && !acknowledged.ToString().Contains("huge", StringComparison.Ordinal), "Rejected raw result acquired canonical metadata.");
            Equal(new FileInfo(nonfinite.Session.Path).Length, nonfinite.Session.Snapshot.Log.CommittedByteLength);
        }

        var failedStorage = new ObservedFactory(); var unused = new Script([new(Message())]);
        var failureFixture = await Fixture.Create(unused, storage: failedStorage);
        var failed = failedStorage.Storage!.Arm(fail: true);
        try
        {
            await failureFixture.Send("prompt", "accepted-fault", "private-input"); await failed.Entered.Task.WaitAsync(Deadline); failed.Release.TrySetResult();
            Equal(RpcDispatchFailure.SessionRunFailed, (await Throws<RpcDispatchException>(() => failureFixture.Dispatcher.Completion)).Failure);
            Equal(1, failureFixture.Output.Records().Count(value => IsResponse(value, "accepted-fault")));
            Check(!failureFixture.Output.Records().Any(value => Type(value) == "message_end" || Type(value) == "agent_settled"), "Failed durable commit was reported as acknowledged settlement.");
            Equal(0, failureFixture.Session.Snapshot.Context.Messages.Length); Equal(0, unused.Requests().Length);
        }
        finally { failed.Release.TrySetResult(); await failureFixture.DisposeAsync(); }
    }

    private static async Task AdmissionAndCleanup()
    {
        var transport = new Script([new(Message())]); await using (var fixture = await Fixture.Create(transport))
        {
            await fixture.Dispatcher.SubmitAsync(JsonData.Parse("""{"type":"get_state"}"""));
            Check(!fixture.Output.Records().Single().Value.TryGetProperty("id", out _), "Missing command ID became null.");
            await fixture.Send("future_command", "unknown"); await fixture.Send("compact", "compact-without-host");
            Check(fixture.Response("unknown").Value.GetProperty("error").GetString()!.StartsWith("Unknown command", StringComparison.Ordinal), "Unknown command misclassified.");
            // compact is implemented, but this fixture installs neither an owning host nor a summary generator.
            var compact = fixture.Response("compact-without-host").Value;
            Equal("compact", compact.GetProperty("command").GetString()); Equal(false, compact.GetProperty("success").GetBoolean());
            Equal("Summaries require an owning native session host and explicit summary transport.", compact.GetProperty("error").GetString());
            Check(!compact.TryGetProperty("data", out _) && !fixture.Output.Records().Any(value => Type(value) is "compaction_start" or "compaction_end"),
                "Unbound compact fabricated a result or lifecycle before host admission.");
            // Pinned original rpc-mode.ts:552-555 acknowledges abort_retry even with no retry controller.
            var beforeAbort = fixture.Output.Records().Length;
            await fixture.Send("abort_retry", "idle-abort");
            var abort = fixture.Response("idle-abort").Value;
            Equal("response", abort.GetProperty("type").GetString()); Equal("abort_retry", abort.GetProperty("command").GetString());
            Equal("idle-abort", abort.GetProperty("id").GetString()); Equal(true, abort.GetProperty("success").GetBoolean());
            Check(abort.EnumerateObject().Count() == 4 && !abort.TryGetProperty("error", out _) && !abort.TryGetProperty("data", out _) &&
                fixture.Output.Records().Length == beforeAbort + 1 && !fixture.Session.IsRetrying && transport.Requests().Length == 0,
                "Idle abort_retry lost original success correlation or fabricated retry lifecycle/provider work.");
            await fixture.Dispatcher.SubmitAsync(JsonData.Parse("""{"id":null,"type":"get_state"}"""));
            // rpc-mode.ts echoes command.id as it came: a null id is written back as null and the command still runs.
            var nullId = fixture.Output.Records().Last().Value; Equal("get_state", nullId.GetProperty("command").GetString());
            Equal(JsonValueKind.Null, nullId.GetProperty("id").ValueKind); Equal(true, nullId.GetProperty("success").GetBoolean());
            var count = fixture.Output.Records().Length;
            await fixture.Dispatcher.SubmitAsync(JsonData.Parse("""{"id":"stale-dialog","type":"extension_ui_response","confirmed":true}""")); Equal(count, fixture.Output.Records().Length);
            using var permissive = JsonDocument.Parse("""{"id":"private-rejected","type":"get_state",}""", new JsonDocumentOptions { AllowTrailingCommas = true });
            await fixture.Dispatcher.SubmitAsync(JsonData.FromElement(permissive.RootElement));
            Check(!fixture.Output.Records().Last().ToString().Contains("private", StringComparison.Ordinal), "Permissive syntax rejection exposed untrusted identity.");
        }
        var rawInput = Encoding.UTF8.GetBytes("\n{broken}\n{\"id\":\"after-errors\",\"type\":\"get_state\"}\n{");
        var recovering = await Fixture.Create(new Script([]));
        await using (recovering)
        {
            await recovering.Dispatcher.RunAsync(new JsonlReader(new MemoryStream(rawInput))).WaitAsync(Deadline);
            Equal(3, recovering.Output.Records().Count(value => IsResponse(value, null) && value.Value.GetProperty("command").GetString() == "parse"));
            Check(recovering.Response("after-errors").Value.GetProperty("success").GetBoolean(), "Valid command after malformed input was lost.");
            Check(recovering.Session.Snapshot.IsDisposed, "EOF did not dispose its owned durable session.");
        }
        var slow = new Capture { HoldWrite = true };
        await using (var fixture = await Fixture.Create(new Script([]), output: slow, options: new(MaximumConcurrentCommands: 2)))
        {
            var first = fixture.Send("get_state", "one"); await slow.WriteEntered.Task.WaitAsync(Deadline);
            var second = fixture.Send("get_messages", "two");
            Equal(RpcDispatchFailure.ResourceLimit, (await Throws<RpcDispatchException>(() => fixture.Send("get_state", "excess"))).Failure);
            Check(!first.IsCompleted && !second.IsCompleted, "Output backpressure did not hold admitted command responses.");
            slow.WriteRelease.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline); Equal(1, slow.MaximumWrites);
        }
        var budget = new Capture();
        await using (var fixture = await Fixture.Create(new Script([]), output: budget, options: new(MaximumOutputBytes: 256)))
        {
            await fixture.Send("steer", "oversized-receipt", new string('x', 240));
            Check(!fixture.Response("oversized-receipt").Value.GetProperty("success").GetBoolean(), "Queue receipt budget was not checked before effects.");
            Equal(0, fixture.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length);
            var before = budget.Records().Length; await fixture.Send("get_entries", "entries-budget");
            Equal(before + 1, budget.Records().Length); Check(!fixture.Response("entries-budget").Value.GetProperty("success").GetBoolean(), "Cumulative entry bytes were not rejected before output.");
        }
        await using (var fixture = await Fixture.Create(new Script([]), options: new(MaximumOutputBytes: 256), priorUserText: new string('z', 512)))
        {
            await fixture.Send("get_messages", "messages-budget");
            Check(!fixture.Response("messages-budget").Value.GetProperty("success").GetBoolean(), "Oversized stored context was materialized as a wire response.");
            Equal(1, fixture.Output.Records().Length); Equal(1, fixture.Session.Snapshot.Context.Messages.Length);
        }
        var cleanup = new Script([new(Message(), Pause: true, HoldCleanup: true)]); var ownedOutput = new Capture { HoldDispose = true };
        var closing = await Fixture.Create(cleanup, output: ownedOutput, ownedOutput: true);
        try
        {
            await closing.Send("prompt", "close", "begin"); await cleanup.Steps[0].Entered.Task.WaitAsync(Deadline);
            var first = closing.Dispatcher.DisposeAsync().AsTask(); var second = closing.Dispatcher.DisposeAsync().AsTask();
            await cleanup.Steps[0].CleanupEntered.Task.WaitAsync(Deadline);
            Check(ReferenceEquals(first, second) && !first.IsCompleted, "Concurrent disposal did not share actual run cleanup.");
            cleanup.Steps[0].CleanupRelease.TrySetResult(); await ownedOutput.DisposeEntered.Task.WaitAsync(Deadline);
            Check(!first.IsCompleted && closing.Session.Snapshot.IsDisposed, "Output disposal skipped session cleanup or was not awaited.");
            ownedOutput.DisposeRelease.TrySetResult(); await first.WaitAsync(Deadline); Equal(1, ownedOutput.Disposals);
        }
        finally { cleanup.Steps[0].Release.TrySetResult(); cleanup.Steps[0].CleanupRelease.TrySetResult(); ownedOutput.DisposeRelease.TrySetResult(); await closing.DisposeAsync(); }
        var broken = new Capture { FailOnType = "agent_start" };
        var failing = await Fixture.Create(new Script([new(Message())]), output: broken, ownedOutput: true);
        try
        {
            await failing.Send("prompt", "accepted-output-fault", "begin");
            Equal(RpcDispatchFailure.OutputFailed, (await Throws<RpcDispatchException>(() => failing.Dispatcher.Completion)).Failure);
            Check(failing.Session.Snapshot.IsDisposed, "Callback output failure joined itself instead of closing owned state."); Equal(1, broken.Disposals);
            Equal(1, broken.Records().Count(value => IsResponse(value, "accepted-output-fault")));
        }
        finally { await failing.DisposeAsync(); }
    }

    private static async Task ProgressWireAndDurability()
    {
        var flushEntered = Gate(); var flushRelease = Gate(); var resumed = 0;
        const string originalMetadata = """{ "progressOnly":"excerpt","scale":1.0,"precise":9007199254740993,"nil":null }""";
        const string compactMetadata = """{"progressOnly":"excerpt","scale":1.0,"precise":9007199254740993,"nil":null}""";
        var raw = JsonData.Parse(originalMetadata); Equal(originalMetadata, raw.ToString());
        var partials = new[]
        {
            new ToolResult([new("partial excerpt")], JsonData.Parse("""{"scale":1.0}"""), true, true,
                new(ToolFailureKind.ExecutionError, "partial status")) { StructuredContent = raw },
            ToolResult.Success("second excerpt"),
            ToolResult.Success("third excerpt") with { StructuredContent = JsonData.Null }
        };
        var tool = ProgressTool(async (_, progress, token) =>
        {
            foreach (var partial in partials) { await progress(partial, token); resumed++; }
            return ToolResult.Success("final complete") with { StructuredContent = JsonData.Parse("{\"finalOnly\":true}") };
        });
        var output = new Capture { BeforeFlush = async (record, token) =>
        {
            if (Type(record) == "tool_execution_update" && record.Value.GetProperty("partialResult").GetProperty("content")[0].GetProperty("text").GetString() == "partial excerpt")
            { flushEntered.TrySetResult(); await flushRelease.Task.WaitAsync(token); }
        } };
        var transport = new Script([new(Message(tool: true)), new(Message("after tool"))]); var storage = new ObservedFactory();
        await using var fixture = await Fixture.Create(transport, output: output, storage: storage, tools: [tool], agentOptions: NativeProgress);
        var metadataProbe = new MetadataProgressProbe(raw, originalMetadata);
        using var metadataLease = fixture.Session.Subscribe(metadataProbe);
        Barrier? finalCommit = null;
        try
        {
            await fixture.Send("prompt", "progress", "tools"); await flushEntered.Task.WaitAsync(Deadline);
            finalCommit = storage.Storage!.Arm();
            Equal(0, resumed); Equal(2, fixture.Session.Snapshot.Context.Messages.Length); Equal(1, transport.Requests().Length);
            Check(!finalCommit.Entered.Task.IsCompleted && !fixture.Dispatcher.WaitForIdleAsync().IsCompleted,
                "Final durability or settlement overtook the admitted update's flush.");
            Check(!output.Records().Any(value => Type(value) is "tool_execution_update" or "tool_execution_end"),
                "Unflushed update or final result escaped the actual output barrier.");
            flushRelease.TrySetResult(); await finalCommit.Entered.Task.WaitAsync(Deadline); Equal(3, resumed);
            var updates = output.Records().Where(value => Type(value) == "tool_execution_update").ToArray(); Equal(3, updates.Length);
            for (var index = 0; index < updates.Length; index++)
            {
                var update = updates[index].Value;
                Equal("args|partialResult|toolCallId|toolName|type", string.Join("|", update.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal)));
                Equal("rpc-call", update.GetProperty("toolCallId").GetString()); Equal("read", update.GetProperty("toolName").GetString());
                Equal(((ToolCallContent)transport.Steps[0].Message.Content[0]).Arguments.ToString(), update.GetProperty("args").GetRawText());
                Equal(partials[index].Content[0].Text, update.GetProperty("partialResult").GetProperty("content")[0].GetProperty("text").GetString());
                var owned = update.GetProperty("partialResult");
                Check(!owned.TryGetProperty("failure", out _), "Native failure classification entered the source result.");
                Equal(partials[index].HasProperty("terminate"), owned.TryGetProperty("terminate", out var terminate));
                Equal(partials[index].HasProperty("isError"), owned.TryGetProperty("isError", out var error));
                if (partials[index].HasProperty("terminate")) Equal(partials[index].Terminate, terminate.GetBoolean());
                if (partials[index].HasProperty("isError")) Equal(partials[index].Property("isError")!.Value.GetBoolean(), error.GetBoolean());
            }
            var first = updates[0].Value.GetProperty("partialResult");
            var structured = first.GetProperty("structuredContent");
            // JSONL encoding removes insignificant whitespace, preserving the complete object and raw number tokens.
            Equal(compactMetadata, structured.GetRawText());
            Equal("progressOnly|scale|precise|nil", string.Join("|", structured.EnumerateObject().Select(value => value.Name)));
            Equal("excerpt", structured.GetProperty("progressOnly").GetString()); Equal("1.0", structured.GetProperty("scale").GetRawText());
            Equal("9007199254740993", structured.GetProperty("precise").GetRawText()); Equal(JsonValueKind.Null, structured.GetProperty("nil").ValueKind);
            Equal("1.0", first.GetProperty("details").GetProperty("scale").GetRawText());
            Equal(1, metadataProbe.Calls); Equal(originalMetadata, raw.ToString());
            Check(!updates[1].Value.GetProperty("partialResult").TryGetProperty("structuredContent", out _), "Absent partial metadata became explicit null.");
            Equal(JsonValueKind.Null, updates[2].Value.GetProperty("partialResult").GetProperty("structuredContent").ValueKind);
            Check(!output.Records().Any(IsToolMessage), "Final tool message bypassed its durable checkpoint.");
            Equal(2, fixture.Session.Snapshot.Context.Messages.Length); Equal(1, transport.Requests().Length);
            finalCommit.Release.TrySetResult(); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            var records = output.Records();
            var lastUpdate = Array.FindLastIndex(records, value => Type(value) == "tool_execution_update");
            var endIndex = Array.FindIndex(records, value => Type(value) == "tool_execution_end");
            var messageIndex = Array.FindIndex(records, IsToolMessage);
            Check(lastUpdate < endIndex && endIndex < messageIndex, "Progress, terminal execution and durable tool message order changed.");
            Equal("final complete", Text(fixture.Session.Snapshot.Context.Messages.Single(value => value.Role == "toolResult")));
            Check(records[endIndex].Value.GetProperty("result").GetProperty("structuredContent").GetProperty("finalOnly").GetBoolean(), "Final structured output was replaced by a partial value.");
            await fixture.Send("get_messages", "progress-messages"); await fixture.Send("get_entries", "progress-entries");
            foreach (var record in records.Where(value => Type(value) is "message_start" or "message_end" or "turn_end" or "agent_end")
                .Concat([fixture.Response("progress-messages"), fixture.Response("progress-entries")]))
                Check(!record.ToString().Contains("progressOnly", StringComparison.Ordinal) && !record.ToString().Contains("finalOnly", StringComparison.Ordinal) &&
                    !record.ToString().Contains("excerpt", StringComparison.Ordinal), "Partial output or structured metadata entered the model/durable message transcript.");
            foreach (var entry in fixture.Session.Snapshot.Log.Entries)
                Check(!entry.WireBody.ToString().Contains("excerpt", StringComparison.Ordinal) && !entry.WireBody.ToString().Contains("progressOnly", StringComparison.Ordinal), "Progress was appended to the session log.");
            Check(!transport.Requests()[1].Messages.Any(value => value.WireBody.ToString().Contains("excerpt", StringComparison.Ordinal) ||
                value.WireBody.Value.TryGetProperty("structuredContent", out _)), "The next actual provider request included progress.");
            Check(ReferenceEquals(raw, partials[0].StructuredContent), "Native progress metadata ownership changed."); Equal(originalMetadata, raw.ToString());
            Equal(1, output.MaximumWrites); Equal(1, output.Records().Count(value => IsResponse(value, "progress")));
        }
        finally { flushRelease.TrySetResult(); finalCommit?.Release.TrySetResult(); }
    }

    private static async Task ProgressOutputFaults()
    {
        foreach (var fault in new[] { "write", "flush", "result-budget", "event-budget" })
        {
            var cleanupEntered = Gate(); var cleanupRelease = Gate(); var publicationReturned = false;
            var partial = ToolResult.Success("partial") with
            { StructuredContent = JsonData.Parse(JsonSerializer.Serialize(new string('x', fault == "event-budget" ? 1900 : fault == "result-budget" ? 2200 : 32))) };
            var tool = ProgressTool(async (_, progress, token) =>
            {
                try { await progress(partial, token); publicationReturned = true; return ToolResult.Success("unreachable"); }
                finally { cleanupEntered.TrySetResult(); await cleanupRelease.Task; }
            });
            var output = new Capture { FailOnType = fault == "write" ? "tool_execution_update" : null,
                BeforeFlush = (record, _) => Type(record) == "tool_execution_update" && fault == "flush"
                    ? ValueTask.FromException(new IOException("private-progress-flush-failure")) : ValueTask.CompletedTask };
            var transport = new Script([new(Message(tool: true)), new(Message())]);
            var fixture = await Fixture.Create(transport, output: output, tools: [tool], ownedOutput: true,
                options: fault.EndsWith("budget", StringComparison.Ordinal) ? new(MaximumOutputBytes: 2048) : null, agentOptions: NativeProgress);
            try
            {
                await fixture.Send("prompt", "accepted-progress-fault", "tools"); await cleanupEntered.Task.WaitAsync(Deadline);
                Check(!publicationReturned && !fixture.Dispatcher.Completion.IsCompleted, "Progress fault escaped publication or skipped owned execution cleanup.");
                Check(!output.Records().Any(value => Type(value) is "tool_execution_update" or "tool_execution_end" or "agent_settled") &&
                    !fixture.Session.Snapshot.Context.Messages.Any(value => value.Role == "toolResult"), "Failed partial projection/output acquired a final acknowledgement.");
                cleanupRelease.TrySetResult(); var error = await Throws<RpcDispatchException>(() => fixture.Dispatcher.Completion);
                Equal(fault.EndsWith("budget", StringComparison.Ordinal) ? RpcDispatchFailure.ResourceLimit : RpcDispatchFailure.OutputFailed, error.Failure);
                Check(!error.Message.Contains("private", StringComparison.Ordinal), "Progress output failure leaked implementation diagnostics.");
                Check(fixture.Session.Snapshot.IsDisposed, "Failed progress did not settle the owned coordinator.");
                Equal(1, output.Disposals); Equal(1, transport.Requests().Length);
                Equal(1, output.Records().Count(value => IsResponse(value, "accepted-progress-fault")));
                Check(!output.Records().Any(value => Type(value) is "tool_execution_end" or "agent_settled"), "Fatal progress failure invented normal settlement.");
            }
            finally { cleanupRelease.TrySetResult(); await fixture.DisposeAsync(); }
        }
    }

    private static async Task ProgressFailureAndAbort()
    {
        foreach (var invalidMetadata in new[] { false, true })
        {
            ToolProgressCallback? retained = null;
            var tool = ProgressTool(async (_, progress, token) =>
            {
                retained = progress; await progress(ToolResult.Success("accepted excerpt"), token);
                if (invalidMetadata)
                {
                    using var parsed = JsonDocument.Parse("{\"privatePayload\":1,}", new JsonDocumentOptions { AllowTrailingCommas = true });
                    await progress(ToolResult.Success("rejected excerpt") with { StructuredContent = JsonData.FromElement(parsed.RootElement) }, token);
                    return ToolResult.Success("unreachable");
                }
                throw new IOException("private-tool-failure");
            });
            await using var fixture = await Fixture.Create(new Script([new(Message(tool: true)), new(Message("after failed tool"))]), tools: [tool], agentOptions: NativeProgress);
            await fixture.Send("prompt", "tool-failure", "tools"); await fixture.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            Equal(1, fixture.Output.Records().Count(value => Type(value) == "tool_execution_update"));
            var end = fixture.Output.Records().Single(value => Type(value) == "tool_execution_end").Value;
            Check(end.GetProperty("isError").GetBoolean() && !end.ToString().Contains("private", StringComparison.Ordinal), "Tool progress failure did not become the bounded execution error.");
            var durable = fixture.Session.Snapshot.Context.Messages.Single(value => value.Role == "toolResult");
            Check(durable.WireBody.Value.GetProperty("isError").GetBoolean() && !Text(durable).Contains("excerpt", StringComparison.Ordinal), "A partial became the finalized error message.");
            Equal(1, fixture.Output.Records().Count(value => Type(value) == "agent_settled")); Equal(1, fixture.Output.Records().Count(value => IsResponse(value, "tool-failure")));
            var count = fixture.Output.Records().Length;
            await Throws<InvalidOperationException>(() => retained!(ToolResult.Success("late excerpt"), default).AsTask()); Equal(count, fixture.Output.Records().Length);
        }

        var flushEntered = Gate(); var flushRelease = Gate(); var canceled = Gate(); var cleanupEntered = Gate(); var cleanupRelease = Gate();
        var abortTool = ProgressTool(async (_, progress, token) =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            try { await progress(ToolResult.Success("abort excerpt"), token); token.ThrowIfCancellationRequested(); return ToolResult.Success("unreachable"); }
            finally { cleanupEntered.TrySetResult(); await cleanupRelease.Task; }
        });
        var slow = new Capture { BeforeFlush = async (record, token) =>
        { if (Type(record) == "tool_execution_update") { flushEntered.TrySetResult(); await flushRelease.Task.WaitAsync(token); } } };
        var transport = new Script([new(Message(tool: true)), new(Message("unexpected continuation"))]);
        await using var aborting = await Fixture.Create(transport, output: slow, tools: [abortTool], agentOptions: NativeProgress);
        Task? abort = null;
        try
        {
            await aborting.Send("prompt", "progress-abort-run", "tools"); await flushEntered.Task.WaitAsync(Deadline);
            abort = aborting.Send("abort", "progress-abort"); await canceled.Task.WaitAsync(Deadline);
            Check(!abort.IsCompleted && !cleanupEntered.Task.IsCompleted && !slow.Records().Any(value => IsResponse(value, "progress-abort") || Type(value) == "agent_settled"),
                "Abort acknowledged before the admitted progress flush settled.");
            flushRelease.TrySetResult(); await cleanupEntered.Task.WaitAsync(Deadline);
            Check(!abort.IsCompleted, "Abort did not await actual tool cleanup."); cleanupRelease.TrySetResult();
            await abort.WaitAsync(Deadline); await aborting.Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
            Equal(1, slow.Records().Count(value => Type(value) == "tool_execution_update")); Equal(1, slow.Records().Count(value => IsResponse(value, "progress-abort")));
            Equal(1, slow.Records().Count(value => Type(value) == "agent_settled")); Equal(1, transport.Requests().Length);
            Check(aborting.Session.Snapshot.Context.Messages.All(value => !value.WireBody.ToString().Contains("abort excerpt", StringComparison.Ordinal)), "Abort persisted a progress value.");
        }
        finally { flushRelease.TrySetResult(); cleanupRelease.TrySetResult(); if (abort is not null) try { await abort.WaitAsync(Deadline); } catch (RpcDispatchException) { } }
    }

    private static bool IsToolMessage(JsonData record) => Type(record) is "message_start" or "message_end" &&
        record.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult";
    private static ToolDefinition ProgressTool(Func<PreparedToolAction, ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> execute) =>
        new("read", new ToolInvoker([new ProgressAdapter(execute)], new AllowPolicy()));

    private static AssistantMessage Message(string text = "done", bool tool = false, bool error = false) => new(Model.Api, Model.Provider, Model.Id, 456,
        tool ? [new ToolCallContent("rpc-call", "read", JsonData.Parse("""{"path":"/authored","scale":1.0}"""))] : [new TextContent(text)],
        TokenUsage.Zero, error ? StopReason.Error : tool ? StopReason.ToolUse : StopReason.Stop,
        error ? JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"authored provider error\"")) : JsonFields.Empty.Set("opaque", JsonData.Parse("""{"fraction":0.5,"huge":1e400,"nil":null}""")));
    private static string Type(JsonData record) => record.Value.GetProperty("type").GetString()!;
    private static bool IsResponse(JsonData record, string? id) => Type(record) == "response" &&
        (id is null ? !record.Value.TryGetProperty("id", out _) : record.Value.TryGetProperty("id", out var value) && value.GetString() == id);
    private static string Text(TranscriptEntry message)
    {
        var content = message.WireBody.Value.GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray()
            .Where(value => value.GetProperty("type").GetString() == "text").Select(value => value.GetProperty("text").GetString()));
    }
    private static void Prefix(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    { Check(actual.Length >= expected.Length, "Prior canonical history was removed."); for (var i = 0; i < expected.Length; i++) Equal(expected[i].WireBody.ToString(), actual[i].WireBody.ToString()); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Deadline); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory; private readonly Script _transport; private readonly ObservedFactory? _storage;
        public PersistentAgentSession Session { get; } public RpcSessionDispatcher Dispatcher { get; } public Capture Output { get; }
        private Fixture(string directory, Script transport, PersistentAgentSession session, RpcSessionDispatcher dispatcher, Capture output, ObservedFactory? storage)
        { _directory = directory; _transport = transport; Session = session; Dispatcher = dispatcher; Output = output; _storage = storage; }
        public static async Task<Fixture> Create(Script transport, Capture? output = null, ObservedFactory? storage = null,
            ImmutableArray<ToolDefinition> tools = default, RpcDispatchOptions? options = null, bool ownedOutput = false, string? priorUserText = null,
            AgentOptions? agentOptions = null, Func<CancellationToken, ValueTask>? startup = null)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiSharp-rpc-dispatch-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory); var path = System.IO.Path.Combine(directory, "session.jsonl"); var ids = 0;
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "rpc-header",
                timestamp = "2026-10-01T00:00:00.000Z", cwd = directory }));
            var configuration = new AgentConfiguration(Model, transport, tools.IsDefault ? [] : tools);
            PersistentAgentSession session;
            if (priorUserText is null)
                session = await PersistentAgentSession.CreateAsync(path, header, configuration, () => 123,
                    () => "rpc-entry-" + Interlocked.Increment(ref ids), new(AgentOptions: agentOptions,
                        SessionLogStoreOptions: storage is null ? null : new(StorageFactory: storage)));
            else
            {
                await using (var seed = await SessionLogStore.CreateNewAsync(path, header))
                    await seed.AppendAsync([new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "message", id = "prior", parentId = (string?)null,
                        timestamp = "2026-10-01T00:00:00.000Z", message = new { role = "user", content = priorUserText, timestamp = 123 } }))]);
                session = await PersistentAgentSession.OpenAsync(path, configuration, () => 123, () => "rpc-entry-" + Interlocked.Increment(ref ids), new(AgentOptions: agentOptions));
            }
            output ??= new Capture();
            var dispatcher = new RpcSessionDispatcher(session, new JsonlWriter(output, ownership: ownedOutput ? JsonlStreamOwnership.Owned : JsonlStreamOwnership.Borrowed),
                () => 123, [new(Model, ModelWire)], options, sessionStartup: startup);
            return new(directory, transport, session, dispatcher, output, storage);
        }
        public Task Send(string type, string? id, string? message = null, string? behavior = null, string? mode = null, string? since = null)
        {
            var body = new Dictionary<string, object?> { ["type"] = type };
            if (id is not null) body["id"] = id; if (message is not null) body["message"] = message;
            if (behavior is not null) body["streamingBehavior"] = behavior; if (mode is not null) body["mode"] = mode; if (since is not null) body["since"] = since;
            return Dispatcher.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(body)));
        }
        public JsonData Response(string id) => Output.Records().Single(value => IsResponse(value, id));
        public async ValueTask DisposeAsync()
        {
            foreach (var step in _transport.Steps) { step.Release.TrySetResult(); step.CleanupRelease.TrySetResult(); }
            _storage?.Storage?.ReleaseBarriers();
            Output.WriteRelease.TrySetResult(); Output.DisposeRelease.TrySetResult();
            try { await Dispatcher.DisposeAsync().AsTask().WaitAsync(Deadline); } catch (RpcDispatchException) { }
            await Session.DisposeAsync();
            var target = System.IO.Path.GetFullPath(_directory); var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            if (System.IO.Path.GetDirectoryName(target) != parent || !System.IO.Path.GetFileName(target).StartsWith("PiSharp-rpc-dispatch-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside owned RPC test directory.");
            System.IO.Directory.Delete(target, recursive: true);
        }
    }
    private sealed record Step(AssistantMessage Message, bool Pause = false, bool HoldCleanup = false)
    { public TaskCompletionSource Entered { get; } = Gate(); public TaskCompletionSource Release { get; } = Gate(); public TaskCompletionSource CleanupEntered { get; } = Gate(); public TaskCompletionSource CleanupRelease { get; } = Gate(); }
    private sealed class Script(Step[] steps) : IChatTransport
    {
        private readonly object _gate = new(); private readonly List<ChatRequest> _requests = [];
        public Step[] Steps { get; } = steps;
        public ChatRequest[] Requests() { lock (_gate) return _requests.ToArray(); }
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Step step; lock (_gate) { step = Steps[_requests.Count]; _requests.Add(request); }
            try
            {
                yield return new StreamStarted(step.Message with { Content = [], StopReason = StopReason.Pending }); step.Entered.TrySetResult();
                if (step.Pause) await step.Release.Task.WaitAsync(token);
                for (var index = 0; index < step.Message.Content.Length; index++)
                    if (step.Message.Content[index] is ToolCallContent call)
                    { yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallDelta(index, call.Arguments.ToString()); yield return new ToolCallEnded(index, call); }
                    else if (step.Message.Content[index] is TextContent text)
                    { yield return new TextStarted(index, new("")); yield return new TextDelta(index, text.Text); yield return new TextEnded(index, text.Text); }
                if (step.Message.StopReason == StopReason.Error) yield return new StreamError(StopReason.Error, step.Message);
                else yield return new StreamDone(step.Message.StopReason, step.Message);
            }
            finally { step.CleanupEntered.TrySetResult(); if (step.HoldCleanup) await step.CleanupRelease.Task; }
        }
    }
    private sealed class Executor : IToolExecutor
    {
        public int Calls;
        public JsonData? StructuredContent { get; init; } = JsonData.Parse("""{"huge":1e300,"fraction":0.5,"nil":null}""");
        public JsonData Details { get; init; } = JsonData.Parse("""{"scale":1.0,"huge":1e300,"nil":null}""");
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult(new ToolResult([new("result")], Details)
            { StructuredContent = StructuredContent }); }
    }
    private sealed class MetadataProgressProbe(JsonData expected, string original) : IAgentEventSink
    {
        public int Calls;
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        {
            if (observation is ToolExecutionUpdated { PartialResult.StructuredContent: { } structured } && structured.Value.ValueKind == JsonValueKind.Object)
            {
                Calls++; Check(ReferenceEquals(expected, structured), "Native normalized progress replaced owned structured metadata.");
                Equal(original, structured.ToString());
            }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class ProgressAdapter(Func<PreparedToolAction, ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> execute) : IPreparedToolAdapter
    {
        public string Name => "read";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(
            new PreparedToolAction("read", "read", PreparedToolActionKind.Path, "/authored", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => ExecuteAsync(action, static (_, _) => ValueTask.CompletedTask, token);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback progress, CancellationToken token) => execute(action, progress, token);
    }
    private sealed class AllowPolicy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(true));
    }
    private sealed class Capture : Stream
    {
        private readonly object _gate = new(); private readonly List<JsonData> _records = []; private JsonData? _written; private int _writes;
        public bool HoldWrite { get; init; } public bool HoldDispose { get; init; } public string? FailOnType { get; init; }
        public Exception? DisposeFailure { get; init; }
        public Action<JsonData>? OnFlush { get; set; }
        public Func<JsonData, CancellationToken, ValueTask>? BeforeFlush { get; init; }
        public int MaximumWrites; public int Disposals;
        public TaskCompletionSource WriteEntered { get; } = Gate(); public TaskCompletionSource WriteRelease { get; } = Gate();
        public TaskCompletionSource DisposeEntered { get; } = Gate(); public TaskCompletionSource DisposeRelease { get; } = Gate();
        public JsonData[] Records() { lock (_gate) return _records.ToArray(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumWrites = Math.Max(MaximumWrites, Interlocked.Increment(ref _writes));
            try
            {
                WriteEntered.TrySetResult(); if (HoldWrite) await WriteRelease.Task.WaitAsync(cancellationToken);
                var text = Encoding.UTF8.GetString(buffer.Span); Check(text.EndsWith('\n') && text.Count(character => character == '\n') == 1, "Protocol bytes interleaved or lacked strict LF framing.");
                var record = JsonData.Parse(text);
                if (Type(record) == FailOnType) throw new IOException("private-output-failure");
                _written = record;
            }
            finally { Interlocked.Decrement(ref _writes); }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); var record = _written!; _written = null;
            if (BeforeFlush is { } before) await before(record, cancellationToken);
            lock (_gate) _records.Add(record); OnFlush?.Invoke(record);
        }
        public override async ValueTask DisposeAsync()
        { Disposals++; DisposeEntered.TrySetResult(); if (HoldDispose) await DisposeRelease.Task; if (DisposeFailure is { } failure) throw failure; }
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class Barrier(bool fail)
    {
        public TaskCompletionSource Entered { get; } = Gate(); public TaskCompletionSource Release { get; } = Gate();
        public async ValueTask WaitAsync() { Entered.TrySetResult(); await Release.Task; if (fail) throw new IOException("private-storage-failure"); }
    }
    private sealed class ObservedFactory : ISessionLogStorageFactory
    {
        public ObservedStorage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) =>
            Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token));
    }
    private sealed class ObservedStorage(ISessionLogStorage inner) : ISessionLogStorage
    {
        private readonly object _gate = new(); private Barrier? _barrier; private readonly List<Barrier> _barriers = [];
        public Barrier Arm(bool fail = false) { lock (_gate) { _barrier = new(fail); _barriers.Add(_barrier); return _barrier; } }
        public void ReleaseBarriers() { lock (_gate) foreach (var barrier in _barriers) barrier.Release.TrySetResult(); }
        public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long expected) => inner.PositionForAppend(expected); public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
        public async ValueTask BeforeCheckpointAsync()
        { Barrier? barrier; lock (_gate) { barrier = _barrier; _barrier = null; } await inner.BeforeCheckpointAsync(); if (barrier is not null) await barrier.WaitAsync(); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
