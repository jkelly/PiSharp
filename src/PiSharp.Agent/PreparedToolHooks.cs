using PiSharp.Contracts;

namespace PiSharp.Agent;

/// <summary>A hook can replace input or deny it; allowing a hook never grants action authorization.</summary>
public sealed record PreparedToolCallHookResult(JsonData? Arguments = null, bool Block = false, bool Terminate = false)
{
    /// <summary>The handler's block reason; agent-loop.ts reports it as the blocked call's error text.</summary>
    public string? Reason { get; init; }
}

/// <summary>Trusted composition seam inside the existing invoker. Arguments are already initially validated.
/// Result patches use the existing source after-hook projection, not complete-result replacement.</summary>
public interface IPreparedToolHooks
{
    ValueTask<PreparedToolCallHookResult> BeforeAsync(ToolInvocation invocation, PreparedToolAction validatedAction,
        CancellationToken cancellationToken);
    ValueTask<JsonData?> AfterAsync(ToolInvocation invocation, PreparedToolAction finalAction, ToolResult result,
        bool isError, CancellationToken cancellationToken);
}

/// <summary>Execution disposition is separate from properties of the source-shaped result object.</summary>
public sealed record FinalizedToolExecution(ToolResult Result, bool IsError)
{
    public PiSharp.Contracts.JsonData? NestedCalls { get; init; }
    public JsonData? NestedUsage { get; init; }
    /// <summary>Milliseconds the tool's own execution took (monotonic); null when the tool did not run.</summary>
    public long? DurationMs { get; init; }
}

/// <summary>Optional executor seam; legacy IToolExecutor implementations retain their existing behavior.</summary>
public interface IFinalizedToolExecutor : IToolExecutor
{
    ValueTask<FinalizedToolExecution> ExecuteFinalizedAsync(ToolInvocation invocation, ToolProgressCallback onProgress,
        CancellationToken cancellationToken);
}
