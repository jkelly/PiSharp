// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/extensions/mcp/tools.ts (createMcpResultSchema, relocateSchema, convertMcpResult,
// toModelContent, blockToContent, limitMcpContent, MCP_OUTPUT_MAX_BYTES), packages/mcp/src/protocol/content.ts (toLlmContent) and
// packages/coding-agent/src/core/tools/truncate.ts (truncateMiddle, formatSize).
using PiSharp.Extensions.Runtime.Mcp.Resources;
using System.Text.Encodings.Web;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Cli.Mcp;

/// <summary>An MCP <c>CallToolResult</c> as a tool result: the model gets text and images (resource links name
/// <c>read_mcp_resource</c>, binary resources are saved to files, text over 20 KB keeps its start and end around a
/// <c>…N chars truncated…</c> marker with the full text saved to a file); scripts get the whole result without <c>_meta</c> as
/// <c>structuredContent</c>; <c>isError</c> results are error results.</summary>
internal static class McpToolResults
{
    internal static async Task<JsonData> ConvertAsync(string server, string tool, JsonData result, bool readableResources, McpOutputSaver save, CancellationToken token)
    {
        var value = result.Value;
        var content = await McpModelContent.ConvertAsync(server, result, save, readableResources ? "read_mcp_resource" : null, token).ConfigureAwait(false);
        var isError = value.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        if (isError && string.Join("\n", content.Where(block => block.Value.GetProperty("type").GetString() == "text")
            .Select(block => JsonUtf16.GetString(block.Value.GetProperty("text")))).Length == 0)
            content = content.Add(Owned(Text($"MCP tool {server}/{tool} returned an error")));
        var (limited, fullOutputPath) = await McpContentLimiter.LimitAsync(content, save, token).ConfigureAwait(false);
        var details = new Dictionary<string, object?> { ["server"] = server, ["tool"] = tool };
        if (fullOutputPath is not null) details["fullOutputPath"] = fullOutputPath;
        var scriptResult = value.EnumerateObject().Where(property => property.Name != "_meta").ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
        var output = new Dictionary<string, object?> { ["content"] = limited.Select(block => block.Value), ["details"] = details, ["structuredContent"] = scriptResult };
        if (isError) output["isError"] = true;
        return Owned(output);
    }

    internal static JsonData ResultSchema(JsonElement? schema) => McpResultSchema.Create(schema);

    /// <summary>The result as an owned value: strings keep every UTF-16 code unit JSON.parse read (a lone surrogate is written back as its
    /// escape, as JSON.stringify writes it), and the server's own values are carried as read.</summary>
    private static JsonData Owned(object? value) => JsonData.Parse(JsonUtf16.ToJsonString(Node(value), JavaScriptEncoder.UnsafeRelaxedJsonEscaping));
    private static System.Text.Json.Nodes.JsonNode? Node(object? value) => value switch
    {
        null => null,
        string text => JsonUtf16.StringNode(text),
        bool flag => System.Text.Json.Nodes.JsonValue.Create(flag),
        JsonElement element => JsonUtf16.MutableNode(element),
        Dictionary<string, object?> map => new System.Text.Json.Nodes.JsonObject(map.Select(pair => KeyValuePair.Create(pair.Key, Node(pair.Value)))),
        System.Collections.IEnumerable items => new System.Text.Json.Nodes.JsonArray([.. items.Cast<object?>().Select(Node)]),
        _ => System.Text.Json.Nodes.JsonValue.Create(JsonSerializer.SerializeToElement(value))
    };

    /// <summary>A call that failed, as the original's agent loop reports a tool that threw: an error result with the error's message.
    /// A server that needs a sign-in names how to sign in (runtime.ts signInRequiredMessage).</summary>
    internal static JsonData Failure(PiSharp.Extensions.Mcp.Configuration.McpServerEntry entry, string tool, Exception failure)
    {
        string? message = null;
        for (Exception? current = failure; current is not null && message is null; current = current.InnerException)
            if (current is PiSharp.Extensions.Mcp.Authentication.McpOAuthAuthorizationRequiredException)
                message = PiSharp.Extensions.Runtime.Mcp.Authentication.McpProviderTokenAuthentication.SignInRequiredMessage(entry);
        if (message is null)
        {
            var current = failure;
            while (true)
            {
                if (current is AggregateException { InnerExceptions.Count: 1 } single) current = single.InnerExceptions[0];
                else if (current is PiSharp.Extensions.Mcp.Resources.McpResourceCallbackException { InnerException: { } wrapped }) current = wrapped;
                else break;
            }
            message = current.Message.Length == 0 ? $"MCP tool {entry.Name}/{tool} failed" : current.Message;
        }
        return Owned(new Dictionary<string, object?>
        {
            ["content"] = new[] { Text(message) }, ["details"] = new Dictionary<string, object?> { ["server"] = entry.Name, ["tool"] = tool }, ["isError"] = true
        });
    }

    private static Dictionary<string, object?> Text(string text) => new() { ["type"] = "text", ["text"] = text };
}
