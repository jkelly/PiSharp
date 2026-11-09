using System.Collections.Concurrent;

namespace PiSharp.Chord.Host;

// Only synchronous transitions own the queue. Awaiting external work releases it; its continuation rejoins it.
internal sealed class HostDispatcher : SynchronizationContext
{
    private readonly ConcurrentQueue<Action> queue = new();
    private int scheduled;
    internal Action<Exception>? Unhandled { get; set; }
    public bool IsCurrent => ReferenceEquals(Current, this);
    public override void Post(SendOrPostCallback callback, object? state) => Post(() => callback(state));
    public void Post(Action action)
    {
        queue.Enqueue(action);
        if (Interlocked.CompareExchange(ref scheduled, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(static self => self.Drain(), this, false);
    }
    private void Drain()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            do
            {
                while (queue.TryDequeue(out var action))
                    try { action(); } catch (Exception error) { Unhandled?.Invoke(error); }
                Volatile.Write(ref scheduled, 0);
            } while (!queue.IsEmpty && Interlocked.CompareExchange(ref scheduled, 1, 0) == 0);
        }
        finally { SetSynchronizationContext(previous); }
    }
    public T Invoke<T>(Func<T> action)
    {
        if (IsCurrent) return action();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { result.SetResult(action()); } catch (Exception error) { result.SetException(error); } });
        return result.Task.GetAwaiter().GetResult();
    }
    public void Invoke(Action action) => Invoke(() => { action(); return true; });
    public Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () => { try { result.TrySetResult(await action()); } catch (Exception error) { result.TrySetException(error); } });
        return result.Task;
    }
    public Task RunAsync(Func<Task> action) => RunAsync(async () => { await action(); return true; });
}
