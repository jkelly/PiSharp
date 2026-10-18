// PiSharp-only (decision 0008): the three example jobs durable-pi ships (apps/dpi-sdk/src/jobs.ts, MIT), rewritten to run on any device:
// files are counted and watched with .NET instead of ls and cksum, and the only program called is git, which the release check tolerates
// missing.
using System.Globalization;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>The device's example jobs: Workspace snapshot, Release check and Change watch.</summary>
public static class SampleJobs
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs", ".idea" };

    /// <summary>All three.</summary>
    public static IReadOnlyList<JobDefinition> All { get; } = [WorkspaceSnapshot(), ReleaseCheck(), ChangeWatch()];

    /// <summary>Counts the working directory's files by extension and saves the summary to App State.</summary>
    public static JobDefinition WorkspaceSnapshot() => new()
    {
        Id = "workspace-snapshot",
        Title = "Workspace snapshot",
        Summary = "Lists the working directory, counts files by extension and saves the summary to App State.",
        ResultPath = "/jobs/workspace-snapshot",
        EstimatedSeconds = 6,
        Phases =
        [
            new JobPhase("scan", "Scan", "List the working directory")
            {
                Run = async context =>
                {
                    var files = Files(context.Cwd, 20_000);
                    await context.NoteAsync($"{files.Count} files under {context.Cwd}").ConfigureAwait(false);
                    return JobPhaseResult.Next($"{files.Count} files", JsonValue.Object([new("files", files.Count)]));
                },
            },
            new JobPhase("measure", "Measure", "Count files by extension")
            {
                Run = context =>
                {
                    var byExtension = Files(context.Cwd, 20_000)
                        .GroupBy(file => Path.GetExtension(file) is { Length: > 0 } extension ? extension.ToLowerInvariant() : "(none)")
                        .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(10)
                        .Select(group => new KeyValuePair<string, JsonValue>(group.Key, group.Count()));
                    var counts = JsonValue.Object(byExtension);
                    return Task.FromResult(JobPhaseResult.Next($"{counts.Count} extensions", context.Data.SetItem("extensions", counts)));
                },
            },
            new JobPhase("save", "Save", "Save the summary to App State")
            {
                Run = async context =>
                {
                    var summary = context.Data.SetItem("at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    await context.SaveStateAsync("/jobs/workspace-snapshot", summary).ConfigureAwait(false);
                    return JobPhaseResult.Next("Saved /jobs/workspace-snapshot", summary);
                },
            },
            new JobPhase("verify", "Verify", "Read the summary back")
            {
                Run = context =>
                {
                    var saved = context.ReadState("/jobs/workspace-snapshot");
                    if (!JsonValue.DeepEquals(saved, context.Data)) throw new InvalidOperationException("The saved summary does not match.");
                    return Task.FromResult(JobPhaseResult.Next("App State matches", context.Data));
                },
            },
        ],
    };

    /// <summary>Plans a release from git history, has a subagent gather evidence, waits for approval, then publishes and verifies.</summary>
    public static JobDefinition ReleaseCheck() => new()
    {
        Id = "release-check",
        Title = "Release check",
        Summary = "Plans the release, gathers evidence with a subagent, waits for your approval, then publishes and verifies.",
        ResultPath = "/jobs/release-check",
        EstimatedSeconds = 40,
        Inputs = [new JobInput("commits", 10, 1, 50, "How many recent commits to review.")],
        Phases =
        [
            new JobPhase("plan", "Plan", "Count the commits to review")
            {
                Run = async context =>
                {
                    var wanted = (int)context.Number("commits");
                    int available;
                    try
                    {
                        available = int.Parse((await context.RunAsync("git", "rev-list", "--count", "HEAD").ConfigureAwait(false)).Trim(), CultureInfo.InvariantCulture);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        await context.NoteAsync("Not a git repository, or git is missing: reviewing the working directory instead.").ConfigureAwait(false);
                        available = 0;
                    }
                    var commits = Math.Min(wanted, available);
                    return JobPhaseResult.Next($"{commits} commits to review", JsonValue.Object([new("commits", commits)]));
                },
            },
            new JobPhase("gather", "Gather", "A subagent summarises the changes") { UsesAgent = true, Run = async context =>
            {
                var commits = context.Data.TryGetValue("commits", out var count) && count is JsonNumberValue number ? (int)number.Value : 0;
                var question = commits > 0
                    ? $"Summarise the latest {commits} commits of this repository in one line each. Read them with git log if you can."
                    : "Summarise what is in this working directory in three lines.";
                var summary = await context.AskAgentAsync(question).ConfigureAwait(false);
                return JobPhaseResult.Next(Shorten(summary), context.Data.SetItem("summary", summary));
            }},
            new JobPhase("approve", "Approve", "Publish the release record?", JobPhaseKind.Approval),
            new JobPhase("publish", "Publish", "Write the release record to App State")
            {
                Run = async context =>
                {
                    var record = context.Data.SetItem("approvedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    await context.SaveStateAsync("/jobs/release-check", record).ConfigureAwait(false);
                    return JobPhaseResult.Next("Saved /jobs/release-check", record);
                },
            },
            new JobPhase("verify", "Verify", "Read the release record back")
            {
                Run = context =>
                {
                    if (!JsonValue.DeepEquals(context.ReadState("/jobs/release-check"), context.Data))
                        throw new InvalidOperationException("The release record does not match.");
                    return Task.FromResult(JobPhaseResult.Next("Release record verified", context.Data));
                },
            },
        ],
    };

    /// <summary>Takes a baseline of the working directory, checks it a few times for changes, and reports them.</summary>
    public static JobDefinition ChangeWatch() => new()
    {
        Id = "change-watch",
        Title = "Change watch",
        Summary = "Takes a baseline of the working directory, checks it for changes a few times, and reports what changed.",
        ResultPath = "/jobs/change-watch",
        EstimatedSeconds = 30,
        Inputs = [new JobInput("checks", 5, 1, 20, "How many checks."), new JobInput("interval_seconds", 6, 1, 60, "Seconds between checks.")],
        Phases =
        [
            new JobPhase("baseline", "Baseline", "Record every file's size and time")
            {
                Run = context =>
                {
                    var baseline = Fingerprint(context.Cwd);
                    return Task.FromResult(JobPhaseResult.Next($"{baseline.Count} files", JsonValue.Object([new("baseline", baseline), new("changes", JsonArrayValue.Empty)])));
                },
            },
            new JobPhase("watch", "Watch", "Compare the files with the baseline", JobPhaseKind.Repeat)
            {
                Run = async context =>
                {
                    var baseline = (JsonObjectValue)context.Data["baseline"];
                    var now = Fingerprint(context.Cwd);
                    var added = now.Keys.Count(key => !baseline.ContainsKey(key));
                    var removed = baseline.Keys.Count(key => !now.ContainsKey(key));
                    var changed = now.Count(pair => baseline.TryGetValue(pair.Key, out var before) && !JsonValue.DeepEquals(before, pair.Value));
                    var check = JsonValue.Object([new("check", context.Iteration + 1), new("added", added), new("removed", removed), new("changed", changed)]);
                    await context.NoteAsync($"Check {context.Iteration + 1}: {added} added, {removed} removed, {changed} changed").ConfigureAwait(false);
                    var data = context.Data.SetItem("changes", JsonValue.Array(((JsonArrayValue)context.Data["changes"]).Items.Add(check)));
                    var checks = (int)context.Number("checks");
                    var summary = $"Check {context.Iteration + 1} of {checks}";
                    return context.Iteration + 1 >= checks
                        ? JobPhaseResult.Next(summary, data)
                        : JobPhaseResult.Again(summary, context.Number("interval_seconds") * 1000, data);
                },
            },
            new JobPhase("report", "Report", "Save what changed to App State")
            {
                Run = async context =>
                {
                    var changes = (JsonArrayValue)context.Data["changes"];
                    var report = JsonValue.Object([new("checks", changes.Count), new("changes", changes),
                        new("at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))]);
                    await context.SaveStateAsync("/jobs/change-watch", report).ConfigureAwait(false);
                    return JobPhaseResult.Next($"Saved {changes.Count} checks", report);
                },
            },
        ],
    };

    private static List<string> Files(string root, int limit)
    {
        var files = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0 && files.Count < limit)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory)) { if (files.Count >= limit) break; files.Add(file); }
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if (!Skipped.Contains(Path.GetFileName(child))) pending.Push(child);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                // Unreadable directories are skipped.
            }
        }
        return files;
    }

    /// <summary>Size and last write time of at most 2000 files, by path relative to <paramref name="root"/>.</summary>
    private static JsonObjectValue Fingerprint(string root) => JsonValue.Object(Files(root, 2000).Select(file =>
    {
        var info = new FileInfo(file);
        return new KeyValuePair<string, JsonValue>(Path.GetRelativePath(root, file),
            info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "gone");
    }));

    private static string Shorten(string text)
    {
        var line = text.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? "";
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
