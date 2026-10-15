using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Runtime;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/extensions/mcp/index.ts (session_start and
// ensureDiscoveryActive: another extension's codemode is activated for `codemode` servers when it declares reachesTools; the tool_call
// handler: a tool that declares reachesTools waits, before it runs, for the servers its call may reach) and
// packages/coding-agent/src/core/extensions/types.ts (ToolDefinition.reachesTools). Authored expectations.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ReachCases() =>
    [
        ("native.reaching-codemode-is-activated-and-waits-for-the-servers-its-call-reaches", NativeReachingCodemode),
    ];

    /// <summary>A fake MCP server behind a channel: initialize answers after a delay, and the time tools/list is answered is kept.</summary>
    private sealed class DelayedMcpServer(TimeSpan delay) : IMcpAdmittedRequestChannel
    {
        public long ListedAt;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            switch (method)
            {
                case "initialize":
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                    return JsonData.Parse("""{"protocolVersion":"2025-11-25","serverInfo":{"name":"fixture","version":"1"},"capabilities":{"tools":{}}}""");
                case "tools/list":
                    Interlocked.CompareExchange(ref ListedAt, Stopwatch.GetTimestamp(), 0);
                    return JsonData.Parse("""{"tools":[{"name":"search","description":"Search.","inputSchema":{"type":"object"}}]}""");
                default: throw new IOException("Unexpected MCP method " + method);
            }
        }
        public Task CloseAsync() => Task.CompletedTask;
    }

    // A native C# extension registers its own `codemode` (the built-in one is disabled) that declares reachesTools, and a plain tool.
    // At session start the MCP extension activates that codemode for the `codemode` servers. Before any tool runs, the session waits for
    // the servers the call may reach: the plain tool runs at once while the servers still connect; codemode, whose input names
    // mcp__wiki, runs once wiki connected and before the slower server it does not name.
    private static async Task NativeReachingCodemode()
    {
        using var sandbox = NativeSandbox("native-reach");
        NativeExtensionFolder(Path.Combine(sandbox.Cwd, ".pi", "extensions", "native-reach"), typeof(ParityNativeReachExtension));
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"extensions":["-builtin:codemode"]}""");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "mcp.json"), """{"mcpServers":{"wiki":{"command":"wiki-server"},"slow":{"command":"slow-server"}}}""");
        var delays = new Dictionary<string, TimeSpan> { ["wiki"] = TimeSpan.FromSeconds(3), ["slow"] = TimeSpan.FromSeconds(15) };
        var servers = new ConcurrentDictionary<string, DelayedMcpServer>(StringComparer.Ordinal);
        sandbox.McpHost = agentDir => new PiSharp.Cli.Mcp.McpSessionHost(agentDir, sandbox.Home, () => [])
        {
            CreateChannel = entry => (actual, token) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(servers.GetOrAdd(entry.Name, name => new(delays[name])))
        };
        sandbox.Respond = (_, index) => index switch
        {
            0 => AnthropicToolCall("native_plain", new { }, "toolu_plain"),
            1 => AnthropicToolCall("codemode", new { code = "return await tools.mcp__wiki__search({});" }, "toolu_code"),
            _ => AnthropicText("done")
        };
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "go"]);
        Equal(0, code, "exit; " + stderr);
        Check(!stderr.Contains("only reachable from the codemode or tool_search tool", StringComparison.Ordinal), "the extension's codemode reaches the MCP tools: " + stderr);
        Equal(3, sandbox.Requests.Count, "requests");
        Check(sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "codemode"),
            "the extension's codemode is active from the first request");
        var ran = NativeLog(sandbox).Select(line => line.Split(' ')).Where(parts => parts.Length == 2 && parts[0] is "plain" or "codemode")
            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        Check(ran.ContainsKey("plain") && ran.ContainsKey("codemode"), "both tools ran: " + string.Join("|", NativeLog(sandbox)));
        var wiki = servers["wiki"].ListedAt; var slow = servers.TryGetValue("slow", out var server) ? server.ListedAt : 0;
        Check(wiki != 0 && ran["codemode"] > wiki, "codemode waited for wiki, which its input names");
        Check(ran["plain"] < wiki, "the plain tool did not wait for the servers");
        Check(slow == 0 || ran["codemode"] < slow, "codemode did not wait for slow, which its input does not name");
        Equal("codemode ran", ToolResultText(sandbox.Requests[2]), "codemode result");
    }
}

/// <summary>A native extension with its own `codemode` that declares reachesTools (the words of its script), inactive until the MCP
/// extension activates it, and a plain tool without reach. Both log when they run (a Stopwatch timestamp).</summary>
public sealed class ParityNativeReachExtension : IPiSharpExtension
{
    private static readonly Regex Words = new("[A-Za-z0-9_]+", RegexOptions.CultureInvariant);

    private static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("PISHARP_EXTENSION_PARITY_NATIVE_LOG") is { } path) File.AppendAllText(path, line + "\n");
    }

    private static ValueTask<JsonData> Ran(string name)
    {
        Log(name + " " + Stopwatch.GetTimestamp().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = name + " ran" } }, details = new { } })));
    }

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        registry.RegisterTool(new("reach-codemode", "codemode", "Runs a script that calls tools.",
            JsonData.Parse("""{"type":"object","properties":{"code":{"type":"string"}},"required":["code"]}"""), (_, _, _) => Ran("codemode"))
        {
            DefaultActive = false,
            ReachesTools = input => input.Value.ValueKind == JsonValueKind.Object && input.Value.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? ToolReach.Of([.. Words.Matches(code.GetString()!).Select(match => match.Value)]) : ToolReach.Of([])
        });
        registry.RegisterTool(new("reach-plain", "native_plain", "A tool that reaches no other tool.", JsonData.Parse("""{"type":"object"}"""), (_, _, _) => Ran("plain")));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
