// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/testing/storage-conformance.ts.
// The semantic conformance suite every backend runs (spec §10), directly and after reopen. Cases keep upstream's names, inputs and
// expected values. Two cases are adapted because records and JSON values are immutable here: "detaches retained writes and every returned
// record" and "detaches prototype-like JSON keys ..." change the caller's own builders (and builders made from read results) where
// upstream mutates the committed and returned objects, and assert storage is unaffected; JavaScript prototype checks have no analogue
// (JSON objects are dictionaries) and are dropped. Upstream's mutations of caller-owned arrays after a commit are likewise impossible and
// omitted.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable.Testing;

/// <summary>The runner-independent storage conformance suite (upstream <c>createStorageConformance</c>).</summary>
public static class StorageConformance
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Creates the cases. <paramref name="withStorage"/> must call and await its callback exactly once per case.</summary>
    public static IReadOnlyList<StorageConformanceCase> Create(StorageConformanceProvider withStorage)
    {
        ArgumentNullException.ThrowIfNull(withStorage);
        StorageConformanceCase Case(string name, Func<IStorage, Task> test) => new(name, () => withStorage(test));
        return
        [
            Case("reserves ID 1 for the immutable root conversation", ReservesRootConversation),
            Case("commits mixed table writes atomically and rolls all of them back on failure", CommitsMixedTableWritesAtomically),
            Case("detaches retained writes and every returned record", DetachesRetainedWritesAndReturnedRecords),
            Case("detaches prototype-like JSON keys without changing object prototypes", DetachesPrototypeLikeKeys),
            Case("indexes entries committed out of ID order", IndexesEntriesOutOfOrder),
            Case("continues an entry cursor below its last item after a newer commit", ContinuesEntryCursorAfterNewerCommit),
            Case("paginates conversations by opaque cursor in ascending ID order", PaginatesConversations),
            Case("filters and pages conversations by durable owner edges", FiltersConversationsByOwner),
            Case("scans deep fork history newest-first through every ancestor cap", ScansDeepForkHistory),
            Case("scans tables in either ID order and continues a cursor in its order", ScansTablesInEitherOrder),
            Case("replaces complete task records and pages filtered task scans", ReplacesTaskRecords),
            Case("stores owners and scans waiting and completing tasks by status", StoresOwnersAndWaitingTasks),
            Case("indexes request IDs per conversation and replaces complete submission records", IndexesRequestIds),
            Case("stores passive write submissions without input-only lifecycle states", StoresPassiveWrites),
            Case("reconstructs rewindable documents and preserves half-open incarnations", ReconstructsRewindableDocuments),
            Case("streams long document tails across root replacement deltas", StreamsLongDocumentTails),
            Case("copies stored document bases independently and rejects ambiguous sources", CopiesDocumentBases),
            Case("uses bases for version transitions and rejects historical reads of current-only documents", UsesBasesForVersionTransitions),
            Case("indexes logical addresses and exact-scope scans independently", IndexesLogicalAddresses),
            Case("keeps document lifecycle failures atomic and gives create-plus-retire an empty lifetime", KeepsDocumentLifecycleAtomic),
            Case("rolls back record tables and secondary indexes when a document command fails", RollsBackOnDocumentFailure),
            Case("keeps indexed string identities lossless", KeepsStringIdentitiesLossless),
            Case("keeps one global record ID namespace and rejects exhausted ID minting", KeepsOneIdNamespace),
            Case("rejects every operation after close", RejectsAfterClose),
        ];
    }

    private static async Task ReservesRootConversation(IStorage storage)
    {
        Equal(2L, (await storage.MintIdAsync<ConversationId>()).Value, "first minted ID");
        Equal(ConversationId.Root, await CreateRoot(storage), "root ID");
        Equal<ConversationRecord?>(new ConversationRecord(ConversationId.Root), await storage.GetConversationAsync(ConversationId.Root, Context), "root record");
        await Rejects(() => Commit(storage, new ConversationWrite(new(ConversationId.Root))),
            $"ID {ConversationId.Root} already belongs to conversation", "recreating the root");
    }

    private static async Task CommitsMixedTableWritesAtomically(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var entryId = await storage.MintIdAsync<EntryId>();
        var taskId = await storage.MintIdAsync<TaskId>();
        var submissionId = await storage.MintIdAsync<SubmissionId>();
        var task = PendingTask(taskId, rootId);
        var input = new SubmissionRecord
        {
            Id = submissionId, ConversationId = rootId, RequestId = "request-1", Type = SubmissionType.Input, Status = SubmissionStatus.Placed, Entry = entryId,
        };
        var userEntry = Entry(entryId, rootId, "user") with { Data = O("{\"text\":\"hello\"}") };
        var initialSeq = await Commit(storage, new EntryWrite(userEntry), new TaskWrite(task), new SubmissionWrite(input));

        Equal<CommittedEntry?>(new CommittedEntry(userEntry, initialSeq), await storage.GetEntryAsync(entryId, Context), "entry");
        Equal<TaskRecord?>(task, await storage.GetTaskAsync(taskId, Context), "task");
        Equal<SubmissionRecord?>(input, await storage.GetSubmissionAsync(submissionId, Context), "submission");

        var transientEntryId = await storage.MintIdAsync<EntryId>();
        var runningTask = task with { State = new RunningTaskState(O("{\"phase\":\"effect\"}")) };
        var doneInput = input with { Status = SubmissionStatus.Done, Answer = transientEntryId };
        await Rejects(() => Commit(storage, new TaskWrite(runningTask), new SubmissionWrite(doneInput),
            new EntryWrite(Entry(transientEntryId, rootId, "assistant")), new ConversationWrite(new(rootId))),
            $"ID {rootId} already belongs to conversation", "mixed batch");

        Equal<TaskRecord?>(task, await storage.GetTaskAsync(taskId, Context), "task after rollback");
        Equal<SubmissionRecord?>(input, await storage.GetSubmissionAsync(submissionId, Context), "submission after rollback");
        Null(await storage.GetEntryAsync(transientEntryId, Context), "transient entry");
        var afterRollbackSeq = await Commit(storage, new EntryWrite(Entry(await storage.MintIdAsync<EntryId>(), rootId, "after-rollback")));
        Greater(afterRollbackSeq.Value, initialSeq.Value, "sequence after rollback");
    }

    // Adapted: the caller's builders and builders made from read results change instead of the committed and returned objects.
    private static async Task DetachesRetainedWritesAndReturnedRecords(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var entryId = await storage.MintIdAsync<EntryId>();
        var taskId = await storage.MintIdAsync<TaskId>();
        var submissionId = await storage.MintIdAsync<SubmissionId>();
        var entryData = O("{\"nested\":[1,2]}").ToBuilder();
        var checkpoint = O("{\"phase\":\"ready\",\"nested\":{\"count\":1}}").ToBuilder();
        var detail = O("{\"codes\":[\"initial\"]}").ToBuilder();
        var storedEntry = Entry(entryId, rootId, "note") with { Data = entryData.ToImmutable() };
        var storedTask = PendingTask(taskId, rootId) with { State = new PendingTaskState(checkpoint.ToImmutable()) };
        var storedInput = new SubmissionRecord
        {
            Id = submissionId, ConversationId = rootId, Type = SubmissionType.Input, Status = SubmissionStatus.Unanswered, Reason = "failed",
            Detail = detail.ToImmutable(),
        };
        await Commit(storage, new EntryWrite(storedEntry), new TaskWrite(storedTask), new SubmissionWrite(storedInput));

        entryData["nested"] = J("[1,2,3]");
        checkpoint["nested"] = J("{\"count\":2}");
        detail["codes"] = J("[\"initial\",\"mutated\"]");
        await ExpectUnchanged();

        var readEntry = ((JsonObjectValue)(await storage.GetEntryAsync(entryId, Context))!.Entry.Data!).ToBuilder();
        readEntry["nested"] = J("[1,2,9]");
        var readTask = (await storage.GetTaskAsync(taskId, Context))!;
        if (readTask.State is LiveTaskState { Checkpoint: JsonObjectValue readCheckpoint }) readCheckpoint.ToBuilder()["nested"] = J("{\"count\":9}");
        var readInput = ((JsonObjectValue)(await storage.GetSubmissionAsync(submissionId, Context))!.Detail!).ToBuilder();
        readInput["codes"] = J("[\"initial\",\"read mutation\"]");
        await ExpectUnchanged();

        async Task ExpectUnchanged()
        {
            Same("{\"nested\":[1,2]}", (await storage.GetEntryAsync(entryId, Context))?.Entry.Data, "entry data");
            Same("{\"status\":\"pending\",\"checkpoint\":{\"phase\":\"ready\",\"nested\":{\"count\":1}}}",
                (await storage.GetTaskAsync(taskId, Context))?.State.ToJson(), "task state");
            Same("{\"codes\":[\"initial\"]}", (await storage.GetSubmissionAsync(submissionId, Context))?.Detail, "submission detail");
        }
    }

    // Adapted: prototype checks are JavaScript-only; the keys must stay ordinary, lossless members.
    private static async Task DetachesPrototypeLikeKeys(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var entryId = await storage.MintIdAsync<EntryId>();
        var data = O("{\"__proto__\":{\"polluted\":false},\"constructor\":{\"label\":\"stored\"},\"toString\":\"value\"}");
        await Commit(storage, new EntryWrite(Entry(entryId, rootId, "note") with { Data = data }));

        var caller = data.ToBuilder();
        caller["__proto__"] = J("{\"polluted\":true}");
        caller["constructor"] = J("{\"label\":\"mutated\"}");
        var firstRead = (JsonObjectValue)(await storage.GetEntryAsync(entryId, Context))!.Entry.Data!;
        Ok(firstRead.ContainsKey("__proto__"), "__proto__ is an own key");
        Same("{\"polluted\":false}", firstRead["__proto__"], "__proto__");
        Same("{\"label\":\"stored\"}", firstRead["constructor"], "constructor");
        Same("\"value\"", firstRead["toString"], "toString");

        firstRead.ToBuilder()["__proto__"] = J("{\"polluted\":true}");
        var secondRead = (JsonObjectValue)(await storage.GetEntryAsync(entryId, Context))!.Entry.Data!;
        Same("{\"polluted\":false}", secondRead["__proto__"], "__proto__ on reread");
        Same("{\"label\":\"stored\"}", secondRead["constructor"], "constructor on reread");
        Same("\"value\"", secondRead["toString"], "toString on reread");
    }

    private static async Task IndexesEntriesOutOfOrder(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        await Commit(storage, new EntryWrite(Entry(new(30), rootId)), new EntryWrite(Entry(new(10), rootId)),
            new EntryWrite(Entry(new(20), rootId, "marker") with { Head = new EntryId(10) }));

        IdsEqual([30, 20, 10], EntryIds(await storage.ScanEntriesAsync(new(rootId), 10, null, Context)), "entries newest first");
        Equal(20L, (await storage.FindLatestHeadMarkerAsync(rootId, null, Context))?.Id.Value, "latest head marker");
    }

    private static async Task ContinuesEntryCursorAfterNewerCommit(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var oldestId = await storage.MintIdAsync<EntryId>();
        var middleId = await storage.MintIdAsync<EntryId>();
        var newestId = await storage.MintIdAsync<EntryId>();
        await Commit(storage, new EntryWrite(Entry(oldestId, rootId)), new EntryWrite(Entry(middleId, rootId)), new EntryWrite(Entry(newestId, rootId)));

        var first = await storage.ScanEntriesAsync(new(rootId), 2, null, Context);
        IdsEqual([newestId.Value, middleId.Value], EntryIds(first), "first page");
        var appendedId = await storage.MintIdAsync<EntryId>();
        await Commit(storage, new EntryWrite(Entry(appendedId, rootId)));
        var second = await storage.ScanEntriesAsync(new(rootId), 2, first.Next, Context);
        IdsEqual([oldestId.Value], EntryIds(second), "second page");
        Null(second.Next, "second page cursor");
    }

    private static async Task PaginatesConversations(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var secondId = await storage.MintIdAsync<ConversationId>();
        var thirdId = await storage.MintIdAsync<ConversationId>();
        await Commit(storage, new ConversationWrite(new(thirdId)), new ConversationWrite(new(secondId)));

        var first = await storage.ScanConversationsAsync(new(), 2, null, Context);
        IdsEqual([rootId.Value, secondId.Value], ConversationIds(first), "first page");
        NotNull(first.Next, "first page cursor");
        var second = await storage.ScanConversationsAsync(new(), 2, RoundTrip(first.Next), Context);
        IdsEqual([thirdId.Value], ConversationIds(second), "second page");
        Null(second.Next, "second page cursor");
    }

    private static async Task FiltersConversationsByOwner(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var otherOwnerId = await storage.MintIdAsync<ConversationId>();
        var firstTaskId = await storage.MintIdAsync<TaskId>();
        var secondTaskId = await storage.MintIdAsync<TaskId>();
        var firstId = await storage.MintIdAsync<ConversationId>();
        var secondId = await storage.MintIdAsync<ConversationId>();
        var thirdId = await storage.MintIdAsync<ConversationId>();
        await Commit(storage,
            new ConversationWrite(new(otherOwnerId)),
            new ConversationWrite(new(firstId, Owner: new(rootId, firstTaskId))),
            new ConversationWrite(new(secondId, Owner: new(rootId, secondTaskId))),
            new ConversationWrite(new(thirdId, Owner: new(otherOwnerId, firstTaskId))));

        var first = await storage.ScanConversationsAsync(new() { OwnerConversationId = rootId }, 1, null, Context);
        IdsEqual([firstId.Value], ConversationIds(first), "first owned page");
        NotNull(first.Next, "first owned cursor");
        var second = await storage.ScanConversationsAsync(new() { OwnerConversationId = rootId }, 1, first.Next, Context);
        IdsEqual([secondId.Value], ConversationIds(second), "second owned page");
        Null(second.Next, "second owned cursor");
        IdsEqual([firstId.Value, thirdId.Value],
            ConversationIds(await storage.ScanConversationsAsync(new() { OwnerTaskId = firstTaskId }, 10, null, Context)), "owned by task");
        IdsEqual([firstId.Value], ConversationIds(await storage.ScanConversationsAsync(
            new() { OwnerConversationId = rootId, OwnerTaskId = firstTaskId }, 10, null, Context)), "owned by conversation and task");
    }

    private static async Task ScansDeepForkHistory(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var rootFirst = await storage.MintIdAsync<EntryId>();
        var rootForkPoint = await storage.MintIdAsync<EntryId>();
        var rootExcludedSameCommit = await storage.MintIdAsync<EntryId>();
        var rootEntriesSeq = await Commit(storage, new EntryWrite(Entry(rootFirst, rootId)),
            new EntryWrite(Entry(rootForkPoint, rootId, "marker") with { Head = rootFirst }), new EntryWrite(Entry(rootExcludedSameCommit, rootId)));
        var childId = await storage.MintIdAsync<ConversationId>();
        await Commit(storage, new ConversationWrite(new(childId, new ConversationParent(rootId, rootForkPoint))));
        var childForkPoint = await storage.MintIdAsync<EntryId>();
        var childExcluded = await storage.MintIdAsync<EntryId>();
        await Commit(storage, new EntryWrite(Entry(childForkPoint, childId, "note")), new EntryWrite(Entry(childExcluded, childId)));
        var rootExcludedLater = await storage.MintIdAsync<EntryId>();
        await Commit(storage, new EntryWrite(Entry(rootExcludedLater, rootId)));
        var grandchildId = await storage.MintIdAsync<ConversationId>();
        await Commit(storage, new ConversationWrite(new(grandchildId, new ConversationParent(childId, childForkPoint))));
        var grandchildHead = await storage.MintIdAsync<EntryId>();
        var grandchildTail = await storage.MintIdAsync<EntryId>();
        var grandchildEntriesSeq = await Commit(storage,
            new EntryWrite(Entry(grandchildHead, grandchildId, "marker") with { Head = grandchildHead }), new EntryWrite(Entry(grandchildTail, grandchildId)));
        var childExcludedLater = await storage.MintIdAsync<EntryId>();
        await Commit(storage, new EntryWrite(Entry(childExcludedLater, childId)));

        var first = await storage.ScanEntriesAsync(new(grandchildId), 2, null, Context);
        IdsEqual([grandchildTail.Value, grandchildHead.Value], EntryIds(first), "first descending page");
        var second = await storage.ScanEntriesAsync(new(grandchildId), 2, first.Next, Context);
        IdsEqual([childForkPoint.Value, rootForkPoint.Value], EntryIds(second), "second descending page");
        var third = await storage.ScanEntriesAsync(new(grandchildId), 2, second.Next, Context);
        IdsEqual([rootFirst.Value], EntryIds(third), "third descending page");
        Null(third.Next, "third descending cursor");

        // Oldest first: the root's segment, then each fork's, each capped at the next fork point.
        var ascending = new EntryQuery(grandchildId) { Order = ScanOrder.Ascending };
        var firstUp = await storage.ScanEntriesAsync(ascending, 2, null, Context);
        IdsEqual([rootFirst.Value, rootForkPoint.Value], EntryIds(firstUp), "first ascending page");
        var secondUp = await storage.ScanEntriesAsync(ascending, 2, firstUp.Next, Context);
        IdsEqual([childForkPoint.Value, grandchildHead.Value], EntryIds(secondUp), "second ascending page");
        var thirdUp = await storage.ScanEntriesAsync(new(grandchildId), 2, secondUp.Next, Context);
        IdsEqual([grandchildTail.Value], EntryIds(thirdUp), "third ascending page");
        Null(thirdUp.Next, "third ascending cursor");
        IdsEqual([rootForkPoint.Value, childForkPoint.Value], EntryIds(await storage.ScanEntriesAsync(
            ascending with { MinEntryId = rootForkPoint, MaxEntryId = childForkPoint }, 10, null, Context)), "bounded ascending scan");
        await Rejects(() => storage.ScanEntriesAsync(new(grandchildId) { Order = ScanOrder.Descending }, 2, firstUp.Next, Context), "cursor",
            "descending scan with an ascending cursor");

        var currentMarker = await storage.FindLatestHeadMarkerAsync(grandchildId, null, Context);
        Equal(grandchildHead, currentMarker?.Id, "current marker");
        Equal(grandchildHead, currentMarker?.Head, "current marker head");
        var historicalMarker = await storage.FindLatestHeadMarkerAsync(grandchildId, childForkPoint, Context);
        Equal(rootForkPoint, historicalMarker?.Id, "historical marker");
        Equal(rootFirst, historicalMarker?.Head, "historical marker head");
        Null(await storage.FindLatestHeadMarkerAsync(grandchildId, rootFirst, Context), "marker before every head");

        var activeFirst = await storage.ScanEntriesAsync(new(grandchildId) { MinEntryId = currentMarker?.Head }, 1, null, Context);
        IdsEqual([grandchildTail.Value], EntryIds(activeFirst), "first active page");
        NotNull(activeFirst.Next, "first active cursor");
        var activeSecond = await storage.ScanEntriesAsync(new(grandchildId) { MinEntryId = currentMarker?.Head }, 1, activeFirst.Next, Context);
        IdsEqual([grandchildHead.Value], EntryIds(activeSecond), "second active page");
        Null(activeSecond.Next, "second active cursor");

        IdsEqual([childForkPoint.Value, rootForkPoint.Value, rootFirst.Value], EntryIds(await storage.ScanEntriesAsync(
            new(grandchildId) { MinEntryId = historicalMarker?.Head, MaxEntryId = childForkPoint }, 10, null, Context)), "historical context range");

        Equal<CommittedEntry?>(new(Entry(rootFirst, rootId), rootEntriesSeq), await storage.GetEntryAsync(rootFirst, Context), "root entry");
        Equal(rootEntriesSeq, (await storage.GetEntryAsync(rootForkPoint, Context))?.CommitSeq, "fork point commit");
        Equal(grandchildEntriesSeq, (await storage.GetEntryAsync(grandchildHead, Context))?.CommitSeq, "grandchild head commit");
        Equal(grandchildEntriesSeq, (await storage.GetEntryAsync(grandchildTail, Context))?.CommitSeq, "grandchild tail commit");
        Null(await storage.GetEntryAsync(new EntryId(999_999), Context), "unknown entry");

        Equal<CommittedEntry?>(new(Entry(rootFirst, rootId), rootEntriesSeq), await storage.GetEntryAsync(grandchildId, rootFirst, Context), "inherited entry");
        Equal(childId, (await storage.GetEntryAsync(grandchildId, childForkPoint, Context))?.Entry.ConversationId, "inherited child entry");
        Equal(grandchildEntriesSeq, (await storage.GetEntryAsync(grandchildId, grandchildTail, Context))?.CommitSeq, "own entry commit");
        Null(await storage.GetEntryAsync(grandchildId, rootExcludedSameCommit, Context), "root entry after the fork point in its commit");
        Null(await storage.GetEntryAsync(grandchildId, rootExcludedLater, Context), "later root entry");
        Null(await storage.GetEntryAsync(grandchildId, childExcluded, Context), "child entry after the fork point");
        Null(await storage.GetEntryAsync(grandchildId, childExcludedLater, Context), "later child entry");
        Null(await storage.GetEntryAsync(rootId, grandchildHead, Context), "descendant entry from the root");
        Null(await storage.GetEntryAsync(grandchildId, new EntryId(999_999), Context), "unknown visible entry");
        await Rejects(() => storage.GetEntryAsync(new ConversationId(999_999), rootFirst, Context), "Unknown conversation", "entry of an unknown conversation");
        await Rejects(() => storage.ScanEntriesAsync(new(new ConversationId(999_999)), 10, null, Context), "Unknown conversation", "scan of an unknown conversation");
    }

    private static async Task ScansTablesInEitherOrder(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var conversationIds = new List<long> { rootId.Value };
        var taskIds = new List<long>();
        var submissionIds = new List<long>();
        for (var index = 0; index < 3; index++)
        {
            var conversationId = await storage.MintIdAsync<ConversationId>();
            var taskId = await storage.MintIdAsync<TaskId>();
            var submissionId = await storage.MintIdAsync<SubmissionId>();
            await Commit(storage, new ConversationWrite(new(conversationId)), new TaskWrite(PendingTask(taskId, rootId)),
                new SubmissionWrite(new() { Id = submissionId, ConversationId = rootId, Type = SubmissionType.Input, Status = SubmissionStatus.Queued }));
            conversationIds.Add(conversationId.Value);
            taskIds.Add(taskId.Value);
            submissionIds.Add(submissionId.Value);
        }
        var scans = new (string Name, Func<ScanOrder?, JsonObjectValue?, Task<(long[] Ids, JsonObjectValue? Next)>> Scan, List<long> Ids)[]
        {
            ("conversations", async (order, cursor) => PageIds(await storage.ScanConversationsAsync(new() { Order = order }, 2, cursor, Context), value => value.Id.Value), conversationIds),
            ("tasks", async (order, cursor) => PageIds(await storage.ScanTasksAsync(new() { Order = order }, 2, cursor, Context), value => value.Id.Value), taskIds),
            ("submissions", async (order, cursor) => PageIds(await storage.ScanSubmissionsAsync(new() { Order = order }, 2, cursor, Context), value => value.Id.Value), submissionIds),
        };
        foreach (var (name, scan, ids) in scans)
        {
            async Task<List<long>> All(ScanOrder? order)
            {
                var found = new List<long>();
                var page = await scan(order, null);
                found.AddRange(page.Ids);
                while (page.Next is not null)
                {
                    // A cursor carries its order; the query may omit it.
                    page = await scan(null, RoundTrip(page.Next));
                    found.AddRange(page.Ids);
                }
                return found;
            }
            var reversed = Enumerable.Reverse(ids).ToList();
            IdsEqual(ids, await All(null), name + " in the default order");
            IdsEqual(ids, await All(ScanOrder.Ascending), name + " ascending");
            IdsEqual(reversed, await All(ScanOrder.Descending), name + " descending");
            var descending = await scan(ScanOrder.Descending, null);
            IdsEqual(reversed.Skip(2).Take(2), (await scan(ScanOrder.Descending, descending.Next)).Ids, name + " second descending page");
            await Rejects(() => scan(ScanOrder.Ascending, descending.Next), "cursor", name + " ascending scan with a descending cursor");
        }
    }

    private static async Task ReplacesTaskRecords(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var firstId = await storage.MintIdAsync<TaskId>();
        var secondId = await storage.MintIdAsync<TaskId>();
        var thirdId = await storage.MintIdAsync<TaskId>();
        var first = PendingTask(firstId, rootId) with { Memos = O("{\"winner\":\"first\"}") };
        var second = PendingTask(secondId, rootId) with { Background = true };
        var third = PendingTask(thirdId, rootId) with { AbortRequested = true };
        await Commit(storage, new TaskWrite(first), new TaskWrite(second), new TaskWrite(third));

        var running = first with { State = new RunningTaskState(O("{\"phase\":\"effect\",\"attempt\":1}")), AbortRequested = true };
        await Commit(storage, new TaskWrite(running));
        Equal<TaskRecord?>(running, await storage.GetTaskAsync(firstId, Context), "running replacement");
        var terminal = new TaskRecord
        {
            Id = firstId, ConversationId = rootId, Kind = first.Kind, Version = first.Version, Input = first.Input,
            State = new TerminalTaskState(new CompletedTaskOutcome(J("{\"entryId\":99}"))), Background = false, AbortRequested = true,
        };
        await Commit(storage, new TaskWrite(terminal));
        Equal<TaskRecord?>(terminal, await storage.GetTaskAsync(firstId, Context), "terminal replacement");

        var pendingPage = await storage.ScanTasksAsync(new() { Status = DurableTaskStatus.Pending }, 1, null, Context);
        IdsEqual([secondId.Value], TaskIds(pendingPage), "first pending page");
        NotNull(pendingPage.Next, "first pending cursor");
        IdsEqual([thirdId.Value], TaskIds(await storage.ScanTasksAsync(new() { Status = DurableTaskStatus.Pending }, 1, pendingPage.Next, Context)), "second pending page");
        ItemsEqual([terminal], (await storage.ScanTasksAsync(new() { Status = DurableTaskStatus.Terminal, AbortRequested = true }, 10, null, Context)).Items,
            "terminal aborted tasks");
        IdsEqual([secondId.Value], TaskIds(await storage.ScanTasksAsync(new() { Background = true }, 10, null, Context)), "background tasks");
    }

    private static async Task StoresOwnersAndWaitingTasks(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var ownerId = await storage.MintIdAsync<TaskId>();
        var waitingId = await storage.MintIdAsync<TaskId>();
        var completingId = await storage.MintIdAsync<TaskId>();
        var owner = PendingTask(ownerId, rootId);
        var waiting = PendingTask(waitingId, rootId) with
        {
            Owner = ownerId, State = new WaitingTaskState(O("{\"phase\":\"next\"}"), [ownerId], JoinPolicy.AllSettled), Memos = O("{\"kept\":true}"),
        };
        var completing = PendingTask(completingId, rootId) with
        {
            Owner = ownerId, State = new CompletingTaskState(new FailedTaskOutcome(new TaskOutcomeError("held"))),
        };
        await Commit(storage, new TaskWrite(owner), new TaskWrite(waiting), new TaskWrite(completing));
        Equal<TaskRecord?>(waiting, await storage.GetTaskAsync(waitingId, Context), "waiting task");
        Equal<TaskRecord?>(completing, await storage.GetTaskAsync(completingId, Context), "completing task");
        async Task<ImmutableArray<TaskRecord>> Scan(DurableTaskStatus status) => (await storage.ScanTasksAsync(new() { Status = status }, 10, null, Context)).Items;
        ItemsEqual([waiting], await Scan(DurableTaskStatus.Waiting), "waiting scan");
        ItemsEqual([completing], await Scan(DurableTaskStatus.Completing), "completing scan");
        IdsEqual([ownerId.Value], (await Scan(DurableTaskStatus.Pending)).Select(task => task.Id.Value), "pending scan");
        var terminal = completing with { State = new TerminalTaskState(((CompletingTaskState)completing.State).Outcome) };
        await Commit(storage, new TaskWrite(terminal));
        ItemsEqual([], await Scan(DurableTaskStatus.Completing), "completing scan after settlement");
        ItemsEqual([terminal], await Scan(DurableTaskStatus.Terminal), "terminal scan");
    }

    private static async Task IndexesRequestIds(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var secondConversationId = await storage.MintIdAsync<ConversationId>();
        await Commit(storage, new ConversationWrite(new(secondConversationId)));
        var firstId = await storage.MintIdAsync<SubmissionId>();
        var secondId = await storage.MintIdAsync<SubmissionId>();
        var otherConversationId = await storage.MintIdAsync<SubmissionId>();
        var first = Queued(firstId, rootId, "same");
        var second = Queued(secondId, rootId, "other");
        var otherConversation = Queued(otherConversationId, secondConversationId, "same");
        await Commit(storage, new SubmissionWrite(first), new SubmissionWrite(second), new SubmissionWrite(otherConversation));
        Equal<SubmissionRecord?>(first, await storage.GetSubmissionByRequestAsync(rootId, "same", Context), "root request");
        Equal<SubmissionRecord?>(otherConversation, await storage.GetSubmissionByRequestAsync(secondConversationId, "same", Context), "other conversation request");

        var placedSecond = second with { Status = SubmissionStatus.Placed, Entry = await storage.MintIdAsync<EntryId>() };
        await Commit(storage, new SubmissionWrite(placedSecond));
        Equal<SubmissionRecord?>(placedSecond, await storage.GetSubmissionAsync(secondId, Context), "placed replacement");
        Equal<SubmissionRecord?>(placedSecond, await storage.GetSubmissionByRequestAsync(rootId, "other", Context), "placed request");

        async Task<List<long>> All(SubmissionQuery query)
        {
            var found = new List<long>();
            JsonObjectValue? cursor = null;
            do
            {
                var page = await storage.ScanSubmissionsAsync(query, 1, cursor, Context);
                found.AddRange(page.Items.Select(item => item.Id.Value));
                cursor = page.Next;
            } while (cursor is not null);
            return found;
        }
        IdsEqual([firstId.Value, secondId.Value, otherConversationId.Value], await All(new()), "all submissions");
        IdsEqual([firstId.Value, secondId.Value], await All(new() { ConversationId = rootId }), "root submissions");
        // A status change moves the record between status scans.
        IdsEqual([firstId.Value, otherConversationId.Value], await All(new() { Status = SubmissionStatus.Queued }), "queued submissions");
        IdsEqual([secondId.Value], await All(new() { Status = SubmissionStatus.Placed }), "placed submissions");
        IdsEqual([otherConversationId.Value], await All(new() { ConversationId = secondConversationId, Status = SubmissionStatus.Queued }), "queued in the other conversation");
        IdsEqual([], await All(new() { ConversationId = secondConversationId, Status = SubmissionStatus.Placed }), "placed in the other conversation");
        ItemsEqual([placedSecond], (await storage.ScanSubmissionsAsync(new() { Status = SubmissionStatus.Placed }, 10, null, Context)).Items, "placed records");

        static SubmissionRecord Queued(SubmissionId id, ConversationId conversationId, string requestId) => new()
        {
            Id = id, ConversationId = conversationId, RequestId = requestId, Type = SubmissionType.Input, Status = SubmissionStatus.Queued,
        };
    }

    private static async Task StoresPassiveWrites(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var doneId = await storage.MintIdAsync<SubmissionId>();
        var failedId = await storage.MintIdAsync<SubmissionId>();
        var queuedDone = new SubmissionRecord
        {
            Id = doneId, ConversationId = rootId, RequestId = "passive-done", Type = SubmissionType.Write, Status = SubmissionStatus.Queued,
        };
        var queuedFailed = queuedDone with { Id = failedId, RequestId = "passive-failed" };
        await Commit(storage, new SubmissionWrite(queuedDone), new SubmissionWrite(queuedFailed));

        var done = queuedDone with { Status = SubmissionStatus.Done, Entry = await storage.MintIdAsync<EntryId>() };
        var unanswered = queuedFailed with { Status = SubmissionStatus.Unanswered, Reason = "closed", Detail = J("{\"retryable\":false}") };
        await Commit(storage, new SubmissionWrite(done), new SubmissionWrite(unanswered));
        Equal<SubmissionRecord?>(done, await storage.GetSubmissionAsync(doneId, Context), "done write");
        Equal<SubmissionRecord?>(done, await storage.GetSubmissionByRequestAsync(rootId, "passive-done", Context), "done write by request");
        Equal<SubmissionRecord?>(unanswered, await storage.GetSubmissionAsync(failedId, Context), "unanswered write");
        Equal<SubmissionRecord?>(unanswered, await storage.GetSubmissionByRequestAsync(rootId, "passive-failed", Context), "unanswered write by request");
    }

    private static async Task ReconstructsRewindableDocuments(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var firstId = await storage.MintIdAsync<DocumentId>();
        var firstRecord = new DocumentCreate
        {
            Id = firstId, Kind = "conversation.notes", Scope = DocumentScope.Conversation(rootId), History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
        };
        var createdAt = await Commit(storage, new DocumentCreateWrite(firstRecord, DocumentContent.Base(1, O("{\"items\":[\"a\"],\"nested\":{\"count\":1}}"))));
        var changedAt = await Commit(storage, new DocumentChangeWrite(firstId,
            DocumentContent.Delta(1, Ops("[[\"p\",[\"items\"],1,0,[\"b\"]],[\"s\",[\"nested\",\"count\"],2]]"))));

        MatchDocument(await storage.GetDocumentAsync(firstId, createdAt, Context), 1, "{\"items\":[\"a\"],\"nested\":{\"count\":1}}", 0, "created");
        var changed = (await storage.GetDocumentAsync(firstId, changedAt, Context))!;
        Same("{\"items\":[\"a\",\"b\"],\"nested\":{\"count\":2}}", changed.Value, "changed value");
        Equal(1, changed.DeltasSinceBase, "changed deltas");
        Same("{\"items\":[\"a\",\"b\"],\"nested\":{\"count\":2}}", (await storage.GetDocumentAsync(firstId, DocumentPoint.Current, Context))?.Value, "current value");

        var checkpointAt = await Commit(storage, new DocumentChangeWrite(firstId,
            DocumentContent.Base(2, O("{\"items\":[\"checkpoint\"],\"nested\":{\"count\":3}}"))));
        var replacedAt = await Commit(storage, new DocumentChangeWrite(firstId,
            DocumentContent.Delta(2, Ops("[[\"r\",{\"items\":[\"replacement\"],\"nested\":{\"count\":4}}]]"))));
        MatchDocument(await storage.GetDocumentAsync(firstId, changedAt, Context), 1, "{\"items\":[\"a\",\"b\"],\"nested\":{\"count\":2}}", null, "changed after checkpoint");
        MatchDocument(await storage.GetDocumentAsync(firstId, checkpointAt, Context), 2, "{\"items\":[\"checkpoint\"],\"nested\":{\"count\":3}}", 0, "checkpoint");
        MatchDocument(await storage.GetDocumentAsync(firstId, replacedAt, Context), null, "{\"items\":[\"replacement\"],\"nested\":{\"count\":4}}", 1, "replaced");
        Equal(1, (await storage.GetDocumentAsync(firstId, DocumentPoint.Current, Context))?.DeltasSinceBase, "current deltas");

        var secondId = await storage.MintIdAsync<DocumentId>();
        var retiredAt = await Commit(storage,
            new DocumentCreateWrite(firstRecord with { Id = secondId }, DocumentContent.Base(1, O("{\"items\":[\"new\"]}"))),
            new DocumentRetireWrite(firstId),
            new DocumentChangeWrite(firstId, DocumentContent.Delta(2, Ops("[[\"s\",[\"retiring\"],true]]"))));
        var address = new DocumentAddress(firstRecord.Kind, firstRecord.Scope);
        Equal(firstId, (await storage.FindDocumentAsync(address, changedAt, Context))?.Id, "address at the change");
        var replacement = await storage.FindDocumentAsync(address, retiredAt, Context);
        Equal(secondId, replacement?.Id, "address at the retirement");
        Equal(retiredAt, replacement?.CreatedAt, "replacement creation");
        IdsEqual([firstId.Value], DocumentIds(await storage.ScanDocumentsAsync(new(firstRecord.Scope, changedAt), 10, null, Context)), "scope at the change");
        IdsEqual([secondId.Value], DocumentIds(await storage.ScanDocumentsAsync(new(firstRecord.Scope, retiredAt), 10, null, Context)), "scope at the retirement");
        Null(await storage.GetDocumentAsync(firstId, retiredAt, Context), "retired incarnation at its retirement");
        Same("{\"items\":[\"new\"]}", (await storage.GetDocumentAsync(secondId, DocumentPoint.Current, Context))?.Value, "replacement value");
    }

    private static async Task StreamsLongDocumentTails(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var id = await storage.MintIdAsync<DocumentId>();
        var record = new DocumentCreate
        {
            Id = id, Kind = "conversation.long-tail", Scope = DocumentScope.Conversation(rootId), History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
        };
        var initialRows = Enumerable.Range(0, 512).Select(value => ((double)value, $"row-{value}")).ToArray();
        var initial = LongTail(0, initialRows);
        var createdAt = await Commit(storage, new DocumentCreateWrite(record, DocumentContent.Base(1, initial)));
        var beforeRows = ((double Value, string Stable)[])initialRows.Clone();
        var beforeRevision = 0;
        var beforeReplacementAt = createdAt;
        for (var revision = 1; revision <= 24; revision++)
        {
            var index = revision * 17 % beforeRows.Length;
            beforeRows[index].Value = -revision;
            beforeRevision = revision;
            beforeReplacementAt = await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Delta(1, RowChange(index, revision))));
        }
        var beforeReplacement = LongTail(beforeRevision, beforeRows);

        var replacementRows = Enumerable.Range(0, 512).Select(value => (10_000.0 + value, $"new-{value}")).ToArray();
        var replacementSnapshot = LongTail(100, replacementRows);
        var replacementAt = await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Delta(1, [new ReplaceOp(replacementSnapshot)])));

        var currentRows = ((double Value, string Stable)[])replacementRows.Clone();
        var currentRevision = 100;
        for (var revision = 101; revision <= 124; revision++)
        {
            var index = revision * 19 % currentRows.Length;
            currentRows[index].Value = -revision;
            currentRevision = revision;
            await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Delta(1, RowChange(index, revision))));
        }
        var current = LongTail(currentRevision, currentRows);

        Same(initial, (await storage.GetDocumentAsync(id, createdAt, Context))?.Value, "initial revision");
        Same(beforeReplacement, (await storage.GetDocumentAsync(id, beforeReplacementAt, Context))?.Value, "revision before the replacement");
        Same(replacementSnapshot, (await storage.GetDocumentAsync(id, replacementAt, Context))?.Value, "replacement revision");
        Same(current, (await storage.GetDocumentAsync(id, DocumentPoint.Current, Context))?.Value, "current revision");

        static JsonObjectValue LongTail(int revision, (double Value, string Stable)[] rows) => Obj(("revision", JsonValue.Number(revision)),
            ("rows", JsonValue.Array(rows.Select(row => (JsonValue)Obj(("value", JsonValue.Number(row.Value)), ("stable", JsonValue.String(row.Stable)))))));

        static Op[] RowChange(int index, int revision) =>
        [
            new SetOp(DeltaPath.Of("rows", index, "value"), JsonValue.Number(-revision)),
            new SetOp(DeltaPath.Of("revision"), JsonValue.Number(revision)),
        ];
    }

    private static async Task CopiesDocumentBases(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var childId = await storage.MintIdAsync<ConversationId>();
        var secondChildId = await storage.MintIdAsync<ConversationId>();
        await Commit(storage, new ConversationWrite(new(childId)), new ConversationWrite(new(secondChildId)));
        var sourceId = await storage.MintIdAsync<DocumentId>();
        var sourceRecord = new DocumentCreate
        {
            Id = sourceId, Kind = "copy.source", Scope = DocumentScope.Conversation(rootId), History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
        };
        var createdAt = await Commit(storage, new DocumentCreateWrite(sourceRecord, DocumentContent.Base(2, O("{\"count\":1,\"rows\":[{\"value\":\"base\"}]}"))));
        await Commit(storage, new DocumentChangeWrite(sourceId,
            DocumentContent.Delta(2, Ops("[[\"s\",[\"count\"],2],[\"p\",[\"rows\"],1,0,[{\"value\":\"current\"}]]]"))));
        var historicalCopyId = await storage.MintIdAsync<DocumentId>();
        var currentCopyId = await storage.MintIdAsync<DocumentId>();
        var retiredCopyId = await storage.MintIdAsync<DocumentId>();
        DocumentCreate ChildRecord(DocumentId id, ConversationId conversationId) => sourceRecord with { Id = id, Scope = DocumentScope.Conversation(conversationId) };
        await Commit(storage,
            new DocumentCopyWrite(ChildRecord(historicalCopyId, childId), new(sourceId, createdAt)),
            new DocumentCopyWrite(ChildRecord(currentCopyId, secondChildId), new(sourceId, DocumentPoint.Current)),
            new DocumentCopyWrite(ChildRecord(retiredCopyId, rootId), new(sourceId, DocumentPoint.Current)),
            new DocumentRetireWrite(retiredCopyId));
        MatchDocument(await storage.GetDocumentAsync(historicalCopyId, DocumentPoint.Current, Context), 2, "{\"count\":1,\"rows\":[{\"value\":\"base\"}]}", null, "historical copy");
        MatchDocument(await storage.GetDocumentAsync(currentCopyId, DocumentPoint.Current, Context), 2,
            "{\"count\":2,\"rows\":[{\"value\":\"base\"},{\"value\":\"current\"}]}", null, "current copy");
        Null(await storage.GetDocumentAsync(retiredCopyId, DocumentPoint.Current, Context), "copy retired in its batch");

        await Commit(storage, new DocumentChangeWrite(sourceId, DocumentContent.Base(2, O("{\"count\":99,\"rows\":[]}"))), new DocumentRetireWrite(sourceId));
        Same("{\"count\":2,\"rows\":[{\"value\":\"base\"},{\"value\":\"current\"}]}",
            (await storage.GetDocumentAsync(currentCopyId, DocumentPoint.Current, Context))?.Value, "copy after the source changes");

        var latestSourceId = await storage.MintIdAsync<DocumentId>();
        var latestCopyId = await storage.MintIdAsync<DocumentId>();
        var latestSource = new DocumentCreate
        {
            Id = latestSourceId, Kind = "copy.latest", Scope = DocumentScope.Conversation(rootId), History = DocumentHistory.Latest, Fork = DocumentFork.Current,
        };
        await Commit(storage, new DocumentCreateWrite(latestSource, DocumentContent.Base(4, O("{\"retained\":\"copy\"}"))));
        await Commit(storage, new DocumentCopyWrite(latestSource with { Id = latestCopyId, Scope = DocumentScope.Conversation(childId) },
            new(latestSourceId, DocumentPoint.Current)));
        await Commit(storage, new DocumentChangeWrite(latestSourceId, DocumentContent.Base(4, O("{\"retained\":\"source-only\"}"))),
            new DocumentRetireWrite(latestSourceId));
        MatchDocument(await storage.GetDocumentAsync(latestCopyId, DocumentPoint.Current, Context), 4, "{\"retained\":\"copy\"}", null, "latest copy");

        var conflictId = await storage.MintIdAsync<DocumentId>();
        // Rejected without effect: the copy and the retirement in the same batch are both absent.
        await RejectsWith<Exception>(() => Commit(storage,
            new DocumentCopyWrite(ChildRecord(conflictId, childId), new(currentCopyId, DocumentPoint.Current)),
            new DocumentRetireWrite(currentCopyId)), "copy from a source retired in the batch");
        Null(await storage.GetDocumentAsync(conflictId, DocumentPoint.Current, Context), "rejected copy");
        Same("{\"count\":2,\"rows\":[{\"value\":\"base\"},{\"value\":\"current\"}]}",
            (await storage.GetDocumentAsync(currentCopyId, DocumentPoint.Current, Context))?.Value, "source after the rejected copy");

        var mismatchId = await storage.MintIdAsync<DocumentId>();
        await RejectsWith<Exception>(() => Commit(storage,
            new DocumentCopyWrite(ChildRecord(mismatchId, childId) with { Kind = "copy.mismatch" }, new(currentCopyId, DocumentPoint.Current))),
            "copy with a mismatched kind");
        Null(await storage.GetDocumentAsync(mismatchId, DocumentPoint.Current, Context), "mismatched copy");
    }

    private static async Task UsesBasesForVersionTransitions(IStorage storage)
    {
        await CreateRoot(storage);
        var id = await storage.MintIdAsync<DocumentId>();
        var record = new DocumentCreate { Id = id, Kind = "session.settings", Scope = DocumentScope.Session };
        await Commit(storage, new DocumentCreateWrite(record, DocumentContent.Base(1, O("{\"count\":1}"))));
        await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"s\",[\"count\"],2]]"))));
        var migratedAt = await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Base(2, O("{\"count\":3}"))));
        MatchDocument(await storage.GetDocumentAsync(id, DocumentPoint.Current, Context), 2, "{\"count\":3}", null, "migrated document");
        await Rejects(() => storage.GetDocumentAsync(id, migratedAt, Context), "does not retain historical content", "historical read of a Session document");

        await Rejects(() => Commit(storage, new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"s\",[\"count\"],4]]")))),
            "version transition requires a base", "delta across versions");
        Same("{\"count\":3}", (await storage.GetDocumentAsync(id, DocumentPoint.Current, Context))?.Value, "value after the rejected delta");
        await Commit(storage, new DocumentRetireWrite(id));
        Null(await storage.GetDocumentAsync(id, DocumentPoint.Current, Context), "retired document");
    }

    private static async Task IndexesLogicalAddresses(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var firstId = await storage.MintIdAsync<DocumentId>();
        var secondId = await storage.MintIdAsync<DocumentId>();
        var conversationId = await storage.MintIdAsync<DocumentId>();
        var taskId = await storage.MintIdAsync<TaskId>();
        var taskSingletonId = await storage.MintIdAsync<DocumentId>();
        var taskFamilyId = await storage.MintIdAsync<DocumentId>();
        var taskOtherKindId = await storage.MintIdAsync<DocumentId>();
        var createdAt = await Commit(storage,
            new TaskWrite(PendingTask(taskId, rootId)),
            new DocumentCreateWrite(new() { Id = firstId, Kind = "cache", Scope = DocumentScope.Session, Key = "__proto__" }, DocumentContent.Base(1, O("{\"owner\":\"first\"}"))),
            new DocumentCreateWrite(new() { Id = secondId, Kind = "cache", Scope = DocumentScope.Session, Key = "constructor" }, DocumentContent.Base(1, O("{\"owner\":\"second\"}"))),
            new DocumentCreateWrite(new()
            {
                Id = conversationId, Kind = "cache", Scope = DocumentScope.Conversation(rootId), History = DocumentHistory.Latest, Fork = DocumentFork.Current, Key = "__proto__",
            }, DocumentContent.Base(1, O("{\"owner\":\"conversation\"}"))),
            new DocumentCreateWrite(new() { Id = taskSingletonId, Kind = "task.cache", Scope = DocumentScope.Task(taskId) }, DocumentContent.Base(1, O("{\"owner\":\"singleton\"}"))),
            new DocumentCreateWrite(new() { Id = taskFamilyId, Kind = "task.cache", Scope = DocumentScope.Task(taskId), Key = "member" }, DocumentContent.Base(1, O("{\"owner\":\"family\"}"))),
            new DocumentCreateWrite(new() { Id = taskOtherKindId, Kind = "task.other", Scope = DocumentScope.Task(taskId) }, DocumentContent.Base(1, O("{\"owner\":\"other\"}"))));

        Equal(firstId, (await storage.FindDocumentAsync(new("cache", DocumentScope.Session, "__proto__"), DocumentPoint.Current, Context))?.Id, "__proto__ member");
        Equal(1, (await storage.ScanDocumentsAsync(new(DocumentScope.Session, DocumentPoint.Current), 1, null, Context)).Items.Length, "one-item page");
        var first = await storage.ScanDocumentsAsync(new(DocumentScope.Session, DocumentPoint.Current), 1, null, Context);
        var second = await storage.ScanDocumentsAsync(new(DocumentScope.Session, DocumentPoint.Current), 1, first.Next, Context);
        IdsEqual([firstId.Value, secondId.Value], first.Items.Concat(second.Items).Select(record => record.Id.Value), "Session documents");
        IdsEqual([conversationId.Value], DocumentIds(await storage.ScanDocumentsAsync(
            new(DocumentScope.Conversation(rootId), DocumentPoint.Current), 10, null, Context)), "conversation documents");
        Equal(taskSingletonId, (await storage.FindDocumentAsync(new("task.cache", DocumentScope.Task(taskId)), DocumentPoint.Current, Context))?.Id, "task singleton");
        Equal(taskFamilyId, (await storage.FindDocumentAsync(new("task.cache", DocumentScope.Task(taskId), "member"), DocumentPoint.Current, Context))?.Id, "task family member");
        IdsEqual([taskSingletonId.Value, taskFamilyId.Value], DocumentIds(await storage.ScanDocumentsAsync(
            new(DocumentScope.Task(taskId), DocumentPoint.Current) { Kind = "task.cache" }, 10, null, Context)), "task documents of one kind");
        await Rejects(() => storage.GetDocumentAsync(taskSingletonId, createdAt, Context), "does not retain historical content", "historical read of a task document");
    }

    private static async Task KeepsDocumentLifecycleAtomic(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var firstId = await storage.MintIdAsync<DocumentId>();
        var secondId = await storage.MintIdAsync<DocumentId>();
        var record = new DocumentCreate { Id = firstId, Kind = "singleton", Scope = DocumentScope.Session };
        await Commit(storage, new DocumentCreateWrite(record, DocumentContent.Base(1, O("{\"value\":1}"))));
        await Rejects(() => Commit(storage,
            new DocumentCreateWrite(record with { Id = secondId }, DocumentContent.Base(1, O("{\"value\":2}"))),
            new DocumentChangeWrite(firstId, DocumentContent.Delta(1, []))), "already has a current incarnation", "second current incarnation");
        Same("{\"value\":1}", (await storage.GetDocumentAsync(firstId, DocumentPoint.Current, Context))?.Value, "first incarnation");
        Null(await storage.GetDocumentAsync(secondId, DocumentPoint.Current, Context), "rejected incarnation");

        var emptyId = await storage.MintIdAsync<DocumentId>();
        var emptyAt = await Commit(storage,
            new DocumentCreateWrite(new()
            {
                Id = emptyId, Kind = record.Kind, Key = "empty", Scope = DocumentScope.Conversation(rootId), History = DocumentHistory.Rewindable, Fork = DocumentFork.Initial,
            }, DocumentContent.Base(1, JsonObjectValue.Empty)),
            new DocumentRetireWrite(emptyId));
        Null(await storage.GetDocumentAsync(emptyId, DocumentPoint.Current, Context), "create-plus-retire now");
        Null(await storage.GetDocumentAsync(emptyId, emptyAt, Context), "create-plus-retire at its commit");
        Null(await storage.FindDocumentAsync(new(record.Kind, DocumentScope.Conversation(rootId), "empty"), emptyAt, Context), "create-plus-retire address");
    }

    private static async Task RollsBackOnDocumentFailure(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var taskId = await storage.MintIdAsync<TaskId>();
        var submissionId = await storage.MintIdAsync<SubmissionId>();
        var documentId = await storage.MintIdAsync<DocumentId>();
        var task = PendingTask(taskId, rootId);
        var submission = new SubmissionRecord
        {
            Id = submissionId, ConversationId = rootId, RequestId = "atomic", Type = SubmissionType.Input, Status = SubmissionStatus.Queued,
        };
        var record = new DocumentCreate { Id = documentId, Kind = "atomic", Scope = DocumentScope.Session };
        var baselineSeq = await Commit(storage, new TaskWrite(task), new SubmissionWrite(submission),
            new DocumentCreateWrite(record, DocumentContent.Base(1, O("{\"count\":1}"))));

        var entryId = await storage.MintIdAsync<EntryId>();
        var conflictingDocumentId = await storage.MintIdAsync<DocumentId>();
        await Rejects(() => Commit(storage,
            new TaskWrite(task with { State = new RunningTaskState(O("{\"phase\":\"effect\"}")) }),
            new SubmissionWrite(submission with { Status = SubmissionStatus.Unanswered, Reason = "failed" }),
            new EntryWrite(Entry(entryId, rootId, "transient")),
            new DocumentCreateWrite(record with { Id = conflictingDocumentId }, DocumentContent.Base(1, O("{\"count\":2}")))),
            "already has a current incarnation", "batch with a conflicting document");

        Equal<TaskRecord?>(task, await storage.GetTaskAsync(taskId, Context), "task after rollback");
        ItemsEqual([task], (await storage.ScanTasksAsync(new() { Status = DurableTaskStatus.Pending }, 10, null, Context)).Items, "pending tasks after rollback");
        Equal<SubmissionRecord?>(submission, await storage.GetSubmissionByRequestAsync(rootId, "atomic", Context), "submission after rollback");
        Null(await storage.GetEntryAsync(entryId, Context), "entry after rollback");
        Null(await storage.GetDocumentAsync(conflictingDocumentId, DocumentPoint.Current, Context), "conflicting document");
        Equal(documentId, (await storage.FindDocumentAsync(DocumentAddress.Of(record), DocumentPoint.Current, Context))?.Id, "current incarnation");
        var afterRollbackSeq = await Commit(storage, new DocumentChangeWrite(documentId, DocumentContent.Delta(1, Ops("[[\"s\",[\"count\"],3]]"))));
        Greater(afterRollbackSeq.Value, baselineSeq.Value, "sequence after rollback");
    }

    private static async Task KeepsStringIdentitiesLossless(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var first = ((char)0xD800).ToString();
        var second = ((char)0xD801).ToString();
        var firstTaskId = await storage.MintIdAsync<TaskId>();
        var secondTaskId = await storage.MintIdAsync<TaskId>();
        var firstSubmissionId = await storage.MintIdAsync<SubmissionId>();
        var secondSubmissionId = await storage.MintIdAsync<SubmissionId>();
        var firstKindDocumentId = await storage.MintIdAsync<DocumentId>();
        var secondKindDocumentId = await storage.MintIdAsync<DocumentId>();
        var firstKeyDocumentId = await storage.MintIdAsync<DocumentId>();
        var secondKeyDocumentId = await storage.MintIdAsync<DocumentId>();
        await Commit(storage,
            new TaskWrite(PendingTask(firstTaskId, rootId) with { Kind = first }),
            new TaskWrite(PendingTask(secondTaskId, rootId) with { Kind = second }),
            new SubmissionWrite(new() { Id = firstSubmissionId, ConversationId = rootId, RequestId = first, Type = SubmissionType.Input, Status = SubmissionStatus.Queued }),
            new SubmissionWrite(new() { Id = secondSubmissionId, ConversationId = rootId, RequestId = second, Type = SubmissionType.Input, Status = SubmissionStatus.Queued }),
            new DocumentCreateWrite(new() { Id = firstKindDocumentId, Kind = first, Scope = DocumentScope.Session }, DocumentContent.Base(1, O("{\"identity\":\"first kind\"}"))),
            new DocumentCreateWrite(new() { Id = secondKindDocumentId, Kind = second, Scope = DocumentScope.Session }, DocumentContent.Base(1, O("{\"identity\":\"second kind\"}"))),
            new DocumentCreateWrite(new() { Id = firstKeyDocumentId, Kind = "family", Key = first, Scope = DocumentScope.Session }, DocumentContent.Base(1, O("{\"identity\":\"first key\"}"))),
            new DocumentCreateWrite(new() { Id = secondKeyDocumentId, Kind = "family", Key = second, Scope = DocumentScope.Session }, DocumentContent.Base(1, O("{\"identity\":\"second key\"}"))));

        IdsEqual([firstTaskId.Value], TaskIds(await storage.ScanTasksAsync(new() { Kind = first }, 10, null, Context)), "tasks of the first kind");
        IdsEqual([secondTaskId.Value], TaskIds(await storage.ScanTasksAsync(new() { Kind = second }, 10, null, Context)), "tasks of the second kind");
        Equal(first, (await storage.GetTaskAsync(firstTaskId, Context))?.Kind, "first task kind");
        Equal(second, (await storage.GetTaskAsync(secondTaskId, Context))?.Kind, "second task kind");
        Equal(first, (await storage.GetSubmissionByRequestAsync(rootId, first, Context))?.RequestId, "first request ID");
        Equal(firstSubmissionId, (await storage.GetSubmissionByRequestAsync(rootId, first, Context))?.Id, "first request");
        Equal(secondSubmissionId, (await storage.GetSubmissionByRequestAsync(rootId, second, Context))?.Id, "second request");
        Equal(firstKindDocumentId, (await storage.FindDocumentAsync(new(first, DocumentScope.Session), DocumentPoint.Current, Context))?.Id, "first kind document");
        Equal(secondKindDocumentId, (await storage.FindDocumentAsync(new(second, DocumentScope.Session), DocumentPoint.Current, Context))?.Id, "second kind document");
        Equal(firstKeyDocumentId, (await storage.FindDocumentAsync(new("family", DocumentScope.Session, first), DocumentPoint.Current, Context))?.Id, "first key document");
        Equal(secondKeyDocumentId, (await storage.FindDocumentAsync(new("family", DocumentScope.Session, second), DocumentPoint.Current, Context))?.Id, "second key document");
        IdsEqual([firstKindDocumentId.Value], DocumentIds(await storage.ScanDocumentsAsync(
            new(DocumentScope.Session, DocumentPoint.Current) { Kind = first }, 10, null, Context)), "documents of the first kind");
    }

    private static async Task KeepsOneIdNamespace(IStorage storage)
    {
        var rootId = await CreateRoot(storage);
        var explicitEntryId = new EntryId(100);
        await Commit(storage, new EntryWrite(Entry(explicitEntryId, rootId)));
        Equal(101L, (await storage.MintIdAsync<EntryId>()).Value, "minted ID after an explicit one");
        await Rejects(() => Commit(storage, new TaskWrite(PendingTask(new TaskId(explicitEntryId.Value), rootId))),
            $"ID {explicitEntryId} already belongs to entry", "task reusing an entry ID");

        await Commit(storage, new EntryWrite(Entry(new EntryId(Ids.MaxSafeInteger), rootId, "last-id")));
        await Rejects(() => storage.MintIdAsync<EntryId>(), "ID space is exhausted", "minting after the last safe ID");
        await Rejects(() => storage.MintIdAsync<EntryId>(), "ID space is exhausted", "minting again after the last safe ID");
    }

    private static async Task RejectsAfterClose(IStorage storage)
    {
        await CreateRoot(storage);
        await storage.CloseAsync(Context);
        await Rejects(() => storage.GetConversationAsync(ConversationId.Root, Context), "closed", "read after close");
        await Rejects(() => Commit(storage), "closed", "commit after close");
        await Rejects(() => storage.MintIdAsync<ConversationId>(), "closed", "mint after close");
    }

    private static async Task<ConversationId> CreateRoot(IStorage storage)
    {
        await Commit(storage, new ConversationWrite(new(ConversationId.Root)));
        return ConversationId.Root;
    }

    private static TaskRecord PendingTask(TaskId id, ConversationId conversationId, string phase = "ready") => new()
    {
        Id = id, ConversationId = conversationId, Kind = "test.task", Version = 1, Input = Obj(("value", id.ToJson())),
        State = new PendingTaskState(Obj(("phase", JsonValue.String(phase)))), Background = false, AbortRequested = false,
    };

    private static EntryRecord Entry(EntryId id, ConversationId conversationId, string kind = "message") =>
        new() { Id = id, ConversationId = conversationId, Kind = kind };

    private static Task<Seq> Commit(IStorage storage, params StorageWrite[] writes) => storage.CommitAsync(writes, Context);

    private static JsonValue J(string json) => JsonValue.Parse(json);
    private static JsonObjectValue O(string json) => (JsonObjectValue)JsonValue.Parse(json);
    private static ImmutableArray<Op> Ops(string json) => [.. ((JsonArrayValue)JsonValue.Parse(json)).Select(Op.FromJson)];
    private static JsonObjectValue Obj(params (string Key, JsonValue Value)[] properties) =>
        JsonObjectValue.From(properties.Select(property => KeyValuePair.Create(property.Key, property.Value)));
    private static JsonObjectValue? RoundTrip(JsonObjectValue? cursor) => cursor is null ? null : (JsonObjectValue)JsonValue.Parse(cursor.ToJson());

    private static (long[] Ids, JsonObjectValue? Next) PageIds<T>(Page<T> page, Func<T, long> id) => ([.. page.Items.Select(id)], page.Next);
    private static IEnumerable<long> EntryIds(Page<EntryRecord> page) => page.Items.Select(item => item.Id.Value);
    private static IEnumerable<long> ConversationIds(Page<ConversationRecord> page) => page.Items.Select(item => item.Id.Value);
    private static IEnumerable<long> TaskIds(Page<TaskRecord> page) => page.Items.Select(item => item.Id.Value);
    private static IEnumerable<long> DocumentIds(Page<DocumentRecord> page) => page.Items.Select(item => item.Id.Value);

    private static void Fail(string message) => throw new StorageConformanceException(message);

    private static void Ok(bool value, string what)
    {
        if (!value) Fail(what + ": expected true");
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) Fail($"{what}: expected <{expected?.ToString() ?? "undefined"}>, actual <{actual?.ToString() ?? "undefined"}>");
    }

    private static void Null(object? actual, string what)
    {
        if (actual is not null) Fail($"{what}: expected undefined, actual <{actual}>");
    }

    private static void NotNull(object? actual, string what)
    {
        if (actual is null) Fail(what + ": expected a value");
    }

    private static void Greater(long actual, long expected, string what)
    {
        if (actual <= expected) Fail($"{what}: expected more than {expected}, actual {actual}");
    }

    private static void IdsEqual(IEnumerable<long> expected, IEnumerable<long> actual, string what)
    {
        long[] left = [.. expected], right = [.. actual];
        if (!left.SequenceEqual(right)) Fail($"{what}: expected [{string.Join(",", left)}], actual [{string.Join(",", right)}]");
    }

    private static void ItemsEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string what)
    {
        if (!expected.SequenceEqual(actual)) Fail($"{what}: expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}]");
    }

    private static void Same(string expected, JsonValue? actual, string what) => Same(JsonValue.Parse(expected), actual, what);

    private static void Same(JsonValue expected, JsonValue? actual, string what)
    {
        if (!JsonValue.DeepEquals(expected, actual)) Fail($"{what}: expected {expected.ToJson()}, actual {actual?.ToJson() ?? "undefined"}");
    }

    /// <summary>vitest's toMatchObject for a stored document: the given fields only.</summary>
    private static void MatchDocument(StoredDocument? actual, int? version, string? value, int? deltasSinceBase, string what)
    {
        if (actual is null) { Fail(what + ": expected a document, actual undefined"); return; }
        if (version is { } expectedVersion) Equal(expectedVersion, actual.Version, what + " version");
        if (value is not null) Same(value, actual.Value, what + " value");
        if (deltasSinceBase is { } expectedDeltas) Equal(expectedDeltas, actual.DeltasSinceBase, what + " deltas since base");
    }

    private static async Task Rejects(Func<Task> operation, string messageIncludes, string what)
    {
        try { await operation(); }
        catch (Exception error) when (error is not StorageConformanceException)
        {
            if (!error.Message.Contains(messageIncludes, StringComparison.Ordinal))
                Fail($"{what}: expected an error including \"{messageIncludes}\", actual {error.GetType().Name}: {error.Message}");
            return;
        }
        Fail($"{what}: expected an error including \"{messageIncludes}\"");
    }

    private static async Task RejectsWith<TException>(Func<Task> operation, string what) where TException : Exception
    {
        try { await operation(); }
        catch (TException) { return; }
        catch (Exception error) when (error is not StorageConformanceException)
        {
            Fail($"{what}: expected {typeof(TException).Name}, actual {error.GetType().Name}: {error.Message}");
        }
        Fail($"{what}: expected {typeof(TException).Name}");
    }
}
