// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/mcp-servers.ts and packages/coding-agent/src/extensions/mcp/config.ts.
using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Configuration;

/// <summary>`codemode-deferred` is accepted in configs as an alias of <see cref="Codemode"/> and resolved on validation.</summary>
public enum McpExposure { Codemode, Deferred, Direct, Hidden }
public enum McpTransportKind { Stdio, Http }
public enum McpConfigurationScope { Global, Project, Extension }

/// <summary>Validated inert values. Raw JSON preserves optional and unknown fields with exposure aliases resolved;
/// it grants no connection authority.</summary>
public sealed class McpServerConfiguration
{
    public JsonData Raw { get; }
    public McpTransportKind Transport { get; }
    public McpExposure Exposure { get; }
    public bool Enabled { get; }
    public double TimeoutSeconds { get; }
    public ImmutableArray<KeyValuePair<string, McpExposure>> ToolExposure { get; }
    /// <summary>What the server offers, as configured. Untrimmed; consumers trim.</summary>
    public string? Description { get; }
    /// <summary>`auth.provider` of an HTTP server: send that provider's login token instead of using OAuth.</summary>
    public string? AuthProvider { get; }

    internal McpServerConfiguration(JsonData raw, McpTransportKind transport, McpExposure exposure,
        bool enabled, double timeoutSeconds, ImmutableArray<KeyValuePair<string, McpExposure>> toolExposure,
        string? description = null, string? authProvider = null)
    {
        Raw = raw; Transport = transport; Exposure = exposure; Enabled = enabled; TimeoutSeconds = timeoutSeconds; ToolExposure = toolExposure;
        Description = description; AuthProvider = authProvider;
    }
}

/// <summary>Only caller-supplied text is parsed. A null document represents an absent file.</summary>
public sealed record McpConfigurationDocument(string Source, string Text);
/// <summary><paramref name="Override"/> names the project `mcp.json` that overrides `enabled`, `exposure` or
/// `toolExposure` of this global server; scope and source stay those of the global definition.</summary>
public sealed record McpServerEntry(string Name, McpServerConfiguration Config, string Source, McpConfigurationScope Scope,
    string? Override = null);
public sealed record McpRegisteredServer(string Name, McpServerConfiguration Config, string ExtensionPath);
public sealed record McpLoadedConfiguration(ImmutableArray<McpServerEntry> Servers, bool? AutoEnableCodemode,
    ImmutableArray<string> Errors)
{
    public bool EffectiveAutoEnableCodemode => AutoEnableCodemode ?? true;
}
public sealed record McpServerCatalog(ImmutableArray<McpServerEntry> Servers, ImmutableArray<string> Overridden);
public sealed record McpConfigurationValidation(McpServerConfiguration? Config, string? Error)
{
    public bool IsValid => Config is not null;
}
