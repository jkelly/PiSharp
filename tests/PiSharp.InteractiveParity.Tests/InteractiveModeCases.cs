using static Expect;

/// <summary>interactive-mode.ts end to end: the Pi entry in interactive mode on a virtual terminal against a fake provider.</summary>
internal static class InteractiveModeCases
{
    private static readonly string[] Model = ["--provider", "anthropic", "--model", "claude-sonnet-4-5"];
    private static readonly string[] Regular = [.. Model, "--tui-mode", "regular"];

    /// <summary>Starts pi in regular mode and waits for the startup header.</summary>
    private static async Task<InteractiveHarness> Started(string name, Action<InteractiveHarness>? setup = null, params string[] extra)
    {
        var pi = new InteractiveHarness(name);
        setup?.Invoke(pi);
        pi.Start([.. Regular, .. extra]);
        await pi.WaitFor("escape interrupt");
        await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5", StringComparison.Ordinal), "footer");
        return pi;
    }

    private static Func<Task> Case(string name, Func<InteractiveHarness, Task> body, Action<InteractiveHarness>? setup = null, params string[] extra) => async () =>
    {
        await using var pi = await Started(name, setup, extra);
        await body(pi);
    };

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("e2e.regular.startup-prompt-quit", async () =>
        {
            await using var pi = new InteractiveHarness("startup");
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("hello there");
            await pi.WaitFor("Hello from the fake model.");
            Contains(pi.Terminal.Text, "hello there", "user message shown");
            Equal(0, await pi.Quit(), "exit code");
            Contains(pi.Stdout.ToString(), "To resume this session:", "resume hint");
            Equal(1, pi.SessionFiles().Length, "one session file");
        });

        yield return ("e2e.fullscreen.startup-prompt-transcript", async () =>
        {
            await using var pi = new InteractiveHarness("fullscreen");
            pi.Start(Model);
            await pi.WaitFor("escape interrupt");
            Check(pi.Terminal.RawOutput.Contains("\u001b[?1049h", StringComparison.Ordinal), "alternate screen entered (fullscreen is the default)");
            await pi.Submit("hello fullscreen");
            await pi.WaitFor("Hello from the fake model.");
            Equal(0, await pi.Quit(), "exit code");
            Check(pi.Terminal.RawOutput.Contains("\u001b[?1049l", StringComparison.Ordinal), "alternate screen left");
            // fullscreenExitOutput "transcript": the conversation is printed to the main screen on exit.
            Contains(pi.Terminal.Text, "Hello from the fake model.", "transcript after exit");
        });

        yield return ("e2e.slash.session-info", Case("session", async pi =>
        {
            await pi.Submit("/session");
            await pi.WaitFor("Session Info");
            Contains(pi.Terminal.Text, "Messages", "messages section");
            Contains(pi.Terminal.Text, "Cache Warming", "cache warming section");
        }));

        yield return ("e2e.slash.hotkeys", async () =>
        {
            await using var pi = new InteractiveHarness("hotkeys", columns: 100, rows: 80);
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("/hotkeys");
            await pi.WaitFor("Keyboard Shortcuts");
            await pi.WaitFor("Open model selector");
        });

        yield return ("e2e.slash.name", Case("name", async pi =>
        {
            await pi.Submit("/name My session");
            await pi.WaitFor("Session name set: My session");
            await pi.Submit("/name");
            await pi.WaitFor("Session name: My session");
        }));

        yield return ("e2e.slash.thinking", Case("thinking", async pi =>
        {
            // The argument autocomplete takes the first Enter (it applies "high"), as in upstream; the second submits.
            pi.Type("/thinking high");
            await pi.WaitFor("/thinking high");
            await Task.Delay(300);
            pi.Type("\r");
            await Task.Delay(300);
            pi.Type("\r");
            await pi.WaitFor("Thinking level: high");
            await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5 • high", StringComparison.Ordinal), "footer thinking level");
            pi.Type("/thinking nonsense");
            await Task.Delay(300);
            pi.Type("\r");
            await pi.WaitFor("Unknown thinking level \"nonsense\"");
        }));

        yield return ("e2e.slash.new-session", Case("new", async pi =>
        {
            await pi.Submit("first message");
            await pi.WaitFor("Hello from the fake model.");
            await pi.Submit("/new");
            await pi.WaitFor("✓ New session started");
        }));

        yield return ("e2e.slash.copy", Case("copy", async pi =>
        {
            await pi.Submit("/copy");
            await pi.WaitFor("No agent messages to copy yet.");
            await pi.Submit("say something");
            await pi.WaitFor("Hello from the fake model.");
            await pi.Submit("/copy");
            await pi.WaitFor("Copied last agent message to clipboard");
            Equal("Hello from the fake model.", pi.Copied.LastOrDefault(), "copied text");
        }));

        yield return ("e2e.slash.export-html-and-jsonl", Case("export", async pi =>
        {
            await pi.Submit("export me");
            await pi.WaitFor("Hello from the fake model.");
            var html = Path.Combine(pi.Root, "out.html");
            await pi.Submit($"/export \"{html}\"");
            await pi.WaitFor("Session exported to:");
            Check(File.Exists(html), "html written");
            var jsonl = Path.Combine(pi.Root, "out.jsonl");
            await pi.Submit($"/export \"{jsonl}\"");
            await pi.WaitUntil(text => File.Exists(jsonl), "jsonl written");
        }));

        yield return ("e2e.bash.command", Case("bash", async pi =>
        {
            await pi.Submit("!echo interactive-bash-ok");
            await pi.WaitFor("interactive-bash-ok");
        }));

        yield return ("e2e.selector.model-escape", Case("model", async pi =>
        {
            await pi.Submit("/model");
            await pi.WaitFor("Only showing models");
            pi.Type("\u001b");
            await pi.WaitUntil(text => !text.Contains("Only showing models", StringComparison.Ordinal), "selector closed");
        }));

        yield return ("e2e.selector.settings-escape", Case("settings", async pi =>
        {
            await pi.Submit("/settings");
            await pi.WaitFor("Auto-compact");
            pi.Type("\u001b");
            await pi.WaitUntil(text => !text.Contains("Auto-compact", StringComparison.Ordinal), "settings closed");
        }));

        yield return ("e2e.selector.tree-and-fork", Case("tree", async pi =>
        {
            await pi.Submit("tree message");
            await pi.WaitFor("Hello from the fake model.");
            await pi.Submit("/tree");
            await pi.WaitFor("Session Tree");
            pi.Type("\u001b");
            await Task.Delay(200);
            await pi.Submit("/fork");
            await pi.WaitFor("Fork from Message");
            pi.Type("\r");
            await pi.WaitFor("Forked to new session");
        }));

        yield return ("e2e.selector.resume", Case("resume", async pi =>
        {
            await pi.Submit("resume me");
            await pi.WaitFor("Hello from the fake model.");
            await pi.Submit("/resume");
            await pi.WaitFor("Resume Session");
            pi.Type("\u001b");
            await pi.WaitUntil(text => !text.Contains("Resume Session", StringComparison.Ordinal), "resume closed");
        }));

        yield return ("e2e.slash.logout-nothing-stored", Case("logout", async pi =>
        {
            await pi.Submit("/logout");
            await pi.WaitUntil(text => text.Contains("No stored credentials", StringComparison.Ordinal) || text.Contains("Could not read stored credentials", StringComparison.Ordinal), "logout message");
        }));

        yield return ("e2e.slash.reload", Case("reload", async pi =>
        {
            await pi.Submit("/reload");
            await pi.WaitUntil(text => text.Contains("Reloaded keybindings", StringComparison.Ordinal) || text.Contains("Reload failed", StringComparison.Ordinal), "reload finished");
        }));

        yield return ("e2e.slash.debug-log", Case("debug", async pi =>
        {
            await pi.Submit("/debug");
            await pi.WaitFor("✓ Debug log written");
            Check(File.Exists(Path.Combine(pi.AgentDir, "pi-debug.log")), "debug log file");
        }));

        yield return ("e2e.slash.changelog-empty", Case("changelog", async pi =>
        {
            await pi.Submit("/changelog");
            await pi.WaitFor("No changelog entries found.");
        }));

        yield return ("e2e.slash.quit", Case("quit", async pi =>
        {
            await pi.Submit("/quit");
            Equal(0, await pi.Exit(), "exit code");
        }));

        yield return ("e2e.keys.ctrl-c-twice-exits", Case("ctrlc", async pi =>
        {
            pi.Type("some draft");
            await pi.WaitFor("some draft");
            pi.Type("\u0003");
            await pi.WaitUntil(text => !text.Contains("some draft", StringComparison.Ordinal), "editor cleared");
            pi.Type("\u0003"); pi.Type("\u0003");
            Equal(0, await pi.Exit(), "exit code");
        }));

        yield return ("e2e.tool.read-call-renders", Case("tool", async pi =>
        {
            File.WriteAllText(Path.Combine(pi.Cwd, "notes.txt"), "alpha\nbeta\n");
            pi.Respond = (_, index) => index == 0
                ? InteractiveHarness.AnthropicToolCall("read", new { path = "notes.txt" })
                : InteractiveHarness.AnthropicText("Read it.");
            await pi.Submit("read the notes");
            await pi.WaitFor("Read it.");
            Contains(pi.Terminal.Text, "read notes.txt", "tool call row");
        }));

        yield return ("e2e.settings.quiet-startup", async () =>
        {
            await using var pi = new InteractiveHarness("quiet");
            pi.Write(Path.Combine(pi.AgentDir, "settings.json"), "{\"quietStartup\":true}");
            pi.Start(Regular);
            await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5", StringComparison.Ordinal), "footer");
            await Task.Delay(300);
            Check(!pi.Terminal.Text.Contains("escape interrupt", StringComparison.Ordinal), "header hidden with quietStartup true");
        });

        yield return ("e2e.settings.quiet-startup-header-keeps-header", async () =>
        {
            await using var pi = new InteractiveHarness("quiet-header");
            pi.Write(Path.Combine(pi.AgentDir, "settings.json"), "{\"quietStartup\":\"header\"}");
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            Check(!pi.Terminal.Text.Contains("and loaded resources", StringComparison.Ordinal), "details hidden with quietStartup header");
        });

        yield return ("e2e.autocomplete.slash-commands", Case("autocomplete", async pi =>
        {
            pi.Type("/hot");
            await pi.WaitFor("Show all keyboard shortcuts");
            pi.Type("\t");
            await pi.WaitUntil(text => text.Contains("/hotkeys", StringComparison.Ordinal), "completed command");
        }));
    }
}
