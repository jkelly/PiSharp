// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/session-checkpoints-migrations.test.ts. Verbatim apart from the
// adaptations in SessionSupport.cs and these: a checkpoint predicate is a delegate, so "its definition as the receiver" (`this.initial()`)
// is the token's definition reached through the closure; a migration result is an immutable value, so "copies the callback result"
// checks that replacing the callback's retained variable changes nothing, and "strict-checks migration results" returns null (the only
// non-object a C# migration can return; "must return a JSON object").
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using static Program;

internal static partial class SessionTests
{
    private static IEnumerable<(string, Func<Task>)> CheckpointsMigrations(string prefix, Func<IStorage> storageFactory)
    {
        static List<string> ChangeKinds(IEnumerable<IEnumerable<StorageWrite>> commits) =>
            [.. commits.SelectMany(writes => writes.OfType<DocumentChangeWrite>().Select(write => write.Content.Kind == DocumentContentKind.Base ? "base" : "delta"))];
        static string KindOf(StorageWrite write) => write is DocumentChangeWrite change
            ? (change.Content.Kind == DocumentContentKind.Base ? "base" : "delta") : write.Type;
        static Task Increment(Session session, SessionDocToken token) =>
            session.CommitAsync(async tx => { var draft = await tx.DocAsync(token); draft["count"] = N(draft["count"]) + 1; }, Context);

        // ─── Session document checkpoints ──────────────────────────────────

        yield return Case(prefix + "calls-checkpointwhen-with-its-definition-as-the-receiver", async () =>
        {
            SessionDocToken? doc = null;
            doc = Documents.DefineSessionDoc("checkpoint.receiver", 1, () => O("{\"count\":0}"),
                checkpointWhen: (value, _, _) => N(value["count"]) == N(doc!.Definition.Initial()["count"]) + 2);
            var (storage, session, _) = Open(storageFactory);
            for (var count = 0; count <= 2; count++)
            {
                var value = count;
                await session.CommitAsync(async tx => (await tx.DocAsync(doc))["count"] = value, Context);
            }
            Seq(["delta", "base"], ChangeKinds(storage.Commits.Select(commit => (IEnumerable<StorageWrite>)commit)), "kinds");
        });

        yield return Case(prefix + "selects-bases-only-for-nonempty-ordinary-batches-and-passes-the-exact-prepared-revision-and-ops", async () =>
        {
            var calls = new List<(JsonObjectValue Value, IReadOnlyList<Op> Ops)>();
            var falseCalls = new List<(JsonObjectValue Value, IReadOnlyList<Op> Ops)>();
            var baseDoc = Documents.DefineSessionDoc("checkpoint.base", 1, () => O("{\"items\":[]}"),
                checkpointWhen: (value, ops, _) => { calls.Add((value, ops)); return true; });
            var deltaDoc = Documents.DefineSessionDoc("checkpoint.delta", 1, () => O("{\"count\":0}"),
                checkpointWhen: (value, ops, _) => { falseCalls.Add((value, ops)); return false; });
            var defaultDoc = Documents.DefineSessionDoc("checkpoint.default", 1, () => O("{\"count\":0}"));
            var (storage, session, publications) = Open(storageFactory);

            await session.CommitAsync(async tx =>
            {
                await tx.DocAsync(baseDoc);
                await tx.DocAsync(deltaDoc);
                await tx.DocAsync(defaultDoc);
            }, Context);
            Equal(0, calls.Count, "no predicate call on creation");
            Equal(0, falseCalls.Count, "no false-predicate call on creation");
            Check(DocumentWrites(storage.LastCommit).All(write => write is DocumentCreateWrite), "creations store bases");

            await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(baseDoc)).GetArray("items").Add("x");
                var delta = await tx.DocAsync(deltaDoc);
                delta["count"] = N(delta["count"]) + 1;
                var plain = await tx.DocAsync(defaultDoc);
                plain["count"] = N(plain["count"]) + 1;
            }, Context);
            await Flush(session);
            var writes = storage.LastAdmitted.OfType<DocumentChangeWrite>().ToList();
            Seq(["base", "delta", "delta"], writes.Select(KindOf), "kinds");
            Equal(1, calls.Count, "predicate calls");
            Equal(1, falseCalls.Count, "false-predicate calls");
            var snapshot = (await session.SnapshotAsync(baseDoc, Context))!;
            var deltaSnapshot = (await session.SnapshotAsync(deltaDoc, Context))!;
            var published = DocumentChanges(Last(publications));
            Same(snapshot, calls[0].Value, "predicate value");
            SameOps(published[0].Ops, calls[0].Ops, "predicate ops");
            Same(deltaSnapshot, falseCalls[0].Value, "false predicate value");
            SameOps(published[1].Ops, falseCalls[0].Ops, "false predicate ops");
            var ordinary = writes[1].Content as DocumentDelta ?? throw new InvalidOperationException("Expected ordinary delta");
            SameOps(ordinary.Ops, falseCalls[0].Ops, "stored delta ops");
            var checkpoint = writes[0].Content as DocumentBase ?? throw new InvalidOperationException("Expected checkpoint base");
            Same(snapshot, checkpoint.Value, "stored base");
        });

        yield return Case(prefix + "passes-the-stored-delta-count-since-the-newest-base-including-after-unload-and-version-bases", async () =>
        {
            var seen = new List<int>();
            var v1 = Documents.DefineSessionDoc("checkpoint.deltas-since-base", 1, () => O("{\"count\":0}"),
                checkpointWhen: (_, _, info) => { seen.Add(info.DeltasSinceBase); return info.DeltasSinceBase >= 2; });
            var v2 = Documents.DefineSessionDoc("checkpoint.deltas-since-base", 2, () => O("{\"count\":0}"),
                migrate: (value, _) => Obj(("count", value["count"])),
                checkpointWhen: (_, _, info) => { seen.Add(info.DeltasSinceBase); return false; });
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(v1), Context);
            await Increment(session, v1);
            await Increment(session, v1);
            await Increment(session, v1);
            await session.UnloadDocumentsAsync();
            await Increment(session, v1);
            Seq([0, 1, 2, 0], seen, "seen");
            Seq(["delta", "delta", "base", "delta"], storage.Commits.TakeLast(4).Select(writes => KindOf(writes[0])), "kinds");

            // A required version base resets the count without calling the predicate.
            await Increment(session, v2);
            await Increment(session, v2);
            Seq([0, 1, 2, 0, 0], seen, "seen after the version base");
            var versionBase = (storage.Commits[^2][0] as DocumentChangeWrite)?.Content;
            Check(versionBase is DocumentBase { Version: 2 }, "version base");
        });

        yield return Case(prefix + "skips-the-predicate-for-empty-batches-but-calls-it-for-nonempty-structural-no-ops", async () =>
        {
            var calls = 0;
            var doc = Documents.DefineSessionDoc("checkpoint.no-op", 1, () => O("{\"items\":[\"a\",\"b\"]}"),
                checkpointWhen: (_, _, _) => { calls++; return false; });
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(doc), Context);
            var commits = storage.CommitCount;

            await session.CommitAsync(async tx =>
            {
                var items = (await tx.DocAsync(doc)).GetArray("items");
                items.Add("x");
                items.RemoveAt(items.Count - 1);
            }, Context);
            Equal(0, calls, "no call for an empty batch");
            Equal(commits, storage.CommitCount, "no commit");

            await session.CommitAsync(async tx =>
            {
                var items = (await tx.DocAsync(doc)).GetArray("items");
                var first = items.RemoveAt(0);
                items.Insert(0, first);
            }, Context);
            Equal(1, calls, "one call for a structural no-op");
            Check(storage.LastCommit[0] is DocumentChangeWrite { Content: DocumentDelta }, "a delta change");
        });

        yield return Case(prefix + "rolls-back-every-prepared-document-when-a-checkpoint-predicate-throws", async () =>
        {
            var throwCheckpoint = true;
            var firstCalls = 0;
            var first = Documents.DefineSessionDoc("checkpoint.rollback.first", 1, () => O("{\"count\":0}"),
                checkpointWhen: (_, _, _) => { firstCalls++; return false; });
            var second = Documents.DefineSessionDoc("checkpoint.rollback.second", 1, () => O("{\"count\":0}"),
                checkpointWhen: (_, _, _) => throwCheckpoint ? throw new InvalidOperationException("checkpoint failed") : false);
            var (storage, session, publications) = Open(storageFactory);
            await session.CommitAsync(async tx =>
            {
                await tx.DocAsync(first);
                await tx.DocAsync(second);
            }, Context);
            await Flush(session);
            var firstValue = (await session.SnapshotAsync(first, Context))!;
            var secondValue = (await session.SnapshotAsync(second, Context))!;
            var commits = storage.CommitCount;
            var published = Count(publications);

            await RejectsAsync(() => session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(first))["count"] = 1;
                (await tx.DocAsync(second))["count"] = 2;
            }, Context), "checkpoint failed", "commit");
            await Flush(session);
            Equal(1, firstCalls, "first predicate calls");
            Equal(commits, storage.CommitCount, "commits");
            Equal(published, Count(publications), "publications");
            Same(firstValue, await session.SnapshotAsync(first, Context), "first unchanged");
            Same(secondValue, await session.SnapshotAsync(second, Context), "second unchanged");

            throwCheckpoint = false;
            await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(first))["count"] = 3;
                (await tx.DocAsync(second))["count"] = 4;
            }, Context);
            SameJson("{\"count\":3}", await session.SnapshotAsync(first, Context), "first");
            SameJson("{\"count\":4}", await session.SnapshotAsync(second, Context), "second");
        });

        yield return Case(prefix + "persists-repeated-false-decisions-as-deltas-and-replays-the-complete-tail", async () =>
        {
            var calls = 0;
            var doc = Documents.DefineSessionDoc("checkpoint.tail", 1, () => O("{\"values\":[]}"), checkpointWhen: (_, _, _) => { calls++; return false; });
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(doc), Context);
            for (var value = 1; value <= 8; value++)
            {
                var item = value;
                await session.CommitAsync(async tx => (await tx.DocAsync(doc)).GetArray("values").Add(item), Context);
            }
            var changes = storage.Commits.SelectMany(writes => writes.OfType<DocumentChangeWrite>()).ToList();
            Equal(8, changes.Count, "changes");
            Check(changes.All(write => write.Content is DocumentDelta), "every change is a delta");
            Equal(8, calls, "calls");
            await session.UnloadDocumentsAsync();
            SameJson("{\"values\":[1,2,3,4,5,6,7,8]}", await session.SnapshotAsync(doc, Context), "replayed");
        });

        yield return Case(prefix + "keeps-a-prepared-root-replacement-as-a-delta-when-the-predicate-is-false", async () =>
        {
            var initial = JsonObjectValue.CreateBuilder();
            for (var index = 0; index < 4_100; index++) initial[$"field{index}"] = 0;
            var initialValue = initial.ToImmutable();
            var doc = Documents.DefineSessionDoc("checkpoint.root-replacement", 1, () => initialValue, checkpointWhen: (_, _, _) => false);
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(doc), Context);
            await session.CommitAsync(async tx =>
            {
                var value = await tx.DocAsync(doc);
                for (var index = 0; index < 4_100; index++) value[$"field{index}"] = 1;
            }, Context);
            var write = storage.LastCommit[0] as DocumentChangeWrite;
            var delta = write?.Content as DocumentDelta ?? throw new InvalidOperationException("Expected root replacement delta");
            Equal(1, delta.Ops.Length, "one operation");
            Check(delta.Ops[0] is ReplaceOp, "a root replacement");
            await session.UnloadDocumentsAsync();
            Equal(1.0, N((await session.SnapshotAsync(doc, Context))!["field4099"]), "replayed");
        });

        yield return Case(prefix + "uses-ordinary-checkpoint-selection-before-retirement", async () =>
        {
            var calls = 0;
            var doc = Documents.DefineSessionDoc("checkpoint.retire", 1, () => O("{\"count\":0}"), checkpointWhen: (_, _, _) => { calls++; return true; });
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(doc), Context);
            await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(doc))["count"] = 1;
                await tx.RetireDocAsync(doc);
            }, Context);
            Equal(1, calls, "calls");
            var writes = DocumentWrites(storage.LastCommit);
            Check(writes is [DocumentChangeWrite { Content: DocumentBase }, DocumentRetireWrite], "base then retirement");
        });

        // ─── Session document migrations ───────────────────────────────────

        yield return Case(prefix + "migrates-read-only-once-per-cold-load-copies-the-callback-result-and-writes-nothing", async () =>
        {
            var old = Documents.DefineSessionDoc("migration.read-only", 1, () => O("{\"count\":2}"));
            var calls = 0;
            JsonObjectValue? retained = null;
            var current = Documents.DefineSessionDoc("migration.read-only", 3, () => O("{\"count\":0,\"labels\":[]}"), migrate: (value, fromVersion) =>
            {
                Equal(1, fromVersion, "fromVersion");
                calls++;
                retained = Obj(("count", value["count"]), ("labels", J("[\"migrated\"]")));
                return retained;
            });
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(old), Context);
            await session.UnloadDocumentsAsync();
            var commits = storage.CommitCount;

            var first = (await session.SnapshotAsync(current, Context))!;
            Same(first, await session.SnapshotAsync(current, Context), "cached");
            Equal(1, calls, "calls");
            Equal(commits, storage.CommitCount, "commits");
            retained = O("{\"count\":99,\"labels\":[\"migrated\",\"mutated\"]}");
            SameJson("{\"count\":2,\"labels\":[\"migrated\"]}", first, "first");

            await session.UnloadDocumentsAsync();
            var second = (await session.SnapshotAsync(current, Context))!;
            Check(!ReferenceEquals(second, first), "reloaded");
            Check(JsonValue.DeepEquals(second, first), "equal");
            Equal(2, calls, "calls after reload");
            Equal(commits, storage.CommitCount, "commits after reload");
        });

        yield return Case(prefix + "writes-the-required-base-on-the-first-successful-transaction-then-writes-deltas", async () =>
        {
            var old = Documents.DefineSessionDoc("migration.transition", 1, () => O("{\"count\":4}"));
            var checkpoints = 0;
            var current = Documents.DefineSessionDoc("migration.transition", 3, () => O("{\"count\":0}"),
                migrate: (value, fromVersion) => Obj(("count", N(value["count"]) + fromVersion - 1)),
                checkpointWhen: (_, _, _) => { checkpoints++; return false; });
            var (storage, session, publications) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(old), Context);
            await session.UnloadDocumentsAsync();
            // An observer of the older shape.
            var watch = (await session.WatchDocAsync(old, Context))!;
            var frames = new List<string>();
            watch.Start((value, ops, _) =>
            {
                lock (frames) frames.Add(value!.ToJson() + " " + OpsJson(ops));
                return Task.CompletedTask;
            });
            SameJson("{\"count\":4}", await session.SnapshotAsync(current, Context), "migrated snapshot");
            var snapshot = await session.SnapshotAsync(current, Context);
            // An observer of the new shape: the migration changes nothing it sees.
            var currentWatch = (await session.WatchDocAsync(current, Context))!;
            var currentFrames = new List<string>();
            currentWatch.Start((value, _, _) =>
            {
                lock (currentFrames) currentFrames.Add(value!.ToJson());
                return Task.CompletedTask;
            });

            await session.CommitAsync(tx => tx.DocAsync(current), Context);
            await Flush(session);
            Check(storage.LastCommit[0] is DocumentChangeWrite { Content: DocumentBase { Version: 3 } versionBase } &&
                JsonValue.DeepEquals(versionBase.Value, O("{\"count\":4}")), "version base");
            Equal(0, checkpoints, "no predicate call");
            // The migration-only base is published, so the older-shape watch receives the new value as a root replacement.
            var published = DocumentChanges(Last(publications));
            Check(published is [{ Version: 3, Ops.IsEmpty: true } change] && JsonValue.DeepEquals(change.Value, O("{\"count\":4}")), "published base");
            lock (frames) Seq(["{\"count\":4} [[\"r\",{\"count\":4}]]"], frames, "older-shape frames");
            lock (currentFrames) Equal(0, currentFrames.Count, "new-shape frames");
            await watch.StopAsync();
            await currentWatch.StopAsync();
            Same(snapshot, await session.SnapshotAsync(current, Context), "unchanged revision");

            await session.CommitAsync(async tx => (await tx.DocAsync(current))["count"] = 7, Context);
            Check(storage.LastCommit[0] is DocumentChangeWrite { Content: DocumentDelta { Version: 3 } }, "then deltas");
            Equal(1, checkpoints, "predicate call");
            await session.UnloadDocumentsAsync();
            SameJson("{\"count\":7}", await session.SnapshotAsync(current, Context), "replayed");
        });

        yield return Case(prefix + "rolls-migration-and-edits-back-with-the-callback-then-coalesces-later-edits-into-one-base", async () =>
        {
            var old = Documents.DefineSessionDoc("migration.rollback", 1, () => O("{\"count\":1}"));
            var migrations = 0;
            var current = Documents.DefineSessionDoc("migration.rollback", 2, () => O("{\"count\":0,\"migrated\":false}"), migrate: (value, _) =>
            {
                migrations++;
                return Obj(("count", value["count"]), ("migrated", true));
            });
            var (storage, session, publications) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(old), Context);
            await session.UnloadDocumentsAsync();
            var commits = storage.CommitCount;

            await RejectsAsync(() => session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(current))["count"] = 8;
                throw new InvalidOperationException("rollback");
            }, Context), "rollback", "commit");
            Equal(commits, storage.CommitCount, "commits");
            SameJson("{\"count\":1,\"migrated\":true}", await session.SnapshotAsync(current, Context), "rolled back");
            Equal(1, migrations, "migrations");

            await session.CommitAsync(async tx =>
            {
                var value = await tx.DocAsync(current);
                value["count"] = 9;
                value["migrated"] = false;
            }, Context);
            var writes = DocumentWrites(storage.LastCommit);
            Check(writes is [DocumentChangeWrite { Content: DocumentBase { Version: 2 } coalesced }] &&
                JsonValue.DeepEquals(coalesced.Value, O("{\"count\":9,\"migrated\":false}")), "one coalesced base");
            await Flush(session);
            var published = DocumentChanges(Last(publications))[0];
            var admitted = storage.LastAdmitted[0] as DocumentChangeWrite;
            var admittedBase = admitted?.Content as DocumentBase ?? throw new InvalidOperationException("Expected migration base");
            Same(await session.SnapshotAsync(current, Context), published.Value, "published value");
            Same(admittedBase.Value, published.Value, "stored base value");
            Check(published.Ops.Length > 0, "published operations");
            Equal(1, migrations, "no second migration");
        });

        yield return Case(prefix + "strict-checks-migration-results-before-tracker-ownership-and-remains-usable-after-rejection", async () =>
        {
            var old = Documents.DefineSessionDoc("migration.invalid", 1, () => O("{\"count\":1}"));
            var invalid = Documents.DefineSessionDoc("migration.invalid", 2, () => O("{}"), migrate: (_, _) => null!);
            var other = Documents.DefineSessionDoc("migration.invalid.other", 1, () => O("{\"ok\":true}"));
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(old), Context);
            await session.UnloadDocumentsAsync();
            var commits = storage.CommitCount;
            await RejectsAsync(() => session.SnapshotAsync(invalid, Context), "must return a JSON object", "snapshot");
            await RejectsAsync(() => session.CommitAsync(tx => tx.DocAsync(invalid), Context), "must return a JSON object", "doc");
            Equal(commits, storage.CommitCount, "commits");
            await session.CommitAsync(tx => tx.DocAsync(other), Context);
            SameJson("{\"ok\":true}", await session.SnapshotAsync(other, Context), "other");
        });

        yield return Case(prefix + "rejects-newer-stored-versions-and-older-versions-without-migration-for-snapshots-and-transactions", async () =>
        {
            var v2 = Documents.DefineSessionDoc("migration.compatibility", 2, () => O("{\"count\":2}"));
            var v1 = Documents.DefineSessionDoc("migration.compatibility", 1, () => O("{\"count\":1}"));
            var v3WithoutMigration = Documents.DefineSessionDoc("migration.compatibility", 3, () => O("{\"count\":3}"));
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(v2), Context);
            await session.UnloadDocumentsAsync();
            var commits = storage.CommitCount;

            await RejectsAsync(() => session.SnapshotAsync(v1, Context), "newer version 2 than 1", "older token snapshot");
            await RejectsAsync(() => session.CommitAsync(tx => tx.DocAsync(v1), Context), "newer version 2 than 1", "older token doc");
            await RejectsAsync(() => session.SnapshotAsync(v3WithoutMigration, Context), "requires migration from version 2", "newer token snapshot");
            await RejectsAsync(() => session.CommitAsync(tx => tx.DocAsync(v3WithoutMigration), Context), "requires migration from version 2", "newer token doc");
            Equal(commits, storage.CommitCount, "commits");
        });

        yield return Case(prefix + "persists-a-required-migration-base-before-retirement-without-consulting-the-checkpoint-predicate", async () =>
        {
            var old = Documents.DefineSessionDoc("migration.retire", 1, () => O("{\"count\":1}"));
            var current = Documents.DefineSessionDoc("migration.retire", 2, () => O("{\"count\":0}"), migrate: (value, _) => Obj(("count", value["count"])),
                checkpointWhen: (_, _, _) => throw new InvalidOperationException("must not run"));
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(old), Context);
            await session.UnloadDocumentsAsync();
            await session.CommitAsync(async tx =>
            {
                await tx.DocAsync(current);
                await tx.RetireDocAsync(current);
            }, Context);
            var writes = DocumentWrites(storage.LastCommit);
            Check(writes is [DocumentChangeWrite { Content: DocumentBase { Version: 2 } migrated }, DocumentRetireWrite] &&
                JsonValue.DeepEquals(migrated.Value, O("{\"count\":1}")), "base then retirement");
        });

        yield return Case(prefix + "leaves-unaccessed-older-documents-and-unavailable-definitions-untouched", async () =>
        {
            var firstV1 = Documents.DefineSessionDoc("migration.lazy.first", 1, () => O("{\"count\":1}"));
            var secondV1 = Documents.DefineSessionDoc("migration.lazy.second", 1, () => O("{\"count\":2}"));
            var secondMigrations = 0;
            var firstV2 = Documents.DefineSessionDoc("migration.lazy.first", 2, () => O("{\"count\":0}"), migrate: (value, _) => Obj(("count", value["count"])));
            _ = Documents.DefineSessionDoc("migration.lazy.second", 2, () => O("{\"count\":0}"), migrate: (value, _) =>
            {
                secondMigrations++;
                return Obj(("count", value["count"]));
            });
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(async tx =>
            {
                await tx.DocAsync(firstV1);
                await tx.DocAsync(secondV1);
            }, Context);
            await session.UnloadDocumentsAsync();
            var commits = storage.CommitCount;
            SameJson("{\"count\":1}", await session.SnapshotAsync(firstV2, Context), "first");
            Equal(commits, storage.CommitCount, "commits");
            Equal(0, secondMigrations, "second migrations");
            var secondRecord = await storage.FindDocumentAsync(new DocumentAddress("migration.lazy.second", DocumentScope.Session), DocumentPoint.Current, Context);
            Equal(1, (await storage.GetDocumentAsync(secondRecord!.Id, DocumentPoint.Current, Context))!.Version, "stored version");
        });

        // ─── Session historical document snapshots ───────────────────────────

        yield return Case(prefix + "migrates-current-and-historical-rewindable-values-independently-and-follows-fork-ancestry", async () =>
        {
            var v1 = Documents.DefineRewindableConversationDoc("history.migration", 1, DocumentFork.AsOf, () => O("{\"count\":0}"));
            var family = Documents.DefineRewindableConversationDocFamily("history.family", 1, DocumentFork.AsOf, seed => Obj(("seed", seed), ("count", 0)));
            var migrations = new List<int>();
            var v3 = Documents.DefineRewindableConversationDoc("history.migration", 3, DocumentFork.AsOf, () => O("{\"count\":0,\"version\":3}"),
                migrate: (value, fromVersion) =>
                {
                    lock (migrations) migrations.Add(fromVersion);
                    return Obj(("count", value["count"]), ("version", 3));
                });
            var (storage, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            EntryId firstEntry = default, secondEntry = default, thirdEntry = default;
            await session.CommitAsync(async tx =>
            {
                firstEntry = (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "first" })).Id;
                (await tx.DocAsync(v1, conversationId))["count"] = 1;
                (await tx.DocAsync(family, conversationId, "member", "seed"))["count"] = 1;
            }, Context);
            await session.CommitAsync(async tx =>
            {
                secondEntry = (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "second" })).Id;
                (await tx.DocAsync(v1, conversationId))["count"] = 2;
            }, Context);
            await session.UnloadDocumentsAsync();
            var commits = storage.CommitCount;
            SameJson("{\"count\":2,\"version\":3}", await session.SnapshotAsync(v3, conversationId, Context), "current");
            lock (migrations) Seq([1], migrations, "migrations");
            Equal(commits, storage.CommitCount, "commits");

            await session.CommitAsync(async tx =>
            {
                thirdEntry = (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "third" })).Id;
                await tx.DocAsync(v3, conversationId);
            }, Context);
            Check(storage.LastCommit.Any(write => write is DocumentChangeWrite { Content: DocumentBase { Version: 3 } }), "version base");

            SameJson("{\"count\":1,\"version\":3}", await session.SnapshotAsOfAsync(v3, conversationId, firstEntry, Context), "as of first");
            SameJson("{\"count\":2,\"version\":3}", await session.SnapshotAsOfAsync(v3, conversationId, secondEntry, Context), "as of second");
            SameJson("{\"count\":2,\"version\":3}", await session.SnapshotAsOfAsync(v3, conversationId, thirdEntry, Context), "as of third");
            lock (migrations) Seq([1, 1, 1], migrations, "historical migrations");
            SameJson("{\"seed\":\"seed\",\"count\":1}", await session.SnapshotAsOfAsync(family, conversationId, "member", firstEntry, Context), "family");

            var childId = await session.CommitAsync(async tx => (await tx.ForkConversationAsync(conversationId, secondEntry, ConversationOwnership.Ownerless)).Id, Context);
            SameJson("{\"count\":1,\"version\":3}", await session.SnapshotAsOfAsync(v3, childId, firstEntry, Context), "child as of first");
            SameJson("{\"count\":2,\"version\":3}", await session.SnapshotAsOfAsync(v3, childId, secondEntry, Context), "child as of second");
            SameJson("{\"seed\":\"seed\",\"count\":1}", await session.SnapshotAsOfAsync(family, childId, "member", firstEntry, Context), "child family");
            await RejectsAsync(() => session.SnapshotAsOfAsync(v3, childId, thirdEntry, Context), $"Entry {thirdEntry} is not visible", "invisible entry");
        });

        yield return Case(prefix + "selects-the-incarnation-alive-at-the-entry-commit-across-retirement-and-recreation", async () =>
        {
            var doc = Documents.DefineRewindableConversationDoc("history.incarnation", 1, DocumentFork.AsOf, () => O("{\"value\":\"initial\"}"));
            var (_, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var beforeCreation = await session.CommitAsync(async tx => (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "before" })).Id, Context);
            EntryId createdAt = default, retiredAt = default, recreatedAt = default;
            await session.CommitAsync(async tx =>
            {
                createdAt = (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "create" })).Id;
                (await tx.DocAsync(doc, conversationId))["value"] = "old";
            }, Context);
            await session.CommitAsync(async tx =>
            {
                retiredAt = (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "retire" })).Id;
                await tx.RetireDocAsync(doc, conversationId);
            }, Context);
            await session.CommitAsync(async tx =>
            {
                recreatedAt = (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "recreate" })).Id;
                (await tx.DocAsync(doc, conversationId))["value"] = "new";
            }, Context);

            Check(await session.SnapshotAsOfAsync(doc, conversationId, beforeCreation, Context) is null, "before creation");
            SameJson("{\"value\":\"old\"}", await session.SnapshotAsOfAsync(doc, conversationId, createdAt, Context), "created");
            Check(await session.SnapshotAsOfAsync(doc, conversationId, retiredAt, Context) is null, "retired");
            SameJson("{\"value\":\"new\"}", await session.SnapshotAsOfAsync(doc, conversationId, recreatedAt, Context), "recreated");
            await session.CloseAsync(Context);
            await RejectsAsync(() => session.SnapshotAsOfAsync(doc, conversationId, recreatedAt, Context), "closed", "after close");
        });

        // ─── Session tracker cache across definition versions ─────────────────

        var v1Doc = Documents.DefineSessionDoc("cache.versioned", 1, () => O("{\"name\":\"first\"}"));
        var v2Doc = Documents.DefineSessionDoc("cache.versioned", 2, () => O("{\"names\":[]}"), migrate: (value, _) => Obj(("names", JsonValue.Array(value["name"]))));

        yield return Case(prefix + "migrates-a-document-cached-by-an-older-token-without-unloading", async () =>
        {
            var (storage, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(v1Doc), Context);
            SameJson("{\"name\":\"first\"}", await session.SnapshotAsync(v1Doc, Context), "v1");

            // Reloaded extension code accesses the still-cached document with a newer token.
            SameJson("{\"names\":[\"first\"]}", await session.SnapshotAsync(v2Doc, Context), "v2");
            await session.CommitAsync(async tx => (await tx.DocAsync(v2Doc)).GetArray("names").Add("second"), Context);
            var write = DocumentWrites(storage.LastCommit)[0];
            Check(write is DocumentChangeWrite { Content: DocumentBase { Version: 2 } written } &&
                JsonValue.DeepEquals(written.Value, O("{\"names\":[\"first\",\"second\"]}")), "version base");
            await RejectsAsync(() => session.SnapshotAsync(v1Doc, Context), "newer version 2", "older token");
        });

        yield return Case(prefix + "sends-observers-of-an-older-shape-a-root-replacement-after-a-newer-token-writes", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(v1Doc), Context);
            var state = (await session.DocumentStateAsync(v1Doc, Context))!;
            var watch = (await session.WatchDocAsync(v1Doc, Context))!;
            var frames = new List<string>();
            var delivered = new Deferred(inline: false);
            watch.Start((_, ops, _) =>
            {
                lock (frames) frames.Add(OpsJson(ops));
                delivered.Resolve();
                return Task.CompletedTask;
            });
            await session.CommitAsync(async tx => (await tx.DocAsync(v2Doc)).GetArray("names").Add("second"), Context);
            await delivered.Task;
            SameJson("{\"names\":[\"first\",\"second\"]}", state.Value, "state");
            SameJson("{\"names\":[\"first\",\"second\"]}", watch.Value, "watch");
            lock (frames) Seq(["[[\"r\",{\"names\":[\"first\",\"second\"]}]]"], frames, "frames");
            state.Dispose();
            await watch.StopAsync();
        });

        yield return Case(prefix + "serves-an-older-token-from-storage-after-a-newer-token-migrated-only-in-memory", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(v1Doc), Context);
            await session.UnloadDocumentsAsync();
            SameJson("{\"names\":[\"first\"]}", await session.SnapshotAsync(v2Doc, Context), "v2");
            SameJson("{\"name\":\"first\"}", await session.SnapshotAsync(v1Doc, Context), "v1");
            await session.CommitAsync(async tx => (await tx.DocAsync(v1Doc))["name"] = "renamed", Context);
            SameJson("{\"names\":[\"renamed\"]}", await session.SnapshotAsync(v2Doc, Context), "v2 after rename");
        });
    }
}
