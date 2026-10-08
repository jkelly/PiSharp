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

internal static class RpcModelThinkingTests
{
    internal const string Prefix = "rpc.model-thinking.";
    private static readonly ModelDescriptor First = new("first", "openai-responses", "fixture");
    private static readonly ModelDescriptor Second = new("second", "openai-responses", "fixture");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "ordered-inventory-durable-selection-and-cycle", Selection),
        (Prefix + "thinking-shapes-clamps-and-explicit-runtime-prerequisite", Thinking),
        (Prefix + "malformed-missing-canceled-and-response-budget-no-mutation", Rejections),
        (Prefix + "queries-during-run-and-mutation-idle-fence", Active),
        (Prefix + "singleton-cycle-null-and-same-model-ack", Singleton)
    ];
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Success(JsonElement response) => Check(response.GetProperty("success").GetBoolean(), response.GetRawText());

    private static async Task Selection()
    {
        await using var fixture = await Fixture.Create();
        var initial = fixture.Session.Snapshot.Log; CheckInitialModelCheckpoint(initial);
        var before = await ReadAcknowledgedBytes(fixture);
        var query = await fixture.Send(new { id = "models", type = "get_available_models" }); Success(query);
        var models = query.GetProperty("data").GetProperty("models").EnumerateArray().ToArray();
        Check(models.Select(value => value.GetProperty("id").GetString()).SequenceEqual(new[] { "first", "second" }), "Inventory reordered explicit runtime metadata.");
        Check(models[1].GetProperty("opaque").GetProperty("nil").ValueKind == JsonValueKind.Null, "Full raw metadata was lost.");
        var afterQuery = await ReadAcknowledgedBytes(fixture);
        Check(before.SequenceEqual(afterQuery), "Inventory query changed durable bytes.");
        var selected = await fixture.Send(new { id = "set", type = "set_model", provider = "fixture", modelId = "second" }); Success(selected);
        Check(selected.GetProperty("data").GetRawText() == models[1].GetRawText() && fixture.Session.Snapshot.Agent.Model == Second,
            "Model response or actual runtime selection differs from registered full metadata.");
        var afterSelection = await CheckModelAppend(fixture, initial, before, Second);
        var cycle = await fixture.Send(new { id = "cycle", type = "cycle_model" }); Success(cycle);
        var result = cycle.GetProperty("data");
        Check(result.GetProperty("model").GetProperty("id").GetString() == "first" && result.GetProperty("thinkingLevel").GetString() == "off" &&
            !result.GetProperty("isScoped").GetBoolean() && fixture.Session.Snapshot.Agent.Model == First, "Forward cycle did not wrap with pinned result shape.");
        await CheckModelAppend(fixture, afterSelection.Log, afterSelection.Bytes, First);
        Check(fixture.Transport.Requests == 0, "Query/selection sent a provider request.");
        await fixture.Dispatcher.DisposeAsync();
        await using var reopened = await PersistentAgentSession.OpenWithRegistryAsync(fixture.Session.Path, fixture.Registry, () => 123,
            () => "reopen-" + Guid.NewGuid().ToString("N"), fallbackModel: First);
        Check(reopened.Snapshot.Agent.Model == First && reopened.Snapshot.Context.ThinkingLevel == "off", "Durable selection did not reopen through the same registry.");
    }
    private static async Task Thinking()
    {
        await using var fixture = await Fixture.Create();
        var off = await fixture.Send(new { id = "off", type = "get_available_thinking_levels" }); Success(off);
        Check(off.GetProperty("data").GetProperty("levels").GetRawText() == "[\"off\"]", "Nonreasoning model did not expose only off.");
        var clamp = await fixture.Send(new { id = "clamp", type = "set_thinking_level", level = "max" }); Success(clamp);
        Check(!clamp.TryGetProperty("data", out _) && fixture.Session.Snapshot.Context.ThinkingLevel == "off", "Set-thinking shape or nonreasoning clamp changed.");
        var noCycle = await fixture.Send(new { id = "none", type = "cycle_thinking_level" }); Success(noCycle);
        Check(noCycle.GetProperty("data").ValueKind == JsonValueKind.Null, "Unsupported thinking cycle omitted its explicit null.");
        Success(await fixture.Send(new { id = "select", type = "set_model", provider = "fixture", modelId = "second" }));
        var levels = await fixture.Send(new { id = "levels", type = "get_available_thinking_levels" }); Success(levels);
        Check(levels.GetProperty("data").GetProperty("levels").GetRawText() == "[\"off\"]",
            "Legacy transport advertised catalog reasoning without operational capabilities.");
        var before = await ReadAcknowledgedBytes(fixture);
        Success(await fixture.Send(new { id = "runtime", type = "set_thinking_level", level = "low" }));
        var cycle = await fixture.Send(new { id = "thinking-cycle", type = "cycle_thinking_level" }); Success(cycle);
        Check(cycle.GetProperty("data").ValueKind == JsonValueKind.Null, "Legacy off-only binding fabricated an enabled thinking cycle.");
        try
        {
            await fixture.Session.ConfigureAsync(new(ThinkingLevel: "medium"));
            throw new InvalidOperationException("Legacy registry admitted direct non-off configuration.");
        }
        catch (SessionRuntimeRegistryException error) when (error.Failure == SessionRuntimeRegistryFailure.UnsupportedThinkingLevel) { }
        var afterRejected = await ReadAcknowledgedBytes(fixture);
        Check(before.SequenceEqual(afterRejected) && fixture.Session.Snapshot.Context.ThinkingLevel == "off" &&
            fixture.Session.Snapshot.Fault is null && fixture.Transport.Requests == 0, "Legacy clamp/refusal changed disk/runtime, poisoned the session or sent.");
    }
    private static async Task Rejections()
    {
        await using var fixture = await Fixture.Create(); var before = await ReadAcknowledgedBytes(fixture);
        foreach (var command in new object[] {
            new { id = "missing", type = "set_model", provider = "fixture", modelId = "missing" },
            new { id = "shape", type = "set_model", provider = 7, modelId = "first" },
            new { id = "absent", type = "set_model", provider = "fixture" },
            new { id = "bad-level", type = "set_thinking_level", level = "ultra" } })
            Check(!(await fixture.Send(command)).GetProperty("success").GetBoolean(), "Malformed/missing selection unexpectedly succeeded.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await fixture.Dispatcher.SubmitAsync(JsonData.Parse("{\"id\":\"canceled\",\"type\":\"set_model\",\"provider\":\"fixture\",\"modelId\":\"second\"}"), canceled.Token); throw new InvalidOperationException("Canceled command admitted."); }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        var afterRejected = await ReadAcknowledgedBytes(fixture);
        Check(before.SequenceEqual(afterRejected) && fixture.Session.Snapshot.Agent.Model == First, "Rejected command changed selection.");
        await using var bounded = await Fixture.Create(largeSecond: true, options: new(MaximumOutputBytes: 512));
        var boundedBefore = await ReadAcknowledgedBytes(bounded);
        var error = await bounded.Send(new { id = "budget", type = "set_model", provider = "fixture", modelId = "second" });
        var boundedAfter = await ReadAcknowledgedBytes(bounded);
        Check(!error.GetProperty("success").GetBoolean() && boundedBefore.SequenceEqual(boundedAfter) &&
            bounded.Session.Snapshot.Agent.Model == First, "Oversized complete response was discovered after durable mutation.");
    }
    private static async Task Active()
    {
        await using var fixture = await Fixture.Create(); fixture.Transport.Hold = true;
        Success(await fixture.Send(new { id = "prompt", type = "prompt", message = "authored held run" }));
        await fixture.Transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var before = await ReadAcknowledgedBytes(fixture);
        Success(await fixture.Send(new { id = "query", type = "get_available_models" }));
        var mutation = await fixture.Send(new { id = "busy", type = "set_model", provider = "fixture", modelId = "second" });
        var afterMutation = await ReadAcknowledgedBytes(fixture);
        Check(!mutation.GetProperty("success").GetBoolean() && fixture.Session.Snapshot.Agent.Model == First &&
            before.SequenceEqual(afterMutation), "Model mutation crossed the active run fence.");
        Success(await fixture.Send(new { id = "abort", type = "abort" }));
        await fixture.Transport.Joined.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(fixture.Transport.Requests == 1, "Model query or rejected mutation started an extra request.");
    }
    private static async Task Singleton()
    {
        await using var fixture = await Fixture.Create(singleton: true); var before = await ReadAcknowledgedBytes(fixture);
        var initial = fixture.Session.Snapshot.Log; CheckInitialModelCheckpoint(initial);
        var cycle = await fixture.Send(new { id = "cycle", type = "cycle_model" }); Success(cycle);
        var afterCycle = await ReadAcknowledgedBytes(fixture);
        Check(cycle.GetProperty("data").ValueKind == JsonValueKind.Null && before.SequenceEqual(afterCycle), "Singleton cycling mutated or omitted null.");
        Success(await fixture.Send(new { id = "same", type = "set_model", provider = "fixture", modelId = "first" }));
        await CheckModelAppend(fixture, initial, afterCycle, First);
        Check(fixture.Transport.Requests == 0, "Singleton cycle/selection sent a provider request.");
    }
    private static void CheckInitialModelCheckpoint(SessionLogStoreSnapshot log)
    {
        Check(log.Entries.Length == 2, "Creation must acknowledge initial model and thinking records.");
        var model = log.Entries[0]; var thinking = log.Entries[1];
        var modelWire = model.WireBody.Value; var thinkingWire = thinking.WireBody.Value;
        Check(modelWire.GetProperty("type").GetString() == "model_change" &&
            modelWire.GetProperty("provider").GetString() == First.Provider && modelWire.GetProperty("modelId").GetString() == First.Id &&
            modelWire.GetProperty("parentId").ValueKind == JsonValueKind.Null &&
            thinkingWire.GetProperty("type").GetString() == "thinking_level_change" &&
            thinkingWire.GetProperty("thinkingLevel").GetString() == "off" &&
            thinkingWire.GetProperty("parentId").GetString() == model.Id && log.LeafId == thinking.Id,
            "Creation checkpoint must link the initial model to thinking off before RPC selection.");
    }
    private static async Task<(SessionLogStoreSnapshot Log, byte[] Bytes)> CheckModelAppend(
        Fixture fixture, SessionLogStoreSnapshot before, byte[] beforeBytes, ModelDescriptor expected)
    {
        var bytes = await ReadAcknowledgedBytes(fixture); var after = fixture.Session.Snapshot.Log;
        Check(bytes.Length > beforeBytes.Length && bytes.AsSpan(0, beforeBytes.Length).SequenceEqual(beforeBytes),
            "Model selection did not append to the unchanged acknowledged byte prefix.");
        Check(after.Entries.Length == before.Entries.Length + 1 &&
            after.Entries.Take(before.Entries.Length).Select(entry => entry.WireBody.Value.GetRawText()).SequenceEqual(
                before.Entries.Select(entry => entry.WireBody.Value.GetRawText())),
            "Model selection must preserve the checkpoint and append exactly one entry.");
        var added = after.Entries[^1];
        Check(!before.Entries.Any(entry => entry.Id == added.Id) && after.LeafId == added.Id &&
            fixture.Session.Snapshot.Agent.Model == expected, "Model acknowledgement has a reused ID, wrong leaf or wrong runtime model.");
        void CheckRecord(JsonElement record) => Check(record.GetProperty("type").GetString() == "model_change" &&
            record.GetProperty("id").GetString() == added.Id && record.GetProperty("parentId").GetString() == before.LeafId &&
            record.GetProperty("provider").GetString() == expected.Provider && record.GetProperty("modelId").GetString() == expected.Id,
            "Appended model acknowledgement has the wrong type, identity, parent or selection.");
        CheckRecord(added.WireBody.Value);
        var lines = Encoding.UTF8.GetString(bytes, beforeBytes.Length, bytes.Length - beforeBytes.Length)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length == 1, "Model selection must append exactly one durable JSONL record.");
        CheckRecord(JsonData.Parse(lines[0]).Value);
        return (after, bytes);
    }
    private static async Task<byte[]> ReadAcknowledgedBytes(Fixture fixture)
    {
        // Callers await the original command response and admit no further work during this read.
        // Start reserves Provider phase even without auto recovery. Only the verified held transport
        // permits reading while that operation owns the session, before any assistant commit.
        bool HeldProvider(PersistentAgentSessionSnapshot snapshot) =>
            snapshot.IsProcessingOperation && snapshot.OperationPhase == SessionOperationPhase.Provider &&
            snapshot.OperationGeneration > 0 && snapshot.Agent.IsRunning && fixture.Transport.Hold &&
            fixture.Transport.Entered.Task.IsCompletedSuccessfully && !fixture.Transport.Release.Task.IsCompleted &&
            !fixture.Transport.Joined.Task.IsCompleted;
        bool ReadableState(PersistentAgentSessionSnapshot snapshot) =>
            (!snapshot.IsProcessingOperation && snapshot.OperationPhase == SessionOperationPhase.Idle && !snapshot.Agent.IsRunning) ||
            HeldProvider(snapshot);
        var session = fixture.Session; var acknowledged = session.Snapshot;
        Check(ReadableState(acknowledged) && !acknowledged.IsCompacting && !acknowledged.IsConfiguring &&
            !acknowledged.IsEditingContext && acknowledged.Fault is null &&
            acknowledged.Log.StorageDurability == SessionLogStorageDurability.LocalFileFlush,
            "Fixture read lacks an idle checkpoint or verified held Provider operation.");
        Check(acknowledged.Log.CommittedByteLength is >= 0 and <= 8_388_608, "Fixture acknowledged bytes exceed the read bound.");
        await using var reader = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Check(reader.Length == acknowledged.Log.CommittedByteLength, "File length differs from acknowledged bytes.");
        var bytes = new byte[checked((int)acknowledged.Log.CommittedByteLength)];
        await reader.ReadExactlyAsync(bytes);
        var after = session.Snapshot;
        Check(reader.Position == bytes.Length && reader.Length == bytes.Length &&
            after.Log.Sequence == acknowledged.Log.Sequence && after.Log.LeafId == acknowledged.Log.LeafId &&
            after.Log.CommittedByteLength == acknowledged.Log.CommittedByteLength && after.Fault is null &&
            after.OperationGeneration == acknowledged.OperationGeneration && after.OperationPhase == acknowledged.OperationPhase &&
            after.IsProcessingOperation == acknowledged.IsProcessingOperation && after.Agent.Generation == acknowledged.Agent.Generation &&
            ReadableState(after) && !after.IsCompacting && !after.IsConfiguring && !after.IsEditingContext,
            "Fixture operation or checkpoint changed during the owned shared read.");
        return bytes;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly HeldTransport Transport = new();
        internal readonly MemoryStream Output = new();
        internal SessionRuntimeRegistry Registry = null!;
        internal PersistentAgentSession Session = null!;
        internal RpcSessionDispatcher Dispatcher = null!;
        internal static async Task<Fixture> Create(bool singleton = false, bool largeSecond = false, RpcDispatchOptions? options = null)
        {
            var fixture = new Fixture(); var ids = 0;
            var directory = Path.Combine(Path.GetTempPath(), "PiSharp-rpc-model-thinking-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var models = singleton ? ImmutableArray.Create(First) : ImmutableArray.Create(First, Second);
            fixture.Registry = new(models.Select(model => new SessionModelBinding(model, fixture.Transport)).ToImmutableArray(), [], new DenyPolicy());
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "model-thinking-header",
                timestamp = "2026-10-01T00:00:00.000Z", cwd = directory }));
            fixture.Session = await PersistentAgentSession.CreateAsync(Path.Combine(directory, "session.jsonl"), header,
                fixture.Registry, First, () => 123, () => "selection-" + Interlocked.Increment(ref ids));
            try
            {
                fixture.Dispatcher = new(fixture.Session, new JsonlWriter(fixture.Output, ownership: JsonlStreamOwnership.Borrowed), () => 123,
                    models.Select(model => new RpcModelDefinition(model, Wire(model, largeSecond && model == Second))).ToImmutableArray(), options);
                return fixture;
            }
            catch { await fixture.Session.DisposeAsync(); fixture.Output.Dispose(); throw; }
        }
        internal async Task<JsonElement> Send(object command)
        {
            var raw = JsonData.Parse(JsonSerializer.Serialize(command));
            await Dispatcher.SubmitAsync(raw);
            var id = raw.Value.GetProperty("id").GetString();
            return Encoding.UTF8.GetString(Output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonData.Parse(line).Value).Single(value => value.GetProperty("type").GetString() == "response" &&
                    value.TryGetProperty("id", out var identity) && identity.GetString() == id);
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Release.TrySetResult();
            try { await Dispatcher.DisposeAsync(); }
            finally { await Session.DisposeAsync(); Output.Dispose(); }
            // Preserve the small durable fixture for coordinator evidence; no recursive cleanup or detached join.
        }
    }
    private static JsonData Wire(ModelDescriptor model, bool large) => JsonData.Parse(JsonSerializer.Serialize(new
    {
        id = model.Id, api = model.Api, provider = model.Provider, name = large ? new string('x', 2048) : model.Id,
        baseUrl = "https://offline.invalid", reasoning = model == Second, input = new[] { "text" }, contextWindow = 32768, maxTokens = 8192,
        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
        thinkingLevelMap = model == Second ? new Dictionary<string, string?> { ["low"] = null, ["xhigh"] = "high", ["max"] = null } : null,
        opaque = new { nil = (string?)null }
    }));
    private sealed class DenyPolicy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(false));
    }
    private sealed class HeldTransport : IChatTransport
    {
        internal bool Hold; internal int Requests;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests++; var message = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, 123, [], TokenUsage.Zero, StopReason.Stop);
            try
            {
                yield return new StreamStarted(message with { StopReason = StopReason.Pending }); Entered.TrySetResult();
                if (Hold) await Release.Task.WaitAsync(token);
                yield return new StreamDone(StopReason.Stop, message);
            }
            finally { Joined.TrySetResult(); }
        }
    }
}