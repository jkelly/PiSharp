// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/connection.ts.
namespace PiSharp.Chord.Surfaces;

public enum ConnectionState { Connecting, Connected, Waiting }
public interface ISocketEvents { void Opened(); void Received(); }
public interface ILoopLink { void OnClose(Action listener); void Close(); }
public interface ILoopEnvironment
{
    double Now();
    Action After(double milliseconds, Action callback);
    bool Online();
    Action OnWake(Action listener);
}

/// <summary>Native timer/clock environment. A desktop or terminal adapter signals wake and connectivity changes.</summary>
public sealed class LoopEnvironment(TimeProvider? timeProvider = null) : ILoopEnvironment
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private event Action? Woken;
    public bool IsOnline { get; set; } = true;
    public double Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();
    public bool Online() => IsOnline;
    public Action OnWake(Action listener) { Woken += listener; return () => Woken -= listener; }
    public void Wake() => Woken?.Invoke();
    public Action After(double milliseconds, Action callback)
    {
        var timer = new Scheduled(callback);
        timer.Start(clock, milliseconds);
        return timer.Cancel;
    }
    private sealed class Scheduled(Action callback)
    {
        private ITimer? timer;
        private int completed;
        public void Start(TimeProvider clock, double milliseconds)
        {
            timer = clock.CreateTimer(_ => Fire(), null, TimeSpan.FromMilliseconds(milliseconds), Timeout.InfiniteTimeSpan);
            if (Volatile.Read(ref completed) != 0) timer.Dispose();
        }
        private void Fire()
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return;
            try { callback(); } finally { timer?.Dispose(); }
        }
        public void Cancel() { Interlocked.Exchange(ref completed, 1); timer?.Dispose(); }
    }
}

public sealed class ConnectionOptions
{
    public required Func<ISocketEvents, ILoopLink> Open { get; init; }
    public required Action<ConnectionState> OnState { get; init; }
    public required Func<Task<bool>> CanRetry { get; init; }
    public required ILoopEnvironment Environment { get; init; }
}

/// <summary>One current connection, bounded reconnect backoff and a wake grace period for queued pings.</summary>
public sealed class ConnectionLoop : IDisposable
{
    public const double RetryInitialMilliseconds = 250, RetryMaximumMilliseconds = 5000,
        StableMilliseconds = 10000, ConnectTimeoutMilliseconds = 20000,
        StaleMilliseconds = 25000, WakeGraceMilliseconds = 1000;
    private readonly object gate = new();
    private readonly ConnectionOptions options;
    private readonly ILoopEnvironment environment;
    private readonly Action removeWake;
    private Attempt? current;
    private Action? cancelRetry, cancelStale;
    private int failures;
    private bool deciding, wokeWhileDeciding, stopped;

    private ConnectionLoop(ConnectionOptions options)
    { this.options = options; environment = options.Environment; removeWake = environment.OnWake(Wake); }

    public static ConnectionLoop KeepConnected(ConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var loop = new ConnectionLoop(options);
        loop.Connect();
        return loop;
    }

    private void Connect()
    {
        lock (gate)
        {
            if (stopped) return;
            cancelRetry = null;
            var attempt = new Attempt(this, environment.Now());
            current = attempt;
            options.OnState(ConnectionState.Connecting);
            try
            {
                var link = options.Open(attempt);
                attempt.Link = link;
                if (stopped || !ReferenceEquals(current, attempt)) { link.Close(); return; }
                if (attempt.OpenedAt is null)
                    attempt.CancelTimeout = environment.After(ConnectTimeoutMilliseconds, () =>
                    { lock (gate) if (ReferenceEquals(current, attempt) && attempt.OpenedAt is null) link.Close(); });
                link.OnClose(() => Closed(attempt));
            }
            catch { Closed(attempt); }
        }
    }

    private void Closed(Attempt attempt)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, attempt)) return;
            current = null;
            attempt.CancelTimeout?.Invoke();
            cancelStale?.Invoke(); cancelStale = null;
            if (stopped) return;
            options.OnState(ConnectionState.Waiting);
            if (attempt.OpenedAt is { } opened && environment.Now() - opened >= StableMilliseconds) failures = 0;
            deciding = true; wokeWhileDeciding = false;
            _ = DecideAsync();
        }
    }

    private async Task DecideAsync()
    {
        bool retry;
        try { retry = await options.CanRetry().ConfigureAwait(false); }
        catch { retry = true; }
        lock (gate)
        {
            deciding = false;
            if (stopped) return;
            if (!retry) { Stop(); return; }
            var backoff = Math.Min(RetryMaximumMilliseconds, RetryInitialMilliseconds * Math.Pow(2, Math.Min(failures++, 30)));
            var delay = wokeWhileDeciding ? 0 : environment.Online() ? backoff : RetryMaximumMilliseconds;
            cancelRetry = environment.After(delay, Connect);
        }
    }

    private void Wake()
    {
        lock (gate)
        {
            if (stopped) return;
            if (cancelRetry is { } retry) { retry(); cancelRetry = null; Connect(); return; }
            if (deciding) { wokeWhileDeciding = true; return; }
            var attempt = current;
            if (attempt?.OpenedAt is null) return;
            cancelStale?.Invoke();
            var due = Math.Max(WakeGraceMilliseconds, attempt.LastFrame + StaleMilliseconds + 1 - environment.Now());
            cancelStale = environment.After(due, () =>
            {
                lock (gate)
                {
                    cancelStale = null;
                    if (ReferenceEquals(current, attempt) && environment.Now() - attempt.LastFrame > StaleMilliseconds) attempt.Link?.Close();
                }
            });
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            removeWake(); cancelRetry?.Invoke(); cancelRetry = null;
            cancelStale?.Invoke(); cancelStale = null;
            current?.CancelTimeout?.Invoke();
            current?.Link?.Close(); current = null;
        }
    }
    public void Dispose() => Stop();

    private sealed class Attempt(ConnectionLoop owner, double now) : ISocketEvents
    {
        public ILoopLink? Link;
        public double? OpenedAt;
        public double LastFrame = now;
        public Action? CancelTimeout;
        public void Opened()
        {
            lock (owner.gate)
            {
                if (!ReferenceEquals(owner.current, this) || owner.stopped) return;
                OpenedAt = LastFrame = owner.environment.Now();
                CancelTimeout?.Invoke(); CancelTimeout = null;
                owner.options.OnState(ConnectionState.Connected);
            }
        }
        public void Received() { lock (owner.gate) if (ReferenceEquals(owner.current, this)) LastFrame = owner.environment.Now(); }
    }
}
