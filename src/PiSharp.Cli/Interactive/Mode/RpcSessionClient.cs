// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/rpc/rpc-client.ts (request/response correlation over the
// RPC JSONL protocol, event subscription). The interactive mode drives its session through the in-process RPC host.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>A failed RPC command: <c>success: false</c> with the host's error text.</summary>
internal sealed class RpcCommandFailedException(string command, string error) : Exception(error)
{
    public string Command { get; } = command;
}

/// <summary>Correlates RPC commands with their responses; every other record is an event.</summary>
internal sealed class RpcSessionClient
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> pending = new(StringComparer.Ordinal);
    private Func<JsonData, CancellationToken, Task>? send;
    private long nextId;
    private volatile bool closed;

    /// <summary>Raised for every non-response record (agent and session events, extension UI requests).</summary>
    public event Action<JsonObject>? Event;

    public void Bind(Func<JsonData, CancellationToken, Task> sender) => send = sender;

    /// <summary>Observer for the connection's output records.</summary>
    public ValueTask ObserveAsync(JsonData record, CancellationToken token)
    {
        JsonObject? body;
        try { body = JsonNode.Parse(record.ToString()) as JsonObject; }
        catch (JsonException) { return ValueTask.CompletedTask; }
        if (body is null) return ValueTask.CompletedTask;
        if (SessionEntries.Str(body["type"]) == "response" && SessionEntries.Str(body["id"]) is { } id && pending.TryRemove(id, out var waiter))
        {
            waiter.TrySetResult(body);
            return ValueTask.CompletedTask;
        }
        Event?.Invoke(body);
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends a command and returns its <c>data</c> (or null); throws <see cref="RpcCommandFailedException"/> on failure.</summary>
    public async Task<JsonNode?> RequestAsync(JsonObject command, CancellationToken token = default)
    {
        var response = await RequestRawAsync(command, token).ConfigureAwait(false);
        if (response["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && ok) return response["data"];
        throw new RpcCommandFailedException(SessionEntries.Str(command["type"]) ?? "", SessionEntries.Str(response["error"]) ?? "Unknown error");
    }

    public async Task<JsonObject> RequestRawAsync(JsonObject command, CancellationToken token = default)
    {
        if (closed || send is null) throw new InvalidOperationException("The session is closed.");
        var id = "pi-i-" + Interlocked.Increment(ref nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        command["id"] = id;
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;
        try
        {
            await send(JsonData.Parse(command.ToJsonString()), token).ConfigureAwait(false);
            using var registration = token.Register(() => waiter.TrySetCanceled(token));
            return await waiter.Task.ConfigureAwait(false);
        }
        finally { pending.TryRemove(id, out _); }
    }

    /// <summary>Sends a command without waiting for its response (fire-and-forget, like an unawaited promise).</summary>
    public void Post(JsonObject command, Action<Exception>? onError = null)
    {
        _ = Task.Run(async () =>
        {
            try { await RequestAsync(command).ConfigureAwait(false); }
            catch (Exception error) { onError?.Invoke(error); }
        });
    }

    public void Close()
    {
        closed = true;
        foreach (var (_, waiter) in pending) waiter.TrySetException(new InvalidOperationException("The session is closed."));
        pending.Clear();
    }
}
