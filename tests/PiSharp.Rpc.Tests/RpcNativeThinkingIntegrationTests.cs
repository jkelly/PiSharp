using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RpcNativeThinkingIntegrationTests
{
    private static readonly ModelDescriptor Responses = new("native-rpc-thinking", "openai-responses", "openai");
    private static readonly ModelDescriptor BoundedAnthropic = new("native-rpc-capped", "anthropic-messages", "anthropic");
    private static readonly ModelDescriptor Unbound = new("metadata-only", "openai-responses", "openai");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (RpcModelThinkingTests.Prefix + "native-supported-control-reaches-http-stamp-and-reopen", Supported),
        (RpcModelThinkingTests.Prefix + "native-selected-binding-cap-clamp-and-cycle", SelectedBinding),
        (RpcModelThinkingTests.Prefix + "native-unbound-selection-and-cap-bypass-fail-closed", FailClosed),
        (RpcModelThinkingTests.Prefix + "native-no-off-query-switch-clamp-and-cycle", WithoutOff)
    ];
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Success(JsonElement response) => Check(response.GetProperty("success").GetBoolean(), response.GetRawText());
    private static async Task Supported()
    {
        await using var fixture = await Fixture.Create();
        var levels = await fixture.Send(new { id = "levels", type = "get_available_thinking_levels" }); Success(levels);
        Check(levels.GetProperty("data").GetProperty("levels").EnumerateArray().Select(value => value.GetString()).SequenceEqual(
            new[] { "off", "minimal", "medium", "high", "xhigh" }), "RPC did not expose admitted native Responses capabilities.");
        var set = await fixture.Send(new { id = "set", type = "set_thinking_level", level = "low" }); Success(set);
        Check(!set.TryGetProperty("data", out _) && fixture.Session.Snapshot.Context.ThinkingLevel == "medium", "Excluded low did not clamp upward to admitted medium.");
        var cycle = await fixture.Send(new { id = "cycle", type = "cycle_thinking_level" }); Success(cycle);
        Check(cycle.GetProperty("data").GetProperty("level").GetString() == "high", "Supported native thinking cycle has the wrong shape/state.");
        Success(await fixture.Send(new { id = "mapped", type = "set_thinking_level", level = "xhigh" }));
        Check(fixture.Handler.Calls == 0, "Thinking selection or query sent before prompting.");
        Success(await fixture.Send(new { id = "prompt", type = "prompt", message = "authored native control fixture" }));
        await fixture.Session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.JoinDispatcherRun();
        Check(fixture.Handler.Calls == 1 && fixture.Handler.LastPayload.GetProperty("reasoning").GetProperty("effort").GetString() == "high",
            "The actual HTTP request lost configured xhigh or its admitted wire mapping.");
        Check(fixture.Handler.LastBody?.Disposed == true, "Original native response body did not join/dispose.");
        var assistant = fixture.Session.Snapshot.Context.Messages.Last(message => message.Role == "assistant").WireBody.Value;
        Check(assistant.GetProperty("thinkingLevel").GetString() == "xhigh" && assistant.GetProperty("stopReason").GetString() == "stop",
            "Final durable assistant did not retain the configured native control.");
        var selected = fixture.Session.Path;
        await fixture.Dispatcher.DisposeAsync();
        await using var reopened = await PersistentAgentSession.OpenWithRegistryAsync(selected, fixture.Registry, () => 123,
            () => "native-reopen-" + Guid.NewGuid().ToString("N"), fallbackModel: Responses);
        Check(reopened.Snapshot.Context.ThinkingLevel == "xhigh" && reopened.Snapshot.Agent.Model == Responses,
            "Acknowledged native thinking selection did not survive registry-backed reopen.");
        Check(fixture.Handler.Calls == 1 && !fixture.Handler.Disposed, "Reopen sent or provider composition disposed the borrowed handler.");
    }
    private static async Task SelectedBinding()
    {
        await using var fixture = await Fixture.Create();
        Success(await fixture.Send(new { id = "high", type = "set_thinking_level", level = "high" }));
        var selected = await fixture.Send(new { id = "select", type = "set_model", provider = "anthropic", modelId = BoundedAnthropic.Id }); Success(selected);
        Check(selected.GetProperty("data").GetProperty("reasoning").GetBoolean() && fixture.Session.Snapshot.Context.ThinkingLevel == "off",
            "Model selection clamped against catalog reasoning instead of the selected 1024-token native cap.");
        var levels = await fixture.Send(new { id = "capped-levels", type = "get_available_thinking_levels" }); Success(levels);
        Check(levels.GetProperty("data").GetProperty("levels").GetRawText() == "[\"off\"]", "Capped native binding advertised unavailable reasoning levels.");
        var thinkingCycle = await fixture.Send(new { id = "no-thinking-cycle", type = "cycle_thinking_level" }); Success(thinkingCycle);
        Check(thinkingCycle.GetProperty("data").ValueKind == JsonValueKind.Null, "Off-only native binding fabricated an enabled thinking cycle.");
        Success(await fixture.Send(new { id = "capped-set", type = "set_thinking_level", level = "max" }));
        Check(fixture.Session.Snapshot.Context.ThinkingLevel == "off", "Capped binding bypassed native operational clamp.");
        var cycle = await fixture.Send(new { id = "model-cycle", type = "cycle_model" }); Success(cycle);
        Check(cycle.GetProperty("data").GetProperty("model").GetProperty("id").GetString() == Responses.Id &&
            cycle.GetProperty("data").GetProperty("thinkingLevel").GetString() == "off" && fixture.Handler.Calls == 0,
            "Model cycle did not use the target native binding or sent without a prompt.");
    }
    private static async Task FailClosed()
    {
        await using var fixture = await Fixture.Create(includeUnbound: true);
        var prior = fixture.Session.Snapshot;
        var missing = await fixture.Send(new { id = "unbound", type = "set_model", provider = "openai", modelId = Unbound.Id });
        Check(!missing.GetProperty("success").GetBoolean() && fixture.Session.Snapshot.Agent.Model == Responses &&
            fixture.Session.Snapshot.Log.Sequence == prior.Log.Sequence && fixture.Session.Snapshot.Log.CommittedByteLength == prior.Log.CommittedByteLength,
            "A metadata-only model changed runtime/durable selection without an operational binding.");
        Success(await fixture.Send(new { id = "cap", type = "set_model", provider = "anthropic", modelId = BoundedAnthropic.Id }));
        var capped = fixture.Session.Snapshot;
        // agent-session.ts setThinkingLevel clamps to the native cap: "high" on the off-only binding selects "off", which is no change.
        await fixture.Session.ConfigureAsync(new(ThinkingLevel: "high"));
        var after = fixture.Session.Snapshot;
        Check(after.Context.ThinkingLevel == "off" && after.Fault is null && after.Log.Sequence == capped.Log.Sequence &&
            after.Log.LeafId == capped.Log.LeafId && after.Log.CommittedByteLength == capped.Log.CommittedByteLength && fixture.Handler.Calls == 0,
            "Direct unsupported native configuration wrote a checkpoint, poisoned the session or sent.");
    }
    private static async Task WithoutOff()
    {
        await using var fixture = await Fixture.Create(withoutOff: true);
        Check(fixture.Session.Snapshot.Agent.Model == BoundedAnthropic && fixture.Session.Snapshot.Context.ThinkingLevel == "off",
            "No-off fixture must begin on the distinct off-only binding.");
        var selected = await fixture.Send(new { id = "no-off-select", type = "set_model", provider = Responses.Provider, modelId = Responses.Id }); Success(selected);
        Check(fixture.Session.Snapshot.Agent.Model == Responses && fixture.Session.Snapshot.Context.ThinkingLevel == "low",
            "Switch from off did not clamp upward against the selected low/high-only binding.");
        var levels = await fixture.Send(new { id = "no-off-levels", type = "get_available_thinking_levels" }); Success(levels);
        Check(levels.GetProperty("data").GetProperty("levels").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "low", "high" }),
            "RPC inserted off or exposed another model's capabilities.");
        foreach (var requested in new[] { "off", "minimal", "medium", "max" })
        {
            var result = await fixture.Send(new { id = "no-off-clamp-" + requested, type = "set_thinking_level", level = requested }); Success(result);
            Check(!result.TryGetProperty("data", out _) && fixture.Session.Snapshot.Context.ThinkingLevel == (requested is "off" or "minimal" ? "low" : "high"),
                "RPC failed upward/downward clamping within the actual admitted domain.");
        }
        var wrap = await fixture.Send(new { id = "no-off-wrap", type = "cycle_thinking_level" }); Success(wrap);
        Check(wrap.GetProperty("data").GetProperty("level").GetString() == "low", "High did not wrap to low without inserting off.");
        var next = await fixture.Send(new { id = "no-off-next", type = "cycle_thinking_level" }); Success(next);
        Check(next.GetProperty("data").GetProperty("level").GetString() == "high", "Low did not cycle to high.");
        var before = fixture.Session.Snapshot;
        // agent-session.ts setThinkingLevel: an explicit unsupported "off" is clamped up to "low" and recorded as that change.
        await fixture.Session.ConfigureAsync(new(ThinkingLevel: "off"));
        var after = fixture.Session.Snapshot;
        Check(after.Log.Entries.Length == before.Log.Entries.Length + 1 && after.Log.Entries[^1].Kind == SessionEntryKind.ThinkingLevelChange &&
            after.Log.Entries[^1].WireBody.Value.GetProperty("thinkingLevel").GetString() == "low" &&
            after.Context.ThinkingLevel == "low" && after.Agent.Model == Responses && after.Fault is null,
            "Explicit unsupported off was not clamped to low and recorded.");
        var other = await fixture.Send(new { id = "no-off-other", type = "cycle_model" }); Success(other);
        Check(other.GetProperty("data").GetProperty("model").GetProperty("id").GetString() == BoundedAnthropic.Id &&
            other.GetProperty("data").GetProperty("thinkingLevel").GetString() == "off", "Model cycle ignored the off-only target.");
        var back = await fixture.Send(new { id = "no-off-back", type = "cycle_model" }); Success(back);
        Check(back.GetProperty("data").GetProperty("model").GetProperty("id").GetString() == Responses.Id &&
            back.GetProperty("data").GetProperty("thinkingLevel").GetString() == "low" && fixture.Handler.Calls == 0,
            "Wrapped model cycle failed target-specific clamping or acquired HTTP.");
    }
    private static async Task<byte[]> ReadIdleAcknowledgedBytes(PersistentAgentSession session)
    {
        var before = session.Snapshot;
        bool Idle(PersistentAgentSessionSnapshot snapshot) => !snapshot.IsProcessingOperation && !snapshot.Agent.IsRunning &&
            !snapshot.IsConfiguring && !snapshot.IsEditingContext && !snapshot.IsCompacting && snapshot.Fault is null;
        Check(Idle(before) && before.Log.StorageDurability == SessionLogStorageDurability.LocalFileFlush &&
            before.Log.CommittedByteLength is >= 0 and <= 8_388_608, "Read requires a bounded idle durable checkpoint.");
        await using var reader = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Check(reader.Length == before.Log.CommittedByteLength, "Physical and acknowledged lengths differ.");
        var bytes = new byte[checked((int)before.Log.CommittedByteLength)]; await reader.ReadExactlyAsync(bytes);
        var after = session.Snapshot;
        Check(Idle(after) && reader.Position == bytes.Length && reader.Length == bytes.Length &&
            after.Log.Sequence == before.Log.Sequence && after.Log.LeafId == before.Log.LeafId &&
            after.Log.CommittedByteLength == before.Log.CommittedByteLength && after.OperationGeneration == before.OperationGeneration &&
            after.OperationPhase == before.OperationPhase && after.Agent.Generation == before.Agent.Generation,
            "Checkpoint changed during the owned shared read.");
        return bytes;
    }
    private static JsonData Metadata(ModelDescriptor model, bool withoutOff = false) => JsonData.Parse(JsonSerializer.Serialize(new
    {
        id = model.Id, api = model.Api, provider = model.Provider, name = model.Id,
        baseUrl = model.Provider == "anthropic" ? "https://api.anthropic.com" : "https://api.openai.com/v1",
        reasoning = true, input = new[] { "text" }, contextWindow = 200000, maxTokens = 8192,
        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
        thinkingLevelMap = withoutOff
            ? new Dictionary<string, string?> { ["off"] = null, ["minimal"] = null, ["medium"] = null, ["xhigh"] = null, ["max"] = null }
            : new Dictionary<string, string?> { ["low"] = null, ["xhigh"] = "high", ["max"] = null }
    }));
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Handler Handler = new();
        private readonly Capture Output = new();
        private readonly List<NativeHttpModelProvider> providers = [];
        internal SessionRuntimeRegistry Registry = null!;
        internal PersistentAgentSession Session = null!;
        internal RpcSessionDispatcher Dispatcher = null!;
        internal static async Task<Fixture> Create(bool includeUnbound = false, bool withoutOff = false)
        {
            var fixture = new Fixture(); var ids = 0;
            try
            {
                var responseProvider = NativeProviderFactory.CreateResponses(Responses, new("https://api.openai.com/v1/responses"),
                    "inert-native-rpc-key", new(Reasoning: true), new(SupportsMaxOutputTokens: true, MaxOutputTokens: 256), fixture.Handler, Metadata(Responses, withoutOff));
                fixture.providers.Add(responseProvider);
                var boundedProvider = NativeProviderFactory.CreateAnthropic(BoundedAnthropic, new("https://api.anthropic.com/"),
                    "inert-native-rpc-key", new(MaximumTokens: 1024, ModelReasoning: true), new(MaxTokens: 1024), fixture.Handler, Metadata(BoundedAnthropic));
                fixture.providers.Add(boundedProvider);
                fixture.Registry = new([new(Responses, responseProvider.Transport), new(BoundedAnthropic, boundedProvider.Transport)], [], new DenyPolicy());
                var directory = Path.Combine(Path.GetTempPath(), "PiSharp-rpc-native-thinking-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "native-rpc-header",
                    timestamp = "2026-10-01T00:00:00.000Z", cwd = directory }));
                fixture.Session = await PersistentAgentSession.CreateAsync(Path.Combine(directory, "session.jsonl"), header, fixture.Registry,
                    withoutOff ? BoundedAnthropic : Responses, () => 123, () => "native-rpc-" + Interlocked.Increment(ref ids));
                var definitions = ImmutableArray.Create(new RpcModelDefinition(Responses, Metadata(Responses, withoutOff)), new RpcModelDefinition(BoundedAnthropic, Metadata(BoundedAnthropic)));
                if (includeUnbound) definitions = definitions.Add(new(Unbound, Metadata(Unbound)));
                fixture.Dispatcher = new(fixture.Session, new JsonlWriter(fixture.Output, ownership: JsonlStreamOwnership.Borrowed), () => 123, definitions);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal async Task<JsonElement> Send(object command)
        {
            var raw = JsonData.Parse(JsonSerializer.Serialize(command)); await Dispatcher.SubmitAsync(raw);
            var id = raw.Value.GetProperty("id").GetString();
            return Encoding.UTF8.GetString(Output.Bytes()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonData.Parse(line).Value)
                .Single(value => value.GetProperty("type").GetString() == "response" && value.TryGetProperty("id", out var identity) && identity.GetString() == id);
        }
        internal async Task JoinDispatcherRun()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            for (var attempt = 0; ; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var state = await Send(new { id = "settlement-" + attempt, type = "get_state" }); Success(state);
                if (state.GetProperty("data").GetProperty("pisharpRunOwnerSettled").GetBoolean()) return;
                await Task.Delay(1, deadline.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            if (Dispatcher is not null) try { await Dispatcher.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            if (Session is not null) try { await Session.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            foreach (var provider in providers) try { provider.Dispose(); } catch (Exception error) { failures.Add(error); }
            providers.Clear(); Output.Dispose(); Handler.Dispose();
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }
    private sealed class DenyPolicy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(false));
    }
    private sealed class Capture : MemoryStream
    {
        private readonly object gate = new();
        internal byte[] Bytes() { lock (gate) return ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); lock (gate) Write(buffer.Span); return ValueTask.CompletedTask; }
    }
    private sealed class Body(byte[] bytes) : MemoryStream(bytes)
    {
        internal bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls; internal bool Disposed; internal JsonElement LastPayload; internal Body? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri == new Uri("https://api.openai.com/v1/responses") && request.Headers.Authorization?.Parameter == "inert-native-rpc-key",
                "Native fixture bypassed the inert intercepted route.");
            Calls++; LastPayload = JsonData.Parse(await request.Content!.ReadAsStringAsync(token)).Value;
            const string wire = "data: {\"type\":\"response.created\",\"response\":{\"id\":\"native-response\"}}\n\n" +
                "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"native-message\",\"role\":\"assistant\",\"content\":[]}}\n\n" +
                "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"native thinking\"}\n\n" +
                "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"native-message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"native thinking\"}]}}\n\n" +
                "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"native-response\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"id\":\"native-message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"native thinking\"}]}]}}\n\n";
            LastBody = new(Encoding.UTF8.GetBytes(wire));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(LastBody) };
            response.Content.Headers.ContentType = new("text/event-stream"); return response;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}