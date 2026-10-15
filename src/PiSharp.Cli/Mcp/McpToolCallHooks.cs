// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/extensions/mcp/index.ts (the tool_call handler:
// reachesServer, waitForServers) and packages/coding-agent/src/core/package-manager.ts (the built-in extensions load after the others,
// so the MCP extension's tool_call handler runs after theirs).
using PiSharp.Agent;
using PiSharp.Contracts;

namespace PiSharp.Cli.Mcp;

/// <summary>The MCP extension's tool_call handler as a session-wide pre-execution step. The MCP host is not an extension of the
/// session's main registry, so the profile composes this over the session's prepared tool hooks: every tool call (native, Node and MCP
/// tools, nested codemode calls too) first runs the extensions' tool_call handlers; when they allow it, the bound MCP generation's
/// handler (<see cref="McpSessionRuntimeAdmission.BeforeToolCall"/>) waits for the servers the call may reach, given the input the
/// handlers leave, before the tool is authorized and runs. A blocked call waits for nothing, as the original stops at the first
/// handler that blocks.</summary>
internal sealed class McpToolCallHooks(IPreparedToolHooks? inner, Func<Func<string, JsonData, CancellationToken, Task>?> current) : IPreparedToolHooks
{
    private static readonly ValueTask<PreparedToolCallHookResult> Allowed = ValueTask.FromResult(new PreparedToolCallHookResult());

    public async ValueTask<PreparedToolCallHookResult> BeforeAsync(ToolInvocation invocation, PreparedToolAction validatedAction,
        CancellationToken cancellationToken)
    {
        var result = await (inner?.BeforeAsync(invocation, validatedAction, cancellationToken) ?? Allowed).ConfigureAwait(false);
        if (result is null || result.Block || current() is not { } beforeToolCall) return result!;
        await beforeToolCall(invocation.Call.Name, result.Arguments ?? validatedAction.Arguments, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<JsonData?> AfterAsync(ToolInvocation invocation, PreparedToolAction finalAction, ToolResult result, bool isError,
        CancellationToken cancellationToken) =>
        inner?.AfterAsync(invocation, finalAction, result, isError, cancellationToken) ?? ValueTask.FromResult<JsonData?>(null);
}
