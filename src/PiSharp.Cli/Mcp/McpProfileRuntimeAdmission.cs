using PiSharp.Agent;
using PiSharp.CodingAgent;

namespace PiSharp.Cli.Mcp;

/// <summary>Host admission for an actual profile catalog and policy. The callback owns any
/// acquisition it has not returned; configuration alone supplies no connection authority.</summary>
public delegate ValueTask<McpSessionRuntimeAdmission> McpProfileRuntimeAdmission(
    string cwd, long generation, SessionRuntimeRegistry nativeRegistry,
    IToolActionPolicy exactPolicy, CancellationToken token);
