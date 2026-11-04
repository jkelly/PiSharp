// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-read-only.test.ts (be14d9732), ported in the Pi
// v1.5.0 sync (area harness-options). A read-only Harness: a viewer of a session another process drives. Verbatim apart from the
// adaptations in HarnessSupport.cs and ChatSupport.cs, and these:
// - openNodeSqliteStorage is SqliteStorage.OpenAsync, and head() reads durable_metadata through Microsoft.Data.Sqlite (a pool-free
//   connection, as upstream's DatabaseSync is one connection).
// - Area sleep: the first case checks Harness.Wake() and HarnessOptions.OnWake as upstream does.
// - TaskRecord.restarts does not exist here yet (area restarts): the last case checks the pending status only.
// - ReadOnlySession is ReadOnlySessionException; root.commit(() => "nothing") is a CommitAsync whose callback writes nothing.
using Microsoft.Data.Sqlite;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessReadOnlyCases() => ReadOnlyTests.Cases();
}

/// <summary>The harness.read-only.* cases (upstream harness-read-only.test.ts).</summary>
internal static class ReadOnlyTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static string SqlitePath() => Path.Combine(Temp("read-only"), "session.sqlite");

    private static async Task<IHarness> OpenReadOnly(string path, ChatSetup setup, Action<Wake>? onWake = null) =>
        await Harness.OpenAsync(await SqliteStorage.OpenAsync(path), new HarnessOptions
        {
            Models = setup.Models,
            Registry = setup.Registry,
            ReadOnly = true,
            OnReport = setup.Record,
            OnWake = onWake,
        }, Context);

    /// <summary>The next sequence the file would commit, read beside every open handle; null before the schema exists.</summary>
    private static long? Head(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE name = 'durable_metadata'";
        if (exists.ExecuteScalar() is null) return null;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT next_seq FROM durable_metadata WHERE singleton = 1";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A session whose root generation is running in another Harness, still open, waiting for the model.</summary>
    private static async Task<OpenedChat> RunningSession(string path, ChatSetup setup)
    {
        var (step, reached) = Unanswered();
        setup.Faux.SetResponses(step);
        var driver = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
        await driver.Root.SubmitAsync(SubmissionDraft.Input("slow"), Context);
        await reached;
        return driver;
    }

    private static async Task<TaskId> RunTaskId(IHarness harness, IConversation root) =>
        new((long)((JsonNumberValue)((JsonObjectValue)(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!["run"])["taskId"]).Value);

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return Case("harness.read-only.opens-a-session-with-work-running-elsewhere-without-writing", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var driver = await RunningSession(path, setup);
            var before = Head(path);
            var wakes = new SyncList<Wake>();

            var viewer = await OpenReadOnly(path, Setup(), wakes.Add);
            var root = await viewer.RootAsync(Context);
            Seq(["pi.user"], (await AllEntries(root)).Select(entry => entry.Kind), "kinds");
            var run = await RunTaskId(viewer, root);
            Equal(DurableTaskStatus.Running, (await viewer.GetTaskAsync(run, Context))?.State.Status, "the run's status");
            var inspection = await viewer.InspectAsync(Context);
            Equal(HarnessScheduling.Paused, inspection.Scheduling, "scheduling");
            Equal(new Wake(false), viewer.Wake(), "wake");
            await viewer.CloseAsync(Context);

            Equal(before, Head(path), "head");
            Seq([], wakes.Snapshot(), "wakes");
            await driver.Harness.CloseAsync(Context);
        });

        yield return Case("harness.read-only.refuses-writes-and-scheduling-and-stays-usable", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage("one"));
            var driver = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            await (await driver.Root.SubmitAsync(SubmissionDraft.Input("hi"), Context)).WaitAsync(Context);
            await driver.Harness.CloseAsync(Context);
            var before = Head(path);

            var viewerSetup = Setup();
            var viewer = await OpenReadOnly(path, viewerSetup);
            var root = await viewer.RootAsync(Context);
            await ThrowsAsync<ReadOnlySessionException>(() => root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.note" }), Context),
                "a commit with writes");
            await ThrowsAsync<ReadOnlySessionException>(() => root.SubmitAsync(SubmissionDraft.Input("again"), Context), "submit");
            Throws<ReadOnlySessionException>(viewer.Resume, "resume");
            await ThrowsAsync<ReadOnlySessionException>(() => viewer.WaitForIdleAsync(Context), "waitForIdle");
            await ThrowsAsync<ReadOnlySessionException>(() => root.AbortAsync(Context), "abort");
            await ThrowsAsync<ReadOnlySessionException>(
                () => viewer.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context), "createConversation");

            // Still healthy: reads work, and a commit that writes nothing succeeds.
            Equal("nothing", await root.CommitAsync(_ => "nothing", Context), "a commit without writes");
            Equal(2, (await AllEntries(root)).Count, "entries");
            await viewer.CloseAsync(Context);
            Equal(before, Head(path), "head");
            Equal(0, viewerSetup.Reports.Snapshot().Count, "reports");
        });

        yield return Case("harness.read-only.refuses-to-create-the-root-of-an-empty-storage", async () =>
        {
            var path = SqlitePath();
            var viewer = await OpenReadOnly(path, Setup());
            await ThrowsAsync<ReadOnlySessionException>(() => viewer.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") }), "root");
            await viewer.CloseAsync(Context);
            Equal(1L, Head(path), "head");
        });

        yield return Case("harness.read-only.leaves-recovery-to-the-next-writable-open", async () =>
        {
            var path = SqlitePath();
            var setup = Setup();
            var driver = await RunningSession(path, setup);
            var taskId = await RunTaskId(driver.Harness, driver.Root);

            var viewer = await OpenReadOnly(path, Setup());
            await viewer.CloseAsync(Context);

            // The writable open recovers the task once, as if the viewer had never opened it.
            var writer = await OpenChat(await SqliteStorage.OpenAsync(path), Setup());
            var record = await writer.Harness.GetTaskAsync(taskId, Context);
            Equal(DurableTaskStatus.Pending, record?.State.Status, "status");
            await writer.Harness.CloseAsync(Context);
            await driver.Harness.CloseAsync(Context);
        });
    }
}
