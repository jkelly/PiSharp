// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/session-tables.test.ts. Verbatim apart from the adaptations in
// SessionSupport.cs and these: task definitions carry only what the Session needs until the task runtime (P7); "takes ownership of table
// JSON and rejects non-strict values" checks that a caller cannot change committed JSON (immutable values) and that NaN cannot be
// constructed as JSON, which rejects inside the callback before any write.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static Program;

internal static partial class SessionTests
{
    private static readonly DurableTask TablesWorkTask = Tasks.DefineTask(new TaskDefinition
    {
        Name = "test.work", Version = 1, Initial = _ => O("{\"phase\":\"start\"}"),
    });

    private static TaskRecord Terminal(TaskRecord task) => new()
    {
        Id = task.Id, ConversationId = task.ConversationId, Kind = task.Kind, Version = task.Version, Input = task.Input,
        Background = task.Background, AbortRequested = task.AbortRequested,
        State = new TerminalTaskState(new CompletedTaskOutcome(O("{\"ok\":true}"))),
    };

    private static IEnumerable<(string, Func<Task>)> Tables(string prefix, Func<IStorage> storageFactory)
    {
        var progressDoc = Documents.DefineTaskDoc("test.progress", 1, () => O("{\"lines\":[]}"));
        var stepDoc = Documents.DefineTaskDocFamily("test.step", 1, _ => O("{\"lines\":[]}"));
        var notesDoc = Documents.DefineRewindableConversationDoc("test.notes", 1, DocumentFork.AsOf, () => O("{\"text\":\"\"}"));

        async Task<TaskId> CreateTask(Session session, ConversationId conversationId, bool withDocument = false) =>
            await session.CommitAsync(async tx =>
            {
                var taskId = await tx.CreateTaskAsync(TablesWorkTask, O("{\"path\":\"a\"}"), new TaskOptions(TaskOwnership.Conversation) { ConversationId = conversationId });
                if (withDocument) (await tx.DocAsync(progressDoc, taskId)).GetArray("lines").Add("started");
                return taskId;
            }, Context);

        yield return Case(prefix + "allows-table-reads-only-before-the-first-table-write", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            await session.CommitAsync(async tx =>
            {
                Equal(new ConversationRecord(conversationId), await tx.GetConversationAsync(conversationId), "conversation");
                var conversations = await tx.ScanConversationsAsync(new ConversationQuery(), 1);
                Seq([conversationId], conversations.Items.Select(item => item.Id), "scanned conversations");
                Check(conversations.Next is null, "one conversation, no continuation");
                Equal(0, (await tx.ScanTasksAsync(new TaskQuery { ConversationId = conversationId }, 10)).Items.Length, "tasks");
                Equal(0, (await tx.ScanEntriesAsync(new EntryQuery(conversationId), 10)).Items.Length, "entries");
                await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "note" });
                await ThrowsAsync<ReadAfterWriteException>(() => tx.GetConversationAsync(conversationId), "conversation after write");
                await RejectsAsync(() => tx.GetTaskAsync(Ids.FromNumber<TaskId>(1)), "Tx.task() cannot read tables after the first table write", "task");
                await ThrowsAsync<ReadAfterWriteException>(() => tx.GetEntryAsync(Ids.FromNumber<EntryId>(1)), "entry after write");
                await ThrowsAsync<ReadAfterWriteException>(() => tx.ScanConversationsAsync(new ConversationQuery(), 10), "scan after write");
                await ThrowsAsync<ReadAfterWriteException>(() => tx.ScanEntriesAsync(new EntryQuery(conversationId), 10), "entries after write");
                // Document access remains available after table writes.
                (await tx.DocAsync(notesDoc, conversationId))["text"] = "after write";
            }, Context);
            SameJson("{\"text\":\"after write\"}", await session.SnapshotAsync(notesDoc, conversationId, Context), "notes");
        });

        yield return Case(prefix + "passes-caller-selected-limits-and-cursors-through-table-scans", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            ConversationId[] ids = [await CreateConversation(session), await CreateConversation(session), await CreateConversation(session)];
            await session.CommitAsync(async tx =>
            {
                var first = await tx.ScanConversationsAsync(new ConversationQuery(), 2);
                Seq(ids[..2], first.Items.Select(item => item.Id), "first page");
                Check(first.Next is not null, "first continuation");
                var second = await tx.ScanConversationsAsync(new ConversationQuery(), 2, first.Next);
                Seq(ids[2..], second.Items.Select(item => item.Id), "second page");
                Check(second.Next is null, "no further continuation");
            }, Context);
        });

        yield return Case(prefix + "treats-synchronous-settask-as-the-first-table-write", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var taskId = await CreateTask(session, conversationId);
            await session.CommitWithAsync(async tx =>
            {
                var task = (await tx.GetTaskAsync(taskId))!;
                tx.SetTask(task);
                await ThrowsAsync<ReadAfterWriteException>(() => tx.GetTaskAsync(taskId), "task after setTask");
            }, Context);
        });

        yield return Case(prefix + "creates-conversations-entries-and-tasks-with-minted-ids", async () =>
        {
            var (storage, session, publications) = Open(storageFactory);
            var created = await session.CommitAsync(async tx =>
            {
                var conversation = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
                var first = await tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = "note", Data = "one" });
                var headed = await tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = "summary", Head = EntryHead.Self });
                var task = await tx.CreateTaskAsync(TablesWorkTask, O("{\"path\":\"x\"}"),
                    new TaskOptions(TaskOwnership.Conversation) { ConversationId = conversation.Id, Background = true });
                return (conversation, first, headed, task);
            }, Context);
            long[] ids = [created.conversation.Id.Value, created.first.Id.Value, created.headed.Id.Value, created.task.Value];
            Equal(4, ids.Distinct().Count(), "distinct minted IDs");
            Equal<EntryId?>(created.headed.Id, created.headed.Head, "head self");
            Equal(new EntryRecord { Id = created.first.Id, ConversationId = created.conversation.Id, Kind = "note", Data = "one" }, created.first, "entry");
            Equal(created.headed, (await storage.GetEntryAsync(created.headed.Id, Context))!.Entry, "stored entry");
            Equal(new TaskRecord
            {
                Id = created.task, ConversationId = created.conversation.Id, Kind = "test.work", Version = 1, Input = O("{\"path\":\"x\"}"),
                Background = true, AbortRequested = false, State = new PendingTaskState(O("{\"phase\":\"start\"}")),
            }, await storage.GetTaskAsync(created.task, Context), "stored task");
            await Flush(session);
            var changes = Last(publications).Changes;
            Equal(4, changes.Count, "changes");
            var admitted = storage.LastAdmitted;
            foreach (var change in changes) Check(admitted.Any(write => ReferenceEquals(write, change)), "a published change is the admitted write");
            Seq(["conversation", "entry", "entry", "task"], changes.Select(change => change.Type).Order(StringComparer.Ordinal), "change types");
            Same(created.conversation, changes.OfType<ConversationWrite>().Single().Value, "published conversation");
            var entries = changes.OfType<EntryWrite>().Select(change => change.Value).ToList();
            Check(entries.Any(entry => ReferenceEquals(entry, created.first)) && entries.Any(entry => ReferenceEquals(entry, created.headed)), "published entries");
            await RejectsAsync(() => session.CommitAsync(tx => tx.CreateTaskAsync(TablesWorkTask, O("{\"path\":\"x\"}"),
                new TaskOptions(TaskOwnership.Conversation)), Context), "requires options.conversationId", "task without conversation");
            await RejectsAsync(() => session.CommitAsync(tx => tx.AppendEntryAsync(Ids.FromNumber<ConversationId>(12345), new EntryDraft { Kind = "note" }),
                Context), "Conversation 12345 does not exist", "missing conversation");
        });

        yield return Case(prefix + "creates-a-conversation-with-an-explicitly-staged-task-owner", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            var created = await session.CommitAsync(async tx =>
            {
                var supervisorId = await tx.CreateTaskAsync(TablesWorkTask, O("{\"path\":\"background\"}"),
                    new TaskOptions(TaskOwnership.Conversation) { ConversationId = parentId, Background = true });
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(supervisorId));
                return (supervisorId, child);
            }, Context);
            Equal(new ConversationOwner(parentId, created.supervisorId), created.child.Owner, "owner");
            Equal(created.child, await storage.GetConversationAsync(created.child.Id, Context), "stored child");

            await RejectsAsync(() => session.CommitWithAsync(async tx =>
            {
                var supervisor = (await tx.GetTaskAsync(created.supervisorId))!;
                tx.SetTask(supervisor with { ConversationId = created.child.Id });
            }, Context), $"Task {created.supervisorId} cannot change conversations", "moved task");
            Equal(parentId, (await storage.GetTaskAsync(created.supervisorId, Context))?.ConversationId, "task conversation");
        });

        yield return Case(prefix + "rejects-missing-terminal-and-abort-marked-conversation-owners-atomically", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            TaskId? movedStagedTaskId = null;
            await RejectsAsync(() => session.CommitWithAsync(async tx =>
            {
                var taskId = await tx.CreateTaskAsync(TablesWorkTask, O("{\"path\":\"move\"}"),
                    new TaskOptions(TaskOwnership.Conversation) { ConversationId = parentId });
                movedStagedTaskId = taskId;
                tx.SetTask(new TaskRecord
                {
                    Id = taskId, ConversationId = Ids.FromNumber<ConversationId>(998), Kind = TablesWorkTask.Definition.Name,
                    Version = TablesWorkTask.Definition.Version, Input = O("{\"path\":\"move\"}"), Background = false, AbortRequested = false,
                    State = new PendingTaskState(O("{\"phase\":\"start\"}")),
                });
            }, Context), "cannot change conversations", "moved staged task");
            Check(movedStagedTaskId is not null, "Expected a staged task ID");
            Check(await storage.GetTaskAsync(movedStagedTaskId!.Value, Context) is null, "nothing committed");

            var missingTaskId = Ids.FromNumber<TaskId>(999);
            await RejectsAsync(() => session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Task(missingTaskId)), Context),
                "Conversation owner task 999 does not exist", "missing owner");

            ConversationId? rejectedChildId = null;
            await RejectsAsync(() => session.CommitWithAsync(async tx =>
            {
                var supervisorId = await tx.CreateTaskAsync(TablesWorkTask, O("{\"path\":\"aborting\"}"),
                    new TaskOptions(TaskOwnership.Conversation) { ConversationId = parentId });
                rejectedChildId = (await tx.CreateConversationAsync(ConversationOwnership.Task(supervisorId))).Id;
                tx.SetTask(new TaskRecord
                {
                    Id = supervisorId, ConversationId = parentId, Kind = TablesWorkTask.Definition.Name, Version = TablesWorkTask.Definition.Version,
                    Input = O("{\"path\":\"aborting\"}"), Background = false, AbortRequested = true, State = new PendingTaskState(O("{\"phase\":\"start\"}")),
                });
            }, Context), "is abort-marked", "abort-marked owner");
            Check(rejectedChildId is not null, "Expected a rejected child ID");
            Check(await storage.GetConversationAsync(rejectedChildId!.Value, Context) is null, "rejected child not committed");

            await RejectsAsync(() => session.CommitWithAsync(async tx =>
            {
                var supervisorId = await tx.CreateTaskAsync(TablesWorkTask, O("{\"path\":\"terminal\"}"),
                    new TaskOptions(TaskOwnership.Conversation) { ConversationId = parentId });
                await tx.CreateConversationAsync(ConversationOwnership.Task(supervisorId));
                tx.SetTask(new TaskRecord
                {
                    Id = supervisorId, ConversationId = parentId, Kind = TablesWorkTask.Definition.Name, Version = TablesWorkTask.Definition.Version,
                    Input = O("{\"path\":\"terminal\"}"), Background = false, AbortRequested = false,
                    State = new TerminalTaskState(new CompletedTaskOutcome(O("{\"ok\":true}"))),
                });
            }, Context), "is terminal", "terminal staged owner");

            var terminalOwnerId = await CreateTask(session, parentId);
            await session.CommitWithAsync(async tx => tx.SetTask(Terminal((await tx.GetTaskAsync(terminalOwnerId))!)), Context);
            await RejectsAsync(() => session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Task(terminalOwnerId)), Context),
                "is terminal", "terminal committed owner");
        });

        yield return Case(prefix + "takes-ownership-of-table-json-and-rejects-non-strict-values", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var payload = O("{\"nested\":{\"value\":1}}");
            var entry = await session.CommitAsync(async tx =>
            {
                var created = await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "data", Data = payload });
                // Upstream mutates the caller's payload here; an immutable value can only be replaced, never changed.
                payload = payload.SetItem("nested", O("{\"value\":2}"));
                return created;
            }, Context);
            SameJson("{\"nested\":{\"value\":1}}", (await storage.GetEntryAsync(entry.Id, Context))!.Entry.Data, "stored data");
            var omitted = await session.CommitAsync(tx => tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "omitted", Data = null }), Context);
            Check(omitted.Data is null && !omitted.ToJson().ContainsKey("data"), "data omitted");
            Check(!(await storage.GetEntryAsync(omitted.Id, Context))!.Entry.ToJson().ContainsKey("data"), "stored data omitted");

            var commits = storage.CommitCount;
            await RejectsAsync(() => session.CommitAsync(tx => tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "invalid", Data = double.NaN }), Context),
                "strict JSON", "NaN data");
            Equal(commits, storage.CommitCount, "no commit");
        });

        yield return Case(prefix + "replaces-task-records-completely", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var taskId = await CreateTask(session, conversationId);
            await session.CommitWithAsync(async tx =>
            {
                var task = (await tx.GetTaskAsync(taskId))!;
                tx.SetTask(new TaskRecord
                {
                    Id = task.Id, ConversationId = task.ConversationId, Kind = task.Kind, Version = task.Version, Input = task.Input,
                    Background = task.Background, AbortRequested = task.AbortRequested,
                    State = new RunningTaskState(O("{\"phase\":\"next\",\"step\":2}")), Memos = O("{\"choice\":\"b\"}"),
                });
            }, Context);
            var stored = (await storage.GetTaskAsync(taskId, Context))!;
            SameJson("{\"status\":\"running\",\"checkpoint\":{\"phase\":\"next\",\"step\":2}}", stored.State.ToJson(), "state");
            SameJson("{\"choice\":\"b\"}", stored.Memos, "memos");
        });

        yield return Case(prefix + "creates-a-task-and-then-its-document-in-one-transaction-without-readafterwrite", async () =>
        {
            var (_, session, publications) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var taskId = await session.CommitAsync(async tx =>
            {
                var createdTaskId = await tx.CreateTaskAsync(TablesWorkTask, O("{\"path\":\"a\"}"),
                    new TaskOptions(TaskOwnership.Conversation) { ConversationId = conversationId });
                // Validation uses the candidate task record, not a caller table read.
                (await tx.DocAsync(progressDoc, createdTaskId)).GetArray("lines").Add("created");
                (await tx.DocAsync(stepDoc, createdTaskId, "one", JsonValue.Null)).GetArray("lines").Add("step");
                return createdTaskId;
            }, Context);
            SameJson("{\"lines\":[\"created\"]}", await session.SnapshotAsync(progressDoc, taskId, Context), "progress");
            await Flush(session);
            var publication = Last(publications);
            var documents = DocumentChanges(publication);
            Check(publication.Changes.OfType<TaskWrite>().Any(change => change.Value.Id == taskId), "task published");
            Equal(2, documents.Count, "document changes");
            // Task documents derive their conversation from the task record.
            foreach (var document in documents) Equal<ConversationId?>(conversationId, document.ConversationId, "document conversation");

            await session.CommitAsync(async tx =>
            {
                await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
                (await tx.DocAsync(progressDoc, taskId)).GetArray("lines").Add("committed task");
            }, Context);
            await Flush(session);
            Equal<ConversationId?>(conversationId, DocumentChanges(Last(publications))[0].ConversationId, "committed task conversation");
        });

        yield return Case(prefix + "rejects-task-documents-after-a-terminal-candidate", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var taskId = await CreateTask(session, conversationId, true);
            await session.CommitWithAsync(async tx =>
            {
                var task = (await tx.GetTaskAsync(taskId))!;
                var progress = await tx.DocAsync(progressDoc, taskId);
                tx.SetTask(Terminal(task));
                await RejectsAsync(() => tx.DocAsync(progressDoc, taskId), $"Task {taskId} is terminal", "singleton");
                await RejectsAsync(() => tx.DocAsync(stepDoc, taskId, "late", JsonValue.Null), $"Task {taskId} is terminal", "family");
                Throws(() => tx.SetTask(task), "terminal candidate", "replacing a terminal candidate");
                progress.GetArray("lines").Add("final");
            }, Context);
            await RejectsAsync(() => session.CommitAsync(tx => tx.DocAsync(progressDoc, taskId), Context), $"Task {taskId} is terminal", "after terminal");
        });

        yield return Case(prefix + "retires-task-documents-at-terminal-settlement-including-documents-created-in-the-same-transaction", async () =>
        {
            var (storage, session, publications) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var taskId = await CreateTask(session, conversationId, true);
            await session.CommitAsync(async tx => (await tx.DocAsync(stepDoc, taskId, "committed", JsonValue.Null)).GetArray("lines").Add("x"), Context);
            await Flush(session);
            var published = Count(publications);
            await session.CommitWithAsync(async tx =>
            {
                var task = (await tx.GetTaskAsync(taskId))!;
                (await tx.DocAsync(stepDoc, taskId, "new", JsonValue.Null)).GetArray("lines").Add("created then retired");
                tx.SetTask(Terminal(task));
            }, Context);
            var writes = storage.LastCommit;
            Equal(5, writes.Length, "writes");
            Equal(1, writes.OfType<TaskWrite>().Count(), "task writes");
            var creations = writes.OfType<DocumentCreateWrite>().ToList();
            var retirements = writes.OfType<DocumentRetireWrite>().ToList();
            Equal(1, creations.Count, "creations");
            Equal(3, retirements.Count, "retirements");
            Check(retirements.Any(write => write.Id == creations[0].Record.Id), "the created document retires");
            await Flush(session);
            Equal(published + 1, Count(publications), "publications");
            var publication = Last(publications);
            var documents = DocumentChanges(publication);
            Equal(1, publication.Changes.OfType<TaskWrite>().Count(), "published task");
            Check(documents.Count == 3 && documents.All(document => document.Value is null), "three retirements");
            foreach (var document in documents) Check(document.Ops.IsEmpty, "retirement ops are empty");
            foreach (var document in documents) Equal<ConversationId?>(conversationId, document.ConversationId, "retirement conversation");
            Check(await session.SnapshotAsync(progressDoc, taskId, Context) is null, "progress retired");
            Check(await session.SnapshotAsync(stepDoc, taskId, "committed", Context) is null, "step retired");
            var alive = await storage.ScanDocumentsAsync(new DocumentQuery(DocumentScope.Task(taskId), DocumentPoint.Current), 10, null, Context);
            Equal(0, alive.Items.Length, "alive task documents");
            await RejectsAsync(() => session.CommitWithAsync(async tx => tx.SetTask(Terminal((await tx.GetTaskAsync(taskId))!)), Context),
                $"Task {taskId} is already terminal", "terminal again");
        });

        yield return Case(prefix + "validates-document-owners", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            await RejectsAsync(() => session.CommitAsync(tx => tx.DocAsync(progressDoc, Ids.FromNumber<TaskId>(4242)), Context), "Task 4242 does not exist", "task owner");
            await RejectsAsync(() => session.CommitAsync(tx => tx.DocAsync(notesDoc, Ids.FromNumber<ConversationId>(4242)), Context),
                "Conversation 4242 does not exist", "conversation owner");
        });
    }
}
