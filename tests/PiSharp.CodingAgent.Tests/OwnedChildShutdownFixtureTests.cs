using OwnedChildShutdownFixture;
using System.Text.Json;

internal static class OwnedChildShutdownFixtureTests
{
    internal const string Prefix = "two-phase owned physical child ";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost) =>
    [
        (Prefix + "exit and both redirected cleanup originals are joined", () => Exercise(dotnetHost, false)),
        (Prefix + "exit and both close failures survive original joins and repeated disposal", () => Exercise(dotnetHost, true))
    ];

    private static async Task Exercise(string dotnetHost, bool failCleanup)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-owned-shutdown-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Entry.Mark(root, "fixture.owned");
        var entry = new Entry(new(root, dotnetHost, typeof(OwnedChildShutdownFixtureTests).Assembly.Location, failCleanup));
        Task? original = null; AggregateException? failure = null;
        try
        {
            // Same Entry and DisposeAsync used by the admitted native extension loader; no fabricated child runner.
            await entry.InitializeAsync(null!, default);
            await Milestone(root, "child.ready");
            using var ownerIdentity = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "owner.started")));
            using var childIdentity = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "child.ready")));
            var actualChild = childIdentity.RootElement.GetProperty("processId").GetInt32();
            Check(actualChild > 0 && actualChild != Environment.ProcessId &&
                actualChild == ownerIdentity.RootElement.GetProperty("processId").GetInt32(), "Child readiness did not match the actual owned Process handle.");
            original = entry.DisposeAsync().AsTask();
            Check(ReferenceEquals(original, entry.DisposeAsync().AsTask()), "Repeated disposal replaced the original join task.");
            await Milestone(root, "dispose.entered");
            Check(!original.IsCompleted && !File.Exists(Path.Combine(root, "exit.joined")), "Owner skipped the held physical child exit.");
            Entry.Mark(root, "exit.release");
            await Milestone(root, "exit.joined"); await Milestone(root, "stdout.eof"); await Milestone(root, "stderr.eof");
            Check(!original.IsCompleted, "Actual exit/EOF detached original redirected stream cleanup.");
            Entry.Mark(root, "stdout.release"); await Milestone(root, "stdout.closed");
            Check(!original.IsCompleted && !File.Exists(Path.Combine(root, "stderr.closed")), "Original stderr cleanup was omitted.");
        }
        finally
        {
            Entry.ReleaseAll(root); original ??= entry.DisposeAsync().AsTask();
            try { await original; } catch (AggregateException error) when (failCleanup) { failure = error; }
        }
        Check(File.Exists(Path.Combine(root, "owner.joined")) && File.Exists(Path.Combine(root, "stderr.closed")), "Owner returned before its original exit/streams joined.");
        if (failCleanup)
        {
            Check(failure is not null, "Real cleanup failure became success.");
            var originals = failure!.Flatten().InnerExceptions;
            foreach (var message in new[] { "AUTHORED_OWNED_CHILD_EXIT_CLEANUP", "AUTHORED_OWNED_CHILD_STDOUT_CLOSE", "AUTHORED_OWNED_CHILD_STDERR_CLOSE" })
                Check(originals.Count(error => error is IOException && error.Message == message) == 1, "A joined original cleanup error was lost or duplicated.");
            try { await entry.DisposeAsync(); throw new InvalidOperationException("Repeated failed disposal became success."); }
            catch (AggregateException repeated) { Check(ReferenceEquals(failure, repeated), "Repeated disposal replaced original failure instances."); }
        }
        // Retain the small marker directory as physical evidence; do not delete evidence in a finally block.
    }
    private static async Task Milestone(string root, string name)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Entry.Wait(root, name, deadline.Token); // Join the diagnostic poll itself; no abandoned waiter.
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
