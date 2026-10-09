// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/agent/src/agent-loop.ts (prepareToolCall) and
// packages/ai/src/utils/validation.ts (validateToolArguments) through the Pi entry: the tool runs with the coerced arguments, a
// failure is the error result whose text is the validation message, and the session keeps the model's arguments.
using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Pi;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;

internal static partial class Program
{
    /// <summary>A fake MCP server with one tool whose plain JSON schema requires an integer; tools/call echoes its arguments.</summary>
    private sealed class EchoMcpChannel : IMcpAdmittedRequestChannel
    {
        public List<string> Calls { get; } = [];
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize")
                return ValueTask.FromResult(JsonData.Parse("{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"fixture\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}"));
            if (method == "tools/list")
                return ValueTask.FromResult(JsonData.Parse("""
                    {"tools":[{"name":"count","description":"Counts.","inputSchema":{"type":"object","properties":{"n":{"type":"integer"},"flag":{"type":"boolean"}},"required":["n"]}}]}
                    """));
            if (method == "tools/call")
            {
                var arguments = parameters!.Value.GetProperty("arguments").GetRawText();
                lock (Calls) Calls.Add(arguments);
                return ValueTask.FromResult(JsonData.Parse(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = "got " + arguments } } })));
            }
            return ValueTask.FromException<JsonData>(new IOException("Unexpected MCP request " + method));
        }
        public Task CloseAsync() => Task.CompletedTask;
    }

    /// <summary>The tool_result block the model received in <paramref name="request"/>'s last message: its text and is_error.</summary>
    private static (string Text, bool IsError) ToolResultBlock(Seen request)
    {
        var messages = request.Json.GetProperty("messages");
        var block = messages[messages.GetArrayLength() - 1].GetProperty("content").EnumerateArray().First(item => item.GetProperty("type").GetString() == "tool_result");
        var content = block.GetProperty("content");
        var text = content.ValueKind == JsonValueKind.String ? content.GetString()!
            : string.Concat(content.EnumerateArray().Where(item => item.GetProperty("type").GetString() == "text").Select(item => item.GetProperty("text").GetString()));
        return (text, block.TryGetProperty("is_error", out var error) && error.GetBoolean());
    }

    private static IEnumerable<(string, Func<Task>)> ValidationCases() =>
    [
        ("validation.read-runs-with-coerced-arguments-and-the-session-keeps-the-model-arguments", async () =>
        {
            using var sandbox = new Sandbox("validation-coerce");
            var file = sandbox.Write(Path.Combine(sandbox.Cwd, "lines.txt"), "one\ntwo\nthree\nfour");
            sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("read", new { path = file, offset = "2", limit = "2" }) : AnthropicText("done");
            var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "read it");
            Equal(0, code, "exit; " + stderr);
            Equal("done\n", stdout, "answer");
            var (text, isError) = ToolResultBlock(sandbox.Requests[1]);
            Equal(false, isError, "coerced read is not an error: " + text);
            Equal("two\nthree\n\n[1 more lines in file. Use offset=4 to continue.]", text, "read received offset 2 and limit 2 as numbers");
            // The persisted assistant message (and the request replaying it) keeps the model's string arguments.
            var replayed = sandbox.Requests[1].Json.GetProperty("messages").EnumerateArray().SelectMany(message => message.GetProperty("content").ValueKind == JsonValueKind.Array
                ? message.GetProperty("content").EnumerateArray() : []).First(block => block.GetProperty("type").GetString() == "tool_use");
            Equal("\"2\"", replayed.GetProperty("input").GetProperty("limit").GetRawText(), "replayed tool_use input");
            var session = File.ReadAllLines(sandbox.SessionFiles().Single()).Select(line => JsonDocument.Parse(line).RootElement)
                .Where(entry => entry.GetProperty("type").GetString() == "message" && entry.GetProperty("message").GetProperty("role").GetString() == "assistant")
                .SelectMany(entry => entry.GetProperty("message").GetProperty("content").EnumerateArray())
                .First(block => block.GetProperty("type").GetString() == "toolCall");
            Equal("\"2\"", session.GetProperty("arguments").GetProperty("offset").GetRawText(), "persisted toolCall arguments");
        }),
        ("validation.read-without-path-returns-the-upstream-validation-error-result", async () =>
        {
            using var sandbox = new Sandbox("validation-missing");
            sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("read", new { limit = "ten" }) : AnthropicText("done");
            var (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "read it");
            Equal(0, code, "exit; " + stderr);
            var (text, isError) = ToolResultBlock(sandbox.Requests[1]);
            Equal(true, isError, "validation failure is an error result");
            Equal("Validation failed for tool \"read\":\n  - path: must have required properties path\n  - limit: must be number\n\nReceived arguments:\n{\n  \"limit\": \"ten\"\n}",
                text, "upstream validation message");
        }),
        ("validation.mcp-tool-plain-json-schema-coerces-and-rejects-before-calling-the-server", async () =>
        {
            using var sandbox = new Sandbox("validation-mcp");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "mcp.json"), """{"mcpServers":{"proj":{"command":"proj-server","exposure":"direct"}}}""");
            var channel = new EchoMcpChannel();
            sandbox.Respond = (_, index) => index switch
            {
                0 => AnthropicToolCall("mcp__proj__count", new { n = "5", flag = "true", extra = (string?)null }, "toolu_c1"),
                1 => AnthropicToolCall("mcp__proj__count", new { n = "five" }, "toolu_c2"),
                _ => AnthropicText("done")
            };
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null) with
            {
                CreateMcpHost = agentDir => new McpSessionHost(agentDir, sandbox.Home, () => [])
                { CreateChannel = entry => (actual, token) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(channel) }
            };
            var code = await PiCommand.RunAsync(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "count"], host, CancellationToken.None);
            Equal(0, code, "exit; " + stderr);
            // coerceWithJsonSchema: "5" -> 5 and "true" -> true; the unknown null property is kept (no Value.Convert for plain schemas).
            Equal("{\"n\":5,\"flag\":true,\"extra\":null}", channel.Calls.Single(), "the server received the coerced arguments");
            var (text, isError) = ToolResultBlock(sandbox.Requests[1]);
            Equal(false, isError, "coerced call succeeded: " + text);
            (text, isError) = ToolResultBlock(sandbox.Requests[2]);
            Equal(true, isError, "invalid MCP arguments are an error result");
            Equal("Validation failed for tool \"mcp__proj__count\":\n  - n: must be integer\n\nReceived arguments:\n{\n  \"n\": \"five\"\n}", text, "upstream validation message");
        }),
        // rpc-mode.ts "bash" -> executeBash: no command length limit of its own (the native cap was 12,000 characters); the operating
        // system's spawn limit fails as Node's spawn error, which the response carries.
        ("validation.rpc-user-bash-runs-long-commands", async () =>
        {
            using var sandbox = new Sandbox("validation-rpc-bash");
            sandbox.Vars["PI_OFFLINE"] = "1";
            var commands = new[] { "echo " + new string('y', 20_000), "echo " + new string('z', 40_000) };
            var lines = string.Concat(commands.Select((command, index) =>
                System.Text.Json.JsonSerializer.Serialize(new { id = "b" + index, type = "bash", command }) + "\n"));
            var gate = new GatedInput(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(lines)));
            using var output = new SignalingStream("\"id\":\"b1\"", gate.Release);
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null, rpcInput: gate, rpcOutput: output) with { StdoutIsTty = false };
            Equal(0, await PiCommand.RunAsync(["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], host, CancellationToken.None), "exit; " + stderr);
            var responses = System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n')
                .Where(line => line.Contains("\"command\":\"bash\"", StringComparison.Ordinal)).Select(line => JsonDocument.Parse(line).RootElement).ToArray();
            var first = responses.Single(response => response.GetProperty("id").GetString() == "b0");
            Check(first.GetProperty("success").GetBoolean() && first.GetProperty("data").GetProperty("output").GetString()!.StartsWith("yyyy", StringComparison.Ordinal),
                "20k user command runs: " + first.GetRawText()[..Math.Min(300, first.GetRawText().Length)]);
            var second = responses.Single(response => response.GetProperty("id").GetString() == "b1");
            if (OperatingSystem.IsWindows())
                Check(!second.GetProperty("success").GetBoolean() && second.GetProperty("error").GetString() == "spawn ENAMETOOLONG",
                    "40k user command: " + second.GetRawText()[..Math.Min(300, second.GetRawText().Length)]);
            else Check(second.GetProperty("success").GetBoolean(), "40k user command runs on Unix");
        }),
        // rpc-mode.ts "bash" -> executeBash -> spawn: a NUL byte fails as Node's argument error (captured from Pi 1.1.0 on Windows,
        // where the shell's arguments are ["-c", command]).
        ("validation.rpc-user-bash-nul-is-the-spawn-argument-error", async () =>
        {
            using var sandbox = new Sandbox("validation-rpc-nul");
            sandbox.Vars["PI_OFFLINE"] = "1";
            var line = System.Text.Json.JsonSerializer.Serialize(new { id = "n0", type = "bash", command = "echo a\0b" }) + "\n";
            var gate = new GatedInput(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(line)));
            using var output = new SignalingStream("\"command\":\"bash\"", gate.Release);
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null, rpcInput: gate, rpcOutput: output) with { StdoutIsTty = false };
            Equal(0, await PiCommand.RunAsync(["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], host, CancellationToken.None), "exit; " + stderr);
            var response = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n')
                .First(text => text.Contains("\"command\":\"bash\"", StringComparison.Ordinal))).RootElement;
            Equal(false, response.GetProperty("success").GetBoolean(), "NUL command fails: " + response.GetRawText());
            Equal("The argument 'args[1]' must be a string without null bytes. Received 'echo a\\x00b'", response.GetProperty("error").GetString(), "spawn error");
        }),
        // bash.ts execute: an empty command runs, resolveTimeoutMs rejects a non-positive timeout and accepts a fractional one, and only
        // the operating system bounds the command length (captured from the installed Pi 1.1.0 bash tool on Windows).
        ("validation.bash-admits-what-the-source-schema-admits", async () =>
        {
            using var sandbox = new Sandbox("validation-bash");
            var long20k = "echo " + new string('x', 20_000);
            var long40k = "echo " + new string('x', 40_000);
            var calls = new (object Input, string? Expected, bool IsError)[]
            {
                (new { command = "" }, "(no output)", false),
                (new { command = "echo hi", timeout = 0 }, "Invalid timeout: must be a finite number of seconds", true),
                (new { command = "echo hi", timeout = -1 }, "Invalid timeout: must be a finite number of seconds", true),
                (new { command = "echo hi", timeout = 0.5 }, "hi\n", false),
                // Runs (the old native cap was 12,000 characters); Git Bash's echo prints its own prefix of the argument, as under Pi.
                (new { command = long20k }, null, false),
                (new { command = long40k }, OperatingSystem.IsWindows() ? "spawn ENAMETOOLONG" : null, OperatingSystem.IsWindows()),
            };
            sandbox.Respond = (_, index) => index < calls.Length ? AnthropicToolCall("bash", calls[index].Input, "toolu_b" + index) : AnthropicText("done");
            var (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "run");
            Equal(0, code, "exit; " + stderr);
            for (var index = 0; index < calls.Length; index++)
            {
                var (text, isError) = ToolResultBlock(sandbox.Requests[index + 1]);
                Equal(calls[index].IsError, isError, "bash call " + index + " isError: " + text[..Math.Min(200, text.Length)]);
                if (calls[index].Expected is { } expected) Equal(expected, text, "bash call " + index);
                else Check(text.Length > 1000 && text.TrimEnd('\n').All(character => character == 'x'), "a long command runs: " + text[..Math.Min(80, text.Length)]);
            }
        }),
    ];
}
