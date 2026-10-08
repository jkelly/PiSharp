// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (_afterToolCall: tool_result
// handlers, then normalizeToolResultImages) and packages/coding-agent/src/utils/tool-result-images.ts.
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools.Images;

namespace PiSharp.Cli.Extensions;

/// <summary>After the extension tool_result hook (when any), every image block of the tool's final content runs through the
/// image pipeline (decode, resize to the model's limits, re-encode) as Pi does, so images that extensions or MCP tools return
/// are normalized too. Returns null (no patch) when neither the hook nor normalization changed the result.</summary>
internal sealed class ImageNormalizingToolHooks(IPreparedToolHooks? inner, Func<bool> autoResizeImages, IImageCodec? codec = null) : IPreparedToolHooks
{
    public ValueTask<PreparedToolCallHookResult> BeforeAsync(ToolInvocation invocation, PreparedToolAction validatedAction, CancellationToken cancellationToken) =>
        inner?.BeforeAsync(invocation, validatedAction, cancellationToken) ?? ValueTask.FromResult(new PreparedToolCallHookResult());

    public async ValueTask<JsonData?> AfterAsync(ToolInvocation invocation, PreparedToolAction finalAction, ToolResult result, bool isError,
        CancellationToken cancellationToken)
    {
        var patch = inner is null ? null : await inner.AfterAsync(invocation, finalAction, result, isError, cancellationToken).ConfigureAwait(false);
        if (patch is { Value.ValueKind: System.Text.Json.JsonValueKind.Null }) patch = null;
        var patched = patch is not null && patch.Value.TryGetProperty("content", out var replaced) && replaced.ValueKind != System.Text.Json.JsonValueKind.Null
            ? JsonData.FromElement(replaced) : null;
        var content = patched ?? result.ContentValue;
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ImageProcessor.NormalizeToolResultImages(content, autoResizeImages(), codec: codec);
        if (ReferenceEquals(normalized, content)) return patch;
        // The normalized content replaces the result's content; structured content that a hook did not replace is retained.
        var node = patch is null ? new JsonObject() : JsonNode.Parse(patch.Value.GetRawText())!.AsObject();
        node["content"] = JsonNode.Parse(normalized.Value.GetRawText());
        if (patch is null && result.StructuredContent is { } structured) node["structuredContent"] = JsonNode.Parse(structured.Value.GetRawText());
        return JsonData.Parse(node.ToJsonString());
    }
}
