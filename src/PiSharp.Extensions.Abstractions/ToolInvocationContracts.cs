using PiSharp.Contracts;

namespace PiSharp.Extensions;

/// <summary>Optional actual-invocation capability. Legacy tool contexts do not invent an invocation identity.</summary>
public interface IExtensionToolInvocationContext : IExtensionToolContext
{
    string ToolCallId { get; }
    string? ParentToolCallId => null;
    long SessionGeneration => 0;
    int CallDepth => 0;
    /// <summary>Native awaited delivery. Only one update may be pending; admitted delivery joins callback settlement.</summary>
    ValueTask ReportUpdateAsync(JsonData partialResult, CancellationToken cancellationToken = default);
}

/// <summary>One host-owned update sink. Its returned receipt must cover actual delivery, not queued admission alone.</summary>
public delegate ValueTask ExtensionToolUpdateCallback(JsonData partialResult, CancellationToken cancellationToken);

public sealed record ExtensionExecuteToolOptions(CancellationToken CancellationToken = default,
    ExtensionToolUpdateCallback? OnUpdate = null);

/// <summary>Owned native outcome. A missing ID means the host did not admit a nested invocation.</summary>
public sealed record ExtensionToolCallOutcome(string? ToolCallId, string ToolName, JsonData Result,
    bool IsError, string? ParentToolCallId = null)
{
    public static ExtensionToolCallOutcome Unavailable(string name) => new(null, name,
        JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"Nested tool invocation is unavailable or inactive.\"}],\"isError\":true}"), true);
}

/// <summary>Trusted host bridge only. It must execute through the prepared tool pipeline, never invoke callbacks directly.</summary>
public sealed record ExtensionToolBroker(System.Collections.Immutable.ImmutableArray<string> Tools,
    long SessionGeneration, string? ParentToolCallId, int CallDepth,
    ExtensionNestedToolCallback ExecuteToolAsync);
public delegate ValueTask<ExtensionToolCallOutcome> ExtensionNestedToolCallback(string name, JsonData arguments,
    ExtensionExecuteToolOptions options);
