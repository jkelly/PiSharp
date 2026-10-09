using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

// The Pi entry end to end through PiCommand.RunAsync (the real Program entry minus the console): print text and JSON output,
// the session layout it writes, @file and piped stdin input, the tool gates and RPC mode, with a fake Anthropic endpoint.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> EntryCases() =>
    [
        ("entry.print-text-final-assistant-text-and-session-layout", PrintText),
        ("entry.json-mode-header-then-event-lines", JsonMode),
        ("entry.system-prompt-default-sections-byte-exact", DefaultSystemPrompt),
        ("entry.file-arguments-stdin-and-images", FileArguments),
        ("entry.initial-message-merge-and-remaining-messages", InitialMessages),
        ("entry.print-error-stop-reason-exits-1", PrintError),
        ("entry.rpc-mode-dispatches-on-standard-streams", RpcMode),
        ("entry.rpc-mode-switches-among-available-models", RpcModelSwitching),
        ("entry.interactive-mode-hands-the-session-to-the-frontend", Interactive),
        ("entry.export-writes-html-and-reports-the-path", Export),
        ("entry.environment-variables-and-proxy-setting", EnvironmentVariables),
    ];

    // cli/file-processor.ts: text files wrapped in <file name="…">, images attached with an empty reference, empty files skipped;
    // initial-message.ts: stdin + file text + first message in one prompt. A missing file stops the run.
    private static async Task FileArguments()
    {
        using var sandbox = new Sandbox("file-args");
        var notes = sandbox.Write(Path.Combine(sandbox.Cwd, "notes.md"), "line one\nline two");
        sandbox.Write(Path.Combine(sandbox.Cwd, "empty.txt"), "");
        var png = Path.Combine(sandbox.Cwd, "pixel.png");
        File.WriteAllBytes(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        var (code, _, stderr) = await sandbox.RunWith("piped text\n", "-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "@notes.md", "@empty.txt", "@pixel.png", "Explain");
        Equal(0, code, "exit; " + stderr);
        var user = sandbox.Requests[0].Json.GetProperty("messages")[0];
        var content = user.GetProperty("content");
        var text = content.EnumerateArray().First(block => block.GetProperty("type").GetString() == "text").GetProperty("text").GetString();
        Equal($"piped text<file name=\"{notes}\">\nline one\nline two\n</file>\n<file name=\"{png}\"></file>\nExplain", text, "prompt text");
        var image = content.EnumerateArray().Single(block => block.GetProperty("type").GetString() == "image");
        Equal("image/png", image.GetProperty("source").GetProperty("media_type").GetString(), "image attached");
        (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "@missing.txt", "x");
        Equal(1, code, "missing file exit");
        Equal($"Error: File not found: {Path.Combine(sandbox.Cwd, "missing.txt")}\n", stderr, "missing file message");
    }

    // main.ts: the first message (with stdin and files) is the initial prompt; the rest are sent one after another, each waiting for
    // the previous run to settle.
    private static async Task InitialMessages()
    {
        var parsed = new List<string> { "Explain it", "Second message" };
        var (message, images) = PiInitialMessage.Build(parsed, "file\n", [], "stdin\n");
        Equal("stdin\nfile\nExplain it", message, "combined");
        Names(["Second message"], parsed, "remaining");
        Equal(0, images.Length, "no images");
        var onlyStdin = new List<string>();
        Equal("README contents", PiInitialMessage.Build(onlyStdin, null, [], "README contents").Message, "stdin alone");
        using var sandbox = new Sandbox("messages");
        var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "first", "second", "third");
        Equal(0, code, "exit; " + stderr);
        Equal(3, sandbox.Requests.Count, "three prompts");
        Equal(5, sandbox.Requests[2].Json.GetProperty("messages").GetArrayLength(), "third request carries the conversation");
        Equal("ok\n", stdout, "only the final answer is printed");
    }

    // print-mode.ts: a final assistant message that failed prints its error message to stderr and exits 1; JSON mode still exits 0.
    private static async Task PrintError()
    {
        using var sandbox = new Sandbox("print-error");
        sandbox.Vars["PI_OFFLINE"] = "1";
        sandbox.Respond = (_, _) => AnthropicError(400, "prompt is too long");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"retry":{"enabled":false}}""");
        var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "x");
        Equal(1, code, "exit");
        Equal("", stdout, "nothing on stdout");
        (var jsonCode, var json, _) = await sandbox.Run("--mode", "json", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "x");
        Equal(0, jsonCode, "json exit");
        var end = json.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!)
            .Last(line => line["type"]?.GetValue<string>() == "message_end" && line["message"]?["role"]?.GetValue<string>() == "assistant");
        Equal("error", end["message"]!["stopReason"]!.GetValue<string>(), "json carries the error stop");
        Equal(end["message"]!["errorMessage"]!.GetValue<string>() + "\n", stderr, "text mode prints the errorMessage");
    }

    // modes/rpc: --mode rpc dispatches JSON lines on stdin and writes responses and events on stdout.
    private static async Task RpcMode()
    {
        using var sandbox = new Sandbox("rpc-mode");
        var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"id\":\"1\",\"type\":\"prompt\",\"message\":\"hi\"}\n"));
        var gate = new GatedInput(input);
        using var output = new SignalingStream("\"type\":\"agent_settled\"", gate.Release);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, rpcInput: gate, rpcOutput: output) with { StdoutIsTty = false };
        var code = await PiCommand.RunAsync(["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], host, CancellationToken.None);
        Equal(0, code, "exit; " + stderr);
        var lines = System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Any(line => line.Contains("\"type\":\"response\"", StringComparison.Ordinal) && line.Contains("\"command\":\"prompt\"", StringComparison.Ordinal)), "prompt response");
        Check(lines.Any(line => line.Contains("\"type\":\"agent_settled\"", StringComparison.Ordinal)), "events on stdout");
        Equal(1, sandbox.SessionFiles().Length, "rpc mode persists the session in the default layout");
    }

    // rpc-mode.ts get_available_models/set_model/cycle_model over modelRuntime.getAvailableSnapshot: the registry's available models are
    // selectable, cycle_model walks the --models scope (isScoped) and the next request goes to the selected model.
    private static async Task RpcModelSwitching()
    {
        using var sandbox = new Sandbox("rpc-models");
        var commands = string.Join("\n",
            """{"id":"1","type":"get_available_models"}""",
            """{"id":"2","type":"set_model","provider":"openai","modelId":"gpt-4o"}""",
            """{"id":"3","type":"cycle_model"}""",
            """{"id":"4","type":"prompt","message":"hi"}""") + "\n";
        var gate = new GatedInput(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(commands)));
        using var output = new SignalingStream("\"type\":\"agent_settled\"", gate.Release);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, rpcInput: gate, rpcOutput: output) with { StdoutIsTty = false };
        var code = await PiCommand.RunAsync(["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5",
            "--models", "claude-sonnet-4-5,claude-haiku-4-5"], host, CancellationToken.None);
        Equal(0, code, "exit; " + stderr);
        var responses = System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).Where(line => line["type"]?.GetValue<string>() == "response")
            .ToDictionary(line => line["id"]!.GetValue<string>());
        var ids = (responses["1"]["data"]!["models"] as JsonArray)!.Select(model => model!["id"]!.GetValue<string>()).ToList();
        Check(ids.Contains("claude-sonnet-4-5") && ids.Contains("claude-haiku-4-5"), "available models: " + string.Join(",", ids));
        Check(responses["2"]["success"]!.GetValue<bool>() == false && responses["2"]["error"]!.GetValue<string>() == "Model not found: openai/gpt-4o",
            "an unavailable model is refused: " + responses["2"].ToJsonString());
        Equal("claude-haiku-4-5", responses["3"]["data"]!["model"]!["id"]!.GetValue<string>(), "cycle_model walks the scope");
        Check(responses["3"]["data"]!["isScoped"]!.GetValue<bool>(), "isScoped");
        Equal("claude-haiku-4-5", sandbox.Requests[^1].Json.GetProperty("model").GetString(), "the prompt goes to the selected model");
    }
    // main.ts interactive: plain `pisharp` on a terminal starts the frontend with the session and the initial messages (IMPL-I renders).
    private static async Task Interactive()
    {
        using var sandbox = new Sandbox("interactive");
        string[]? received = null; PiEntryOptions? options = null;
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, interactive: true, runInteractive: (args, entry, _) => { received = args; options = entry; return Task.FromResult(0); });
        var code = await PiCommand.RunAsync(["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--use-theme", "light", "first", "second"], host, CancellationToken.None);
        Equal(0, code, "exit; " + stderr);
        Check(received is ["session", "terminal", ..] && received.Contains("--live") && received.Contains("new-lazy"), "terminal arguments: " + string.Join(" ", received ?? []));
        Equal("first", options!.InitialMessage, "initial message");
        Names(["second"], options.InitialMessages, "queued messages");
        Equal("light", options.Theme, "--use-theme applies to the interactive run");
        Equal(PiToolPolicyMode.Pi, options.ToolPolicy.Mode, "pi tool policy");
        Equal("", stdout.ToString(), "nothing printed before the frontend");
    }

    // main.ts --export: exportFromFile writes pi-session-<name>.html in the cwd unless an output path follows.
    private static async Task Export()
    {
        using var sandbox = new Sandbox("export");
        Equal(0, (await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi")).Code, "session run");
        var file = sandbox.SessionFiles().Single();
        var (code, stdout, stderr) = await sandbox.Run("--export", file);
        Equal(0, code, "exit; " + stderr);
        var name = $"pi-session-{Path.GetFileNameWithoutExtension(file)}.html";
        Equal($"Exported to: {name}\n", stdout, "message");
        Check(File.ReadAllText(Path.Combine(sandbox.Cwd, name)).Contains("<html", StringComparison.OrdinalIgnoreCase), "html written");
        (code, stdout, _) = await sandbox.Run("--export", file, "out.html");
        Check(code == 0 && stdout == "Exported to: out.html\n" && File.Exists(Path.Combine(sandbox.Cwd, "out.html")), "explicit output");
        (code, _, stderr) = await sandbox.Run("--export", "missing.jsonl");
        Check(code == 1 && stderr == $"Error: File not found: {Path.Combine(sandbox.Cwd, "missing.jsonl")}\n", "missing: " + stderr);
    }

    // main.ts and cli/setup.ts: PI_CODING_AGENT/AI_AGENT for children, --offline sets PI_OFFLINE and PI_SKIP_VERSION_CHECK,
    // PI_STARTUP_BENCHMARK is interactive-only, PI_PACKAGE_DIR locates the documentation, httpProxy fills HTTP(S)_PROXY.
    private static async Task EnvironmentVariables()
    {
        using var sandbox = new Sandbox("environment");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), "{\"httpProxy\":\" http://proxy.invalid:8080 \"}");
        sandbox.Vars["PI_PACKAGE_DIR"] = Path.Combine(sandbox.Root, "package");
        sandbox.Vars["HTTPS_PROXY"] = "http://kept.invalid";
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null) with { ApplicationDirectory = null };
        var code = await PiCommand.RunAsync(["-p", "--offline", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi"], host, CancellationToken.None);
        Equal(0, code, "exit; " + stderr);
        Equal("true", sandbox.Vars["PI_CODING_AGENT"], "PI_CODING_AGENT"); Equal("pi", sandbox.Vars["AI_AGENT"], "AI_AGENT");
        Equal("1", sandbox.Vars["PI_OFFLINE"], "PI_OFFLINE"); Equal("1", sandbox.Vars["PI_SKIP_VERSION_CHECK"], "PI_SKIP_VERSION_CHECK");
        Equal("http://proxy.invalid:8080", sandbox.Vars["HTTP_PROXY"], "HTTP_PROXY from the trimmed setting");
        Equal("http://kept.invalid", sandbox.Vars["HTTPS_PROXY"], "an existing HTTPS_PROXY is kept");
        var system = sandbox.Requests[0].Json.GetProperty("system")[0].GetProperty("text").GetString()!;
        Check(system.Contains("- Main documentation: " + Path.Join(sandbox.Root, "package", "README.md") + "\n", StringComparison.Ordinal), "PI_PACKAGE_DIR docs");
        sandbox.Vars["PI_STARTUP_BENCHMARK"] = "yes";
        var (benchCode, _, benchErr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
        Check(benchCode == 1 && benchErr == "Error: PI_STARTUP_BENCHMARK only supports interactive mode\n", "benchmark: " + benchErr);
    }

    /// <summary>Standard input that ends only after the host answered (an RPC client keeps stdin open until it is done).</summary>
    private sealed class GatedInput(Stream inner) : Stream
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => released.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            if (count > 0) return count;
            await released.Task.WaitAsync(cancellationToken);
            return 0;
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

    /// <summary>Captured standard output that runs a callback once a marker was written.</summary>
    private sealed class SignalingStream(string marker, Action signal) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { base.Write(buffer, offset, count); Probe(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await base.WriteAsync(buffer, cancellationToken); Probe(); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { base.Write(buffer, offset, count); Probe(); return Task.CompletedTask; }
        private void Probe() { if (System.Text.Encoding.UTF8.GetString(ToArray()).Contains(marker, StringComparison.Ordinal)) signal(); }
    }

    // print-mode.ts json: the session header line, then one JSON line per session event (toJsonEvent drops message_update's partial).
    private static async Task JsonMode()
    {
        using var sandbox = new Sandbox("json-mode");
        sandbox.Respond = (_, _) => AnthropicText("json answer");
        var (code, stdout, stderr) = await sandbox.Run("--mode", "json", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "Say hi");
        Equal(0, code, "exit code; stderr: " + stderr);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = JsonNode.Parse(lines[0])!.AsObject();
        Names(["type", "version", "id", "timestamp", "cwd"], header.Select(pair => pair.Key), "header fields");
        Equal("session", header["type"]!.GetValue<string>(), "header type");
        var types = lines.Skip(1).Select(line => JsonNode.Parse(line)!["type"]!.GetValue<string>()).ToArray();
        Check(types.First() == "agent_start" && types.Last() == "agent_settled", "event order: " + string.Join(",", types));
        Check(!types.Contains("response") && !types.Any(type => type.StartsWith("pisharp_", StringComparison.Ordinal)), "only session events: " + string.Join(",", types));
    }

    // system-prompt.ts buildSystemPromptSections with the default tools (read, bash, edit, write), joined as getSystemMessageText does.
    private static async Task DefaultSystemPrompt()
    {
        using var sandbox = new Sandbox("system-prompt");
        var (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
        Equal(0, code, "exit code; stderr: " + stderr);
        var body = sandbox.Requests[0].Json;
        var system = string.Join("\n\n", body.GetProperty("system").EnumerateArray().Select(block => block.GetProperty("text").GetString()));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "cliparity-system.txt"), system);
        Equal(ExpectedDefaultPrompt(sandbox, DefaultTools), system, "system prompt");
    }

    /// <summary>Pi's default tools (read, bash, edit, write); bash runs on Windows, Linux and macOS.</summary>
    private static string[] DefaultTools => ["read", "bash", "edit", "write"];

    /// <summary>The upstream default prompt for <paramref name="tools"/> (built-in snippets and guidelines), with no context files or skills.</summary>
    private static string ExpectedDefaultPrompt(Sandbox sandbox, string[] tools, string? projectContext = null, string? append = null)
    {
        var app = Path.Combine(sandbox.Root, "app");
        var snippets = new Dictionary<string, string>
        {
            ["read"] = "Read file contents", ["bash"] = "Execute bash commands (ls, grep, find, etc.)",
            ["edit"] = "Make precise file edits with exact text replacement, including multiple disjoint edits in one call",
            ["write"] = "Create or overwrite files", ["ls"] = "List directory contents"
        };
        var guidelines = new Dictionary<string, string[]>
        {
            ["read"] = ["Use read to examine files instead of cat or sed."],
            ["bash"] = ["You can inspect PI_* environment variables for current model and session details."],
            ["edit"] = ["Use edit for precise changes (edits[].oldText must match exactly)",
                "When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls",
                "Each edits[].oldText is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.",
                "Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions."],
            ["write"] = ["Use write only for new files or complete rewrites."]
        };
        var rules = new List<string>();
        if (tools.Contains("bash") && !tools.Any(tool => tool is "grep" or "find" or "ls")) rules.Add("Use bash for file operations like ls, rg, find");
        foreach (var tool in tools) rules.AddRange(guidelines.GetValueOrDefault(tool) ?? []);
        rules.Add("Be concise in your responses"); rules.Add("Show file paths clearly when working with files");
        var parts = new List<string>
        {
            "You are an expert coding assistant operating inside pi, a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.",
            "<tools>\n" + string.Join("\n", tools.Select(tool => $"- {tool}: {snippets[tool]}")) + "\n\nIn addition to the tools above, you may have access to other custom tools depending on the project.\n</tools>",
            "<rules>\n" + string.Join("\n", rules.Select(rule => "- " + rule)) + "\n</rules>",
            $"<docs>\nPi documentation (read only when the user asks about pi itself, its SDK, extensions, themes, skills, or TUI):\n- Main documentation: {Path.Join(app, "README.md")}\n- Additional docs: {Path.Join(app, "docs")}\n- Examples: {Path.Join(app, "examples")} (extensions, custom tools, SDK)\n- When reading pi docs or examples, resolve docs/... under Additional docs and examples/... under Examples, not the current working directory\n- When asked about: extensions (docs/extensions.md, examples/extensions/), themes (docs/themes.md), skills (docs/skills.md), prompt templates (docs/prompt-templates.md), TUI components (docs/tui.md), keybindings (docs/keybindings.md), SDK integrations (docs/sdk.md), custom providers (docs/custom-provider.md), adding models (docs/models.md), pi packages (docs/packages.md), environment variables (docs/environment-variables.md), MCP servers (docs/mcp.md), codemode scripts and non-LLM models such as classifiers and image models (docs/codemode.md)\n- When working on pi topics, read the docs and examples, and follow .md cross-references before implementing\n- Always read pi .md files completely and follow links to related docs (e.g., tui.md for TUI API details)\n</docs>"
        };
        if (append is not null) parts.Add($"<addendum>\n{append}\n</addendum>");
        if (projectContext is not null) parts.Add($"<project_context>\n{projectContext}\n</project_context>");
        parts.Add($"<cwd>\n{sandbox.Cwd.Replace('\\', '/')}\n</cwd>");
        return string.Join("\n\n", parts);
    }

    // print-mode.ts text: the last assistant message's text blocks, each followed by a newline; nothing else on stdout. main.ts:
    // a new session file under <agentDir>/sessions/--<cwd>--/<timestamp>_<uuidv7>.jsonl whose header carries that id.
    private static async Task PrintText()
    {
        using var sandbox = new Sandbox("print-text");
        sandbox.Respond = (_, _) => AnthropicText("Hello from the model");
        var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "Say hi");
        Equal(0, code, "exit code; stderr: " + stderr);
        Equal("Hello from the model\n", stdout, "stdout");
        Equal("", stderr, "stderr");
        var files = sandbox.SessionFiles();
        Equal(1, files.Length, "session files");
        var expectedDir = PiSessions.DefaultSessionDirectoryPath(sandbox.Cwd, sandbox.AgentDir);
        Equal(expectedDir, Path.GetDirectoryName(files[0]), "session directory");
        var header = JsonNode.Parse(File.ReadLines(files[0]).First())!.AsObject();
        var name = Path.GetFileNameWithoutExtension(files[0]);
        Check(name.EndsWith("_" + header["id"]!.GetValue<string>(), StringComparison.Ordinal), "file name ends with the header id: " + name);
        Equal(header["timestamp"]!.GetValue<string>().Replace(':', '-').Replace('.', '-'), name[..name.IndexOf('_')], "file timestamp");
        Equal(sandbox.Cwd, header["cwd"]!.GetValue<string>(), "header cwd");
        Equal(1, sandbox.Requests.Count, "provider requests");
        var body = sandbox.Requests[0].Json;
        Equal(64000, body.GetProperty("max_tokens").GetInt32(), "model maxTokens");
    }
}
