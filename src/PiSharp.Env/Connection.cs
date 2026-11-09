// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/src/connection.ts.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PiSharp.Env.Protocol;

namespace PiSharp.Env;

/// <summary>A lazy, reconnecting connection. A lost request is never replayed: a mutation's outcome may be unknown.</summary>
public sealed class Connection : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ConnectionOptions options;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<ConnectionSession> sessions = [];
    private readonly HashSet<Task> startupOperations = [];
    private readonly HashSet<Task> factoryOperations = [];
    private Task<(RemoteInfo Info, ConnectionSession Session)>? ready;
    private ConnectionSession? current;
    private long sessionId;
    private long nextRequestId;
    private bool closed;
    private Task? disposal;

    public Connection(ConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if ((options.Command is null) == (options.CommandFactory is null))
            throw new ArgumentException("Supply exactly one of Command and CommandFactory.", nameof(options));
        foreach (var delay in new[] { options.StartupTimeout, options.PingInterval, options.SilenceLimit, options.AbortGrace, options.ShutdownTimeout })
            if (delay <= TimeSpan.Zero || delay.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options), "Connection time limits must be positive, finite timer intervals.");
        this.options = options with { Command = options.Command?.ToArray() };
    }

    public async Task<RemoteInfo> InfoAsync(CancellationToken cancellationToken = default) =>
        (await WaitForStartAsync(cancellationToken).ConfigureAwait(false)).Info;

    public async Task<long> SessionAsync(CancellationToken cancellationToken = default) =>
        (await WaitForStartAsync(cancellationToken).ConfigureAwait(false)).Session.Id;

    public async Task<RemoteReply> RequestAsync(string op, JsonObject json, RemoteRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(op);
        ArgumentNullException.ThrowIfNull(json);
        if (cancellationToken.IsCancellationRequested) throw RemoteError.Aborted();
        ConnectionSession session;
        if (requestOptions?.Session is { } required)
        {
            lock (gate)
            {
                session = current is { Live: true } selected && selected.Id == required
                    ? selected : throw RemoteError.Lost("pi-env connection lost");
            }
        }
        else session = (await WaitForStartAsync(cancellationToken).ConfigureAwait(false)).Session;
        return await SendAsync(session, op, json, requestOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Kill an exec without reporting it as aborted. Handles never migrate between sessions.</summary>
    public void Kill(uint id, long session)
    {
        ConnectionSession? selected;
        lock (gate) selected = current is { Live: true } candidate && candidate.Id == session ? candidate : null;
        selected?.Send(new(FrameType.Cancel, id, new() { ["mode"] = "kill" }));
    }

    /// <summary>Stop this connection permanently and fail all outstanding requests.</summary>
    public void Close()
    {
        ConnectionSession[] all;
        lock (gate)
        {
            if (closed) return;
            closed = true;
            all = sessions.ToArray();
        }
        lifetime.Cancel();
        foreach (var session in all) session.Fail(RemoteError.Closed());
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        // Publish the one disposal task before cancellation can run user continuations.
        await Task.Yield();
        Close();
        try { await DrainOwnedWorkAsync().WaitAsync(options.ShutdownTimeout).ConfigureAwait(false); }
        catch (TimeoutException error)
        {
            throw new TimeoutException("pi-env shutdown did not finish; original startup cleanup remains owned in the background.", error);
        }
    }

    private async Task DrainOwnedWorkAsync()
    {
        // Await start attempts first: a factory may be recorded by an already admitted start while Close runs.
        Task[] starts;
        lock (gate) starts = startupOperations.ToArray();
        await SettleAllAsync(starts).ConfigureAwait(false);
        Task[] factories;
        ConnectionSession[] all;
        lock (gate) { factories = factoryOperations.ToArray(); all = sessions.ToArray(); }
        await SettleAllAsync(factories).ConfigureAwait(false);
        foreach (var session in all) await session.DisposeAsync().ConfigureAwait(false);
    }

    private static Task SettleAllAsync(IEnumerable<Task> tasks) => Task.WhenAll(tasks.Select(async task =>
    {
        try { await task.ConfigureAwait(false); }
        catch { /* Failure is already delivered to the startup caller; joining still waits for its finally blocks. */ }
    }));

    private async Task ObserveFactoryAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch { /* Observe even when the startup waiter timed out first. */ }
        finally { lock (gate) factoryOperations.Remove(operation); }
    }

    private async Task<(RemoteInfo Info, ConnectionSession Session)> WaitForStartAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) throw RemoteError.Aborted();
        try { return await ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw RemoteError.Aborted(); }
    }

    private Task<(RemoteInfo Info, ConnectionSession Session)> ConnectAsync()
    {
        lock (gate)
        {
            if (closed) return Task.FromException<(RemoteInfo, ConnectionSession)>(RemoteError.Closed());
            if (ready is null)
            {
                ready = StartAsync();
                startupOperations.Add(ready);
                _ = ClearFailedStartAsync(ready);
            }
            return ready;
        }
    }

    private async Task ClearFailedStartAsync(Task<(RemoteInfo, ConnectionSession)> attempt)
    {
        try { await attempt.ConfigureAwait(false); }
        catch { lock (gate) { if (ReferenceEquals(ready, attempt)) ready = null; } }
        finally { lock (gate) startupOperations.Remove(attempt); }
    }

    private async Task<(RemoteInfo Info, ConnectionSession Session)> StartAsync()
    {
        // Defer until ConnectAsync has published this attempt, including for immediate spawn failures.
        await Task.Yield();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(options.StartupTimeout);
        ConnectionSession? session = null;
        try
        {
            IReadOnlyList<string> command;
            if (options.CommandFactory is { } factory)
            {
                var original = factory(timeout.Token);
                lock (gate) factoryOperations.Add(original);
                _ = ObserveFactoryAsync(original);
                command = await original.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            else command = options.Command!;
            if (command.Count == 0 || string.IsNullOrEmpty(command[0]) || command.Any(argument => argument is null || argument.Contains('\0')))
                throw new ArgumentException("Invalid daemon command.");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var start = new ProcessStartInfo(command[0])
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("serve"); start.ArgumentList.Add("--token"); start.ArgumentList.Add(token);
            lock (gate)
            {
                if (closed) throw RemoteError.Closed();
                var process = new Process { StartInfo = start };
                try { if (!process.Start()) throw new IOException("Daemon process did not start."); }
                catch { process.Dispose(); throw; }
                session = new ConnectionSession(++sessionId, process, token, options, OnLost);
                sessions.Add(session);
                current = session;
            }
            session.Start();
            RemoteReply hello;
            try { hello = await SendAsync(session, "hello", new() { ["protocol"] = 1 }, null, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            { throw RemoteError.Lost($"pi-env did not answer within {options.StartupTimeout.TotalSeconds:g} s"); }
            var info = RemoteInfo.Parse(hello.Json);
            if (info.Protocol != 1) throw RemoteError.Lost($"Unsupported protocol {info.Protocol}");
            options.ValidateHello?.Invoke(info);
            session.MarkReady();
            return (info, session);
        }
        catch (Exception error)
        {
            var failure = error is RemoteError remote ? remote : new RemoteError(new()
            {
                ["code"] = "spawn_error", ["message"] = error.Message, ["lost"] = true,
            });
            if (timeout.IsCancellationRequested && !lifetime.IsCancellationRequested)
                failure = RemoteError.Lost($"pi-env did not answer within {options.StartupTimeout.TotalSeconds:g} s");
            if (lifetime.IsCancellationRequested) failure = RemoteError.Closed();
            if (session is not null)
            {
                // Reap first so pre-ready stderr is drained before it is included in the error.
                session.Fail(failure);
                await session.DrainStartupDiagnosticsAsync().ConfigureAwait(false);
                failure = session.WithDiagnostics(failure);
            }
            throw failure;
        }
    }

    private Task<RemoteReply> SendAsync(ConnectionSession session, string op, JsonObject json,
        RemoteRequestOptions? requestOptions, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref nextRequestId);
        if (id > uint.MaxValue) throw new InvalidOperationException("pi-env request IDs exhausted; create a new connection.");
        return session.RequestAsync((uint)id, op, json, requestOptions, cancellationToken);
    }

    private void OnLost(ConnectionSession session)
    {
        lock (gate)
        {
            if (ReferenceEquals(current, session))
            {
                current = null;
                ready = null;
            }
        }
        _ = ReleaseSessionAsync(session);
    }

    private async Task ReleaseSessionAsync(ConnectionSession session)
    {
        // Reconnects must not retain every former process and its redirected pipes until final shutdown.
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            try { options.OnLog?.Invoke("pi-env cleanup failed: " + error.Message); } catch { }
        }
        finally { lock (gate) sessions.Remove(session); }
    }
}
