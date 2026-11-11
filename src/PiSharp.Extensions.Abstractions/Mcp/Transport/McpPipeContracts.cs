// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/mcp/src/transports/pipe.ts.
namespace PiSharp.Extensions.Mcp.Transport;

/// <summary>A caller-opened program's stdio. The pipe owns the program and its descendants.</summary>
public interface IMcpPipe
{
    void Write(ReadOnlyMemory<byte> bytes);
    void CloseStdin();
    /// <summary>Stop the program and its descendants; Exited must subsequently settle.</summary>
    void Close();
    /// <summary>Settles after output callbacks have finished, including after Close.</summary>
    Task Exited { get; }
}

/// <summary>Output callbacks are active during Open. Buffers may be reused after a callback returns.</summary>
public sealed record McpPipeHandlers(Action<ReadOnlyMemory<byte>> OnStdout, Action<ReadOnlyMemory<byte>> OnStderr);

/// <summary>Explicit caller-owned pipe admission; does not acquire a process or environment.</summary>
public sealed record McpPipeOptions(Func<McpPipeHandlers, Task<IMcpPipe>> Open,
    int MaximumMessageBytes = 16 * 1024 * 1024, int MaximumStderrBytes = 64 * 1024,
    Action<string>? OnStderr = null, int CloseTimeoutMilliseconds = 2000);
