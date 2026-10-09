using System.Collections.Concurrent;

namespace PiSharp.Tui.Pi;

/// <summary>A single-threaded event loop, the C# stand-in for Node's event loop that Pi's TUI and interactive mode assume.
/// Components, rendering and interactive-mode state are only touched on the loop thread; other threads post to it.
/// It is the loop thread's <see cref="SynchronizationContext"/>, so <c>await</c> continuations resume on it.</summary>
public sealed class UiLoop : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
    private readonly Thread? thread;
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int threadId;
    private volatile bool disposed;

    /// <summary>Unhandled exceptions thrown by posted work (Node's uncaughtException).</summary>
    public event Action<Exception>? UnhandledException;

    /// <summary>Starts a loop on its own thread.</summary>
    public UiLoop(string name = "pi-tui")
    {
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.Start();
    }

    /// <summary>A loop driven manually with <see cref="RunPending"/> (tests).</summary>
    private UiLoop(bool manual) { threadId = Environment.CurrentManagedThreadId; }
    public static UiLoop CreateManual() => new(manual: true);

    public bool IsLoopThread => Environment.CurrentManagedThreadId == threadId;
    public Task Completion => stopped.Task;

    private void Run()
    {
        threadId = Environment.CurrentManagedThreadId;
        SetSynchronizationContext(this);
        try
        {
            foreach (var (callback, state) in queue.GetConsumingEnumerable())
                Execute(callback, state);
        }
        finally { stopped.TrySetResult(); }
    }

    private void Execute(SendOrPostCallback callback, object? state)
    {
        try { callback(state); }
        catch (Exception error)
        {
            if (UnhandledException is { } handler) handler(error);
            else throw;
        }
    }

    /// <summary>Runs queued work on the calling thread until the queue is empty (manual loops).</summary>
    public int RunPending(int maximum = 100_000)
    {
        var previous = Current; SetSynchronizationContext(this);
        try
        {
            var count = 0;
            while (count < maximum && queue.TryTake(out var item)) { Execute(item.Callback, item.State); count++; }
            return count;
        }
        finally { SetSynchronizationContext(previous); }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (disposed) return;
        try { queue.Add((d, state)); } catch (InvalidOperationException) { }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (IsLoopThread) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        Post(_ => { try { d(state); } catch (Exception error) { failure = error; } finally { done.Set(); } }, null);
        done.Wait();
        if (failure is not null) throw new AggregateException(failure);
    }

    public override SynchronizationContext CreateCopy() => this;

    public void Post(Action action) => Post(static state => ((Action)state!)(), action);

    /// <summary>Runs <paramref name="action"/> on the loop and returns its completion.</summary>
    public Task InvokeAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { action(); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } });
        return done.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { done.TrySetResult(func()); } catch (Exception error) { done.TrySetException(error); } });
        return done.Task;
    }

    public Task InvokeAsync(Func<Task> func)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try { await func(); done.TrySetResult(); }
            catch (Exception error) { done.TrySetException(error); }
        });
        return done.Task;
    }

    /// <summary>Node's setTimeout: runs on the loop after the delay; dispose the handle to cancel.</summary>
    public IDisposable SetTimeout(Action action, double milliseconds)
    {
        var handle = new TimerHandle();
        handle.Timer = new Timer(_ => Post(() => { if (!handle.Cancelled) { handle.Cancelled = true; action(); } }), null,
            TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)), Timeout.InfiniteTimeSpan);
        return handle;
    }

    /// <summary>Node's setInterval.</summary>
    public IDisposable SetInterval(Action action, double milliseconds)
    {
        var handle = new TimerHandle();
        var period = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
        handle.Timer = new Timer(_ => Post(() => { if (!handle.Cancelled) action(); }), null, period, period);
        return handle;
    }

    /// <summary>Node's process.nextTick/queueMicrotask approximation: runs after the current work item.</summary>
    public void NextTick(Action action) => Post(action);

    private sealed class TimerHandle : IDisposable
    {
        internal Timer? Timer;
        internal volatile bool Cancelled;
        public void Dispose() { Cancelled = true; Timer?.Dispose(); }
    }

    /// <summary>Stops accepting work and lets the loop thread finish what is queued.</summary>
    public void Stop() { if (!queue.IsAddingCompleted) queue.CompleteAdding(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop();
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(5));
        if (thread is null) stopped.TrySetResult();
    }
}
