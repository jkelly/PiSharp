// The release watcher sample (decision 0010, M5): samples/Durable.ReleaseWatcher's offline reference scenario end to end, against a device
// in Kestrel with a recorded feed on loopback, a manual clock and the faux classifier.
using PiSharp.Durable.Hosting;
using PiSharp.Durable.ReleaseWatcher;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ReleaseWatcherCases()
    {
        yield return Case("release-watcher.offline-scenario", async () =>
        {
            var log = new StringWriter();
            var report = await OfflineDemo.RunAsync(Temp("release-watcher"), log);
            var output = log.ToString();
            Check(output.Contains("restricted to schedule", StringComparison.Ordinal), "setup issues a trigger credential for the schedule");
            Equal(2, report.FirstTickJudged, "the first tick's two releases are judged");
            Equal(1, report.FirstTickResearched, "the breaking release is researched once its judgment is approved");
            Equal(2, report.SecondTickJudged, "the changed and the new release are judged");
            Equal(2, report.SecondTickReplayed, "the releases judged before replay their judgments");
            Equal("Widget 1.2.0", report.ReviewedRelease, "the security fix waits for review");
            Equal("confirm", report.ReviewRoute, "its medium confidence routes it to a person");
            Equal("approved", report.ReviewDecision, "the person approves it");
            Equal(5, report.Audits.Count, "four judgments and one research run");
            Check(report.Audits.All(audit => audit.EndsWith("audit passed", StringComparison.Ordinal)), "every completion audit passed: " + string.Join("; ", report.Audits));
            Check(report.Audits.Contains("Widget 1.1.0 judged breaking, approved: audit passed"), "the breaking release is judged and approved automatically");
            Check(report.Audits.Count(audit => audit.StartsWith("Widget 1.0.0 judged ignore", StringComparison.Ordinal)) == 2, "the changed release is judged twice");
            Equal(1, report.ReviewedReservations, "the reviewed judgment reserved one classification");
            Equal(true, report.ReviewedCostKnown, "with reported usage its cost is known");
            Equal(186L, report.ReviewedTokens, "the recorded usage");
            Check(report.RequestsToday > 0, "the day's budget counts the model requests");
            Equal(2, report.Weights.Count, "feedback weighted two labels");
            Check(Math.Abs(report.Weights["breaking"] - 0.10) < 1e-9, "more on the breaking finding");
            Check(Math.Abs(report.Weights["ignore"] + 0.15) < 1e-9, "less, undone, then less again counts once; its retry replays");
            Equal(4L, report.ProfileRevision, "more, less, undo and less are four events");
            Equal(true, report.SetupRepeatable, "setup after the restart finds the same session, policy version and schedule");
            Equal("started", report.UnchangedTick, "the tick after the restart runs the check");
            Equal(0, report.UnchangedTickArtifacts, "an unchanged feed keeps no evidence list");
            Equal(report.ArtifactsBeforeRestart, report.ArtifactsAfterRestart, "no duplicate artifact after the restart");
            Equal(9, report.ArtifactsAfterRestart, "two evidence lists, four findings, two angle findings and a synthesis");
            Equal(report.JudgmentsBeforeRestart, report.JudgmentsAfterRestart, "no duplicate judgment after the restart");
            Equal(4, report.ReplayedAfterRestart, "every judgment replays");
        });
        // Decision 0010, M6: a backup of the stopped device, restored into a clean data root, reproduces the watcher run and its evidence.
        yield return Case("release-watcher.a-restored-backup-replays-the-run-and-its-evidence", async () =>
        {
            var root = Temp("release-watcher-restore");
            var log = new StringWriter();
            var report = await OfflineDemo.RunAsync(root, log);
            var backup = Path.Join(root, "backup");
            var manifest = DeviceBackup.Create(Path.Join(root, "data"), backup);
            Check(DeviceBackup.Verify(backup).Ok, "the backup verifies");
            Check(!manifest.IncludesSecrets, "without the device token");
            var restored = Path.Join(root, "restored");
            DeviceBackup.Restore(backup, restored);
            var replay = await OfflineDemo.ReplayAsync(restored, Path.Join(root, "work"), log);
            Equal(0, replay.Judged, "nothing is judged again on the restored device");
            Equal(report.ReplayedAfterRestart, replay.Replayed, "every judgment replays with its restored evidence");
            Equal(report.JudgmentsAfterRestart, replay.Judgments, "the same judgment runs");
            Equal(report.ArtifactsAfterRestart, replay.Artifacts, "the same artifacts");
            Check(replay.Evidence.Count >= 3 && replay.Evidence.Values.All(text => text.Length > 0), "the evidence texts are restored: " + replay.Evidence.Count);
            var original = await OfflineDemo.ReplayAsync(Path.Join(root, "data"), Path.Join(root, "work"), log);
            Equal(string.Join("\n", original.Evidence.Select(pair => pair.Key + "=" + pair.Value)), string.Join("\n", replay.Evidence.Select(pair => pair.Key + "=" + pair.Value)),
                "the restored evidence is the original's");
        });
    }
}
