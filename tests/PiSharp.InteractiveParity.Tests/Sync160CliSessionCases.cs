// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/changelog.test.ts (parseChangelog, #11264) and
// packages/coding-agent/src/modes/interactive/interactive-mode.ts (the resume command printed on exit, #12237).
using PiSharp.Cli.Interactive.Mode.Utilities;
using static Expect;

/// <summary>Sync 1.6.0 cases of the cli-session package whose native code lives in the interactive host.</summary>
internal static class Sync160CliSessionCases
{
    private static readonly string[] Regular = ["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular"];

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // Runs in the TUI (startup notes, /changelog), where a direct write draws over the screen (#11264).
        yield return ("sync160.changelog.unreadable-changelog-has-no-entries-and-prints-nothing", Sync(() =>
        {
            using var dir = new TempDir();
            var path = Path.Combine(dir.Path, "CHANGELOG.md");
            File.WriteAllText(path, "# Changelog\n\n## [1.6.0] - 2026-11-20\n\n- one\n");
            Equal(1, Changelog.ParseChangelog(path).Count, "readable changelog");
            var previousError = Console.Error; var previousOut = Console.Out;
            using var error = new StringWriter(); using var output = new StringWriter();
            Console.SetError(error); Console.SetOut(output);
            try
            {
                // Upstream reads a directory (EISDIR); File.Exists is false for one here, so the unreadable file is one another handle
                // holds exclusively (Windows) or a directory (elsewhere: no entries without a read).
                using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Equal(0, OperatingSystem.IsWindows() ? Changelog.ParseChangelog(path).Count : Changelog.ParseChangelog(dir.Path).Count, "unreadable changelog");
                Equal(0, Changelog.ParseChangelog(dir.Path).Count, "a directory");
            }
            finally { Console.SetError(previousError); Console.SetOut(previousOut); }
            Equal("", error.ToString(), "nothing on stderr");
            Equal("", output.ToString(), "nothing on stdout");
        }));

        // #12237: the command is on its own line after the label, so a triple-click selects it.
        yield return ("sync160.exit.resume-command-is-on-its-own-line", async () =>
        {
            await using var pi = new InteractiveHarness("sync160-resume-line");
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("hello there");
            await pi.WaitFor("Hello from the fake model.");
            Equal(0, await pi.Quit(), "exit code");
            var stdout = pi.Stdout.ToString();
            const string label = "\u001b[2mTo resume this session:\u001b[22m\n";
            var at = stdout.IndexOf(label, StringComparison.Ordinal);
            Check(at >= 0, "the label ends its line: " + stdout);
            var rest = stdout[(at + label.Length)..];
            var line = rest[..rest.IndexOf('\n')];
            Check(line.Contains("--session ", StringComparison.Ordinal) && !line.Contains("To resume", StringComparison.Ordinal), "the command line: " + line);
            Equal(rest.Length, line.Length + 1, "the command line is the last line");
        });
    }
}
