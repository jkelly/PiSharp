// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/src/process.ts.
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Env;

public sealed record RemoteProcessOptions
{
    public required string Cwd { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public bool InheritEnv { get; init; } = true;
    public required Action<ReadOnlyMemory<byte>> OnStdout { get; init; }
    public Action<ReadOnlyMemory<byte>>? OnStderr { get; init; }
}

/// <summary>A program with raw, separate output pipes and ordered stdin. Closing kills its owned process tree.</summary>
public sealed class RemoteProcess : IAsyncDisposable
{
    private const int WriteChunk = 1024 * 1024;
    private readonly Connection connection;
    private readonly uint id;
    private readonly long session;
    private readonly CancellationTokenSource abort;
    private readonly SemaphoreSlim writes = new(1, 1);
    private bool inputClosed;
    private RemoteProcess(Connection connection, uint id, long session, int pid, Task<int> exited, CancellationTokenSource abort)
    { this.connection = connection; this.id = id; this.session = session; this.abort = abort; Pid = pid; Exited = exited; }
    public int Pid { get; }
    public Task<int> Exited { get; }

    public static async Task<RemoteProcess> OpenAsync(Connection connection, RemoteProcessOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.OnStdout);
        if (!(await connection.InfoAsync(cancellationToken).ConfigureAwait(false)).Features.Contains("process"))
            throw new RemoteError(new() { ["code"] = "not_supported", ["message"] = "The pi-env daemon cannot run processes; update it" });
        var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = new TaskCompletionSource<RemoteProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint requestId = 0; long requestSession = 0;
        var request = connection.RequestAsync("process", new()
        {
            ["cwd"] = options.Cwd, ["argv"] = new JsonArray(options.Argv.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["env"] = options.Env is null ? null : JsonSerializerNode(options.Env), ["inheritEnv"] = options.InheritEnv,
        }, new()
        {
            OnStart = (id, session) => { requestId = id; requestSession = session; },
            OnEvent = (json, payload) =>
            {
                if (json["kind"]?.GetValue<string>() == "started")
                    started.TrySetResult(new(connection, requestId, requestSession, json["pid"]!.GetValue<int>(), exit.Task, abort));
                else if (json["kind"]?.GetValue<string>() == "output")
                {
                    if (json["stream"]?.GetValue<string>() == "stderr") options.OnStderr?.Invoke(payload);
                    else options.OnStdout(payload);
                }
            },
        }, abort.Token);
        _ = CompleteAsync();
        try { return await started.Task.ConfigureAwait(false); }
        catch { try { await exit.Task.ConfigureAwait(false); } catch { } throw; }

        async Task CompleteAsync()
        {
            try
            {
                var reply = await request.ConfigureAwait(false);
                exit.TrySetResult(reply.Json["exitCode"]!.GetValue<int>());
                started.TrySetException(new InvalidDataException("Program exited without a started event"));
            }
            catch (Exception error) { exit.TrySetException(error); started.TrySetException(error); }
            finally { abort.Dispose(); }
        }
    }
    private static JsonObject JsonSerializerNode(IReadOnlyDictionary<string, string> environment)
    { var json = new JsonObject(); foreach (var pair in environment) json[pair.Key] = pair.Value; return json; }

    /// <summary>Writes are serialized, including chunk boundaries. Input after EOF or exit is ignored.</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (inputClosed || Exited.IsCompleted) return;
            for (var offset = 0; offset < data.Length; offset += WriteChunk)
                if (!await SendAsync("processWrite", data.Slice(offset, Math.Min(WriteChunk, data.Length - offset)), cancellationToken).ConfigureAwait(false)) break;
        }
        finally { writes.Release(); }
    }
    public Task WriteAsync(string text, CancellationToken cancellationToken = default) => WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    public void Write(ReadOnlyMemory<byte> data) => _ = WriteAsync(data);
    public void Write(string text) => _ = WriteAsync(text);
    public async Task CloseStdinAsync(CancellationToken cancellationToken = default)
    {
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (!inputClosed && !Exited.IsCompleted) { inputClosed = true; await SendAsync("processCloseStdin", default, cancellationToken).ConfigureAwait(false); } }
        finally { writes.Release(); }
    }
    public void CloseStdin() => _ = CloseStdinAsync();
    public void Close() { try { abort.Cancel(); } catch (ObjectDisposedException) { } }
    public async ValueTask DisposeAsync() { Close(); try { await Exited.ConfigureAwait(false); } catch (RemoteError) { } }
    private async Task<bool> SendAsync(string operation, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        try { await connection.RequestAsync(operation, new() { ["target"] = id }, new() { Session = session, Payload = payload }, cancellationToken).ConfigureAwait(false); return true; }
        catch (RemoteError) { inputClosed = true; return false; }
    }
}
