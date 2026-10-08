using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Mcp.Configuration;
namespace PiSharp.Cli.Mcp;
/// <summary>Explicit profile host acquisition with exact native registry, policy and committed MCP catalog.
/// Configuration alone never grants connection/resource acquisition authority.</summary>
public delegate ValueTask<McpSessionRuntimeAdmission> McpRegisteredProfileRuntimeAdmission(
    string cwd, long generation, SessionRuntimeRegistry nativeRegistry,
    IToolActionPolicy exactPolicy, McpServerCatalog committedCatalog, CancellationToken token);