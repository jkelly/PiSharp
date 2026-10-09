using System.Threading.Tasks.Sources;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Roots;
using PiSharp.Extensions.Runtime.Mcp.Roots;

// Source-only authored controls; root owns Program registration and every actual execution.
internal static class McpDynamicRootsTests
{
    internal const string Prefix = "mcp-dynamic-roots.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "each-request-evaluates-current-roots-and-copies-result", CurrentRoots),
        (Prefix + "close-joins-held-callback-and-fences-fresh-admission", HeldClose),
        (Prefix + "single-use-valuetask-is-captured-and-consumed-once", SingleConsume),
        (Prefix + "multi-fault-original-preserves-nested-and-duplicate-identities", MultiFault),
        (Prefix + "faulted-oce-original-remains-faulted-with-complete-evidence", FaultedCancellation),
        (Prefix + "actual-owned-cancellation-is-canceled-and-close-joins", OwnedCancellation),
        (Prefix + "synchronous-callback-error-retains-null-original-and-same-evidence", SynchronousFailure),
        (Prefix + "borrowed-callback-close-reentry-refuses-before-stop", CloseReentry),
        (Prefix + "array-response-utf8-and-request-bounds-have-no-new-authority", Bounds)
    ];

    private static TaskCompletionSource<JsonData> Original() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool ok) { if (!ok) throw new InvalidOperationException("Dynamic roots control failed."); }
    private static async Task<T> Failure<T>(Task task) where T : Exception
    { try { await task; } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task SignalOrOriginal(Task signal, Task original)
    {
        var winner = await Task.WhenAny(signal, original).WaitAsync(TimeSpan.FromSeconds(5));
        await winner;
        if (!signal.IsCompletedSuccessfully) throw new InvalidOperationException("Original settled before its required signal.");
    }
    private static async Task CleanupHeld(Exception? primary, Action release, Task? request,
        Func<Task> close, Func<Exception, bool>? expectedRequest = null, Task? unexpected = null)
    {
        var errors = new List<Exception>(); if (primary is not null) errors.Add(primary);
        try { release(); } catch (Exception error) { errors.Add(error); }
        if (request is not null)
            try { await request; } catch (Exception error) { if (expectedRequest?.Invoke(error) != true) errors.Add(request.IsFaulted ? request.Exception! : error); }
        if (unexpected is not null && !ReferenceEquals(unexpected, request))
            try { await unexpected; } catch (Exception error) { errors.Add(unexpected.IsFaulted ? unexpected.Exception! : error); }
        Task? closing = null;
        try { closing = close(); await closing; } catch (Exception error) { errors.Add(closing is { IsFaulted: true } ? closing.Exception! : error); }
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
    }

    private static async Task CurrentRoots()
    {
        var count = 0;
        var handler = new McpDynamicRootsHandler(_ => ValueTask.FromResult(JsonData.Parse(
            "[{\"uri\":\"file:///inert-" + ++count + "\",\"name\":\"root\",\"unknown\":9007199254740993}]")));
        var first = await handler.HandleRootsListAsync(); var second = await handler.HandleRootsListAsync();
        Require(count == 2 && first.Value.GetProperty("roots")[0].GetProperty("uri").GetString() == "file:///inert-1" &&
            second.Value.GetProperty("roots")[0].GetProperty("uri").GetString() == "file:///inert-2");
        Require(first.Value.GetProperty("roots")[0].GetProperty("unknown").GetRawText() == "9007199254740993");
        await handler.CloseAsync();
    }

    private static async Task HeldClose()
    {
        var original = Original(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new McpDynamicRootsHandler(_ => { entered.SetResult(); return new(original.Task); });
        Task<JsonData>? request = null; Task? close = null; Task<JsonData>? unexpected = null; Exception? primary = null;
        try
        {
            request = handler.HandleRootsListAsync(); await SignalOrOriginal(entered.Task, request);
            close = handler.CloseAsync(); Require(!close.IsCompleted && !original.Task.IsCompleted);
            Require(ReferenceEquals(close, handler.CloseAsync()));
            try { unexpected = handler.HandleRootsListAsync(); throw new InvalidOperationException("Fresh admission succeeded."); }
            catch (ObjectDisposedException) when (unexpected is null) { }
        }
        catch (Exception error) { primary = error; }
        finally { await CleanupHeld(primary, () => original.TrySetResult(JsonData.Parse("[]")), request, () => close ?? handler.CloseAsync(), unexpected: unexpected); }
    }

    private sealed class SingleRootsSource : IValueTaskSource<JsonData>
    {
        private ManualResetValueTaskSourceCore<JsonData> core = new() { RunContinuationsAsynchronously = true };
        internal int Consumed;
        internal ValueTask<JsonData> Value => new(this, core.Version);
        internal void Complete() => core.SetResult(JsonData.Parse("[]"));
        public JsonData GetResult(short token)
        { if (++Consumed != 1) throw new InvalidOperationException("Roots consumed twice."); return core.GetResult(token); }
        public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            core.OnCompleted(continuation, state, token, flags);
    }

    private static async Task SingleConsume()
    {
        var source = new SingleRootsSource(); var handler = new McpDynamicRootsHandler(_ => source.Value);
        Task<JsonData>? request = null; Exception? primary = null;
        try { request = handler.HandleRootsListAsync(); Require(!request.IsCompleted); }
        catch (Exception error) { primary = error; }
        finally { await CleanupHeld(primary, source.Complete, request, handler.CloseAsync); }
        Require(source.Consumed == 1);
    }

    private static async Task MultiFault()
    {
        var leaf = new IOException("shared leaf"); var nested = new AggregateException(leaf, leaf);
        var original = Original(); var handler = new McpDynamicRootsHandler(_ => new(original.Task));
        var request = handler.HandleRootsListAsync(); original.SetException(new Exception[] { leaf, nested });
        var failure = await Failure<McpRootsCallbackException>(request);
        Require(ReferenceEquals(failure.Original, original.Task) && original.Task.IsFaulted);
        var all = (AggregateException)failure.Evidence;
        Require(all.InnerExceptions.Count == 2 && ReferenceEquals(all.InnerExceptions[0], leaf) &&
            ReferenceEquals(all.InnerExceptions[1], nested) && ReferenceEquals(nested.InnerExceptions[0], nested.InnerExceptions[1]));
        var close = handler.CloseAsync(); Require(ReferenceEquals(await Failure<McpRootsCallbackException>(close), failure));
        Require(ReferenceEquals(close, handler.CloseAsync()) && close.IsFaulted && original.Task.IsFaulted);
    }

    private static async Task FaultedCancellation()
    {
        var error = new OperationCanceledException("Faulted callback is not canceled.");
        var original = Task.FromException<JsonData>(error); var handler = new McpDynamicRootsHandler(_ => new(original));
        var request = handler.HandleRootsListAsync(); var failure = await Failure<McpRootsCallbackException>(request);
        Require(original.IsFaulted && request.IsFaulted && !request.IsCanceled && ReferenceEquals(failure.Original, original) &&
            ReferenceEquals(((AggregateException)failure.Evidence).InnerExceptions[0], error));
        await Failure<McpRootsCallbackException>(handler.CloseAsync());
    }

    private static async Task OwnedCancellation()
    {
        var original = Original(); CancellationToken captured = default;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var handler = new McpDynamicRootsHandler(token => { captured = token; registration = token.Register(() => stopped.TrySetResult()); return new(original.Task); });
        Task<JsonData>? request = null; Task? close = null; Exception? primary = null;
        McpRootsCallbackCancellation? canceled = null;
        try
        {
            request = handler.HandleRootsListAsync(); close = handler.CloseAsync();
            await SignalOrOriginal(stopped.Task, request); Require(captured.IsCancellationRequested && !close.IsCompleted);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            try { await CleanupHeld(primary, () => original.TrySetCanceled(captured), request, () => close ?? handler.CloseAsync(), error => { canceled = error as McpRootsCallbackCancellation; return canceled is not null; }); }
            finally { registration.Dispose(); }
        }
        Require(request!.IsCanceled && original.Task.IsCanceled && canceled is not null && ReferenceEquals(canceled.Original, original.Task));
    }

    private static async Task SynchronousFailure()
    {
        var original = new AggregateException(); var handler = new McpDynamicRootsHandler(_ => throw original);
        var request = handler.HandleRootsListAsync(); var failure = await Failure<McpRootsCallbackException>(request);
        Require(failure.Original is null && ReferenceEquals(failure.Evidence, original));
        Require(ReferenceEquals(await Failure<McpRootsCallbackException>(handler.CloseAsync()), failure));
    }

    private static async Task CloseReentry()
    {
        McpDynamicRootsHandler? handler = null; var refused = 0;
        handler = new McpDynamicRootsHandler(token =>
        {
            try { _ = handler!.CloseAsync(); } catch (InvalidOperationException) { refused++; }
            return ValueTask.FromResult(JsonData.Parse("[]"));
        });
        await handler.HandleRootsListAsync(); Require(refused == 1); await handler.CloseAsync();
    }

    private static async Task Bounds()
    {
        var calls = 0; var handler = new McpDynamicRootsHandler(_ => { calls++; return ValueTask.FromResult(JsonData.Parse("[\"éé\"]")); }, 15, 1);
        await Failure<InvalidOperationException>(handler.HandleRootsListAsync());
        try { _ = handler.HandleRootsListAsync(); throw new IOException("Request bound failed."); } catch (InvalidOperationException) { }
        Require(calls == 1); await handler.CloseAsync();
        var invalid = new McpDynamicRootsHandler(_ => ValueTask.FromResult(JsonData.EmptyObject));
        await Failure<ArgumentException>(invalid.HandleRootsListAsync()); await invalid.CloseAsync();
    }
}
