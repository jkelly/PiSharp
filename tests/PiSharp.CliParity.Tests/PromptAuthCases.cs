// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (prompt: model and auth
// validation), packages/coding-agent/src/core/auth-guidance.ts, packages/coding-agent/src/main.ts (no model outside interactive mode),
// packages/coding-agent/src/modes/print-mode.ts and packages/coding-agent/src/modes/rpc/rpc-mode.ts (prompt error responses).
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.Cli.Models;
using PiSharp.Cli.Pi;

// A session starts with a selected model whose provider has no credentials; the prompt is refused before anything is persisted, with
// upstream's texts, and a credential that appears later is used by the next prompt. Without any model only interactive mode starts.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> PromptAuthCases() =>
    [
        ("prompt-auth.print-mode-refuses-a-prompt-without-credentials", async () =>
        {
            using var sandbox = new Sandbox("prompt-no-key");
            sandbox.Vars.Remove("ANTHROPIC_API_KEY");
            var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
            // agent-session.ts prompt: formatNoApiKeyFoundMessage; print-mode.ts: console.error(error.message), exit 1.
            Equal(1, code, "exit; " + stderr);
            Equal(ModelListing.NoApiKeyFoundMessage("anthropic") + "\n", stderr, "stderr");
            Equal("", stdout, "stdout");
            Equal(0, sandbox.Requests.Count, "nothing sent");
            NoPromptPersisted(sandbox, "print");
            // The catalog route of another provider is refused the same way.
            (code, _, stderr) = await sandbox.Run("-p", "--provider", "openai", "--model", "gpt-4o", "hi");
            Check(code == 1 && stderr == ModelListing.NoApiKeyFoundMessage("openai") + "\n", $"openai: {code} {stderr}");
            NoPromptPersisted(sandbox, "openai");
            // JSON mode writes the session header, then the refusal on stderr.
            (code, stdout, stderr) = await sandbox.Run("--mode", "json", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
            Check(code == 1 && stderr == ModelListing.NoApiKeyFoundMessage("anthropic") + "\n", $"json: {code} {stderr}");
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check(lines.Length == 1 && JsonNode.Parse(lines[0])!["type"]!.GetValue<string>() == "session", "json mode prints only the header: " + stdout);
            Equal(0, sandbox.Requests.Count, "nothing sent in any mode");
            NoPromptPersisted(sandbox, "json");
            // A credential stored since (auth.json) is picked up by the next run's prompt.
            sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"), """{"anthropic":{"type":"api_key","key":"sk-stored"}}""");
            (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
            Check(code == 0 && stdout == "ok\n", $"stored key: {code} {stdout} {stderr}");
            Equal("sk-stored", sandbox.Requests.Single().Headers["x-api-key"], "the stored key is used");
        }),
        ("prompt-auth.without-any-model-every-mode-starts-on-the-unknown-model", async () =>
        {
            using var sandbox = new Sandbox("prompt-no-model");
            sandbox.Vars.Remove("ANTHROPIC_API_KEY");
            // sdk.ts leaves the Agent's DEFAULT_MODEL (agent.ts: provider "unknown") when findInitialModel finds nothing, so the
            // `!session.model` exit in main.ts never runs: the prompt is refused as "the selected model" (as Pi 1.1.0 does when run).
            var unknown = ModelListing.NoApiKeyFoundMessage("unknown");
            Check(unknown.StartsWith("No API key found for the selected model.\n\nUse /login", StringComparison.Ordinal), "unknown provider text: " + unknown);
            var (code, stdout, stderr) = await sandbox.Run("-p", "hi");
            Check(code == 1 && stderr == unknown + "\n" && stdout == "", $"print: {code} {stdout} {stderr}");
            (code, stdout, stderr) = await sandbox.Run("--mode", "json", "hi");
            Check(code == 1 && stderr == unknown + "\n" && stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1, $"json: {code} {stdout} {stderr}");
            NoPromptPersisted(sandbox, "print and json");
            // rpc-mode.ts: the session runs; get_state reports DEFAULT_MODEL, nothing is available and thinking is off.
            var input = new ScriptedInput();
            var responses = new Dictionary<string, JsonNode>();
            using var output = new LineOutput(line =>
            {
                if (line["type"]?.GetValue<string>() != "response") return;
                lock (responses) { responses[line["id"]!.GetValue<string>()] = line; if (responses.Count == 5) input.Complete(); }
            });
            foreach (var command in new[] { """{"id":"1","type":"prompt","message":"hi"}""", """{"id":"2","type":"get_state"}""",
                """{"id":"3","type":"get_available_models"}""", """{"id":"4","type":"get_available_thinking_levels"}""", """{"id":"5","type":"cycle_model"}""" })
                input.Send(command);
            using var rpcOut = new StringWriter(); using var rpcErr = new StringWriter();
            var host = sandbox.Host(rpcOut, rpcErr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
            code = await PiCommand.RunAsync(["--mode", "rpc"], host, CancellationToken.None);
            Equal(0, code, "rpc exit; " + rpcErr);
            Check(!responses["1"]["success"]!.GetValue<bool>() && responses["1"]["error"]!.GetValue<string>() == unknown, "prompt: " + responses["1"].ToJsonString());
            Equal("""{"id":"unknown","name":"unknown","api":"unknown","provider":"unknown","baseUrl":"","reasoning":false,"input":[],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":0,"maxTokens":0}""",
                responses["2"]["data"]!["model"]!.ToJsonString(), "get_state model");
            Equal("off", responses["2"]["data"]!["thinkingLevel"]!.GetValue<string>(), "thinking level");
            Equal("""{"models":[]}""", responses["3"]["data"]!.ToJsonString(), "available models");
            Equal("""{"levels":["off"]}""", responses["4"]["data"]!.ToJsonString(), "thinking levels");
            Check(responses["5"]["success"]!.GetValue<bool>() && responses["5"]["data"] is null, "cycle_model: " + responses["5"].ToJsonString());
            Equal(0, sandbox.Requests.Count, "nothing sent");
            NoPromptPersisted(sandbox, "rpc");
            Check(!sandbox.SessionFiles().SelectMany(File.ReadLines).Any(line => line.Contains("\"model_change\"", StringComparison.Ordinal)),
                "no model_change is recorded for the unknown model");
        }),
        ("prompt-auth.rpc-prompt-error-then-a-stored-key-serves-the-next-prompt", async () =>
        {
            using var sandbox = new Sandbox("prompt-rpc-later-key");
            sandbox.Vars.Remove("ANTHROPIC_API_KEY");
            var input = new ScriptedInput();
            var responses = new Dictionary<string, JsonNode>(); var types = new List<string>();
            using var output = new LineOutput(line =>
            {
                var type = line["type"]?.GetValue<string>() ?? "";
                lock (types) types.Add(type);
                if (type == "response") lock (responses) responses[line["id"]!.GetValue<string>()] = line;
                if (type == "response" && line["id"]?.GetValue<string>() == "1")
                {
                    // auth.json written while the session runs (another process, or /login): the next prompt re-checks auth.
                    sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"), """{"anthropic":{"type":"api_key","key":"sk-later"}}""");
                    input.Send("""{"id":"2","type":"get_state"}""");
                }
                if (type == "response" && line["id"]?.GetValue<string>() == "2") input.Send("""{"id":"3","type":"prompt","message":"second"}""");
                if (type == "agent_settled") input.Complete();
            });
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
            input.Send("""{"id":"1","type":"prompt","message":"first"}""");
            var code = await PiCommand.RunAsync(["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], host, CancellationToken.None);
            Equal(0, code, "exit; " + stderr);
            // rpc-mode.ts: the prompt command's error response carries the preflight's message; no run starts for it.
            var refused = responses["1"];
            Check(refused["command"]!.GetValue<string>() == "prompt" && !refused["success"]!.GetValue<bool>() &&
                refused["error"]!.GetValue<string>() == ModelListing.NoApiKeyFoundMessage("anthropic"), "refused prompt: " + refused.ToJsonString());
            Equal("claude-sonnet-4-5", responses["2"]["data"]!["model"]!["id"]!.GetValue<string>(), "the session keeps its selected model");
            Check(responses["3"]["success"]!.GetValue<bool>(), "second prompt accepted: " + responses["3"].ToJsonString());
            Equal(1, types.Count(type => type == "agent_start"), "one run: " + string.Join(",", types));
            var request = sandbox.Requests.Single();
            Equal("sk-later", request.Headers["x-api-key"], "the key written after startup is used");
            Equal(1, request.Json.GetProperty("messages").GetArrayLength(), "the refused prompt never reached the history");
            var users = sandbox.SessionFiles().SelectMany(File.ReadLines).Select(line => JsonNode.Parse(line)!)
                .Where(entry => entry["type"]?.GetValue<string>() == "message" && entry["message"]?["role"]?.GetValue<string>() == "user")
                .Select(entry => entry["message"]!["content"]![0]!["text"]!.GetValue<string>()).ToArray();
            Names(["second"], users, "persisted user messages");
        }),
    ];

    /// <summary>No session file holds a message entry (the refused prompt persisted nothing).</summary>
    private static void NoPromptPersisted(Sandbox sandbox, string what)
    {
        foreach (var file in sandbox.SessionFiles())
            Check(!File.ReadLines(file).Any(line => JsonNode.Parse(line)?["type"]?.GetValue<string>() == "message"), what + ": a message was persisted in " + file);
    }

    /// <summary>RPC standard input fed one JSON line at a time; end of input after <see cref="Complete"/>.</summary>
    private sealed class ScriptedInput : Stream
    {
        private readonly Channel<byte[]> lines = Channel.CreateUnbounded<byte[]>();
        private byte[]? current; private int offset;
        public void Send(string line) => lines.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));
        public void Complete() => lines.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (current is null || offset >= current.Length)
            {
                if (!await lines.Reader.WaitToReadAsync(cancellationToken) || !lines.Reader.TryRead(out current)) return 0;
                offset = 0;
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer); offset += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Captured RPC standard output that hands each complete JSON line to a callback.</summary>
    private sealed class LineOutput(Action<JsonNode> onLine) : MemoryStream
    {
        private readonly object gate = new();
        private int scanned;
        public override void Write(byte[] buffer, int offset, int count) { base.Write(buffer, offset, count); Scan(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await base.WriteAsync(buffer, cancellationToken); Scan(); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { base.Write(buffer, offset, count); Scan(); return Task.CompletedTask; }
        private void Scan()
        {
            List<string> complete = [];
            lock (gate)
            {
                var bytes = ToArray();
                var end = Array.LastIndexOf(bytes, (byte)'\n');
                if (end < scanned) return;
                complete.AddRange(Encoding.UTF8.GetString(bytes, scanned, end + 1 - scanned).Split('\n', StringSplitOptions.RemoveEmptyEntries));
                scanned = end + 1;
            }
            foreach (var line in complete) onLine(JsonNode.Parse(line, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 4096 })!);
        }
    }
}
