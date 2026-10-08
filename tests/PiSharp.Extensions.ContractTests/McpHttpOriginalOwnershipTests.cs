using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp.Transport;

// Source-only controls. The coordinator owns registration and execution.
internal static class McpHttpOriginalOwnershipTests
{
    internal const string Prefix = "mcp-http-originals.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "disconnect-before-held-notification-single-original", () => Notification(false, false)),
        (Prefix + "disconnect-before-held-notification-multiple-original-faults", () => Notification(true, false)),
        (Prefix + "disconnect-before-held-notification-faulted-oce", () => Notification(true, true)),
        (Prefix + "separate-notification-failures-retained-through-close", NotificationPair),
        (Prefix + "genuinely-canceled-notification-retains-successful-owned-close", NotificationCanceled),
        (Prefix + "notification-empty-aggregate-task-evidence", () => NotificationEvidence("empty", false)),
        (Prefix + "notification-empty-aggregate-sync-evidence", () => NotificationEvidence("empty", true)),
        (Prefix + "notification-nested-aggregate-task-evidence", () => NotificationEvidence("nested", false)),
        (Prefix + "notification-nested-aggregate-sync-evidence", () => NotificationEvidence("nested", true)),
        (Prefix + "notification-duplicate-aggregate-task-evidence", () => NotificationEvidence("duplicate", false)),
        (Prefix + "notification-duplicate-aggregate-sync-evidence", () => NotificationEvidence("duplicate", true)),
        (Prefix + "notification-single-oce-task-evidence", () => NotificationEvidence("oce", false)),
        (Prefix + "notification-single-oce-sync-evidence", () => NotificationEvidence("oce", true)),
        (Prefix + "notification-duplicate-task-entries-evidence", () => NotificationEvidence("entries", false)),
        (Prefix + "owned-get-995-abort-retains-original-without-disconnect", () => GetAbort("owned")),
        (Prefix + "nonstop-get-995-remains-original-failure", () => GetAbort("nonstop")),
        (Prefix + "unrelated-get-io-during-close-remains-failure", () => GetAbort("unrelated")),
        (Prefix + "faulted-get-oce-during-close-remains-failure", () => GetAbort("oce")),
        (Prefix + "owned-get-abort-does-not-hide-independent-disposal-failure", () => GetAbort("cleanup")),
        (Prefix + "unrelated-get-read-and-disposal-faults-both-retained", () => GetAbort("both"))
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new IOException("MCP original ownership control failed."); }
    private static async Task Observe(Task task) { try { await task; } catch (Exception) { } }
    private static async Task<Exception> Failure(Task task)
    { try { await task; } catch (Exception error) { return error; } throw new IOException("Expected original failure."); }
    private static IEnumerable<Exception> Walk(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) foreach (var item in Walk(inner)) yield return item;
        else if (error.InnerException is { } inner)
            foreach (var item in Walk(inner)) yield return item;
    }
    private sealed class Wire : IMcpWireTransport
    {
        internal McpWireCallbacks Callbacks = null!;
        public Task StartAsync(McpWireCallbacks callbacks, CancellationToken token) { Callbacks = callbacks; return Task.CompletedTask; }
        public Task SendAsync(JsonData message, CancellationToken token) => Task.CompletedTask;
        public Task SetProtocolVersionAsync(string version, CancellationToken token) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;
    }
    private static async Task Notification(bool multiple, bool oce)
    {
        var wire = new Wire(); var entered = Gate(); var original = Gate(); var calls = 0;
        Exception first = oce ? new OperationCanceledException("faulted notification OCE") : new IOException("notification first");
        var second = new IOException("notification second");
        var channel = new McpJsonRpcRequestChannel(wire, notification: (_, _, _) =>
        { calls++; entered.TrySetResult(); return new(original.Task); });
        Task? close = null;
        try
        {
            await channel.StartAsync(default);
            await wire.Callbacks.Receive(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/test\"}"));
            await entered.Task;
            wire.Callbacks.Disconnected(new McpRuntimeDisconnectedException("earlier EOF"));
            close = channel.CloseAsync(); Check(!close.IsCompleted && ReferenceEquals(close, channel.CloseAsync()));
            original.SetException(multiple ? new[] { first, second } : new[] { first });
            var failure = await Failure(close); Check(close.IsFaulted && calls == 1 && Walk(failure).Contains(first));
            var wrapper = failure as McpChannelCallbackException;
            Check(wrapper is not null && ReferenceEquals(wrapper.Original, original.Task));
            var evidence = wrapper!.Evidence as AggregateException;
            Check(evidence is not null && evidence.InnerExceptions.Count == (multiple ? 2 : 1) && ReferenceEquals(evidence.InnerExceptions[0], first));
            if (multiple) Check(ReferenceEquals(evidence!.InnerExceptions[1], second));
            Check(ReferenceEquals(failure, await Failure(channel.CloseAsync())));
        }
        finally { original.TrySetResult(); if (close is not null) await Observe(close); else await Observe(channel.CloseAsync()); }
    }
    private static async Task NotificationPair()
    {
        var wire = new Wire(); var firstEntered = Gate(); var secondEntered = Gate(); var secondOriginal = Gate(); var calls = 0;
        var first = new IOException("first notification"); var second = new IOException("second notification");
        var firstOriginal = Task.FromException(first);
        var channel = new McpJsonRpcRequestChannel(wire, notification: (_, _, _) =>
        {
            if (++calls == 1) { firstEntered.TrySetResult(); return new(firstOriginal); }
            secondEntered.TrySetResult(); return new(secondOriginal.Task);
        });
        Task? close = null;
        try
        {
            await channel.StartAsync(default);
            var message = JsonData.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/test\"}");
            await wire.Callbacks.Receive(message); await firstEntered.Task;
            await wire.Callbacks.Receive(message); await secondEntered.Task;
            close = channel.CloseAsync(); Check(!close.IsCompleted); secondOriginal.SetException(second);
            var error = await Failure(close); Check(calls == 2 && Walk(error).Contains(first) && Walk(error).Contains(second));
            var wrappers = Walk(error).OfType<McpChannelCallbackException>().ToArray();
            Check(wrappers.Length == 2 && ReferenceEquals(wrappers[0].Original, firstOriginal) && ReferenceEquals(wrappers[1].Original, secondOriginal.Task));
        }
        finally { secondOriginal.TrySetResult(); await Observe(close ?? channel.CloseAsync()); }
    }
    private static async Task NotificationCanceled()
    {
        var wire = new Wire(); var entered = Gate(); var original = Gate(); CancellationToken captured = default;
        var channel = new McpJsonRpcRequestChannel(wire, notification: (_, _, token) =>
        { captured = token; entered.TrySetResult(); return new(original.Task); });
        Task? close = null;
        try
        {
            await channel.StartAsync(default);
            await wire.Callbacks.Receive(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/test\"}"));
            await entered.Task; close = channel.CloseAsync(); Check(captured.IsCancellationRequested && !close.IsCompleted);
            original.SetCanceled(captured); await close;
        }
        finally { original.TrySetResult(); await Observe(close ?? channel.CloseAsync()); }
    }
    private static async Task NotificationEvidence(string shape, bool synchronous)
    {
        var wire = new Wire(); var entered = Gate(); var original = Gate(); var calls = 0;
        var leaf = new IOException("same original reference"); var empty = new AggregateException();
        Exception payload = shape switch
        {
            "empty" => empty,
            "nested" => new AggregateException(empty, new AggregateException(leaf)),
            "duplicate" => new AggregateException(leaf, leaf, empty),
            "oce" => new OperationCanceledException("faulted or synchronous OCE"),
            _ => leaf
        };
        var channel = new McpJsonRpcRequestChannel(wire, notification: (_, _, _) =>
        {
            calls++; entered.TrySetResult();
            if (synchronous)
            {
                wire.Callbacks.Disconnected(new McpRuntimeDisconnectedException("earlier synchronous EOF"));
                throw payload;
            }
            return new(original.Task);
        });
        Task? close = null;
        try
        {
            await channel.StartAsync(default);
            await wire.Callbacks.Receive(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/test\"}"));
            await entered.Task;
            if (!synchronous) wire.Callbacks.Disconnected(new McpRuntimeDisconnectedException("earlier EOF"));
            close = channel.CloseAsync();
            if (!synchronous)
            {
                Check(!close.IsCompleted);
                original.SetException(shape == "entries" ? new[] { payload, payload } : new[] { payload });
            }
            var failure = await Failure(close);
            Check(close.IsFaulted && calls == 1 && failure is McpChannelCallbackException);
            var wrapper = (McpChannelCallbackException)failure;
            Check(ReferenceEquals(wrapper.InnerException, wrapper.Evidence));
            if (synchronous) Check(wrapper.Original is null && ReferenceEquals(wrapper.Evidence, payload));
            else
            {
                Check(ReferenceEquals(wrapper.Original, original.Task) && original.Task.IsFaulted);
                var evidence = (AggregateException)wrapper.Evidence;
                Check(evidence.InnerExceptions.Count == (shape == "entries" ? 2 : 1));
                foreach (var item in evidence.InnerExceptions) Check(ReferenceEquals(item, payload));
            }
            if (shape == "empty") Check(((AggregateException)payload).InnerExceptions.Count == 0);
            if (shape == "nested") Check(ReferenceEquals(((AggregateException)payload).InnerExceptions[0], empty));
            if (shape == "duplicate")
            {
                var entries = ((AggregateException)payload).InnerExceptions;
                Check(entries.Count == 3 && ReferenceEquals(entries[0], leaf) && ReferenceEquals(entries[1], leaf) && ReferenceEquals(entries[2], empty));
            }
            Check(ReferenceEquals(close, channel.CloseAsync()) && ReferenceEquals(failure, await Failure(channel.CloseAsync())));
        }
        finally { original.TrySetResult(); await Observe(close ?? channel.CloseAsync()); }
    }
    private sealed class Body(Exception? cleanup) : MemoryStream
    {
        internal readonly TaskCompletionSource ReadEntered = Gate(), DisposeEntered = Gate();
        internal readonly TaskCompletionSource<int> ReadOriginal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { ReadEntered.TrySetResult(); return new(ReadOriginal.Task); }
        protected override void Dispose(bool disposing)
        { DisposeEntered.TrySetResult(); if (cleanup is not null) throw cleanup; }
        public override ValueTask DisposeAsync()
        { DisposeEntered.TrySetResult(); return cleanup is null ? ValueTask.CompletedTask : ValueTask.FromException(cleanup); }
    }
    private sealed class HttpOperation(HttpResponseMessage response) : IMcpAdmittedHttpRequestOperation
    {
        public ValueTask<HttpResponseMessage> SendAsync(CancellationToken token) => ValueTask.FromResult(response);
        public Task StopAsync() => Task.CompletedTask;
    }
    private static async Task GetAbort(string kind)
    {
        var cleanup = kind is "cleanup" or "both" ? new IOException("independent disposal original") : null;
        var body = new Body(cleanup); var disconnected = Gate(); Exception? reported = null; var requests = new List<string>();
        Exception read = kind is "unrelated" or "both" ? new IOException("unrelated IO during stop") : kind == "oce" ?
            new OperationCanceledException("faulted body OCE") : new IOException("native owner abort", new SocketException(995));
        var wire = new McpStreamableHttpTransport(new(new Uri("http://fixture.invalid/mcp"), ImmutableDictionary<string, string>.Empty, new(true, false)),
            (McpAdmittedHttpRequestFactory)(request =>
            {
                requests.Add(request.Method.Method);
                return new HttpOperation(request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) { Headers = { ContentType = new("text/event-stream") } } }
                    : new HttpResponseMessage(HttpStatusCode.Accepted));
            }));
        Task? close = null;
        try
        {
            await wire.StartAsync(new(_ => ValueTask.CompletedTask, error => { reported = error; disconnected.TrySetResult(); }), default);
            await wire.SendAsync(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"), default);
            await body.ReadEntered.Task;
            if (kind == "nonstop") { body.ReadOriginal.SetException(read); await disconnected.Task; }
            close = wire.CloseAsync(); await body.DisposeEntered.Task;
            if (kind != "nonstop") { Check(!close.IsCompleted); body.ReadOriginal.SetException(read); }
            if (kind == "owned")
            {
                await close; Check(reported is null && wire.OwnedReadAborts.Count == 1);
            }
            else
            {
                var failure = await Failure(close); Check(close.IsFaulted);
                Check(Walk(failure).Contains(cleanup ?? read));
                if (kind == "both") Check(Walk(failure).Contains(read));
                if (kind != "cleanup") Check(wire.OwnedReadAborts.Count == 0);
            }
            if (kind is "owned" or "cleanup")
                Check(wire.OwnedReadAborts.Count == 1 && ReferenceEquals(wire.OwnedReadAborts[0].Original, body.ReadOriginal.Task) && ReferenceEquals(wire.OwnedReadAborts[0].Cause, read));
            Check(ReferenceEquals(close, wire.CloseAsync()) && requests.SequenceEqual(new[] { "POST", "GET" }));
        }
        finally { body.ReadOriginal.TrySetException(read); if (close is not null) await Observe(close); else await Observe(wire.CloseAsync()); }
    }
}
