// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/src/watch.ts.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using JsonValue = System.Text.Json.Nodes.JsonValue;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Env;

public sealed record RemoteWatchOptions
{
    public WatchMode? Mode { get; init; }
    public int? PollIntervalMs { get; init; }
    public int? MaxDirectories { get; init; }
}

/// <summary>A daemon subscription. Transport loss opens a fresh subscription and reports uncertain coverage.</summary>
public sealed class RemoteWatcher : IFileWatcher
{
    private readonly Connection connection;
    private readonly JsonObject arguments;
    private readonly Action<FileChange> onChange;
    private readonly CancellationTokenSource lifetime;
    private readonly object gate = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task running = Task.CompletedTask;
    private WatchMode mode;
    private bool stopped;

    private RemoteWatcher(Connection connection, JsonObject arguments, Action<FileChange> onChange, CancellationToken token)
    { this.connection = connection; this.arguments = arguments; this.onChange = onChange; lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); }

    public WatchMode Mode { get { lock (gate) return mode; } }

    public static async Task<RemoteWatcher> OpenAsync(Connection connection, IReadOnlyList<WatchTarget> absoluteTargets,
        Action<FileChange> onChange, RemoteWatchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(absoluteTargets);
        ArgumentNullException.ThrowIfNull(onChange);
        options ??= new();
        if (options.PollIntervalMs is <= 0 || options.MaxDirectories is < 0 ||
            options.Mode is { } selected && !Enum.IsDefined(selected)) throw new ArgumentOutOfRangeException(nameof(options));
        // Snapshot all caller-owned collections before the first asynchronous operation, including recovery arguments.
        var targets = new JsonArray(absoluteTargets.Select(target => (JsonNode)new JsonObject
        {
            ["path"] = target.Path, ["recursive"] = target.Recursive,
            ["exclude"] = new JsonObject { ["hidden"] = target.Exclude?.Hidden == true,
                ["names"] = new JsonArray((target.Exclude?.Names ?? []).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()) },
        }).ToArray());
        var args = new JsonObject { ["targets"] = targets };
        if (options.Mode is { } watchMode) args["mode"] = watchMode == WatchMode.Polling ? "polling" : "native";
        if (options.PollIntervalMs is { } interval) args["pollIntervalMs"] = interval;
        if (options.MaxDirectories is { } maximum) args["maxDirectories"] = maximum;
        var watcher = new RemoteWatcher(connection, args, onChange, cancellationToken);
        watcher.running = watcher.RunAsync();
        try { await watcher.ready.Task.ConfigureAwait(false); return watcher; }
        catch { await watcher.StopAsync(DurableContext.Background).ConfigureAwait(false); throw; }
    }

    public async Task StopAsync(DurableContext context)
    {
        lock (gate)
        {
            if (!stopped)
            {
                stopped = true;
                lifetime.Cancel();
            }
        }
        await running.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        var opened = false;
        var recovering = false;
        var delay = 1000;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var subscriptionReady = false;
                var failed = false;
                Exception? protocolFailure = null;
                using var subscription = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                try
                {
                    await connection.RequestAsync("watch", (JsonObject)arguments.DeepClone(), new()
                    {
                        OnEvent = (ev, payload) =>
                        {
                            lock (gate)
                            {
                                if (stopped || failed || protocolFailure is not null) return;
                                try
                                {
                                    if (!payload.IsEmpty) throw new InvalidDataException("Watch event contains a binary payload.");
                                    var kind = ev["kind"]?.GetValue<string>();
                                    if (kind == "ready" && !subscriptionReady)
                                    {
                                        mode = ParseMode(ev["mode"]);
                                        subscriptionReady = true;
                                        opened = true;
                                        ready.TrySetResult();
                                        if (recovering) Deliver(new FileOverflowChange());
                                        recovering = false;
                                        delay = 1000;
                                    }
                                    else if (kind == "change" && subscriptionReady)
                                    {
                                        if (ev["mode"] is { } updated) mode = ParseMode(updated);
                                        if (ev["overflow"]?.GetValue<bool>() == true) Deliver(new FileOverflowChange());
                                        else if (ev["paths"] is JsonArray paths)
                                            Deliver(new FilePathsChange(paths.Select(path => path!.GetValue<string>()).ToImmutableArray()));
                                        else throw new InvalidDataException("Watch change requires paths or overflow.");
                                    }
                                    else if (kind == "error" && subscriptionReady)
                                    {
                                        var code = ev["code"]?.GetValue<string>() == "permission_denied" ? FileErrorCode.PermissionDenied : FileErrorCode.Invalid;
                                        Deliver(new FileFailedChange(new(code, ev["message"]?.GetValue<string>() ?? "Watch failed.")));
                                        failed = true;
                                    }
                                    else throw new InvalidDataException("Unexpected watch event ordering.");
                                }
                                catch (Exception error) { protocolFailure = error; subscription.Cancel(); }
                            }
                        },
                    }, subscription.Token).ConfigureAwait(false);
                    if (protocolFailure is not null) throw protocolFailure;
                    if (!subscriptionReady) throw new InvalidDataException("Watch ended before ready.");
                    if (!failed && !lifetime.IsCancellationRequested) throw new InvalidDataException("Watch ended without cancellation or error.");
                    return;
                }
                catch (Exception error)
                {
                    if (lifetime.IsCancellationRequested || failed) break;
                    if (protocolFailure is not null) throw protocolFailure;
                    if (!opened || error is not RemoteError { IsConnectionLost: true }) throw;
                    recovering = true;
                }
                await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 30_000);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!opened) ready.TrySetException(error);
            else lock (gate) { if (!stopped) Deliver(new FileFailedChange(RemoteExecutionEnv.FileFailure(error))); }
        }
        finally
        {
            ready.TrySetException(RemoteError.Aborted());
            lock (gate) { stopped = true; lifetime.Dispose(); }
        }
    }

    private static WatchMode ParseMode(JsonNode? node) => node?.GetValue<string>() switch
    { "native" => WatchMode.Native, "polling" => WatchMode.Polling, _ => throw new InvalidDataException("Invalid watch mode.") };

    // Called while holding gate, so StopAsync cannot race a new callback; observer failures are isolated.
    private void Deliver(FileChange change) { if (!stopped) try { onChange(change); } catch (Exception) { } }
}
