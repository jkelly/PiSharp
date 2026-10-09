using static Expect;

/// <summary>interactive-mode.ts end to end: the Pi entry in interactive mode on a virtual terminal against a fake provider.</summary>
internal static class InteractiveModeCases
{
    private static readonly string[] Model = ["--provider", "anthropic", "--model", "claude-sonnet-4-5"];

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("e2e.regular.startup-prompt-quit", async () =>
        {
            await using var pi = new InteractiveHarness("startup");
            pi.Start([.. Model, "--tui-mode", "regular"]);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("hello there");
            await pi.WaitFor("Hello from the fake model.");
            Contains(pi.Terminal.Text, "hello there", "user message shown");
            Equal(0, await pi.Quit(), "exit code");
            Contains(pi.Stdout.ToString(), "To resume this session:", "resume hint");
            Equal(1, pi.SessionFiles().Length, "one session file");
        });
    }
}
