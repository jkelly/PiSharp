using PiSharp.Env.Protocol;

namespace PiSharp.Env.Daemon;

/// <summary>One stdout writer, control-first scheduling, and four MiB of queued command/bulk payload.</summary>
internal sealed class DaemonOutput(Stream stream, CancellationToken lifetime)
{
    private const int Capacity = 4 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Queue<Item> control = new(), bulk = new();
    private readonly SemaphoreSlim ready = new(0);
    private readonly Queue<QuotaWaiter> waiting = new();
    private int used;
    private Task? runner;
    public void Start() => runner = RunAsync();
    public Task Completion => runner ?? Task.CompletedTask;

    public async ValueTask SendAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        var isBulk = frame.Payload.Length > 64 * 1024 || frame.Type == FrameType.Event && frame.Json["kind"]?.ToString() == "output";
        var size = isBulk ? Math.Min(Capacity, Math.Max(1, frame.Payload.Length)) : 0;
        if (size != 0) await ReserveAsync(size, cancellationToken).ConfigureAwait(false);
        var item = new Item(frame, size, cancellationToken);
        lock (gate)
        {
            if (lifetime.IsCancellationRequested) { Release(size); throw new OperationCanceledException(lifetime); }
            (isBulk ? bulk : control).Enqueue(item);
            ready.Release();
        }
        await item.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private async Task ReserveAsync(int bytes, CancellationToken ct)
    {
        Task? wait = null;
        lock (gate)
        {
            if (waiting.Count == 0 && used + bytes <= Capacity) used += bytes;
            else { var waiter = new QuotaWaiter(bytes); waiting.Enqueue(waiter); wait = waiter.Done.Task; }
        }
        if (wait is not null)
        {
            // Waiters stay in arrival order; cancelling one releases its reservation as soon as it is granted.
            await wait.ConfigureAwait(false);
            if (ct.IsCancellationRequested) { lock (gate) Release(bytes); ct.ThrowIfCancellationRequested(); }
        }
    }
    private void Release(int bytes)
    {
        used -= bytes;
        while (waiting.TryPeek(out var waiter) && used + waiter.Bytes <= Capacity)
        {
            waiting.Dequeue(); used += waiter.Bytes; waiter.Done.TrySetResult();
        }
    }
    private async Task RunAsync()
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                await ready.WaitAsync(lifetime).ConfigureAwait(false);
                Item item;
                lock (gate) item = control.Count > 0 ? control.Dequeue() : bulk.Dequeue();
                try
                {
                    if (item.CancellationToken.IsCancellationRequested) item.Done.TrySetCanceled(item.CancellationToken);
                    else
                    {
                        await FrameCodec.WriteAsync(stream, item.Frame, lifetime).ConfigureAwait(false);
                        item.Done.TrySetResult();
                    }
                }
                catch (Exception error) { item.Done.TrySetException(error); throw; }
                finally { lock (gate) Release(item.Bytes); }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            lock (gate)
            {
                foreach (var item in control.Concat(bulk)) item.Done.TrySetException(failure ?? new OperationCanceledException(lifetime));
                control.Clear(); bulk.Clear();
                foreach (var waiter in waiting) waiter.Done.TrySetException(failure ?? new OperationCanceledException(lifetime));
                waiting.Clear();
            }
        }
    }
    private sealed record Item(ProtocolFrame Frame, int Bytes, CancellationToken CancellationToken)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record QuotaWaiter(int Bytes)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
