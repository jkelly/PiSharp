using System.Collections.Concurrent;
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime.Mcp;

// Synthetic admitted channels and publication sinks; no process/HTTP/server/credential access.
// Coordinator owns shared Cases registration and all formal execution.
internal static class McpServerRuntimeTests
{
    internal const string Prefix = "mcp-runtime.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "initialize-roots-protocol-and-paging", Handshake),
        (Prefix + "startup-original-coalesces-until-publication", SharedStartup),
        (Prefix + "disabled-and-capability-absent-no-tool-acquisition", Disabled),
        (Prefix + "invalid-initialize-closes-acquired-lease", InvalidInitialize),
        (Prefix + "held-initialize-close-fences-late-protocol-notify", HeldInitialize),
        (Prefix + "native-call-identity-progress-and-structured-result", Call),
        (Prefix + "close-joins-held-call-and-repeats-exact-task", HeldCall),
        (Prefix + "caller-cancel-joins-original-request-cleanup", CancelCall),
        (Prefix + "late-acquired-lease-closes-without-start-or-publication", LateAcquisition),
        (Prefix + "notification-refresh-withdraws-last-acknowledged-tools", Withdrawal),
        (Prefix + "close-drops-late-refresh-before-publication", StaleRefresh),
        (Prefix + "publication-failure-retains-old-and-stops-automatic-republication", PublicationFailure),
        (Prefix + "mismatched-receipt-and-reentry-refuse-before-effects", PublicationGuards),
        (Prefix + "close-joins-already-committed-publication-receipt", HeldPublication),
        (Prefix + "swallowed-progress-failure-and-repeat-close-fault-identity", FailureIdentity),
        (Prefix + "close-joins-held-native-progress-original", HeldProgress),
        (Prefix + "native-progress-reentry-refuses-before-effects", ProgressReentry),
        (Prefix + "definite-disconnect-is-not-replayed-and-next-call-reconnects", Disconnect),
        (Prefix + "refresh-disconnect-retires-joins-cleanup-and-later-reconnects", RefreshDisconnect),
        (Prefix + "tools-call-shape-rejection-retains-one-original-request", ResultShapes),
        (Prefix + "response-schema-tools-pages-and-cursor-bounds", Bounds),
        (Prefix + "close-initiates-physical-stop-before-request-cancellation-callback-join", StopBeforeCallback),
        (Prefix + "startup-close-initiates-physical-stop-before-start-cancellation-callback-join", StartStopBeforeCallback),
        (Prefix + "close-retains-cancellation-callback-and-physical-cleanup-original-faults", StopCallbackFaults)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonData Json(string text) => JsonData.Parse(text);
    private static readonly JsonData Initialize = Json("""{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"synthetic","version":"1"},"instructions":"  group instructions  "} """);
    private static JsonData Tools(params string[] names) => JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new
    { tools = names.Select(name => new { name, inputSchema = new { }, description = "fixture " + name }).ToArray() }));
    private static McpServerEntry Entry(bool enabled = true)
    {
        var config = McpConfigurationReader.Validate("synthetic", Json(enabled ? "{\"command\":\"never-run\",\"timeout\":2}" : "{\"command\":\"never-run\",\"enabled\":false}").Value).Config!;
        return new("synthetic", config, "supplied-fixture.json", McpConfigurationScope.Global);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal Channel Channel { get; } = new();
        internal ConcurrentQueue<McpCatalogPublication> Publications { get; } = new();
        internal int Acquisitions;
        internal Func<McpCatalogPublication, CancellationToken, Task<McpCatalogPublicationReceipt>>? Publisher;
        internal McpServerRuntime Runtime { get; }
        internal Fixture(bool enabled = true, McpRuntimeOptions? options = null)
        {
            Runtime = new(Entry(enabled), options ?? new(7, "0.99.1"), (_, _) => { Interlocked.Increment(ref Acquisitions); return ValueTask.FromResult<IMcpAdmittedRequestChannel>(Channel); }, async (candidate, token) =>
            {
                Publications.Enqueue(candidate);
                return Publisher is { } sink ? await sink(candidate, token) : Ack(candidate);
            });
        }
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
    private sealed record Request(string Method, JsonData? Parameters, McpRequestOptions Options);
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal ConcurrentQueue<string> Events { get; } = new();
        internal ConcurrentQueue<Request> Requests { get; } = new();
        internal Func<string, JsonData?, McpRequestOptions, CancellationToken, Task<JsonData>>? Handler;
        internal Func<CancellationToken, Task>? Start;
        internal Func<Task>? Cleanup;
        internal JsonData? Roots;
        internal int Starts, Closes;
        internal TaskCompletionSource StopEntered { get; } = Gate();
        private readonly object gate = new(); private readonly HashSet<Task> pending = [];
        private readonly Lazy<Task> close;
        internal Channel() { close = new(CloseCore, LazyThreadSafetyMode.ExecutionAndPublication); }
        public async ValueTask StartAsync(CancellationToken token)
        { Interlocked.Increment(ref Starts); Events.Enqueue("start"); if (Start is { } start) await start(token); token.ThrowIfCancellationRequested(); }
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) { Roots = roots; return ValueTask.CompletedTask; }
        public ValueTask SetProtocolVersionAsync(string version, CancellationToken token) { Events.Enqueue("protocol:" + version); return ValueTask.CompletedTask; }
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) { token.ThrowIfCancellationRequested(); Events.Enqueue(method); return ValueTask.CompletedTask; }
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token) => new(RequestCore(method, parameters, options, token));
        private async Task<JsonData> RequestCore(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            var settled = Gate(); lock (gate) pending.Add(settled.Task);
            try
            {
                Events.Enqueue(method); Requests.Enqueue(new(method, parameters, options));
                return Handler is { } handler ? await handler(method, parameters, options, token) : method switch
                { "initialize" => Initialize, "tools/list" => Tools("a", "b"), "tools/call" => Json("{\"content\":[]}"), _ => throw new InvalidOperationException("Unexpected synthetic method") };
            }
            finally { lock (gate) pending.Remove(settled.Task); settled.TrySetResult(); }
        }
        public Task CloseAsync() => close.Value;
        private async Task CloseCore()
        { Interlocked.Increment(ref Closes); StopEntered.TrySetResult(); Task[] originals; lock (gate) originals = pending.ToArray(); await Task.WhenAll(originals); if (Cleanup is { } cleanup) await cleanup(); }
    }
    private sealed class Context : IExtensionToolInvocationContext
    {
        public string OwnerId => "native-owner";
        public long OwnerGeneration => 3;
        public long SessionGeneration => 11;
        public string ToolCallId => "actual-root";
        public string? ParentToolCallId => "actual-parent";
        public CancellationToken OperationCancellationToken { get; init; }
        public CancellationToken SessionCancellationToken => default;
        public CancellationToken ExtensionLifetimeCancellationToken => default;
        internal List<JsonData> Updates { get; } = [];
        internal Exception? Failure;
        internal Func<JsonData, CancellationToken, Task>? Reporter;
        public async ValueTask ReportUpdateAsync(JsonData update, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (Failure is { } error) throw error; Updates.Add(update); if (Reporter is { } reporter) await reporter(update, token); }
    }
    private static McpCatalogPublicationReceipt Ack(McpCatalogPublication publication) => new(publication.Current.Generation, publication.Current.Revision, true);

    private static async Task Handshake()
    {
        var roots = Json("[{\"uri\":\"file:///supplied/synthetic\",\"name\":\"synthetic\"}]");
        await using var fixture = new Fixture(options: new(7, "0.99.1", roots));
        fixture.Channel.Handler = (method, parameters, _, _) => Task.FromResult(method == "initialize" ? Initialize :
            parameters is null ? Json("{\"tools\":[{\"name\":\"a\",\"inputSchema\":{}}],\"nextCursor\":\"page-two\"}") : Tools("b"));
        var snapshot = await fixture.Runtime.ConnectAsync();
        Equal("start,initialize,protocol:2025-11-25,notifications/initialized,tools/list,tools/list", string.Join(',', fixture.Channel.Events));
        Check(ReferenceEquals(roots, fixture.Channel.Roots)); Equal(1L, snapshot.Revision); Equal(2, snapshot.Catalog.Tools.Length);
        Equal("group instructions", snapshot.Catalog.Instructions); Equal(1, fixture.Acquisitions);
        var initialize = fixture.Channel.Requests.First(); Equal("2025-11-25", initialize.Parameters!.Value.GetProperty("protocolVersion").GetString());
        Equal("0.99.1", initialize.Parameters.Value.GetProperty("clientInfo").GetProperty("version").GetString());
        Check(initialize.Parameters.Value.GetProperty("capabilities").TryGetProperty("roots", out _));
        Check(fixture.Channel.Requests.All(request => request.Options.TimeoutMilliseconds == 2000));
    }
    private static async Task SharedStartup()
    {
        await using var fixture = new Fixture(); var entered = Gate(); var release = Gate();
        fixture.Channel.Start = async _ => { entered.TrySetResult(); await release.Task; };
        Task<McpRuntimeSnapshot>? first = null, second = null;
        try
        {
            first = fixture.Runtime.ConnectAsync(); await entered.Task; second = fixture.Runtime.ConnectAsync();
            Check(!first.IsCompleted && !second.IsCompleted); Equal(1, fixture.Acquisitions);
            release.TrySetResult(); await Task.WhenAll(first, second); Equal(1, fixture.Publications.Count);
        }
        finally { release.TrySetResult(); if (first is not null) await Observe(first); if (second is not null) await Observe(second); }
    }
    private static async Task Disabled()
    {
        await using (var disabled = new Fixture(false)) { await Throws<InvalidOperationException>(disabled.Runtime.ConnectAsync()); Equal(0, disabled.Acquisitions); Equal(0, disabled.Publications.Count); }
        await using var fixture = new Fixture(); fixture.Channel.Handler = (_, _, _, _) => Task.FromResult(Json("{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"serverInfo\":{\"name\":\"s\",\"version\":\"1\"}}"));
        var snapshot = await fixture.Runtime.ConnectAsync(); Equal(0, snapshot.Catalog.Tools.Length); Equal(1, fixture.Channel.Requests.Count);
    }
    private static async Task InvalidInitialize()
    {
        foreach (var text in new[] { "{}", "{\"protocolVersion\":\"bad\",\"capabilities\":{},\"serverInfo\":{\"name\":\"s\",\"version\":\"1\"}}" })
        {
            await using var fixture = new Fixture(); fixture.Channel.Handler = (_, _, _, _) => Task.FromResult(Json(text));
            await Throws<McpRuntimeProtocolException>(fixture.Runtime.ConnectAsync()); Equal(1, fixture.Channel.Closes); Equal(0, fixture.Publications.Count);
        }
    }
    private static async Task Call()
    {
        await using var fixture = new Fixture(); await fixture.Runtime.ConnectAsync(); var context = new Context();
        fixture.Channel.Handler = async (method, parameters, options, token) =>
        {
            Equal("tools/call", method); Equal("a", parameters!.Value.GetProperty("name").GetString());
            Equal(42, parameters.Value.GetProperty("arguments").GetProperty("value").GetInt32());
            Equal(new McpInvocationIdentity("native-owner", 3, 11, "actual-root", "actual-parent"), options.InvocationIdentity);
            await options.OnProgress!(new(1, 2), token); return Json("{\"structuredContent\":{\"answer\":42},\"isError\":true,\"unknown\":7}");
        };
        var result = await fixture.Runtime.CallToolAsync("a", Json("{\"value\":42}"), context);
        Equal(0, result.Value.GetProperty("content").GetArrayLength()); Check(result.Value.GetProperty("isError").GetBoolean());
        Equal(7, result.Value.GetProperty("unknown").GetInt32()); Equal("Progress 1/2", context.Updates.Single().Value.GetProperty("content")[0].GetProperty("text").GetString());
        Equal("synthetic", context.Updates.Single().Value.GetProperty("details").GetProperty("server").GetString());
    }
    private static async Task HeldInitialize()
    {
        await using var fixture = new Fixture(); var entered = Gate(); var release = Gate(); Task<McpRuntimeSnapshot>? startup = null; Task? close = null;
        fixture.Channel.Handler = async (_, _, _, _) => { entered.TrySetResult(); await release.Task; return Initialize; };
        try
        {
            startup = fixture.Runtime.ConnectAsync(); await entered.Task; close = fixture.Runtime.CloseAsync(); Check(!close.IsCompleted);
            release.TrySetResult(); await Throws<OperationCanceledException>(startup); await close;
            Equal("start,initialize", string.Join(',', fixture.Channel.Events)); Equal(0, fixture.Publications.Count); Equal(1, fixture.Channel.Closes);
        }
        finally { release.TrySetResult(); if (startup is not null) await Observe(startup); if (close is not null) await Observe(close); }
    }
    private static async Task HeldCall()
    {
        await using var fixture = new Fixture(); await fixture.Runtime.ConnectAsync(); var entered = Gate(); var release = Gate(); Task<JsonData>? call = null; Task? close = null;
        fixture.Channel.Handler = async (_, _, _, token) => { entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return Json("{\"content\":[]}"); };
        try
        {
            call = fixture.Runtime.CallToolAsync("a", JsonData.EmptyObject, new Context()); await entered.Task;
            close = fixture.Runtime.CloseAsync(); Check(ReferenceEquals(close, fixture.Runtime.CloseAsync())); Check(!close.IsCompleted);
            release.TrySetResult(); await Throws<OperationCanceledException>(call); await close; Equal(1, fixture.Channel.Closes);
            Check(!fixture.Runtime.Snapshot.Catalog.Connected); await Throws<ObjectDisposedException>(fixture.Runtime.ConnectAsync());
        }
        finally { release.TrySetResult(); if (call is not null) await Observe(call); if (close is not null) await Observe(close); }
    }
    private static async Task CancelCall()
    {
        await using var fixture = new Fixture(); await fixture.Runtime.ConnectAsync(); using var caller = new CancellationTokenSource();
        var entered = Gate(); var release = Gate(); Task<JsonData>? call = null; CancellationToken captured = default;
        fixture.Channel.Handler = async (_, _, _, token) => { captured = token; entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return Json("{\"content\":[]}"); };
        try { call = fixture.Runtime.CallToolAsync("a", JsonData.EmptyObject, new Context(), caller.Token); await entered.Task; caller.Cancel(); Check(captured.IsCancellationRequested); Check(!call.IsCompleted); release.TrySetResult(); await Throws<OperationCanceledException>(call); }
        finally { release.TrySetResult(); if (call is not null) await Observe(call); }
        Equal(1, fixture.Channel.Requests.Count(request => request.Method == "tools/call"));
    }
    private static async Task LateAcquisition()
    {
        var channel = new Channel(); var entered = Gate(); var release = Gate(); var publications = 0;
        await using var runtime = new McpServerRuntime(Entry(), new(7, "0.99.1"), async (_, _) => { entered.TrySetResult(); await release.Task; return channel; }, (candidate, _) => { publications++; return ValueTask.FromResult(Ack(candidate)); });
        Task<McpRuntimeSnapshot>? startup = null; Task? close = null;
        try { startup = runtime.ConnectAsync(); await entered.Task; close = runtime.CloseAsync(); Check(!close.IsCompleted); release.TrySetResult(); await Throws<OperationCanceledException>(startup); await close; Equal(0, channel.Starts); Equal(1, channel.Closes); Equal(0, publications); }
        finally { release.TrySetResult(); if (startup is not null) await Observe(startup); if (close is not null) await Observe(close); }
    }
    private static async Task Withdrawal()
    {
        await using var fixture = new Fixture(); var original = await fixture.Runtime.ConnectAsync();
        fixture.Channel.Handler = (_, _, _, _) => Task.FromResult(Tools("b"));
        Check(await fixture.Runtime.HandleNotificationAsync("notifications/tools/list_changed"));
        Check(!await fixture.Runtime.HandleNotificationAsync("unhandled"));
        var latest = fixture.Publications.Last(); Equal(2L, latest.Current.Revision); Equal("a", latest.WithdrawnTools.Single().OriginalName);
        Equal(ToolExposure.Hidden, latest.WithdrawnTools.Single().Exposure); Equal(2, original.Catalog.Tools.Length); Equal(1, fixture.Runtime.Snapshot.Catalog.Tools.Length);
    }
    private static async Task StaleRefresh()
    {
        await using var fixture = new Fixture(); await fixture.Runtime.ConnectAsync(); var entered = Gate(); var release = Gate(); Task<McpRuntimeSnapshot>? refresh = null; Task? close = null;
        fixture.Channel.Handler = async (_, _, _, _) => { entered.TrySetResult(); await release.Task; return Tools("late"); };
        try { refresh = fixture.Runtime.RefreshToolsAsync(); await entered.Task; close = fixture.Runtime.CloseAsync(); Check(!close.IsCompleted); release.TrySetResult(); await Throws<OperationCanceledException>(refresh); await close; Equal(1, fixture.Publications.Count); Equal(1L, fixture.Runtime.Snapshot.Revision); }
        finally { release.TrySetResult(); if (refresh is not null) await Observe(refresh); if (close is not null) await Observe(close); }
    }
    private static async Task PublicationFailure()
    {
        await using var fixture = new Fixture(); var original = await fixture.Runtime.ConnectAsync(); var failure = new IOException("unknown publication outcome");
        fixture.Channel.Handler = (_, _, _, _) => Task.FromResult(Tools("replacement")); fixture.Publisher = (_, _) => Task.FromException<McpCatalogPublicationReceipt>(failure);
        Check(ReferenceEquals(failure, await Throws<IOException>(fixture.Runtime.RefreshToolsAsync()))); Check(ReferenceEquals(original, fixture.Runtime.Snapshot));
        var repeated = await Throws<InvalidOperationException>(fixture.Runtime.RefreshToolsAsync()); Check(ReferenceEquals(failure, repeated.InnerException)); Equal(2, fixture.Publications.Count);
    }
    private static async Task PublicationGuards()
    {
        await using (var mismatch = new Fixture())
        {
            mismatch.Publisher = (candidate, _) => Task.FromResult(new McpCatalogPublicationReceipt(candidate.Current.Generation + 1, candidate.Current.Revision, true));
            await Throws<InvalidOperationException>(mismatch.Runtime.ConnectAsync()); Equal(0L, mismatch.Runtime.Snapshot.Revision); Equal(1, mismatch.Channel.Closes);
        }
        await using var fixture = new Fixture(); fixture.Publisher = async (candidate, _) =>
        {
            await Throws<InvalidOperationException>(fixture.Runtime.RefreshToolsAsync());
            ThrowsAction<InvalidOperationException>(() => fixture.Runtime.CloseAsync());
            ThrowsAction<InvalidOperationException>(() => fixture.Runtime.CallToolAsync("a", JsonData.EmptyObject, new Context()));
            Equal(0L, fixture.Runtime.Snapshot.Revision); return Ack(candidate);
        };
        await fixture.Runtime.ConnectAsync(); Equal(1, fixture.Channel.Requests.Count(request => request.Method == "tools/list"));
    }
    private static async Task HeldPublication()
    {
        await using var fixture = new Fixture(); var committed = Gate(); var release = Gate(); Task<McpRuntimeSnapshot>? startup = null; Task? close = null;
        fixture.Publisher = async (candidate, _) => { committed.TrySetResult(); await release.Task; return Ack(candidate); };
        try { startup = fixture.Runtime.ConnectAsync(); await committed.Task; close = fixture.Runtime.CloseAsync(); Check(!close.IsCompleted); release.TrySetResult(); await Throws<OperationCanceledException>(startup); await close; Equal(1L, fixture.Runtime.Snapshot.Revision); Equal(1, fixture.Publications.Count); }
        finally { release.TrySetResult(); if (startup is not null) await Observe(startup); if (close is not null) await Observe(close); }
    }
    private static async Task FailureIdentity()
    {
        var fixture = new Fixture(); await fixture.Runtime.ConnectAsync(); var failure = new IOException("actual native progress failed");
        fixture.Channel.Handler = async (_, _, options, token) => { try { await options.OnProgress!(new(1, Message: "progress"), token); } catch { } return Json("{\"content\":[]}"); };
        Check(ReferenceEquals(failure, await Throws<IOException>(fixture.Runtime.CallToolAsync("a", JsonData.EmptyObject, new Context { Failure = failure }))));
        var cleanup = new IOException("actual lease close failed"); fixture.Channel.Cleanup = () => Task.FromException(cleanup);
        var close = fixture.Runtime.CloseAsync(); Check(ReferenceEquals(close, fixture.Runtime.CloseAsync()));
        Check(ReferenceEquals(cleanup, await Throws<IOException>(close))); Check(ReferenceEquals(cleanup, await Throws<IOException>(fixture.Runtime.CloseAsync())));
        Equal(1, fixture.Channel.Closes); Check(ReferenceEquals(close, fixture.Runtime.DisposeAsync().AsTask()));
    }
    private static async Task Bounds()
    {
        foreach (var limits in new[] { new McpRuntimeLimits(MaximumTools: 1), new McpRuntimeLimits(MaximumSchemaBytes: 1), new McpRuntimeLimits(MaximumResponseBytes: 20), new McpRuntimeLimits(MaximumCatalogBytes: 1) })
        { await using var fixture = new Fixture(options: new(7, "0.99.1") { Limits = limits }); await Throws<McpRuntimeProtocolException>(fixture.Runtime.ConnectAsync()); Equal(0, fixture.Publications.Count); Equal(1, fixture.Channel.Closes); }
        await using var cursor = new Fixture(); cursor.Channel.Handler = (method, _, _, _) => Task.FromResult(method == "initialize" ? Initialize : Json("{\"tools\":[],\"nextCursor\":\"repeat\"}"));
        await Throws<McpRuntimeProtocolException>(cursor.Runtime.ConnectAsync()); Equal(2, cursor.Channel.Requests.Count(request => request.Method == "tools/list"));
        await using var pages = new Fixture(options: new(7, "0.99.1") { Limits = new(MaximumPages: 1) }); pages.Channel.Handler = cursor.Channel.Handler;
        await Throws<McpRuntimeProtocolException>(pages.Runtime.ConnectAsync()); Equal(1, pages.Channel.Requests.Count(request => request.Method == "tools/list"));
    }
    private static async Task HeldProgress()
    {
        await using var fixture = new Fixture(); await fixture.Runtime.ConnectAsync(); var entered = Gate(); var release = Gate(); Task<JsonData>? call = null; Task? close = null;
        var context = new Context { Reporter = async (_, _) => { entered.TrySetResult(); await release.Task; } };
        fixture.Channel.Handler = async (_, _, options, token) => { await options.OnProgress!(new(1), token); return Json("{\"content\":[]}"); };
        try
        {
            call = fixture.Runtime.CallToolAsync("a", JsonData.EmptyObject, context); await entered.Task;
            close = fixture.Runtime.CloseAsync(); Check(!call.IsCompleted && !close.IsCompleted); release.TrySetResult();
            await Throws<OperationCanceledException>(call); await close; Equal(1, context.Updates.Count); Equal(1, fixture.Channel.Closes);
        }
        finally { release.TrySetResult(); if (call is not null) await Observe(call); if (close is not null) await Observe(close); }
    }
    private static async Task Disconnect()
    {
        var first = new Channel(); var next = new Channel(); var acquisitions = 0; var publications = 0;
        first.Handler = (method, _, _, _) => method == "tools/call" ? Task.FromException<JsonData>(new McpRuntimeDisconnectedException("definite disconnected lease")) : Task.FromResult(method == "initialize" ? Initialize : Tools("a"));
        await using var runtime = new McpServerRuntime(Entry(), new(7, "0.99.1"), (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(++acquisitions == 1 ? first : next), (candidate, _) => { publications++; return ValueTask.FromResult(Ack(candidate)); });
        await runtime.ConnectAsync(); await Throws<McpRuntimeDisconnectedException>(runtime.CallToolAsync("a", JsonData.EmptyObject, new Context()));
        Equal(1, acquisitions); Equal(1, first.Requests.Count(request => request.Method == "tools/call")); Equal(1, first.Closes); Check(!runtime.Snapshot.Catalog.Connected);
        var result = await runtime.CallToolAsync("a", JsonData.EmptyObject, new Context()); Equal(2, acquisitions); Equal(2, publications);
        Equal(0, result.Value.GetProperty("content").GetArrayLength()); Equal(1, next.Requests.Count(request => request.Method == "tools/call"));
    }
    private static async Task ProgressReentry()
    {
        await using var fixture = new Fixture(); await fixture.Runtime.ConnectAsync();
        var context = new Context { Reporter = async (_, _) =>
        {
            ThrowsAction<InvalidOperationException>(() => fixture.Runtime.CloseAsync());
            ThrowsAction<InvalidOperationException>(() => fixture.Runtime.CallToolAsync("a", JsonData.EmptyObject, new Context()));
            await Throws<InvalidOperationException>(fixture.Runtime.RefreshToolsAsync());
            await Throws<InvalidOperationException>(fixture.Runtime.ConnectAsync());
        } };
        fixture.Channel.Handler = async (_, _, options, token) => { await options.OnProgress!(new(1), token); return Json("{\"content\":[]}"); };
        await fixture.Runtime.CallToolAsync("a", JsonData.EmptyObject, context);
        Equal(1, fixture.Channel.Requests.Count(request => request.Method == "tools/call"));
        Equal(1, fixture.Channel.Requests.Count(request => request.Method == "tools/list")); Equal(0, fixture.Channel.Closes);
    }
    private static async Task RefreshDisconnect()
    {
        foreach (var cleanupFails in new[] { false, true })
        {
            var first = new Channel(); var next = new Channel(); var acquisitions = 0; var lists = 0;
            var entered = Gate(); var release = Gate(); var failure = new McpRuntimeDisconnectedException("refresh definitely disconnected");
            var cleanup = new IOException("actual retiring lease cleanup failed");
            first.Handler = (method, _, _, _) => method == "tools/list" && ++lists > 1 ? Task.FromException<JsonData>(failure) : Task.FromResult(method == "initialize" ? Initialize : Tools("a"));
            first.Cleanup = async () => { entered.TrySetResult(); await release.Task; if (cleanupFails) throw cleanup; };
            var runtime = new McpServerRuntime(Entry(), new(7, "0.99.1"), (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(++acquisitions == 1 ? first : next), (candidate, _) => ValueTask.FromResult(Ack(candidate)));
            Task<bool>? refresh = null; Task? close = null;
            try
            {
                var acknowledged = await runtime.ConnectAsync(); refresh = runtime.HandleNotificationAsync("notifications/tools/list_changed"); await entered.Task;
                Check(!refresh.IsCompleted); Equal(2, lists); Equal(1, first.Closes); Equal(1, acquisitions);
                var retired = runtime.Snapshot; Check(!retired.Catalog.Connected); Equal(acknowledged.Revision, retired.Revision);
                Check(retired.OriginalTools.SequenceEqual(acknowledged.OriginalTools)); Equal("a", retired.Catalog.Tools.Single().Name);
                if (cleanupFails) { close = runtime.CloseAsync(); Check(!close.IsCompleted); }
                release.TrySetResult();
                if (cleanupFails)
                {
                    var aggregate = await Throws<AggregateException>(refresh); Check(ReferenceEquals(failure, aggregate.InnerExceptions[0])); Check(ReferenceEquals(cleanup, aggregate.InnerExceptions[1]));
                    Check(ReferenceEquals(cleanup, await Throws<IOException>(close!)));
                    Check(ReferenceEquals(close, runtime.CloseAsync()));
                    Check(ReferenceEquals(cleanup, await Throws<IOException>(runtime.CloseAsync())));
                }
                else
                {
                    Check(ReferenceEquals(failure, await Throws<McpRuntimeDisconnectedException>(refresh)));
                    var reconnected = await runtime.ConnectAsync(); Equal(2, acquisitions); Check(reconnected.Catalog.Connected); Equal(2L, reconnected.Revision);
                    Equal(1, next.Requests.Count(request => request.Method == "initialize")); Equal(1, next.Requests.Count(request => request.Method == "tools/list"));
                }
                Equal(2, lists); Equal(1, first.Closes);
            }
            finally { release.TrySetResult(); if (refresh is not null) await Observe(refresh); if (close is not null) await Observe(close); else await runtime.CloseAsync(); }
        }
    }
    private static Task StopBeforeCallback() => StopBeforeCallbackCore(startup: false, faults: false);
    private static Task StartStopBeforeCallback() => StopBeforeCallbackCore(startup: true, faults: false);
    private static Task StopCallbackFaults() => StopBeforeCallbackCore(startup: false, faults: true);
    private static async Task StopBeforeCallbackCore(bool startup, bool faults)
    {
        var fixture = new Fixture(); var entered = Gate(); var requestRelease = Gate();
        var cancellationEntered = Gate(); var cancellationRelease = Gate(); var physicalEntered = Gate(); var physicalRelease = Gate();
        var cancellationFault = new IOException("runtime-cancellation-original"); var physicalFault = new IOException("runtime-physical-close-original");
        Task? operation = null, closing = null; Exception? operationFailure = null, closeFailure = null;
        var failures = new List<Exception>();
        void Canceled()
        {
            cancellationEntered.TrySetResult();
            // This is the real callback original invoked by the supplied request token. It cannot
            // settle until physical close has initiated stop, then remains independently held.
            fixture.Channel.StopEntered.Task.GetAwaiter().GetResult();
            cancellationRelease.Task.GetAwaiter().GetResult();
            if (faults) throw cancellationFault;
        }
        async Task Held(CancellationToken token)
        {
            using var registration = token.Register(Canceled);
            entered.TrySetResult(); await requestRelease.Task; token.ThrowIfCancellationRequested();
        }
        fixture.Channel.Cleanup = async () =>
        { physicalEntered.TrySetResult(); await physicalRelease.Task; if (faults) throw physicalFault; };
        try
        {
            if (startup)
            { fixture.Channel.Start = Held; operation = fixture.Runtime.ConnectAsync(); }
            else
            {
                await fixture.Runtime.ConnectAsync();
                fixture.Channel.Handler = async (method, parameters, options, token) =>
                { await Held(token); return Json("{\"content\":[]}"); };
                operation = fixture.Runtime.CallToolAsync("a", JsonData.EmptyObject, new Context());
            }
            await entered.Task;
            closing = fixture.Runtime.CloseAsync(); Check(ReferenceEquals(closing, fixture.Runtime.CloseAsync()));
            // These bounded gate observations carry no effect ownership. Finally directly joins
            // every operation/close original even if the old source fails the ordering assertion.
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Channel.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!closing.IsCompleted && fixture.Channel.Closes == 1);
            requestRelease.TrySetResult(); cancellationRelease.TrySetResult();
            await physicalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!closing.IsCompleted);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            // Teardown can unblock the old-source cycle without weakening the preceding assertion.
            fixture.Channel.StopEntered.TrySetResult(); cancellationRelease.TrySetResult(); requestRelease.TrySetResult(); physicalRelease.TrySetResult();
            if (operation is not null) try { await operation; } catch (Exception error) { operationFailure = error; }
            if (closing is not null) try { await closing; } catch (Exception error) { closeFailure = error; }
            try { await fixture.DisposeAsync(); } catch (Exception error) { if (!ReferenceEquals(error, closeFailure)) failures.Add(error); }
        }
        try
        {
            Check(operationFailure is OperationCanceledException);
            if (faults)
            {
                Check(closeFailure is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Count == 2 &&
                    aggregate.Flatten().InnerExceptions.Contains(cancellationFault) && aggregate.Flatten().InnerExceptions.Contains(physicalFault));
                Check(ReferenceEquals(closing, fixture.Runtime.CloseAsync()));
                var repeated = await Throws<AggregateException>(fixture.Runtime.CloseAsync()); Check(ReferenceEquals(closeFailure, repeated));
            }
            else Check(closeFailure is null);
            Equal(1, fixture.Channel.Closes); Check(!fixture.Runtime.Snapshot.Catalog.Connected);
        }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
    private static async Task ResultShapes()
    {
        foreach (var text in new[] { "[]", "{\"content\":null}", "{\"structuredContent\":[]}" })
        {
            await using var fixture = new Fixture(); await fixture.Runtime.ConnectAsync(); fixture.Channel.Handler = (_, _, _, _) => Task.FromResult(Json(text));
            await Throws<McpRuntimeProtocolException>(fixture.Runtime.CallToolAsync("a", JsonData.EmptyObject, new Context()));
            Equal(1, fixture.Channel.Requests.Count(request => request.Method == "tools/call"));
        }
    }
    private static async Task<T> Throws<T>(Task task) where T : Exception
    { try { await task; } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void ThrowsAction<T>(Func<object> action) where T : Exception
    { try { _ = action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Observe(Task task) { try { await task; } catch { } }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("MCP runtime fixture assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
}
