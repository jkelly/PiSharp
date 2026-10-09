using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Pi;

/// <summary>A fresh home, agent directory and project with a fake Anthropic endpoint, running the Pi entry in interactive mode on
/// a <see cref="VirtualTerminal"/>. Nothing touches the network, the real home or the real terminal.</summary>
internal sealed class InteractiveHarness : IAsyncDisposable
{
    public string Root { get; }
    public string Home { get; }
    public string AgentDir { get; }
    public string Cwd { get; }
    public Dictionary<string, string?> Vars { get; } = new(StringComparer.Ordinal);
    public List<string> Requests { get; } = [];
    public Func<string, int, HttpResponseMessage> Respond { get; set; } = (_, _) => AnthropicText("Hello from the fake model.");
    public VirtualTerminal Terminal { get; }
    public StringWriter Stdout { get; } = new() { NewLine = "\n" };
    public StringWriter Stderr { get; } = new() { NewLine = "\n" };
    public InteractiveMode? Mode { get; private set; }
    /// <summary>Text the mode copied to the clipboard (the fake clipboard).</summary>
    public List<string> Copied { get; } = [];
    public string? ClipboardText { get; set; }
    /// <summary>Further context changes for a case (applied after the harness defaults).</summary>
    public Func<InteractiveModeContext, InteractiveModeContext>? Configure { get; set; }

    private InteractiveModeContext ConfigureContext(InteractiveModeContext context)
    {
        var configured = context with
        {
            CopyToClipboard = text => { lock (Copied) Copied.Add(text); return Task.CompletedTask; },
            ReadClipboardText = () => Task.FromResult(ClipboardText),
            ReadClipboardFilePaths = () => Task.FromResult<IReadOnlyList<string>?>(null),
            ReadClipboardImage = () => Task.FromResult<PiSharp.Cli.Interactive.Mode.Utilities.ClipboardImage?>(null),
            OpenUrl = _ => { },
            CheckForNewVersion = _ => Task.FromResult<PiSharp.Cli.Interactive.Mode.Utilities.LatestPiRelease?>(null),
            ParseChangelogEntries = () => [],
            GetEnvironment = name => Vars.GetValueOrDefault(name),
            Login = null
        };
        return Configure?.Invoke(configured) ?? configured;
    }
    private Task<int>? run;
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(150));

    public InteractiveHarness(string name, int columns = 100, int rows = 30, bool trusted = true)
    {
        Root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-interactive-e2e", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Home = Path.Combine(Root, "home"); AgentDir = Path.Combine(Home, ".pi", "agent"); Cwd = Path.Combine(Root, "project");
        Directory.CreateDirectory(AgentDir); Directory.CreateDirectory(Cwd);
        Directory.CreateDirectory(Path.Combine(Root, ".git"));
        File.WriteAllText(Path.Combine(Root, ".git", "HEAD"), "ref: refs/heads/main\n");
        if (trusted) new ProjectTrustStore(AgentDir, Home).Set(Root, true);
        Vars["ANTHROPIC_API_KEY"] = "sk-test-key";
        Vars["PI_CODING_AGENT_DIR"] = AgentDir;
        Vars["PI_OFFLINE"] = "1";
        Vars["PATH"] = Environment.GetEnvironmentVariable("PATH");
        Vars["PATHEXT"] = Environment.GetEnvironmentVariable("PATHEXT");
        Terminal = new VirtualTerminal(columns, rows);
    }

    public string Write(string relative, string text)
    {
        var path = Path.IsPathRooted(relative) ? relative : Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private sealed class Endpoint(InteractiveHarness harness) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            int index;
            lock (harness.Requests) { harness.Requests.Add(body); index = harness.Requests.Count - 1; }
            return harness.Respond(body, index);
        }
    }

    /// <summary>Starts <c>pi</c> with <paramref name="args"/> in interactive mode.</summary>
    public void Start(params string[] args)
    {
        Themes.Environment = name => Vars.GetValueOrDefault(name);
        var runtime = new LiveSessionRuntime(name => Vars.GetValueOrDefault(name), () => new Endpoint(this), Path.Combine(AgentDir, "auth.json"),
            ModelsPath: Path.Combine(AgentDir, "models.json"));
        var host = new PiHost
        {
            Cwd = Cwd, Home = Home, GetEnvironment = name => Vars.GetValueOrDefault(name), SetEnvironment = (name, value) => Vars[name] = value,
            Stdout = Stdout, Stderr = Stderr, Stdin = new StringReader(""), StdinIsTty = true, StdoutIsTty = true,
            LiveRuntime = runtime, CreateMcpHost = agentDir => new PiSharp.Cli.Mcp.McpSessionHost(agentDir, Home, () => []),
            RunInteractive = (terminalArgs, options, token) => InteractiveModeHost.RunAsync(terminalArgs, options,
                new PiSharp.Cli.Mcp.McpSessionHost(AgentDir, Home, () => []), new McpBinding(), Stdout, Stderr, token,
                loop => new PiSharp.Tui.Pi.ProcessTerminal(loop, Terminal, name => Vars.GetValueOrDefault(name)), mode => Mode = mode, ConfigureContext),
            ApplicationDirectory = Path.Combine(Root, "app"), Now = () => DateTimeOffset.UtcNow
        };
        run = Task.Run(() => PiCommand.RunAsync(args, host, deadline.Token));
    }

    public Task<string> WaitFor(string text, int timeoutMs = 30_000) => Run(() => Terminal.WaitForTextAsync(text, timeoutMs));
    public Task<string> WaitUntil(Func<string, bool> predicate, string what, int timeoutMs = 30_000) => Run(() => Terminal.WaitForAsync(predicate, what, timeoutMs));

    private async Task<string> Run(Func<Task<string>> wait)
    {
        var waiting = wait();
        if (run is not null && await Task.WhenAny(waiting, run) == run && !waiting.IsCompleted)
            throw new InvalidOperationException($"pi exited with {await run} before the screen matched. stderr:\n{Stderr}\nScreen:\n{Terminal.Text}");
        return await waiting;
    }

    public void Type(string text) => Terminal.Send(text);
    public async Task Submit(string text)
    {
        Terminal.Send(text);
        await Task.Delay(30);
        Terminal.Send("\r");
    }

    /// <summary>Quits with Ctrl+D on an empty editor and returns the exit code.</summary>
    public async Task<int> Quit()
    {
        Terminal.Send("\u0004");
        return await Exit();
    }

    public async Task<int> Exit()
    {
        if (run is null) throw new InvalidOperationException("Not started.");
        var finished = await Task.WhenAny(run, Task.Delay(60_000));
        if (finished != run) throw new TimeoutException($"pi did not exit. Screen:\n{Terminal.Text}\nstderr:\n{Stderr}");
        return await run;
    }

    public string[] SessionFiles() => Directory.Exists(Path.Combine(AgentDir, "sessions"))
        ? Directory.GetFiles(Path.Combine(AgentDir, "sessions"), "*.jsonl", SearchOption.AllDirectories) : [];

    public async ValueTask DisposeAsync()
    {
        if (run is { IsCompleted: false })
        {
            Terminal.Send("\u0003"); Terminal.Send("\u0003");
            deadline.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(30)); } catch { }
        }
        deadline.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    public static HttpResponseMessage AnthropicText(string text) => Sse(
        Frame("message_start", new { type = "message_start", message = new { id = "msg_1", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(), usage = new { input_tokens = 3, output_tokens = 0 } } })
        + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
        + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })
        + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
        + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 2 } })
        + Frame("message_stop", new { type = "message_stop" }));

    public static HttpResponseMessage AnthropicToolCall(string name, object input, string id = "toolu_01") => Sse(
        Frame("message_start", new { type = "message_start", message = new { id = "msg_t", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(), usage = new { input_tokens = 3, output_tokens = 0 } } })
        + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id, name, input = new { } } })
        + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(input) } })
        + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
        + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 2 } })
        + Frame("message_stop", new { type = "message_stop" }));
}
