using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

// core/trust-manager.ts and core/project-trust.ts (ported from test/trust-manager.test.ts), and decision 0004's tool gates: the pi
// policy (any path, any command) and the explicit policy, with session files and extension files protected in both.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> TrustCases() =>
    [
        ("trust.store-inherits-from-parents-and-writes-sorted-json", Sync(() =>
        {
            using var sandbox = new Sandbox("trust-store", trusted: false);
            var store = new ProjectTrustStore(sandbox.AgentDir, sandbox.Home);
            var parent = Path.Combine(sandbox.Root, "trusted-parent"); var child = Path.Combine(parent, "project");
            Directory.CreateDirectory(child);
            Equal(null, store.Get(child), "undecided");
            store.Set(parent, true); Equal(true, store.Get(child), "inherits the parent");
            store.Set(child, false); Equal(false, store.Get(child), "own decision");
            store.Set(child, null); Equal(true, store.Get(child), "cleared falls back to the parent");
            var text = File.ReadAllText(store.TrustPath);
            Equal("{\n  " + JsonSerializer.Serialize(PiPaths.Canonicalize(parent)) + ": true\n}\n", text, "trust.json bytes");
            Check(!Directory.Exists(store.TrustPath + ".lock"), "lock released");
            File.WriteAllText(store.TrustPath, "{\"x\": 1}");
            Equal($"Invalid trust store {store.TrustPath}: value for \"x\" must be true, false, or null",
                Throws<InvalidDataException>(() => store.Get(child), "bad value").Message, "invalid value text");
            File.WriteAllText(store.TrustPath, "[]");
            Equal($"Invalid trust store {store.TrustPath}: expected an object", Throws<InvalidDataException>(() => store.Get(child), "array").Message, "array text");
        })),
        ("trust.detects-trust-requiring-project-resources", Sync(() =>
        {
            using var sandbox = new Sandbox("trust-detect", trusted: false);
            var home = Path.Combine(sandbox.Root, "home2"); var cwd = Path.Combine(home, "project");
            Directory.CreateDirectory(Path.Combine(home, ".pi", "agent")); Directory.CreateDirectory(Path.Combine(home, ".agents", "skills")); Directory.CreateDirectory(cwd);
            // The sandbox root is a git repository without .agents; ancestors above it may hold their own, so detection runs below a probe root.
            var probe = ProjectTrustStore.HasTrustRequiringProjectResources(home, home);
            Equal(probe, ProjectTrustStore.HasTrustRequiringProjectResources(cwd, home), "the user's own ~/.agents/skills is ignored (as for its parent)");
            if (!probe)
            {
                File.WriteAllText(Path.Combine(home, ".pi", "settings.json"), "{}");
                Equal(true, ProjectTrustStore.HasTrustRequiringProjectResources(home, home), ".pi/settings.json requires trust");
                File.Delete(Path.Combine(home, ".pi", "settings.json"));
                Directory.CreateDirectory(Path.Combine(cwd, ".pi")); File.WriteAllText(Path.Combine(cwd, ".pi", "settings.json"), "{}");
                Equal(true, ProjectTrustStore.HasTrustRequiringProjectResources(cwd, home), "project settings require trust");
                Directory.Delete(Path.Combine(cwd, ".pi"), true);
                Directory.CreateDirectory(Path.Combine(cwd, ".agents", "skills"));
                Equal(true, ProjectTrustStore.HasTrustRequiringProjectResources(cwd, home), "project .agents/skills require trust");
            }
            foreach (var entry in new[] { "mcp.json", "SYSTEM.md", "APPEND_SYSTEM.md" })
            {
                var other = Path.Combine(sandbox.Root, "p-" + entry); Directory.CreateDirectory(Path.Combine(other, ".pi"));
                File.WriteAllText(Path.Combine(other, ".pi", entry), "x");
                Equal(true, ProjectTrustStore.HasTrustRequiringProjectResources(other, home), entry + " requires trust");
            }
        })),
        ("trust.resolution-override-store-default-and-prompt", async () =>
        {
            using var sandbox = new Sandbox("trust-resolve", trusted: false);
            Directory.CreateDirectory(Path.Combine(sandbox.Cwd, ".pi")); File.WriteAllText(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), "{}");
            var store = new ProjectTrustStore(sandbox.AgentDir, sandbox.Home);
            Task<bool> Resolve(bool? forced, string defaults, PiProjectTrustPrompt? prompt = null) =>
                PiProjectTrust.ResolveAsync(sandbox.Cwd, sandbox.Home, store, forced, defaults, prompt, CancellationToken.None);
            Equal(true, await Resolve(true, "never"), "--approve");
            Equal(false, await Resolve(false, "always"), "--no-approve");
            Equal(false, await Resolve(null, "ask"), "no UI: untrusted");
            Equal(true, await Resolve(null, "always"), "defaultProjectTrust always");
            Equal(false, await Resolve(null, "never"), "defaultProjectTrust never");
            string? title = null; string[]? labels = null;
            Equal(true, await Resolve(null, "ask", (text, options, _) =>
            {
                title = text; labels = [.. options.Select(option => option.Label)];
                return Task.FromResult<ProjectTrustOption?>(options[1]);
            }), "prompt answer");
            var canonical = PiPaths.Canonicalize(sandbox.Cwd); var parent = Path.GetDirectoryName(canonical)!;
            Equal($"Trust project folder?\n{sandbox.Cwd}\n\nThis allows pi to load .pi settings and resources, install missing project packages, and execute project extensions.", title, "prompt text");
            Names(["Trust", $"Trust parent folder ({parent})", "Trust (this session only)", "Do not trust", "Do not trust (this session only)"], labels!, "options");
            Equal(true, store.Get(sandbox.Cwd), "the parent decision was saved");
            Equal(true, await Resolve(null, "never"), "a stored decision wins over defaultProjectTrust");
        }),
        ("trust.untrusted-project-ignores-project-files-and-falls-back-to-explicit-tools", async () =>
        {
            using var sandbox = new Sandbox("trust-cli", trusted: false);
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "SYSTEM.md"), "PROJECT SYSTEM PROMPT");
            var outside = sandbox.Write("outside.txt", "outside secret");
            sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("read", new { path = outside }) : AnthropicText("done");
            var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "read it");
            Equal(0, code, "exit; " + stderr);
            Equal("done\n", stdout, "answer");
            var system = sandbox.Requests[0].Json.GetProperty("system")[0].GetProperty("text").GetString()!;
            Check(!system.Contains("PROJECT SYSTEM PROMPT", StringComparison.Ordinal), "untrusted .pi/SYSTEM.md ignored");
            Check(!ToolResultText(sandbox.Requests[1]).Contains("outside secret", StringComparison.Ordinal), "explicit policy: no read grant");
            // --approve trusts the project for the run: the project prompt applies and the pi policy reads the file.
            sandbox.Requests.Clear();
            (code, _, stderr) = await sandbox.Run("-p", "--approve", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "read it");
            Equal(0, code, "approved exit; " + stderr);
            Equal("PROJECT SYSTEM PROMPT", sandbox.Requests[0].Json.GetProperty("system")[0].GetProperty("text").GetString()!.Split("\n\n")[0], "project SYSTEM.md replaces the preamble");
            Check(ToolResultText(sandbox.Requests[1]).Contains("outside secret", StringComparison.Ordinal), "pi policy reads outside the workspace");
        }),
        ("tools.pi-policy-reads-and-writes-anywhere-but-protects-sessions", async () =>
        {
            using var sandbox = new Sandbox("pi-policy");
            var outside = sandbox.Write("elsewhere/notes.txt", "outside text");
            var written = Path.Combine(sandbox.Root, "elsewhere", "new.txt");
            string? sessionFile = null;
            var calls = new List<(string Name, Func<object> Input)>
            {
                ("read", () => new { path = outside }),
                ("write", () => new { path = written, content = "made by the model" }),
                ("read", () => new { path = sessionFile = sandbox.SessionFiles().FirstOrDefault() ?? "missing" }),
                ("write", () => new { path = Path.Combine(sandbox.AgentDir, "sessions", "planted.jsonl"), content = "{}" })
            };
            sandbox.Respond = (_, index) => index >= calls.Count ? AnthropicText("finished") : AnthropicToolCall(calls[index].Name, calls[index].Input(), "toolu_0" + index);
            var (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "work");
            Equal(0, code, "exit; " + stderr + "; requests " + sandbox.Requests.Count + "; last " + (sandbox.Requests.Count > 0 ? ToolResultText(sandbox.Requests[^1]) : ""));
            Check(sessionFile is not (null or "missing"), "the session file exists while the run continues");
            Check(ToolResultText(sandbox.Requests[1]).Contains("outside text", StringComparison.Ordinal), "read outside the workspace: " + ToolResultText(sandbox.Requests[1]));
            Equal("made by the model", File.ReadAllText(written), "write outside the workspace");
            Check(!ToolResultText(sandbox.Requests[3]).Contains("\"type\":\"session\"", StringComparison.Ordinal), "the active session file is protected: " + ToolResultText(sandbox.Requests[3]));
            Check(!File.Exists(Path.Combine(sandbox.AgentDir, "sessions", "planted.jsonl")), "session files in the sessions directory are protected");
        }),
        ("tools.explicit-policy-flag-denies-without-grants", async () =>
        {
            using var sandbox = new Sandbox("explicit-policy");
            var inside = sandbox.Write(Path.Combine(sandbox.Cwd, "inside.txt"), "inside text");
            sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("read", new { path = inside }) : AnthropicText("done");
            var (code, _, stderr) = await sandbox.Run("-p", "--tool-policy", "explicit", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "read");
            Equal(0, code, "exit; " + stderr);
            Check(!ToolResultText(sandbox.Requests[1]).Contains("inside text", StringComparison.Ordinal), "explicit policy denies even workspace reads without a grant");
            var tools = sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray();
            Check(!tools.Contains("bash"), "no bash under the explicit policy: " + string.Join(",", tools));
        }),
        ("tools.pi-policy-bash-runs-any-command-with-the-full-environment", async () =>
        {
            if (!OperatingSystem.IsWindows()) return; // The native process layer is Windows-only in this build (see the report).
            using var sandbox = new Sandbox("pi-bash");
            Environment.SetEnvironmentVariable("CLIPARITY_MARKER", "marker-value");
            try
            {
                sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("bash", new { command = "echo $CLIPARITY_MARKER; echo $PI_CODING_AGENT; pwd" }) : AnthropicText("done");
                var (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "run");
                Equal(0, code, "exit; " + stderr);
                var result = ToolResultText(sandbox.Requests[1]);
                Check(result.Contains("marker-value", StringComparison.Ordinal), "full environment: " + result);
            }
            finally { Environment.SetEnvironmentVariable("CLIPARITY_MARKER", null); }
        }),
    ];

    /// <summary>The tool result text an Anthropic request carries back to the model.</summary>
    private static string ToolResultText(Seen request)
    {
        var messages = request.Json.GetProperty("messages");
        var last = messages[messages.GetArrayLength() - 1];
        return last.GetRawText();
    }
}
