using static Expect;

/// <summary>interactive-mode.ts createExtensionUIContext setTheme (1.1.0.3 close-out): a TypeScript extension switches the theme; a theme
/// that applies becomes the theme setting (settingsManager.setTheme), one that does not load reports its error (theme.ts setTheme).</summary>
internal static class CloseoutCases
{
    private static readonly string[] Regular = ["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular"];
    private const string Enter = "\r";

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("e2e.extensions.set-theme-switches-and-saves-the-theme", async () =>
        {
            if (PiSharp.Compatibility.Node.Pi.PiNodeHost.FindNode(Environment.GetEnvironmentVariable) is null) throw new SkipCaseException("Node.js is not on PATH.");
            await using var pi = new InteractiveHarness("set-theme");
            foreach (var name in new[] { "SystemRoot", "PISHARP_NODE" })
                if (Environment.GetEnvironmentVariable(name) is { } value) pi.Vars[name] = value;
            pi.Write(Path.Combine(pi.AgentDir, "settings.json"), """{"theme":"dark"}""");
            var extension = pi.Write("project/theme.ts", """
                export default function (pi: any) {
                  pi.registerCommand("skin", { description: "Theme probe", handler: async (args: string, ctx: any) => {
                    ctx.ui.notify("result " + JSON.stringify(ctx.ui.setTheme(args)));
                  } });
                }
                """);
            pi.Start([.. Regular, "-e", extension]);
            await pi.WaitFor("escape interrupt");
            pi.Type("/skin light"); await Task.Delay(300); pi.Type(Enter);
            await pi.WaitFor("result {\"success\":true}");
            await pi.WaitUntil(_ => File.ReadAllText(Path.Combine(pi.AgentDir, "settings.json")).Contains("\"light\"", StringComparison.Ordinal), "theme setting saved");
            pi.Type("/skin no-such-theme"); await Task.Delay(300); pi.Type(Enter);
            await pi.WaitFor("result {\"success\":false,\"error\":\"Theme not found: no-such-theme\"}");
            Contains(File.ReadAllText(Path.Combine(pi.AgentDir, "settings.json")), "\"light\"", "a theme that failed is not saved");
            Equal(0, await pi.Quit(), "exit code");
        });
        // interactive-mode.ts bindCurrentSessionExtensions commandContextActions.fork: a fork that happened puts the selected text in the
        // editor and shows "Forked to new session"; a failed one is handleFatalRuntimeError ("Failed to fork session: <message>", exit 1).
        yield return ("e2e.extensions.fork-sets-the-editor-and-a-failed-fork-is-fatal", async () =>
        {
            if (PiSharp.Compatibility.Node.Pi.PiNodeHost.FindNode(Environment.GetEnvironmentVariable) is null) throw new SkipCaseException("Node.js is not on PATH.");
            await using var pi = new InteractiveHarness("extension-fork");
            foreach (var name in new[] { "SystemRoot", "PISHARP_NODE" })
                if (Environment.GetEnvironmentVariable(name) is { } value) pi.Vars[name] = value;
            var extension = pi.Write("project/fork.ts", """
                export default function (pi: any) {
                  pi.registerCommand("fk", { description: "Fork probe", handler: async (args: string, ctx: any) => {
                    const user = ctx.sessionManager.getBranch().find((entry: any) => entry.type === "message" && entry.message.role === "user");
                    await ctx.fork(args === "bogus" ? "no-such-entry" : user.id);
                  } });
                }
                """);
            pi.Start([.. Regular, "-e", extension]);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("first question");
            await pi.WaitFor("Hello from the fake model.");
            pi.Type("/fk"); await Task.Delay(300); pi.Type(Enter);
            var screen = await pi.WaitFor("Forked to new session");
            Contains(screen, "first question", "the editor holds the forked user message");
            pi.Terminal.Send("\u0015"); await Task.Delay(100); // clear the editor
            pi.Type("/fk bogus"); await Task.Delay(300); pi.Type(Enter);
            Equal(1, await pi.Exit(), "a failed fork ends the run");
            // recordCrash("fatal_error", error): the crash log keeps the error (the regular renderer stops before the error renders).
            var crashes = Path.Combine(pi.AgentDir, "crashes.json");
            Check(File.Exists(crashes), "crash recorded");
            Contains(File.ReadAllText(crashes), "Invalid entry ID for forking", "the crash names the fork error");
        });
    }
}
