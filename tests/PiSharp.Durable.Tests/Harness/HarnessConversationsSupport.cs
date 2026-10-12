// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): support for packages/durable/test/harness-conversations.test.ts and
// packages/durable/test/harness-context.test.ts (HarnessConversationsCases.cs, HarnessContextCases.cs). Both groups live in their own class,
// HarnessConversationTests, so their helpers cannot collide with other HarnessTests partials; the shared helpers of HarnessSupport.cs are
// imported with `using static HarnessTests`.
//
// Upstream runs both files on MemoryStorage (harness-conversations' reopen case on a SQLite file). Every storage-agnostic case also runs on
// a fresh JSONL directory and a fresh SQLite file, as the harness.tasks groups do; the reopen case runs on SQLite (upstream's backend) and on
// JSONL, closing each storage before the next open.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessConversationsContextCases()
    {
        static IStorage Memory() => new MemoryStorage();
        static IStorage Jsonl() => JsonlStorage.OpenAsync(Temp("harness-conv-jsonl"), DurableContext.Background).GetAwaiter().GetResult();
        static IStorage Sqlite() => SqliteStorage.OpenAsync(Path.Combine(Temp("harness-conv-sqlite"), "harness.sqlite")).GetAwaiter().GetResult();
        // Reopenable stores: one location, a fresh storage per open.
        static Func<IStorage> SqliteFile()
        {
            var path = Path.Combine(Temp("harness-conv-reopen-sqlite"), "session.sqlite");
            return () => SqliteStorage.OpenAsync(path).GetAwaiter().GetResult();
        }
        static Func<IStorage> JsonlDirectory()
        {
            var path = Temp("harness-conv-reopen-jsonl");
            return () => JsonlStorage.OpenAsync(path, DurableContext.Background).GetAwaiter().GetResult();
        }
        return
        [
            .. HarnessConversationTests.ConversationCases("harness.conversations.", Memory, SqliteFile),
            .. HarnessConversationTests.ConversationCases("harness.conversations.jsonl.", Jsonl, JsonlDirectory),
            .. HarnessConversationTests.ConversationCases("harness.conversations.sqlite.", Sqlite, null),
            .. HarnessConversationTests.ContextCases("harness.context.", Memory),
            .. HarnessConversationTests.ContextCases("harness.context.jsonl.", Jsonl),
            .. HarnessConversationTests.ContextCases("harness.context.sqlite.", Sqlite),
        ];
    }
}

/// <summary>The harness.conversations.* and harness.context.* case groups and their support.</summary>
internal static partial class HarnessConversationTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Deep equality of two context views (vitest's <c>toEqual</c>): head, entries, contributions and messages as JSON.</summary>
    internal static void SameView(ContextView expected, ContextView actual, string what)
    {
        var left = ViewJson(expected);
        var right = ViewJson(actual);
        Check(JsonValue.DeepEquals(left, right), $"{what}: expected {left.ToJson()}, actual {right.ToJson()}");
    }

    /// <summary>A context view as JSON; an absent head is JSON null.</summary>
    internal static JsonObjectValue ViewJson(ContextView view) => SessionTests.Obj(
        ("head", view.Head?.ToJson() ?? (JsonValue)JsonValue.Null),
        ("entries", JsonValue.Array(view.Entries.Select(entry => (JsonValue)entry.ToJson()))),
        ("contributions", JsonValue.Array(view.Contributions.Select(messages => (JsonValue)JsonValue.Array(messages)))),
        ("messages", JsonValue.Array(view.Messages)));

    /// <summary>Entry IDs of <paramref name="entries"/> (upstream <c>ids</c>).</summary>
    internal static List<EntryId> Ids(IEnumerable<EntryRecord> entries) => [.. entries.Select(entry => entry.Id)];

    /// <summary>Described model messages of a view (upstream <c>view.messages.map(describeMessage)</c>).</summary>
    internal static List<string> Described(IEnumerable<JsonValue> messages) => [.. messages.Select(HarnessTests.DescribeMessage)];

    /// <summary>
    /// The first failure of an assertion made inside a task handler. Upstream asserts inside handlers, where a failure faults the task; here
    /// the handler records it and rethrows, and the test body rethrows the recorded failure first so it names itself.
    /// </summary>
    internal sealed class HandlerFailures
    {
        private Exception? first;

        public async Task Guard(Func<Task> body)
        {
            try
            {
                await body();
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref first, error, null);
                throw;
            }
        }

        public void ThrowIfAny()
        {
            if (Volatile.Read(ref first) is { } error) throw new InvalidOperationException("A handler assertion failed: " + error.Message, error);
        }
    }

    /// <summary>
    /// A storage decorator that counts scanned entry rows (upstream countingSetup's Proxy). When <see cref="HoldNextScan"/> is set, the next
    /// range scan (limit above one) waits for it; bounds probes (one row, on the Session line) do not. <see cref="MapScan"/>, when set, maps
    /// every scanned page.
    /// </summary>
    internal sealed class CountingStorage(IStorage inner) : IStorage
    {
        private int rows;
        private Task? hold;

        public int Rows => Volatile.Read(ref rows);

        public Task? HoldNextScan
        {
            get => Volatile.Read(ref hold);
            set => Volatile.Write(ref hold, value);
        }

        public Func<Page<EntryRecord>, Page<EntryRecord>>? MapScan { get; init; }

        public async Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
        {
            var held = limit > 1 ? Interlocked.Exchange(ref hold, null) : null;
            if (held is not null) await held;
            var page = await inner.ScanEntriesAsync(query, limit, cursor, context);
            Interlocked.Add(ref rows, page.Items.Length);
            return MapScan is { } map ? map(page) : page;
        }

        public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, DurableContext context) => inner.CommitAsync(writes, context);
        public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => inner.MintIdAsync<TId>();
        public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => inner.GetDocumentAsync(id, at, context);
        public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) =>
            inner.FindDocumentAsync(address, at, context);
        public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => inner.GetConversationAsync(id, context);
        public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanConversationsAsync(query, limit, cursor, context);
        public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => inner.GetEntryAsync(id, context);
        public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) =>
            inner.GetEntryAsync(conversationId, id, context);
        public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
            inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => inner.GetTaskAsync(id, context);
        public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanTasksAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => inner.GetSubmissionAsync(id, context);
        public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanSubmissionsAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
            inner.GetSubmissionByRequestAsync(conversationId, requestId, context);
        public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            inner.ScanDocumentsAsync(query, limit, cursor, context);
        public Task CloseAsync(DurableContext context) => inner.CloseAsync(context);
    }

    /// <summary>A Harness over a <see cref="CountingStorage"/>, its registry and root, and the root's first entry.</summary>
    internal sealed record CountingHarness(IHarness Harness, IRegistry Registry, IConversation Root, EntryRecord First, CountingStorage Storage);

    /// <summary>
    /// A Harness over a <see cref="CountingStorage"/> that counts scanned entry rows, with a 21-entry root transcript (upstream
    /// <c>countingSetup</c>).
    /// </summary>
    internal static async Task<CountingHarness> CountingSetup(IStorage inner, HarnessSettings? settings = null, Func<double>? now = null,
        TimeProvider? timeProvider = null, Func<Page<EntryRecord>, Page<EntryRecord>>? mapScan = null)
    {
        var storage = new CountingStorage(inner) { MapScan = mapScan };
        var registry = Registry.Create();
        var harness = await PiSharp.Durable.Harness.OpenAsync(storage, new HarnessOptions
        {
            Models = EmptyDurableModels.Instance,
            Registry = registry,
            Settings = settings,
            Now = now,
            TimeProvider = timeProvider,
        }, Context);
        var root = await harness.RootAsync(Context);
        var first = await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, Message(HarnessTests.User("first"))), Context);
        for (var index = 0; index < 20; index++)
            await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, Message(HarnessTests.Assistant($"old {index}"))), Context);
        return new CountingHarness(harness, registry, root, first, storage);
    }

    /// <summary>A <c>message</c> entry draft (or <paramref name="kind"/>) with one model message.</summary>
    internal static EntryDraft Message(JsonValue model, string kind = "message") => new() { Kind = kind, Model = JsonValue.Array(model) };

    /// <summary>The terminal outcome JSON of a settled task record.</summary>
    internal static JsonValue OutcomeOf(TaskRecord record) => ((SettlingTaskState)record.State).Outcome.ToJson();

    /// <summary>Upstream <c>DONE</c>'s outcome.</summary>
    internal const string DoneOutcome = """{"status":"completed","result":null}""";

    /// <summary>Upstream <c>DONE</c>: completes with null.</summary>
    internal static TaskState Done() => NextTaskState.Completed(null);

    /// <summary>Upstream <c>ABORTED</c>: aborted without a reason.</summary>
    internal static TaskState AbortedState() => NextTaskState.Aborted();

    /// <summary>A one-phase task named <paramref name="name"/> whose abort handler commits <see cref="AbortedState"/>.</summary>
    internal static DurableTask ContextTask(string name, Func<RunningTask, ITaskRuntime, DurableContext, Task> run) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = 1,
        Initial = _ => SessionTests.O("""{"phase":"run"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (task, runtime, ctx) => run(task, runtime, ctx) },
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedState(), ctx),
    });

    /// <summary>A task that reads its conversation's context once, named by its input (upstream <c>probeTask</c>).</summary>
    internal static DurableTask ProbeTask(Func<string, Func<Task<ContextView>>, DurableContext, Task> read) =>
        ContextTask("test.context-probe", async (task, runtime, ctx) =>
        {
            var name = SessionTests.S(((JsonObjectValue)task.Input)["name"]);
            await read(name, () => runtime.ContextAsync(runtime.ConversationId, ctx), ctx);
            await runtime.CommitAsync((_, _) => Done(), ctx);
        });

    /// <summary>Task options of a conversation-owned task of <paramref name="conversation"/>.</summary>
    internal static TaskOptions OwnedBy(ConversationId conversation) => new(TaskOwnership.Conversation) { ConversationId = conversation };

    /// <summary>Rows scanned per named read, filled from task handlers.</summary>
    internal sealed class RowLog
    {
        private readonly Dictionary<string, int> rows = [];
        public void Set(string name, int count) { lock (rows) rows[name] = count; }
        public Dictionary<string, int> Snapshot() { lock (rows) return new(rows); }
    }

    /// <summary>Asserts <paramref name="log"/> holds exactly <paramref name="expected"/> (upstream <c>toEqual</c> on a record of counts).</summary>
    internal static void SameRows(IReadOnlyList<(string Name, int Rows)> expected, RowLog log, string what)
    {
        var actual = log.Snapshot();
        static string Show(IEnumerable<(string Name, int Rows)> rows) => "{" + string.Join(", ", rows.Select(row => $"{row.Name}: {row.Rows}")) + "}";
        Check(actual.Count == expected.Count && expected.All(row => actual.TryGetValue(row.Name, out var count) && count == row.Rows),
            $"{what}: expected {Show(expected)}, actual {Show(actual.Select(pair => (pair.Key, pair.Value)))}");
    }
}
