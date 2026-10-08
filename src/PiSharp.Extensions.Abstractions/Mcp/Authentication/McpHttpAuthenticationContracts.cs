namespace PiSharp.Extensions.Mcp.Authentication;

/// <summary>Explicit caller-supplied token work. No storage, lookup or OAuth grant is inferred.</summary>
public delegate ValueTask<string?> McpAdmittedHttpTokenProvider(CancellationToken cancellationToken);
public delegate ValueTask McpAdmittedHttpUnauthorizedHandler(McpHttpUnauthorizedContext context, CancellationToken cancellationToken);

/// <summary>Borrowed first rejected response; its owning HTTP operation discards it after this callback
/// settles. Any discovery/exchange dependencies must be separately admitted by the caller.</summary>
public sealed record McpHttpUnauthorizedContext(HttpResponseMessage Response, Uri ServerUrl, string? RejectedToken)
{
    public override string ToString() => nameof(McpHttpUnauthorizedContext);
}
public sealed record McpAdmittedHttpAuthentication(McpAdmittedHttpTokenProvider Token,
    McpAdmittedHttpUnauthorizedHandler? OnUnauthorized = null);

/// <summary>Actual callback/content/send/cleanup original and complete unflattened failure evidence.
/// Faulted OCE originals remain faults; synchronous failure has a null original.</summary>
public sealed class McpHttpAuthenticationOriginalException(string phase, Task? original, Exception evidence, Exception? direct = null)
    : IOException("Admitted MCP HTTP authentication " + phase + " failed.", evidence)
{
    public string Phase { get; } = phase;
    public Task? Original { get; } = original;
    public Exception Evidence { get; } = evidence;
    public Exception Direct { get; } = direct ?? evidence;
}
