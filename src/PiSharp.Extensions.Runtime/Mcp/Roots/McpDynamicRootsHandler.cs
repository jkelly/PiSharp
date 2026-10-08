using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Roots;
using PiSharp.Extensions.Runtime.Mcp.Transport;

namespace PiSharp.Extensions.Runtime.Mcp.Roots;

/// <summary>Dynamic roots metadata invocation with admitted callback lifetime ownership.
/// Uses the existing MCP operation owner to stop admission and join all admitted callback work.
/// This handler acquires no channel, transport, process, credentials or filesystem capability.</summary>
public sealed class McpDynamicRootsHandler : IMcpDynamicRootsHandler
{
    private sealed class CallbackFrame { internal bool Active = true; }
    private readonly McpAdmittedDynamicRootsProvider provider;
    private readonly McpOperationSet operations = new();
    private readonly AsyncLocal<CallbackFrame?> frame = new();
    private readonly object gate = new();
    private readonly List<McpRootsCallbackException> failures = [];
    private readonly Lazy<Task> close;
    private readonly int maximumResponseBytes;
    private readonly int maximumRequests;
    private int admittedRequests;
    private bool closed;

    public McpDynamicRootsHandler(McpAdmittedDynamicRootsProvider provider,
        int maximumResponseBytes = 1_048_576, int maximumRequests = 4096)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.GetInvocationList().Length != 1)
            throw new ArgumentException("One admitted dynamic roots provider is required.", nameof(provider));
        if (maximumResponseBytes < 12)
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        if (maximumRequests < 1) throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        this.provider = provider;
        this.maximumResponseBytes = maximumResponseBytes;
        this.maximumRequests = maximumRequests;
        close = new(CloseCoreAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<JsonData> HandleRootsListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(McpDynamicRootsHandler));
            if (admittedRequests >= maximumRequests)
                throw new InvalidOperationException("MCP dynamic roots request bound reached.");
            admittedRequests++;
        }
        return operations.RunAsync(InvokeAsync, cancellationToken);
    }

    public Task CloseAsync()
    {
        if (frame.Value is { Active: true })
            throw new InvalidOperationException("A dynamic roots callback cannot join its own handler close.");
        return close.Value;
    }

    private async Task<JsonData> InvokeAsync(CancellationToken token)
    {
        var borrowed = new CallbackFrame();
        frame.Value = borrowed;
        Task<JsonData>? original = null;
        JsonData roots;
        try
        {
            original = provider(token).AsTask(); // Capture a single-use ValueTask exactly once.
            roots = await original.ConfigureAwait(false); // Never cancel an await while abandoning its original.
        }
        catch (OperationCanceledException error) when (original is { IsCanceled: true } &&
            token.IsCancellationRequested && error.CancellationToken == token)
        {
            throw new McpRootsCallbackCancellation(original, error);
        }
        catch (Exception error)
        {
            var evidence = original is { IsFaulted: true } ? original.Exception! : error;
            var failure = new McpRootsCallbackException(original, evidence);
            lock (gate) failures.Add(failure);
            throw failure;
        }
        finally { borrowed.Active = false; frame.Value = null; }
        if (roots is null || roots.Value.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("The admitted roots provider must return a roots array.");
        var response = "{\"roots\":" + roots.ToString() + "}";
        if (Encoding.UTF8.GetByteCount(response) > maximumResponseBytes)
            throw new InvalidOperationException("MCP dynamic roots response byte bound reached.");
        return JsonData.Parse(response);
    }

    private Task ThrowCallbackFailures()
    {
        McpRootsCallbackException[] captured;
        lock (gate) captured = failures.ToArray();
        if (captured.Length == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured[0]).Throw();
        if (captured.Length > 1) throw new AggregateException(captured);
        return Task.CompletedTask;
    }

    private Task CloseCoreAsync()
    {
        lock (gate) closed = true;
        return operations.CloseAsync(() => Task.CompletedTask, ThrowCallbackFailures);
    }
}
