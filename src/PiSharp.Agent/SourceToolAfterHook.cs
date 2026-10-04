using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Agent;

/// <summary>Source afterToolCall returns a patch; existing IToolHooks return a complete replacement.</summary>
public interface ISourceToolHooks : IToolHooks
{
    ValueTask<JsonData?> AfterToolCallAsync(ToolInvocation invocation, ToolResult result, bool isError, CancellationToken cancellationToken);
    ValueTask<ToolResult> IToolHooks.AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken cancellationToken) =>
        ValueTask.FromResult(result);
}

public static class SourceToolAfterHook
{
    /// <summary>Known nullish patch fields retain their original values. Extra patch fields are not copied.</summary>
    public static ToolOutcome Apply(ToolOutcome original, JsonData? patch, ToolResultValueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ToolResultValueCodec.Validate(original.Result, options);
        if (patch is null || patch.Value.ValueKind == JsonValueKind.Null) return original;
        // Admission includes ignored patch properties: they still consume retained input resources.
        var admitted = ToolResult.FromJson(patch, options);
        var result = original.Result;
        foreach (var name in new[] { "content", "details", "usage", "terminate" })
            if (admitted.Property(name) is { Value.ValueKind: not JsonValueKind.Null } value)
                result = result.WithProperty(name, value);
        var structured = admitted.StructuredContent;
        if (structured is not null && structured.Value.ValueKind != JsonValueKind.Null)
            result = result with { StructuredContent = structured };
        else if (admitted.Property("content") is { Value.ValueKind: not JsonValueKind.Null })
            result = result with { StructuredContent = null };
        var isError = original.IsError;
        if (admitted.Property("isError") is { Value.ValueKind: not JsonValueKind.Null } error)
        {
            if (error.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("Source after-hook error disposition must be a boolean.");
            isError = error.Value.GetBoolean();
        }
        ToolResultValueCodec.Validate(result, options);
        return original with { Result = result, IsError = isError };
    }
}
