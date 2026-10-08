using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.Extensions.Runtime.Mcp.Transport;

internal static class McpWireJson
{
    internal static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    internal static void Limits(McpTransportLimits limits)
    {
        if (limits is not { MaximumFrameBytes: > 0, ReadBufferBytes: > 0 and <= 65536, MaximumInflight: > 0,
            MaximumCallbacks: > 0, MaximumRequestId: > 0 and <= 9_007_199_254_740_991 }) throw new ArgumentException("Finite MCP transport limits required.");
    }
    internal static JsonData Json(object value) => JsonData.Parse(JsonSerializer.Serialize(value));
    internal static bool Id(JsonElement value) => value.ValueKind == JsonValueKind.String || value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);
    internal static string Key(JsonElement value) => value.ValueKind == JsonValueKind.String ? "s:" + value.GetString() : "n:" + value.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    internal static JsonData Validate(JsonData message)
    {
        var value = message.Value;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0") throw new McpRuntimeProtocolException("Invalid JSON-RPC message");
        var hasId = value.TryGetProperty("id", out var id);
        if (value.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && (!hasId || Id(id))) return message;
        if (!hasId || !Id(id)) throw new McpRuntimeProtocolException("Invalid JSON-RPC message");
        if (value.TryGetProperty("result", out _))
        { if (value.TryGetProperty("error", out _)) throw new McpRuntimeProtocolException("Invalid JSON-RPC message"); return message; }
        if (value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetDouble(out _) && error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String) return message;
        throw new McpRuntimeProtocolException("Invalid JSON-RPC message");
    }
    internal static byte[] Encode(JsonData message, int maximum, bool newline = false)
    {
        Validate(message); var bytes = Utf8.GetBytes(message.ToString());
        if (bytes.Length > maximum) throw new McpRuntimeProtocolException("MCP frame byte limit exceeded");
        if (!newline) return bytes;
        var framed = new byte[bytes.Length + 1]; bytes.CopyTo(framed, 0); framed[^1] = 10; return framed;
    }
    internal static JsonData Parse(ReadOnlySpan<byte> bytes) => Validate(JsonData.Parse(Utf8.GetString(bytes)));
}

/// <summary>Tracks settlement slots before effects. Close requests owner stop before draining all admitted originals.</summary>
internal sealed class McpOperationSet
{
    private readonly object gate = new(); private readonly HashSet<Task> slots = []; private bool closed;
    internal CancellationTokenSource Lifetime { get; } = new();
    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> run, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested(); var slot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { if (closed) throw new ObjectDisposedException("MCP transport"); slots.Add(slot.Task); }
        try { using var owned = CancellationTokenSource.CreateLinkedTokenSource(caller, Lifetime.Token); return await run(owned.Token).ConfigureAwait(false); }
        finally { lock (gate) slots.Remove(slot.Task); slot.TrySetResult(); }
    }
    internal Task RunAsync(Func<CancellationToken, Task> run, CancellationToken caller = default) => RunAsync(async token => { await run(token).ConfigureAwait(false); return true; }, caller);
    internal Task[] Fence() { lock (gate) { closed = true; return slots.ToArray(); } }
    internal async Task CloseAsync(Func<Task> requestStop, Func<Task>? extra = null)
    {
        var admitted = Fence(); var errors = new List<Exception>();
        Task cancellation = Task.CompletedTask, stop = Task.CompletedTask;
        try { cancellation = Lifetime.CancelAsync(); } catch (Exception error) { errors.Add(error); }
        // Initiate owner stop before awaiting cancellation callbacks that may themselves join a pipe read.
        try { stop = requestStop(); } catch (Exception error) { errors.Add(error); }
        try { await cancellation.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        try { await stop.ConfigureAwait(false); } catch (Exception error) { if (!errors.Any(existing => ReferenceEquals(existing, error))) errors.Add(error); }
        await Task.WhenAll(admitted).ConfigureAwait(false);
        if (extra is not null) try { await extra().ConfigureAwait(false); } catch (Exception error) { if (!errors.Any(existing => ReferenceEquals(existing, error))) errors.Add(error); }
        Lifetime.Dispose();
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
    }
}
