using System.Collections.Immutable;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Configuration;

public enum McpExposure { Codemode, CodemodeDeferred, Deferred, Direct, Hidden }
public enum McpTransportKind { Stdio, Http }
public enum McpConfigurationScope { Global, Project, Extension }

/// <summary>Validated inert values. Raw JSON preserves optional and unknown fields; it grants no connection authority.</summary>
public sealed class McpServerConfiguration
{
    public JsonData Raw { get; }
    public McpTransportKind Transport { get; }
    public McpExposure Exposure { get; }
    public bool Enabled { get; }
    public double TimeoutSeconds { get; }
    public ImmutableArray<KeyValuePair<string, McpExposure>> ToolExposure { get; }

    internal McpServerConfiguration(JsonData raw, McpTransportKind transport, McpExposure exposure,
        bool enabled, double timeoutSeconds, ImmutableArray<KeyValuePair<string, McpExposure>> toolExposure)
    { Raw = raw; Transport = transport; Exposure = exposure; Enabled = enabled; TimeoutSeconds = timeoutSeconds; ToolExposure = toolExposure; }
}

/// <summary>Only caller-supplied text is parsed. A null document represents an absent file.</summary>
public sealed record McpConfigurationDocument(string Source, string Text);
public sealed record McpServerEntry(string Name, McpServerConfiguration Config, string Source, McpConfigurationScope Scope);
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
