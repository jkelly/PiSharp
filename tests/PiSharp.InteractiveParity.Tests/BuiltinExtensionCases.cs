// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/cli/config-selector.ts and
// packages/coding-agent/src/modes/interactive/components/config-selector.ts (the Built-in group: getGroupLabel, displayName without
// the builtin: prefix, toggleTopLevelResource writing -builtin:<name>), packages/coding-agent/src/package-manager-cli.ts
// (handleConfigCommand passes the built-in extension names) and packages/coding-agent/src/extensions/index.ts (a built-in extension
// that is not loaded registers no /llama or /mcp command).
using static Expect;

/// <summary>Built-in extensions in interactive mode: <c>pisharp config</c> lists and toggles them, and one that is disabled offers no
/// command.</summary>
internal static class BuiltinExtensionCases
{
    private static readonly string[] Regular = ["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular"];

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // pi config: the built-in extensions form the "Built-in" group, named without the builtin: prefix; space writes
        // -builtin:<name> to the global extensions setting.
        yield return ("e2e.builtins.config-lists-and-toggles-built-in-extensions", async () =>
        {
            await using var pi = new InteractiveHarness("builtins-config");
            var host = new PiSharp.Cli.Pi.PiHost
            {
                Cwd = pi.Cwd, Home = pi.Home, GetEnvironment = name => pi.Vars.GetValueOrDefault(name), Stdout = pi.Stdout, Stderr = pi.Stderr,
                Stdin = new StringReader(""), StdinIsTty = true, StdoutIsTty = true, LiveRuntime = PiSharp.Cli.Commands.LiveSessionRuntime.Default,
                ConfigSelector = (request, token) => new PiSharp.Cli.Interactive.Mode.StartupUi(request.AgentDir, request.Cwd, name => pi.Vars.GetValueOrDefault(name),
                    loop => new PiSharp.Tui.Pi.ProcessTerminal(loop, pi.Terminal, name => pi.Vars.GetValueOrDefault(name))).SelectConfigAsync(request, token)
            };
            var run = Task.Run(() => PiSharp.Cli.Pi.PiCommand.RunAsync(["config"], host, CancellationToken.None));
            await pi.WaitFor("tool-search");
            var screen = pi.Terminal.Text;
            Contains(screen, "Built-in", "the Built-in group");
            foreach (var name in new[] { "codemode", "llama.cpp", "mcp", "tool-search" }) Contains(screen, name, "built-in extension " + name);
            Check(!screen.Contains("builtin:", StringComparison.Ordinal), "names without the builtin: prefix:\n" + screen);
            // The first item (codemode, by name) is selected: space disables it.
            pi.Type(" ");
            var settings = Path.Combine(pi.AgentDir, "settings.json");
            await pi.WaitUntil(_ => File.Exists(settings) && File.ReadAllText(settings).Contains("-builtin:codemode", StringComparison.Ordinal), "-builtin:codemode written");
            // Space again enables it: +builtin:codemode replaces the entry.
            pi.Type(" ");
            await pi.WaitUntil(_ => File.ReadAllText(settings).Contains("+builtin:codemode", StringComparison.Ordinal) &&
                !File.ReadAllText(settings).Contains("-builtin:codemode", StringComparison.Ordinal), "+builtin:codemode written");
            pi.Type("\u001b");
            Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30)), "config exits 0 after closing: " + pi.Stderr);
        });

        // A project override shows in its own group (Built-in (project override)).
        yield return ("e2e.builtins.config-project-override-group", async () =>
        {
            await using var pi = new InteractiveHarness("builtins-config-project");
            pi.Write(Path.Combine(pi.Cwd, ".pi", "settings.json"), """{"extensions":["-builtin:mcp"]}""");
            var host = new PiSharp.Cli.Pi.PiHost
            {
                Cwd = pi.Cwd, Home = pi.Home, GetEnvironment = name => pi.Vars.GetValueOrDefault(name), Stdout = pi.Stdout, Stderr = pi.Stderr,
                Stdin = new StringReader(""), StdinIsTty = true, StdoutIsTty = true, LiveRuntime = PiSharp.Cli.Commands.LiveSessionRuntime.Default,
                ConfigSelector = (request, token) => new PiSharp.Cli.Interactive.Mode.StartupUi(request.AgentDir, request.Cwd, name => pi.Vars.GetValueOrDefault(name),
                    loop => new PiSharp.Tui.Pi.ProcessTerminal(loop, pi.Terminal, name => pi.Vars.GetValueOrDefault(name))).SelectConfigAsync(request, token)
            };
            var run = Task.Run(() => PiSharp.Cli.Pi.PiCommand.RunAsync(["config", "--approve"], host, CancellationToken.None));
            await pi.WaitFor("tool-search");
            // Project mode (tab) lists the project resolution, where the project entry overrides the built-in extension.
            pi.Type("\t");
            await pi.WaitFor("Built-in (project override)");
            Contains(pi.Terminal.Text, "Project Local Resources", "project mode");
            pi.Type("\u001b");
            Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30)), "config exits 0 after closing: " + pi.Stderr);
        });

        // Without the llama.cpp and mcp extensions /llama and /mcp are no commands: they are not offered and go to the model.
        yield return ("e2e.builtins.disabled-commands-are-prompts", async () =>
        {
            await using var pi = new InteractiveHarness("builtins-commands");
            pi.Write(Path.Combine(pi.AgentDir, "settings.json"), """{"extensions":["-builtin:llama.cpp","-builtin:mcp"]}""");
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5", StringComparison.Ordinal), "footer");
            // The editor's autocomplete starts once the session's commands are read: type again until the list shows.
            async Task Complete(string typed, string expected)
            {
                for (var attempt = 0; ; attempt++)
                {
                    pi.Type(""); pi.Type(typed);
                    try { await pi.WaitFor(expected, 5_000); return; }
                    catch (TimeoutException) when (attempt < 5) { }
                }
            }
            await Complete("/l", "Configure provider authentication");
            Check(!pi.Terminal.Text.Contains("Manage llama.cpp router models", StringComparison.Ordinal), "no /llama completion:\n" + pi.Terminal.Text);
            pi.Type("\u001b");
            await Task.Delay(100);
            pi.Type("\u0015");
            await Complete("/m", "Select model (opens selector UI)");
            Check(!pi.Terminal.Text.Contains("Manage MCP servers", StringComparison.Ordinal), "no /mcp completion:\n" + pi.Terminal.Text);
            pi.Type("\u001b");
            await Task.Delay(100);
            pi.Type("\u0015");
            await pi.Submit("/llama");
            await pi.WaitFor("Hello from the fake model.");
            lock (pi.Requests) Check(pi.Requests.Count == 1 && pi.Requests[0].Contains("/llama", StringComparison.Ordinal), "/llama went to the model");
            Check(!pi.Terminal.Text.Contains("Configure llama.cpp with /login", StringComparison.Ordinal), "the llama.cpp command did not run");
            await pi.Submit("/mcp");
            await pi.WaitUntil(_ => { lock (pi.Requests) return pi.Requests.Count == 2; }, "/mcp went to the model");
            Check(!pi.Terminal.Text.Contains("MCP servers are not available", StringComparison.Ordinal), "the mcp command did not run");
        });
    }
}
