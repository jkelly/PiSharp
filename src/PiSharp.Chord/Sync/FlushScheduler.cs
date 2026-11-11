// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/client.ts (FlushScheduler).
namespace PiSharp.Chord.Sync;

public sealed record FlushSchedulerOptions
{
    public required Func<Task> Flush { get; init; }
    /// <summary>Arms one timer and returns its disarm action.</summary>
    public required Func<Action, double, Action> SetTimer { get; init; }
    public required Func<double> Now { get; init; }
    public required Action<Exception> OnError { get; init; }
    public double? IntervalMs { get; init; }
    public double? UrgentMs { get; init; }
    public double? RetryMs { get; init; }
    public double? MaxRetryMs { get; init; }
}

/// <summary>Schedules one flush at a time, preserves urgency during a flush and retries failures with capped backoff.</summary>
public sealed class FlushScheduler(FlushSchedulerOptions options)
{
    private readonly Lock gate = new();
    private double? due;
    private Action? disarm;
    private Task? running;
    private Task? closing;
    private double? pendingDue;
    private int failures;
    private bool closed;
    private long timerGeneration;

    public void Notify(bool urgent)
    {
        lock (gate)
        {
            if (closed) return;
            var next = options.Now() + (urgent ? options.UrgentMs ?? 200 : options.IntervalMs ?? 5000);
            if (running is not null)
            {
                pendingDue = Math.Min(pendingDue ?? next, next);
                return;
            }
            Arm(next);
        }
    }

    /// <summary>Stops scheduling, waits for an active flush and flushes once more. A failure rejects the close.</summary>
    public Task CloseAsync()
    {
        Task? underway;
        TaskCompletionSource completion;
        lock (gate)
        {
            if (closing is not null) return closing;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            closing = completion.Task;
            closed = true;
            timerGeneration++;
            disarm?.Invoke();
            disarm = null;
            due = null;
            underway = running;
        }
        _ = FinishCloseAsync(underway, completion);
        return completion.Task;
    }

    private async Task FinishCloseAsync(Task? underway, TaskCompletionSource completion)
    {
        try
        {
            if (underway is not null) await underway.ConfigureAwait(false);
            await options.Flush().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    // Called while holding gate. A generation check makes a late/disarmed timer harmless.
    private void Arm(double next)
    {
        if (due is { } existing && existing <= next) return;
        var generation = ++timerGeneration;
        disarm?.Invoke();
        due = next;
        disarm = null;
        var cancel = options.SetTimer(() => StartRun(generation), Math.Max(0, next - options.Now()));
        // An injected timer may fire synchronously, including while SetTimer is returning.
        if (!closed && generation == timerGeneration && due == next) disarm = cancel;
        else cancel();
    }

    private void StartRun(long generation)
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            if (closed || generation != timerGeneration || running is not null) return;
            timerGeneration++;
            due = null;
            disarm = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running = completion.Task;
        }
        _ = RunAsync(completion);
    }

    private async Task RunAsync(TaskCompletionSource completion)
    {
        double? retry = null;
        Exception? callbackFailure = null;
        try
        {
            await options.Flush().ConfigureAwait(false);
            lock (gate) failures = 0;
        }
        catch (Exception error)
        {
            try
            {
                options.OnError(error);
                lock (gate)
                {
                    var delay = (options.RetryMs ?? 1000) * Math.Pow(2, Math.Min(failures, 1024));
                    if (failures < int.MaxValue) failures++;
                    retry = options.Now() + Math.Min(delay, options.MaxRetryMs ?? 60_000);
                }
            }
            catch (Exception reportError) { callbackFailure = reportError; }
        }
        finally
        {
            lock (gate)
            {
                running = null;
                var pending = pendingDue;
                pendingDue = null;
                if (!closed && callbackFailure is null && (retry ?? pending) is { } next) Arm(next);
            }
        }
        if (callbackFailure is null) completion.TrySetResult();
        else completion.TrySetException(callbackFailure);
    }
}
