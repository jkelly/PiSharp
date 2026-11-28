// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/coding-agent/src/cli/startup-ui.ts,
// cli/project-trust.ts and modes/interactive/program-status-reporter.ts.
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;

internal static class StartupStatus170Cases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("startup.status.project-trust-permission-and-clear", async () =>
        {
            await using var harness = new InteractiveHarness("startup-status");
            harness.Vars["PI_PROGRAM_STATUS"] = "1";
            string? Env(string name) => harness.Vars.GetValueOrDefault(name);
            var startup = new StartupUi(harness.AgentDir, harness.Cwd, Env, loop => new ProcessTerminal(loop, harness.Terminal, Env));
            var prompt = startup.PromptProjectTrustAsync("Trust this folder?\r\nprivate path", [new ProjectTrustOption("Trust", true, [])], default);
            await harness.WaitFor("Trust this folder?");
            var expected = new ProgramStatus(ProgramState.Blocked, "pi", ProgramBlockedKind.Permission, "Trust this folder?").Format();
            Expect.Check(harness.Terminal.RawOutput.Contains(expected, StringComparison.Ordinal), "only the question is in the OSC permission report");
            harness.Type("\r");
            Expect.Check(await prompt is { Trusted: true }, "project trust choice");
            Expect.Check(harness.Terminal.RawOutput.Contains(new ProgramStatus(ProgramState.Clear).Format(), StringComparison.Ordinal), "status was cleared");
            Expect.Check(!harness.Terminal.RawMode, "terminal restored");
        });
        yield return ("startup.status.input-cancellation-clears-terminal", async () =>
        {
            await using var harness = new InteractiveHarness("startup-cancel");
            harness.Vars["PI_PROGRAM_STATUS"] = "1";
            string? Env(string name) => harness.Vars.GetValueOrDefault(name);
            var startup = new StartupUi(harness.AgentDir, harness.Cwd, Env, loop => new ProcessTerminal(loop, harness.Terminal, Env));
            using var cancel = new CancellationTokenSource();
            var prompt = startup.ShowStartupInputAsync("Name?\nMore details", null, cancel.Token);
            await harness.WaitFor("Name?");
            Expect.Check(harness.Terminal.RawOutput.Contains(new ProgramStatus(ProgramState.Blocked, "pi", ProgramBlockedKind.Question, "Name?").Format(), StringComparison.Ordinal), "input question reported");
            cancel.Cancel();
            try { await prompt; throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            Expect.Check(!harness.Terminal.RawMode && harness.Terminal.RawOutput.Contains(new ProgramStatus(ProgramState.Clear).Format(), StringComparison.Ordinal), "cancelled prompt restored terminal");
        });
        yield return ("sync170.console-cancellation-restores-fullscreen-terminal", async () =>
        {
            await using var harness = new InteractiveHarness("sync170-console-cancel");
            harness.Start("--no-session", "--tui-mode", "fullscreen");
            await harness.WaitUntil(_ => harness.Terminal.RawOutput.Contains("\u001b[?1049h", StringComparison.Ordinal), "fullscreen entry");
            harness.CancelHost(); // Same token cancelled by Console.CancelKeyPress for Ctrl+Break.
            await harness.Exit();
            var output = harness.Terminal.RawOutput;
            Expect.Check(!harness.Terminal.RawMode && output.Contains("\u001b[?1049l", StringComparison.Ordinal) &&
                output.Contains("\u001b[?1000l", StringComparison.Ordinal), "raw mode, mouse tracking and the alternate screen were restored");
        });
    }
}
