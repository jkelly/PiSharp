// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/modes/interactive/session-share.ts (shareViaGist: a
// failed spawn of the GitHub CLI resolves with its error instead of crashing pi). Authored from the pinned source; never captured.
using PiSharp.CodingAgent.Export;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160ShareCases() =>
    [
        Case("sync160.share.gist-start-failure-reports-the-spawn-error", async () =>
        {
            var root = Temp("sync160-gist-start"); var fixture = WriteFixture(root);
            var session = new SessionShareSession(() => SessionExportSource.Open(fixture), () => new SessionExportAgentState("sys", []),
                (path, _) => { File.WriteAllText(path, "<html>"); return Task.CompletedTask; }, new FakeRadius(null));
            var ui = new RecordingUi();
            // gh answered `auth status` but could not start for `gist create`: a null exit code with the start error as stderr.
            await new SessionShare { Processes = new FakeProcesses { Gist = new(null, "", "spawn gh ENOENT") }, TempRoot = root, Environment = NoEnvironment }
                .ShareSessionAsync(session, ui.Ui);
            Equal("loader:Creating gist...|restore|error:Failed to create gist: spawn gh ENOENT", ui.Log, "the spawn error is shown");
        }),
        Case("sync160.share.a-program-that-cannot-start-yields-its-error-not-an-exception", async () =>
        {
            var result = await new SystemShareProcessRunner().RunAsync("pisharp-sync160-missing-" + Guid.NewGuid().ToString("N"), ["gist", "create"], CancellationToken.None);
            Check(result.ExitCode is null, "a program that cannot start has no exit code");
            Equal("", result.Stdout, "stdout");
            Check(result.Stderr.Length > 0, "the start error is the stderr text");
        }),
    ];
}
