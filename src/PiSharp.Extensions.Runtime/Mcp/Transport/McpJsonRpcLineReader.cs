// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/mcp/src/transports/lines.ts.
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;

namespace PiSharp.Extensions.Runtime.Mcp.Transport;

/// <summary>Single-pass JSONL framing. Only unfinished frames are copied and retained.</summary>
internal sealed class McpJsonRpcLineReader(int maximumBytes)
{
    private readonly MemoryStream pending = new();
    private bool discarding;

    internal void Read(ReadOnlySpan<byte> bytes, Action<JsonData> message, Action<Exception> error)
    {
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)10);
            var part = newline < 0 ? bytes : bytes[..newline];
            if (!discarding)
            {
                if (pending.Length + part.Length > maximumBytes)
                {
                    pending.SetLength(0); discarding = true;
                    error(new McpRuntimeProtocolException("MCP stdio frame byte limit exceeded"));
                }
                else if (newline < 0 || pending.Length > 0)
                {
                    pending.Write(part);
                    if (newline >= 0) Dispatch(pending.GetBuffer().AsSpan(0, (int)pending.Length), message, error);
                }
                else Dispatch(part, message, error);
            }
            if (newline < 0) return;
            pending.SetLength(0); discarding = false; bytes = bytes[(newline + 1)..];
        }
    }

    private static void Dispatch(ReadOnlySpan<byte> bytes, Action<JsonData> message, Action<Exception> error)
    {
        if (bytes.Length > 0 && bytes[^1] == 13) bytes = bytes[..^1];
        if (string.IsNullOrWhiteSpace(McpWireJson.Utf8.GetString(bytes))) return;
        JsonData parsed;
        try { parsed = McpWireJson.Parse(bytes); }
        catch (Exception failure) { error(failure); return; }
        message(parsed);
    }

    internal void End(Action<Exception> error)
    {
        var incomplete = !discarding && !string.IsNullOrWhiteSpace(McpWireJson.Utf8.GetString(pending.GetBuffer(), 0, (int)pending.Length));
        pending.SetLength(0); discarding = false;
        if (incomplete) error(new McpRuntimeProtocolException("MCP stdio incomplete final message"));
    }
}

/// <summary>Fixed-capacity tail; appending and copying remain bounded independently of total output.</summary>
internal sealed class McpByteTail(int capacity)
{
    private readonly byte[] bytes = new byte[capacity];
    private int start, count;
    internal void Append(ReadOnlySpan<byte> value)
    {
        if (capacity == 0) return;
        if (value.Length >= capacity) { value[^capacity..].CopyTo(bytes); start = 0; count = capacity; return; }
        var overflow = Math.Max(0, count + value.Length - capacity);
        start = (start + overflow) % capacity; count -= overflow;
        var end = (start + count) % capacity; var first = Math.Min(value.Length, capacity - end);
        value[..first].CopyTo(bytes.AsSpan(end)); value[first..].CopyTo(bytes); count += value.Length;
    }
    internal byte[] ToArray()
    {
        var result = new byte[count]; var first = Math.Min(count, capacity - start);
        bytes.AsSpan(start, first).CopyTo(result); bytes.AsSpan(0, count - first).CopyTo(result.AsSpan(first));
        return result;
    }
}
