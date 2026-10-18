// PiSharp-only (decision 0008). One Server-Sent Events response, after durable-pi's openSse (apps/dpi-backend/src/streams.ts, MIT): numbered
// `id`/`event`/`data` frames, a `: ping` comment when idle, and a backlog limit: a viewer that falls 2 MB behind is cut off and reconnects
// to a fresh snapshot instead of making the device buffer without bound. Frames go through a queue that one writer drains, so producers never
// wait for a slow client. A stream is a viewer only: closing it never stops work.
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using PiSharp.Durable.Client;

namespace PiSharp.Durable.Server;

internal sealed class SseStream : IAsyncDisposable
{
    private readonly HttpResponse response;
    private readonly DurableDeviceServerOptions options;
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource closed;
    private readonly Dictionary<string, string> last = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly Task pump;
    private long sequence;
    private long backlog;
    private long lastWrite = Environment.TickCount64;

    private SseStream(HttpContext http, DurableDeviceServerOptions options)
    {
        response = http.Response;
        this.options = options;
        closed = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        pump = PumpAsync();
    }

    /// <summary>Cancelled when the client goes away or the stream is cut off.</summary>
    public CancellationToken Closed => closed.Token;

    /// <summary>Starts the response and sends <c>hello</c>.</summary>
    public static async Task<SseStream> OpenAsync(HttpContext http, JsonObject scope, DurableDeviceServerOptions options)
    {
        http.Response.StatusCode = 200;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
        var stream = new SseStream(http, options);
        stream.Send(new JsonObject
        {
            ["type"] = "hello",
            ["apiVersion"] = DurableWire.ApiVersion,
            ["serverTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["scope"] = scope,
        });
        return stream;
    }

    /// <summary>Queues a frame; <paramref name="frame"/> carries its <c>type</c>.</summary>
    public void Send(JsonObject frame) => Enqueue(frame["type"]?.GetValue<string>() ?? "message", frame.ToJsonString(DurableWire.Options));

    /// <summary>Queues a frame unless it equals the last one sent under <paramref name="key"/>.</summary>
    public void SendIfChanged(string key, JsonObject frame)
    {
        var json = frame.ToJsonString(DurableWire.Options);
        lock (gate)
        {
            if (last.TryGetValue(key, out var previous) && previous == json) return;
            last[key] = json;
        }
        Enqueue(frame["type"]?.GetValue<string>() ?? "message", json);
    }

    /// <summary>Completes when the stream closes.</summary>
    public Task WaitClosedAsync() => Task.Delay(Timeout.Infinite, Closed).ContinueWith(_ => { }, CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void Enqueue(string type, string json)
    {
        if (closed.IsCancellationRequested) return;
        string text;
        lock (gate) text = SseReader.Format(++sequence, type, json);
        if (Interlocked.Add(ref backlog, Encoding.UTF8.GetByteCount(text)) > options.MaxStreamBacklogBytes)
        {
            Close();
            return;
        }
        queue.Writer.TryWrite(text);
    }

    private async Task PumpAsync()
    {
        using var ping = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1000, options.PingInterval.TotalMilliseconds / 3)));
        var pinging = PingAsync(ping);
        try
        {
            await foreach (var text in queue.Reader.ReadAllAsync(closed.Token).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await response.Body.WriteAsync(bytes, closed.Token).ConfigureAwait(false);
                await response.Body.FlushAsync(closed.Token).ConfigureAwait(false);
                Interlocked.Add(ref backlog, -bytes.Length);
                Interlocked.Exchange(ref lastWrite, Environment.TickCount64);
            }
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The client went away.
        }
        finally
        {
            Close();
            ping.Dispose();
            try { await pinging.ConfigureAwait(false); } catch (OperationCanceledException) { /* Closed. */ }
        }
    }

    private async Task PingAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync(closed.Token).ConfigureAwait(false))
            if (Environment.TickCount64 - Interlocked.Read(ref lastWrite) >= options.PingInterval.TotalMilliseconds && Interlocked.Read(ref backlog) == 0)
                queue.Writer.TryWrite(": ping\n\n");
    }

    /// <summary>Ends the stream.</summary>
    public void Close()
    {
        queue.Writer.TryComplete();
        try { closed.Cancel(); } catch (ObjectDisposedException) { /* Already disposed. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { /* Closed. */ }
        closed.Dispose();
    }
}

/// <summary>Runs work at most once per delay and never concurrently, always catching up with the newest trigger (durable-pi
/// <c>coalesce</c>).</summary>
internal sealed class Coalescer
{
    private readonly SemaphoreSlim signal = new(0, 1);

    public void Trigger()
    {
        try { signal.Release(); }
        catch (SemaphoreFullException) { /* Already triggered. */ }
    }

    public async Task RunAsync(Func<Task> work, TimeSpan delay, Action<Exception> report, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                try { await work().ConfigureAwait(false); }
                catch (Exception failure) when (failure is not OperationCanceledException) { report(failure); }
            }
        }
        catch (OperationCanceledException) { /* The stream closed. */ }
    }
}
