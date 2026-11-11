// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/mcp/src/transports/pipe.ts.
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.Extensions.Runtime.Mcp.Transport;

/// <summary>MCP JSONL over a caller-opened pipe, including a remote process. No process acquisition.</summary>
public sealed class McpPipeTransport : IMcpWireTransport
{
    private readonly McpPipeOptions options;
    private readonly McpJsonRpcLineReader lines;
    private readonly McpByteTail stderr;
    private readonly object gate = new();
    private readonly List<Exception> callbackFailures = [];
    private McpWireCallbacks? callbacks;
    private IMcpPipe? pipe;
    private Task? opening, closing, exit;
    private Task delivery = Task.CompletedTask;
    private bool started, closed, ended;

    public McpPipeTransport(McpPipeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Open is null || options.MaximumMessageBytes <= 0 || options.MaximumStderrBytes < 0 ||
            options.CloseTimeoutMilliseconds < 0) throw new ArgumentException("Invalid MCP pipe options.", nameof(options));
        this.options = options; lines = new(options.MaximumMessageBytes); stderr = new(options.MaximumStderrBytes);
    }

    /// <summary>The last MaximumStderrBytes of the program's stderr, decoded as UTF-8.</summary>
    public string Stderr { get { lock (gate) return McpWireJson.Utf8.GetString(stderr.ToArray()); } }

    public Task StartAsync(McpWireCallbacks receiver, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiver); cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closed) throw new McpRuntimeDisconnectedException("MCP pipe closed");
            if (started) throw new InvalidOperationException("MCP pipe already started");
            started = true; callbacks = receiver;
            return opening = Task.Run(() => StartCoreAsync(cancellationToken));
        }
    }

    private async Task StartCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Open has no cancellation escape hatch: retain and close a late pipe instead of abandoning it.
        var opened = await options.Open(new(ReadStdout, ReadStderr)).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MCP pipe opener returned no pipe");
        bool stop;
        lock (gate)
        {
            pipe = opened; exit = ObserveExitAsync(opened);
            stop = closed || token.IsCancellationRequested;
        }
        if (stop)
        {
            try { opened.Close(); }
            finally { await Observe(opened.Exited).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested();
            throw new McpRuntimeDisconnectedException("MCP pipe closed while opening");
        }
    }

    private void ReadStdout(ReadOnlyMemory<byte> bytes)
    {
        lock (gate)
        {
            if (ended) return;
            lines.Read(bytes.Span, message => Queue(() => callbacks!.Receive(message)), Report);
        }
    }
    private void ReadStderr(ReadOnlyMemory<byte> bytes)
    {
        lock (gate)
        {
            if (ended) return;
            stderr.Append(bytes.Span);
            // Decode now: the caller is permitted to reuse the buffer immediately.
            var text = McpWireJson.Utf8.GetString(bytes.Span);
            if (options.OnStderr is not null) Queue(() => { options.OnStderr(text); return ValueTask.CompletedTask; });
        }
    }
    private void Report(Exception error) => Queue(() => { callbacks!.Error?.Invoke(error); return ValueTask.CompletedTask; });
    // Output can arrive during Open, before Start returns. Preserve order and join every async receiver at close.
    private void Queue(Func<ValueTask> action)
    {
        var previous = delivery;
        delivery = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try { await action().ConfigureAwait(false); }
            catch (Exception error)
            {
                try { callbacks!.Error?.Invoke(error); }
                catch (Exception callbackError) { lock (gate) callbackFailures.Add(callbackError); }
            }
        });
    }
    private async Task ObserveExitAsync(IMcpPipe observed)
    {
        Exception? failure = null;
        try { await observed.Exited.ConfigureAwait(false); } catch (Exception error) { failure = error; }
        lock (gate)
        {
            ended = true;
            if (failure is not null && !closed) Report(failure);
            lines.End(Report);
            Queue(() => { callbacks!.Disconnected(new McpRuntimeDisconnectedException("MCP pipe EOF")); return ValueTask.CompletedTask; });
        }
    }

    public Task SendAsync(JsonData message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = McpWireJson.Encode(message, options.MaximumMessageBytes, newline: true);
        lock (gate)
        {
            if (pipe is null || closed || ended || pipe.Exited.IsCompleted)
                throw new McpRuntimeDisconnectedException("MCP pipe not started, closed or ended");
            pipe.Write(bytes);
        }
        return Task.CompletedTask;
    }
    public Task SetProtocolVersionAsync(string version, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    public Task CloseAsync()
    {
        lock (gate) { closed = true; return closing ??= Task.Run(CloseCoreAsync); }
    }
    private async Task CloseCoreAsync()
    {
        Task? start; lock (gate) start = opening;
        if (start is not null) await Observe(start).ConfigureAwait(false);
        IMcpPipe? current; Task? observed; lock (gate) { current = pipe; observed = exit; }
        var failures = new List<Exception>();
        if (current is not null && !current.Exited.IsCompleted)
        {
            try { current.CloseStdin(); } catch (Exception error) { failures.Add(error); }
            using var timer = new CancellationTokenSource();
            var grace = Task.Delay(options.CloseTimeoutMilliseconds, timer.Token);
            if (await Task.WhenAny(current.Exited, grace).ConfigureAwait(false) != current.Exited)
                try { current.Close(); } catch (Exception error) { failures.Add(error); }
            await timer.CancelAsync().ConfigureAwait(false);
            await Observe(current.Exited).ConfigureAwait(false);
        }
        if (observed is not null) await observed.ConfigureAwait(false);
        Task pending; lock (gate) pending = delivery;
        await pending.ConfigureAwait(false);
        lock (gate) failures.AddRange(callbackFailures);
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }
    private static async Task Observe(Task task) { try { await task.ConfigureAwait(false); } catch { /* Original caller owns start/exit failure. */ } }
}
