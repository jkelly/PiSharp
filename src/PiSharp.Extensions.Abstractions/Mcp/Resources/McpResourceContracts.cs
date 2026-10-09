using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;

namespace PiSharp.Extensions.Mcp.Resources;

/// <summary>Borrowed request through an existing owning runtime. The owner validates the captured
/// server generation, joins the admitted channel original, and never replays reads on disconnect.</summary>
public delegate ValueTask<JsonData> McpResourceRequest(long serverGeneration, string method,
    JsonData? parameters, McpRequestOptions options, CancellationToken cancellationToken);
public sealed record McpResourceServer(string Name, long Generation, double TimeoutMilliseconds, McpResourceRequest Request);
/// <summary>Explicitly admitted output effect. Return only after the physical original joins.
/// The host owns path policy and file lifetime; no ambient file writer is provided.</summary>
public delegate ValueTask<string> McpResourceOutputSaver(ReadOnlyMemory<byte> data, string extension,
    McpInvocationIdentity identity, CancellationToken cancellationToken);
/// <summary>Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/mcp/src/client.ts MAX_LIST_PAGES (1,000 pages) and
/// transports/transport.ts DEFAULT_MAX_MESSAGE_BYTES (16 MiB per response); resources.ts keeps every listed item, and
/// tools.ts MCP_OUTPUT_MAX_BYTES (20 KiB) bounds only the model-facing text.</summary>
public sealed record McpResourceLimits(int MaximumPages = 1000, int MaximumItems = int.MaxValue,
    int MaximumResponseBytes = 16 * 1024 * 1024, int MaximumRetainedBytes = int.MaxValue,
    int MaximumModelTextBytes = 20 * 1024);
/// <summary>Preserves the exact original and its complete Exception aggregate. Faulted OCEs and
/// cancellation without owned original/token provenance remain ordinary callback faults.</summary>
public sealed class McpResourceCallbackException(string callback, Task? original, Exception evidence)
    : IOException($"MCP resource {callback} callback failed: {evidence.Message}", evidence)
{
    public string Callback { get; } = callback;
    public Task? Original { get; } = original;
}
public sealed record McpResourceResult(ImmutableArray<JsonData> Content, JsonData StructuredContent,
    string Server, string Tool, string? FullOutputPath = null)
{
    /// <summary>Native evidence only; never serialized into model/script payloads.</summary>
    public ImmutableArray<McpResourceCallbackException> OriginalCallbackFailures { get; init; } = [];
    public JsonData ToToolResult() => JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(new
    {
        content = Content.Select(block => block.Value), structuredContent = StructuredContent.Value,
        details = FullOutputPath is null ? new Dictionary<string, object?> { ["server"] = Server, ["tool"] = Tool } :
            new Dictionary<string, object?> { ["server"] = Server, ["tool"] = Tool, ["fullOutputPath"] = FullOutputPath }
    }));
}
