// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/src/terminal.ts.
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Env;

public sealed record TerminalOptions
{
    public required string Cwd { get; init; }
    public IReadOnlyList<string>? Argv { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public bool InheritEnv { get; init; } = true;
    public required int Cols { get; init; }
    public required int Rows { get; init; }
    /// <summary>Raw terminal bytes, in order. Copy bytes if they will be retained.</summary>
    public required Action<ReadOnlyMemory<byte>> OnData { get; init; }
}

public sealed record TerminalForeground(int Pid, string? Name);

/// <summary>A session-bound remote Unix pseudo-terminal. Closing it aborts Exited.</summary>
public sealed class RemoteTerminal : IAsyncDisposable
{
    private const int WriteChunk = 1024 * 1024;
    private readonly Connection connection;
    private readonly uint id;
    private readonly long session;
    private readonly CancellationTokenSource abort;
    private int closed;
    public int Pid { get; }
    public Task<int> Exited { get; }

    private RemoteTerminal(Connection connection, uint id, long session, int pid, Task<int> exited, CancellationTokenSource abort)
    { this.connection = connection; this.id = id; this.session = session; Pid = pid; Exited = exited; this.abort = abort; }

    public static async Task<RemoteTerminal> OpenAsync(Connection connection, TerminalOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.OnData);
        var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint id = 0; long session = 0;
        var json = new JsonObject { ["cwd"] = options.Cwd, ["cols"] = options.Cols, ["rows"] = options.Rows, ["inheritEnv"] = options.InheritEnv };
        if (options.Argv is { } argv) json["argv"] = new JsonArray(argv.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        if (options.Env is { } env) json["env"] = new JsonObject(env.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value))));
        var request = connection.RequestAsync("pty", json, new()
        {
            OnStart = (requestId, run) => { id = requestId; session = run; },
            OnEvent = (eventJson, payload) =>
            {
                if (eventJson["kind"]?.GetValue<string>() == "output") options.OnData(payload);
                else if (eventJson["kind"]?.GetValue<string>() == "started") started.TrySetResult(eventJson["pid"]!.GetValue<int>());
            }
        }, abort.Token);
        var exited = ObserveExitAsync(request, started, abort);
        int pid;
        try { pid = await started.Task.ConfigureAwait(false); }
        catch { await exited.ConfigureAwait(false); throw; }
        return new(connection, id, session, pid, exited, abort);
    }

    private static async Task<int> ObserveExitAsync(Task<RemoteReply> request, TaskCompletionSource<int> started, CancellationTokenSource abort)
    {
        try
        {
            var reply = await request.ConfigureAwait(false);
            if (!started.Task.IsCompleted) throw new InvalidDataException("Terminal ended without a started event.");
            return reply.Json["exitCode"]!.GetValue<int>();
        }
        catch (Exception error) { started.TrySetException(error); throw; }
        finally { abort.Dispose(); }
    }

    /// <summary>Queue input; input after termination is dropped, as in upstream.</summary>
    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));
    public void Write(ReadOnlyMemory<byte> bytes)
    {
        for (var offset = 0; offset < bytes.Length; offset += WriteChunk)
            _ = IgnoreAsync(RequestAsync("ptyWrite", new(), bytes.Slice(offset, Math.Min(WriteChunk, bytes.Length - offset))));
    }
    public void Resize(int cols, int rows) => _ = IgnoreAsync(ResizeAsync(cols, rows));
    public Task ResizeAsync(int cols, int rows, CancellationToken cancellationToken = default) =>
        RequestAsync("ptyResize", new() { ["cols"] = cols, ["rows"] = rows }, cancellationToken: cancellationToken);
    public async Task<TerminalForeground?> ForegroundAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync("ptyForeground", new(), cancellationToken: cancellationToken).ConfigureAwait(false);
        return reply.Json["foreground"] is JsonObject foreground ? new(foreground["pid"]!.GetValue<int>(), foreground["name"]?.GetValue<string>()) : null;
    }
    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0 || Exited.IsCompleted) return;
        try { abort.Cancel(); } catch (ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync() { Close(); try { await Exited.ConfigureAwait(false); } catch (RemoteError) { } }
    private Task<RemoteReply> RequestAsync(string operation, JsonObject json, ReadOnlyMemory<byte> payload = default, CancellationToken cancellationToken = default)
    { json["target"] = id; return connection.RequestAsync(operation, json, new() { Session = session, Payload = payload }, cancellationToken); }
    private static async Task IgnoreAsync(Task task) { try { await task.ConfigureAwait(false); } catch { /* Exited reports terminal/connection loss. */ } }
}
