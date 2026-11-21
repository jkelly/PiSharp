// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/modes/interactive/interactive-mode.ts (showRuntimeDiagnostics after
// setRebindSession and at startup, #11264) and packages/coding-agent/test/model-scope-session-replacement.test.ts (its interactive
// counterpart; the RPC case is in PiSharp.CliParity.Tests). Authored against the native port in the virtual terminal; not an
// upstream execution capture.
using PiSharp.Cli.Interactive.Mode;
using static Expect;

/// <summary>Sync 1.6.0: the session runtime's diagnostics go in the chat once, at startup and after a session replacement.</summary>
internal static class Sync160UiDiagnosticsCases
{
    private static readonly string[] Regular = ["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular"];

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // showRuntimeDiagnostics: error, warning and anything else, in the runtime's order, before the command's own status line.
        yield return ("sync160.ui.diagnostics.replacement-diagnostics-are-shown-in-the-chat-by-type", async () =>
        {
            await using var pi = new InteractiveHarness("sync160-diagnostics-types");
            var pending = new List<(string Type, string Message)>();
            var takes = 0;
            pi.Configure = context => context with
            {
                TakeUnreportedRuntimeDiagnostics = () =>
                {
                    lock (pending)
                    {
                        takes++;
                        (string Type, string Message)[] taken = [.. pending];
                        pending.Clear();
                        return taken;
                    }
                }
            };
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.WaitUntil(_ => { lock (pending) return takes >= 1; }, "the startup diagnostics are taken once the mode runs");
            lock (pending)
            {
                pending.Add(("error", "Failed to load extension \"x\""));
                pending.Add(("warning", "No models match pattern \"github-copilot/nope\""));
                pending.Add(("info", "Model scope changed"));
            }
            await pi.Submit("/new");
            var screen = await pi.WaitFor("New session started");
            var error = screen.IndexOf("Error: Failed to load extension \"x\"", StringComparison.Ordinal);
            var warning = screen.IndexOf("Warning: No models match pattern \"github-copilot/nope\"", StringComparison.Ordinal);
            var info = screen.IndexOf("Model scope changed", StringComparison.Ordinal);
            var started = screen.IndexOf("New session started", StringComparison.Ordinal);
            Check(error >= 0 && warning > error && info > warning && started > info, "error, warning, status, then the command's line: " + screen);
            Check(!screen.Contains("Warning: Model scope changed", StringComparison.Ordinal) && !screen.Contains("Error: Model scope changed", StringComparison.Ordinal),
                "an info diagnostic is a status line");
            Equal("", pi.Stderr.ToString(), "nothing is written over the TUI");
            Equal(0, await pi.Quit(), "exit code");
        });

        // #11264 through the session host: an enabledModels pattern that matches nothing is a startup diagnostic, shown once in the
        // chat and not again after /new; a settings problem that first appears on a replacement is shown once after that replacement.
        yield return ("sync160.ui.diagnostics.model-scope-warning-once-at-startup-and-a-new-problem-once-after-new", async () =>
        {
            await using var pi = new InteractiveHarness("sync160-diagnostics-host");
            var settingsPath = pi.Write(Path.Combine(pi.AgentDir, "settings.json"), """{"enabledModels":["anthropic/claude-sonnet-4-5","github-copilot/missing-model"]}""");
            var taken = new List<IReadOnlyList<(string Type, string Message)>>();
            pi.Configure = context =>
            {
                var take = context.TakeUnreportedRuntimeDiagnostics;
                return context with { TakeUnreportedRuntimeDiagnostics = () => { var diagnostics = take(); lock (taken) taken.Add(diagnostics); return diagnostics; } };
            };
            int Takes() { lock (taken) return taken.Count; }
            async Task<IReadOnlyList<(string Type, string Message)>> NewSession()
            {
                var before = Takes();
                await pi.Submit("/new");
                await pi.WaitUntil(_ => Takes() > before, "the replacement's diagnostics are taken");
                await pi.WaitFor("New session started");
                lock (taken) return taken[^1];
            }
            pi.Start("--tui-mode", "regular");
            const string scopeWarning = "Warning: No models match pattern \"github-copilot/missing-model\"";
            var startup = await pi.WaitFor(scopeWarning);
            await pi.WaitFor("escape interrupt");
            Equal(1, Count(pi.Terminal.Text, scopeWarning), "shown once at startup: " + startup);
            lock (taken) Check(taken.Count >= 1 && taken.All(diagnostics => diagnostics.Count == 0), "the entry already took the startup diagnostics");

            Equal(0, (await NewSession()).Count, "the unchanged warning is not a diagnostic of the first /new");
            Check(Count(pi.Terminal.Text, scopeWarning) <= 1, "not shown again after /new: " + pi.Terminal.Text);

            File.WriteAllText(settingsPath, "{ not json");
            var afterBreak = await NewSession();
            Check(afterBreak.Count == 1 && afterBreak[0].Message.Contains("settings", StringComparison.OrdinalIgnoreCase), "one settings diagnostic: " + string.Join(" | ", afterBreak));
            var label = afterBreak[0].Type == "error" ? "Error: " : "Warning: ";
            // The chat wraps at the terminal width; the start of the message is enough to find the line.
            var shown = label + afterBreak[0].Message;
            var shownStart = shown[..Math.Min(60, shown.Length)].Replace(" ", "", StringComparison.Ordinal);
            await pi.WaitUntil(text => text.Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Contains(shownStart, StringComparison.Ordinal),
                "the new problem is in the chat: " + shown);

            Equal(0, (await NewSession()).Count, "the same problem is not reported after the next /new");
            Equal("", pi.Stderr.ToString(), "nothing is written over the TUI");
            Equal(0, await pi.Quit(), "exit code");
        });

        // Native (7829-invalid-settings-warning.test.ts is the upstream startup case): a problem the runtime found at startup is in
        // the chat once, through the same showRuntimeDiagnostics, and a /new right after does not report it again.
        yield return ("sync160.ui.diagnostics.native-a-startup-problem-is-in-the-chat-once", async () =>
        {
            await using var pi = new InteractiveHarness("sync160-diagnostics-startup", columns: 200);
            pi.Write(Path.Combine(pi.AgentDir, "settings.json"), "{ not json");
            var replacementTakes = new List<IReadOnlyList<(string Type, string Message)>>();
            pi.Configure = context =>
            {
                var take = context.TakeUnreportedRuntimeDiagnostics;
                return context with { TakeUnreportedRuntimeDiagnostics = () => { var diagnostics = take(); lock (replacementTakes) replacementTakes.Add(diagnostics); return diagnostics; } };
            };
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            var screen = await pi.WaitUntil(text => text.Contains("Warning: ", StringComparison.Ordinal) && text.Contains("settings", StringComparison.OrdinalIgnoreCase), "the settings warning");
            var line = screen.Split('\n').First(candidate => candidate.Contains("Warning: ", StringComparison.Ordinal) && candidate.Contains("settings", StringComparison.OrdinalIgnoreCase)).Trim();
            Equal(1, Count(screen, line), "shown once at startup: " + screen);
            int Takes() { lock (replacementTakes) return replacementTakes.Count; }
            await pi.WaitUntil(_ => Takes() >= 1, "the startup take");
            var before = Takes();
            await pi.Submit("/new");
            await pi.WaitUntil(_ => Takes() > before, "the replacement's diagnostics are taken");
            lock (replacementTakes) Check(replacementTakes.All(diagnostics => diagnostics.Count == 0), "the startup problem is not a diagnostic of the replacement");
            Equal(0, await pi.Quit(), "exit code");
        });
    }
}
