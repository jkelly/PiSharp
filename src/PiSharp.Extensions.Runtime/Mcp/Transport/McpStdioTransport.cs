using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.Extensions.Runtime.Mcp.Transport;

/// <summary>Actual MCP JSONL over an explicitly admitted duplex lease. No process acquisition or stream disposal.</summary>
public sealed class McpStdioTransport : IMcpWireTransport
{
    private readonly IMcpAdmittedDuplexLease lease; private readonly McpTransportLimits limits;
    private readonly McpOperationSet operations = new(); private readonly SemaphoreSlim write = new(1, 1);
    private readonly object gate = new(); private readonly Lazy<Task> close; private Task? reader; private bool started, closed;
    public McpStdioTransport(IMcpAdmittedDuplexLease lease, McpTransportLimits? limits = null)
    { this.lease = lease ?? throw new ArgumentNullException(nameof(lease)); this.limits = limits ?? new(); McpWireJson.Limits(this.limits); close = new(() => operations.CloseAsync(lease.CloseAsync, JoinReader), LazyThreadSafetyMode.ExecutionAndPublication); }
    public Task StartAsync(McpWireCallbacks callbacks, CancellationToken token) => operations.RunAsync(async owned =>
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        lock (gate) { if (started) throw new InvalidOperationException("MCP stdio already started"); started = true; }
        await lease.StartAsync(owned).ConfigureAwait(false); owned.ThrowIfCancellationRequested();
        if (!lease.Input.CanWrite || !lease.Output.CanRead) throw new ArgumentException("Admitted MCP duplex stream capabilities differ");
        lock (gate) { owned.ThrowIfCancellationRequested(); if (closed) throw new OperationCanceledException(owned); reader = operations.RunAsync(lifetime => ReadAsync(callbacks, lifetime)); }
    }, token);
    public Task SetProtocolVersionAsync(string version, CancellationToken token) => operations.RunAsync(_ => Task.CompletedTask, token);
    public Task SendAsync(JsonData message, CancellationToken token)
    {
        var bytes = McpWireJson.Encode(message, limits.MaximumFrameBytes, newline: true);
        return operations.RunAsync(async owned =>
        {
            lock (gate) if (!started || closed) throw new McpRuntimeDisconnectedException("MCP stdio not started or closed");
            await write.WaitAsync(owned).ConfigureAwait(false);
            try { owned.ThrowIfCancellationRequested(); await lease.Input.WriteAsync(bytes, owned).ConfigureAwait(false); await lease.Input.FlushAsync(owned).ConfigureAwait(false); owned.ThrowIfCancellationRequested(); }
            finally { write.Release(); }
        }, token);
    }
    private async Task ReadAsync(McpWireCallbacks callbacks, CancellationToken token)
    {
        try
        {
            var chunk = new byte[limits.ReadBufferBytes]; using var line = new MemoryStream();
            while (true)
            {
                var count = await lease.Output.ReadAsync(chunk, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                if (count == 0)
                {
                    if (!string.IsNullOrWhiteSpace(McpWireJson.Utf8.GetString(line.GetBuffer(), 0, (int)line.Length))) throw new McpRuntimeProtocolException("MCP stdio incomplete final message");
                    callbacks.Disconnected(new McpRuntimeDisconnectedException("MCP stdio EOF")); return;
                }
                for (var index = 0; index < count; index++)
                {
                    if (chunk[index] == 10)
                    {
                        var bytes = line.GetBuffer(); var length = (int)line.Length;
                        if (length > 0 && bytes[length - 1] == 13) length--;
                        if (!string.IsNullOrWhiteSpace(McpWireJson.Utf8.GetString(bytes, 0, length))) await callbacks.Receive(McpWireJson.Parse(bytes.AsSpan(0, length))).ConfigureAwait(false);
                        line.SetLength(0);
                    }
                    else { if (line.Length >= limits.MaximumFrameBytes) throw new McpRuntimeProtocolException("MCP stdio frame byte limit exceeded"); line.WriteByte(chunk[index]); }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { callbacks.Disconnected(error); throw; }
    }
    private async Task JoinReader() { Task? original; lock (gate) original = reader; try { if (original is not null) await original.ConfigureAwait(false); } finally { write.Dispose(); } }
    public Task CloseAsync() { lock (gate) closed = true; return close.Value; }
}
