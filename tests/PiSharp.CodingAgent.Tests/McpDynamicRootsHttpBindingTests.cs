using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Roots;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp;
using PiSharp.Extensions.Mcp.IncomingRequests;

// Offline authored controls: HttpClient is explicitly borrowed and uses only this in-memory handler.
// The coordinator owns fixture registration, compilation and actual execution.
internal static class McpDynamicRootsHttpBindingTests
{
    internal sealed record OriginalEvidence(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly ConcurrentQueue<OriginalEvidence> originals = new();
    private static readonly ConcurrentDictionary<Task, OriginalEvidence> joined = new(ReferenceEqualityComparer.Instance);
    internal static OriginalEvidence[] CapturedOriginals => originals.ToArray();
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("mcp-http-dynamic-roots.admission-rejects-static-multicast-and-foreign-entry-before-http", Admission),
        ("mcp-http-dynamic-roots.actual-runtime-initialize-capability-and-changing-roots-list", RuntimeRequests),
        ("mcp-http-dynamic-roots.channel-close-joins-held-original-and-refuses-stale-use", HeldClose),
        ("mcp-http-dynamic-roots.independent-channel-owners-and-exact-multifault-original", IndependentFault)
    ];
    private static void Require(bool value) { if (!value) throw new IOException("Dynamic roots HTTP binding control failed."); }
    private static McpServerEntry Entry() => new("roots", McpConfigurationReader.Validate("roots",
        JsonData.Parse("{\"url\":\"https://inert.invalid/mcp\",\"exposure\":\"direct\"}").Value).Config!,
        "explicit-offline-control", McpConfigurationScope.Extension);
    private static McpHttpBinding Binding() => new(new Uri("https://inert.invalid/mcp"),
        ImmutableDictionary<string, string>.Empty, new(false, false));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception?> Join(string phase, Task task)
    {
        if (joined.TryGetValue(task, out var prior)) return prior.Aggregate ?? prior.Direct;
        Exception? direct = null;
        try { await task.ConfigureAwait(false); } catch (Exception error) { direct = error; }
        var aggregate = task.IsFaulted ? task.Exception : null;
        var record = new OriginalEvidence(phase, task, aggregate, direct);
        joined.TryAdd(task, record); originals.Enqueue(record);
        return aggregate ?? direct;
    }
    private static async Task WaitSignal(Task signal, Task triggeringOriginal)
    {
        var failures = new List<Exception>(); var deadlineSource = new CancellationTokenSource();
        var deadline = Task.Delay(TimeSpan.FromSeconds(10), deadlineSource.Token);
        try
        {
            var winner = await Task.WhenAny(signal, triggeringOriginal, deadline);
            if (ReferenceEquals(winner, triggeringOriginal))
            {
                var triggerError = await Join("signal-trigger-original", triggeringOriginal);
                if (triggerError is not null) throw triggerError;
                // JSON-RPC reply can settle before its asynchronously dispatched server callback.
                winner = await Task.WhenAny(signal, deadline);
            }
            if (ReferenceEquals(winner, deadline)) throw new TimeoutException("Required roots signal did not arrive within the finite control bound.");
            var signalError = await Join("required-signal-original", signal); if (signalError is not null) throw signalError;
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { var cancel = deadlineSource.CancelAsync(); var error = await Join("signal-deadline-cancel", cancel); if (error is not null) failures.Add(error); }
            catch (Exception error) { failures.Add(error); }
            var deadlineError = await Join("signal-deadline-original", deadline);
            if (deadline.IsFaulted && deadlineError is not null) failures.Add(deadlineError);
            try { deadlineSource.Dispose(); } catch (Exception error) { failures.Add(error); }
        }
        Throw(failures);
    }
    private static async Task<IMcpAdmittedRequestChannel> Acquire(McpAdmittedChannelFactory factory, McpServerEntry entry)
    {
        var original = factory(entry, default).AsTask();
        var error = await Join("actual-channel-acquisition", original);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return original.Result;
    }
    private static void Throw(List<Exception> failures)
    {
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }
    private static bool Contains(Exception? root, Exception leaf)
    {
        if (root is null) return false;
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var queue = new Queue<Exception>(); queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var item = queue.Dequeue(); if (ReferenceEquals(item, leaf)) return true;
            if (!seen.Add(item)) continue;
            IEnumerable<Exception> children = item is AggregateException all ? all.InnerExceptions : item.InnerException is { } inner ? [inner] : [];
            foreach (var child in children) queue.Enqueue(child);
        }
        return false;
    }
    private static T? Find<T>(Exception? root, Func<T, bool> matches) where T : Exception
    {
        if (root is null) return null;
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var queue = new Queue<Exception>(); queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var item = queue.Dequeue(); if (!seen.Add(item)) continue;
            if (item is T target && matches(target)) return target;
            IEnumerable<Exception> children = item is AggregateException all ? all.InnerExceptions : item.InnerException is { } inner ? [inner] : [];
            foreach (var child in children) queue.Enqueue(child);
        }
        return null;
    }
    private static async Task Admission()
    {
        using var server = new Server(); using var client = new HttpClient(server, false); var entry = Entry(); var calls = 0;
        McpAdmittedDynamicRootsProvider provider = _ => { calls++; return ValueTask.FromResult(JsonData.Parse("[]")); };
        try { AdmittedMcpHttpChannelFactory.Create(entry, Binding(), client, new(1, "0.99.1", JsonData.Parse("[]")), dynamicRoots: provider); throw new IOException("Ambiguous roots admitted."); }
        catch (ArgumentException) { }
        try { AdmittedMcpHttpChannelFactory.Create(entry, Binding(), client, new(1, "0.99.1"), dynamicRoots: provider + provider); throw new IOException("Multicast roots admitted."); }
        catch (ArgumentException) { }
        var factory = AdmittedMcpHttpChannelFactory.Create(entry, Binding(), client, new(1, "0.99.1"), dynamicRoots: provider);
        Task<IMcpAdmittedRequestChannel>? unexpected = null;
        try { unexpected = factory(Entry(), default).AsTask(); await unexpected; throw new IOException("Foreign entry admitted."); }
        catch (InvalidOperationException) when (unexpected is null) { }
        Require(calls == 0 && server.Calls == 0);
    }
    private static async Task RuntimeRequests()
    {
        using var server = new Server(); using var client = new HttpClient(server, false); var entry = Entry(); var count = 0;
        var options = new McpRuntimeOptions(11, "0.99.1");
        var factory = AdmittedMcpHttpChannelFactory.Create(entry, Binding(), client, options,
            dynamicRoots: _ => ValueTask.FromResult(JsonData.Parse("[{\"uri\":\"file:///inert-" + Interlocked.Increment(ref count) + "\",\"unknown\":9007199254740993}]")));
        var runtime = new McpServerRuntime(entry, options, factory, (publication, _) =>
            ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
        var failures = new List<Exception>();
        try
        {
            var connect = runtime.ConnectAsync(); var connectFailure = await Join("runtime-connect", connect); if (connectFailure is not null) throw connectFailure;
            await WaitSignal(server.FirstResponse.Task, connect); Require(server.RootsAdvertised && runtime.Snapshot.Catalog.Connected);
            var refresh = runtime.RefreshToolsAsync(); var refreshFailure = await Join("runtime-refresh", refresh); if (refreshFailure is not null) throw refreshFailure;
            await WaitSignal(server.SecondResponse.Task, refresh);
            var responses = server.RootReplies.ToArray();
            Require(count == 2 && responses.Length == 2 && responses[0].Value.GetProperty("result").GetProperty("roots")[0].GetProperty("uri").GetString() == "file:///inert-1" &&
                responses[1].Value.GetProperty("result").GetProperty("roots")[0].GetProperty("uri").GetString() == "file:///inert-2" &&
                responses[0].Value.GetProperty("result").GetProperty("roots")[0].GetProperty("unknown").GetRawText() == "9007199254740993");
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { var closing = runtime.DisposeAsync().AsTask(); var error = await Join("runtime-close", closing); if (error is not null) failures.Add(error); }
            catch (Exception error) { failures.Add(error); }
        }
        Throw(failures);
    }
    private static async Task HeldClose()
    {
        using var server = new Server(); using var client = new HttpClient(server, false); var entry = Entry(); var entered = Gate();
        var original = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; CancellationToken callbackToken = default;
        IMcpAdmittedRequestChannel? acquired = null; Task? unexpectedReentry = null; var reentryRefused = false;
        var factory = AdmittedMcpHttpChannelFactory.Create(entry, Binding(), client, new(1, "0.99.1"), dynamicRoots: token =>
        {
            callbackToken = token; Interlocked.Increment(ref calls);
            try { unexpectedReentry = (acquired ?? throw new IOException("No actual acquired channel.")).CloseAsync(); }
            catch (InvalidOperationException) { reentryRefused = true; }
            entered.TrySetResult(); return new(original.Task);
        });
        var channel = acquired = await Acquire(factory, entry); Task? request = null; Task? closing = null; var failures = new List<Exception>();
        try
        {
            var start = channel.StartAsync(default).AsTask(); var startFailure = await Join("held-start", start); if (startFailure is not null) throw startFailure;
            request = channel.RequestAsync("tools/list", JsonData.EmptyObject, new(0), default).AsTask();
            await WaitSignal(entered.Task, request);
            closing = channel.CloseAsync(); Require(!closing.IsCompleted && !original.Task.IsCompleted && ReferenceEquals(closing, channel.CloseAsync()));
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            original.TrySetResult(JsonData.Parse("[]"));
            try
            {
                var callbackError = await Join("held-provider-original", original.Task); if (callbackError is not null) failures.Add(callbackError);
                if (request is not null) { var requestError = await Join("held-client-request", request); if (request.IsFaulted && requestError is not null) failures.Add(requestError); }
                var closeError = await Join("held-channel-close", closing ?? channel.CloseAsync());
                // Physical response retirement can fault after wire stop. Preserve its full original
                // graph and require the actual response phase, rather than accepting arbitrary failure.
                if (closeError is not null && Find<McpIncomingOriginalException>(closeError, fault => fault.Phase == "response") is null) failures.Add(closeError);
                if (failures.Count > 0 && closeError is not null) failures.Add(closeError);
            }
            catch (Exception error) { failures.Add(error); }
            if (unexpectedReentry is not null)
            {
                var reentryError = await Join("unexpected-callback-close-original", unexpectedReentry);
                if (reentryError is not null) failures.Add(reentryError);
            }
        }
        try { Require(original.Task.IsCompletedSuccessfully && callbackToken.IsCancellationRequested && reentryRefused); }
        catch (Exception error) { failures.Add(error); }
        try { var stale = channel.RequestAsync("tools/list", JsonData.EmptyObject, new(0), default).AsTask(); var error = await Join("stale-client-request", stale); Require(error is not null); }
        catch (ObjectDisposedException) { }
        catch (Exception error) { failures.Add(error); }
        try { Require(calls == 1); } catch (Exception error) { failures.Add(error); }
        Throw(failures);
    }
    private static async Task IndependentFault()
    {
        using var server = new Server(); using var client = new HttpClient(server, false); var entry = Entry();
        var leaf = new IOException("shared roots fault"); var nested = new AggregateException(leaf, leaf);
        var userOriginal = Task.FromException<JsonData>(new AggregateException(leaf, nested)); var call = 0;
        var factory = AdmittedMcpHttpChannelFactory.Create(entry, Binding(), client, new(1, "0.99.1"), dynamicRoots: _ =>
            Interlocked.Increment(ref call) == 1 ? new(userOriginal) : ValueTask.FromResult(JsonData.Parse("[]")));
        var first = await Acquire(factory, entry); IMcpAdmittedRequestChannel? second = null; var failures = new List<Exception>();
        try
        {
            second = await Acquire(factory, entry);
            var startFirst = first.StartAsync(default).AsTask(); var error = await Join("fault-first-start", startFirst); if (error is not null) throw error;
            var request = first.RequestAsync("tools/list", JsonData.EmptyObject, new(0), default).AsTask(); await Join("fault-first-request", request);
            await WaitSignal(server.FirstResponse.Task, request);
            var firstClose = first.CloseAsync(); var firstFault = await Join("fault-first-close", firstClose);
            Require(firstFault is not null && Contains(firstFault, leaf) && Contains(firstFault, nested) && userOriginal.IsFaulted);
            Require(Find<McpRootsCallbackException>(firstFault, fault => ReferenceEquals(fault.Original, userOriginal)) is not null);
            var startSecond = second.StartAsync(default).AsTask(); error = await Join("peer-start", startSecond); if (error is not null) throw error;
            var peer = second.RequestAsync("tools/list", JsonData.EmptyObject, new(0), default).AsTask(); error = await Join("peer-request", peer); if (error is not null) throw error;
            await WaitSignal(server.SecondResponse.Task, peer); Require(call == 2 && !ReferenceEquals(first.CloseAsync(), second.CloseAsync()));
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            var userFault = await Join("fault-provider-original", userOriginal);
            try { var error = await Join("fault-owner-final-close", first.CloseAsync()); if (failures.Count > 0 && error is not null) failures.Add(error); }
            catch (Exception error) { failures.Add(error); }
            try { if (second is not null) { var error = await Join("peer-final-close", second.CloseAsync()); if (error is not null) failures.Add(error); } }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0 && userFault is not null) failures.Add(userFault);
        }
        Throw(failures);
    }
    private sealed class Server : HttpMessageHandler
    {
        internal int Calls; private int rootsId;
        internal bool RootsAdvertised;
        internal readonly ConcurrentQueue<JsonData> RootReplies = new();
        internal readonly TaskCompletionSource FirstResponse = Gate(), SecondResponse = Gate();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Require(request.Method == HttpMethod.Post && request.RequestUri == Binding().Endpoint);
            var body = await (request.Content ?? throw new IOException("Missing JSON-RPC body.")).ReadAsStringAsync(token);
            var message = JsonData.Parse(body).Value;
            if (!message.TryGetProperty("method", out var method))
            {
                RootReplies.Enqueue(JsonData.FromElement(message)); if (RootReplies.Count == 1) FirstResponse.TrySetResult(); else SecondResponse.TrySetResult();
                return new(HttpStatusCode.Accepted);
            }
            if (method.GetString() == "notifications/initialized") return new(HttpStatusCode.Accepted);
            var id = message.GetProperty("id").GetRawText();
            if (method.GetString() == "initialize")
            {
                RootsAdvertised = message.GetProperty("params").GetProperty("capabilities").TryGetProperty("roots", out _);
                return Response("application/json", "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"offline-roots\",\"version\":\"1\"}}}");
            }
            Require(method.GetString() == "tools/list");
            var roots = Interlocked.Increment(ref rootsId);
            return Response("text/event-stream", "data: {\"jsonrpc\":\"2.0\",\"id\":\"roots-" + roots + "\",\"method\":\"roots/list\"}\n\n" +
                "data: {\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[]}}\n\n");
        }
        private static HttpResponseMessage Response(string media, string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, media) };
    }
}
