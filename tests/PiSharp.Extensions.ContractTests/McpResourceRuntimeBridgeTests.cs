using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Resources;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime.Mcp;
using PiSharp.Extensions.Runtime.Mcp.Resources;

// Synthetic runtime/channel originals only. Coordinator owns registration and all execution.
internal static class McpResourceRuntimeBridgeTests
{
    internal const string Prefix = "mcp-resource-runtime-bridge.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "actual-list-template-read-bridge-and-native-identity", Methods),
        (Prefix + "stale-generation-identity-method-and-options-refuse-before-acquire", Admission),
        (Prefix + "capability-absent-refuses-resource-effects", Capability),
        (Prefix + "held-request-close-joins-physical-request-and-cleanup-originals", HeldClose),
        (Prefix + "owned-cancel-maps-proven-original-to-leaf-token", OwnedCancellation),
        (Prefix + "faulted-and-synchronous-oce-stay-faulted-with-all-original-faults", Faults),
        (Prefix + "definite-disconnect-retires-original-before-later-reconnect-without-replay", Disconnect),
        (Prefix + "held-native-progress-and-reentry-originals-joined-through-close", Progress),
        (Prefix + "swallowed-progress-first-oce-and-cleanup-faults-retained", ProgressFaults),
        (Prefix + "swallowed-progress-limit-rejection-still-faults-owning-request", ProgressLimit),
        (Prefix + "multicast-factory-and-publisher-refuse-before-effects", Multicast),
        (Prefix + "physical-close-multiple-faults-retained", CloseFaults)
    ];
    private static JsonData Json(string value) => JsonData.Parse(value);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("MCP resource runtime bridge control failed."); }
    private sealed class Context : IExtensionToolInvocationContext
    {
        public string OwnerId => "resource-scope";
        public long OwnerGeneration => 3;
        public long SessionGeneration => 11;
        public string ToolCallId => "resource-root";
        public string? ParentToolCallId => "resource-parent";
        public CancellationToken OperationCancellationToken { get; init; }
        public CancellationToken SessionCancellationToken => default;
        public CancellationToken ExtensionLifetimeCancellationToken => default;
        internal Func<JsonData, CancellationToken, ValueTask>? Report;
        public ValueTask ReportUpdateAsync(JsonData data, CancellationToken token)
        {
            if (Report?.GetInvocationList().Length > 1) throw new InvalidOperationException("Synthetic reporter must be single.");
            return Report is { } report ? report(data, token) : ValueTask.CompletedTask;
        }
    }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal bool Resources = true;
        internal Func<string, JsonData?, McpRequestOptions, CancellationToken, Task<JsonData>>? Handler;
        internal readonly List<string> Requests = [];
        internal readonly TaskCompletionSource CloseEntered = Gate();
        internal Task CloseRelease = Task.CompletedTask;
        internal Task? CloseOriginal;
        private readonly object gate = new(); private readonly List<Task> pending = [];
        private readonly Lazy<Task> close;
        internal Channel() { close = new(CloseCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication); }
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            Requests.Add(method);
            if (method == "initialize") return ValueTask.FromResult(Json(Resources ?
                """{"protocolVersion":"2025-11-25","capabilities":{"resources":{}},"serverInfo":{"name":"synthetic","version":"1"}}""" :
                """{"protocolVersion":"2025-11-25","capabilities":{},"serverInfo":{"name":"synthetic","version":"1"}}"""));
            if (Handler?.GetInvocationList().Length > 1) throw new InvalidOperationException("Synthetic channel must be single.");
            var original = Handler is { } handler ? handler(method, parameters, options, token) : Task.FromResult(Response(method));
            lock (gate) pending.Add(original); return new(original);
        }
        internal static JsonData Response(string method) => method switch
        {
            "resources/list" => Json("""{"resources":[{"uri":"data://a","name":"a"}]}"""),
            "resources/templates/list" => Json("""{"resourceTemplates":[{"uriTemplate":"data://{x}","name":"template"}]}"""),
            "resources/read" => Json("""{"contents":[{"uri":"data://a","text":"read value"}]}"""),
            _ => throw new InvalidOperationException("Unexpected resource method")
        };
        public Task CloseAsync() { if (CloseOriginal is { } original) { CloseEntered.TrySetResult(); return original; } return close.Value; }
        private async Task CloseCoreAsync()
        {
            CloseEntered.TrySetResult(); Task[] originals; lock (gate) originals = pending.ToArray();
            foreach (var original in originals) try { await original; } catch { /* Request owner reports its own fault. */ }
            await CloseRelease;
        }
    }
    private static McpServerEntry Entry()
    {
        var config = McpConfigurationReader.Validate("demo", Json("""{"command":"inert","timeout":2}""").Value).Config!;
        return new("demo", config, "synthetic", McpConfigurationScope.Extension);
    }
    private static McpServerRuntime Runtime(Channel channel) => new(Entry(), new(7, "0.99.1"),
        (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel),
        (publication, _) => ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
    private static McpRequestOptions Options(Context context) => new(5000)
    { MaximumResponseBytes = 2_000_000, InvocationIdentity = new(context.OwnerId, context.OwnerGeneration, context.SessionGeneration, context.ToolCallId, context.ParentToolCallId) };
    private static ValueTask<string> Save(ReadOnlyMemory<byte> data, string extension, McpInvocationIdentity identity, CancellationToken token) => ValueTask.FromResult("synthetic/output" + extension);
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected failure"); }
    private static IEnumerable<Exception> OriginalFailures(Exception error)
    {
        if (error is AggregateException aggregate) return aggregate.InnerExceptions.SelectMany(OriginalFailures);
        if (error is McpResourceCallbackException { InnerException: { } inner }) return OriginalFailures(inner);
        return [error];
    }
    private static async Task Methods()
    {
        var channel = new Channel(); await using var runtime = Runtime(channel); await runtime.ConnectAsync(); var context = new Context();
        channel.Handler = (method, parameters, options, token) =>
        {
            Check(options.InvocationIdentity == Options(context).InvocationIdentity && options.TimeoutMilliseconds == 2000 && options.MaximumResponseBytes == 1_048_576 && options.OnProgress is not null);
            Check(method != "resources/read" || parameters!.Value.GetProperty("uri").GetString() == "data://a");
            return Task.FromResult(Channel.Response(method));
        };
        foreach (var tool in new[] { McpResourceTools.ListResources, McpResourceTools.ListTemplates, McpResourceTools.ReadResource })
        {
            var leaf = new McpResourceTools(() => [runtime.CaptureResourceServer(context)], Save);
            var result = await leaf.ExecuteAsync(tool, tool == McpResourceTools.ReadResource ? Json("""{"server":"demo","uri":"data://a"}""") : Json("""{"server":"demo"}"""), context);
            Check(result.Server == "demo");
        }
        Check(string.Join(',', channel.Requests) == "initialize,resources/list,resources/templates/list,resources/read");
    }
    private static async Task Admission()
    {
        var acquisitions = 0; var context = new Context();
        await using var runtime = new McpServerRuntime(Entry(), new(7, "0.99.1"), (_, _) => { acquisitions++; return ValueTask.FromResult<IMcpAdmittedRequestChannel>(new Channel()); },
            (publication, _) => ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
        void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Invalid dispatch admitted"); }
        Reject(() => runtime.RequestResourceAsync(6, "resources/list", null, Options(context), context));
        Reject(() => runtime.RequestResourceAsync(7, "tools/call", Json("{}"), Options(context), context));
        Reject(() => runtime.RequestResourceAsync(7, "resources/read", Json("{}"), Options(context), context));
        Reject(() => runtime.RequestResourceAsync(7, "resources/list", null, Options(context) with { InvocationIdentity = new("impostor", 3, 11, "resource-root", null) }, context));
        Reject(() => runtime.RequestResourceAsync(7, "resources/list", null, Options(context) with { OnProgress = (_, _) => ValueTask.CompletedTask }, context));
        Check(acquisitions == 0); await Task.CompletedTask;
    }
    private static async Task Capability()
    {
        var channel = new Channel { Resources = false }; await using var runtime = Runtime(channel); var context = new Context();
        Check(await Failure(runtime.RequestResourceAsync(7, "resources/list", null, Options(context), context)) is InvalidOperationException);
        Check(channel.Requests.SequenceEqual(new[] { "initialize" }));
        try { runtime.CaptureResourceServer(context); throw new InvalidOperationException("Absent resource capture admitted"); } catch (InvalidOperationException error) { Check(error.Message.Contains("ready admitted")); }
    }
    private static async Task HeldClose()
    {
        var channel = new Channel(); var runtime = Runtime(channel); await runtime.ConnectAsync(); var context = new Context();
        var entered = Gate(); var original = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously); var cleanup = Gate(); channel.CloseRelease = cleanup.Task;
        channel.Handler = (_, _, _, _) => { entered.SetResult(); return original.Task; };
        var operation = runtime.RequestResourceAsync(7, "resources/list", null, Options(context), context); await entered.Task;
        var closing = runtime.CloseAsync(); await channel.CloseEntered.Task; Check(!operation.IsCompleted && !closing.IsCompleted && ReferenceEquals(closing, runtime.CloseAsync()));
        original.SetResult(Channel.Response("resources/list")); await Failure(operation); Check(!closing.IsCompleted); cleanup.SetResult(); await closing;
        Check(!runtime.Snapshot.Catalog.Connected);
    }
    private static async Task OwnedCancellation()
    {
        var channel = new Channel(); await using var runtime = Runtime(channel); await runtime.ConnectAsync(); using var cancel = new CancellationTokenSource(); var context = new Context();
        var entered = Gate(); var original = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken physical = default;
        channel.Handler = (_, _, _, token) => { physical = token; entered.SetResult(); return original.Task; };
        var leaf = new McpResourceTools(() => [runtime.CaptureResourceServer(context)], Save);
        var operation = leaf.ExecuteAsync(McpResourceTools.ListResources, Json("""{"server":"demo"}"""), context, cancel.Token);
        await entered.Task; cancel.Cancel(); Check(!operation.IsCompleted); original.SetCanceled(physical);
        Check(await Failure(operation) is OperationCanceledException && operation.IsCanceled);
    }
    private static async Task Faults()
    {
        var channel = new Channel(); await using var runtime = Runtime(channel); await runtime.ConnectAsync(); var context = new Context();
        var original = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously); var oce = new OperationCanceledException("faulted OCE"); var cleanup = new IOException("request cleanup");
        channel.Handler = (_, _, _, _) => original.Task; var operation = runtime.RequestResourceAsync(7, "resources/list", null, Options(context), context);
        original.SetException(new Exception[] { oce, cleanup }); var failure = await Failure(operation);
        Check(operation.IsFaulted && failure is McpResourceCallbackException retained && ReferenceEquals(retained.Original, original.Task));
        Check(OriginalFailures(failure).Contains(oce) && OriginalFailures(failure).Contains(cleanup));
        channel.Handler = (_, _, _, _) => throw new OperationCanceledException("synchronous OCE"); operation = runtime.RequestResourceAsync(7, "resources/list", null, Options(context), context);
        Check(await Failure(operation) is McpResourceCallbackException && operation.IsFaulted);
    }
    private static async Task Disconnect()
    {
        var first = new Channel(); var second = new Channel(); var acquisitions = 0; var context = new Context(); var cleanup = Gate(); first.CloseRelease = cleanup.Task;
        await using var runtime = new McpServerRuntime(Entry(), new(7, "0.99.1"), (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(++acquisitions == 1 ? first : second),
            (publication, _) => ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
        await runtime.ConnectAsync(); var disconnected = new McpRuntimeDisconnectedException("definite disconnect");
        first.Handler = (_, _, _, _) => Task.FromException<JsonData>(disconnected);
        var operation = runtime.RequestResourceAsync(7, "resources/read", Json("""{"uri":"data://a"}"""), Options(context), context);
        await first.CloseEntered.Task; Check(!operation.IsCompleted && !runtime.Snapshot.Catalog.Connected && acquisitions == 1); cleanup.SetResult();
        Check(OriginalFailures(await Failure(operation)).Contains(disconnected));
        await runtime.RequestResourceAsync(7, "resources/list", null, Options(context), context);
        Check(acquisitions == 2 && first.Requests.Count(method => method == "resources/read") == 1 && second.Requests.Count(method => method == "resources/read") == 0);
    }
    private static async Task Progress()
    {
        var channel = new Channel(); var runtime = Runtime(channel); await runtime.ConnectAsync(); var entered = Gate(); var report = Gate(); var context = new Context();
        context.Report = (_, _) =>
        {
            try { runtime.CloseAsync(); throw new InvalidOperationException("Progress close reentry admitted"); } catch (InvalidOperationException error) { Check(error.Message.Contains("own close")); }
            try { runtime.CaptureResourceServer(context); throw new InvalidOperationException("Progress capture reentry admitted"); } catch (InvalidOperationException error) { Check(error.Message.Contains("cannot capture")); }
            entered.SetResult(); return new(report.Task);
        };
        channel.Handler = (_, _, options, token) => { _ = options.OnProgress!(new(1, Message: "native progress"), token); return Task.FromResult(Channel.Response("resources/list")); };
        var operation = runtime.RequestResourceAsync(7, "resources/list", null, Options(context), context); await entered.Task; Check(!operation.IsCompleted);
        var closing = runtime.CloseAsync(); await channel.CloseEntered.Task; Check(!closing.IsCompleted); report.SetResult(); await Failure(operation); await closing;
    }
    private static async Task ProgressFaults()
    {
        var channel = new Channel(); await using var runtime = Runtime(channel); await runtime.ConnectAsync(); var context = new Context(); var entered = Gate();
        var report = Gate(); var oce = new OperationCanceledException("faulted progress OCE"); var cleanup = new IOException("progress cleanup");
        context.Report = (_, _) => { entered.SetResult(); return new(report.Task); };
        channel.Handler = (_, _, options, token) => { _ = options.OnProgress!(new(1), token); return Task.FromResult(Channel.Response("resources/list")); };
        var operation = runtime.RequestResourceAsync(7, "resources/list", null, Options(context), context); await entered.Task; Check(!operation.IsCompleted); report.SetException(new Exception[] { oce, cleanup });
        var failure = await Failure(operation); Check(operation.IsFaulted && OriginalFailures(failure).Contains(oce) && OriginalFailures(failure).Contains(cleanup));
    }
    private static async Task ProgressLimit()
    {
        var channel = new Channel(); var context = new Context();
        await using var runtime = new McpServerRuntime(Entry(), new(7, "0.99.1") { Limits = new(MaximumTools: 1) },
            (_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel),
            (publication, _) => ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
        await runtime.ConnectAsync();
        channel.Handler = async (_, _, options, token) =>
        {
            await options.OnProgress!(new(1), token);
            try { await options.OnProgress!(new(2), token); } catch (McpRuntimeProtocolException) { }
            return Channel.Response("resources/list");
        };
        var failure = await Failure(runtime.RequestResourceAsync(7, "resources/list", null, Options(context), context));
        Check(OriginalFailures(failure).Any(error => error is McpRuntimeProtocolException && error.Message.Contains("update limit")));
    }
    private static async Task Multicast()
    {
        var effects = 0; McpAdmittedChannelFactory acquire = (_, _) => { effects++; return ValueTask.FromResult<IMcpAdmittedRequestChannel>(new Channel()); };
        McpCatalogPublisher publish = (_, _) => { effects++; return ValueTask.FromResult(new McpCatalogPublicationReceipt(7, 1, true)); };
        foreach (var multipleAcquire in new[] { true, false })
        {
            try { _ = new McpServerRuntime(Entry(), new(7, "0.99.1"), multipleAcquire ? acquire + acquire : acquire, multipleAcquire ? publish : publish + publish); throw new InvalidOperationException("Multicast runtime admitted"); }
            catch (ArgumentException) { }
        }
        Check(effects == 0); await Task.CompletedTask;
    }
    private static async Task CloseFaults()
    {
        var channel = new Channel(); var runtime = Runtime(channel); await runtime.ConnectAsync(); var cleanup = Gate(); var first = new IOException("close first"); var second = new IOException("close cleanup"); channel.CloseOriginal = cleanup.Task;
        var closing = runtime.CloseAsync(); await channel.CloseEntered.Task; Check(!closing.IsCompleted); cleanup.SetException(new[] { first, second });
        // The synthetic channel exposes its close original; runtime must retain every fault of that original.
        var failure = await Failure(closing); Check(closing.IsFaulted && ReferenceEquals(closing, runtime.CloseAsync()));
        Check(OriginalFailures(failure).Contains(first) && OriginalFailures(failure).Contains(second));
    }
}
