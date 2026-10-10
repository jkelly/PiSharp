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

        // agent-session-runtime.ts newSession recreates the session through createAgentSession: a new session has no thinking entry,
        // so it takes the per-model or global default (defaultThinkingLevel, else medium) and records it. /thinking <level> does not
        // persist, so it does not carry over.
        foreach (var (id, configured, expected) in new[] { ("default", (string?)null, "medium"), ("settings-default", "low", "low") })
            yield return ($"e2e.slash.new-session-keeps-default-thinking-{id}", Case("new-thinking", async pi =>
            {
                await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5 • " + expected, StringComparison.Ordinal), "startup thinking level");
                pi.Type("/thinking high");
                await pi.WaitFor("/thinking high");
                await Task.Delay(300);
                pi.Type("\r");
                await Task.Delay(300);
                pi.Type("\r");
                await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5 • high", StringComparison.Ordinal), "footer thinking level");
                await pi.Submit("/new");
                await pi.WaitFor("✓ New session started");
                await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5 • " + expected, StringComparison.Ordinal), "default thinking level after /new");
            }, setup: pi =>
            {
                if (configured is not null) File.WriteAllText(Path.Combine(pi.AgentDir, "settings.json"), $$"""{"defaultThinkingLevel":"{{configured}}"}""");
            }));
        // Issue #5: /new (handleClearCommand) on a model whose thinking cannot be turned off failed with "Failed to create session: RPC
        // command failed." and exited. sdk.ts createAgentSession clamps the new session's level (defaultThinkingLevel "off") to the
        // model: anthropic/claude-haiku-5-5 maps off and minimal to null, so "low"; setModel (/model) clamps the same way.
        yield return ("e2e.slash.new-session-on-a-model-without-off-clamps-thinking", async () =>
        {
            await using var pi = new InteractiveHarness("new-clamped");
            File.WriteAllText(Path.Combine(pi.AgentDir, "settings.json"), """{"defaultThinkingLevel":"off"}""");
            pi.Start("--provider", "anthropic", "--model", "claude-haiku-5-5", "--tui-mode", "regular");
            await pi.WaitFor("escape interrupt");
            await pi.WaitUntil(text => text.Contains("claude-haiku-5-5 • low", StringComparison.Ordinal), "startup level clamped");
            await pi.Submit("first message");
            await pi.WaitFor("Hello from the fake model.");
            await pi.Submit("/new");
            await pi.WaitFor("✓ New session started");
            await pi.WaitUntil(text => text.Contains("claude-haiku-5-5 • low", StringComparison.Ordinal), "new session level clamped");
            await pi.Submit("after clear");
            await pi.WaitUntil(text => text.IndexOf("after clear", StringComparison.Ordinal) is >= 0 and var asked &&
                text.IndexOf("Hello from the fake model.", asked, StringComparison.Ordinal) > asked, "the answer in the new session");
            Equal(2, pi.Requests.Count, "one request per turn");
            Check(!pi.Terminal.Text.Contains("Failed to create session", StringComparison.Ordinal), "no fatal error");
            var created = pi.SessionFiles().Select(InteractiveHarness.ReadShared).Single(text => text.Contains("after clear", StringComparison.Ordinal));
            Check(created.Contains("\"thinkingLevel\":\"low\"", StringComparison.Ordinal) && !created.Contains("\"thinkingLevel\":\"off\"", StringComparison.Ordinal),
                "the new session records the clamped level: " + created);
            Equal(0, await pi.Quit(), "exit code");
        });
        // sdk.ts createAgentSession: a resumed branch that records "off" on that model opens at the clamped "low" without rewriting the
        // file; /reload (the catalog republished) and later turns keep running at it instead of refusing the recorded level.
        yield return ("e2e.resume-reload-and-turns-on-a-clamped-thinking-level", async () =>
        {
            await using var pi = new InteractiveHarness("resume-clamped");
            var cwd = System.Text.Json.JsonSerializer.Serialize(pi.Cwd);
            var session = pi.Write("resume-clamped.jsonl", string.Join("\n",
                "{\"type\":\"session\",\"version\":3,\"id\":\"01a00000-0000-7000-8000-00000000c1a5\",\"timestamp\":\"2026-10-09T10:00:00.000Z\",\"cwd\":" + cwd + "}",
                "{\"type\":\"model_change\",\"id\":\"a1\",\"parentId\":null,\"timestamp\":\"2026-10-09T10:00:00.001Z\",\"provider\":\"anthropic\",\"modelId\":\"claude-haiku-5-5\"}",
                "{\"type\":\"thinking_level_change\",\"id\":\"a2\",\"parentId\":\"a1\",\"timestamp\":\"2026-10-09T10:00:00.002Z\",\"thinkingLevel\":\"off\"}",
                "{\"type\":\"message\",\"id\":\"a3\",\"parentId\":\"a2\",\"timestamp\":\"2026-10-09T10:00:00.003Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"first\"}],\"timestamp\":1}}",
                "{\"type\":\"message\",\"id\":\"a4\",\"parentId\":\"a3\",\"timestamp\":\"2026-10-09T10:00:00.004Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"api\":\"anthropic-messages\",\"provider\":\"anthropic\",\"model\":\"claude-haiku-5-5\",\"usage\":{\"input\":3,\"output\":2,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":5,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}},\"stopReason\":\"stop\",\"timestamp\":2}}") + "\n");
            pi.Start("--session", session, "--tui-mode", "regular");
            await pi.WaitFor("escape interrupt");
            await pi.WaitUntil(text => text.Contains("claude-haiku-5-5 • low", StringComparison.Ordinal), "resumed at the clamped level");
            await pi.Submit("/reload");
            await pi.WaitFor("Reloaded keybindings, extensions, skills, prompts, themes, and context files");
            await pi.Submit("after reload");
            await pi.WaitFor("Hello from the fake model.");
            var state = await pi.Mode!.Rpc.RequestAsync(new System.Text.Json.Nodes.JsonObject { ["type"] = "get_state" });
            Equal("low", state?["thinkingLevel"]?.GetValue<string>(), "level after reload and a turn");
            Equal(2, InteractiveHarness.ReadShared(session).Split("thinking_level_change").Length, "only the recorded \"off\" change; the clamped level is not recorded");
        });
        yield return ("e2e.slash.model-switch-clamps-thinking", Case("model-clamped", async pi =>
        {
            await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5 • thinking off", StringComparison.Ordinal), "startup level");
            async Task Model(string reference)
            {
                // The first Enter closes the argument completion list, the second submits.
                pi.Type("/model " + reference);
                await pi.WaitFor("/model " + reference);
                await Task.Delay(300); pi.Type("\r"); await Task.Delay(300); pi.Type("\r");
            }
            await Model("anthropic/claude-haiku-5-5");
            await pi.WaitUntil(text => text.Contains("claude-haiku-5-5 • low", StringComparison.Ordinal), "switched model clamps off to low");
            await Model("anthropic/claude-sonnet-4-5");
            // _getThinkingLevelForModelSwitch: the settings default ("off") again, supported by sonnet.
            await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5 • thinking off", StringComparison.Ordinal), "switched back");
        }, setup: pi => File.WriteAllText(Path.Combine(pi.AgentDir, "settings.json"), """{"defaultThinkingLevel":"off"}""")));
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

        // interactive-mode.ts handleBashCommand: a failed executeBash completes the bash component and shows
        // "Bash command failed: <error.message>"; a command beyond Windows' command-line limit fails as Node's spawn error.
        yield return ("e2e.bash.spawn-failure-shows-the-error", Case("bash-failure", async pi =>
        {
            pi.Type("!");
            pi.Type("\u001b[200~echo " + new string('q', 40_000) + "\u001b[201~");
            pi.Type("\r");
            if (OperatingSystem.IsWindows()) await pi.WaitFor("Bash command failed: spawn ENAMETOOLONG", 60_000);
            else await pi.WaitFor("qqqqqqqqqq", 60_000);
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

        // interactive-mode.ts showTreeSelector: the label editor (shift+l) appends a label entry through sessionManager.appendLabelChange.
        yield return ("e2e.selector.tree-label", Case("tree-label", async pi =>
        {
            await pi.Submit("label me");
            await pi.WaitFor("Hello from the fake model.");
            await pi.Submit("/tree");
            await pi.WaitFor("Session Tree");
            pi.Type("L");
            await Task.Delay(300);
            pi.Type("checkpoint-one");
            await Task.Delay(100);
            pi.Type("\r");
            await pi.WaitFor("checkpoint-one");
            Check(!pi.Terminal.Text.Contains("not supported", StringComparison.Ordinal), "no unsupported error");
            var file = pi.SessionFiles().Single();
            await pi.WaitUntil(_ => InteractiveHarness.ReadShared(file).Contains("\"label\":\"checkpoint-one\"", StringComparison.Ordinal), "label entry written");
            pi.Type("\u001b");
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
            // agent-session.ts reload re-reads the settings and syncs the queue modes from them.
            File.WriteAllText(Path.Combine(pi.AgentDir, "settings.json"), """{"steeringMode":"all","followUpMode":"all"}""");
            await pi.Submit("/reload");
            await pi.WaitFor("Reloaded keybindings, extensions, skills, prompts, themes, and context files");
            var state = await pi.Mode!.Rpc.RequestAsync(new System.Text.Json.Nodes.JsonObject { ["type"] = "get_state" });
            Equal("all", state?["steeringMode"]?.GetValue<string>(), "steering mode from the reloaded settings");
            Equal("all", state?["followUpMode"]?.GetValue<string>(), "follow-up mode from the reloaded settings");
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

        // agent-session.ts setModel/cycleModel over modelRuntime.getAvailableSnapshot: /model, Ctrl+P and --models switch the live
        // session; the next request goes to the selected model and the session records the change.
        yield return ("e2e.models.switch-with-model-command", Case("model-switch", async pi =>
        {
            var available = await pi.Mode!.Rpc.RequestAsync(new System.Text.Json.Nodes.JsonObject { ["type"] = "get_available_models" });
            var ids = (available?["models"] as System.Text.Json.Nodes.JsonArray ?? []).Select(model => model?["id"]?.GetValue<string>()).ToList();
            Check(ids.Contains("claude-haiku-4-5") && ids.Contains("claude-sonnet-4-5"), "the registry's anthropic models are available: " + string.Join(",", ids));
            pi.Type("/model claude-haiku-4-5");
            await Task.Delay(300);
            pi.Type("\r");
            await Task.Delay(300);
            pi.Type("\r");
            await pi.WaitUntil(text => text.Contains("claude-haiku-4-5 •", StringComparison.Ordinal), "footer shows the selected model");
            await pi.Submit("hello haiku");
            await pi.WaitFor("Hello from the fake model.");
            Contains(pi.Requests[^1], "\"model\":\"claude-haiku-4-5\"", "request goes to the selected model");
            var file = pi.SessionFiles().Single();
            await pi.WaitUntil(_ => InteractiveHarness.ReadShared(file).Contains("\"modelId\":\"claude-haiku-4-5\"", StringComparison.Ordinal), "model_change recorded");
            // Summaries (compaction here) run on the switched model's own route.
            await pi.Submit("second turn");
            await pi.WaitUntil(text => text.Split("Hello from the fake model.").Length > 2, "second answer");
            var before = pi.Requests.Count;
            pi.Respond = (_, _) => InteractiveHarness.AnthropicText("## Goal\nsummary from haiku");
            await pi.Submit("/compact");
            await pi.WaitUntil(_ => InteractiveHarness.ReadShared(file).Contains("\"type\":\"compaction\"", StringComparison.Ordinal), "compaction recorded");
            Check(pi.Requests.Count > before, "a summary request was sent");
            Contains(pi.Requests[^1], "\"model\":\"claude-haiku-4-5\"", "the summary request goes to the switched model");
        }, setup: pi => File.WriteAllText(Path.Combine(pi.AgentDir, "settings.json"), """{"compaction":{"keepRecentTokens":1}}""")));

        // settings-manager.ts getCompactionTokenSetting: an invalid token setting fails the compaction with upstream's message.
        yield return ("e2e.compact.invalid-settings", Case("compact-invalid", async pi =>
        {
            await pi.Submit("/compact");
            await pi.WaitFor("Compaction failed: Invalid compaction.keepRecentTokens setting: -1. Expected a non-negative");
        }, setup: pi => File.WriteAllText(Path.Combine(pi.AgentDir, "settings.json"), """{"compaction":{"keepRecentTokens":-1}}""")));
        // agent-session.ts compact: a session too small to compact reports one error (the compaction_end event's).
        yield return ("e2e.compact.nothing-to-compact-once", Case("compact-small", async pi =>
        {
            await pi.Submit("/compact");
            await pi.WaitFor("Nothing to compact (session too small)");
            await Task.Delay(500);
            Equal(1, pi.Terminal.Text.Split("Nothing to compact").Length - 1, "one error line: " + pi.Terminal.Text);
        }));

        yield return ("e2e.models.ctrl-p-cycles-available", Case("model-cycle", async pi =>
        {
            pi.Type("\u0010");
            await pi.WaitUntil(text => !text.Contains("claude-sonnet-4-5 •", StringComparison.Ordinal) && text.Contains(" • ", StringComparison.Ordinal), "footer leaves the startup model");
            var state = await pi.Mode!.Rpc.RequestAsync(new System.Text.Json.Nodes.JsonObject { ["type"] = "get_state" });
            Check(state?["model"]?["id"]?.GetValue<string>() is { } id && id != "claude-sonnet-4-5", "the session model changed");
        }));

        yield return ("e2e.models.ctrl-p-cycles-scope", Case("model-scope", async pi =>
        {
            pi.Type("\u0010");
            await pi.WaitUntil(text => text.Contains("claude-haiku-4-5 •", StringComparison.Ordinal), "next scoped model");
            pi.Type("\u0010");
            await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5 •", StringComparison.Ordinal), "scope wraps around");
            await pi.Submit("scoped hello");
            await pi.WaitFor("Hello from the fake model.");
            Contains(pi.Requests[^1], "\"model\":\"claude-sonnet-4-5\"", "request goes to the scoped model");
        }, extra: ["--models", "claude-sonnet-4-5,claude-haiku-4-5"]));

        // After /login the provider's models join the available snapshot and can be selected at once.
        yield return ("e2e.models.login-makes-models-available", Case("model-login", async pi =>
        {
            static async Task<List<string?>> Providers(InteractiveHarness pi) =>
                [.. ((await pi.Mode!.Rpc.RequestAsync(new System.Text.Json.Nodes.JsonObject { ["type"] = "get_available_models" }))?["models"] as System.Text.Json.Nodes.JsonArray ?? [])
                    .Select(model => model?["provider"]?.GetValue<string>()).Distinct()];
            Check(!(await Providers(pi)).Contains("openai"), "openai is not available before /login");
            pi.Type("/login openai");
            await Task.Delay(300);
            pi.Type("\r");
            await Task.Delay(300);
            pi.Type("\r");
            await pi.WaitFor("Select authentication method for OpenAI:");
            pi.Type("\u001b[B");
            await Task.Delay(100);
            pi.Type("\r");
            await pi.WaitFor("Enter OpenAI API key");
            await Task.Delay(100);
            pi.Type("sk-openai-test");
            pi.Type("\r");
            await pi.WaitFor("Saved API key for OpenAI");
            await pi.WaitUntil(_ => Providers(pi).GetAwaiter().GetResult().Contains("openai"), "openai models available after /login");
        }, setup: pi => pi.Configure = context => context with { Login = new PiSharp.Cli.Authentication.ProviderLoginHost(
            new PiSharp.Cli.Authentication.AuthJsonCredentialStore(Path.Combine(pi.AgentDir, "auth.json")), () => new HttpClient()) }));
        yield return ("e2e.selector.scoped-models", Case("scoped", async pi =>
        {
            await pi.Submit("/scoped-models");
            await pi.WaitFor("Model Configuration");
            pi.Type("\u001b");
            await pi.WaitUntil(text => !text.Contains("Model Configuration", StringComparison.Ordinal), "closed");
        }));

        yield return ("e2e.selector.trust", Case("trust", async pi =>
        {
            await pi.Submit("/trust");
            await pi.WaitFor("Project trust");
            pi.Type("\u001b");
            await pi.WaitUntil(text => !text.Contains("Project trust", StringComparison.Ordinal), "closed");
        }));

        yield return ("e2e.login.auth-type-selector", Case("login", async pi =>
        {
            await pi.Submit("/login");
            await pi.WaitFor("Select authentication method:");
            Contains(pi.Terminal.Text, "Sign in with an API key", "api key option");
            pi.Type("\u001b");
            await pi.WaitUntil(text => !text.Contains("Select authentication method:", StringComparison.Ordinal), "closed");
        }));

        yield return ("e2e.login.api-key-prompt-is-unmasked-like-upstream", Case("login-mask", async pi =>
        {
            pi.Type("/login anthropic");
            await Task.Delay(300);
            pi.Type("\r");
            await Task.Delay(300);
            pi.Type("\r");
            await pi.WaitFor("Select authentication method for Anthropic:");
            pi.Type("\u001b[B");
            await Task.Delay(100);
            pi.Type("\r");
            await pi.WaitFor("Enter Anthropic API key");
            await Task.Delay(100);
            pi.Type("sk-secret-value-123");
            await pi.WaitFor("sk-secret-value-123");
            Check(!pi.Terminal.Text.Contains("*******************", StringComparison.Ordinal), "upstream 1.1.0 does not mask the API key");
            pi.Type("\u001b");
        }, setup: pi => pi.Configure = context => context with { Login = new PiSharp.Cli.Authentication.ProviderLoginHost(
            new PiSharp.Cli.Authentication.AuthJsonCredentialStore(Path.Combine(pi.AgentDir, "auth.json")), () => new HttpClient()) }));

        yield return ("e2e.mcp.manager", Case("mcp", async pi =>
        {
            await pi.Submit("/mcp");
            await pi.WaitUntil(text => text.Contains("MCP", StringComparison.Ordinal), "mcp output");
        }));

        yield return ("e2e.theme.light-setting", async () =>
        {
            await using var pi = new InteractiveHarness("light");
            pi.Write(Path.Combine(pi.AgentDir, "settings.json"), "{\"theme\":\"light\"}");
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            Equal("light", PiSharp.Cli.Interactive.Mode.Themes.CurrentThemeName, "light theme active");
        });

        yield return ("e2e.settings.output-pad-zero", async () =>
        {
            await using var pi = new InteractiveHarness("pad");
            pi.Write(Path.Combine(pi.AgentDir, "settings.json"), "{\"outputPad\":0}");
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("/thinking bogus");
            await pi.Submit("");
            await pi.WaitFor("Error: Unknown thinking level");
            Check(pi.Terminal.Lines.Any(line => line.StartsWith("Error: Unknown thinking level", StringComparison.Ordinal)), "error line has no padding with outputPad 0");
        });

        // interactive-mode.ts init: ensureTool("fd"/"rg") through the run's tools manager after the TUI mounts; download statuses show
        // in the chat (showManagedToolStatus).
        yield return ("e2e.tools.ensure-through-tools-manager", async () =>
        {
            await using var pi = new InteractiveHarness("tools");
            var seen = new List<string>();
            pi.Vars.Remove("PI_OFFLINE");
            pi.Vars["PATH"] = Directory.CreateDirectory(Path.Combine(pi.Root, "empty-path")).FullName;
            pi.ToolsHttp = () => new RecordingHandler(seen);
            pi.Start(Regular);
            await pi.WaitFor("fd not found. Downloading...");
            await pi.WaitFor("Warning: Failed to download fd");
            await pi.WaitFor("Warning: Failed to download ripgrep");
            lock (seen)
            {
                Check(seen.Any(url => url.StartsWith("https://gh.test/sharkdp/fd/", StringComparison.Ordinal)), "fd requested from the release server");
                Check(seen.Any(url => url.StartsWith("https://gh.test/BurntSushi/ripgrep/", StringComparison.Ordinal)), "rg requested from the release server");
            }
            await pi.Submit("still works");
            await pi.WaitFor("Hello from the fake model.");
        });
        // interactive-mode.ts init: checkForPackageUpdates (skipped with PI_OFFLINE) and showPackageUpdateNotification.
        yield return ("e2e.packages.update-notification", async () =>
        {
            await using var pi = new InteractiveHarness("package-updates");
            var checks = 0;
            pi.Vars.Remove("PI_OFFLINE");
            pi.Configure = context => context with
            {
                CheckForPackageUpdates = () => { Interlocked.Increment(ref checks); return Task.FromResult<IReadOnlyList<string>>(["@acme/pi-tools", "github.com/acme/skills"]); },
                EnsureTool = (_, _) => Task.FromResult<string?>(null)
            };
            pi.Start(Regular);
            await pi.WaitFor("Package Updates Available");
            await pi.WaitFor("- github.com/acme/skills");
            Contains(pi.Terminal.Text, "update --extensions", "update instruction");
            Equal(1, checks, "checked once at startup");
        });
        yield return ("e2e.packages.no-check-offline", async () =>
        {
            var checks = 0;
            await using var pi = await Started("package-offline", pi => pi.Configure = context => context with
            {
                CheckForPackageUpdates = () => { Interlocked.Increment(ref checks); return Task.FromResult<IReadOnlyList<string>>(["x"]); }
            });
            await pi.Submit("hello");
            await pi.WaitFor("Hello from the fake model.");
            Equal(0, checks, "no package check with PI_OFFLINE");
        });
        // interactive-mode.ts run(): checkForNewPiVersion starts after init, once per startup with no cache, and a newer release shows
        // showNewVersionNotification. Owner decision 12: the latest PiSharp.Cli on NuGet, `dotnet tool update -g PiSharp.Cli`.
        const string feedIndex = "https://nuget.test/v3-flatcontainer/pisharp.cli/index.json";
        static Func<HttpRequestMessage, HttpResponseMessage> Feed(string json, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
            _ => new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        static InteractiveHarness Online(string name, string version, Func<HttpRequestMessage, HttpResponseMessage> feed)
        {
            var pi = new InteractiveHarness(name) { ProductVersion = version, VersionFeed = feed };
            pi.Vars.Remove("PI_OFFLINE");
            pi.Configure = context => context with { EnsureTool = (_, _) => Task.FromResult<string?>(null) };
            return pi;
        }
        yield return ("e2e.version.update-notification", async () =>
        {
            await using var pi = Online("version-newer", "1.1.0.2", Feed("{\"versions\":[\"1.1.0\",\"1.1.0.1\",\"1.1.0.2\",\"1.1.0.10\",\"1.1.0.11-preview.1\"]}"));
            pi.Start(Regular);
            await pi.WaitFor("Update Available");
            await pi.WaitFor("Changelog: ");
            var lines = pi.Terminal.Lines;
            var title = Array.FindIndex(lines, line => line.Trim() == "Update Available");
            Check(title > 0, "title line");
            Check(lines[title - 1].Trim().Length > 0 && lines[title - 1].Trim().All(c => c == '─'), "top border");
            Equal(" New version 1.1.0.10 is available. Run dotnet tool update -g PiSharp.Cli", lines[title + 1], "instruction");
            Equal(" Changelog: https://github.com/jkelly/PiSharp/blob/main/CHANGELOG.md", lines[title + 2], "changelog");
            Check(lines[title + 3].Trim().Length > 0 && lines[title + 3].Trim().All(c => c == '─'), "bottom border");
            lock (pi.VersionRequests) Equal(feedIndex, string.Join(" | ", pi.VersionRequests), "one NuGet request");
            Check(!pi.Terminal.Text.Contains("pi update", StringComparison.Ordinal), "no pi update instruction");
        });
        yield return ("e2e.version.prerelease-notification", async () =>
        {
            await using var pi = Online("version-prerelease", "1.1.0.3-preview.1", Feed("{\"versions\":[\"1.1.0.2\",\"1.1.0.3-preview.1\",\"1.1.0.3-preview.2\"]}"));
            pi.Start(Regular);
            await pi.WaitFor("Update Available");
            Contains(pi.Terminal.Text, "New version 1.1.0.3-preview.2 is available. Run dotnet tool update -g PiSharp.Cli --prerelease", "prerelease instruction");
        });
        const string stableFeed = "{\"versions\":[\"1.1.0.1\",\"1.1.0.2\",\"1.1.0.12-preview.1\"]}";
        foreach (var (label, version, feed) in new[] { ("equal", "1.1.0.2", stableFeed), ("equal-three-part", "1.1.0.0", "{\"versions\":[\"1.0.0\",\"1.1.0\"]}"),
            ("older-feed", "1.1.0.10", stableFeed), ("stable-ignores-prerelease", "1.1.0.11", stableFeed) })
            yield return ($"e2e.version.no-notice-{label}", async () =>
            {
                await using var pi = Online("version-" + label, version, Feed(feed));
                pi.Start(Regular);
                await pi.WaitFor("escape interrupt");
                await pi.Submit("hello");
                await pi.WaitFor("Hello from the fake model.");
                lock (pi.VersionRequests) Equal(1, pi.VersionRequests.Count, "one NuGet request");
                Check(!pi.Terminal.Text.Contains("Update Available", StringComparison.Ordinal), "no notice");
            });
        foreach (var (label, status, body) in new[] { ("not-found", System.Net.HttpStatusCode.NotFound, "{}"),
            ("server-error", System.Net.HttpStatusCode.InternalServerError, "{}"), ("malformed", System.Net.HttpStatusCode.OK, "<html>"),
            ("unexpected-shape", System.Net.HttpStatusCode.OK, "{\"versions\":\"9.9.9\"}") })
            yield return ($"e2e.version.feed-error-silent-{label}", async () =>
            {
                await using var pi = Online("version-error-" + label, "1.1.0.2", Feed(body, status));
                pi.Start(Regular);
                await pi.WaitFor("escape interrupt");
                await pi.Submit("hello");
                await pi.WaitFor("Hello from the fake model.");
                lock (pi.VersionRequests) Equal(1, pi.VersionRequests.Count, "one NuGet request");
                Check(!pi.Terminal.Text.Contains("Update Available", StringComparison.Ordinal), "no notice");
                Check(!pi.Terminal.Text.Contains("Error", StringComparison.Ordinal) && !pi.Terminal.Text.Contains("Warning", StringComparison.Ordinal), "silent");
            });
        yield return ("e2e.version.feed-throws-silent", async () =>
        {
            await using var pi = Online("version-throws", "1.1.0.2", _ => throw new HttpRequestException("fetch failed"));
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("hello");
            await pi.WaitFor("Hello from the fake model.");
            lock (pi.VersionRequests) Equal(1, pi.VersionRequests.Count, "one NuGet request, no retry");
            Check(!pi.Terminal.Text.Contains("fetch failed", StringComparison.Ordinal), "silent");
        });
        // version-check.ts: PI_OFFLINE (getLatestPiRelease) and PI_SKIP_VERSION_CHECK (checkForNewPiVersion; main.ts sets both for
        // --offline) send nothing. Pi has no settings key for the check (docs/settings.md: enableInstallTelemetry "Does not control
        // update checks"), so the environment and --offline are the switches.
        foreach (var (label, configure, extra) in new (string, Action<InteractiveHarness>, string[])[]
        {
            ("pi-offline", pi => pi.Vars["PI_OFFLINE"] = "1", []),
            ("skip-version-check", pi => pi.Vars["PI_SKIP_VERSION_CHECK"] = "1", []),
            ("offline-flag", _ => { }, ["--offline"]),
        })
            yield return ($"e2e.version.no-request-{label}", async () =>
            {
                await using var pi = Online("version-off-" + label, "1.1.0.1", Feed("{\"versions\":[\"9.0.0\"]}"));
                configure(pi);
                pi.Start([.. Regular, .. extra]);
                await pi.WaitFor("escape interrupt");
                await pi.Submit("hello");
                await pi.WaitFor("Hello from the fake model.");
                lock (pi.VersionRequests) Equal(0, pi.VersionRequests.Count, "no NuGet request");
                Check(!pi.Terminal.Text.Contains("Update Available", StringComparison.Ordinal), "no notice");
            });
        // main.ts: a resumed session whose stored cwd is gone asks to continue in the current cwd (Continue/Cancel); the session file
        // keeps its header and receives the new entries.
        foreach (var answer in new[] { "continue", "cancel" })
            yield return ($"e2e.session.missing-cwd-{answer}", async () =>
            {
                await using var pi = new InteractiveHarness("missing-cwd-" + answer);
                var missing = Path.Combine(pi.Root, "gone");
                var sessionFile = pi.Write("old-sessions/2026-01-01T00-00-00-000Z_0198a2b0-0000-7000-8000-000000000001.jsonl",
                    System.Text.Json.JsonSerializer.Serialize(new { type = "session", version = 3, id = "0198a2b0-0000-7000-8000-000000000001",
                        timestamp = "2026-01-01T00:00:00.000Z", cwd = missing }) + "\n");
                string? asked = null;
                pi.MissingCwdAnswer = (prompt, fallback) => { asked = prompt; return answer == "continue" ? fallback : null; };
                pi.Start([.. Regular, "--session", sessionFile]);
                if (answer == "cancel")
                {
                    Equal(0, await pi.Exit(), "cancel exits 0");
                    Equal($"cwd from session file does not exist\n{missing}\n\ncontinue in current cwd\n{pi.Cwd}", asked, "prompt text");
                    return;
                }
                await pi.WaitFor("escape interrupt");
                Contains(asked ?? "", "continue in current cwd", "prompted");
                await pi.Submit("still here");
                await pi.WaitFor("Hello from the fake model.");
                await pi.WaitUntil(_ => InteractiveHarness.ReadShared(sessionFile).Contains("still here", StringComparison.Ordinal), "entries appended to the session file");
                Contains(InteractiveHarness.ReadShared(sessionFile), missing.Replace("\\", "\\\\"), "header keeps the stored cwd");
            });
        // interactive-mode.ts createExtensionUIContext with a TypeScript extension in the Node extension host: a component widget, a
        // footer factory, a message renderer, a markdown transformer and ctx.ui.custom() draw in the mode.
        yield return ("e2e.extensions.node-ui-components", async () =>
        {
            RequireNode();
            await using var pi = new InteractiveHarness("ext-ui");
            foreach (var name in new[] { "SystemRoot", "PISHARP_NODE" })
                if (Environment.GetEnvironmentVariable(name) is { } value) pi.Vars[name] = value;
            var extension = pi.Write("project/ui.ts", """
                import { Text } from "@earendil-works/pi-tui";
                export default function (pi: any) {
                  pi.on("session_start", async (_event: any, ctx: any) => {
                    ctx.ui.setWidget("demo", () => ({ render: (width: number) => ["LIVE WIDGET " + width], invalidate() {} }));
                    ctx.ui.setFooter(() => ({ render: () => ["CUSTOM FOOTER"], invalidate() {} }));
                  });
                  pi.registerMessageRenderer("note", (message: any) => new Text("NOTE: " + message.content, 1, 0));
                  pi.registerMarkdownTransformer((markdown: string) => markdown.replace("fake model", "FAKE MODEL"));
                  pi.registerEntryRenderer("marker", (entry: any) => new Text("MARKER " + entry.data.n, 1, 0));
                  pi.registerCommand("pick", { description: "Pick something", handler: async (_args: any, ctx: any) => {
                    const choice = await ctx.ui.custom((_tui: any, _theme: any, _keys: any, done: (value: string) => void) =>
                      ({ render: () => ["CUSTOM PICKER"], handleInput: (data: string) => { if (data === "\r") done("chosen"); }, invalidate() {} }));
                    ctx.ui.notify("picked " + choice);
                  } });
                }
                """);
            // A resumed session's custom message and custom entry draw through the extension's renderers.
            var session = pi.Write("old/2026-01-01T00-00-00-000Z_0198a2b0-0000-7000-8000-000000000002.jsonl", string.Join("\n",
                System.Text.Json.JsonSerializer.Serialize(new { type = "session", version = 3, id = "0198a2b0-0000-7000-8000-000000000002", timestamp = "2026-01-01T00:00:00.000Z", cwd = pi.Cwd }),
                System.Text.Json.JsonSerializer.Serialize(new { type = "custom_message", id = "a1", parentId = (string?)null, timestamp = "2026-01-01T00:00:01.000Z", customType = "note", content = "hi there", display = true }),
                System.Text.Json.JsonSerializer.Serialize(new { type = "custom", id = "a2", parentId = "a1", timestamp = "2026-01-01T00:00:02.000Z", customType = "marker", data = new { n = 7 } })) + "\n");
            pi.Start([.. Regular, "-e", extension, "--session", session]);
            await pi.WaitFor("escape interrupt");
            await pi.WaitFor("LIVE WIDGET 100");
            await pi.WaitFor("CUSTOM FOOTER");
            await pi.Submit("hello");
            await pi.WaitFor("Hello from the FAKE MODEL.");
            await pi.WaitFor("NOTE: hi there");
            await pi.WaitFor("MARKER 7");

            pi.Type("/pick");
            await Task.Delay(300);
            pi.Type("\r");
            await Task.Delay(300);
            pi.Type("\r");
            await pi.WaitFor("CUSTOM PICKER");
            pi.Type("\r");
            await pi.WaitFor("picked chosen");
            await pi.WaitUntil(text => !text.Contains("CUSTOM PICKER", StringComparison.Ordinal), "the editor is back");
        });
        // cli/config-selector.ts selectConfig: `pisharp config` opens the resource configuration TUI; space toggles the selected resource
        // (written to settings.json) and escape closes it.
        yield return ("e2e.config.selector", async () =>
        {
            await using var pi = new InteractiveHarness("config");
            pi.Write(Path.Combine(pi.AgentDir, "extensions", "local.ts"), "export default function () {}\n");
            var host = new PiSharp.Cli.Pi.PiHost
            {
                Cwd = pi.Cwd, Home = pi.Home, GetEnvironment = name => pi.Vars.GetValueOrDefault(name), Stdout = pi.Stdout, Stderr = pi.Stderr,
                Stdin = new StringReader(""), StdinIsTty = true, StdoutIsTty = true, LiveRuntime = PiSharp.Cli.Commands.LiveSessionRuntime.Default,
                ConfigSelector = (request, token) => new PiSharp.Cli.Interactive.Mode.StartupUi(request.AgentDir, request.Cwd, name => pi.Vars.GetValueOrDefault(name),
                    loop => new PiSharp.Tui.Pi.ProcessTerminal(loop, pi.Terminal, name => pi.Vars.GetValueOrDefault(name))).SelectConfigAsync(request, token)
            };
            var run = Task.Run(() => PiSharp.Cli.Pi.PiCommand.RunAsync(["config"], host, CancellationToken.None));
            await pi.WaitFor("local.ts");
            Contains(pi.Terminal.Text, "Global Resources", "global mode");
            pi.Type(" ");
            await pi.WaitUntil(_ => File.Exists(Path.Combine(pi.AgentDir, "settings.json")) &&
                File.ReadAllText(Path.Combine(pi.AgentDir, "settings.json")).Contains("local.ts", StringComparison.Ordinal), "toggle written to settings.json");
            pi.Type("\u001b");
            Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30)), "config exits 0 after closing: " + pi.Stderr);
        });
        yield return ("e2e.autocomplete.at-file", Case("at-file", async pi =>
        {
            if (new PiSharp.Cli.Pi.PiToolsManager(Path.Join(pi.AgentDir, "bin"), Environment.GetEnvironmentVariable).GetToolPath("fd") is null)
                throw new SkipCaseException("fd is not installed on this machine.");
            File.WriteAllText(Path.Combine(pi.Cwd, "readme-target.md"), "x");
            pi.Type("look at @readme-t");
            await pi.WaitFor("readme-target.md");
        }));

        yield return ("e2e.slash.bug-cancel", Case("bug", async pi =>
        {
            await pi.Submit("/bug");
            await pi.WaitFor("Nothing is uploaded.");
            await Task.Delay(200);
            pi.Type("\u001b");
            await pi.WaitFor("Bug report cancelled");
        }));
        // /bug without the transcript: the session model writes the summary (generateBugReportSummary), the zip carries it as
        // summary.md and the session records the report (appendCustomEntry).
        yield return ("e2e.slash.bug-summary-zip", Case("bug-summary", async pi =>
        {
            await pi.Submit("first message");
            await pi.WaitFor("Hello from the fake model.");
            pi.Respond = (_, _) => InteractiveHarness.AnthropicText("Summary: the agent misbehaved.");
            await pi.Submit("/bug");
            await pi.WaitFor("What went wrong?");
            pi.Type("tool output vanished");
            pi.Type("\r");
            await pi.WaitFor("Include the session transcript?");
            pi.Type("\u001b[B");
            await Task.Delay(100);
            pi.Type("\r");
            await pi.WaitFor("Attach a summary written by");
            pi.Type("\r");
            await pi.WaitFor("Open GitHub Issue writes");
            pi.Type("\u001b[B");
            await Task.Delay(100);
            pi.Type("\r");
            await pi.WaitFor("Bug report exported to:");
            Contains(pi.Requests[^1], "<user-report>\\ntool output vanished\\n</user-report>", "summary request carries the hint");
            var zip = Directory.GetFiles(pi.Cwd, "pi-bug-report-*.zip").Single();
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
            {
                using var reader = new StreamReader(archive.GetEntry("summary.md")!.Open());
                Contains(reader.ReadToEnd(), "Summary: the agent misbehaved.", "summary.md");
            }
            var file = pi.SessionFiles().Single();
            await pi.WaitUntil(_ => InteractiveHarness.ReadShared(file).Contains("\"type\":\"custom\"", StringComparison.Ordinal), "bug report custom entry");
        }));
        // Owner decision 11: /bug's "Open GitHub Issue" writes the zip and opens a prefilled issue at github.com/jkelly/PiSharp with
        // the mode's URL opener (a fake here: no real browser). The only HTTP traffic is the prompt and the summary request to the
        // fake provider; nothing is uploaded.
        yield return ("e2e.slash.bug-github-issue", async () =>
        {
            var opened = new List<string>();
            await using var pi = new InteractiveHarness("bug-issue", columns: 120, rows: 90);
            pi.Vars["DISPLAY"] = ":0";
            pi.Configure = context => context with { OpenUrl = url => { lock (opened) opened.Add(url); } };
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("first message");
            await pi.WaitFor("Hello from the fake model.");
            pi.Respond = (_, _) => InteractiveHarness.AnthropicText("Summary: the agent misbehaved.");
            await pi.Submit("/bug");
            await pi.WaitFor("What went wrong?");
            pi.Type("tool output vanished");
            pi.Type("\r");
            await pi.WaitFor("Include the session transcript?");
            pi.Type("\u001b[B");
            await Task.Delay(100);
            pi.Type("\r");
            await pi.WaitFor("Attach a summary written by");
            pi.Type("\r");
            await pi.WaitFor("Open GitHub Issue writes");
            pi.Type("\r");
            await pi.WaitFor("Opened a prefilled GitHub issue in your browser.");
            var url = opened.Single();
            var zip = Directory.GetFiles(pi.Cwd, "pi-bug-report-*.zip").Single();
            var id = Path.GetFileNameWithoutExtension(zip)["pi-bug-report-".Length..];
            Check(url.StartsWith("https://github.com/jkelly/PiSharp/issues/new?title=Bug%20report%3A%20tool%20output%20vanished&body=" +
                "%23%23%20Description%0A%0Atool%20output%20vanished%0A%0A%23%23%20Summary%0A%0ASummary%3A%20the%20agent%20misbehaved.%0A%0A" +
                "%23%23%20Environment%0A%0A-%20PiSharp%3A%20", StringComparison.Ordinal), "issue link: " + url);
            var body = Uri.UnescapeDataString(url[(url.IndexOf("&body=", StringComparison.Ordinal) + "&body=".Length)..]);
            Contains(body, "\n- Model: anthropic/claude-sonnet-4-5", "model in the issue");
            Contains(body, $"Report ID: `{id}`. The full report is `pi-bug-report-{id}.zip` (report.json, diagnostics.json, summary.md)", "report to attach");
            Check(!body.Contains(pi.Cwd, StringComparison.Ordinal) && !body.Contains("sk-test-key", StringComparison.Ordinal), "no paths or keys in the issue");
            Check(url.Length <= 8000, "link length");
            var screen = string.Join("", pi.Terminal.Text.Split('\n').Select(line => line.Trim()));
            Contains(screen, Path.GetFileName(zip), "report path printed");
            Contains(screen, url[..60], "link printed");
            Equal(2, pi.Requests.Count, "only the prompt and the summary went over HTTP");
            Check(pi.Requests.All(request => !request.Contains("bug_report", StringComparison.Ordinal) && !request.Contains("report.json", StringComparison.Ordinal)), "nothing uploaded");
            Check(pi.CatalogRequests.All(request => !request.Contains("bug-reports", StringComparison.Ordinal)), "no upload endpoint");
            var file = pi.SessionFiles().Single();
            await pi.WaitUntil(_ => InteractiveHarness.ReadShared(file).Contains("\"delivery\":\"github-issue\"", StringComparison.Ordinal), "bug report custom entry");
        });
        // Without a browser the user can see (here an SSH session), the report path and the link are printed and nothing opens.
        yield return ("e2e.slash.bug-github-issue-headless", async () =>
        {
            var opened = new List<string>();
            await using var pi = new InteractiveHarness("bug-headless", columns: 220, rows: 60);
            pi.Vars["SSH_CONNECTION"] = "10.0.0.1 50000 10.0.0.2 22";
            pi.Vars["DISPLAY"] = ":0";
            pi.Configure = context => context with { OpenUrl = url => { lock (opened) opened.Add(url); } };
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("/bug");
            await pi.WaitFor("What went wrong?");
            pi.Type("\r");
            await pi.WaitFor("Include the session transcript?");
            pi.Type("\u001b[B");
            await Task.Delay(100);
            pi.Type("\r");
            await pi.WaitFor("Attach a summary written by");
            pi.Type("\u001b[B");
            await Task.Delay(100);
            pi.Type("\r");
            await pi.WaitFor("Open GitHub Issue writes");
            pi.Type("\r");
            await pi.WaitFor("No browser could be opened.");
            var zip = Directory.GetFiles(pi.Cwd, "pi-bug-report-*.zip").Single();
            var screen = string.Join("", pi.Terminal.Text.Split('\n').Select(line => line.Trim()));
            Contains(screen, "Bug report exported to: " + zip, "report path printed");
            Contains(screen, "https://github.com/jkelly/PiSharp/issues/new?title=Bug%20report%20from%20PiSharp%20", "link printed");
            Equal(0, opened.Count, "no browser opened");
            Equal(0, pi.Requests.Count, "nothing sent");
        });
        yield return ("e2e.autocomplete.slash-commands", Case("autocomplete", async pi =>
        {
            pi.Type("/hot");
            await pi.WaitFor("Show all keyboard shortcuts");
            pi.Type("\t");
            await pi.WaitUntil(text => text.Contains("/hotkeys", StringComparison.Ordinal), "completed command");
        }));
    }

    private sealed class RecordingHandler(List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (seen) seen.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    /// <summary>TypeScript extensions need Node.js 22.13 or later (module.stripTypeScriptTypes).</summary>
    private static void RequireNode()
    {
        var node = PiSharp.Compatibility.Node.Pi.PiNodeHost.FindNode(Environment.GetEnvironmentVariable) ?? throw new SkipCaseException("Node.js is not on PATH.");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(node, "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var version = process.StandardOutput.ReadToEnd().Trim().TrimStart('v').Split('.');
        process.WaitForExit();
        if (version.Length < 2 || !int.TryParse(version[0], out var major) || !int.TryParse(version[1], out var minor) || major < 22 || major == 22 && minor < 13)
            throw new SkipCaseException("Node.js " + string.Join('.', version) + " is older than 22.13.");
    }
}