// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/session-documents.test.ts. Verbatim apart from the adaptations
// in SessionSupport.cs and these:
// - "copies assigned values per placement": placements are immutable JsonValue trees, so a placement cannot be mutated afterwards and two
//   placements of one value may share it; the case checks the values and that the caller's later replacement does not reach the document.
// - "rejects non-JSON initializer values and draft placements": a Date or undefined cannot be a JsonValue. The initializer case returns
//   null ("must return a JSON object") and the placement case passes null (ArgumentNullException before the draft changes).
// - Pending-acquisition cases: with MemoryStorage a Tx operation completes synchronously, so only the held findDocument leaves one pending,
//   exactly as upstream's cases arrange it. The negative "line stays held" check yields instead of flushing (the line is held).
// - "reloads an unloaded document from Storage": the reload is shown by the storage read, not a new object (an immutable stored value may
//   be handed out again).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using static Program;

internal static partial class SessionTests
{
    private static IEnumerable<(string, Func<Task>)> DocumentCases(string prefix, Func<IStorage> storageFactory)
    {
        var liveInitCount = 0;
        const string liveInitial = "{\"items\":[],\"nested\":{\"count\":0},\"other\":{\"label\":\"x\"}}";
        var liveDoc = Documents.DefineConversationDoc("test.live", 1, DocumentFork.Initial, () =>
        {
            Interlocked.Increment(ref liveInitCount);
            return O(liveInitial);
        });
        var rewindableLiveDoc = Documents.DefineRewindableConversationDoc("test.live", 1, DocumentFork.AsOf, () => O(liveInitial));
        var liveDocV2 = Documents.DefineConversationDoc("test.live", 2, DocumentFork.Initial, () => O(liveInitial));
        var counterDoc = Documents.DefineSessionDoc("test.counter", 1, () => O("{\"count\":0}"));
        var seeds = new List<string>();
        var memberDoc = Documents.DefineSessionDocFamily("test.member", 1, seed =>
        {
            lock (seeds) seeds.Add(S(seed));
            return Obj(("seed", seed), ("hits", 0));
        });

        async Task<(ControlledStorage Storage, Session Session, List<CommitPublication> Publications, ConversationId ConversationId)> SetupLive()
        {
            var (storage, session, publications) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            await session.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(liveDoc, conversationId);
                live.GetArray("items").AddRange(["a", "b"]);
            }, Context);
            return (storage, session, publications, conversationId);
        }

        yield return Case(prefix + "creates-an-initial-base-on-first-access-and-adopts-it-after-storage-success", async () =>
        {
            var (storage, session, publications) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            await session.CommitAsync(async tx => (await tx.DocAsync(liveDoc, conversationId))["message"] = "hello", Context);
            var writes = storage.LastCommit;
            Equal(1, writes.Length, "writes");
            var create = writes[0] as DocumentCreateWrite ?? throw new InvalidOperationException("Expected document.create");
            Equal("test.live", create.Record.Kind, "kind");
            Equal(DocumentScope.Conversation(conversationId), create.Record.Scope, "scope");
            Equal<DocumentHistory?>(DocumentHistory.Latest, create.Record.History, "history");
            Equal<DocumentFork?>(DocumentFork.Initial, create.Record.Fork, "fork");
            Equal(1, create.Content.Version, "content version");
            SameJson("{\"items\":[],\"nested\":{\"count\":0},\"other\":{\"label\":\"x\"},\"message\":\"hello\"}", create.Content.Value, "content value");
            var snapshot = await session.SnapshotAsync(liveDoc, conversationId, Context);
            SameJson("{\"message\":\"hello\",\"items\":[],\"nested\":{\"count\":0},\"other\":{\"label\":\"x\"}}", snapshot, "snapshot");
            await Flush(session);
            var publication = Last(publications);
            var published = DocumentChanges(publication)[0];
            Equal(publication.Seq, published.Record.CreatedAt, "createdAt");
            Same(snapshot, published.Value, "published value");
            Equal<ConversationId?>(conversationId, published.ConversationId, "conversation");
            var admitted = storage.LastAdmitted.OfType<DocumentCreateWrite>().Single();
            Same(published.Value, admitted.Content.Value, "admitted base");
        });

        yield return Case(prefix + "never-creates-on-snapshot-and-returns-undefined-when-absent", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var before = storage.CommitCount;
            Check(await session.SnapshotAsync(liveDoc, conversationId, Context) is null, "conversation");
            Check(await session.SnapshotAsync(counterDoc, Context) is null, "session");
            Check(await session.SnapshotAsync(memberDoc, "k", Context) is null, "family");
            Equal(before, storage.CommitCount, "commits");
            Equal(1, storage.MintCount, "mints");
        });

        yield return Case(prefix + "returns-shared-immutable-snapshots-and-keeps-prior-revisions-stable", async () =>
        {
            var (_, session, _, conversationId) = await SetupLive();
            var first = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            Same(first, await session.SnapshotAsync(liveDoc, conversationId, Context), "shared snapshot");
            await session.CommitAsync(async tx => (await tx.DocAsync(liveDoc, conversationId)).GetObject("nested")["count"] = 1, Context);
            var second = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            Check(!ReferenceEquals(second, first), "a new revision");
            Equal(0.0, N(At(first, "nested", "count")), "first count");
            Equal(1.0, N(At(second, "nested", "count")), "second count");
            // Unchanged subtrees are structurally shared between immutable revisions.
            Same(first["items"], second["items"], "items shared");
            Same(first["other"], second["other"], "other shared");
        });

        yield return Case(prefix + "adopts-by-pointer-swap-and-shares-operation-payloads-with-the-published-revision", async () =>
        {
            var (storage, session, publications, conversationId) = await SetupLive();
            await session.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(liveDoc, conversationId);
                live["other"] = O("{\"label\":\"y\"}");
                live.GetArray("items").Add("c");
            }, Context);
            await Flush(session);
            var snapshot = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            var published = DocumentChanges(Last(publications))[0];
            Same(snapshot, published.Value, "published value");
            var admitted = storage.LastAdmitted.OfType<DocumentChangeWrite>().Single();
            var delta = admitted.Content as DocumentDelta ?? throw new InvalidOperationException("Expected delta");
            SameOps(delta.Ops, published.Ops, "published ops");
            var set = published.Ops.OfType<SetOp>().First();
            Equal("[\"s\",[\"other\"],{\"label\":\"y\"}]", set.ToJson().ToJson(), "set op");
            // Trusted immutability: the Session makes no second copy of operation payloads.
            Same(snapshot["other"], set.Value, "payload shared");
        });

        yield return Case(prefix + "copies-assigned-values-per-placement", async () =>
        {
            var (_, session, _, conversationId) = await SetupLive();
            var value = O("{\"label\":\"shared\"}");
            await session.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(liveDoc, conversationId);
                live["other"] = value;
                live["copy"] = value;
                value = value.SetItem("label", "mutated");
            }, Context);
            var snapshot = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            SameJson("{\"label\":\"shared\"}", snapshot["other"], "other");
            SameJson("{\"label\":\"shared\"}", snapshot["copy"], "copy");
            Check(!ReferenceEquals(snapshot["other"], value), "the caller's replacement does not reach the document");
        });

        yield return Case(prefix + "suppresses-writes-and-publications-for-empty-batches", async () =>
        {
            var (storage, session, publications, conversationId) = await SetupLive();
            await Flush(session);
            var commits = storage.CommitCount;
            var published = Count(publications);
            await session.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(liveDoc, conversationId);
                live.GetObject("nested")["count"] = 0;
                live.GetArray("items").Add("z");
                live.GetArray("items").RemoveAt(live.GetArray("items").Count - 1);
            }, Context);
            await Flush(session);
            Equal(commits, storage.CommitCount, "commits");
            Equal(published, Count(publications), "publications");
        });

        yield return Case(prefix + "writes-and-publishes-replayable-nonempty-structural-no-ops", async () =>
        {
            var (storage, session, publications, conversationId) = await SetupLive();
            var before = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            await session.CommitAsync(async tx =>
            {
                var items = (await tx.DocAsync(liveDoc, conversationId)).GetArray("items");
                var first = items.RemoveAt(0);
                items.Insert(0, first);
            }, Context);
            await Flush(session);
            var write = storage.LastCommit[0] as DocumentChangeWrite;
            Check(write?.Content is DocumentDelta, "a delta change");
            var after = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            Check(JsonValue.DeepEquals(before, after), "equal value");
            Check(!ReferenceEquals(before, after), "a new revision");
            Check(DocumentChanges(Last(publications))[0].Ops.Length > 0, "published operations");
        });

        yield return Case(prefix + "revokes-escaped-drafts-when-the-callback-settles", async () =>
        {
            var (_, session, _, conversationId) = await SetupLive();
            DraftObject? escaped = null;
            DraftArray? items = null;
            await session.CommitAsync(async tx =>
            {
                escaped = await tx.DocAsync(liveDoc, conversationId);
                items = escaped.GetArray("items");
                escaped["message"] = "inside";
            }, Context);
            Throws(() => _ = escaped!.Get("message"), "", "read");
            Throws(() => escaped!["message"] = "outside", "", "write");
            Throws(() => _ = items!.Count, "", "array length");
            Throws(() => items!.Add("outside"), "", "array push");
            Equal("inside", S((await session.SnapshotAsync(liveDoc, conversationId, Context))!["message"]), "committed");

            var returned = await session.CommitAsync(tx => tx.DocAsync(liveDoc, conversationId), Context);
            Throws(() => _ = returned.Get("message"), "", "returned draft");
        });

        yield return Case(prefix + "aborts-every-change-when-the-callback-fails", async () =>
        {
            var (storage, session, _, conversationId) = await SetupLive();
            var before = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            var commits = storage.CommitCount;
            await RejectsAsync(() => session.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(liveDoc, conversationId);
                var counter = await tx.DocAsync(counterDoc);
                live["message"] = "lost";
                counter["count"] = 5;
                throw new InvalidOperationException("callback failed");
            }, Context), "callback failed", "callback");
            Equal(commits, storage.CommitCount, "commits");
            Same(before, await session.SnapshotAsync(liveDoc, conversationId, Context), "live unchanged");
            Check(await session.SnapshotAsync(counterDoc, Context) is null, "counter absent");
            await session.CommitAsync(async tx => (await tx.DocAsync(liveDoc, conversationId))["message"] = "kept", Context);
            Equal("kept", S((await session.SnapshotAsync(liveDoc, conversationId, Context))!["message"]), "kept");
        });

        yield return Case(prefix + "memoizes-concurrent-duplicate-acquisition-and-initializes-once", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var initCount = Volatile.Read(ref liveInitCount);
            var mints = storage.MintCount;
            await session.CommitAsync(async tx =>
            {
                var both = await Task.WhenAll(tx.DocAsync(liveDoc, conversationId), tx.DocAsync(liveDoc, conversationId));
                Same(both[0], both[1], "duplicate acquisitions");
                Same(both[0], await tx.DocAsync(liveDoc, conversationId), "later acquisition");
                both[0]["message"] = "once";
            }, Context);
            Equal(initCount + 1, Volatile.Read(ref liveInitCount), "initialized once");
            Equal(mints + 1, storage.MintCount, "minted once");
            Equal(1, storage.LastCommit.OfType<DocumentCreateWrite>().Count(), "one creation");
        });

        yield return Case(prefix + "uses-the-first-family-seed-and-ignores-seeds-for-existing-members", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            lock (seeds) seeds.Clear();
            await session.CommitAsync(async tx =>
            {
                var first = await tx.DocAsync(memberDoc, "k", "first");
                var second = await tx.DocAsync(memberDoc, "k", "second");
                Same(first, second, "same draft");
                first["hits"] = N(first["hits"]) + 1;
            }, Context);
            await session.CommitAsync(async tx =>
            {
                var k = await tx.DocAsync(memberDoc, "k", "third");
                k["hits"] = N(k["hits"]) + 1;
                var other = await tx.DocAsync(memberDoc, "other", "fourth");
                other["hits"] = N(other["hits"]) + 1;
            }, Context);
            lock (seeds) Seq(["first", "fourth"], seeds, "seeds");
            SameJson("{\"seed\":\"first\",\"hits\":2}", await session.SnapshotAsync(memberDoc, "k", Context), "k");
            SameJson("{\"seed\":\"fourth\",\"hits\":1}", await session.SnapshotAsync(memberDoc, "other", Context), "other");
        });

        yield return Case(prefix + "rejects-a-callback-that-succeeds-with-a-pending-acquisition-and-drains-it", async () =>
        {
            var (storage, session, _, conversationId) = await SetupLive();
            await session.UnloadDocumentsAsync();
            var gate = storage.HoldFindDocument();
            var commits = storage.CommitCount;
            Task? pending = null;
            var commit = session.CommitAsync(tx => { pending = tx.DocAsync(liveDoc, conversationId); }, Context);
            await gate.Entered;
            var settled = false;
            _ = commit.ContinueWith(_ => settled = true, TaskScheduler.Default);
            await Task.Yield();
            // The line stays held until the late acquisition settles.
            Check(!settled, "the commit waits for the pending acquisition");
            gate.Release();
            await RejectsAsync(() => commit, "pending Tx operations", "commit");
            await RejectsAsync(() => pending!, "Transaction has settled", "pending acquisition");
            Equal(commits, storage.CommitCount, "commits");
            await session.CommitAsync(async tx => (await tx.DocAsync(liveDoc, conversationId))["message"] = "after", Context);
            Equal("after", S((await session.SnapshotAsync(liveDoc, conversationId, Context))!["message"]), "after");
        });

        yield return Case(prefix + "does-not-initialize-or-mint-for-an-absent-acquisition-that-finishes-after-settlement", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            var initialized = 0;
            var lateDoc = Documents.DefineSessionDoc("test.late", 1, () =>
            {
                initialized++;
                return O("{\"count\":0}");
            });
            var gate = storage.HoldFindDocument();
            var mints = storage.MintCount;
            Task? pending = null;
            var commit = session.CommitAsync(tx => { pending = tx.DocAsync(lateDoc); }, Context);
            await gate.Entered;
            gate.Release();
            await RejectsAsync(() => commit, "pending Tx operations", "commit");
            await RejectsAsync(() => pending!, "Transaction has settled", "pending acquisition");
            Equal(0, initialized, "initializer calls");
            Equal(mints, storage.MintCount, "mints");
        });

        yield return Case(prefix + "rejects-with-the-callback-error-when-it-fails-with-a-pending-acquisition", async () =>
        {
            var (storage, session, _, conversationId) = await SetupLive();
            await session.UnloadDocumentsAsync();
            var gate = storage.HoldFindDocument();
            Task? pending = null;
            var commit = session.CommitAsync(tx =>
            {
                pending = tx.DocAsync(liveDoc, conversationId);
                throw new InvalidOperationException("callback failed");
            }, Context);
            await gate.Entered;
            gate.Release();
            await RejectsAsync(() => commit, "callback failed", "commit");
            await RejectsAsync(() => pending!, "Transaction has settled", "pending acquisition");
        });

        yield return Case(prefix + "rejects-tx-use-after-the-callback-settles", async () =>
        {
            var (_, session, _, conversationId) = await SetupLive();
            Transaction? captured = null;
            await session.CommitWithAsync(tx => { captured = tx; }, Context);
            await RejectsAsync(() => captured!.DocAsync(liveDoc, conversationId), "Transaction has settled", "doc");
            await RejectsAsync(() => captured!.GetConversationAsync(conversationId), "Transaction has settled", "conversation");
            Throws(() => captured!.SetTask(null!), "Transaction has settled", "setTask");
        });

        yield return Case(prefix + "rejects-tokens-whose-semantics-or-version-disagree-with-the-stored-incarnation", async () =>
        {
            var (_, session, _, conversationId) = await SetupLive();
            await RejectsAsync(() => session.SnapshotAsync(rewindableLiveDoc, conversationId, Context), "does not match the supplied definition semantics", "snapshot");
            await RejectsAsync(() => session.CommitAsync(tx => tx.DocAsync(rewindableLiveDoc, conversationId), Context),
                "does not match the supplied definition semantics", "doc");
            await RejectsAsync(() => session.CommitAsync(tx => tx.DocAsync(liveDocV2, conversationId), Context), "requires migration from version 1", "version");
        });

        yield return Case(prefix + "rejects-non-json-initializer-values-and-draft-placements-before-storage-admission", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var nullDoc = Documents.DefineSessionDoc("test.date", 1, () => null!);
            await RejectsAsync(() => session.CommitAsync(tx => tx.DocAsync(nullDoc), Context), "must return a JSON object", "initializer");
            var commits = storage.CommitCount;
            await ThrowsAsync<ArgumentNullException>(() => session.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(liveDoc, conversationId);
                live.GetArray("items").Add(null!);
            }, Context), "null placement");
            Equal(commits, storage.CommitCount, "commits");
            Check(await session.SnapshotAsync(liveDoc, conversationId, Context) is null, "nothing created");
        });

        yield return Case(prefix + "rolls-back-prepared-documents-when-batch-assembly-fails", async () =>
        {
            var (storage, session, _, conversationId) = await SetupLive();
            await session.CommitAsync(async tx => (await tx.DocAsync(counterDoc))["count"] = 1, Context);
            var live = await session.SnapshotAsync(liveDoc, conversationId, Context);
            var counter = await session.SnapshotAsync(counterDoc, Context);
            var commits = storage.CommitCount;
            await RejectsAsync(() => session.CommitWithAsync(async tx =>
            {
                (await tx.DocAsync(liveDoc, conversationId))["message"] = "lost";
                (await tx.DocAsync(counterDoc))["count"] = 2;
                // Replacing a missing task fails during assembly, after every change was prepared.
                tx.SetTask(new TaskRecord
                {
                    Id = Ids.FromNumber<TaskId>(999), ConversationId = conversationId, Kind = "missing", Version = 1, Input = JsonValue.Null,
                    Background = false, AbortRequested = false, State = new PendingTaskState(O("{\"phase\":\"start\"}")),
                });
            }, Context), "Task 999 does not exist", "assembly");
            Equal(commits, storage.CommitCount, "commits");
            Same(live, await session.SnapshotAsync(liveDoc, conversationId, Context), "live");
            Same(counter, await session.SnapshotAsync(counterDoc, Context), "counter");
            await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(liveDoc, conversationId))["message"] = "next";
                (await tx.DocAsync(counterDoc))["count"] = 3;
            }, Context);
            Equal(3.0, N((await session.SnapshotAsync(counterDoc, Context))!["count"]), "count");
        });

        yield return Case(prefix + "fails-the-session-after-a-storage-failure-and-publishes-nothing", async () =>
        {
            var (storage, session, publications, conversationId) = await SetupLive();
            await Flush(session);
            var before = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            var published = Count(publications);
            storage.FailNextCommit(new IOException("disk vanished"));
            await RejectsAsync(() => session.CommitAsync(async tx => (await tx.DocAsync(liveDoc, conversationId))["message"] = "uncertain", Context),
                "disk vanished", "commit");
            await Flush(session);
            Equal(published, Count(publications), "publications");
            Check(!before.ContainsKey("message"), "previous revision unchanged");
            await ThrowsAsync<SessionFailedException>(() => session.SnapshotAsync(liveDoc, conversationId, Context), "snapshot");
            await ThrowsAsync<SessionFailedException>(() => session.CommitAsync(_ => { }, Context), "commit");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-the-previous-revision-unchanged-through-storage-settlement", async () =>
        {
            var (storage, session, _, conversationId) = await SetupLive();
            var before = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            var copy = before.ToJson();
            var gate = storage.HoldCommits();
            var commit = session.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(liveDoc, conversationId);
                live.GetArray("items").Add("c");
                live.GetObject("nested")["count"] = 9;
            }, Context);
            await gate.Entered;
            Same(before, await session.SnapshotAsync(liveDoc, conversationId, Context), "snapshot during settlement");
            Equal(copy, before.ToJson(), "before unchanged");
            gate.Release();
            await commit;
            var after = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            SameJson("[\"a\",\"b\",\"c\"]", after["items"], "items");
            Equal(copy, before.ToJson(), "before still unchanged");
        });

        yield return Case(prefix + "retires-documents-and-creates-a-new-incarnation-at-the-same-address", async () =>
        {
            var (storage, session, publications, conversationId) = await SetupLive();
            await Flush(session);
            var oldId = DocumentChanges(Last(publications))[0].Record.Id;
            await session.CommitAsync(async tx =>
            {
                var live = await tx.DocAsync(liveDoc, conversationId);
                live["message"] = "final";
                await tx.RetireDocAsync(liveDoc, conversationId);
                var replacement = await tx.DocAsync(liveDoc, conversationId);
                Check(!ReferenceEquals(replacement, live), "a new draft");
                replacement["message"] = "new";
            }, Context);
            var writes = storage.LastCommit;
            Equal(3, writes.Length, "writes");
            Check(writes.Any(write => write is DocumentChangeWrite change && change.Id == oldId), "final change of the old incarnation");
            Check(writes.Contains(new DocumentRetireWrite(oldId)), "retirement");
            Check(writes.Any(write => write is DocumentCreateWrite { Record.Kind: "test.live" }), "new incarnation");
            await Flush(session);
            var publication = Last(publications);
            var changes = DocumentChanges(publication);
            var (retired, created) = (changes[0], changes[1]);
            Check(retired.Record.Id == oldId && retired.Value is null && retired.Ops.IsEmpty, "retired change");
            Equal<Seq?>(publication.Seq, retired.Record.RetiredAt, "retiredAt");
            Check(created.Record.Id != oldId, "new ID");
            Equal(publication.Seq, created.Record.CreatedAt, "createdAt");
            Check(S(created.Value!["message"]) == "new" && ((JsonArrayValue)created.Value["items"]).Count == 0 && created.Ops.IsEmpty, "created change");
            Same(created.Value, await session.SnapshotAsync(liveDoc, conversationId, Context), "snapshot is the new incarnation");

            await session.CommitAsync(tx => tx.RetireDocAsync(liveDoc, conversationId), Context);
            Check(await session.SnapshotAsync(liveDoc, conversationId, Context) is null, "retired");
            await session.UnloadDocumentsAsync();
            Check(await session.SnapshotAsync(liveDoc, conversationId, Context) is null, "retired after unload");
            // Retiring an absent address is a no-op.
            var commits = storage.CommitCount;
            await session.CommitAsync(tx => tx.RetireDocAsync(liveDoc, conversationId), Context);
            Equal(commits, storage.CommitCount, "no-op retirement");
        });

        yield return Case(prefix + "retires-without-acquisition-and-recreates-both-existing-and-absent-addresses", async () =>
        {
            var (storage, session, publications, conversationId) = await SetupLive();
            await Flush(session);
            var oldId = DocumentChanges(Last(publications))[0].Record.Id;
            await session.UnloadDocumentsAsync();
            await session.CommitAsync(async tx =>
            {
                var retired = tx.RetireDocAsync(liveDoc, conversationId);
                var replacement = tx.DocAsync(liveDoc, conversationId);
                await Task.WhenAll(replacement, retired);
                (await replacement)["message"] = "replacement";
            }, Context);
            var existingWrites = storage.LastCommit;
            Equal(2, existingWrites.Length, "existing writes");
            Check(existingWrites.Contains(new DocumentRetireWrite(oldId)), "retirement");
            Check(existingWrites.Any(write => write is DocumentCreateWrite { Record.Kind: "test.live" }), "creation");

            await session.CommitAsync(async tx =>
            {
                var retired = tx.RetireDocAsync(memberDoc, "absent");
                var replacement = tx.DocAsync(memberDoc, "absent", "seed");
                await Task.WhenAll(replacement, retired);
                (await replacement)["hits"] = 1;
            }, Context);
            var absentWrites = storage.LastCommit;
            Equal(0, absentWrites.OfType<DocumentRetireWrite>().Count(), "no retirement");
            Equal(1, absentWrites.OfType<DocumentCreateWrite>().Count(), "one creation");
            SameJson("{\"seed\":\"seed\",\"hits\":1}", await session.SnapshotAsync(memberDoc, "absent", Context), "member");
        });

        yield return Case(prefix + "retires-the-existing-incarnation-when-retirement-races-a-pending-acquisition", async () =>
        {
            var (storage, session, publications, conversationId) = await SetupLive();
            await Flush(session);
            var oldId = DocumentChanges(Last(publications))[0].Record.Id;
            await session.CommitAsync(async tx =>
            {
                var acquired = tx.DocAsync(liveDoc, conversationId);
                var retired = tx.RetireDocAsync(liveDoc, conversationId);
                (await acquired)["message"] = "final";
                await retired;
            }, Context);
            var firstWrites = storage.LastCommit;
            Equal(2, firstWrites.Length, "first writes");
            Check(firstWrites.Any(write => write is DocumentChangeWrite { Content: DocumentDelta } change && change.Id == oldId), "final delta");
            Check(firstWrites.Contains(new DocumentRetireWrite(oldId)), "retirement");
            Check(await session.SnapshotAsync(liveDoc, conversationId, Context) is null, "retired");

            await session.CommitAsync(async tx => (await tx.DocAsync(liveDoc, conversationId))["message"] = "second", Context);
            await Flush(session);
            var secondId = DocumentChanges(Last(publications))[0].Record.Id;
            await session.CommitAsync(async tx =>
            {
                var acquired = tx.DocAsync(liveDoc, conversationId);
                var retired = tx.RetireDocAsync(liveDoc, conversationId);
                var recreated = tx.DocAsync(liveDoc, conversationId);
                await Task.WhenAll(acquired, recreated, retired);
                var (old, fresh) = (await acquired, await recreated);
                Check(!ReferenceEquals(fresh, old), "a fresh draft");
                fresh["message"] = "third";
            }, Context);
            var writes = storage.LastCommit;
            Equal(2, writes.Length, "writes");
            Check(writes.Contains(new DocumentRetireWrite(secondId)), "retirement");
            Check(writes.Any(write => write is DocumentCreateWrite { Record.Kind: "test.live" }), "creation");
            Equal("third", S((await session.SnapshotAsync(liveDoc, conversationId, Context))!["message"]), "third");
        });

        yield return Case(prefix + "reloads-an-unloaded-document-from-storage", async () =>
        {
            var (storage, session, _, conversationId) = await SetupLive();
            var loaded = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            var reads = storage.DocumentReadCount;
            await session.UnloadDocumentsAsync();
            var reloaded = (await session.SnapshotAsync(liveDoc, conversationId, Context))!;
            // Upstream: a new object. An immutable stored value may be handed out again; the storage read shows the reload.
            Check(storage.DocumentReadCount > reads, "reloaded from storage");
            Check(JsonValue.DeepEquals(reloaded, loaded), "equal");
            await session.CommitAsync(async tx => (await tx.DocAsync(liveDoc, conversationId)).GetArray("items").Add("c"), Context);
            await session.UnloadDocumentsAsync();
            SameJson("[\"a\",\"b\",\"c\"]", (await session.SnapshotAsync(liveDoc, conversationId, Context))!["items"], "items");
        });

        yield return Case(prefix + "delivers-complete-publications-synchronously-after-adoption", async () =>
        {
            var (_, session, publications, conversationId) = await SetupLive();
            var published = Count(publications);
            DurableContext? listenerContext = null;
            var subscription = session.SubscribeCommits((_, delivered) => listenerContext = delivered);
            var result = await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(liveDoc, conversationId))["message"] = "m";
                return "done";
            }, Context);
            Equal("done", result, "result");
            Same(Context, listenerContext, "listener context");
            Equal(published + 1, Count(publications), "publications");
            subscription.Dispose();
        });

        yield return Case(prefix + "publishes-close-synchronously-and-supports-unsubscription", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            var calls = new List<string>();
            session.SubscribeClose(() => calls.Add("active"));
            var subscription = session.SubscribeClose(() => calls.Add("removed"));
            subscription.Dispose();
            await session.CloseAsync(Context);
            Seq(["active"], calls, "calls");
        });

        yield return Case(prefix + "settles-admitted-commits-before-close-and-rejects-later-admission", async () =>
        {
            var (storage, session, _, conversationId) = await SetupLive();
            var gate = storage.HoldCommits();
            var commit = session.CommitAsync(async tx => (await tx.DocAsync(liveDoc, conversationId))["message"] = "admitted", Context);
            await gate.Entered;
            var queued = session.CommitAsync(async tx => (await tx.DocAsync(liveDoc, conversationId))["message"] = "queued", Context);
            var admittedSnapshot = session.SnapshotAsync(memberDoc, "absent", Context);
            var closed = session.CloseAsync(Context);
            await RejectsAsync(() => session.CommitAsync(_ => { }, Context), "closed", "commit after close");
            await RejectsAsync(() => session.SnapshotAsync(liveDoc, conversationId, Context), "closed", "snapshot after close");
            gate.Release();
            await commit;
            await queued;
            Check(await admittedSnapshot is null, "admitted snapshot");
            await closed;
            await RejectsAsync(() => storage.FindDocumentAsync(new DocumentAddress("test.live", DocumentScope.Conversation(conversationId)),
                DocumentPoint.Current, Context), "", "storage closed");
        });
    }
}
