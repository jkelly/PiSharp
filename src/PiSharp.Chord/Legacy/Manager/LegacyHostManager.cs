// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): manager.ts.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

public sealed record LegacyControlMessage(string Type)
{
    public JsonNode? Value { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
    public int? ActiveCalls { get; init; }
    public int? Viewers { get; init; }
    public int? DurableTasks { get; init; }
    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = Type };
        if (Value is not null) json["value"] = Value.DeepClone();
        if (Message is not null) json["message"] = Message;
        if (Reason is not null) json["reason"] = Reason;
        if (ActiveCalls is not null) json["activeCalls"] = ActiveCalls;
        if (Viewers is not null) json["viewers"] = Viewers;
        if (DurableTasks is not null) json["durableTasks"] = DurableTasks;
        return json;
    }
    internal static LegacyControlMessage? FromJson(JsonNode? node) => node is JsonObject json && json["type"] is { } type
        ? new(type.GetValue<string>()) { Value = json["value"]?.DeepClone(), Message = json["message"]?.GetValue<string>(), Reason = json["reason"]?.GetValue<string>(),
            ActiveCalls = json["activeCalls"]?.GetValue<int>(), Viewers = json["viewers"]?.GetValue<int>(), DurableTasks = json["durableTasks"]?.GetValue<int>() } : null;
}
public sealed record LegacyProcessExit(int? Code, string? Signal = null);
public sealed record LegacyLiveness(bool Booting, int ActiveCalls, int Viewers, int DurableTasks, TimeSpan IdleFor);
public interface ILegacyHostInstance
{
    int Pid { get; }
    ILegacyLink Link { get; }
    Task<LegacyProcessExit> Exited { get; }
    void Send(LegacyControlMessage message);
    void KillTree();
    void OnControl(Action<LegacyControlMessage> listener);
}
public interface ILegacyHostDriver { ILegacyHostInstance Spawn(); }
public sealed record LegacyHostManagerOptions
{
    public required ILegacyHostDriver Driver { get; init; }
    public Func<LegacyLiveness, bool>? KeepAlive { get; init; }
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Action<LegacyProcessExit>? OnExit { get; init; }
    public Action<Exception>? OnError { get; init; }
}

/// <summary>Owns at most one child, with serialized start/stop/replace and readiness admission.</summary>
public sealed class LegacyHostManager : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly LegacyHostManagerOptions options;
    private Task queue = Task.CompletedTask;
    private ILegacyHostInstance? instance;
    private string status = "stopped";
    private int activeCalls, viewers, durableTasks;
    private bool booting, autoStopQueued, disposed;
    private DateTimeOffset? idleSince;
    private ITimer? timer;
    public LegacyHostManager(LegacyHostManagerOptions options)
    {
        this.options = options;
        if (options.Grace < TimeSpan.Zero || options.ReadyTimeout <= TimeSpan.Zero || options.CheckInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
    public string Status { get { lock (gate) return status; } }
    public ILegacyHostInstance? Instance { get { lock (gate) return instance; } }
    public LegacyLiveness Liveness { get { lock (gate) return new(booting, activeCalls, viewers, durableTasks,
        idleSince is { } since ? options.TimeProvider.GetUtcNow() - since : TimeSpan.Zero); } }
    public Task<ILegacyHostInstance> StartAsync() => Serialized(StartCore);
    public Task StopAsync() => Serialized(async () => { await StopCore().ConfigureAwait(false); return true; });
    public Task<ILegacyHostInstance> ReplaceAsync() => Serialized(async () => { await StopCore().ConfigureAwait(false); return await StartCore().ConfigureAwait(false); });
    private Task<T> Serialized<T>(Func<Task<T>> operation)
    {
        lock (gate)
        {
            if (disposed) return Task.FromException<T>(new ObjectDisposedException(nameof(LegacyHostManager)));
            // A rejected start never poisons subsequent operations. The queue owns processes, not host callbacks.
            var next = queue.ContinueWith(_ => operation(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            queue = next.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return next;
        }
    }
    private async Task<ILegacyHostInstance> StartCore()
    {
        lock (gate)
        {
            if (instance is not null && status == "running") return instance;
            status = "starting"; booting = true; activeCalls = viewers = durableTasks = 0; idleSince = null;
        }
        ILegacyHostInstance current;
        try { current = options.Driver.Spawn(); }
        catch { lock (gate) { status = "stopped"; booting = false; } throw; }
        lock (gate) instance = current;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        current.OnControl(message =>
        {
            lock (gate)
            {
                if (!ReferenceEquals(instance, current)) return;
                switch (message.Type)
                {
                    case "ready": ready.TrySetResult(); break;
                    case "fatal": ready.TrySetException(new ChordException("unavailable", "child failed: " + message.Message, message.Reason)); break;
                    case "status":
                        activeCalls = Math.Max(0, message.ActiveCalls ?? 0); viewers = Math.Max(0, message.Viewers ?? 0); durableTasks = Math.Max(0, message.DurableTasks ?? 0);
                        if ((long)activeCalls + viewers + durableTasks > 0) idleSince = null;
                        else idleSince ??= options.TimeProvider.GetUtcNow();
                        break;
                }
            }
        });
        _ = ObserveExit(current, ready);
        try { await ready.Task.WaitAsync(options.ReadyTimeout, options.TimeProvider).ConfigureAwait(false); }
        catch (Exception error)
        {
            current.KillTree(); await current.Exited.ConfigureAwait(false);
            lock (gate) if (ReferenceEquals(instance, current)) { instance = null; status = "stopped"; booting = false; }
            throw error is TimeoutException ? new ChordException("unavailable", "child not ready in time", cause: error) : error;
        }
        lock (gate)
        {
            if (!ReferenceEquals(instance, current) || current.Exited.IsCompleted) throw new ChordException("unavailable", "child exited while becoming ready");
            booting = false; status = "running";
            if ((long)activeCalls + viewers + durableTasks == 0) idleSince ??= options.TimeProvider.GetUtcNow();
            if (options.KeepAlive is not null) timer = options.TimeProvider.CreateTimer(_ => CheckIdle(), null, options.CheckInterval, options.CheckInterval);
        }
        return current;
    }
    private async Task ObserveExit(ILegacyHostInstance current, TaskCompletionSource ready)
    {
        LegacyProcessExit exit;
        try { exit = await current.Exited.ConfigureAwait(false); }
        catch (Exception error) { ready.TrySetException(error); Report(error); return; }
        lock (gate)
        {
            if (ReferenceEquals(instance, current))
            {
                instance = null; status = "stopped"; booting = false; idleSince = null;
                timer?.Dispose(); timer = null; autoStopQueued = false;
            }
        }
        ready.TrySetException(new ChordException("unavailable", $"child exited before ready ({exit.Code?.ToString() ?? exit.Signal})"));
        try { options.OnExit?.Invoke(exit); } catch (Exception error) { Report(error); }
    }
    private void CheckIdle()
    {
        lock (gate) if (status != "running" || autoStopQueued || disposed) return;
        bool keep;
        try { keep = options.KeepAlive!(Liveness); }
        catch (Exception error) { Report(error); return; }
        if (keep) return;
        lock (gate) { if (status != "running" || autoStopQueued || disposed) return; autoStopQueued = true; }
        _ = StopAsync();
    }
    private async Task StopCore()
    {
        ILegacyHostInstance? current;
        lock (gate)
        {
            current = instance; timer?.Dispose(); timer = null;
            if (current is null) { status = "stopped"; booting = false; autoStopQueued = false; return; }
            status = "stopping";
        }
        try { current.Send(new("shutdown")); } catch (Exception error) { Report(error); }
        try { await current.Exited.WaitAsync(options.Grace, options.TimeProvider).ConfigureAwait(false); }
        catch (TimeoutException) { current.KillTree(); await current.Exited.ConfigureAwait(false); }
        lock (gate) { if (ReferenceEquals(instance, current)) instance = null; status = "stopped"; booting = false; autoStopQueued = false; }
    }
    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending = queue.ContinueWith(_ => StopCore(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            queue = pending;
        }
        await pending.ConfigureAwait(false);
    }
    private void Report(Exception error)
    {
        try { options.OnError?.Invoke(error); } catch (Exception) { /* User reporting must not poison process ownership. */ }
    }
}
