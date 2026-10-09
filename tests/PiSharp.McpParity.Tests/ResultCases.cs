using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;

// Upstream: extensions/mcp/tools.ts (convertMcpResult, blockToContent, limitMcpContent), packages/mcp/src/protocol/content.ts
// (toLlmContent) and core/tools/truncate.ts (truncateMiddle, formatSize); test/mcp-extension.test.ts ("converts results, passing the
// CallToolResult to scripts and flagging errors", "points resource links to read_mcp_resource and saves binary resources", "cuts the
// middle of model-facing text over 20KB and keeps the full result for scripts").
internal static partial class Program
{
    /// <summary>A server whose tool answers with the CallToolResult named by its `case` argument.</summary>
    private sealed class ResultServer : IMcpAdmittedRequestChannel
    {
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token) => ValueTask.FromResult(JsonData.Parse(method switch
        {
            "initialize" => """{"protocolVersion":"2025-11-25","serverInfo":{"name":"docs","version":"1"},"capabilities":{"tools":{},"resources":{}}}""",
            "tools/list" => """{"tools":[{"name":"get","inputSchema":{"type":"object"}}]}""",
            "resources/list" => """{"resources":[]}""",
            "resources/templates/list" => """{"resourceTemplates":[]}""",
            "tools/call" => parameters!.Value.GetProperty("arguments").GetProperty("case").GetString() switch
            {
                "link" => """{"content":[{"type":"resource_link","uri":"docs://guide","name":"guide","title":"The guide","mimeType":"text/plain","size":2048,"description":"How to install"}]}""",
                "binary" => """{"content":[{"type":"resource","resource":{"uri":"docs://host/file.pdf","mimeType":"application/pdf","blob":"JVBERg=="}},{"type":"resource","resource":{"uri":"docs://data","mimeType":"application/json","blob":"eyJhIjoxfQ=="}},{"type":"audio","data":"AA==","mimeType":"audio/wav"}]}""",
                "error" => """{"content":[],"isError":true,"_meta":{"trace":"x"}}""",
                "structured" => """{"content":[],"structuredContent":{"rows":[1,2]}}""",
                _ => "{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('a', 15000) + "\"},{\"type\":\"text\",\"text\":\"" + new string('b', 15000) + "\"}]}"
            },
            _ => throw new IOException("Unexpected MCP method " + method)
        }));
        public Task CloseAsync() => Task.CompletedTask;
    }

    // tools.ts convertMcpResult: resource links name read_mcp_resource, binary resources are saved, JSON blobs are text, audio is
    // omitted, isError without text gets a message and is an error, structured-only results show as JSON, and text over 20 KB is cut
    // in the middle with the full text saved to a file.
    private static Task ToolResultConversion() => WithRoot("results", DirectDocs, async fixture =>
    {
        var provider = new Endpoint(() => Call("t1", "mcp__docs__get", new { @case = "link" }), () => Call("t2", "mcp__docs__get", new { @case = "binary" }),
            () => Call("t3", "mcp__docs__get", new { @case = "error" }), () => Call("t4", "mcp__docs__get", new { @case = "structured" }),
            () => Call("t5", "mcp__docs__get", new { @case = "long" }), () => Text("done"));
        var host = fixture.Host() with { CreateChannel = entry => (actual, token) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(new ResultServer()) };
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, host))
        {
            await rpc.Prompt("p1", "convert");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        }
        var requests = provider.Snapshot();
        Names(["[Resource docs://guide \"The guide\" (text/plain, 2.0KB): How to install. Read it with read_mcp_resource (server \"docs\")]"], ToolResults(requests[1]), "resource link");
        var binary = ToolResults(requests[2]).Single();
        Check(System.Text.RegularExpressions.Regex.IsMatch(binary, @"^\[Binary resource docs://host/file\.pdf \(application/pdf, 4B\) saved to .*pi-mcp-[0-9a-f]{16}\.pdf\]\n\{""a"":1\}\n\[audio audio/wav omitted\]$"), binary);
        var error = JsonDocument.Parse(requests[3].Body!).RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0];
        Check(error.TryGetProperty("is_error", out var isError) && isError.GetBoolean(), "isError is an error result: " + error);
        Names(["MCP tool docs/get returned an error"], ToolResults(requests[3]), "error text");
        Names(["{\n  \"rows\": [\n    1,\n    2\n  ]\n}"], ToolResults(requests[4]), "structured content as JSON");
        var truncated = ToolResults(requests[5]).Single();
        Check(truncated.StartsWith("Warning: truncated output (original token count: 7501)\nTotal output lines: 2\n\n" + new string('a', 10240) + "…", StringComparison.Ordinal) &&
            truncated.Contains("…9521 chars truncated…" + new string('b', 10240) + "\n\n[Full output: ", StringComparison.Ordinal), truncated[..200] + " ... " + truncated[^200..]);
        var saved = System.Text.RegularExpressions.Regex.Match(truncated, @"\[Full output: (.*) \(read it with offset/limit\)\]$").Groups[1].Value;
        Equal(30001, File.ReadAllText(saved).Length, "the full text is saved");
        File.Delete(saved);
    });
}
