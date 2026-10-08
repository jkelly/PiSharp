using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

internal static class SourceProgressDifferentialTests
{
    public static object? DifferentialEvidence { get; private set; }
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("whole Agent progress: complete parallel listener/barrier/order observations", () => CapturedCase(0)),
        ("whole Agent progress: complete sequential listener/late-callback observations", () => CapturedCase(1)),
        ("whole Agent progress: actual abort admits third update then joins listener settlement", () => CapturedCase(2))
    ];

    private static async Task CapturedCase(int caseIndex)
    {
        using var input = Frozen("tools/PiReferenceRunner/agent-progress-inputs.json", "d1894b264cd5aa070f5774574e6199f253c6fa2f756de34f725ac8913a55551e");
        using var golden = Frozen("fixtures/pi-v0.99.1/agent-progress/core.expected.json", "9646c803429bd3a56b1a1e8fc3f07c01060052f9f32e6b4021265f7249dc8d3e");
        using var oracleLock = Frozen("fixtures/pi-v0.99.1/agent-progress/oracle.lock.json", "9d1c348cb9b25901cab362bd5d832d40c433ed6e1f1a5f611f6a9a1aa3b33d53");
        var fixture = input.RootElement; var scenario = fixture.GetProperty("cases")[caseIndex];
        var expected = golden.RootElement.GetProperty("observations").GetProperty("cases")[caseIndex];
        Equal(scenario.GetProperty("caseId").GetString(), expected.GetProperty("caseId").GetString());
        var specs = scenario.GetProperty("tools").EnumerateArray().ToArray();
        var mode = scenario.GetProperty("mode").GetString(); var aborting = mode == "abort-blocked-update";
        var blockedId = scenario.TryGetProperty("blockedToolId", out var blocked) ? blocked.GetString() : null;
        var clock = fixture.GetProperty("clock").GetProperty("unixMilliseconds").GetInt64();
        var modelJson = fixture.GetProperty("model");
        var model = new ModelDescriptor(modelJson.GetProperty("id").GetString()!, modelJson.GetProperty("api").GetString()!, modelJson.GetProperty("provider").GetString()!);
        var assistant = PiWireJson.ReadMessage(JsonSerializer.SerializeToElement(new
        {
            role = "assistant", api = model.Api, provider = model.Provider, model = model.Id,
            timestamp = clock, usage = fixture.GetProperty("assistantMessage").GetProperty("usage"), stopReason = "toolUse",
            content = specs.Select(spec => new { type = "toolCall", id = Id(spec), name = Name(spec), arguments = spec.GetProperty("call").GetProperty("arguments") })
        }));
        var started = specs.ToDictionary(Id, _ => Gate()); var emit = specs.ToDictionary(Id, _ => Gate());
        var ends = specs.ToDictionary(Id, _ => Gate()); var second = specs.ToDictionary(Id, _ => Gate());
        var secondCallbackReturned = specs.ToDictionary(Id, _ => Gate());
        var callbacks = new Dictionary<string, ToolProgressCallback>();
        var counts = new Dictionary<string, int>(); var metadata = new Dictionary<AgentEvent, (int Index, int Ordinal)>(ReferenceEqualityComparer.Instance);
        var events = new List<(AgentEvent Event, string Type, bool Aborted)>();
        var listenerTrace = new List<object>(); var controls = new List<object>(); var observations = new List<object>();
        var toolTrace = new List<object>(); var afterIds = new List<string>();
        var firstEntered = Gate(); var firstRelease = Gate(); var executeLeaving = Gate(); var abortObserved = Gate();
        var endEntered = Gate(); var endRelease = Gate();
        var gate = new object(); var active = 0; var maxActive = 0; var callbacksInvoked = 0;
        var promptResolved = false; var idleResolved = false;
        var tools = specs.Select(spec => new ToolDefinition(Name(spec), new Executor(async (invocation, progress, token) =>
        {
            var id = Id(spec); lock (gate)
            {
                callbacks.Add(id, progress);
                toolTrace.Add(new { kind = "execute_enter", id, args = invocation.Call.Arguments.Value, signalAborted = token.IsCancellationRequested });
            }
            using var registration = aborting ? token.Register(() =>
            {
                lock (gate) toolTrace.Add(new { kind = "tool_signal_abort", id, signalAborted = token.IsCancellationRequested });
                abortObserved.TrySetResult();
            }) : default;
            started[id].TrySetResult(); await emit[id].Task;
            var returnedCount = 0;
            void Deliver(JsonElement raw)
            {
                lock (gate) { callbacksInvoked++; toolTrace.Add(new { kind = "invoke_update", id, partial = raw, signalAborted = token.IsCancellationRequested }); }
                var returned = progress(Result(raw), token);
                Check(returned.IsCompletedSuccessfully, "Source callback waited for a listener or rejected an admitted update.");
                lock (gate) toolTrace.Add(new { kind = "update_callback_return", id, signalAborted = token.IsCancellationRequested });
                if (++returnedCount == 2) secondCallbackReturned[id].TrySetResult();
            }
            foreach (var partial in spec.GetProperty("updates").EnumerateArray()) Deliver(partial);
            if (aborting)
            {
                await abortObserved.Task; Deliver(spec.GetProperty("updateAfterAbort"));
                lock (gate) toolTrace.Add(new { kind = "execute_throw", id, errorMessage = spec.GetProperty("abortError").GetString(), signalAborted = token.IsCancellationRequested });
                executeLeaving.TrySetResult(); throw new InvalidOperationException(spec.GetProperty("abortError").GetString());
            }
            var result = Result(spec.GetProperty("result"));
            lock (gate) toolTrace.Add(new { kind = "execute_return", id, result = result.ToJson().Value, signalAborted = token.IsCancellationRequested });
            if (id == blockedId) executeLeaving.TrySetResult();
            return result;
        }))).ToImmutableArray();
        var hooks = new Hooks((invocation, result, isError, token) =>
        {
            lock (gate) afterIds.Add(invocation.Call.Id);
            var spec = specs.Single(value => Id(value) == invocation.Call.Id);
            return ValueTask.FromResult(spec.TryGetProperty("afterHook", out var after) ? JsonData.FromElement(after) : null);
        });
        var source = new Source(assistant);
        // The authoritative sink retains the uncanceled durable-commit boundary, including during abort.
        await using var agent = new NativeAgent(new(model, source, tools, hooks,
            mode == "sequential-field-shapes" ? ToolExecutionMode.Sequential : ToolExecutionMode.Parallel,
            new AgentHooks(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))), () => clock,
            new Sink((_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }));
        var system = new TranscriptEntry("system", JsonData.FromElement(JsonSerializer.SerializeToElement(new
        {
            role = "system", content = fixture.GetProperty("systemPrompt").GetString(), timestamp = 0,
            toolsAdded = specs.Select(spec => new { name = Name(spec), description = spec.GetProperty("description").GetString(), parameters = fixture.GetProperty("toolParameters") })
        })));
        agent.ReplaceMessages([system]);
        using var firstSubscription = agent.Subscribe(new Sink(async (observation, token) =>
        {
            var type = Type(observation); if (type is null) return;
            int index, ordinal; lock (gate)
            {
                index = events.Count; ordinal = 0;
                if (observation is ToolExecutionUpdated update)
                { counts.TryGetValue(update.Invocation.Call.Id, out ordinal); counts[update.Invocation.Call.Id] = ++ordinal; }
                metadata.Add(observation, (index, ordinal)); events.Add((observation, type, token.IsCancellationRequested));
                active++; maxActive = Math.Max(maxActive, active);
                listenerTrace.Add(new { kind = "first_enter", index, type, activeListeners = active, signalAborted = token.IsCancellationRequested });
            }
            if (observation is ToolExecutionUpdated blockedUpdate && blockedUpdate.Invocation.Call.Id == blockedId && ordinal == 1)
            {
                lock (gate) controls.Add(new { kind = "first_update_barrier_enter", id = blockedId });
                firstEntered.TrySetResult(); await firstRelease.Task;
                lock (gate) controls.Add(new { kind = "first_update_barrier_release", id = blockedId, signalAborted = token.IsCancellationRequested });
            }
            if (observation is AgentLoopEnded)
            {
                lock (gate) controls.Add(new { kind = "agent_end_barrier_enter" });
                endEntered.TrySetResult(); await endRelease.Task;
                lock (gate) controls.Add(new { kind = "agent_end_barrier_release" });
            }
            lock (gate)
            { listenerTrace.Add(new { kind = "first_exit", index, type, activeListeners = active, signalAborted = token.IsCancellationRequested }); active--; }
        }));
        using var secondSubscription = agent.Subscribe(new Sink((observation, token) =>
        {
            var type = Type(observation); if (type is null) return ValueTask.CompletedTask;
            lock (gate)
            {
                var (index, ordinal) = metadata[observation];
                listenerTrace.Add(new { kind = "second_enter", index, type, signalAborted = token.IsCancellationRequested });
                listenerTrace.Add(new { kind = "second_exit", index, type, signalAborted = token.IsCancellationRequested });
                if (observation is ToolExecutionUpdated update && ordinal == 2) second[update.Invocation.Call.Id].TrySetResult();
                if (observation is ToolExecutionEnded completed) ends[completed.Outcome.Invocation.Call.Id].TrySetResult();
            }
            return ValueTask.CompletedTask;
        }));
        var run = agent.PromptAsync(new TranscriptEntry("user", JsonData.FromElement(fixture.GetProperty("prompt"))));
        var idle = agent.WaitForIdleAsync();
        void Probe(string kind)
        {
            lock (gate) observations.Add(new
            {
                kind, eventCount = events.Count, callbackCount = callbacksInvoked,
                acceptedUpdateCount = events.Count(value => value.Event is ToolExecutionUpdated), afterHookIds = afterIds.ToArray(),
                toolEndIds = events.Select(value => value.Event).OfType<ToolExecutionEnded>().Select(value => value.Outcome.Invocation.Call.Id).ToArray(),
                toolMessageIds = events.Select(value => value.Event).OfType<ToolResultMessageEnded>().Select(value => value.Message.ToolCallId).ToArray(),
                activeListeners = active, idleResolved, promptResolved,
                signalAborted = agent.Snapshot.IsRunning ? (bool?)agent.Snapshot.CancellationRequested : null
            });
            Check(agent.Snapshot.Messages.All(message => message.Role != "toolResult") || events.Any(value => value.Event is ToolResultMessageEnded), "History contains an uncommitted tool message.");
        }
        void Release(string id) { lock (gate) controls.Add(new { kind = "release_tool_updates", id }); emit[id].TrySetResult(); }
        void Late(JsonElement spec, string kind)
        {
            lock (gate)
            {
                var before = events.Count; var returned = callbacks[Id(spec)](Result(spec.GetProperty("lateUpdate")), default);
                Check(returned.IsCompletedSuccessfully, "Late source callback rejected rather than being ignored.");
                controls.Add(new { kind, id = Id(spec), eventCountBefore = before, eventCountAfter = events.Count });
                Equal(before, events.Count);
            }
        }
        try
        {
            if (mode == "parallel-blocked-update")
            {
                await Task.WhenAll(started.Values.Select(value => value.Task)); Release(blockedId!);
                await firstEntered.Task; await second[blockedId!].Task; await executeLeaving.Task;
                Probe("first_update_blocked_after_second_listener_and_execute_return");
                var other = specs.Single(spec => Id(spec) != blockedId); Release(Id(other)); await ends[Id(other)].Task;
                Probe("unrelated_tool_finalized_while_first_update_blocked");
                lock (gate) controls.Add(new { kind = "release_first_update" }); firstRelease.TrySetResult(); await ends[blockedId!].Task;
            }
            else if (aborting)
            {
                var id = Id(specs[0]); await started[id].Task; Release(id);
                await firstEntered.Task; await second[id].Task; await secondCallbackReturned[id].Task;
                Probe("before_abort_with_first_update_blocked");
                lock (gate) controls.Add(new { kind = "call_public_abort" }); Check(agent.Abort(), "Actual Agent abort was absent.");
                await abortObserved.Task; await executeLeaving.Task; Probe("actual_tool_signal_aborted_after_third_update_before_barrier_release");
                lock (gate) controls.Add(new { kind = "release_first_update" }); firstRelease.TrySetResult(); await ends[id].Task;
            }
            else foreach (var spec in specs) { await started[Id(spec)].Task; Release(Id(spec)); await ends[Id(spec)].Task; }
            await endEntered.Task; Probe("agent_end_listener_blocked");
            Check(!run.IsCompleted && !idle.IsCompleted && agent.Snapshot.IsRunning, "Public settlement overtook the end listener.");
            foreach (var spec in specs) Late(spec, "late_callback_after_tool_end");
            Probe("late_callbacks_returned_while_agent_end_blocked");
            lock (gate) controls.Add(new { kind = "release_agent_end" }); endRelease.TrySetResult();
            var completed = await run; promptResolved = true; await idle; idleResolved = true;
            Probe("public_prompt_and_idle_settled"); Late(specs[0], "late_callback_after_idle");
            Same(expected.GetProperty("listenerTrace"), JsonSerializer.SerializeToElement(listenerTrace), "full listener trace");
            Same(ProjectRows(expected.GetProperty("controls"), "returned"), JsonSerializer.SerializeToElement(controls), "full control/barrier/disposition trace");
            foreach (var item in expected.GetProperty("controls").EnumerateArray().Where(value => value.TryGetProperty("returned", out _)))
                Same(JsonSerializer.SerializeToElement(new[] { "" }), item.GetProperty("returned").GetProperty("ownUndefinedPaths"), "source void callback inventory");
            Same(ProjectRows(expected.GetProperty("observations"), "state", "activeSignal"), JsonSerializer.SerializeToElement(observations), "all public barrier probes");
            var sourceEvents = expected.GetProperty("events").EnumerateArray().ToArray();
            Equal(sourceEvents.Length, events.Count);
            for (var index = 0; index < sourceEvents.Length; index++)
            {
                var sourceEvent = sourceEvents[index].GetProperty("event").GetProperty("json"); var native = events[index];
                Equal(sourceEvent.GetProperty("type").GetString(), native.Type); Equal(sourceEvents[index].GetProperty("signalAborted").GetBoolean(), native.Aborted);
                if (native.Event is ToolExecutionUpdated update)
                {
                    Equal(sourceEvent.GetProperty("toolCallId").GetString(), update.Invocation.Call.Id);
                    Equal(sourceEvent.GetProperty("toolName").GetString(), update.Invocation.Call.Name);
                    Same(sourceEvent.GetProperty("args"), update.Invocation.Call.Arguments.Value, "update arguments");
                    Same(sourceEvent.GetProperty("partialResult"), update.PartialResult.ToJson().Value, "complete owned progress result including presence");
                }
                if (native.Event is ToolExecutionEnded end)
                {
                    Equal(sourceEvent.GetProperty("toolCallId").GetString(), end.Outcome.Invocation.Call.Id);
                    Equal(sourceEvent.GetProperty("isError").GetBoolean(), end.Outcome.IsError);
                    Same(sourceEvent.GetProperty("result"), end.Outcome.Result.ToJson().Value, "complete finalized owned result and independent outcome error");
                }
            }
            // Source-ordered durable history and generated end messages use the actual native message bodies.
            Same(JsonSerializer.SerializeToElement(completed.Transcript.Select(value => value.WireBody.Value)), JsonSerializer.SerializeToElement(agent.Snapshot.Messages.Select(value => value.WireBody.Value)), "canonical history");
            var nativeHistory = JsonSerializer.SerializeToElement(agent.Snapshot.Messages.Select(value => value.WireBody.Value));
            var historyDifferences = new List<string>();
            Differences(expected.GetProperty("finalState").GetProperty("json").GetProperty("messages"), nativeHistory, "", historyDifferences);
            Sequence(Array.Empty<string>(), historyDifferences);
            Same(expected.GetProperty("finalState").GetProperty("json").GetProperty("messages"), nativeHistory, "complete source canonical history");
            // Terminal event history is generated-only, unlike the canonical context containing the system record.
            foreach (var native in events.Where(value => value.Event is AgentLoopEnded))
            {
                var end = (AgentLoopEnded)native.Event;
                var generated = end.Result.Transcript.Skip(1).Select(value => value.WireBody.Value).ToArray();
                var sourceEnd = sourceEvents.Last().GetProperty("event").GetProperty("json").GetProperty("messages");
                var generatedDifferences = new List<string>(); Differences(sourceEnd, JsonSerializer.SerializeToElement(generated), "", generatedDifferences);
                Sequence(Array.Empty<string>(), generatedDifferences);
                Same(sourceEnd, JsonSerializer.SerializeToElement(generated), "complete generated terminal history");
            }
            Equal(1, source.Requests); Equal(1, source.Cleanups); Equal(aborting, agent.Snapshot.CancellationRequested);
            Equal(expected.GetProperty("metrics").GetProperty("maxActiveFirstListeners").GetInt32(), maxActive);
            Sequence(specs.Select(Id), completed.Turns.Single().ToolResults.Select(value => value.WireBody.Value.GetProperty("toolCallId").GetString()!));
            foreach (var body in completed.Turns.Single().ToolResults) Check(!body.WireBody.Value.TryGetProperty("structuredContent", out _), "Structured output entered durable model history.");
            Same(ProjectToolTrace(expected.GetProperty("toolTrace")), JsonSerializer.SerializeToElement(toolTrace.Select(ProjectNativeToolTrace)), "complete tool invocation/callback/abort ordering");
            DifferentialEvidence = new { sourceSha = fixture.GetProperty("sourceSha").GetString(), caseId = scenario.GetProperty("caseId").GetString(),
                fullListenerTrace = true, fullControls = true, allBarrierProbeFieldsExceptExplicitStateRepresentation = true,
                completeOwnedProgressAndFinalResults = true, completeCanonicalAndGeneratedHistories = true, actualAbort = aborting,
                remainingRepresentationRows = RepresentationDifferences };
        }
        finally
        {
            foreach (var value in emit.Values) value.TrySetResult(); firstRelease.TrySetResult(); endRelease.TrySetResult();
            agent.Abort(); try { await run; } catch { } try { await idle; } catch { }
        }
    }

    internal static readonly string[] RepresentationDifferences =
    [
        "Source callback return/Agent prompt/idle returns are JavaScript undefined; CLR callbacks return an immediately completed ValueTask and Agent returns its loop result.",
        "AgentSnapshot is canonical durable history, not the full JavaScript state object: provisional streaming message, Set pendingToolCalls, callable tools, rich model/options and AbortSignal objects remain different API representations.",
        "JavaScript own undefined fields introduced by a truthy empty after-hook patch remain separately inventoried; JSON omission is represented by owned property absence. Callable/Set/AbortSignal object identity remains mandatory unfinished API work.",
        "Provider toolcall_delta source partial arguments remain empty; native stream observations are typed deltas without source partial snapshots. This test compares the complete event/listener order, not provider-partial JSON equality."
    ];

    private static string Id(JsonElement spec) => spec.GetProperty("call").GetProperty("id").GetString()!;
    private static string Name(JsonElement spec) => spec.GetProperty("call").GetProperty("name").GetString()!;
    private static ToolResult Result(JsonElement raw) => ToolResult.FromJson(JsonData.FromElement(raw));
    private static string? Type(AgentEvent value) => value switch
    {
        AgentLoopStarted => "agent_start", AgentLoopTurnStarted => "turn_start", AgentLoopInputMessageStarted or AssistantMessageStarted or ToolResultMessageStarted => "message_start",
        AgentLoopInputMessageEnded or AssistantMessageEnded or ToolResultMessageEnded => "message_end",
        TurnStreamObserved { Event: ToolCallStarted or ToolCallDelta or ToolCallEnded } => "message_update",
        TurnStreamObserved => null, ToolExecutionStarted => "tool_execution_start", ToolExecutionUpdated => "tool_execution_update", ToolExecutionEnded => "tool_execution_end",
        AgentLoopTurnEnded => "turn_end", AgentLoopEnded => "agent_end", _ => throw new InvalidOperationException("Unexpected captured lifecycle event.")
    };
    private sealed class Source(AssistantMessage final) : IChatTransport
    {
        public int Requests, Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests++; try
            {
                yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
                for (var index = 0; index < final.Content.Length; index++)
                {
                    var call = (ToolCallContent)final.Content[index]; yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject });
                    yield return new ToolCallDelta(index, call.Arguments.ToString()); yield return new ToolCallEnded(index, call);
                }
                yield return new StreamDone(StopReason.ToolUse, final); await Task.CompletedTask;
            }
            finally { Cleanups++; }
        }
    }
    private sealed class Executor(Func<ToolInvocation, ToolProgressCallback, CancellationToken, ValueTask<ToolResult>> callback) : IToolExecutor
    {
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token) => ExecuteAsync(invocation, (_, _) => ValueTask.CompletedTask, token);
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, ToolProgressCallback progress, CancellationToken token) => callback(invocation, progress, token);
    }
    private sealed class Hooks(Func<ToolInvocation, ToolResult, bool, CancellationToken, ValueTask<JsonData?>> after) : ISourceToolHooks
    {
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(ToolPreflightDecision.Allow);
        public ValueTask<JsonData?> AfterToolCallAsync(ToolInvocation invocation, ToolResult result, bool isError, CancellationToken token) => after(invocation, result, isError, token);
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> callback) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => callback(observation, token); }
    private static JsonElement ProjectRows(JsonElement rows, params string[] omitted) => JsonSerializer.SerializeToElement(rows.EnumerateArray().Select(row =>
        row.EnumerateObject().Where(property => !omitted.Contains(property.Name)).ToDictionary(property => property.Name, property => property.Value)));
    private static JsonElement ProjectToolTrace(JsonElement rows) => JsonSerializer.SerializeToElement(rows.EnumerateArray().Select(row =>
        row.EnumerateObject().Where(property => property.Name != "returned")
            .ToDictionary(property => property.Name, property => property.Name is "args" or "partial" or "result" ? property.Value.GetProperty("json") : property.Value)));
    private static JsonElement ProjectNativeToolTrace(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonDocument Frozen(string relative, string hash)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative))) directory = directory.Parent;
        Check(directory is not null, "Frozen whole Agent progress input/oracle unavailable.");
        var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, relative));
        Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes))); return JsonDocument.Parse(bytes);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Values differ: expected {expected}; actual {actual}.");
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Check(expected.SequenceEqual(actual), "Source order differs.");
    private static void Same(JsonElement expected, JsonElement actual, string label) => Check(Equivalent(expected, actual), $"{label} differs. Expected {expected.GetRawText()}; actual {actual.GetRawText()}.");
    private static bool Equivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var a = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var b = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && Equivalent(pair.Value, value));
        }
        if (left.ValueKind == JsonValueKind.Array) return left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Equivalent(pair.First, pair.Second));
        return left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText();
    }
    private static void Differences(JsonElement left, JsonElement right, string path, List<string> differences)
    {
        if (left.ValueKind != right.ValueKind) { differences.Add(path); return; }
        if (left.ValueKind == JsonValueKind.Object)
        {
            var a = left.EnumerateObject().ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
            var b = right.EnumerateObject().ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
            foreach (var name in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
            {
                var next = path + "/" + name.Replace("~", "~0").Replace("/", "~1");
                if (!a.TryGetValue(name, out var av) || !b.TryGetValue(name, out var bv)) differences.Add(next);
                else Differences(av, bv, next, differences);
            }
        }
        else if (left.ValueKind == JsonValueKind.Array)
        {
            if (left.GetArrayLength() != right.GetArrayLength()) { differences.Add(path); return; }
            for (var index = 0; index < left.GetArrayLength(); index++) Differences(left[index], right[index], path + "/" + index, differences);
        }
        else if (!Equivalent(left, right)) differences.Add(path);
    }
}
