using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.IncomingRequests;
using PiSharp.Extensions.Runtime.Mcp.Transport;

namespace PiSharp.Extensions.Runtime.Mcp.IncomingRequests;

/// <summary>Bounded incoming request ownership over one explicitly admitted response sender.</summary>
public sealed class McpIncomingRequestRegistry : IMcpIncomingRequestRegistry
{
    private sealed class Frame { internal bool Active = true; }
    private sealed class Entry(CancellationTokenSource stop)
    {
        internal readonly CancellationTokenSource Stop = stop;
        internal readonly TaskCompletionSource Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Cancellation;
        internal bool Retiring;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, McpIncomingRequestHandler> handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> incoming = new(StringComparer.Ordinal);
    private readonly List<Exception> failures = [];
    private readonly McpOperationSet operations = new();
    private readonly AsyncLocal<Frame?> frame = new();
    private readonly McpAdmittedIncomingResponseSender sender;
    private readonly Lazy<Task> close;
    private readonly int maximumMethods, maximumInflight, maximumResponseBytes, maximumRequests;
    private int admittedRequests;
    private Task[] closingDispatchers = [];
    private bool closed;

    public McpIncomingRequestRegistry(McpAdmittedIncomingResponseSender sender,
        int maximumMethods = 256, int maximumInflight = 64, int maximumResponseBytes = 1_048_576, int maximumRequests = 4096)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (sender.GetInvocationList().Length != 1) throw new ArgumentException("One admitted sender required.", nameof(sender));
        if (maximumMethods < 1 || maximumInflight < 1 || maximumResponseBytes < 128 || maximumRequests < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumMethods));
        this.sender = sender; this.maximumMethods = maximumMethods;
        this.maximumInflight = maximumInflight; this.maximumResponseBytes = maximumResponseBytes;
        this.maximumRequests = maximumRequests;
        close = new(CloseCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Action SetRequestHandler(string method, McpIncomingRequestHandler handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method); ArgumentNullException.ThrowIfNull(handler);
        if (handler.GetInvocationList().Length != 1) throw new ArgumentException("One handler required.", nameof(handler));
        lock (gate)
        {
            Check();
            if (!handlers.ContainsKey(method) && handlers.Count >= maximumMethods)
                throw new InvalidOperationException("Incoming method admission bound reached.");
            handlers[method] = handler;
        }
        // Match Pi's callback identity comparison: an old remover cannot remove a different replacement.
        return () => { lock (gate) { if (handlers.TryGetValue(method, out var current) && ReferenceEquals(current, handler)) handlers.Remove(method); } };
    }

    public Task DispatchAsync(JsonData request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request); token.ThrowIfCancellationRequested();
        McpWireJson.Validate(request);
        var value = request.Value;
        if (!value.TryGetProperty("method", out var method) || !value.TryGetProperty("id", out var id))
            throw new ArgumentException("Incoming request with method and id required.", nameof(request));
        var name = method.GetString()!; var key = McpWireJson.Key(id);
        McpIncomingRequestHandler? handler; Entry entry;
        lock (gate)
        {
            Check();
            if (incoming.Count >= maximumInflight || incoming.ContainsKey(key) || admittedRequests >= maximumRequests)
                throw new InvalidOperationException("Incoming ID/inflight admission refused.");
            handlers.TryGetValue(name, out handler);
            entry = new(CancellationTokenSource.CreateLinkedTokenSource(token));
            incoming.Add(key, entry); admittedRequests++;
        }
        return DispatchOwnedAsync(request, name, key, handler, entry);
    }

    public bool CancelIncoming(JsonData id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!McpWireJson.Id(id.Value)) throw new ArgumentException("Incoming id required.", nameof(id));
        lock (gate)
        {
            Check();
            if (!incoming.TryGetValue(McpWireJson.Key(id.Value), out var entry) || entry.Retiring) return false;
            // Nonblocking cancellation scheduling. The owning original must still settle before slot removal.
            entry.Cancellation ??= entry.Stop.CancelAsync();
            return true;
        }
    }

    private async Task DispatchOwnedAsync(JsonData request, string name, string key,
        McpIncomingRequestHandler? handler, Entry entry)
    {
        Exception? primary = null;
        try
        {
            await operations.RunAsync(async owned =>
            {
                var borrowed = new Frame(); frame.Value = borrowed;
                try
                {
                    JsonData response; Exception? callbackFailure = null;
                    var id = request.Value.GetProperty("id").GetRawText();
                    if (handler is null) response = Error(id, -32601, "Method not found: " + name);
                    else
                    {
                        Task<JsonData?>? original = null;
                        try
                        {
                            var parameters = request.Value.TryGetProperty("params", out var p) ? JsonData.Parse(p.GetRawText()) : null;
                            original = handler(parameters, owned).AsTask();
                            var result = await original.ConfigureAwait(false);
                            response = JsonData.Parse("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + (result?.ToString() ?? "{}") + "}");
                        }
                        catch (Exception error)
                        {
                            callbackFailure = Capture("handler", original, error);
                            response = Error(id, -32603, "Incoming request handler failed.");
                        }
                    }
                    Exception? sendFailure = null; Task? sendOriginal = null;
                    try
                    {
                        if (Encoding.UTF8.GetByteCount(response.ToString()) > maximumResponseBytes)
                            throw new InvalidOperationException("Incoming response byte bound reached.");
                        // Reply ownership survives request cancellation. Physical sender is always directly joined.
                        sendOriginal = sender(response, CancellationToken.None) ?? throw new InvalidOperationException("Sender returned no original task.");
                        await sendOriginal.ConfigureAwait(false);
                    }
                    catch (Exception error) { sendFailure = Capture("response", sendOriginal, error); }
                    if (callbackFailure is not null && sendFailure is not null) throw new AggregateException(callbackFailure, sendFailure);
                    if (callbackFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackFailure).Throw();
                    if (sendFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(sendFailure).Throw();
                }
                finally { borrowed.Active = false; frame.Value = null; }
            }, entry.Stop.Token).ConfigureAwait(false);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Task? cancellation;
            Exception? cancellationFailure = null;
            lock (gate) { entry.Retiring = true; cancellation = entry.Cancellation; }
            if (cancellation is not null)
                try { await cancellation.ConfigureAwait(false); } catch (Exception error) { cancellationFailure = Capture("cancellation", cancellation, error); }
            entry.Stop.Dispose();
            lock (gate) incoming.Remove(key);
            entry.Joined.TrySetResult();
            if (cancellationFailure is not null)
            {
                if (primary is not null) throw new AggregateException(primary, cancellationFailure);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
            }
        }
    }

    private McpIncomingOriginalException Capture(string phase, Task? original, Exception error)
    {
        var retained = new McpIncomingOriginalException(phase, original, original is { IsFaulted: true } ? original.Exception! : error);
        lock (gate) failures.Add(retained);
        return retained;
    }
    private static JsonData Error(string id, int code, string message) => JsonData.Parse(
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":" + code + ",\"message\":" + JsonSerializer.Serialize(message) + "}}");
    private void Check() { if (closed) throw new ObjectDisposedException(nameof(McpIncomingRequestRegistry)); }
    public Task CloseAsync()
    {
        if (frame.Value is { Active: true }) throw new InvalidOperationException("An incoming callback cannot join its own owner close.");
        return close.Value;
    }
    private Task CloseCoreAsync()
    {
        lock (gate) { closed = true; handlers.Clear(); closingDispatchers = incoming.Values.Select(entry => entry.Joined.Task).ToArray(); }
        return operations.CloseAsync(() => Task.CompletedTask, JoinEvidenceAsync);
    }
    private async Task JoinEvidenceAsync()
    {
        await Task.WhenAll(closingDispatchers).ConfigureAwait(false);
        Exception[] retained; lock (gate) retained = failures.ToArray();
        if (retained.Length == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(retained[0]).Throw();
        if (retained.Length > 1) throw new AggregateException(retained);
    }
}
