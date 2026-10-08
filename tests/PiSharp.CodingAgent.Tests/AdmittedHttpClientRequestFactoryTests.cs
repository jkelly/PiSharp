using System.Net;
using PiSharp.Cli.Mcp;
using PiSharp.Extensions.Mcp.Transport;

internal static class AdmittedHttpClientRequestFactoryTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("mcp-http-admission.effect-free-stop-before-send-and-borrowed-client", EffectFree),
        ("mcp-http-admission.physical-request-abort-joins-held-header-original", HeldHeaders),
        ("mcp-http-admission.cancellation-callback-fault-and-selfwait-original", CallbackFault),
        ("mcp-http-admission.caller-cancel-starts-same-stop-original", CallerCancellation),
        ("mcp-http-admission.late-headers-transfer-remains-transport-owned", LateHeaders),
        ("mcp-http-admission.header-failure-is-send-owned-and-not-replayed", HeaderFailure),
        ("mcp-http-admission.handler-cancel-during-send-initiation-is-joined", InlineCancellation),
        ("mcp-http-admission.stop-cancels-only-one-concurrent-request", IndependentRequests),
        ("mcp-http-admission.borrowed-client-sync-initiation-fault-is-joined", DisposedClient),
        ("mcp-http-admission.async-handler-lifecycle-reentry-and-affine-send", Reentry)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls, Disposals;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); return send(request, token); }
        protected override void Dispose(bool disposing) { Interlocked.Increment(ref Disposals); base.Dispose(disposing); }
    }
    private static HttpRequestMessage Request() => new(HttpMethod.Post, "https://never-contacted.invalid/mcp");
    private static HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content = new StringContent("{}") };
    private static async Task EffectFree()
    {
        using var handler = new Handler((request, token) => Task.FromResult(Response()));
        using var client = new HttpClient(handler, disposeHandler: false); var factory = AdmittedHttpClientRequestFactory.Create(client);
        using var request = Request(); var lease = factory(request); Equal(0, handler.Calls);
        var stop = lease.StopAsync(); await stop; Check(ReferenceEquals(stop, lease.StopAsync()));
        await Throws<InvalidOperationException>(() => lease.SendAsync(default).AsTask()); Equal(0, handler.Calls); Equal(0, handler.Disposals);
        using var nextRequest = Request(); var next = factory(nextRequest); using var response = await next.SendAsync(default);
        await next.StopAsync(); Equal(1, handler.Calls); Equal(0, handler.Disposals);
    }
    private static async Task HeldHeaders()
    {
        var entered = Gate(); var aborted = Gate(); var release = Gate();
        using var handler = new Handler(async (request, token) =>
        {
            using var registration = token.Register(() => aborted.TrySetResult()); entered.TrySetResult();
            await release.Task; token.ThrowIfCancellationRequested(); return Response();
        });
        using var client = new HttpClient(handler, false); using var request = Request(); var lease = AdmittedHttpClientRequestFactory.Create(client)(request);
        var send = lease.SendAsync(default).AsTask(); Task? stop = null;
        try
        {
            await entered.Task; stop = lease.StopAsync(); await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!stop.IsCompleted && !send.IsCompleted); Check(ReferenceEquals(stop, lease.StopAsync()));
            release.TrySetResult(); await Throws<OperationCanceledException>(() => send); await stop;
            Equal(1, handler.Calls); Equal(0, handler.Disposals);
        }
        finally { release.TrySetResult(); await Observe(send); await Observe(stop ?? lease.StopAsync()); }
    }
    private static async Task CallbackFault()
    {
        var entered = Gate(); var callback = Gate(); var callbackRelease = Gate(); var headersRelease = Gate();
        var failure = new IOException("actual per-request cancellation callback fault"); IMcpAdmittedHttpRequestOperation? lease = null;
        using var handler = new Handler(async (request, token) =>
        {
            using var registration = token.Register(() =>
            {
                ThrowsAction<InvalidOperationException>(() => lease!.StopAsync()); callback.TrySetResult();
                callbackRelease.Task.GetAwaiter().GetResult(); throw failure;
            });
            entered.TrySetResult(); await headersRelease.Task; token.ThrowIfCancellationRequested(); return Response();
        });
        using var client = new HttpClient(handler, false); using var request = Request(); lease = AdmittedHttpClientRequestFactory.Create(client)(request);
        var send = lease.SendAsync(default).AsTask(); Task? stop = null;
        try
        {
            await entered.Task; stop = lease.StopAsync(); await callback.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!stop.IsCompleted); headersRelease.TrySetResult();
            // Header disposal joins the callback registration too; release each independent original
            // explicitly rather than treating cancellation of an await as settlement.
            Check(!stop.IsCompleted); callbackRelease.TrySetResult(); await Throws<OperationCanceledException>(() => send);
            var error = await Throws<Exception>(() => stop!); Check(Flatten(error).Any(item => ReferenceEquals(item, failure)));
            Check(ReferenceEquals(stop, lease.StopAsync())); Check(ReferenceEquals(error, await Throws<Exception>(() => lease.StopAsync())));
        }
        finally { headersRelease.TrySetResult(); callbackRelease.TrySetResult(); await Observe(send); await Observe(stop ?? lease.StopAsync()); }
    }
    private static async Task CallerCancellation()
    {
        var entered = Gate(); var aborted = Gate(); var release = Gate(); using var caller = new CancellationTokenSource();
        using var handler = new Handler(async (request, token) =>
        { using var registration = token.Register(() => aborted.TrySetResult()); entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return Response(); });
        using var client = new HttpClient(handler, false); using var request = Request(); var lease = AdmittedHttpClientRequestFactory.Create(client)(request);
        var send = lease.SendAsync(caller.Token).AsTask(); Task? stop = null;
        try
        {
            await entered.Task; await caller.CancelAsync(); await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop = lease.StopAsync(); Check(!stop.IsCompleted); Check(ReferenceEquals(stop, lease.StopAsync()));
            release.TrySetResult(); await Throws<OperationCanceledException>(() => send); await stop;
        }
        finally { release.TrySetResult(); await Observe(send); await Observe(stop ?? lease.StopAsync()); }
    }
    private sealed class Content(Action disposed) : StringContent("{}")
    { protected override void Dispose(bool disposing) { disposed(); base.Dispose(disposing); } }
    private static async Task LateHeaders()
    {
        var entered = Gate(); var release = Gate(); var disposed = 0;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(() => Interlocked.Increment(ref disposed)) };
        using var handler = new Handler(async (request, token) => { entered.TrySetResult(); await release.Task; return response; });
        using var client = new HttpClient(handler, false); using var request = Request(); var lease = AdmittedHttpClientRequestFactory.Create(client)(request);
        var send = lease.SendAsync(default).AsTask(); Task? stop = null;
        try
        {
            await entered.Task; stop = lease.StopAsync(); Check(!stop.IsCompleted); release.TrySetResult();
            Check(ReferenceEquals(response, await send)); await stop; Equal(0, disposed); Equal(1, handler.Calls);
            response.Dispose(); Equal(1, disposed);
        }
        finally { release.TrySetResult(); await Observe(send); await Observe(stop ?? lease.StopAsync()); if (disposed == 0) response.Dispose(); }
    }
    private static async Task HeaderFailure()
    {
        var failure = new IOException("original header initiation failed");
        using var handler = new Handler((request, token) => throw failure); using var client = new HttpClient(handler, false);
        using var request = Request(); var lease = AdmittedHttpClientRequestFactory.Create(client)(request);
        Check(ReferenceEquals(failure, await Throws<IOException>(() => lease.SendAsync(default).AsTask())));
        var stop = lease.StopAsync(); await stop; Check(ReferenceEquals(stop, lease.StopAsync())); Equal(1, handler.Calls);
    }
    private static async Task InlineCancellation()
    {
        using var caller = new CancellationTokenSource(); var entered = Gate(); var release = Gate();
        using var handler = new Handler((request, token) =>
        { caller.Cancel(); entered.TrySetResult(); return Finish(token); });
        async Task<HttpResponseMessage> Finish(CancellationToken token) { await release.Task; token.ThrowIfCancellationRequested(); return Response(); }
        using var client = new HttpClient(handler, false); using var request = Request(); var lease = AdmittedHttpClientRequestFactory.Create(client)(request);
        var send = lease.SendAsync(caller.Token).AsTask(); Task? stop = null;
        try
        {
            await entered.Task; stop = lease.StopAsync(); Check(!stop.IsCompleted && !send.IsCompleted);
            release.TrySetResult(); await Throws<OperationCanceledException>(() => send); await stop; Equal(1, handler.Calls);
        }
        finally { release.TrySetResult(); await Observe(send); await Observe(stop ?? lease.StopAsync()); }
    }
    private static async Task Reentry()
    {
        IMcpAdmittedHttpRequestOperation? lease = null;
        using var handler = new Handler(async (request, token) =>
        { await Task.Yield(); ThrowsAction<InvalidOperationException>(() => lease!.StopAsync()); ThrowsAction<InvalidOperationException>(() => lease!.SendAsync(default)); return Response(); });
        using var client = new HttpClient(handler, false); using var request = Request(); lease = AdmittedHttpClientRequestFactory.Create(client)(request);
        using var response = await lease.SendAsync(default); await Throws<InvalidOperationException>(() => lease.SendAsync(default).AsTask());
        await lease.StopAsync(); Equal(1, handler.Calls); Equal(0, handler.Disposals);
    }
    private static async Task IndependentRequests()
    {
        var firstEntered = Gate(); var secondEntered = Gate(); var firstAborted = Gate();
        var firstRelease = Gate(); var secondRelease = Gate(); CancellationToken secondToken = default;
        using var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/first")
            { using var registration = token.Register(() => firstAborted.TrySetResult()); firstEntered.TrySetResult(); await firstRelease.Task; token.ThrowIfCancellationRequested(); }
            else { secondToken = token; secondEntered.TrySetResult(); await secondRelease.Task; token.ThrowIfCancellationRequested(); }
            return Response();
        });
        using var client = new HttpClient(handler, false); var factory = AdmittedHttpClientRequestFactory.Create(client);
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "https://never-contacted.invalid/first");
        using var secondRequest = Request(); var first = factory(firstRequest); var second = factory(secondRequest);
        var one = first.SendAsync(default).AsTask(); var two = second.SendAsync(default).AsTask(); Task? stop = null; HttpResponseMessage? received = null;
        try
        {
            await firstEntered.Task; await secondEntered.Task; stop = first.StopAsync(); await firstAborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!secondToken.IsCancellationRequested && !two.IsCompleted); firstRelease.TrySetResult();
            await Throws<OperationCanceledException>(() => one); await stop; Check(!secondToken.IsCancellationRequested);
            secondRelease.TrySetResult(); using var response = received = await two; await second.StopAsync(); Equal(2, handler.Calls); Equal(0, handler.Disposals);
        }
        finally
        {
            firstRelease.TrySetResult(); secondRelease.TrySetResult(); await Observe(one); await Observe(two);
            await Observe(stop ?? first.StopAsync()); await Observe(second.StopAsync());
            if (received is null && two.IsCompletedSuccessfully) two.Result.Dispose();
        }
    }
    private static async Task DisposedClient()
    {
        using var handler = new Handler((request, token) => Task.FromResult(Response()));
        var client = new HttpClient(handler, false); client.Dispose(); using var request = Request();
        var lease = AdmittedHttpClientRequestFactory.Create(client)(request);
        await Throws<ObjectDisposedException>(() => lease.SendAsync(default).AsTask());
        var stop = lease.StopAsync(); await stop; Check(ReferenceEquals(stop, lease.StopAsync())); Equal(0, handler.Calls);
    }
    private static async Task<T> Throws<T>(Func<Task> body) where T : Exception
    { try { await body(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void ThrowsAction<T>(Func<object> body) where T : Exception
    { try { body(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static IEnumerable<Exception> Flatten(Exception error) => error is AggregateException aggregate ? aggregate.InnerExceptions.SelectMany(Flatten) : [error];
    private static async Task Observe(Task original) { try { await original; } catch { } }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Admitted HTTP fixture assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}"); }
}
