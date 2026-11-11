// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/coding-agent/src/core/agent-session.ts.
// Authored native regression; not an upstream execution capture.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Commands;
using PiSharp.AI.Catalogs;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Usage;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Tools.Images;

internal static class QueuedImageNormalizationTests
{
    private static readonly ModelDescriptor Model = new("image-delivery", "openai-responses", "fixture");
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("queued images preserve nonimage blocks and apply current model limits", Content),
        ("queued image-only duplicate texts deliver independently before events persistence and transport", Delivery),
        ("queued image-only entry can be removed by owned queue snapshot", Removal),
        ("cache warming idle default and independent replay preserve original request", Replay),
        ("cache warming forwards environment retention to original replay and summary requests", Retention),
        ("cache warming attachment retirement joins replay before resources and leaves current warmer alive", Retirement)
    ];

    private static Task Content()
    {
        var codec = new Codec(); var original = Image("same", "first");
        var normalized = PromptImageNormalizer.Normalize(original, true, Limits(500), codec);
        Check(!ReferenceEquals(original, normalized), "Image did not normalize.");
        var body = normalized.WireBody.Value; var blocks = body.GetProperty("content");
        Check(body.GetProperty("tag").GetString() == "first", "Message metadata was lost.");
        Check(blocks[1].GetProperty("opaque").GetInt32() == 7 && blocks[1].GetProperty("text").GetString() == "keep", "Non-image block changed.");
        Check(blocks[0].GetProperty("opaque").GetString() == "image metadata", "Image metadata was lost.");
        Check(blocks[2].GetProperty("text").GetString()!.Contains("displayed at 500x125", StringComparison.Ordinal), "Model resize limit was ignored.");
        Check(codec.Widths.SequenceEqual([500]), "Unexpected resize.");
        Check(codec.Qualities.SequenceEqual([95]), "Model JPEG quality was ignored.");
        Check(ReferenceEquals(normalized, PromptImageNormalizer.Normalize(normalized, false, Limits(500), codec)), "Unchanged values lost identity.");
        var invalid = new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":[{\"type\":\"image\",\"data\":\"!\",\"mimeType\":\"image/png\"}]}"));
        var omitted = PromptImageNormalizer.Normalize(invalid, true, null, codec);
        Check(omitted.WireBody.Value.GetProperty("content")[0].GetProperty("type").GetString() == "text", "Invalid image omission missing.");
        return Task.CompletedTask;
    }

    private static async Task Delivery()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-image-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = new Script(); var ids = 0;
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = "image-delivery-header", timestamp = "2026-11-10T00:00:00.000Z", cwd = root }));
            await using var session = await PersistentAgentSession.CreateAsync(Path.Combine(root, "session.jsonl"), header,
                new(Model, script, []), () => 1, () => "entry-" + ++ids);
            var codec = new Codec(); var width = 2000;
            session.InputNormalizer = (model, message, _) =>
            { Check(model == Model, "Wrong delivery model."); return ValueTask.FromResult(PromptImageNormalizer.Normalize(message, true, Limits(width), codec)); };
            var started = new List<TranscriptEntry>();
            using var subscription = session.Subscribe(new Sink(observation =>
            { if (observation is AgentLoopInputMessageStarted input) started.Add(input.Message); }));
            var first = Image("", "first", includeOpaqueBlock: false); var second = Image("", "second", includeOpaqueBlock: false);
            session.Steer(first); session.Steer(second);
            Check(codec.Widths.Count == 0, "Image was processed while enqueued.");
            width = 500;
            await session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"start\",\"timestamp\":1}")));
            Check(session.Snapshot.Agent.SteeringCount == 0 && session.Snapshot.Agent.PendingInputs.IsEmpty, "Delivered image-only values remain queued/pending.");
            var delivered = started.Where(message => message.WireBody.Value.TryGetProperty("tag", out _)).ToArray();
            Check(delivered.Length == 2 && delivered[0].WireBody.Value.GetProperty("tag").GetString() == "first" &&
                delivered[1].WireBody.Value.GetProperty("tag").GetString() == "second", "Duplicate-text image identities/order changed.");
            foreach (var value in delivered)
            {
                Check(value.WireBody.Value.GetProperty("content")[0].GetProperty("data").GetString() == "AQI=", "Public event saw raw image.");
                Check(session.Snapshot.Agent.Messages.Any(message => ReferenceEquals(message, value)), "Agent history differs from event.");
                Check(session.Snapshot.Context.LlmMessages.Any(message => message.WireBody.Value.GetRawText() == value.WireBody.Value.GetRawText()), "Persistence differs from event.");
                Check(script.Requests.Any(request => request.Messages.Any(message => ReferenceEquals(message, value))), "Transport differs from event.");
            }
            Check(codec.Widths.SequenceEqual([500, 500]), "Delivery did not use current resize limits.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task Removal()
    {
        var queue = new AgentPendingInputQueue(); var image = Image("", "remove"); queue.EnqueueSteering(image);
        var snapshot = queue.GetSnapshot(); Check(queue.TryClearAndSnapshot(snapshot, out var removed) &&
            ReferenceEquals(removed!.SteeringMessages[0], image) && !queue.HasQueuedMessages, "Image-only queue removal failed.");
        return Task.CompletedTask;
    }

    private static Task Replay()
    {
        Check(CacheWarmingModes.Resolve(null) == "idle" && CacheWarmingModes.Resolve("invalid") == "idle", "Incorrect warming default.");
        var request = new ChatRequest(Model, [] ) { ThinkingLevel = "high", SessionId = "session", DisableContinuation = false };
        var replay = PiSharp.AI.ModelOperations.CacheWarmingPolicy.PrepareReplay(request);
        Check(replay.DisableContinuation && !request.DisableContinuation && replay.SessionId == request.SessionId && replay.ThinkingLevel == "high",
            "Replay changed the original or lost options.");
        return Task.CompletedTask;
    }

    private static async Task Retirement()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-warm-retirement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "session.jsonl");
        var profile = await OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], default);
        var released = new TaskCompletionSource<AssistantMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposals = new List<long>(); var ids = 0;
        try
        {
            var lifecycle = new PersistentSessionLifecycle(profile.Registry, () => 1, () => "warm-entry-" + ++ids,
                runtimeForAttachment: (_, generation, _) =>
                {
                    var ownership = profile.CaptureInitialRuntimeViewOwnership(new Cleanup(() => disposals.Add(generation)), generation);
                    return ValueTask.FromResult(new SessionRuntimeLease(profile.Registry, ownership.Resources, ownership.BindOwner));
                });
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
            { type = "session", version = 3, id = "warm-retirement", timestamp = "2026-11-10T00:00:00Z", cwd = root }));
            var session = await lifecycle.CreateAsync(path, header, profile.SelectedModel);
            await profile.AttachOwnerAsync(session, lifecycle: lifecycle);
            var owner = profile.Sessions!; var previous = owner.Current;
            var time = new WarmTime(); var replayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var warmer = new CacheWarmer(async (_, _, token) =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                replayEntered.TrySetResult(); return await released.Task;
            }, (_, _, _, _, _) => throw new InvalidOperationException("Retired warm must not append usage."),
                () => [], () => CacheWarmingModes.Idle, _ => Task.FromResult("warm"), time, _ => null);
            var warmers = (ConditionalWeakTable<PersistentAgentSession, CacheWarmer>)typeof(OfflineSessionProfile)
                .GetField("_cacheWarmers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(profile)!;
            warmers.Add(session, warmer);
            var request = new CacheWarmRequest(new("cache", "fixture", "openai-responses", new(1, 1, 0.1, 0), 20), new());
            warmer.Start(request, () => true); time.Fire(); await replayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var switching = owner.CreateAsync(previous, new(AgentSessionCreationKind.New));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!disposals.Contains(previous.Generation), "Retired provider disposed while replay original remained active.");
            Check(!session.DisposeAsync().IsCompleted, "Retired session did not join active warm replay.");
            var current = owner.Current;
            Check(!ReferenceEquals(current.Session, session), "Replacement did not publish before old cleanup.");
            var currentTime = new WarmTime();
            var currentWarmer = new CacheWarmer((_, _, _) => throw new InvalidOperationException("Current timer must not fire."),
                (_, _, _, _, _) => throw new InvalidOperationException(), () => [], () => CacheWarmingModes.Idle,
                timeProvider: currentTime, processEnvironment: _ => null);
            warmers.Add(current.Session, currentWarmer); currentWarmer.Start(request, () => true);
            released.TrySetResult(new(Model.Api, Model.Provider, Model.Id, 1, [], TokenUsage.Zero, StopReason.Stop));
            await switching; await session.DisposeAsync();
            Check(disposals.Count(value => value == previous.Generation) == 1, "Old native owner was not disposed exactly once.");
            Check(currentTime.IsPending, "Old attachment retirement cancelled the current warmer.");
        }
        finally
        {
            released.TrySetResult(new(Model.Api, Model.Provider, Model.Id, 1, [], TokenUsage.Zero, StopReason.Stop));
            await profile.DisposeAsync(); Directory.Delete(root, true);
        }
    }

    private sealed class Cleanup(Action close) : IAsyncDisposable
    { public ValueTask DisposeAsync() { close(); return ValueTask.CompletedTask; } }
    private sealed class WarmTime : TimeProvider
    {
        private Timer? timer;
        private long now;
        internal bool IsPending => timer?.Active == true;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(now);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => timer = new(callback, state);
        internal void Fire() { now += 10000; timer!.Fire(); }
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal bool Active => !disposed;
            internal void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static async Task Retention()
    {
        foreach (var api in new[] { "openai-responses", "openai-completions" })
        {
            var raw = new { type = "chat", id = "gpt-5", name = "fixture", api, provider = "openai", baseUrl = "https://api.openai.com/v1",
                reasoning = false, input = new[] { "text" }, contextWindow = 100000, maxTokens = 4096,
                cost = new { input = 1, output = 1, cacheRead = 0.1, cacheWrite = 0 }, promptCache = new { @short = 300, @long = 86400 } };
            var catalog = FrozenModelCatalog.ReadProviderJson("openai", JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            { [api] = new Dictionary<string, object> { ["chat:gpt-5"] = raw } }));
            Check(catalog.TryGetModel(CatalogModelType.Chat, "gpt-5", out var definition), "Fixture model missing.");
            var selection = LiveSessionSelection.FromDefinition(definition!, 100);
            using var handler = new CaptureHandler();
            using var connection = selection.Connect(new(name => name switch
            { "OPENAI_API_KEY" => "fixture-only", "PI_CACHE_RETENTION" => "long", _ => null }, () => handler));
            var request = new ChatRequest(selection.Model, [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"hello\",\"timestamp\":1}"))]) { SessionId = "cache-fixture" };
            Check(connection.CacheWarmingOptions(request).CacheRetention == "long", "Warmer lost environment retention.");
            foreach (var transport in new[] { connection.CreateTransport(), connection.CreateCacheWarmTransport(), connection.CreateTransport(summary: true) })
                await foreach (var _ in transport.StreamAsync(request)) { }
            Check(handler.Payloads.Count == 3, "Unexpected provider request count.");
            foreach (var index in new[] { 0, 1 })
                Check(handler.Payloads[index].Value.GetProperty("prompt_cache_retention").GetString() == "24h", "Main/replay cache retention mismatch.");
            var warm = handler.Payloads[1].Value;
            var max = warm.TryGetProperty("max_output_tokens", out var value) ? value : warm.TryGetProperty("max_completion_tokens", out value) ? value : warm.GetProperty("max_tokens");
            // api/openai-responses.ts clamps the requested one-token warm to its provider minimum of 16.
            Check(max.GetInt32() == (api == "openai-responses" ? 16 : 1), "Warm request lost provider-adjusted output cap.");
            Check(!handler.Payloads[2].Value.TryGetProperty("prompt_cache_retention", out _), "Summary retained prompt cache.");
        }
        Check(LiveSessionConnection.ResolveCacheRetention("short", "long") == "short" &&
            LiveSessionConnection.ResolveCacheRetention("", "long") == "long", "Scoped retention precedence differs.");
    }
    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal List<JsonData> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Payloads.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)));
            return new(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"fixture rejection\"}}") };
        }
    }

    private static JsonData Limits(int width) => JsonData.Parse(JsonSerializer.Serialize(new { inputLimits = new { images = new { resize = new { maxWidth = width, jpegQuality = 95 } } } }));
    private static TranscriptEntry Image(string text, string tag, bool includeOpaqueBlock = true)
    {
        var blocks = new List<object> { new { type = "image", data = "AA==", mimeType = "image/png", opaque = "image metadata" } };
        // The pure normalization test preserves arbitrary non-image content; actual agent admission keeps its existing supported block contract.
        if (includeOpaqueBlock) blocks.Add(new { type = "custom", text = "keep", opaque = 7 });
        blocks.Add(new { type = "text", text });
        return new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", timestamp = 1, tag, content = blocks })));
    }
    private sealed class Codec : IImageCodec
    {
        internal List<int> Widths { get; } = [];
        internal List<int> Qualities { get; } = [];
        public (int Width, int Height)? ProbeDimensions(ReadOnlySpan<byte> bytes, string mimeType) => bytes.Length == 1 ? (4000, 1000) : (500, 125);
        public byte[]? ConvertToPng(ReadOnlySpan<byte> bytes, string mimeType) => null;
        public ImmutableArray<(byte[] Bytes, string MimeType)>? Encode(ReadOnlySpan<byte> bytes, string mimeType, int width, int height, IReadOnlyList<int> qualities)
        { Widths.Add(width); Qualities.Add(qualities[0]); return [(new byte[] { 1, 2 }, "image/png")]; }
    }
    private sealed class Script : IChatTransport
    {
        internal List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests.Add(request); await Task.CompletedTask;
            var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); yield return new StreamDone(StopReason.Stop, message);
        }
    }
    private sealed class Sink(Action<AgentEvent> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) { emit(observation); return ValueTask.CompletedTask; } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
