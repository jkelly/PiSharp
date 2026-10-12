// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/session-forks.test.ts. Verbatim apart from the adaptations in
// SessionSupport.cs; the 260 concurrent family acquisitions start together and are awaited together, as upstream's Promise.all does.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static Program;

internal static partial class SessionTests
{
    private static IEnumerable<(string, Func<Task>)> ForkCases(string prefix, Func<IStorage> storageFactory)
    {
        static Task<ConversationRecord> Fork(Session session, ConversationId parent, EntryId at) =>
            session.CommitAsync(tx => tx.ForkConversationAsync(parent, at, ConversationOwnership.Ownerless), Context);
        static DocumentAddress Address(DocumentToken token, ConversationId conversationId) =>
            new(token.Definition.Kind, DocumentScope.Conversation(conversationId));

        yield return Case(prefix + "copies-as-of-and-current-singleton-and-family-bases-while-leaving-initial-documents-absent", async () =>
        {
            var asOf = Documents.DefineRewindableConversationDoc("fork.policies.as-of", 1, DocumentFork.AsOf, () => O("{\"value\":\"as-initial\"}"));
            var current = Documents.DefineConversationDoc("fork.policies.current", 1, DocumentFork.Current, () => O("{\"value\":\"current-initial\"}"));
            var initial = Documents.DefineConversationDoc("fork.policies.initial", 1, DocumentFork.Initial, () => O("{\"value\":\"fresh\"}"));
            var asOfFamily = Documents.DefineRewindableConversationDocFamily("fork.policies.as-of-family", 1, DocumentFork.AsOf, seed => Obj(("value", seed)));
            var currentFamily = Documents.DefineConversationDocFamily("fork.policies.current-family", 1, DocumentFork.Current, seed => Obj(("value", seed)));
            var (storage, session, publications) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId forkAt = default;
            await session.CommitAsync(async tx =>
            {
                forkAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "fork-point" })).Id;
                (await tx.DocAsync(asOf, parentId))["value"] = "as-at-fork";
                (await tx.DocAsync(current, parentId))["value"] = "current-at-fork";
                (await tx.DocAsync(initial, parentId))["value"] = "parent-only";
                (await tx.DocAsync(asOfFamily, parentId, "a", "unused"))["value"] = "family-as-a";
                (await tx.DocAsync(asOfFamily, parentId, "b", "unused"))["value"] = "family-as-b";
                (await tx.DocAsync(currentFamily, parentId, "a", "unused"))["value"] = "family-current-a";
            }, Context);
            await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(asOf, parentId))["value"] = "as-after-fork";
                (await tx.DocAsync(current, parentId))["value"] = "current-when-copied";
                (await tx.DocAsync(asOfFamily, parentId, "a", "unused"))["value"] = "family-as-after";
                (await tx.DocAsync(currentFamily, parentId, "a", "unused"))["value"] = "family-current-when-copied";
            }, Context);
            await Flush(session);
            var documentReads = storage.DocumentReadCount;

            var child = await Fork(session, parentId, forkAt);
            await Flush(session);
            Equal(documentReads, storage.DocumentReadCount, "a fork reads no document content");

            SameJson("{\"value\":\"as-at-fork\"}", await session.SnapshotAsync(asOf, child.Id, Context), "as-of");
            SameJson("{\"value\":\"current-when-copied\"}", await session.SnapshotAsync(current, child.Id, Context), "current");
            Check(await session.SnapshotAsync(initial, child.Id, Context) is null, "initial");
            SameJson("{\"value\":\"family-as-a\"}", await session.SnapshotAsync(asOfFamily, child.Id, "a", Context), "as-of family a");
            SameJson("{\"value\":\"family-as-b\"}", await session.SnapshotAsync(asOfFamily, child.Id, "b", Context), "as-of family b");
            SameJson("{\"value\":\"family-current-when-copied\"}", await session.SnapshotAsync(currentFamily, child.Id, "a", Context), "current family");

            var copies = storage.LastAdmitted.OfType<DocumentCopyWrite>().ToList();
            Equal(5, copies.Count, "copies");
            Check(copies.All(write => write.Record.Scope == DocumentScope.Conversation(child.Id)), "copies belong to the child");
            var publication = Last(publications);
            var copied = DocumentCopyChanges(publication);
            Equal(5, copied.Count, "copy publications");
            foreach (var change in copied)
            {
                Equal(publication.Seq, change.Record.CreatedAt, "createdAt");
                Equal(child.Id, change.ConversationId, "conversation");
                Equal(copies.Single(candidate => candidate.Record.Id == change.Record.Id).Source, change.Source, "source");
            }

            var parentRecord = await storage.FindDocumentAsync(Address(asOf, parentId), DocumentPoint.Current, Context);
            var childRecord = await storage.FindDocumentAsync(Address(asOf, child.Id), DocumentPoint.Current, Context);
            Check(childRecord!.Id != parentRecord!.Id, "a new incarnation");

            await session.CommitAsync(async tx => (await tx.DocAsync(asOf, child.Id))["value"] = "child-independent", Context);
            SameJson("{\"value\":\"as-after-fork\"}", await session.SnapshotAsync(asOf, parentId, Context), "parent unchanged");
            SameJson("{\"value\":\"child-independent\"}", await session.SnapshotAsync(asOf, child.Id, Context), "child changed");
            await session.CommitAsync(async tx => (await tx.DocAsync(initial, child.Id))["value"] = "child-created", Context);
            SameJson("{\"value\":\"child-created\"}", await session.SnapshotAsync(initial, child.Id, Context), "initial created");
        });

        yield return Case(prefix + "uses-final-document-state-from-the-fork-entry-commit-while-excluding-later-same-commit-entries", async () =>
        {
            var doc = Documents.DefineRewindableConversationDoc("fork.same-commit", 1, DocumentFork.AsOf, () => O("{\"value\":\"initial\"}"));
            var (_, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId forkAt = default, excluded = default;
            await session.CommitAsync(async tx =>
            {
                forkAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "included" })).Id;
                (await tx.DocAsync(doc, parentId))["value"] = "final-state-of-commit";
                excluded = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "excluded" })).Id;
            }, Context);
            var child = await Fork(session, parentId, forkAt);
            SameJson("{\"value\":\"final-state-of-commit\"}", await session.SnapshotAsync(doc, child.Id, Context), "copied");
            var visible = await session.CommitAsync(tx => tx.ScanEntriesAsync(new EntryQuery(child.Id), 10), Context);
            Check(visible.Items.Any(item => item.Id == forkAt), "fork entry visible");
            Check(visible.Items.All(item => item.Id != excluded), "later entry excluded");
        });

        yield return Case(prefix + "selects-the-entry-owning-ancestor-for-as-of-copies-and-the-immediate-parent-for-current-copies", async () =>
        {
            var asOf = Documents.DefineRewindableConversationDoc("fork.ancestry.as-of", 1, DocumentFork.AsOf, () => O("{\"value\":\"initial\"}"));
            var current = Documents.DefineConversationDoc("fork.ancestry.current", 1, DocumentFork.Current, () => O("{\"value\":\"initial\"}"));
            var (_, session, _) = Open(storageFactory);
            var rootId = await CreateConversation(session);
            EntryId inherited = default;
            await session.CommitAsync(async tx =>
            {
                inherited = (await tx.AppendEntryAsync(rootId, new EntryDraft { Kind = "root" })).Id;
                (await tx.DocAsync(asOf, rootId))["value"] = "root-at-entry";
                (await tx.DocAsync(current, rootId))["value"] = "root-current";
            }, Context);
            var parent = await Fork(session, rootId, inherited);
            EntryId parentEntry = default;
            await session.CommitAsync(async tx =>
            {
                parentEntry = (await tx.AppendEntryAsync(parent.Id, new EntryDraft { Kind = "parent" })).Id;
                (await tx.DocAsync(asOf, parent.Id))["value"] = "parent-at-own-entry";
                (await tx.DocAsync(current, parent.Id))["value"] = "parent-current";
            }, Context);

            var inheritedFork = await Fork(session, parent.Id, inherited);
            SameJson("{\"value\":\"root-at-entry\"}", await session.SnapshotAsync(asOf, inheritedFork.Id, Context), "inherited as-of");
            SameJson("{\"value\":\"parent-current\"}", await session.SnapshotAsync(current, inheritedFork.Id, Context), "inherited current");

            var ownEntryFork = await Fork(session, parent.Id, parentEntry);
            SameJson("{\"value\":\"parent-at-own-entry\"}", await session.SnapshotAsync(asOf, ownEntryFork.Id, Context), "own as-of");
            SameJson("{\"value\":\"parent-current\"}", await session.SnapshotAsync(current, ownEntryFork.Id, Context), "own current");
        });

        yield return Case(prefix + "copies-the-stored-value-and-version-without-consulting-migration-definitions-or-migrated-caches", async () =>
        {
            var v1 = Documents.DefineRewindableConversationDoc("fork.stored-version", 1, DocumentFork.AsOf, () => O("{\"count\":1}"));
            var migrations = 0;
            var v3 = Documents.DefineRewindableConversationDoc("fork.stored-version", 3, DocumentFork.AsOf, () => O("{\"count\":0,\"migrated\":false}"),
                migrate: (value, fromVersion) =>
                {
                    Equal(1, fromVersion, "fromVersion");
                    migrations++;
                    return Obj(("count", value["count"]), ("migrated", true));
                });
            var (storage, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId forkAt = default;
            await session.CommitAsync(async tx =>
            {
                forkAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "point" })).Id;
                await tx.DocAsync(v1, parentId);
            }, Context);
            await session.UnloadDocumentsAsync();
            SameJson("{\"count\":1,\"migrated\":true}", await session.SnapshotAsync(v3, parentId, Context), "migrated parent");
            Equal(1, migrations, "migrations");

            var child = await Fork(session, parentId, forkAt);
            Equal(1, migrations, "a fork does not migrate");
            var childRecord = await storage.FindDocumentAsync(Address(v1, child.Id), DocumentPoint.Current, Context);
            var childStored = (await storage.GetDocumentAsync(childRecord!.Id, DocumentPoint.Current, Context))!;
            Equal(1, childStored.Version, "stored version");
            SameJson("{\"count\":1}", childStored.Value, "stored value");
            SameJson("{\"count\":1,\"migrated\":true}", await session.SnapshotAsync(v3, child.Id, Context), "migrated child");
            Equal(2, migrations, "child migrates on access");
        });

        yield return Case(prefix + "coalesces-a-typed-migration-and-override-into-the-copied-creation-base", async () =>
        {
            var v1 = Documents.DefineRewindableConversationDoc("fork.override", 1, DocumentFork.AsOf, () => O("{\"count\":2}"));
            var checkpoints = 0;
            var v2 = Documents.DefineRewindableConversationDoc("fork.override", 2, DocumentFork.AsOf, () => O("{\"count\":0,\"migrated\":false}"),
                migrate: (value, _) => Obj(("count", value["count"]), ("migrated", true)),
                checkpointWhen: (_, _, _) => { checkpoints++; return false; });
            var (storage, session, publications) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId forkAt = default;
            await session.CommitAsync(async tx =>
            {
                forkAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "point" })).Id;
                await tx.DocAsync(v1, parentId);
            }, Context);

            var documentReads = storage.DocumentReadCount;
            var child = await session.CommitAsync(async tx =>
            {
                var created = await tx.ForkConversationAsync(parentId, forkAt, ConversationOwnership.Ownerless);
                (await tx.DocAsync(v2, created.Id))["count"] = 9;
                return created;
            }, Context);
            await Flush(session);
            Equal(documentReads + 1, storage.DocumentReadCount, "one source read");
            var creates = storage.LastAdmitted.OfType<DocumentCreateWrite>().ToList();
            Equal(1, creates.Count, "creates");
            Equal(2, creates[0].Content.Version, "version");
            SameJson("{\"count\":9,\"migrated\":true}", creates[0].Content.Value, "base");
            Equal(0, checkpoints, "no predicate call");
            Check(DocumentChanges(Last(publications)) is [{ Version: 2 }], "published creation");
            SameJson("{\"count\":9,\"migrated\":true}", await session.SnapshotAsync(v2, child.Id, Context), "child");
            var parentRecord = await storage.FindDocumentAsync(Address(v1, parentId), DocumentPoint.Current, Context);
            Equal(1, (await storage.GetDocumentAsync(parentRecord!.Id, DocumentPoint.Current, Context))!.Version, "parent version");
        });

        yield return Case(prefix + "copies-the-incarnation-alive-at-the-fork-point-across-retirement-and-recreation", async () =>
        {
            var doc = Documents.DefineRewindableConversationDoc("fork.incarnations", 1, DocumentFork.AsOf, () => O("{\"value\":\"initial\"}"));
            var (_, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId oldAt = default, retiredAt = default, newAt = default;
            await session.CommitAsync(async tx =>
            {
                oldAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "old" })).Id;
                (await tx.DocAsync(doc, parentId))["value"] = "old";
            }, Context);
            await session.CommitAsync(async tx =>
            {
                retiredAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "retired" })).Id;
                await tx.RetireDocAsync(doc, parentId);
            }, Context);
            await session.CommitAsync(async tx =>
            {
                newAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "new" })).Id;
                (await tx.DocAsync(doc, parentId))["value"] = "new";
            }, Context);

            var oldChild = await Fork(session, parentId, oldAt);
            var emptyChild = await Fork(session, parentId, retiredAt);
            var newChild = await Fork(session, parentId, newAt);
            SameJson("{\"value\":\"old\"}", await session.SnapshotAsync(doc, oldChild.Id, Context), "old");
            Check(await session.SnapshotAsync(doc, emptyChild.Id, Context) is null, "retired");
            SameJson("{\"value\":\"new\"}", await session.SnapshotAsync(doc, newChild.Id, Context), "new");
        });

        yield return Case(prefix + "rejects-invisible-fork-points-before-admission-and-remains-usable", async () =>
        {
            var (storage, session, publications) = Open(storageFactory);
            var rootId = await CreateConversation(session);
            EntryId visible = default, hidden = default;
            await session.CommitAsync(async tx =>
            {
                visible = (await tx.AppendEntryAsync(rootId, new EntryDraft { Kind = "visible" })).Id;
                hidden = (await tx.AppendEntryAsync(rootId, new EntryDraft { Kind = "hidden" })).Id;
            }, Context);
            var parent = await Fork(session, rootId, visible);
            await Flush(session);
            var commits = storage.CommitCount;
            var published = Count(publications);
            await RejectsAsync(() => Fork(session, parent.Id, hidden), $"Entry {hidden} is not visible", "invisible fork point");
            await Flush(session);
            Equal(commits, storage.CommitCount, "commits");
            Equal(published, Count(publications), "publications");
            var independent = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);
            Equal(independent, await storage.GetConversationAsync(independent.Id, Context), "usable");
        });

        yield return Case(prefix + "rejects-duplicate-as-of-and-current-selections-for-one-child-address-before-admission", async () =>
        {
            var asOf = Documents.DefineRewindableConversationDoc("fork.duplicate-policy", 1, DocumentFork.AsOf, () => O("{\"value\":\"as-of\"}"));
            var current = Documents.DefineConversationDoc("fork.duplicate-policy", 1, DocumentFork.Current, () => O("{\"value\":\"current\"}"));
            var (storage, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId oldAt = default;
            await session.CommitAsync(async tx =>
            {
                oldAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "old" })).Id;
                await tx.DocAsync(asOf, parentId);
            }, Context);
            await session.CommitAsync(tx => tx.RetireDocAsync(asOf, parentId), Context);
            await session.CommitAsync(tx => tx.DocAsync(current, parentId), Context);
            var commits = storage.CommitCount;
            await RejectsAsync(() => Fork(session, parentId, oldAt), "Fork selects multiple source documents", "duplicate selection");
            Equal(commits, storage.CommitCount, "commits");
            var independent = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);
            Equal(independent, await storage.GetConversationAsync(independent.Id, Context), "usable");
        });

        yield return Case(prefix + "rejects-current-and-as-of-source-writes-in-the-fork-transaction", async () =>
        {
            var current = Documents.DefineConversationDoc("fork.same-transaction-current", 1, DocumentFork.Current, () => O("{\"value\":\"committed\"}"));
            var asOf = Documents.DefineRewindableConversationDoc("fork.same-transaction-as-of", 1, DocumentFork.AsOf, () => O("{\"value\":\"committed\"}"));
            var (storage, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId forkAt = default;
            await session.CommitAsync(async tx =>
            {
                forkAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "point" })).Id;
                await tx.DocAsync(current, parentId);
                await tx.DocAsync(asOf, parentId);
            }, Context);
            var commits = storage.CommitCount;
            await RejectsAsync(() => session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(current, parentId))["value"] = "before-fork";
                await tx.ForkConversationAsync(parentId, forkAt, ConversationOwnership.Ownerless);
            }, Context), "Cannot change fork source document", "current before fork");
            await RejectsAsync(() => session.CommitAsync(async tx =>
            {
                await tx.ForkConversationAsync(parentId, forkAt, ConversationOwnership.Ownerless);
                (await tx.DocAsync(current, parentId))["value"] = "after-fork";
            }, Context), "Cannot change fork source document", "current after fork");
            await RejectsAsync(() => session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(asOf, parentId))["value"] = "as-of-write";
                await tx.ForkConversationAsync(parentId, forkAt, ConversationOwnership.Ownerless);
            }, Context), "Cannot change fork source document", "as-of write");
            Equal(commits, storage.CommitCount, "commits");
            SameJson("{\"value\":\"committed\"}", await session.SnapshotAsync(current, parentId, Context), "parent");
            var child = await Fork(session, parentId, forkAt);
            SameJson("{\"value\":\"committed\"}", await session.SnapshotAsync(current, child.Id, Context), "child");
        });

        yield return Case(prefix + "rolls-every-copied-base-back-when-later-pre-admission-assembly-fails", async () =>
        {
            var copied = Documents.DefineRewindableConversationDoc("fork.rollback.copied", 1, DocumentFork.AsOf, () => O("{\"value\":\"copied\"}"));
            var failCheckpoint = true;
            var failure = Documents.DefineSessionDoc("fork.rollback.failure", 1, () => O("{\"count\":0}"),
                checkpointWhen: (_, _, _) => failCheckpoint ? throw new InvalidOperationException("checkpoint failed") : false);
            var (storage, session, publications) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId forkAt = default;
            await session.CommitAsync(async tx =>
            {
                forkAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "point" })).Id;
                await tx.DocAsync(copied, parentId);
                await tx.DocAsync(failure);
            }, Context);
            await Flush(session);
            var commits = storage.CommitCount;
            var published = Count(publications);
            ConversationId childId = default;
            await RejectsAsync(() => session.CommitAsync(async tx =>
            {
                childId = (await tx.ForkConversationAsync(parentId, forkAt, ConversationOwnership.Ownerless)).Id;
                (await tx.DocAsync(failure))["count"] = 1;
            }, Context), "checkpoint failed", "assembly");
            await Flush(session);
            Equal(commits, storage.CommitCount, "commits");
            Equal(published, Count(publications), "publications");
            Check(await storage.GetConversationAsync(childId, Context) is null, "child not committed");
            failCheckpoint = false;
            await session.CommitAsync(async tx => (await tx.DocAsync(failure))["count"] = 2, Context);
            SameJson("{\"count\":2}", await session.SnapshotAsync(failure, Context), "failure doc");
            SameJson("{\"value\":\"copied\"}", await session.SnapshotAsync(copied, parentId, Context), "parent copy source");
        });

        yield return Case(prefix + "rolls-back-a-guaranteed-storage-rejection-without-poisoning-the-session", async () =>
        {
            var doc = Documents.DefineRewindableConversationDoc("fork.storage-rejected", 1, DocumentFork.AsOf, () => O("{\"value\":\"source\"}"));
            var (storage, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId forkAt = default;
            await session.CommitAsync(async tx =>
            {
                forkAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "point" })).Id;
                await tx.DocAsync(doc, parentId);
            }, Context);
            ConversationId rejectedChildId = default;
            storage.FailNextCommit(new StorageRejectedException("copy rejected"));
            await RejectsAsync(() => session.CommitAsync(async tx =>
            {
                rejectedChildId = (await tx.ForkConversationAsync(parentId, forkAt, ConversationOwnership.Ownerless)).Id;
            }, Context), "copy rejected", "rejection");
            Check(await storage.GetConversationAsync(rejectedChildId, Context) is null, "nothing committed");
            var next = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);
            Equal(next, await storage.GetConversationAsync(next.Id, Context), "not poisoned");
        });

        yield return Case(prefix + "retires-a-copied-document-and-can-recreate-the-address-in-the-fork-transaction", async () =>
        {
            var doc = Documents.DefineRewindableConversationDoc("fork.retire-recreate", 1, DocumentFork.AsOf, () => O("{\"value\":\"fresh\"}"));
            var (storage, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId forkAt = default;
            await session.CommitAsync(async tx =>
            {
                forkAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "point" })).Id;
                (await tx.DocAsync(doc, parentId))["value"] = "copied";
            }, Context);
            var child = await session.CommitAsync(async tx =>
            {
                var created = await tx.ForkConversationAsync(parentId, forkAt, ConversationOwnership.Ownerless);
                await tx.RetireDocAsync(doc, created.Id);
                (await tx.DocAsync(doc, created.Id))["value"] = "replacement";
                return created;
            }, Context);
            var writes = storage.LastCommit;
            Equal(1, writes.OfType<DocumentCopyWrite>().Count(), "copies");
            Equal(1, writes.OfType<DocumentCreateWrite>().Count(), "creates");
            Equal(1, writes.OfType<DocumentRetireWrite>().Count(), "retirements");
            SameJson("{\"value\":\"replacement\"}", await session.SnapshotAsync(doc, child.Id, Context), "replacement");
        });

        yield return Case(prefix + "copies-only-conversation-documents-leaving-session-and-task-documents-in-their-original-scopes", async () =>
        {
            var copied = Documents.DefineConversationDoc("fork.scope.conversation", 1, DocumentFork.Current, () => O("{\"value\":\"conversation\"}"));
            var sessionOnly = Documents.DefineSessionDoc("fork.scope.session", 1, () => O("{\"value\":\"session\"}"));
            var taskOnly = Documents.DefineTaskDoc("fork.scope.task", 1, () => O("{\"value\":\"task\"}"));
            var work = Tasks.DefineTask(new TaskDefinition { Name = "fork.scope.work", Version = 1, Initial = _ => O("{\"phase\":\"start\"}") });
            var (storage, session, publications) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            EntryId forkAt = default;
            TaskId taskId = default;
            await session.CommitAsync(async tx =>
            {
                forkAt = (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "point" })).Id;
                taskId = await tx.CreateTaskAsync(work, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation) { ConversationId = parentId });
                await tx.DocAsync(copied, parentId);
                await tx.DocAsync(sessionOnly);
                await tx.DocAsync(taskOnly, taskId);
            }, Context);
            var child = await Fork(session, parentId, forkAt);
            await Flush(session);
            Seq([copied.Definition.Kind], storage.LastCommit.OfType<DocumentCopyWrite>().Select(write => write.Record.Kind), "copies");
            Seq([copied.Definition.Kind], DocumentCopyChanges(Last(publications)).Select(change => change.Record.Kind), "copy publications");
            SameJson("{\"value\":\"conversation\"}", await session.SnapshotAsync(copied, child.Id, Context), "copied");
            SameJson("{\"value\":\"session\"}", await session.SnapshotAsync(sessionOnly, Context), "session");
            SameJson("{\"value\":\"task\"}", await session.SnapshotAsync(taskOnly, taskId, Context), "task");
        });

        yield return Case(prefix + "copies-every-family-member-across-storage-scan-pages", async () =>
        {
            var family = Documents.DefineConversationDocFamily("fork.pagination", 1, DocumentFork.Current, seed => Obj(("value", seed)));
            var (storage, session, _) = Open(storageFactory);
            var parentId = await CreateConversation(session);
            var forkAt = await session.CommitAsync(async tx => (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "point" })).Id, Context);
            await session.CommitAsync(tx => Task.WhenAll(Enumerable.Range(0, 260).Select(index => tx.DocAsync(family, parentId, $"member-{index}", index))), Context);
            var child = await Fork(session, parentId, forkAt);
            Equal(260, storage.LastCommit.OfType<DocumentCopyWrite>().Count(), "copies");
            SameJson("{\"value\":0}", await session.SnapshotAsync(family, child.Id, "member-0", Context), "first");
            SameJson("{\"value\":259}", await session.SnapshotAsync(family, child.Id, "member-259", Context), "last");
        });
    }
}
