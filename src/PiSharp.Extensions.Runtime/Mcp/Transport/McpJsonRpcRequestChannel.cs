using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Mcp.IncomingRequests;
using PiSharp.Extensions.Mcp.Roots;
using PiSharp.Extensions.Runtime.Mcp.IncomingRequests;
using PiSharp.Extensions.Runtime.Mcp.Roots;

namespace PiSharp.Extensions.Runtime.Mcp.Transport;

/// <summary>Native callback evidence, preserving the actual task and its complete fault tree.</summary>
public sealed class McpChannelCallbackException(Task? original, Exception evidence)
    : IOException("MCP channel callback failed.", evidence)
{
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
}

/// <summary>Bounded MCP JSON-RPC client over a transferred wire transport. No ambient acquisition or host grants.</summary>
public sealed class McpJsonRpcRequestChannel : IMcpAdmittedRequestChannel, IMcpNotificationRetirementChannel
{
    private sealed class Pending(long id, McpRequestOptions options, CancellationToken token)
    {
        internal readonly long Id = id;
        internal readonly McpRequestOptions Options = options; internal readonly CancellationToken Token = token;
        internal readonly TaskCompletionSource<JsonData> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationTokenSource SendStop = new();
        internal Task CancelWork = Task.CompletedTask;
        internal Task Progress = Task.CompletedTask; internal ITimer? Timer; internal bool CancelWire, Accepting = true;
    }
    private readonly IMcpWireTransport wire; private readonly McpTransportLimits limits; private readonly TimeProvider clock;
    private readonly McpNotificationHandler? notification; private readonly McpOperationSet operations = new();
    private readonly object gate = new(); private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly HashSet<Task> callbackSlots = [], callbackOriginals = []; private readonly AsyncLocal<bool> inCallback = new();
    private readonly Lazy<Task> close, closeCore; private long nextId; private bool started, closed; private JsonData? roots; private Exception? failure;
    private sealed class NotificationFrame { internal Task Original = null!; internal bool Active; }
    private readonly AsyncLocal<NotificationFrame?> notificationFrame = new();
    private readonly TaskCompletionSource<Task?> retirementRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task retirementLoop;
    private IMcpAdmittedChannelRetirement? retirementReceipt;
    private Task notificationTail = Task.CompletedTask;
    private readonly List<Exception> notificationFailures = [];
    private int failedNotificationCount;
    private McpIncomingRequestRegistry? incomingRequests;
    private McpDynamicRootsHandler? dynamicRoots;
    public McpJsonRpcRequestChannel(IMcpWireTransport wire, McpTransportLimits? limits = null, McpNotificationHandler? notification = null, TimeProvider? clock = null)
    {
        this.wire = wire ?? throw new ArgumentNullException(nameof(wire)); this.limits = limits ?? new(); this.notification = notification; this.clock = clock ?? TimeProvider.System;
        if (notification?.GetInvocationList().Length > 1) throw new ArgumentException("One joined notification callback is required.", nameof(notification));
        McpWireJson.Limits(this.limits);
        closeCore = new(() => operations.CloseAsync(CloseWireAndIncomingAsync, JoinCallbacks), LazyThreadSafetyMode.ExecutionAndPublication);
        // Created before this channel can admit a borrowed callback; it never inherits that callback's guard.
        retirementLoop = RetirementLoopAsync();
        close = new(CloseJoinedAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }
    public ValueTask ConfigureRootsAsync(JsonData supplied, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (supplied.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException("MCP roots array required.");
        lock (gate)
        {
            Check(); if (started) throw new InvalidOperationException("Install roots before MCP start");
            if (dynamicRoots is not null) throw new InvalidOperationException("Dynamic roots already owns roots/list.");
            roots = supplied;
            incomingRequests?.SetRequestHandler("roots/list", (_, _) => ValueTask.FromResult<JsonData?>(McpWireJson.Json(new { roots = supplied.Value })));
        }
        return ValueTask.CompletedTask;
    }
    /// <summary>Install one explicitly admitted dynamic metadata provider before start. No URI access or host grant.</summary>
    public void ConfigureDynamicRoots(McpAdmittedDynamicRootsProvider provider)
    {
        lock (gate)
        {
            Check();
            if (started || roots is not null || dynamicRoots is not null)
                throw new InvalidOperationException("Install one roots mode before MCP start.");
            var handler = new McpDynamicRootsHandler(provider, limits.MaximumFrameBytes, maximumRequests: limits.MaximumCallbacks);
            EnsureIncoming().SetRequestHandler("roots/list", async (_, token) => await handler.HandleRootsListAsync(token).ConfigureAwait(false));
            dynamicRoots = handler;
        }
    }
    /// <summary>Register an incoming callback on this actual admitted transport; returned removal is handler-identity bound.</summary>
    public Action SetRequestHandler(string method, McpIncomingRequestHandler handler)
    {
        lock (gate)
        {
            Check();
            if (method == "roots/list" && (roots is not null || dynamicRoots is not null))
                throw new InvalidOperationException("Configured roots owns roots/list.");
            return EnsureIncoming().SetRequestHandler(method, handler);
        }
    }
    private McpIncomingRequestRegistry EnsureIncoming()
    {
        if (incomingRequests is { } existing) return existing;
        var registry = new McpIncomingRequestRegistry((response, token) => wire.SendAsync(response, token),
            maximumInflight: limits.MaximumInflight, maximumResponseBytes: limits.MaximumFrameBytes,
            maximumRequests: limits.MaximumCallbacks);
        registry.SetRequestHandler("ping", (_, _) => ValueTask.FromResult<JsonData?>(JsonData.EmptyObject));
        if (roots is { } supplied)
            registry.SetRequestHandler("roots/list", (_, _) => ValueTask.FromResult<JsonData?>(McpWireJson.Json(new { roots = supplied.Value })));
        incomingRequests = registry;
        return registry;
    }
    public ValueTask StartAsync(CancellationToken token) => new(operations.RunAsync(async owned =>
    {
        lock (gate) { Check(); if (started) throw new InvalidOperationException("MCP channel already started"); started = true; }
        await wire.StartAsync(new(Receive, Fail), owned).ConfigureAwait(false); owned.ThrowIfCancellationRequested();
    }, token));
    public ValueTask SetProtocolVersionAsync(string version, CancellationToken token) => new(operations.RunAsync(owned => wire.SetProtocolVersionAsync(version, owned), token));
    public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => new(operations.RunAsync(async owned =>
    {
        lock (gate) { Check(); if (!started) throw new McpRuntimeDisconnectedException("MCP channel not started"); }
        await wire.SendAsync(Message(method, parameters), owned).ConfigureAwait(false);
    }, token));
    public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(method); ArgumentNullException.ThrowIfNull(options);
        if (parameters is not null && parameters.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("MCP request params object required");
        if (options.MaximumResponseBytes <= 0) throw new ArgumentException("Finite response limit required");
        lock (gate)
        {
            if (method == "initialize" && dynamicRoots is not null)
            {
                if (parameters is null) throw new ArgumentException("Dynamic roots initialize params required.");
                var fields = parameters.Value.EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value, StringComparer.Ordinal);
                var capabilities = fields.TryGetValue("capabilities", out var raw) && raw is JsonElement json && json.ValueKind == JsonValueKind.Object
                    ? json.EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                capabilities.TryAdd("roots", new { }); fields["capabilities"] = capabilities;
                parameters = McpWireJson.Json(fields);
            }
        }
        return new(operations.RunAsync(owned => RequestCore(method, parameters, options, owned), token));
    }
    private async Task<JsonData> RequestCore(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
    {
        Pending entry;
        lock (gate)
        {
            Check(); if (!started) throw new McpRuntimeDisconnectedException("MCP channel not started");
            if (pending.Count >= limits.MaximumInflight || nextId >= limits.MaximumRequestId) throw new InvalidOperationException("MCP inflight/ID admission limit exceeded");
            entry = new(++nextId, options, token); pending.Add("n:" + entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), entry);
            if (double.IsFinite(options.TimeoutMilliseconds) && options.TimeoutMilliseconds > 0)
            {
                var timeout = Math.Min(options.TimeoutMilliseconds, uint.MaxValue - 1d);
                entry.Timer = clock.CreateTimer(_ => Cancel(entry, new McpRequestTimeoutException(options.TimeoutMilliseconds), true), null, TimeSpan.FromMilliseconds(timeout), Timeout.InfiniteTimeSpan);
            }
        }
        using var registration = token.Register(() => Cancel(entry, new OperationCanceledException(token), true));
        JsonData? result = null; Exception? primary = null;
        try
        {
            var fields = parameters is null ? new Dictionary<string, object?>(StringComparer.Ordinal) : parameters.Value.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
            if (options.OnProgress is not null)
            {
                var meta = fields.TryGetValue("_meta", out var raw) && raw is JsonElement json && json.ValueKind == JsonValueKind.Object
                    ? json.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal) : new Dictionary<string, object?>(StringComparer.Ordinal);
                meta["progressToken"] = entry.Id; fields["_meta"] = meta;
            }
            var request = Message(method, parameters is null && options.OnProgress is null ? null : McpWireJson.Json(fields), entry.Id);
            _ = McpWireJson.Encode(request, limits.MaximumFrameBytes);
            // Sending is directly joined before observing response/cancellation, including held physical sends.
            await wire.SendAsync(request, entry.SendStop.Token).ConfigureAwait(false);
            result = await entry.Reply.Task.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Deadline/caller outcome owns the result after its physical send has settled.
            Cancel(entry, error, false);
            try { result = await entry.Reply.Task.ConfigureAwait(false); primary = error; }
            catch (Exception outcome) { primary = ReferenceEquals(error, outcome) || error is OperationCanceledException ? outcome : new AggregateException(error, outcome); }
        }
        finally
        {
            lock (gate) { entry.Accepting = false; pending.Remove("n:" + entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            if (entry.Timer is not null) await entry.Timer.DisposeAsync().ConfigureAwait(false);
            registration.Dispose();
        }
        var errors = new List<Exception>(); if (primary is not null) errors.Add(primary);
        Task cancelWork; lock (gate) cancelWork = entry.CancelWork;
        try { await cancelWork.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        entry.SendStop.Dispose();
        bool cancel; Task progress; lock (gate) { cancel = entry.CancelWire && method != "initialize" && !closed; progress = entry.Progress; }
        if (cancel)
            try { await wire.SendAsync(Message("notifications/cancelled", McpWireJson.Json(new { requestId = entry.Id, reason = primary is McpRequestTimeoutException ? "Request timed out" : "Aborted" })), operations.Lifetime.Token).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        try { await progress.ConfigureAwait(false); } catch (Exception error) { if (!errors.Any(existing => ReferenceEquals(existing, error))) errors.Add(error); }
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
        token.ThrowIfCancellationRequested(); return result ?? throw new McpRuntimeProtocolException("MCP result missing");
    }
    private void Cancel(Pending entry, Exception error, bool sendCancel)
    {
        lock (gate)
        {
            if (!entry.Accepting || !entry.Reply.TrySetException(error)) return;
            entry.Accepting = false; entry.CancelWire = sendCancel;
            entry.CancelWork = entry.SendStop.CancelAsync();
        }
    }
    private ValueTask Receive(JsonData message)
    {
        if (McpWireJson.Utf8.GetByteCount(message.ToString()) > limits.MaximumFrameBytes) throw new McpRuntimeProtocolException("MCP channel frame byte limit exceeded");
        McpWireJson.Validate(message); var value = message.Value;
        if (!value.TryGetProperty("method", out var method))
        {
            lock (gate)
            {
                if (closed || !pending.TryGetValue(McpWireJson.Key(value.GetProperty("id")), out var entry) || !entry.Accepting) return ValueTask.CompletedTask;
                entry.Accepting = false;
                if (value.TryGetProperty("error", out var error))
                    entry.Reply.TrySetException(new McpRemoteErrorException(error.GetProperty("code").GetDouble(), error.GetProperty("message").GetString()!, error.TryGetProperty("data", out var data) ? JsonData.FromElement(data) : null));
                else
                {
                    var result = JsonData.FromElement(value.GetProperty("result"));
                    if (McpWireJson.Utf8.GetByteCount(result.ToString()) > entry.Options.MaximumResponseBytes) entry.Reply.TrySetException(new McpRuntimeProtocolException("MCP result byte limit exceeded"));
                    else entry.Reply.TrySetResult(result);
                }
            }
            return ValueTask.CompletedTask;
        }
        var name = method.GetString()!; var parameters = value.TryGetProperty("params", out var rawParams) ? JsonData.FromElement(rawParams) : null;
        McpIncomingRequestRegistry? incomingRegistry; lock (gate) incomingRegistry = incomingRequests;
        if (value.TryGetProperty("id", out var incomingId))
        {
            if (incomingRegistry is not null)
            {
                _ = Dispatch(() => incomingRegistry.DispatchAsync(message, operations.Lifetime.Token));
                return ValueTask.CompletedTask;
            }
            var id = JsonData.FromElement(incomingId);
            _ = Dispatch(async () =>
            {
                JsonData response;
                if (name == "ping") response = McpWireJson.Json(new { jsonrpc = "2.0", id = id.Value, result = new { } });
                else if (name == "roots/list" && roots is { } supplied) response = McpWireJson.Json(new { jsonrpc = "2.0", id = id.Value, result = new { roots = supplied.Value } });
                else response = McpWireJson.Json(new { jsonrpc = "2.0", id = id.Value, error = new { code = -32601, message = "Method not found: " + name } });
                await wire.SendAsync(response, operations.Lifetime.Token).ConfigureAwait(false);
            });
        }
        else if (name == "notifications/cancelled" && incomingRegistry is not null && parameters is { } cancellation)
        {
            if (cancellation.Value.ValueKind == JsonValueKind.Object &&
                cancellation.Value.TryGetProperty("requestId", out var requestId) && McpWireJson.Id(requestId))
                lock (gate) { if (!closed) incomingRegistry.CancelIncoming(JsonData.FromElement(requestId)); }
        }
        else if (name == "notifications/progress" && parameters is { } progressParams) Progress(progressParams);
        else if (notification is { } handler)
        {
            lock (gate)
            {
                var previous = notificationTail;
                var frame = new NotificationFrame();
                notificationTail = Dispatch(async () =>
                {
                    await previous.ConfigureAwait(false); notificationFrame.Value = frame; lock (gate) frame.Active = true;
                    try { await InvokeNotification(handler, name, parameters).ConfigureAwait(false); }
                    finally { lock (gate) frame.Active = false; notificationFrame.Value = null; }
                }, frame: frame);
            }
        }
        return ValueTask.CompletedTask;
    }
    private void Progress(JsonData parameters)
    {
        var value = parameters.Value;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("progressToken", out var progressId) || !McpWireJson.Id(progressId) || !value.TryGetProperty("progress", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var amount)) return;
        lock (gate)
        {
            if (closed || !pending.TryGetValue(McpWireJson.Key(progressId), out var entry) || !entry.Accepting || entry.Options.OnProgress is not { } callback) return;
            if (entry.Timer is not null) entry.Timer.Change(TimeSpan.FromMilliseconds(Math.Min(entry.Options.TimeoutMilliseconds, uint.MaxValue - 1d)), Timeout.InfiniteTimeSpan);
            var total = value.TryGetProperty("total", out var supplied) && supplied.ValueKind == JsonValueKind.Number && supplied.TryGetDouble(out var t) ? (double?)t : null;
            var text = value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;
            var previous = entry.Progress;
            entry.Progress = Dispatch(async () => { await previous.ConfigureAwait(false); await callback(new(amount, total, text), entry.Token).ConfigureAwait(false); }, preserveFault: true);
        }
    }
    private Task Dispatch(Func<Task> run, bool preserveFault = false, NotificationFrame? frame = null)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (closed) return Task.CompletedTask;
            if (callbackSlots.Count + failedNotificationCount >= limits.MaximumCallbacks) throw new McpRuntimeProtocolException("MCP callback admission limit exceeded");
            callbackOriginals.RemoveWhere(task => task.IsCompleted); callbackSlots.Add(settled.Task);
            // Original task is retained before close can snapshot it; no detached callback work.
            var original = Task.Run(async () =>
            {
                Task? invoked = null;
                try
                {
                    // Dispatch admits the exact original under the gate before the callback can queue retirement.
                    lock (gate) { }
                    inCallback.Value = true; operations.Lifetime.Token.ThrowIfCancellationRequested(); invoked = run(); await invoked.ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (!preserveFault && operations.Lifetime.IsCancellationRequested &&
                    error.CancellationToken == operations.Lifetime.Token && (invoked is null || invoked.IsCanceled)) { }
                catch (Exception error)
                {
                    if (preserveFault) throw;
                    // InvokeNotification already captured the user original; other dispatches own invoked.
                    var evidence = error as McpChannelCallbackException ?? new McpChannelCallbackException(invoked, invoked?.Exception ?? error);
                    lock (gate) { failedNotificationCount++; notificationFailures.Add(evidence); }
                    Fail(new McpRuntimeDisconnectedException("MCP callback failed"));
                }
                finally { inCallback.Value = false; lock (gate) callbackSlots.Remove(settled.Task); settled.TrySetResult(); }
            });
            if (frame is not null) frame.Original = original;
            callbackOriginals.Add(original); return original;
        }
    }
    private async Task InvokeNotification(McpNotificationHandler handler, string name, JsonData? parameters)
    {
        Task? original = null;
        try
        {
            original = handler(name, parameters, operations.Lifetime.Token).AsTask();
            await original.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (original is { IsCanceled: true } && operations.Lifetime.IsCancellationRequested &&
                error is OperationCanceledException cancellation && cancellation.CancellationToken == operations.Lifetime.Token) throw;
            // An ordinary wrapper preserves empty/nested aggregates and duplicate entries, and
            // prevents synchronous or faulted-task OCE from becoming async cancellation.
            throw new McpChannelCallbackException(original, (Exception?)original?.Exception ?? error);
        }
    }
    private void Fail(Exception error)
    {
        lock (gate)
        {
            failure ??= error;
            foreach (var entry in pending.Values)
            {
                entry.Accepting = false; entry.Reply.TrySetException(error is McpRuntimeDisconnectedException ? error : new McpRuntimeDisconnectedException("MCP wire failed: " + error.GetType().Name));
                if (!entry.SendStop.IsCancellationRequested) entry.CancelWork = entry.SendStop.CancelAsync();
            }
        }
    }
    private void Check()
    { if (closed) throw new ObjectDisposedException(nameof(McpJsonRpcRequestChannel)); if (failure is not null) throw new McpRuntimeDisconnectedException("MCP channel disconnected"); }
    private static JsonData Message(string method, JsonData? parameters, long? id = null)
    {
        var fields = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is { } supplied) fields["id"] = supplied;
        if (parameters is not null) fields["params"] = parameters.Value;
        return McpWireJson.Json(fields);
    }
    private async Task JoinCallbacks()
    {
        Task[] originals; lock (gate) originals = callbackOriginals.ToArray();
        try { await Task.WhenAll(originals).ConfigureAwait(false); }
        catch { /* Progress faults remain on the directly joined request original; notification faults are latched below. */ }
        // Callback slots settle successfully, but a failed host notification remains an explicit close fault.
        List<Exception> errors;
        lock (gate)
        {
            errors = notificationFailures.ToList();
            if (failure is not null and not McpRuntimeDisconnectedException) errors.Add(failure);
        }
        if (errors.Count == 1 && errors[0] is not OperationCanceledException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private async Task CloseWireAndIncomingAsync()
    {
        McpIncomingRequestRegistry? registry; McpDynamicRootsHandler? rootsOwner;
        lock (gate) { registry = incomingRequests; rootsOwner = dynamicRoots; }
        if (registry is null && rootsOwner is null)
        {
            // Keep the established default/static-roots close exception identity and shape.
            await wire.CloseAsync().ConfigureAwait(false); return;
        }
        var originals = new List<Task>(); var errors = new List<Exception>();
        void Start(Func<Task> operation)
        { try { originals.Add(operation()); } catch (Exception error) { errors.Add(error); } }
        // Stop callback owners before physical wire retirement; join every initiated original even after a failure.
        if (registry is not null) Start(registry.CloseAsync);
        if (rootsOwner is not null) Start(rootsOwner.CloseAsync);
        Start(wire.CloseAsync);
        foreach (var original in originals)
            try { await original.ConfigureAwait(false); } catch (Exception error) { errors.Add(original.IsFaulted ? original.Exception! : error); }
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
    }
    public Task CloseAsync()
    {
        if (inCallback.Value) throw new InvalidOperationException("MCP borrowed callback cannot await its own close");
        lock (gate) return close.Value;
    }
    private void FenceClose()
    {
        lock (gate)
        {
            closed = true;
            foreach (var entry in pending.Values)
            {
                entry.Accepting = false; entry.Reply.TrySetException(new McpRuntimeDisconnectedException("MCP channel closed"));
                if (!entry.SendStop.IsCancellationRequested) entry.CancelWork = entry.SendStop.CancelAsync();
            }
        }
    }
    public bool IsInNotificationCallback => notificationFrame.Value is not null;
    private sealed class RetirementReceipt(McpJsonRpcRequestChannel owner, Task original) : IMcpAdmittedChannelRetirement
    {
        public Task JoinAsync()
        {
            if (owner.inCallback.Value) throw new InvalidOperationException("MCP borrowed callback cannot join its retirement original");
            return original;
        }
    }
    public IMcpAdmittedChannelRetirement RetireAfterNotification()
    {
        lock (gate)
        {
            var frame = notificationFrame.Value;
            if (frame is not { Active: true }) throw new InvalidOperationException("Retirement requires the actual live notification callback");
            if (retirementReceipt is not null) return retirementReceipt;
            if (close.IsValueCreated) return retirementReceipt = new RetirementReceipt(this, close.Value);
            closed = true; // Fence admission; physical close remains owned by the callback-return loop.
            retirementRequest.TrySetResult(frame.Original);
            return retirementReceipt = new RetirementReceipt(this, retirementLoop);
        }
    }
    private async Task RetirementLoopAsync()
    {
        var callback = await retirementRequest.Task.ConfigureAwait(false);
        if (callback is null) return;
        try { await callback.ConfigureAwait(false); } catch { /* Notification failure is owned by JoinCallbacks. */ }
        FenceClose(); await closeCore.Value.ConfigureAwait(false);
    }
    private async Task CloseJoinedAsync()
    {
        FenceClose(); retirementRequest.TrySetResult(null);
        Task? physical = null; var errors = new List<Exception>();
        try { physical = closeCore.Value; } catch (Exception error) { errors.Add(error); }
        if (physical is not null) try { await physical.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { await retirementLoop.ConfigureAwait(false); }
        catch (Exception error) { if (!errors.Any(item => ReferenceEquals(item, error))) errors.Add(error); }
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
    }
}
