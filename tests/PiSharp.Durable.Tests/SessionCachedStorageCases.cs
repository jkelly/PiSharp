// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/session-cached-storage.test.ts, new in Pi v1.5.0 (e5c6182ba,
// sync area publication-caches). The Session's record cache (Session/CachedStorage.cs) over MemoryStorage. Verbatim apart from these:
// - CountingStorage is a ControlledStorage subclass over MemoryStorage (which is sealed here) counting GetTaskAsync calls.
// - A read in flight: upstream's MemoryStorage resolves a read a microtask later, so a publication made right after the call overlaps it.
//   MemoryStorage completes it at once here, so the cases that need reads in flight hold the backend's task reads (HoldTasks) until the
//   publication was applied, and "keeps a read only when no commit overlapped it" runs the read and the commit concurrently (either order
//   keeps the published record, as upstream's, and costs one read).
// - "keeps the written record itself, frozen: no copy": the kept objects are the written ones (ReferenceEquals); records are immutable here,
//   so the freeze has no counterpart, and the case checks instead that the record and its state cannot be changed (every settable property
//   init-only, InitOnly).
using System.Reflection;
using System.Runtime.CompilerServices;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Testing;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionCachedStorageCases() => CachedStorageTests.Cases("session.cached-storage.");
}

internal static class CachedStorageTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static JsonObjectValue O(string json) => SessionTests.O(json);

    private static TaskRecord TaskOf(long id, DurableTaskStatus status = DurableTaskStatus.Pending) => new()
    {
        Id = new TaskId(id),
        ConversationId = new ConversationId(1),
        Kind = "test.task",
        Version = 1,
        Input = JsonValue.Null,
        Background = false,
        AbortRequested = false,
        State = status == DurableTaskStatus.Running ? new RunningTaskState(O("""{"phase":"run"}""")) : new PendingTaskState(O("""{"phase":"run"}""")),
    };

    private static TaskRecord With(TaskRecord task, TaskState state) => new()
    {
        Id = task.Id, ConversationId = task.ConversationId, Kind = task.Kind, Version = task.Version, Input = task.Input,
        Background = task.Background, AbortRequested = task.AbortRequested, State = state,
    };

    private static TaskRecord Ended(long id) => With(TaskOf(id), NextTaskState.Completed(JsonValue.Number(id)));

    private static ConversationWrite Conversation(long id) => new(new ConversationRecord(new ConversationId(id)));

    /// <summary>Memory storage counting its task reads (upstream <c>CountingStorage</c>); <see cref="HoldTasks"/>, when set, holds each task
    /// read until it completes.</summary>
    private sealed class CountingStorage() : ControlledStorage(new MemoryStorage())
    {
        private int taskReads;

        public int TaskReads
        {
            get => Volatile.Read(ref taskReads);
            set => Volatile.Write(ref taskReads, value);
        }

        public Task? HoldTasks { get; set; }

        public override async Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context)
        {
            Interlocked.Increment(ref taskReads);
            if (HoldTasks is { } hold) await hold;
            return await base.GetTaskAsync(id, context);
        }
    }

    /// <summary>Stores <paramref name="writes"/> as one batch, then publishes it, as the Session does.</summary>
    private static async Task Commit(CachedStorage storage, params StorageWrite[] writes)
    {
        await storage.CommitWritesAsync(writes, Context);
        storage.Published(writes);
    }

    private static async Task<DurableTaskStatus?> Status(CachedStorage storage, long id) => (await storage.GetTaskAsync(new TaskId(id), Context))?.State.Status;

    /// <summary>Every settable instance property of <paramref name="type"/> is init-only: an instance cannot change once built.</summary>
    internal static void InitOnly(Type type, string what)
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var declaring = type; declaring is not null && declaring != typeof(object); declaring = declaring.BaseType)
        {
            foreach (var property in declaring.GetProperties(Declared))
            {
                if (property.SetMethod is not { } setter) continue;
                var init = setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
                Check(init, $"{what}: {declaring.Name}.{property.Name} has a setter that is not init-only");
            }
            foreach (var field in declaring.GetFields(Declared))
                Check(field.IsInitOnly, $"{what}: {declaring.Name}.{field.Name} is a mutable field");
        }
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "serves-a-written-record-without-a-read-and-follows-later-writes", async () =>
        {
            var backend = new CountingStorage();
            var storage = new CachedStorage(backend);
            await Commit(storage, Conversation(1));
            await Commit(storage, new TaskWrite(TaskOf(2)));
            Equal(DurableTaskStatus.Pending, await Status(storage, 2), "pending");
            await Commit(storage, new TaskWrite(TaskOf(2, DurableTaskStatus.Running)));
            Equal(DurableTaskStatus.Running, await Status(storage, 2), "running");
            Equal(0, backend.TaskReads, "task reads");
        });

        yield return Case(prefix + "keeps-a-read-only-when-no-commit-overlapped-it", async () =>
        {
            var backend = new CountingStorage();
            await backend.CommitWritesAsync([Conversation(1), new TaskWrite(TaskOf(2))], Context);
            var storage = new CachedStorage(backend);
            var overlapped = storage.GetTaskAsync(new TaskId(2), Context);
            var written = Commit(storage, new TaskWrite(TaskOf(2, DurableTaskStatus.Running)));
            await Task.WhenAll(overlapped, written);
            Equal(DurableTaskStatus.Running, await Status(storage, 2), "status");
            await storage.GetTaskAsync(new TaskId(2), Context);
            Equal(1, backend.TaskReads, "task reads");
        });

        yield return Case(prefix + "keeps-a-bounded-number-of-ended-tasks-and-every-live-one-until-it-ends", async () =>
        {
            var backend = new CountingStorage();
            var storage = new CachedStorage(backend);
            await Commit(storage, Conversation(1));
            for (var id = 2; id < 600; id++) await Commit(storage, new TaskWrite(TaskOf(id)));
            // More live tasks than the ended table holds: each one's next change finds it without a read.
            Equal((0, 598), (storage.Sizes.Tasks, storage.Sizes.LiveTasks), "sizes");
            for (var id = 2; id < 600; id++) await storage.GetTaskAsync(new TaskId(id), Context);
            Equal(0, backend.TaskReads, "task reads");
            for (var id = 2; id < 600; id++)
                await Commit(storage, new TaskWrite(With(TaskOf(id), NextTaskState.Completed(JsonValue.Null))));
            Equal((256, 0), (storage.Sizes.Tasks, storage.Sizes.LiveTasks), "ended sizes");
        });

        yield return Case(prefix + "applies-a-batch-stored-with-commit-when-it-is-published-not-at-its-receipt", async () =>
        {
            var backend = new CountingStorage();
            var storage = new CachedStorage(backend);
            await Commit(storage, Conversation(1), new TaskWrite(TaskOf(2)));
            StorageWrite[] batch = [new TaskWrite(TaskOf(2, DurableTaskStatus.Running))];
            var receipts = new List<int>();
            var result = await storage.CommitAsync([batch], (index, _) => receipts.Add(index), Context);
            Equal(new CommitResult(1), result, "result");
            Equal("0", string.Join(",", receipts), "receipts");
            // Stored, not yet published: the cache still shows what was published.
            Equal(DurableTaskStatus.Pending, await Status(storage, 2), "before publication");
            storage.Published(batch);
            Equal(DurableTaskStatus.Running, await Status(storage, 2), "after publication");
            Equal(0, backend.TaskReads, "task reads");
        });

        yield return Case(prefix + "drops-a-read-a-publication-of-its-record-overlapped-and-keeps-versions-only-for-reads-in-flight", async () =>
        {
            var backend = new CountingStorage();
            await backend.CommitWritesAsync([Conversation(1), new TaskWrite(TaskOf(2)), new TaskWrite(TaskOf(3))], Context);
            var storage = new CachedStorage(backend);
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.HoldTasks = hold.Task;
            var overlapped = storage.GetTaskAsync(new TaskId(2), Context);
            var untouched = storage.GetTaskAsync(new TaskId(3), Context);
            Equal(2, storage.ReadsInFlight, "reads in flight");
            // A publication of another record does not drop the read of task 3.
            storage.Published([new TaskWrite(TaskOf(2, DurableTaskStatus.Running))]);
            backend.HoldTasks = null;
            hold.SetResult();
            await Task.WhenAll(overlapped, untouched);
            Equal(0, storage.ReadsInFlight, "reads in flight after");
            Equal(DurableTaskStatus.Running, await Status(storage, 2), "task 2");
            Equal(DurableTaskStatus.Pending, await Status(storage, 3), "task 3");
            Equal(2, backend.TaskReads, "task reads");
        });

        yield return Case(prefix + "keeps-the-ended-tasks-a-task-waits-on-until-it-moved-on-from-its-wait", async () =>
        {
            var backend = new CountingStorage();
            var storage = new CachedStorage(backend);
            await Commit(storage, Conversation(1));
            TaskRecord Waiting(TaskState state) => With(TaskOf(2), state);
            TaskId[] on = [new TaskId(3), new TaskId(4)];
            await Commit(storage, new TaskWrite(Waiting(NextTaskState.Waiting(O("""{"phase":"read"}"""), on, JoinPolicy.AllSettled))));
            await Commit(storage, new TaskWrite(Ended(3)), new TaskWrite(Ended(4)));
            // A burst of other ended tasks fills the bounded table.
            for (var id = 10; id < 300; id++) await Commit(storage, new TaskWrite(Ended(id)));
            Equal((2, 1), (storage.Sizes.Awaited, storage.Sizes.Waits), "waiting sizes");
            // Resumed: still held for the phase that reads the outcomes.
            await Commit(storage, new TaskWrite(Waiting(NextTaskState.Running(O("""{"phase":"read"}""")))));
            foreach (var id in on) Equal(DurableTaskStatus.Terminal, (await storage.GetTaskAsync(id, Context))?.State.Status, "awaited " + id.Value);
            Equal(0, backend.TaskReads, "task reads");
            Equal((2, 1), (storage.Sizes.Awaited, storage.Sizes.Waits), "resumed sizes");
            // Its next change ends the hold: the records go back to the bounded table, and no wait is left.
            await Commit(storage, new TaskWrite(Waiting(NextTaskState.Running(O("""{"phase":"next"}""")))));
            Equal((0, 0, 256), (storage.Sizes.Awaited, storage.Sizes.Waits, storage.Sizes.Tasks), "moved-on sizes");
            // A wait that ends with its task leaves nothing either.
            await Commit(storage, new TaskWrite(Waiting(NextTaskState.Waiting(O("""{"phase":"read"}"""), on, JoinPolicy.AllSettled))));
            Equal((2, 1), (storage.Sizes.Awaited, storage.Sizes.Waits), "second wait sizes");
            await Commit(storage, new TaskWrite(Ended(2)));
            Equal((0, 0), (storage.Sizes.Awaited, storage.Sizes.Waits), "ended sizes");
        });

        yield return Case(prefix + "keeps-the-written-record-itself-frozen-no-copy", async () =>
        {
            var storage = new CachedStorage(new MemoryStorage());
            var conversation = new ConversationRecord(new ConversationId(1));
            var record = TaskOf(2);
            storage.Published([new ConversationWrite(conversation), new TaskWrite(record)]);
            Same(record, await storage.GetTaskAsync(new TaskId(2), Context), "task");
            Same(conversation, await storage.GetConversationAsync(new ConversationId(1), Context), "conversation");
            // Frozen by construction: neither the record nor its state can change.
            InitOnly(typeof(TaskRecord), "task record");
            InitOnly(record.State.GetType(), "task state");
            InitOnly(typeof(ConversationRecord), "conversation record");
        });
    }

    private static void Same(object? expected, object? actual, string what) => Check(ReferenceEquals(expected, actual), what + ": not the same reference");
}
