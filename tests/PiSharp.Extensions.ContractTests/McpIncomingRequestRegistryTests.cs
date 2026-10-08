using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.IncomingRequests;
using PiSharp.Extensions.Runtime.Mcp.IncomingRequests;

internal static class McpIncomingRequestRegistryTests
{
    internal const string Prefix = "mcp-incoming-registry.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "replacement-removal-and-opaque-reply", Replacement),
        (Prefix + "unknown-method-awaits-physical-response", UnknownMethod),
        (Prefix + "close-joins-held-handler-and-response", HeldClose),
        (Prefix + "cancellation-id-refuses-duplicate-and-joins-original", Cancellation),
        (Prefix + "handler-and-send-full-fault-dags-retained", DualFault),
        (Prefix + "synchronous-empty-failure-and-null-result", Synchronous),
        (Prefix + "borrowed-handler-and-sender-close-reentry", Reentry),
        (Prefix + "finite-method-inflight-request-and-byte-bounds", Bounds)
    ];
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Incoming registry control failed."); }
    private static JsonData Request(string id = "1", string method = "custom") => JsonData.Parse(
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":{\"opaque\":9007199254740993}}");
    private static TaskCompletionSource<T> Held<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Failure<T>(Task task) where T : Exception
    { try { await task; } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task SignalOrOriginal(Task signal, Task original)
    {
        var winner = await Task.WhenAny(signal, original).WaitAsync(TimeSpan.FromSeconds(5));
        await winner;
        if (!signal.IsCompletedSuccessfully) throw new InvalidOperationException("Original settled before its required signal.");
    }
    private static async Task CleanupHeld(Exception? primary, Action release, Task? request,
        Func<Task> close, Func<Exception, bool>? expectedRequest = null, Func<Exception, bool>? expectedClose = null, Task? unexpected = null)
    {
        var errors = new List<Exception>(); if (primary is not null) errors.Add(primary);
        try { release(); } catch (Exception error) { errors.Add(error); }
        if (request is not null)
            try { await request; } catch (Exception error) { if (expectedRequest?.Invoke(error) != true) errors.Add(request.IsFaulted ? request.Exception! : error); }
        if (unexpected is not null && !ReferenceEquals(unexpected, request))
            try { await unexpected; } catch (Exception error) { errors.Add(unexpected.IsFaulted ? unexpected.Exception! : error); }
        Task? closing = null;
        try { closing = close(); await closing; }
        catch (Exception error) { if (expectedClose?.Invoke(error) != true) errors.Add(closing is { IsFaulted: true } ? closing.Exception! : error); }
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
    }

    private static async Task Replacement()
    {
        JsonData? reply = null; var calls = 0;
        var registry = new McpIncomingRequestRegistry((value, _) => { reply = value; return Task.CompletedTask; });
        var old = registry.SetRequestHandler("custom", (_, _) => ValueTask.FromResult<JsonData?>(JsonData.Parse("1")));
        var remove = registry.SetRequestHandler("custom", (parameters, _) => { calls++; return ValueTask.FromResult(parameters); });
        old(); await registry.DispatchAsync(Request("\"request-string\""));
        Require(calls == 1 && reply!.Value.GetProperty("id").GetString() == "request-string" &&
            reply.Value.GetProperty("result").GetProperty("opaque").GetRawText() == "9007199254740993");
        remove(); remove(); await registry.DispatchAsync(Request());
        Require(calls == 1 && reply!.Value.GetProperty("error").GetProperty("code").GetInt32() == -32601);
        await registry.CloseAsync();
    }
    private static async Task UnknownMethod()
    {
        var sent = Held<bool>(); JsonData? reply = null;
        var registry = new McpIncomingRequestRegistry((value, _) => { reply = value; return sent.Task; });
        Task? request = null; Task? close = null; Exception? primary = null;
        try
        {
            request = registry.DispatchAsync(Request()); close = registry.CloseAsync();
            Require(!request.IsCompleted && !close.IsCompleted && reply!.Value.GetProperty("error").GetProperty("code").GetInt32() == -32601);
        }
        catch (Exception error) { primary = error; }
        finally { await CleanupHeld(primary, () => sent.TrySetResult(true), request, () => close ?? registry.CloseAsync()); }
    }
    private static async Task HeldClose()
    {
        var original = Held<JsonData?>(); var send = Held<bool>(); var sent = Held<bool>();
        var registry = new McpIncomingRequestRegistry((_, _) => { sent.SetResult(true); return send.Task; });
        registry.SetRequestHandler("custom", (_, _) => new(original.Task));
        Task? request = null; Task? close = null; Task? unexpected = null; Exception? primary = null;
        try
        {
            request = registry.DispatchAsync(Request()); close = registry.CloseAsync();
            Require(ReferenceEquals(close, registry.CloseAsync()) && !request.IsCompleted && !close.IsCompleted);
            try { unexpected = registry.DispatchAsync(Request("2")); throw new InvalidOperationException("Closed owner admitted a second original."); }
            catch (ObjectDisposedException) when (unexpected is null) { }
            original.TrySetResult(JsonData.Parse("{}")); await SignalOrOriginal(sent.Task, request);
            Require(!request.IsCompleted && !close.IsCompleted);
        }
        catch (Exception error) { primary = error; }
        finally { await CleanupHeld(primary, () => { original.TrySetResult(JsonData.Parse("{}")); send.TrySetResult(true); }, request, () => close ?? registry.CloseAsync(), unexpected: unexpected); }
    }
    private static async Task Cancellation()
    {
        var original = Held<JsonData?>(); CancellationToken owned = default; JsonData? response = null;
        var stopped = Held<bool>(); CancellationTokenRegistration registration = default;
        var registry = new McpIncomingRequestRegistry((value, _) => { response = value; return Task.CompletedTask; });
        registry.SetRequestHandler("custom", (_, token) => { owned = token; registration = token.Register(() => stopped.TrySetResult(true)); return new(original.Task); });
        Task? request = null; Task? unexpected = null; Exception? primary = null; McpIncomingOriginalException? failure = null; Exception? closeFailure = null;
        try
        {
            request = registry.DispatchAsync(Request());
            try { unexpected = registry.DispatchAsync(Request()); throw new IOException("Duplicate ID admitted a second original."); }
            catch (InvalidOperationException) when (unexpected is null) { }
            Require(!registry.CancelIncoming(JsonData.Parse("2")) && registry.CancelIncoming(JsonData.Parse("1")));
            await SignalOrOriginal(stopped.Task, request); Require(!request.IsCompleted);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            try { await CleanupHeld(primary, () => original.TrySetCanceled(owned), request, registry.CloseAsync,
                error => { failure = error as McpIncomingOriginalException; return failure is not null; },
                error => { closeFailure = error; return error is McpIncomingOriginalException && ReferenceEquals(error, failure); }, unexpected); }
            finally { registration.Dispose(); }
        }
        Require(failure is not null && ReferenceEquals(failure.Original, original.Task) && original.Task.IsCanceled && request!.IsFaulted &&
            response!.Value.GetProperty("error").GetProperty("code").GetInt32() == -32603 && ReferenceEquals(closeFailure, failure));
    }
    private static async Task DualFault()
    {
        var leaf = new IOException("shared"); var nested = new AggregateException(leaf, leaf);
        var original = Held<JsonData?>(); original.SetException([leaf, nested]);
        var send = Held<bool>(); send.SetException(new AggregateException());
        var registry = new McpIncomingRequestRegistry((_, _) => send.Task);
        registry.SetRequestHandler("custom", (_, _) => new(original.Task));
        var failure = await Failure<AggregateException>(registry.DispatchAsync(Request()));
        Require(failure.InnerExceptions.Count == 2);
        var callback = (McpIncomingOriginalException)failure.InnerExceptions[0];
        var physical = (McpIncomingOriginalException)failure.InnerExceptions[1];
        var graph = (AggregateException)callback.Evidence;
        Require(ReferenceEquals(callback.Original, original.Task) && ReferenceEquals(physical.Original, send.Task) &&
            graph.InnerExceptions.Count == 2 && ReferenceEquals(graph.InnerExceptions[0], leaf) && ReferenceEquals(graph.InnerExceptions[1], nested) &&
            ReferenceEquals(nested.InnerExceptions[0], nested.InnerExceptions[1]));
        var close = await Failure<AggregateException>(registry.CloseAsync());
        Require(ReferenceEquals(close.InnerExceptions[0], callback) && ReferenceEquals(close.InnerExceptions[1], physical));
    }
    private static async Task Synchronous()
    {
        var empty = new AggregateException(); JsonData? response = null;
        var registry = new McpIncomingRequestRegistry((value, _) => { response = value; return Task.CompletedTask; });
        registry.SetRequestHandler("custom", (_, _) => throw empty);
        var failure = await Failure<McpIncomingOriginalException>(registry.DispatchAsync(Request()));
        Require(failure.Original is null && ReferenceEquals(failure.Evidence, empty));
        registry.SetRequestHandler("custom", (_, _) => ValueTask.FromResult<JsonData?>(null));
        await registry.DispatchAsync(Request("2")); Require(response!.Value.GetProperty("result").GetRawText() == "{}");
        _ = await Failure<McpIncomingOriginalException>(registry.CloseAsync());
    }
    private static async Task Reentry()
    {
        McpIncomingRequestRegistry registry = null!; var refused = 0;
        void BorrowedClose() { try { _ = registry.CloseAsync(); } catch (InvalidOperationException) { refused++; } }
        registry = new((_, _) => { BorrowedClose(); return Task.CompletedTask; });
        registry.SetRequestHandler("custom", (_, _) => { BorrowedClose(); return ValueTask.FromResult<JsonData?>(null); });
        await registry.DispatchAsync(Request()); Require(refused == 2); await registry.CloseAsync();
    }
    private static async Task Bounds()
    {
        var registry = new McpIncomingRequestRegistry((_, _) => Task.CompletedTask, maximumMethods: 1, maximumRequests: 1);
        registry.SetRequestHandler("custom", (_, _) => ValueTask.FromResult<JsonData?>(null));
        await Failure<InvalidOperationException>(Task.Run(() => { registry.SetRequestHandler("other", (_, _) => ValueTask.FromResult<JsonData?>(null)); }));
        await registry.DispatchAsync(Request());
        await Failure<InvalidOperationException>(Task.Run(() => registry.DispatchAsync(Request("2")))); await registry.CloseAsync();
        var held = Held<JsonData?>();
        var inflight = new McpIncomingRequestRegistry((_, _) => Task.CompletedTask, maximumInflight: 1);
        inflight.SetRequestHandler("custom", (_, _) => new(held.Task));
        Task? first = null; Task? unexpected = null; Exception? primary = null;
        try
        {
            first = inflight.DispatchAsync(Request());
            try { unexpected = inflight.DispatchAsync(Request("2")); throw new IOException("Inflight bound admitted a second original."); }
            catch (InvalidOperationException) when (unexpected is null) { }
        }
        catch (Exception error) { primary = error; }
        finally { await CleanupHeld(primary, () => held.TrySetResult(null), first, inflight.CloseAsync, unexpected: unexpected); }
        var sends = 0; var bounded = new McpIncomingRequestRegistry((_, _) => { sends++; return Task.CompletedTask; }, maximumResponseBytes: 128);
        bounded.SetRequestHandler("custom", (_, _) => ValueTask.FromResult<JsonData?>(JsonData.Parse("\"" + new string('é', 128) + "\"")));
        var failure = await Failure<McpIncomingOriginalException>(bounded.DispatchAsync(Request()));
        Require(sends == 0 && failure.Phase == "response" && failure.Original is null);
        _ = await Failure<McpIncomingOriginalException>(bounded.CloseAsync());
    }
}
