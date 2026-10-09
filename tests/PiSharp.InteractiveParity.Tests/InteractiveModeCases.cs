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
            await pi.WaitUntil(text => text.Contains("Pi developers", StringComparison.Ordinal) || text.Contains("bug", StringComparison.OrdinalIgnoreCase), "bug flow prompt");
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
            await pi.WaitFor("Upload sends the report");
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