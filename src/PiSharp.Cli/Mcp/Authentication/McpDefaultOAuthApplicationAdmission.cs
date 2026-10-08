using System.Collections.Immutable;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.Cli.Mcp.Authentication;

/// <summary>Explicit application-generation OAuth/HTTP capability; factories are inert until
/// actual channel acquisition. Configuration labels authority and never creates it.</summary>
public sealed record McpDefaultOAuthApplicationAdmission(string Name,
    Action<McpServerEntry> ValidateConfiguration,
    Func<McpApplicationGenerationRequest, McpServerEntry, CancellationToken, ValueTask<McpDefaultOAuthHostResources>> AcquireResources,
    Func<McpServerEntry, long, McpAdmittedHttpRequestFactory, McpAdmittedChannelFactory> CreateHttpChannel);

public sealed class McpDefaultOAuthApplicationFailure(string phase,
    ImmutableArray<McpDefaultOAuthHostOriginal> originals, Exception evidence)
    : IOException("Default OAuth application " + phase + " failed.", evidence)
{
    public ImmutableArray<McpDefaultOAuthHostOriginal> Originals { get; } = originals;
}
