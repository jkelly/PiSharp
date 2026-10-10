using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using static EventFixture;

// Pi v1.1.0 packages/coding-agent/src/core/agent-session.ts (compact and _runAutoCompaction with session_before_compact,
// _emitSessionCompactFailed, _dispatchTurnEndBoundary, _runBeforeSettleBoundary, _buildBoundaryContext, _commitBoundaryDrafts,
// message_end replacement), core/extensions/runner.ts (emit, emitBoundary, emitMessageEnd), core/virtual-models.ts
// (getBranchSelection) and core/sdk.ts (session model restore), by reading.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> BoundaryCases() =>
    [
        Case("boundary.session-before-compact-payload-cancel-stops-dispatch", BeforeCompactCancel),
        Case("boundary.session-before-compact-extension-compaction-replaces-summary", BeforeCompactExtensionCompaction),
        Case("boundary.session-compact-failed-from-extension", CompactFailedFromExtension),
        Case("boundary.turn-end-drafts-committed-and-continue-runs-next-request", TurnEndDraftsAndContinue),
        Case("boundary.turn-end-invalid-entries-and-unrunnable-continue-reported", TurnEndInvalid),
        Case("boundary.agent-before-settle-drafts-and-continue", BeforeSettleContinue),
        Case("boundary.message-end-replacement-persisted-and-role-checked", MessageEndReplacement),
        Case("boundary.turn-end-drafts-continue-the-same-run", TurnEndSameRun),
        Case("boundary.message-end-replacement-applies-within-the-run", MessageEndWithinRun),
        Case("boundary.resume-restores-virtual-branch-selection", ResumeVirtualSelection),
        Case("boundary.switch-emits-no-restore-model-select", SwitchEmitsNoModelSelect),
    ];

    private static void AttachEvents(EventFixture f) =>
        new NativeSessionEventBinding(f.Registry, f.Registry.CaptureSnapshot(), f.Report).Attach(f.Owner, f.Owner.Current);
    private static ValueTask<JsonData?> Result(string json) => ValueTask.FromResult<JsonData?>(JsonData.Parse(json));
    private static readonly ValueTask<JsonData?> NoResult = ValueTask.FromResult<JsonData?>(null);
    private static Task<SessionSummaryCheckpointReceipt?> CompactAsync(EventFixture f, ISessionSummaryGenerator generator) =>
        f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(new(KeepRecentTokens: 1), ContextWindow: 128_000), generator);

    private static async Task BeforeCompactCancel()
    {
        await using var f = await CreateAsync(); var calls = new List<string>(); JsonData? seen = null; var failed = new List<JsonData>();
        await f.Activate(api =>
        {
            var handlers = (IExtensionEventHandlerRegistry)api;
            handlers.RegisterEventHandler(new("first", "session_before_compact", (value, _, _) => { calls.Add("first"); seen = value; return Result("""{"cancel":true}"""); }));
            handlers.RegisterEventHandler(new("second", "session_before_compact", (_, _, _) => { calls.Add("second"); return NoResult; }));
            api.Observe(new("failed", "session_compact_failed", (value, _, _) => { failed.Add(value); return ValueTask.CompletedTask; }));
        });
        AttachEvents(f); f.StartRpc(); var generator = new FlakySummary();
        var error = await ThrowsAsync<SessionCompactionException>(() => CompactAsync(f, generator), "cancelled compaction");
        Check(error.Failure == SessionCompactionFailure.Cancelled && error.Message == "Compaction cancelled", "cancel failure: " + error.Message);
        Check(calls.SequenceEqual(["first"]), "A cancelling result did not stop the dispatch: " + string.Join(",", calls));
        Equal(0, generator.Calls, "default summary requests after cancel");
        var value = seen!.Value;
        Check(Keys(seen).SequenceEqual(["type", "preparation", "branchEntries", "reason", "willRetry"]), "event shape: " + string.Join(",", Keys(seen)));
        Check(Keys(JsonData.FromElement(value.GetProperty("preparation"))).SequenceEqual(["firstKeptEntryId", "messagesToSummarize", "turnPrefixMessages",
            "isSplitTurn", "tokensBefore", "fileOps", "settings"]), "preparation shape: " + value.GetProperty("preparation").GetRawText()[..200]);
        Equal("""{"read":[],"written":[],"edited":[]}""", value.GetProperty("preparation").GetProperty("fileOps").GetRawText(), "fileOps");
        Equal(4, value.GetProperty("branchEntries").GetArrayLength(), "branch entries");
        Check(value.GetProperty("reason").GetString() == "manual" && !value.GetProperty("willRetry").GetBoolean(), "reason/willRetry");
        var end = f.Frames().Single(frame => Type(frame) == "compaction_end").Value;
        Check(end.GetProperty("aborted").GetBoolean() && !end.TryGetProperty("errorMessage", out _), "compaction_end after cancel: " + end.GetRawText());
        var compactFailed = failed.Single().Value;
        Check(compactFailed.GetProperty("aborted").GetBoolean() && !compactFailed.GetProperty("fromExtension").GetBoolean() &&
            !compactFailed.TryGetProperty("errorMessage", out _), "session_compact_failed after cancel: " + compactFailed.GetRawText());
        Check(!f.Session.Snapshot.Log.Entries.Any(entry => entry.Kind == SessionEntryKind.Compaction), "A cancelled compaction was written.");
    }

    private static async Task BeforeCompactExtensionCompaction()
    {
        await using var f = await CreateAsync(); var calls = new List<string>(); string? kept = null;
        await f.Activate(api =>
        {
            var handlers = (IExtensionEventHandlerRegistry)api;
            handlers.RegisterEventHandler(new("broken", "session_before_compact", (_, _, _) => { calls.Add("broken"); throw new InvalidOperationException("hook failed"); }));
            handlers.RegisterEventHandler(new("custom", "session_before_compact", (value, _, _) =>
            {
                calls.Add("custom"); kept = value.Value.GetProperty("preparation").GetProperty("firstKeptEntryId").GetString();
                return Result(JsonSerializer.Serialize(new { compaction = new { summary = "extension summary", firstKeptEntryId = kept, tokensBefore = 42,
                    details = new { source = "extension" }, usage = new { input = 1, output = 2, cacheRead = 0, cacheWrite = 0, totalTokens = 3,
                        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 } } } }));
            }));
            handlers.RegisterEventHandler(new("silent", "session_before_compact", (_, _, _) => { calls.Add("silent"); return NoResult; }));
        });
        AttachEvents(f); f.StartRpc(); var generator = new FlakySummary();
        var receipt = await CompactAsync(f, generator);
        Check(calls.SequenceEqual(["broken", "custom", "silent"]), "handler order: " + string.Join(",", calls));
        Equal(0, generator.Calls, "default summary requests");
        var diagnostic = f.Diagnostics.Single();
        Check(diagnostic.EventName == "session_before_compact" && diagnostic.Message == "hook failed", "handler error not reported");
        var entry = receipt!.Entry.WireBody.Value;
        Check(entry.GetProperty("summary").GetString() == "extension summary" && entry.GetProperty("firstKeptEntryId").GetString() == kept &&
            entry.GetProperty("tokensBefore").GetDouble() == 42 && entry.GetProperty("fromHook").GetBoolean() &&
            entry.GetProperty("details").GetProperty("source").GetString() == "extension" && entry.GetProperty("usage").GetProperty("totalTokens").GetInt32() == 3,
            "extension compaction entry: " + entry.GetRawText());
        var end = f.Frames().Single(frame => Type(frame) == "compaction_end").Value;
        Check(end.GetProperty("result").GetProperty("summary").GetString() == "extension summary", "compaction_end result");
    }

    private static async Task CompactFailedFromExtension()
    {
        await using var f = await CreateAsync(); var failed = new List<JsonData>();
        await f.Activate(api =>
        {
            ((IExtensionEventHandlerRegistry)api).RegisterEventHandler(new("custom", "session_before_compact", (_, _, _) =>
                Result("""{"compaction":{"summary":"extension summary","firstKeptEntryId":"missing-entry","tokensBefore":-1}}""")));
            api.Observe(new("failed", "session_compact_failed", (value, _, _) => { failed.Add(value); return ValueTask.CompletedTask; }));
        });
        AttachEvents(f); f.StartRpc();
        await ThrowsAsync<Exception>(() => CompactAsync(f, new FlakySummary()), "invalid extension compaction");
        var value = failed.Single().Value;
        Check(value.GetProperty("fromExtension").GetBoolean() && !value.GetProperty("aborted").GetBoolean() &&
            value.GetProperty("errorMessage").GetString()!.StartsWith("Compaction failed: ", StringComparison.Ordinal), "session_compact_failed: " + value.GetRawText());
    }

    private static async Task TurnEndDraftsAndContinue()
    {
        await using var f = await CreateAsync(Response(text: "alpha"), Response(text: "beta")); var events = new List<JsonData>();
        await f.Activate(api => ((IExtensionEventHandlerRegistry)api).RegisterEventHandler(new("boundary", "turn_end", (value, _, _) =>
        {
            events.Add(value);
            return events.Count == 1 ? Result("""{"entries":[{"type":"custom_message","customType":"note","content":"please continue","display":true},{"type":"custom","customType":"mark","data":{"n":1}}],"continue":true}""") : NoResult;
        })));
        AttachEvents(f); f.StartRpc(); await f.PromptAsync();
        Equal(2, f.Transport.Calls, "provider requests");
        Equal(2, events.Count, "turn_end dispatches");
        var first = events[0];
        Check(Keys(first).SequenceEqual(["type", "turnIndex", "message", "toolResults", "messageEntryId", "toolResultEntryIds", "outcome", "entries", "continue", "context"]),
            "turn_end shape: " + string.Join(",", Keys(first)));
        Check(first.Value.GetProperty("entries").GetArrayLength() == 0 && !first.Value.GetProperty("continue").GetBoolean() &&
            first.Value.GetProperty("outcome").GetString() == "completed" && !first.Value.GetProperty("context").GetProperty("canContinue").GetBoolean(), "initial boundary state");
        Check(Keys(JsonData.FromElement(first.Value.GetProperty("context"))).SequenceEqual(["contextEntries", "contextMessages", "llmMessages", "pendingMessages", "canContinue"]), "context shape");
        var log = f.Session.Snapshot.Log.Entries;
        Equal(first.Value.GetProperty("messageEntryId").GetString(), log.Single(entry => entry.Kind == SessionEntryKind.Message &&
            entry.WireBody.Value.GetProperty("message").TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array &&
            content.GetArrayLength() == 1 && content[0].TryGetProperty("text", out var text) && text.GetString() == "alpha").Id, "messageEntryId");
        var tail = log.Skip(log.Length - 4).Select(entry => entry.Type).ToArray();
        Check(tail.SequenceEqual(["message", "custom_message", "custom", "message"]), "committed order: " + string.Join(",", tail));
        var appended = f.Frames().Where(frame => Type(frame) == "entry_appended").Select(frame => frame.Value.GetProperty("entry").GetProperty("type").GetString()).ToArray();
        Check(appended.SequenceEqual(["custom_message", "custom"]), "entry_appended: " + string.Join(",", appended));
        var request = f.Transport.Requests[1].Messages;
        Check(request[^1].WireBody.ToString().Contains("please continue", StringComparison.Ordinal), "The drafted message did not reach the next request.");
    }

    private static async Task TurnEndInvalid()
    {
        await using var f = await CreateAsync(); var mode = "invalid";
        await f.Activate(api => ((IExtensionEventHandlerRegistry)api).RegisterEventHandler(new("boundary", "turn_end", (_, _, _) =>
            mode == "invalid" ? Result("""{"entries":[{"type":"bogus"}],"continue":true}""") : Result("""{"continue":true}"""))));
        AttachEvents(f); f.StartRpc(); var before = f.Session.Snapshot.Log.Entries.Length;
        await f.PromptAsync();
        Equal(1, f.Transport.Calls, "invalid drafts continued the run");
        Equal(before + 2, f.Session.Snapshot.Log.Entries.Length, "invalid drafts were committed");
        Check(f.Diagnostics.Any(d => d.EventName == "turn_end" && d.Message == "Invalid boundary entries: Unknown boundary entry type: bogus"),
            "invalid entries not reported: " + string.Join(" | ", f.Diagnostics.Select(d => d.ErrorText)));
        mode = "continue"; await f.PromptAsync("again", "again");
        Equal(2, f.Transport.Calls, "an unrunnable continuation ran");
        Check(f.Diagnostics.Any(d => d.EventName == "turn_end" && d.Message == "turn_end requested continuation without runnable model context"),
            "invalid continuation not reported");
    }

    private static async Task BeforeSettleContinue()
    {
        await using var f = await CreateAsync(Response(text: "first"), Response(text: "second")); var events = new List<JsonData>();
        await f.Activate(api => ((IExtensionEventHandlerRegistry)api).RegisterEventHandler(new("settle", "agent_before_settle", (value, _, _) =>
        {
            events.Add(value);
            return events.Count == 1 ? Result("""{"entries":[{"type":"custom_message","customType":"note","content":[{"type":"text","text":"settle note"}],"display":false}],"continue":true}""") : NoResult;
        })));
        AttachEvents(f); f.StartRpc(); await f.PromptAsync();
        Equal(2, f.Transport.Calls, "provider requests");
        Equal(2, events.Count, "agent_before_settle dispatches");
        Check(Keys(events[0]).SequenceEqual(["type", "outcome", "entries", "continue", "context"]), "agent_before_settle shape: " + string.Join(",", Keys(events[0])));
        Equal("completed", events[0].Value.GetProperty("outcome").GetString(), "outcome");
        Check(f.Transport.Requests[1].Messages[^1].WireBody.ToString().Contains("settle note", StringComparison.Ordinal), "The drafted message did not reach the next request.");
        var tail = f.Session.Snapshot.Log.Entries.TakeLast(3).Select(entry => entry.Type).ToArray();
        Check(tail.SequenceEqual(["message", "custom_message", "message"]), "committed order: " + string.Join(",", tail));
    }

    private static async Task MessageEndReplacement()
    {
        await using var f = await CreateAsync(Response(text: "original"), Response(text: "next"));
        await f.Activate(api => ((IExtensionEventHandlerRegistry)api).RegisterEventHandler(new("replace", "message_end", (value, _, _) =>
        {
            var message = JsonNode.Parse(value.Value.GetProperty("message").GetRawText())!.AsObject();
            if (message["role"]!.GetValue<string>() == "user")
                return Result("""{"message":{"role":"assistant","content":[]}}""");
            if (message["role"]!.GetValue<string>() != "assistant" || message["content"]![0]!["text"]!.GetValue<string>() != "original") return NoResult;
            message["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "replaced" });
            return Result(new JsonObject { ["message"] = message }.ToJsonString());
        })));
        AttachEvents(f); f.StartRpc(); await f.PromptAsync();
        var assistant = f.Session.Snapshot.Log.Entries.Last(entry => entry.Kind == SessionEntryKind.Message).WireBody.Value.GetProperty("message");
        Equal("replaced", assistant.GetProperty("content")[0].GetProperty("text").GetString(), "persisted replacement");
        Check(f.Diagnostics.Any(d => d.EventName == "message_end" && d.Message == "message_end handlers must return a message with the same role"),
            "role change not reported: " + string.Join(" | ", f.Diagnostics.Select(d => d.ErrorText)));
        Equal("user", f.Session.Snapshot.Log.Entries.Reverse().Skip(1).First().WireBody.Value.GetProperty("message").GetProperty("role").GetString(), "user kept");
        await f.PromptAsync("again", "again");
        var sent = f.Transport.Requests[1].Messages.Where(message => message.Role == "assistant").Select(message => message.WireBody.ToString()).ToArray();
        Check(sent.Any(text => text.Contains("replaced", StringComparison.Ordinal)) && !sent.Any(text => text.Contains("original", StringComparison.Ordinal)),
            "The next request did not use the persisted replacement.");
    }

    // Upstream finishTurn commits turn_end drafts and returns {action:"continue"}; runLoop continues: one agent_start/agent_end
    // pair, turn_start/turn_end per request, turnIndex 0 and 1, and agent_end.messages are the run's newMessages (no drafts).
    private static async Task TurnEndSameRun()
    {
        await using var f = await CreateAsync(Response(text: "alpha"), Response(text: "beta")); var events = new List<JsonData>();
        await f.Activate(api => ((IExtensionEventHandlerRegistry)api).RegisterEventHandler(new("boundary", "turn_end", (value, _, _) =>
        {
            events.Add(value);
            return events.Count == 1 ? Result("""{"entries":[{"type":"custom_message","customType":"note","content":"please continue","display":true}],"continue":true}""") : NoResult;
        })));
        AttachEvents(f); f.StartRpc(); await f.PromptAsync();
        Equal(2, f.Transport.Calls, "provider requests");
        var types = f.Frames().Select(Type).Where(type => type is "agent_start" or "agent_end" or "turn_start" or "turn_end").ToArray();
        Check(types.SequenceEqual(["agent_start", "turn_start", "turn_end", "turn_start", "turn_end", "agent_end"]), "lifecycle: " + string.Join(",", types));
        Check(events.Select(value => value.Value.GetProperty("turnIndex").GetInt32()).SequenceEqual([0, 1]), "turnIndex continues across the boundary");
        var agentEnd = f.Frames().Single(frame => Type(frame) == "agent_end").Value.GetProperty("messages");
        var texts = agentEnd.EnumerateArray().Select(message => message.GetProperty("role").GetString() + ":" + message.GetRawText()).ToArray();
        Check(agentEnd.GetArrayLength() == 3 && texts[0].StartsWith("user:", StringComparison.Ordinal) && texts[1].Contains("alpha", StringComparison.Ordinal) &&
            texts[2].Contains("beta", StringComparison.Ordinal), "agent_end messages: " + string.Join(" | ", texts));
        Check(f.Transport.Requests[1].Messages.Any(message => message.WireBody.ToString().Contains("please continue", StringComparison.Ordinal)), "draft in the next request");
        var turnStarts = f.Frames().Where(frame => Type(frame) == "turn_start").ToArray();
        Equal(2, turnStarts.Length, "turn_start frames");
    }

    // Upstream _replaceMessageInPlace mutates the finalized message: the loop's next request in the same run, the turn_end and
    // agent_end payloads and the agent state all carry the replacement.
    private static async Task MessageEndWithinRun()
    {
        await using var f = await CreateAsync(true, ToolCall(), Response(text: "done"));
        await f.Session.SetActiveToolsAsync(["probe"]);
        await f.Activate(api => ((IExtensionEventHandlerRegistry)api).RegisterEventHandler(new("replace", "message_end", (value, _, _) =>
        {
            var message = JsonNode.Parse(value.Value.GetProperty("message").GetRawText())!.AsObject();
            var role = message["role"]!.GetValue<string>();
            if (role == "assistant" && message["stopReason"]!.GetValue<string>() == "toolUse")
            {
                ((JsonArray)message["content"]!).Add(new JsonObject { ["type"] = "text", ["text"] = "assistant-replaced" });
                return Result(new JsonObject { ["message"] = message }.ToJsonString());
            }
            if (role == "toolResult")
            {
                message["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "result-replaced" });
                return Result(new JsonObject { ["message"] = message }.ToJsonString());
            }
            return NoResult;
        })));
        AttachEvents(f); f.StartRpc(); await f.PromptAsync();
        Equal(2, f.Transport.Calls, "provider requests");
        Equal(1, f.Probe.Executions, "tool executions");
        var second = string.Join("\n", f.Transport.Requests[1].Messages.Select(message => message.WireBody.ToString()));
        Check(second.Contains("assistant-replaced", StringComparison.Ordinal) && second.Contains("result-replaced", StringComparison.Ordinal) &&
            !second.Contains("probe output", StringComparison.Ordinal), "The same run's next request did not use the replacements.");
        var turnEnd = f.Frames().First(frame => Type(frame) == "turn_end").Value;
        Check(turnEnd.GetProperty("message").GetRawText().Contains("assistant-replaced", StringComparison.Ordinal) &&
            turnEnd.GetProperty("toolResults")[0].GetRawText().Contains("result-replaced", StringComparison.Ordinal), "RPC turn_end: " + turnEnd.GetRawText());
        var agentEnd = f.Frames().Single(frame => Type(frame) == "agent_end").Value.GetProperty("messages").GetRawText();
        Check(agentEnd.Contains("assistant-replaced", StringComparison.Ordinal) && agentEnd.Contains("result-replaced", StringComparison.Ordinal) &&
            !agentEnd.Contains("probe output", StringComparison.Ordinal), "RPC agent_end: " + agentEnd);
        var agentMessages = string.Join("\n", f.Session.Snapshot.Agent.Messages.Select(message => message.WireBody.ToString()));
        Check(agentMessages.Contains("assistant-replaced", StringComparison.Ordinal) && !agentMessages.Contains("probe output", StringComparison.Ordinal), "agent state");
        var types = f.Frames().Select(Type).Where(type => type is "agent_start" or "agent_end").ToArray();
        Check(types.SequenceEqual(["agent_start", "agent_end"]), "lifecycle: " + string.Join(",", types));
    }

    private static readonly ModelDescriptor Router = new("router", "pi-virtual", "fixture");
    private static async Task<ModelDescriptor> OpenSelectionAsync(bool registerRouter, params object[] entries)
    {
        var root = Temp("virtual-resume"); var path = Path.Combine(root, "s.jsonl"); var codec = new SessionEntryCodec(); var ticks = 1_800_000_000_000L;
        var transport = new ScriptTransport();
        try
        {
            await using (var store = await SessionLogStore.CreateNewAsync(path, codec.Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "virtual-resume", timestamp = "2026-10-08T00:00:00.000Z", cwd = root }))))
                await store.AppendAsync([.. entries.Select((entry, index) => codec.Parse(JsonSerializer.Serialize(entry)))]);
            var runtime = new SessionRuntimeRegistry(registerRouter ? [new(Model, transport), new(Model2, transport), new(Router, transport)] :
                [new(Model, transport), new(Model2, transport)], [], new NoPolicy());
            var lifecycle = new PersistentSessionLifecycle(runtime, () => ++ticks, () => "e" + ++ticks,
                new PersistentAgentSessionOptions(AgentOptions: new() { TimeProvider = TimeProvider.System }));
            await using var owner = lifecycle.Attach(await lifecycle.OpenAsync(new(path), Model));
            return owner.Current.Session.Snapshot.Agent.Model;
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
    private static object User(string id, string? parent) => new { type = "message", id, parentId = parent, timestamp = "2026-10-08T00:00:00.000Z",
        message = new { role = "user", content = "hi", timestamp = 10 } };
    private static object Answer(string id, string parent, string model) => new { type = "message", id, parentId = parent, timestamp = "2026-10-08T00:00:00.000Z",
        message = JsonSerializer.Deserialize<JsonElement>(PiWireJson.WriteMessage(Response(text: "a") with { Model = model, Timestamp = 10 }).ToString()) };
    private static object Change(string id, string parent, string model) => new { type = "model_change", id, parentId = parent, timestamp = "2026-10-08T00:00:00.000Z",
        provider = "fixture", modelId = model };

    private static async Task ResumeVirtualSelection()
    {
        // A virtual model_change holds over the physical response it routed to.
        Equal(Router, await OpenSelectionAsync(true, User("u", null), Change("c", "u", "router"), Answer("a", "c", "parity-events")), "virtual selection");
        // A virtual model that is no longer registered falls back to the physical model that answered last.
        Equal(Model, await OpenSelectionAsync(false, User("u", null), Change("c", "u", "router"), Answer("a", "c", "parity-events")), "unregistered virtual");
        // A physical model_change before the response does not hold: the response wins.
        Equal(Model, await OpenSelectionAsync(true, User("u", null), Change("c", "u", "parity-events-2"), Answer("a", "c", "parity-events")), "physical change");
        // A later model_change always wins.
        Equal(Model2, await OpenSelectionAsync(true, User("u", null), Change("c", "u", "router"), Answer("a", "c", "parity-events"), Change("d", "a", "parity-events-2")),
            "later change");
        Check(SessionBranchSelection.Select([], (_, _) => true) is null, "empty branch");
    }

    private static async Task SwitchEmitsNoModelSelect()
    {
        // Upstream v1.1.0 emits model_select only from setModel ("set") and cycleModel ("cycle"); restoring a session's model on
        // open, resume or switch emits nothing.
        var (f, seen) = await ObservedAsync(false, ["model_select"]);
        await using var owned = f;
        var other = Path.Combine(f.Root, "other.jsonl"); var codec = new SessionEntryCodec();
        await using (var store = await SessionLogStore.CreateNewAsync(other, codec.Parse(JsonSerializer.Serialize(new
            { type = "session", version = 3, id = "other", timestamp = "2026-10-08T00:00:00.000Z", cwd = f.Root }))))
            await store.AppendAsync([codec.Parse(JsonSerializer.Serialize(User("u", null))), codec.Parse(JsonSerializer.Serialize(Change("c", "u", "parity-events-2")))]);
        var binding = new NativeSessionEventBinding(f.Registry, f.Registry.CaptureSnapshot(), f.Report);
        var result = await f.Owner.SwitchAsync(f.Owner.Current, new(other), afterSwitch: replacement => { binding.Attach(f.Owner, replacement.Current); return ValueTask.CompletedTask; });
        Check(result is not null, "switch vetoed");
        Equal(Model2, f.Session.Snapshot.Agent.Model, "restored model");
        Check(seen.Count == 0, "A restored model emitted model_select.");
    }
}
