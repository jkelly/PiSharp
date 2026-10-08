using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

internal static class McpOAuthAdmittedWork
{
    internal static async Task<T> Invoke<T>(string phase, Func<ValueTask<T>> invoke, CancellationToken token)
    {
        Task<T>? original = null;
        try { original = invoke().AsTask(); return await original.ConfigureAwait(false); }
        catch (OperationCanceledException direct) when (original is { IsCanceled: true } && token.IsCancellationRequested && direct.CancellationToken == token)
        { throw new McpOAuthFlowCanceledException(original, direct); }
        catch (Exception direct) { throw new McpOAuthFlowOriginalException(phase, original, original is { IsFaulted: true } ? original.Exception! : direct, direct); }
    }
    internal static async Task Invoke(string phase, Func<ValueTask> invoke, CancellationToken token)
    {
        Task? original = null;
        try { original = invoke().AsTask(); await original.ConfigureAwait(false); }
        catch (OperationCanceledException direct) when (original is { IsCanceled: true } && token.IsCancellationRequested && direct.CancellationToken == token)
        { throw new McpOAuthFlowCanceledException(original, direct); }
        catch (Exception direct) { throw new McpOAuthFlowOriginalException(phase, original, original is { IsFaulted: true } ? original.Exception! : direct, direct); }
    }
}