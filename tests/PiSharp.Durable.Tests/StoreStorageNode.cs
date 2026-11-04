// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/store-storage-node.ts and the pass vitest.store.config.ts wires.
//
// Upstream's store pass swaps the harness test files' SQLite opener (openNodeSqliteStorage) for one that opens Durable's storage on Chord's
// store over the same file, and reruns those files unchanged. Here the harness groups are parameterized by their storage (fresh and
// reopenable locations), so the store pass is those groups called again under a "store." prefix with OpenNodeSqliteStorageAsync as the
// opener: harness.<group>.store.<case>. A table prefix becomes nothing, as upstream's opener drops it (the owner name separates).
// Rerun: the groups of upstream's list that take their storage from the caller (compaction, conversations, generation-recovery, inbox,
// lifecycle, ownership, structured, submissions, task-graph, tasks-recovery, tools-recovery, view); every one runs on the store fresh and,
// where the group has reopen cases, across reopen. Not rerun: the groups of upstream's list that open their own SQLite file
// (input-entries, nested-tools, nested-tools-restart, read-only, scheduler-indexes, scheduler-retention; ports that take no storage) and
// the files later areas port (after-run, blocked, continue, restart-failure, tool-hooks, turn-limit). Area sleep adds harness.sleep.store.*:
// its reopen case opens the file as a StoreStorage, and its MemoryStorage cases run on a fresh StoreStorage.
using PiSharp.Durable;
using PiSharp.Durable.Sqlite;

internal static partial class Program
{
    /// <summary>A StoreStorage on a store over the file at <paramref name="path"/>, as SqliteStorage.OpenAsync's place in the store pass
    /// (upstream <c>openNodeSqliteStorage</c>): a table prefix becomes nothing.</summary>
    internal static Task<IStorage> OpenNodeSqliteStorageAsync(string path, SqliteStorageOptions? options = null) =>
        OpenStoreStorageAsync(path, options is null ? null : options with { Prefix = null });

    /// <summary>A fresh StoreStorage on a new file.</summary>
    private static IStorage FreshStore(string name) => OpenNodeSqliteStorageAsync(Path.Combine(Temp(name), "store.sqlite")).GetAwaiter().GetResult();

    /// <summary>A new file location: each call opens a StoreStorage on it.</summary>
    private static Func<IStorage> StoreFile(string name)
    {
        var path = Path.Combine(Temp(name), "store.sqlite");
        return () => OpenNodeSqliteStorageAsync(path).GetAwaiter().GetResult();
    }

    /// <summary>A new file location for the async reopen groups.</summary>
    private static StructuredTests.OpenLocation StoreLocation(string name)
    {
        var path = Path.Combine(Temp(name), "store.sqlite");
        return () => OpenNodeSqliteStorageAsync(path);
    }

    private static IEnumerable<(string, Func<Task>)> StoreStorageHarnessCases() =>
    [
        .. CompactionTests.Cases("harness.compaction.store.", () => FreshStore("compaction-store"), () => StoreLocation("compaction-store-reopen").Invoke),
        .. HarnessConversationTests.ConversationCases("harness.conversations.store.", () => FreshStore("conversations-store"),
            () => StoreFile("conversations-store-reopen")),
        .. GenerationRecoveryTests.Cases("harness.generation-recovery.store.", () => StoreFile("generation-recovery-store")),
        .. InboxTests.Cases("harness.inbox.store.", () => FreshStore("inbox-store"), () => StoreFile("inbox-store-reopen")),
        .. LifecycleTests.Cases("harness.lifecycle.store.", () => StoreLocation("lifecycle-store")(), () => StoreLocation("lifecycle-store-reopen")),
        .. OwnershipTests.Cases("harness.ownership.store.", () => StoreLocation("ownership-store")(), () => StoreLocation("ownership-store-reopen")),
        .. StructuredTests.Cases("harness.structured.store.", () => StoreLocation("structured-store")(), () => StoreLocation("structured-store-reopen")),
        .. SubmissionsTests.Cases("harness.submissions.store.", () => FreshStore("submissions-store"), () => StoreFile("submissions-store-reopen")),
        .. TaskGraphTests.Cases("harness.task-graph.store.", () => Task.FromResult(FreshStore("task-graph-store")),
            new TaskGraphTests.Reopenable("store", () =>
            {
                var path = Path.Combine(Temp("task-graph-store-reopen"), "store.sqlite");
                return () => OpenNodeSqliteStorageAsync(path);
            })),
        .. HarnessRecoveryTests.RecoveryCases("harness.tasks-recovery.store.", new HarnessRecoveryTests.Backend(() => FreshStore("tasks-recovery-store"), () =>
        {
            var path = Path.Combine(Temp("tasks-recovery-store-reopen"), "store.sqlite");
            return () => OpenNodeSqliteStorageAsync(path);
        })),
        .. ToolsRecoveryTests.Cases("harness.tools-recovery.store.", () => StoreFile("tools-recovery-store")),
        .. ViewTests.Cases("harness.view.store.", () => Task.FromResult(FreshStore("view-store"))),
        .. SleepTests.Cases("harness.sleep.store.", () => FreshStore("sleep-store"), path => OpenNodeSqliteStorageAsync(path)),
    ];
}
