using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Pi;

// Authored expectations for IMPL-E (extensions, Node bridge and packages) of Pi v1.1.0: core/extensions/** (loader, runner, types,
// wrapper), core/package-manager.ts, core/pi-manifest.ts, core/resource-loader.ts (extension parts), package-manager-cli.ts,
// cli/args.ts (--extension, --no-extensions, extension flags) and cli/config-selector.ts. Expectations are written from the pinned
// sources by reading, never captured from an upstream run. Cases that run extension code need Node.js on PATH (22.13 or later for
// TypeScript) and report SKIP_NO_NODE without it; package cases that install from npm need npm and use only local tarballs and a
// local registry fixture. The only network use is the one-time npm install of the Pi 1.1.0 packages extensions run
// against (PiNodeRuntime, verified against the public registry); without it the cases run on the compatibility modules. No real credentials, no real home or agent directory.
internal static partial class Program
{
    private const string Upstream = "abe508e1b89912adde45528136c3221eb69acdd7";

    private static async Task<int> Main(string[] args)
    {
        string? report = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--report" && report is null && index + 1 < args.Length) report = Path.GetFullPath(args[++index]);
            else throw new ArgumentException("Use [--report <fresh path>].");
        }
        var cases = new List<(string Id, Func<Task> Run)>();
        cases.AddRange(PackageCases());
        cases.AddRange(DiscoveryCases());
        cases.AddRange(BridgeCases());
        cases.AddRange(CliCases());
        cases.AddRange(ProviderCases());
        cases.AddRange(RuntimeCases());
        var filter = Environment.GetEnvironmentVariable("EXTPARITY_FILTER");
        if (!string.IsNullOrEmpty(filter)) cases = [.. cases.Where(test => test.Id.Contains(filter, StringComparison.Ordinal))];
        var results = new List<object>(); var failures = 0; var skipped = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(240)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (SkipException skip) { skipped++; results.Add(new { test.Id, status = "SKIP_" + skip.Reason, reason = skip.Message }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var output = new { sourceSha = Upstream, status = "AUTHORED NATIVE; NO UPSTREAM CAPTURE", cases = cases.Count, failures, skipped,
            genuineSourceCasesCaptured = 0, results };
        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
        if (report is not null)
        {
            await using var file = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>A case that cannot run on this machine (no Node.js, no npm, no git): reported, never counted as a failure.</summary>
    internal sealed class SkipException(string reason, string message) : Exception(message) { public string Reason { get; } = reason; }
    private static readonly Lazy<string?> NodeVersion = new(() => Tool("node", "--version"));
    private static readonly Lazy<string?> NpmVersion = new(() => Tool(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", "--version"));
    private static readonly Lazy<string?> GitVersion = new(() => Tool("git", "--version"));
    private static string? Tool(string name, string argument)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(name, argument)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null) return null;
            var text = process.StandardOutput.ReadToEnd(); process.WaitForExit(30_000);
            return process.ExitCode == 0 ? text.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
    /// <summary>Cases that run extension code need Node.js (TypeScript needs 22.13 or later for module.stripTypeScriptTypes).</summary>
    private static void RequireNode()
    {
        if (NodeVersion.Value is not { } version) throw new SkipException("NO_NODE", "Node.js is not on PATH.");
        var parts = version.TrimStart('v').Split('.');
        if (int.Parse(parts[0]) < 22 || int.Parse(parts[0]) == 22 && int.Parse(parts[1]) < 13) throw new SkipException("NO_NODE", "Node.js " + version + " is older than 22.13.");
    }
    /// <summary>The Pi 1.1.0 packages for every sandbox: installed with npm from the public registry once (the production installer,
    /// verified against the registry's integrity hashes) into a machine-wide test folder. Null (and the compatibility modules) when
    /// Node, npm or the registry is unavailable.</summary>
    internal static readonly string SharedPiRuntimeDirectory = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-ext-parity-pi-runtime", PiSharp.Cli.Extensions.Pi.PiNodeRuntime.PiVersion);
    internal static readonly Lazy<Task<string?>> SharedPiRuntime = new(async () =>
    {
        if (NodeVersion.Value is null || NpmVersion.Value is null) return null;
        var (modules, fallback) = await PiSharp.Cli.Extensions.Pi.PiNodeRuntime.EnsureAsync(SharedPiRuntimeDirectory,
            name => name == "PISHARP_PI_RUNTIME_DIR" ? SharedPiRuntimeDirectory : null, Console.Error, null, null, CancellationToken.None);
        if (fallback is not null) Console.Error.WriteLine("ExtensionParity: " + fallback);
        return modules;
    });
    private static string RequirePiRuntime()
    {
        RequireNpm();
        return SharedPiRuntime.Value.GetAwaiter().GetResult() ?? throw new SkipException("NO_PI_RUNTIME", "The Pi 1.1.0 packages could not be installed (no registry access).");
    }
    private static void RequireNpm() { RequireNode(); if (NpmVersion.Value is null) throw new SkipException("NO_NPM", "npm is not on PATH."); }
    private static void RequireGit() { if (GitVersion.Value is null) throw new SkipException("NO_GIT", "git is not on PATH."); }

    private static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal), $"{what}: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}].");
    private static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }

    /// <summary>A fresh home, agent directory and project directory, with an explicit environment and fake provider.</summary>
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }
        public string Home { get; }
        public string AgentDir { get; }
        public string Cwd { get; set; }
        public Dictionary<string, string?> Vars { get; } = new(StringComparer.Ordinal);
        public List<Seen> Requests { get; } = [];
        public Func<Seen, int, HttpResponseMessage> Respond { get; set; } = (_, _) => AnthropicText("ok");
        public Sandbox(string name, bool trusted = true)
        {
            Root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-ext-parity", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Home = Path.Combine(Root, "home"); AgentDir = Path.Combine(Home, ".pi", "agent"); Cwd = Path.Combine(Root, "project");
            Directory.CreateDirectory(AgentDir); Directory.CreateDirectory(Cwd);
            Vars["ANTHROPIC_API_KEY"] = "sk-test-key";
            // Extensions run against the real Pi 1.1.0 packages, installed once per machine for the suite (PiNodeRuntime).
            if (SharedPiRuntime.Value.GetAwaiter().GetResult() is not null) Vars["PISHARP_PI_RUNTIME_DIR"] = SharedPiRuntimeDirectory;
            // The sandbox is a git repository, so the ancestor walk for .agents/skills stops at its root (collectAncestorAgentsSkillDirs).
            Directory.CreateDirectory(Path.Combine(Root, ".git"));
            File.WriteAllText(Path.Combine(Root, ".git", "HEAD"), "ref: refs/heads/main\n");
            // An ancestor of the temporary directory may hold .agents/skills (a trust-requiring resource), so the project is trusted
            // explicitly, as a user's earlier choice would; trust cases construct untrusted sandboxes.
            if (trusted) new ProjectTrustStore(AgentDir, Home).Set(Root, true);
        }
        public string Write(string relative, string text)
        {
            var path = Path.IsPathRooted(relative) ? relative : Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        public LiveSessionRuntime Runtime() => new(name => Vars.GetValueOrDefault(name), () => new Endpoint(this),
            Path.Combine(AgentDir, "auth.json"), ModelsPath: Path.Combine(AgentDir, "models.json"));
        public PiHost Host(StringWriter stdout, StringWriter stderr, string? stdin, bool interactive = false,
            Func<string[], PiEntryOptions, CancellationToken, Task<int>>? runInteractive = null, Stream? rpcInput = null, Stream? rpcOutput = null) => new()
        {
            Cwd = Cwd, Home = Home, GetEnvironment = name => Vars.GetValueOrDefault(name), SetEnvironment = (name, value) => Vars[name] = value,
            Stdout = stdout, Stderr = stderr, Stdin = new StringReader(stdin ?? ""), StdinIsTty = stdin is null, StdoutIsTty = interactive,
            LiveRuntime = Runtime(), CreateMcpHost = agentDir => new PiSharp.Cli.Mcp.McpSessionHost(agentDir, Home, () => []),
            RunInteractive = runInteractive, OpenRpcInput = rpcInput is null ? null : () => rpcInput, OpenRpcOutput = rpcOutput is null ? null : () => rpcOutput,
            ApplicationDirectory = Path.Combine(Root, "app"), Now = () => DateTimeOffset.UtcNow
        };
        public async Task<(int Code, string Out, string Err)> Run(params string[] args) => await RunWith(null, args);
        public async Task<(int Code, string Out, string Err)> RunWith(string? stdin, params string[] args)
        {
            using var stdout = new StringWriter { NewLine = "\n" }; using var stderr = new StringWriter { NewLine = "\n" };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var code = await PiCommand.RunAsync(args, Host(stdout, stderr, stdin), deadline.Token);
            return (code, stdout.ToString(), stderr.ToString());
        }
        public string[] SessionFiles() => Directory.Exists(Path.Combine(AgentDir, "sessions"))
            ? Directory.GetFiles(Path.Combine(AgentDir, "sessions"), "*.jsonl", SearchOption.AllDirectories) : [];
        public void Dispose()
        {
            // git object files are read-only; clear the attribute so the sandbox is removed.
            try { foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed record Seen(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body!).RootElement;
    }

    private sealed class Endpoint(Sandbox sandbox) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers.Concat(request.Content?.Headers.AsEnumerable() ?? [])) headers[header.Key] = string.Join(", ", header.Value);
            var seen = new Seen(request.Method.Method, request.RequestUri!.AbsoluteUri, headers,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            int index;
            lock (sandbox.Requests) { sandbox.Requests.Add(seen); index = sandbox.Requests.Count - 1; }
            return sandbox.Respond(seen, index);
        }
    }

    private static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    /// <summary>An Anthropic Messages stream answering with one text block.</summary>
    internal static HttpResponseMessage AnthropicText(string text) => Sse(
        Frame("message_start", new { type = "message_start", message = new { id = "msg_1", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(), usage = new { input_tokens = 3, output_tokens = 0 } } })
        + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
        + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })
        + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
        + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 2 } })
        + Frame("message_stop", new { type = "message_stop" }));

    /// <summary>An Anthropic Messages stream calling one tool.</summary>
    internal static HttpResponseMessage AnthropicToolCall(string name, object input, string id = "toolu_01") => Sse(
        Frame("message_start", new { type = "message_start", message = new { id = "msg_t", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(), usage = new { input_tokens = 3, output_tokens = 0 } } })
        + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id, name, input = new { } } })
        + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(input) } })
        + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
        + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 2 } })
        + Frame("message_stop", new { type = "message_stop" }));

    internal static HttpResponseMessage AnthropicError(int status, string message) => new((HttpStatusCode)status)
    { Content = new StringContent(JsonSerializer.Serialize(new { type = "error", error = new { type = "invalid_request_error", message } }), Encoding.UTF8, "application/json") };
}
