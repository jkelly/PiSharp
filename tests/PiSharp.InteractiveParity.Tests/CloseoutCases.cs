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
    }
}
