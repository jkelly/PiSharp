using System.Text;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Testing;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/jsonl-storage.test.ts. Cases keep upstream's names (as
// storage.jsonl.<slug>, and storage.jsonl.batches.<slug> for the "JsonlStorage batches" group), inputs and expected values. The storage conformance kit runs on JsonlStorage directly
// (storage.jsonl.conformance.*) and through a storage that closes and reopens the directory after every commit
// (storage.jsonl.reopen.conformance.*). Adapted: "serializes the complete candidate before I/O" injects a preparation failure (a write that
// reuses the root conversation's ID), because a BigInt that JSON.stringify rejects cannot occur in a JsonValue; upstream's for-loops over
// failure points become one case per point, named after it. Authored: storage.jsonl.writes-upstream-line-formats pins the exact bytes.
internal static partial class Program
{
    private static readonly DurableContext JsonlContext = DurableContext.Background;

    private static IEnumerable<(string, Func<Task>)> StorageJsonlCases()
    {
        foreach (var testCase in StorageConformance.Create(async use =>
        {
            var directory = Temp("jsonl");
            var storage = await JsonlStorage.OpenAsync(directory, new LocalFileSystem(directory), JsonlContext);
            try { await use(storage); }
            finally { await storage.CloseAsync(JsonlContext); DeleteDirectory(directory); }
        }))
            yield return Case("storage.jsonl.conformance." + testCase.Slug, testCase.Run);

        foreach (var testCase in StorageConformance.Create(async use =>
        {
            var directory = Temp("jsonl-conformance");
            var storage = new ReopeningStorage(await JsonlStorage.OpenAsync(directory, new LocalFileSystem(directory), JsonlContext), directory);
            try { await use(storage); }
            finally { await storage.CloseAsync(JsonlContext); DeleteDirectory(directory); }
        }))
            yield return Case("storage.jsonl.reopen.conformance." + testCase.Slug, testCase.Run);

        yield return JsonlCase("opens-through-the-local-adapter", async fixture =>
        {
            var storage = await fixture.Track(JsonlStorage.OpenAsync(fixture.NewDirectory(), JsonlContext));
            await CreateRoot(storage);
            Equal<ConversationRecord?>(new ConversationRecord(ConversationId.Root), await storage.GetConversationAsync(ConversationId.Root, JsonlContext), "root");
        });

        yield return JsonlCase("publishes-every-commit-before-reclaiming-current-only-document-and-terminal-task-sidecars", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var taskId = await storage.MintIdAsync<TaskId>();
            var documentId = await storage.MintIdAsync<DocumentId>();
            await Commit(storage, new TaskWrite(PendingTask(taskId)));
            await Commit(storage, new DocumentCreateWrite(SessionDocument(documentId), DocumentContent.Base(1, JO("{\"count\":0}"))));
            await Commit(storage, new DocumentChangeWrite(documentId, DocumentContent.Delta(1, [])));
            await Commit(storage, new DocumentChangeWrite(documentId, DocumentContent.Base(1, JO("{\"count\":1}"))));
            Equal(1, ReadJsonlLines(Path.Combine(directory, $"task-{taskId}.jsonl")).Length, "task sidecar lines");
            Equal(1, ReadJsonlLines(Path.Combine(directory, $"doc-{documentId}.jsonl")).Length, "reclaimed document sidecar lines");

            await Commit(storage, new DocumentRetireWrite(documentId));
            await Commit(storage, new TaskWrite(TerminalTask(taskId)));

            Equal(7, ReadJsonlLines(Path.Combine(directory, "main.jsonl")).Length, "main lines");
            Check(!PathExists(Path.Combine(directory, $"task-{taskId}.jsonl")), "the terminal task's sidecar is removed");
            Check(!PathExists(Path.Combine(directory, $"doc-{documentId}.jsonl")), "the retired document's sidecar is removed");
            var markerTypes = ReadJsonlLines(Path.Combine(directory, "main.jsonl")).Select(line => ((JsonStringValue)((JsonObjectValue)J(line))["type"]).Value);
            Equal("commit,commit,commit,commit,commit,commit,commit", string.Join(",", markerTypes), "marker types");
        });

        yield return JsonlCase("orders-multiple-live-task-replacements-in-one-sidecar-by-commit-ordinal", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var taskId = await storage.MintIdAsync<TaskId>();
            await Commit(storage, new TaskWrite(PendingTask(taskId, "first")), new TaskWrite(PendingTask(taskId, "second")));
            Equal(2, ReadJsonlLines(Path.Combine(directory, $"task-{taskId}.jsonl")).Length, "task sidecar lines");

            var reopened = await fixture.Open(directory);
            Equal<TaskRecord?>(PendingTask(taskId, "second"), await reopened.GetTaskAsync(taskId, JsonlContext), "replayed task");
        });

        yield return JsonlCase("removes-a-complete-plus-torn-unconfirmed-multi-record-sidecar-append", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var env = new InstrumentedFileSystem(directory);
            var storage = await fixture.Open(directory, env);
            await CreateRoot(storage);
            var taskId = await storage.MintIdAsync<TaskId>();
            env.Fail(new(FailureOperation.Append, 1, FailureMode.Short));
            await RejectsPoisoned(() => Commit(storage, new TaskWrite(PendingTask(taskId, "first")),
                new TaskWrite(PendingTask(taskId, "second-" + new string('x', 512)))), "torn commit");
            var partial = File.ReadAllBytes(Path.Combine(directory, $"task-{taskId}.jsonl"));
            Check(partial[^1] != (byte)'\n', "the sidecar ends in a torn line");
            Equal(1, partial.Count(value => value == (byte)'\n'), "complete sidecar lines");

            var reopened = await fixture.Open(directory);
            Check(await reopened.GetTaskAsync(taskId, JsonlContext) is null, "the unconfirmed task is not recovered");
            Equal(0L, new FileInfo(Path.Combine(directory, $"task-{taskId}.jsonl")).Length, "truncated sidecar size");
        });

        yield return JsonlFixtureCase("storage.jsonl.poisons-the-storage-when-an-append-fails-after-its-batches-applied-before-a-waiting-read-goes-on-review", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fs = new FailingJoin(directory, gate.Task);
            var storage = await fixture.Open(directory, fs);
            fs.Fail = true;
            var conversationId = new ConversationId(100);
            var committing = storage.CommitAsync(
            [
                [new ConversationWrite(new(conversationId))],
                [new DocumentCreateWrite(new DocumentCreate { Id = new DocumentId(101), Kind = "state", Scope = DocumentScope.Session }, DocumentContent.Base(1, JO("{}")))],
            ], (_, _) => { }, JsonlContext);
            // Started while the batches are in memory and the append waits.
            var read = storage.GetConversationAsync(conversationId, JsonlContext);
            gate.SetResult();
            await RejectsPoisoned(() => committing, "the commit whose append fails");
            await RejectsPoisoned(() => read, "the read that waited on the append");
            await storage.CloseAsync(JsonlContext);
            var reopened = await fixture.Open(directory);
            Check(await reopened.GetConversationAsync(conversationId, JsonlContext) is null, "the batches of the failed append are not stored");
        });

        yield return JsonlCase("serializes-the-complete-candidate-before-io-and-leaves-preparation-failures-usable", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var env = new InstrumentedFileSystem(directory);
            var storage = await fixture.Open(directory, env);
            await CreateRoot(storage);
            env.Clear();
            var id = await storage.MintIdAsync<EntryId>();
            // Upstream commits an entry whose data is a BigInt, which JSON.stringify rejects; a JsonValue cannot hold one, so a write that
            // MemoryStorage.PrepareCommit rejects (an entry reusing the root conversation's ID) fails the candidate instead.
            await ThrowsAsync<Exception>(() => Commit(storage,
                new EntryWrite(new() { Id = id, ConversationId = ConversationId.Root, Kind = "bad" }),
                new EntryWrite(new() { Id = new EntryId(ConversationId.Root.Value), ConversationId = ConversationId.Root, Kind = "bad" })), "rejected candidate");
            Equal("", string.Join(",", env.Operations), "file operations of a rejected candidate");
            Check(await storage.GetEntryAsync(id, JsonlContext) is null, "nothing of the rejected candidate is published");
            Equal(Ids.SeqFromNumber(2), await Commit(storage, new EntryWrite(new() { Id = id, ConversationId = ConversationId.Root, Kind = "good" })), "next commit");
        });

        foreach (var failure in new Failure[]
        {
            new(FailureOperation.Append, 1, FailureMode.Before), new(FailureOperation.Append, 1, FailureMode.After),
            new(FailureOperation.Append, 1, FailureMode.Short), new(FailureOperation.Append, 2, FailureMode.Before),
            new(FailureOperation.Append, 2, FailureMode.After), new(FailureOperation.Append, 3, FailureMode.Before),
            new(FailureOperation.Append, 3, FailureMode.Short), new(FailureOperation.Append, 3, FailureMode.After),
        })
        {
            yield return JsonlCase($"poisons-after-{Mode(failure)}-failure-at-append-{failure.Call}-and-recovers-only-confirmed-state", async fixture =>
            {
                var directory = fixture.NewDirectory();
                var env = new InstrumentedFileSystem(directory);
                var storage = await fixture.Open(directory, env);
                await CreateRoot(storage);
                var firstId = await storage.MintIdAsync<DocumentId>();
                var secondId = await storage.MintIdAsync<DocumentId>();
                env.Fail(failure);
                await RejectsPoisoned(() => Commit(storage,
                    new DocumentCreateWrite(SessionDocument(firstId, "first"), DocumentContent.Base(1, JO("{\"text\":\"α\"}"))),
                    new DocumentCreateWrite(SessionDocument(secondId, "second"), DocumentContent.Base(1, JO("{\"text\":\"β\"}")))), "failed commit");
                await RejectsPoisoned(() => storage.GetDocumentAsync(firstId, DocumentPoint.Current, JsonlContext), "read after poisoning");

                var reopened = await fixture.Open(directory);
                var markerSurvived = failure is { Call: 3, Mode: FailureMode.After };
                var first = await reopened.GetDocumentAsync(firstId, DocumentPoint.Current, JsonlContext);
                var second = await reopened.GetDocumentAsync(secondId, DocumentPoint.Current, JsonlContext);
                if (markerSurvived)
                {
                    Same("{\"text\":\"α\"}", first?.Value ?? throw new InvalidOperationException("first document missing"), "first document");
                    Same("{\"text\":\"β\"}", second?.Value ?? throw new InvalidOperationException("second document missing"), "second document");
                }
                else Check(first is null && second is null, "no document of the unconfirmed commit is recovered");
            });
        }

        foreach (var failure in new Failure[]
        {
            new(FailureOperation.Flush, 1, FailureMode.Before), new(FailureOperation.Flush, 1, FailureMode.After),
            new(FailureOperation.Flush, 2, FailureMode.Before), new(FailureOperation.Flush, 2, FailureMode.After),
        })
        {
            yield return JsonlCase($"poisons-after-{Mode(failure)}-failure-at-flush-{failure.Call}-and-never-writes-a-marker", async fixture =>
            {
                var directory = fixture.NewDirectory();
                var env = new InstrumentedFileSystem(directory);
                var storage = await fixture.Open(directory, env, fsync: true);
                await CreateRoot(storage);
                var firstId = await storage.MintIdAsync<DocumentId>();
                var secondId = await storage.MintIdAsync<DocumentId>();
                env.Fail(failure);
                await RejectsPoisoned(() => Commit(storage,
                    new DocumentCreateWrite(SessionDocument(firstId, "flush.first"), DocumentContent.Base(1, JsonObjectValue.Empty)),
                    new DocumentCreateWrite(SessionDocument(secondId, "flush.second"), DocumentContent.Base(1, JsonObjectValue.Empty))), "failed commit");
                var reopened = await fixture.Open(directory);
                Check(await reopened.GetDocumentAsync(firstId, DocumentPoint.Current, JsonlContext) is null, "first document");
                Check(await reopened.GetDocumentAsync(secondId, DocumentPoint.Current, JsonlContext) is null, "second document");
            });
        }

        yield return JsonlCase("orders-publication-flushes-exactly-and-flushes-main-only-to-authorize-reclamation", async fixture =>
        {
            foreach (var fsync in new[] { false, true })
            {
                var directory = fixture.NewDirectory();
                var env = new InstrumentedFileSystem(directory);
                var storage = await fixture.Open(directory, env, fsync);
                await CreateRoot(storage);
                var firstId = await storage.MintIdAsync<DocumentId>();
                var secondId = await storage.MintIdAsync<DocumentId>();
                env.Clear();
                await Commit(storage,
                    new DocumentCreateWrite(SessionDocument(secondId, "second"), DocumentContent.Base(1, JsonObjectValue.Empty)),
                    new DocumentCreateWrite(SessionDocument(firstId, "first"), DocumentContent.Base(1, JsonObjectValue.Empty)));
                SameOperations(env, [$"append:doc-{secondId}.jsonl", $"append:doc-{firstId}.jsonl",
                    .. fsync ? new[] { $"flush:doc-{secondId}.jsonl", $"flush:doc-{firstId}.jsonl" } : [], "append:main.jsonl"], $"creation (fsync {fsync})");

                env.Clear();
                await Commit(storage, new DocumentChangeWrite(firstId, DocumentContent.Base(1, JO("{\"checkpoint\":true}"))));
                SameOperations(env, [$"append:doc-{firstId}.jsonl", .. fsync ? new[] { $"flush:doc-{firstId}.jsonl" } : [], "append:main.jsonl",
                    .. fsync ? new[] { "flush:main.jsonl" } : [], $"write:doc-{firstId}.jsonl.reclaim",
                    .. fsync ? new[] { $"flush:doc-{firstId}.jsonl.reclaim" } : [], $"rename:doc-{firstId}.jsonl.reclaim->doc-{firstId}.jsonl"],
                    $"base checkpoint (fsync {fsync})");

                env.Clear();
                await Commit(storage, new EntryWrite(new() { Id = await storage.MintIdAsync<EntryId>(), ConversationId = ConversationId.Root, Kind = "main-only" }));
                SameOperations(env, ["append:main.jsonl"], $"main-only commit (fsync {fsync})");

                var taskId = await storage.MintIdAsync<TaskId>();
                await Commit(storage, new TaskWrite(PendingTask(taskId)));
                env.Clear();
                await Commit(storage, new TaskWrite(TerminalTask(taskId)));
                SameOperations(env, ["append:main.jsonl", .. fsync ? new[] { "flush:main.jsonl" } : [], $"remove:task-{taskId}.jsonl"],
                    $"terminal task (fsync {fsync})");
            }
        });

        foreach (var failure in new Failure[]
        {
            new(FailureOperation.Write, 1, FailureMode.Before), new(FailureOperation.Write, 1, FailureMode.Short),
            new(FailureOperation.Write, 1, FailureMode.After), new(FailureOperation.Rename, 1, FailureMode.Before),
            new(FailureOperation.Rename, 1, FailureMode.After),
        })
        {
            yield return JsonlCase($"recovers-a-committed-base-across-reclaim-{failure.Operation.ToString().ToLowerInvariant()}-{Mode(failure)}", async fixture =>
            {
                var directory = fixture.NewDirectory();
                var env = new InstrumentedFileSystem(directory);
                var storage = await fixture.Open(directory, env);
                await CreateRoot(storage);
                var id = await storage.MintIdAsync<DocumentId>();
                await Commit(storage, new DocumentCreateWrite(SessionDocument(id), DocumentContent.Base(1, JO("{\"count\":0}"))));
                await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"s\",[\"count\"],1]]"))));

                env.Fail(failure);
                Equal(Ids.SeqFromNumber(4), await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Base(1, JO("{\"count\":2}")))), "committed despite reclamation failure");
                Same("{\"count\":2}", (await storage.GetDocumentAsync(id, DocumentPoint.Current, JsonlContext))!.Value, "current value");
                await storage.CloseAsync(JsonlContext);

                var reopened = await fixture.Open(directory);
                Same("{\"count\":2}", (await reopened.GetDocumentAsync(id, DocumentPoint.Current, JsonlContext))!.Value, "recovered value");
                Equal(1, ReadJsonlLines(Path.Combine(directory, $"doc-{id}.jsonl")).Length, "reclaimed sidecar lines");
                Equal("", ReclaimFiles(directory), "temporary replacements");
            });
        }

        foreach (var mode in new[] { FailureMode.Before, FailureMode.After })
        {
            yield return JsonlCase($"recovers-document-retirement-reclamation-across-remove-{mode.ToString().ToLowerInvariant()}", async fixture =>
            {
                var directory = fixture.NewDirectory();
                var env = new InstrumentedFileSystem(directory);
                var storage = await fixture.Open(directory, env);
                await CreateRoot(storage);
                var taskId = await storage.MintIdAsync<TaskId>();
                var id = await storage.MintIdAsync<DocumentId>();
                await Commit(storage, new TaskWrite(PendingTask(taskId)),
                    new DocumentCreateWrite(new() { Id = id, Kind = "task.document", Scope = DocumentScope.Task(taskId) }, DocumentContent.Base(1, JO("{\"count\":1}"))));
                env.Fail(new(FailureOperation.Remove, 1, mode));
                Equal(Ids.SeqFromNumber(3), await Commit(storage, new DocumentRetireWrite(id)), "retirement");
                Check(await storage.GetDocumentAsync(id, DocumentPoint.Current, JsonlContext) is null, "retired document");
                await storage.CloseAsync(JsonlContext);

                var reopened = await fixture.Open(directory);
                Check(await reopened.GetDocumentAsync(id, DocumentPoint.Current, JsonlContext) is null, "recovered retired document");
                Equal<TaskRecord?>(PendingTask(taskId), await reopened.GetTaskAsync(taskId, JsonlContext), "recovered task");
                Check(!PathExists(Path.Combine(directory, $"doc-{id}.jsonl")), "the retired document's sidecar is removed on reopen");
                Equal("", ReclaimFiles(directory), "temporary replacements");
            });
        }

        foreach (var mode in new[] { FailureMode.Before, FailureMode.After })
        {
            yield return JsonlCase($"defers-reclamation-after-authorizing-main-flush-{mode.ToString().ToLowerInvariant()}-failure", async fixture =>
            {
                var directory = fixture.NewDirectory();
                var env = new InstrumentedFileSystem(directory);
                var storage = await fixture.Open(directory, env, fsync: true);
                await CreateRoot(storage);
                var id = await storage.MintIdAsync<DocumentId>();
                await Commit(storage, new DocumentCreateWrite(SessionDocument(id), DocumentContent.Base(1, JO("{\"count\":0}"))));
                env.Fail(new(FailureOperation.Flush, 2, mode));
                Equal(Ids.SeqFromNumber(3), await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Base(1, JO("{\"count\":2}")))), "committed base");
                SameOperations(env, [$"append:doc-{id}.jsonl", $"flush:doc-{id}.jsonl", "append:main.jsonl", "flush:main.jsonl"], "deferred reclamation");
                Same("{\"count\":2}", (await storage.GetDocumentAsync(id, DocumentPoint.Current, JsonlContext))!.Value, "current value");
                Equal(2, ReadJsonlLines(Path.Combine(directory, $"doc-{id}.jsonl")).Length, "unreclaimed sidecar lines");
                await storage.CloseAsync(JsonlContext);

                var recoveryEnv = new InstrumentedFileSystem(directory);
                recoveryEnv.Fail(new(FailureOperation.Flush, 1, mode));
                var deferred = await fixture.Open(directory, recoveryEnv, fsync: true);
                Same("{\"count\":2}", (await deferred.GetDocumentAsync(id, DocumentPoint.Current, JsonlContext))!.Value, "value with reclamation deferred again");
                SameOperations(recoveryEnv, ["flush:main.jsonl"], "recovery operations");
                Equal(2, ReadJsonlLines(Path.Combine(directory, $"doc-{id}.jsonl")).Length, "still unreclaimed sidecar lines");
                await deferred.CloseAsync(JsonlContext);

                var reclaimed = await fixture.Open(directory, fsync: true);
                Same("{\"count\":2}", (await reclaimed.GetDocumentAsync(id, DocumentPoint.Current, JsonlContext))!.Value, "value after reclamation");
                Equal(1, ReadJsonlLines(Path.Combine(directory, $"doc-{id}.jsonl")).Length, "reclaimed sidecar lines");
            });
        }

        foreach (var mode in new[] { FailureMode.Before, FailureMode.After })
        {
            yield return JsonlCase($"keeps-a-committed-base-usable-after-reclaim-temp-flush-{mode.ToString().ToLowerInvariant()}-failure", async fixture =>
            {
                var directory = fixture.NewDirectory();
                var env = new InstrumentedFileSystem(directory);
                var storage = await fixture.Open(directory, env, fsync: true);
                await CreateRoot(storage);
                var id = await storage.MintIdAsync<DocumentId>();
                await Commit(storage, new DocumentCreateWrite(SessionDocument(id), DocumentContent.Base(1, JO("{\"count\":0}"))));
                env.Fail(new(FailureOperation.Flush, 3, mode));
                Equal(Ids.SeqFromNumber(3), await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Base(1, JO("{\"count\":2}")))), "committed base");
                env.Clear();
                await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"s\",[\"count\"],3]]"))));
                await storage.CloseAsync(JsonlContext);

                var reopened = await fixture.Open(directory, fsync: true);
                Same("{\"count\":3}", (await reopened.GetDocumentAsync(id, DocumentPoint.Current, JsonlContext))!.Value, "recovered value");
                Equal(2, ReadJsonlLines(Path.Combine(directory, $"doc-{id}.jsonl")).Length, "reclaimed sidecar lines");
            });
        }

        foreach (var mode in new[] { FailureMode.Before, FailureMode.After })
        {
            yield return JsonlCase($"recovers-terminal-task-reclamation-across-remove-{mode.ToString().ToLowerInvariant()}", async fixture =>
            {
                var directory = fixture.NewDirectory();
                var env = new InstrumentedFileSystem(directory);
                var storage = await fixture.Open(directory, env);
                await CreateRoot(storage);
                var id = await storage.MintIdAsync<TaskId>();
                await Commit(storage, new TaskWrite(PendingTask(id)));
                env.Fail(new(FailureOperation.Remove, 1, mode));
                Equal(Ids.SeqFromNumber(3), await Commit(storage, new TaskWrite(TerminalTask(id))), "terminal commit");
                Equal<TaskRecord?>(TerminalTask(id), await storage.GetTaskAsync(id, JsonlContext), "terminal task");
                await storage.CloseAsync(JsonlContext);

                var reopened = await fixture.Open(directory);
                Equal<TaskRecord?>(TerminalTask(id), await reopened.GetTaskAsync(id, JsonlContext), "recovered terminal task");
                Check(!PathExists(Path.Combine(directory, $"task-{id}.jsonl")), "the terminal task's sidecar is removed on reopen");
                Equal("", ReclaimFiles(directory), "temporary replacements");
            });
        }

        yield return JsonlCase("appends-later-deltas-to-the-replacement-sidecar-after-a-current-only-base", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var id = await storage.MintIdAsync<DocumentId>();
            await Commit(storage, new DocumentCreateWrite(SessionDocument(id), DocumentContent.Base(1, JO("{\"count\":0}"))));
            await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Base(1, JO("{\"count\":10}"))));
            await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"s\",[\"count\"],11]]"))));
            Equal(2, ReadJsonlLines(Path.Combine(directory, $"doc-{id}.jsonl")).Length, "sidecar lines");
            var reopened = await fixture.Open(directory);
            Same("{\"count\":11}", (await reopened.GetDocumentAsync(id, DocumentPoint.Current, JsonlContext))!.Value, "recovered value");
        });

        yield return JsonlCase("never-reclaims-rewindable-document-history-including-after-a-base-and-retirement", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var id = await storage.MintIdAsync<DocumentId>();
            var record = new DocumentCreate
            {
                Id = id, Kind = "rewindable", Scope = DocumentScope.Conversation(ConversationId.Root), History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            };
            var createdAt = await Commit(storage, new DocumentCreateWrite(record, DocumentContent.Base(1, JO("{\"count\":0}"))));
            var changedAt = await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Delta(1, Ops("[[\"s\",[\"count\"],1]]"))));
            await Commit(storage, new DocumentChangeWrite(id, DocumentContent.Base(1, JO("{\"count\":2}"))));
            await Commit(storage, new DocumentRetireWrite(id));

            Equal(3, ReadJsonlLines(Path.Combine(directory, $"doc-{id}.jsonl")).Length, "sidecar lines");
            var reopened = await fixture.Open(directory);
            Same("{\"count\":0}", (await reopened.GetDocumentAsync(id, createdAt, JsonlContext))!.Value, "value at creation");
            Same("{\"count\":1}", (await reopened.GetDocumentAsync(id, changedAt, JsonlContext))!.Value, "value at change");
            Check(await reopened.GetDocumentAsync(id, DocumentPoint.Current, JsonlContext) is null, "retired document");
            Equal(3, ReadJsonlLines(Path.Combine(directory, $"doc-{id}.jsonl")).Length, "sidecar lines after reopen");
        });

        yield return JsonlCase("reclaims-retired-task-session-and-latest-conversation-document-sidecars", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var taskId = await storage.MintIdAsync<TaskId>();
            var sessionId = await storage.MintIdAsync<DocumentId>();
            var latestId = await storage.MintIdAsync<DocumentId>();
            var taskDocumentId = await storage.MintIdAsync<DocumentId>();
            var createdAt = await Commit(storage,
                new TaskWrite(PendingTask(taskId)),
                new DocumentCreateWrite(SessionDocument(sessionId, "session"), DocumentContent.Base(1, JsonObjectValue.Empty)),
                new DocumentCreateWrite(new()
                {
                    Id = latestId, Kind = "latest", Scope = DocumentScope.Conversation(ConversationId.Root), History = DocumentHistory.Latest, Fork = DocumentFork.Current,
                }, DocumentContent.Base(1, JsonObjectValue.Empty)),
                new DocumentCreateWrite(new() { Id = taskDocumentId, Kind = "task", Scope = DocumentScope.Task(taskId) }, DocumentContent.Base(1, JsonObjectValue.Empty)));
            var retiredAt = await Commit(storage, new DocumentRetireWrite(sessionId), new DocumentRetireWrite(latestId),
                new DocumentRetireWrite(taskDocumentId), new TaskWrite(TerminalTask(taskId)));
            foreach (var file in new[] { $"doc-{sessionId}.jsonl", $"doc-{latestId}.jsonl", $"doc-{taskDocumentId}.jsonl", $"task-{taskId}.jsonl" })
                Check(!PathExists(Path.Combine(directory, file)), file + " is removed");

            var reopened = await fixture.Open(directory);
            Equal<TaskRecord?>(TerminalTask(taskId), await reopened.GetTaskAsync(taskId, JsonlContext), "terminal task");
            Check(await reopened.GetDocumentAsync(sessionId, DocumentPoint.Current, JsonlContext) is null, "session document");
            Check(await reopened.GetDocumentAsync(latestId, DocumentPoint.Current, JsonlContext) is null, "latest document");
            Check(await reopened.GetDocumentAsync(taskDocumentId, DocumentPoint.Current, JsonlContext) is null, "task document");
            var found = await reopened.FindDocumentAsync(new DocumentAddress("session", DocumentScope.Session), createdAt, JsonlContext);
            Check(found is not null && found.Id == sessionId && found.CreatedAt == createdAt && found.RetiredAt == retiredAt, "historical membership: " + found);
            Check(await reopened.FindDocumentAsync(new DocumentAddress("session", DocumentScope.Session), retiredAt, JsonlContext) is null, "membership after retirement");
        });

        yield return JsonlCase("truncates-torn-utf-8-tails-at-exact-byte-offsets-and-reuses-the-unconfirmed-sequence", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var documentId = await storage.MintIdAsync<DocumentId>();
            await Commit(storage, new DocumentCreateWrite(SessionDocument(documentId), DocumentContent.Base(1, JO("{\"text\":\"kept\"}"))));
            var sidecarPath = Path.Combine(directory, $"doc-{documentId}.jsonl");
            var mainPath = Path.Combine(directory, "main.jsonl");
            var sidecarSize = new FileInfo(sidecarPath).Length;
            var mainSize = new FileInfo(mainPath).Length;
            var torn = Encoding.UTF8.GetBytes("{\"text\":\"€");
            AppendBytes(sidecarPath, torn[..^1]);
            AppendBytes(mainPath, torn[..^1]);

            var reopened = await fixture.Open(directory);
            Equal(sidecarSize, new FileInfo(sidecarPath).Length, "sidecar size");
            Equal(mainSize, new FileInfo(mainPath).Length, "main size");
            Equal(Ids.SeqFromNumber(3), await Commit(reopened, new DocumentChangeWrite(documentId, DocumentContent.Delta(1, []))), "reused sequence");
        });

        yield return JsonlCase("removes-complete-unconfirmed-sidecar-tails-without-resurrecting-terminal-tasks", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var taskId = await storage.MintIdAsync<TaskId>();
            await Commit(storage, new TaskWrite(PendingTask(taskId)));
            await Commit(storage, new TaskWrite(TerminalTask(taskId)));
            var sidecarPath = Path.Combine(directory, $"task-{taskId}.jsonl");
            Check(!PathExists(sidecarPath), "the terminal task's sidecar is removed");
            AppendBytes(sidecarPath, Encoding.UTF8.GetBytes(JsonValue.Object(
                KeyValuePair.Create("format", (JsonValue)1), KeyValuePair.Create("type", (JsonValue)"record"), KeyValuePair.Create("seq", (JsonValue)4),
                KeyValuePair.Create("ordinal", (JsonValue)0), KeyValuePair.Create("payload", (JsonValue)JsonValue.Object(
                    KeyValuePair.Create("type", (JsonValue)"task"), KeyValuePair.Create("value", (JsonValue)PendingTask(taskId, "stale").ToJson())))).ToJson() + "\n"));

            var reopened = await fixture.Open(directory);
            Check(!PathExists(sidecarPath), "the unconfirmed sidecar is removed");
            Equal<TaskRecord?>(TerminalTask(taskId), await reopened.GetTaskAsync(taskId, JsonlContext), "terminal task");
            var next = await reopened.MintIdAsync<ConversationId>();
            Equal(Ids.SeqFromNumber(4), await Commit(reopened, new ConversationWrite(new(next))), "next sequence");
        });

        yield return JsonlCase("fails-open-when-confirmed-sidecar-data-is-missing", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var documentId = await storage.MintIdAsync<DocumentId>();
            await Commit(storage, new DocumentCreateWrite(SessionDocument(documentId), DocumentContent.Base(1, JsonObjectValue.Empty)));
            File.WriteAllText(Path.Combine(directory, $"doc-{documentId}.jsonl"), "");
            await OpenRejects(directory, "Missing confirmed sidecar record");
        });

        yield return JsonlCase("rejects-a-confirmed-record-after-an-unconfirmed-sidecar-record", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var documentId = await storage.MintIdAsync<DocumentId>();
            await Commit(storage, new DocumentCreateWrite(SessionDocument(documentId), DocumentContent.Base(1, JO("{\"count\":0}"))));
            await Commit(storage, new DocumentChangeWrite(documentId, DocumentContent.Delta(1, Ops("[[\"s\",[\"count\"],1]]"))));
            var path = Path.Combine(directory, $"doc-{documentId}.jsonl");
            var lines = ReadJsonlLines(path);
            var unconfirmed = $"{{\"format\":1,\"type\":\"record\",\"seq\":2,\"ordinal\":999,\"payload\":{{\"type\":\"document\",\"id\":{documentId},\"content\":{{\"kind\":\"delta\",\"version\":1,\"ops\":[]}}}}}}";
            File.WriteAllText(path, $"{lines[0]}\n{unconfirmed}\n{lines[1]}\n");
            await OpenRejects(directory, "Confirmed record follows an unconfirmed tail");
        });

        yield return JsonlCase("rejects-non-increasing-main-commit-sequences", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var path = Path.Combine(directory, "main.jsonl");
            AppendBytes(path, File.ReadAllBytes(path));
            await OpenRejects(directory, "Commit sequence does not strictly increase");
        });

        yield return JsonlCase("rejects-structurally-invalid-confirmed-document-content", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var documentId = await storage.MintIdAsync<DocumentId>();
            await Commit(storage, new DocumentCreateWrite(SessionDocument(documentId), DocumentContent.Base(1, JsonObjectValue.Empty)));
            var path = Path.Combine(directory, $"doc-{documentId}.jsonl");
            var record = (JsonObjectValue)J(File.ReadAllText(path).Trim());
            var payload = (JsonObjectValue)record["payload"];
            var content = ((JsonObjectValue)payload["content"]).Remove("value");
            File.WriteAllText(path, record.SetItem("payload", payload.SetItem("content", content)).ToJson() + "\n");
            await OpenRejects(directory, "Invalid document content");
        });

        yield return JsonlCase("rejects-malformed-complete-main-and-sidecar-lines", async fixture =>
        {
            var mainDirectory = fixture.NewDirectory();
            var mainStorage = await fixture.Open(mainDirectory);
            await CreateRoot(mainStorage);
            AppendBytes(Path.Combine(mainDirectory, "main.jsonl"), "{bad}\n"u8.ToArray());
            await OpenRejects(mainDirectory, "Malformed complete main.jsonl");

            var sidecarDirectory = fixture.NewDirectory();
            var sidecarStorage = await fixture.Open(sidecarDirectory);
            await CreateRoot(sidecarStorage);
            File.WriteAllText(Path.Combine(sidecarDirectory, "doc-99.jsonl"), "{bad}\n");
            await OpenRejects(sidecarDirectory, "Malformed complete doc-99.jsonl");
        });

        // Authored: the exact lines upstream writes for every write kind (checked against Pi v1.1.0's JsonlStorage writing the same
        // commits; v1.2.0 changed none of its line formats), including the {kind, version, value} base that MemoryStorage builds when it resolves a document copy. Records built here
        // without a Session take the key order the Session creates them in (an untyped entry draft's fields, then id and conversationId;
        // a submission's fields, then id); compat.* checks the per-path orders against sessions upstream wrote.
        yield return JsonlCase("writes-upstream-line-formats", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var task = PendingTask(new TaskId(2));
            var submission = new SubmissionRecord
            {
                Id = new SubmissionId(4), ConversationId = ConversationId.Root, RequestId = "r", Type = SubmissionType.Input, Status = SubmissionStatus.Placed, Entry = new EntryId(3),
            };
            await Commit(storage, new EntryWrite(new() { Id = new EntryId(3), ConversationId = ConversationId.Root, Kind = "note", Data = J("{\"text\":\"é\\u2028\"}") }),
                new TaskWrite(task), new SubmissionWrite(submission));
            var source = new DocumentCreate
            {
                Id = new DocumentId(5), Kind = "notes", Scope = DocumentScope.Conversation(ConversationId.Root), History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            };
            await Commit(storage, new DocumentCreateWrite(source, DocumentContent.Base(2, JO("{\"n\":1}"))), new TaskWrite(PendingTask(task.Id, "second")));
            await Commit(storage, new DocumentChangeWrite(source.Id, DocumentContent.Delta(2, Ops("[[\"s\",[\"n\"],2]]"))));
            await Commit(storage, new ConversationWrite(new(new ConversationId(6), new ConversationParent(ConversationId.Root, new EntryId(3)))),
                new DocumentCopyWrite(source with { Id = new DocumentId(7), Scope = DocumentScope.Conversation(new ConversationId(6)) }, new(source.Id, DocumentPoint.Current)),
                new TaskWrite(TerminalTask(task.Id)));
            await Commit(storage, new DocumentRetireWrite(source.Id));

            Equal(string.Join("\n",
                "{\"format\":1,\"type\":\"commit\",\"seq\":1,\"writes\":[{\"type\":\"conversation\",\"value\":{\"id\":1}}]}",
                "{\"format\":1,\"type\":\"commit\",\"seq\":2,\"writes\":[{\"type\":\"entry\",\"value\":{\"kind\":\"note\",\"data\":{\"text\":\"é\u2028\"},\"id\":3,\"conversationId\":1}}," +
                    "{\"type\":\"task.sidecar\",\"id\":2,\"ordinal\":0},{\"type\":\"submission\",\"value\":{\"conversationId\":1,\"requestId\":\"r\",\"type\":\"input\",\"status\":\"placed\",\"entry\":3,\"id\":4}}]}",
                "{\"format\":1,\"type\":\"commit\",\"seq\":3,\"writes\":[{\"type\":\"document.create\",\"record\":{\"id\":5,\"kind\":\"notes\",\"scope\":{\"kind\":\"conversation\",\"conversationId\":1},\"history\":\"rewindable\",\"fork\":\"asOf\"},\"ordinal\":0}," +
                    "{\"type\":\"task.sidecar\",\"id\":2,\"ordinal\":1}]}",
                "{\"format\":1,\"type\":\"commit\",\"seq\":4,\"writes\":[{\"type\":\"document.change\",\"id\":5,\"ordinal\":0}]}",
                "{\"format\":1,\"type\":\"commit\",\"seq\":5,\"writes\":[{\"type\":\"conversation\",\"value\":{\"id\":6,\"parent\":{\"conversationId\":1,\"at\":3}}}," +
                    "{\"type\":\"document.create\",\"record\":{\"id\":7,\"kind\":\"notes\",\"scope\":{\"kind\":\"conversation\",\"conversationId\":6},\"history\":\"rewindable\",\"fork\":\"asOf\"},\"ordinal\":0}," +
                    "{\"type\":\"task\",\"value\":" + TerminalTask(task.Id).ToJson().ToJson() + "}]}",
                "{\"format\":1,\"type\":\"commit\",\"seq\":6,\"writes\":[{\"type\":\"document.retire\",\"id\":5}]}",
                ""), File.ReadAllText(Path.Combine(directory, "main.jsonl")), "main.jsonl");
            Equal(string.Join("\n",
                "{\"format\":1,\"type\":\"record\",\"seq\":3,\"ordinal\":0,\"payload\":{\"type\":\"document\",\"id\":5,\"content\":{\"version\":2,\"kind\":\"base\",\"value\":{\"n\":1}}}}",
                "{\"format\":1,\"type\":\"record\",\"seq\":4,\"ordinal\":0,\"payload\":{\"type\":\"document\",\"id\":5,\"content\":{\"version\":2,\"kind\":\"delta\",\"ops\":[[\"s\",[\"n\"],2]]}}}",
                ""), File.ReadAllText(Path.Combine(directory, "doc-5.jsonl")), "doc-5.jsonl (rewindable history is never reclaimed)");
            Equal("{\"format\":1,\"type\":\"record\",\"seq\":5,\"ordinal\":0,\"payload\":{\"type\":\"document\",\"id\":7,\"content\":{\"kind\":\"base\",\"version\":2,\"value\":{\"n\":2}}}}\n",
                File.ReadAllText(Path.Combine(directory, "doc-7.jsonl")), "doc-7.jsonl (a resolved copy)");
            Check(!PathExists(Path.Combine(directory, "task-2.jsonl")), "the terminal task's sidecar is removed");
            Equal("main.jsonl,doc-5.jsonl,doc-7.jsonl", string.Join(",", Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(name => name!.StartsWith("main") ? 0 : 1).ThenBy(name => name, StringComparer.Ordinal)), "files");
        });

        yield return JsonlCase("bounds-retained-heap-under-large-payloads-and-reloads-lazily", async fixture =>
        {
            static string MakeLargePayload(int index) =>
                string.Create(64 * 1024, index, static (span, idx) =>
                {
                    var fill = (char)('A' + (idx % 26));
                    span.Fill(fill);
                    var header = $"payload-{idx:D4}:";
                    header.AsSpan().CopyTo(span);
                });

            static long ForceManagedHeap()
            {
                for (var round = 0; round < 3; round++)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                }
                return GC.GetTotalMemory(forceFullCollection: true);
            }

            static async Task<(List<EntryId> Ids, DocumentId DocId)> SeedLargePayloadsAsync(JsonlStorage target)
            {
                var seeded = new List<EntryId>(80);
                for (var index = 0; index < 80; index++)
                {
                    var id = await target.MintIdAsync<EntryId>();
                    seeded.Add(id);
                    var blob = MakeLargePayload(index);
                    await Commit(target, new EntryWrite(new()
                    {
                        Id = id, ConversationId = ConversationId.Root, Kind = "large",
                        Data = JsonValue.Object([KeyValuePair.Create("index", (JsonValue)index), KeyValuePair.Create("blob", (JsonValue)blob)]),
                    }));
                }
                var seededDocId = await target.MintIdAsync<DocumentId>();
                await Commit(target, new DocumentCreateWrite(SessionDocument(seededDocId), DocumentContent.Base(1,
                    JsonValue.Object([KeyValuePair.Create("blob", (JsonValue)MakeLargePayload(100))]))));
                await Commit(target, new DocumentChangeWrite(seededDocId, DocumentContent.Base(1,
                    JsonValue.Object([KeyValuePair.Create("blob", (JsonValue)("v2:" + MakeLargePayload(101)))]))));
                return (seeded, seededDocId);
            }

            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var baselineHeap = ForceManagedHeap();

            var (ids, docId) = await SeedLargePayloadsAsync(storage);

            var retainedAfterWrites = ForceManagedHeap();
            var heapDelta = Math.Max(0, retainedAfterWrites - baselineHeap);
            var mainBytes = new FileInfo(Path.Combine(directory, "main.jsonl")).Length;
            // 80 distinct 64 KiB payloads (~5.1 MiB) were written; retained heap delta must stay well below half of main.jsonl.
            Check(heapDelta < mainBytes / 2,
                $"retained heap delta should stay well below raw main.jsonl payload size (delta {heapDelta}, baseline {baselineHeap}, after {retainedAfterWrites}, main {mainBytes})");

            var first = await storage.GetEntryAsync(ids[0], JsonlContext);
            var last = await storage.GetEntryAsync(ids[^1], JsonlContext);
            Equal(MakeLargePayload(0), ((JsonStringValue)((JsonObjectValue)first!.Entry.Data!)["blob"]).Value, "first lazy entry");
            Equal(MakeLargePayload(79), ((JsonStringValue)((JsonObjectValue)last!.Entry.Data!)["blob"]).Value, "last lazy entry");
            var page = await storage.ScanEntriesAsync(new EntryQuery(ConversationId.Root), 10, null, JsonlContext);
            Equal(10, page.Items.Length, "paged lazy scan");
            var doc = await storage.GetDocumentAsync(docId, DocumentPoint.Current, JsonlContext);
            Equal("v2:" + MakeLargePayload(101), ((JsonStringValue)doc!.Value["blob"]).Value, "reclaimed current-only document");

            var reopened = await fixture.Open(directory);
            var reloaded = await reopened.GetEntryAsync(ids[40], JsonlContext);
            Equal(MakeLargePayload(40), ((JsonStringValue)((JsonObjectValue)reloaded!.Entry.Data!)["blob"]).Value, "entry after reopen");
            Equal("v2:" + MakeLargePayload(101), ((JsonStringValue)(await reopened.GetDocumentAsync(docId, DocumentPoint.Current, JsonlContext))!.Value["blob"]).Value,
                "document after reopen");
        });

        yield return JsonlCase("protects-cold-sidecar-reads-from-concurrent-terminal-task-and-document-reclamation", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var taskPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var taskRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var docPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var docRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fs = new PausingJoinFileSystem(directory);
            var storage = await fixture.Open(directory, fs);
            await CreateRoot(storage);
            var taskId = await storage.MintIdAsync<TaskId>();
            var docId = await storage.MintIdAsync<DocumentId>();
            var pendingTask = PendingTask(taskId, "live");
            await Commit(storage, new TaskWrite(pendingTask),
                new DocumentCreateWrite(SessionDocument(docId), DocumentContent.Base(1, JO("{\"v\":1}"))));

            // Pause the cold GetTaskAsync inside sidecar path resolution while a concurrent commit makes the task terminal and reclaims its sidecar.
            fs.PauseOnce($"task-{taskId}.jsonl", taskPaused, taskRelease.Task);
            var taskRead = storage.GetTaskAsync(taskId, JsonlContext);
            try
            {
                await Task.WhenAny(taskPaused.Task, taskRead).WaitAsync(TimeSpan.FromSeconds(5));
                Check(taskPaused.Task.IsCompleted, "cold task read reached sidecar path resolution");
                var reclaimEntered = fs.NotifyJoinOnce($"task-{taskId}.jsonl");
                var terminalCommit = storage.CommitAsync([[new TaskWrite(TerminalTask(taskId))]], (_, _) => { }, JsonlContext);
                await reclaimEntered.WaitAsync(TimeSpan.FromSeconds(5));
                // Give reclamation a brief window to prove it blocks on the reader gate rather than deleting the sidecar.
                await Task.WhenAny(terminalCommit, Task.Delay(50));
                Check(!terminalCommit.IsCompleted, "task sidecar reclamation waits for the in-flight cold reader");
                Check(File.Exists(Path.Combine(directory, $"task-{taskId}.jsonl")), "sidecar stays until in-flight cold task read finishes");
                taskRelease.TrySetResult();
                var readTask = await taskRead.WaitAsync(TimeSpan.FromSeconds(5));
                Equal<TaskRecord?>(pendingTask, readTask, "in-flight cold task read returns the pending record selected before reclamation");
                await terminalCommit.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                taskRelease.TrySetResult();
            }
            Check(!File.Exists(Path.Combine(directory, $"task-{taskId}.jsonl")), "sidecar is reclaimed after reader finishes");
            Equal<TaskRecord?>(TerminalTask(taskId), await storage.GetTaskAsync(taskId, JsonlContext), "subsequent read sees terminal task from main");

            // Pause a cold GetDocumentAsync while a concurrent commit rewrites the current-only document base and reclaims its sidecar.
            fs.PauseOnce($"doc-{docId}.jsonl", docPaused, docRelease.Task);
            var docRead = storage.GetDocumentAsync(docId, DocumentPoint.Current, JsonlContext);
            try
            {
                await Task.WhenAny(docPaused.Task, docRead).WaitAsync(TimeSpan.FromSeconds(5));
                Check(docPaused.Task.IsCompleted, "cold document read reached sidecar path resolution");
                var reclaimWritten = fs.NotifyWriteOnce($"doc-{docId}.jsonl.reclaim");
                var baseCommit = storage.CommitAsync([[new DocumentChangeWrite(docId, DocumentContent.Base(1, JO("{\"v\":2}")))]], (_, _) => { }, JsonlContext);
                await reclaimWritten.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAny(baseCommit, Task.Delay(50));
                Check(!baseCommit.IsCompleted, "document sidecar reclamation waits for the in-flight cold reader");
                docRelease.TrySetResult();
                var readDoc = await docRead.WaitAsync(TimeSpan.FromSeconds(5));
                Check(readDoc is not null, "in-flight cold document read succeeds across concurrent base reclamation");
                Same("{\"v\":1}", readDoc!.Value, "in-flight cold document read returns the original base selected before reclamation");
                await baseCommit.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                docRelease.TrySetResult();
            }
            Same("{\"v\":2}", (await storage.GetDocumentAsync(docId, DocumentPoint.Current, JsonlContext))!.Value, "subsequent read sees reclaimed base");
        });

        yield return JsonlCase("retries-filtered-task-scan-when-cold-task-status-changes-mid-page", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var taskPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var taskRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fs = new PausingJoinFileSystem(directory);
            var storage = await fixture.Open(directory, fs);
            await CreateRoot(storage);
            var taskA = await storage.MintIdAsync<TaskId>();
            var taskB = await storage.MintIdAsync<TaskId>();
            await Commit(storage, new TaskWrite(PendingTask(taskA, "a")), new TaskWrite(PendingTask(taskB, "b")));

            // Pause the pending-only scan while loading taskA; concurrently make taskB terminal before taskB is loaded.
            fs.PauseOnce($"task-{taskA}.jsonl", taskPaused, taskRelease.Task);
            var scan = storage.ScanTasksAsync(new TaskQuery { Status = DurableTaskStatus.Pending }, 10, null, JsonlContext);
            try
            {
                await Task.WhenAny(taskPaused.Task, scan).WaitAsync(TimeSpan.FromSeconds(5));
                Check(taskPaused.Task.IsCompleted, "filtered scan paused on taskA");
                await Commit(storage, new TaskWrite(TerminalTask(taskB))).WaitAsync(TimeSpan.FromSeconds(5));
                taskRelease.TrySetResult();
                var page = await scan.WaitAsync(TimeSpan.FromSeconds(5));
                Equal(1, page.Items.Length, "retried pending scan excludes taskB after it became terminal");
                Equal(taskA, page.Items[0].Id, "only pending taskA remains");
                Equal(DurableTaskStatus.Pending, page.Items[0].State.Status, "returned task matches pending filter");
            }
            finally
            {
                taskRelease.TrySetResult();
            }
        });

        yield return JsonlCase("cancels-sidecar-deletion-when-later-batch-in-same-commit-writes-live-task", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var taskId = await storage.MintIdAsync<TaskId>();
            await Commit(storage, new TaskWrite(PendingTask(taskId, "initial")));

            // Batch 1 makes taskId terminal (scheduling sidecar deletion); batch 2 replaces it with a live task in the same CommitAsync.
            var liveAgain = PendingTask(taskId, "reborn");
            var result = await storage.CommitAsync([[new TaskWrite(TerminalTask(taskId))], [new TaskWrite(liveAgain)]], (_, _) => { }, JsonlContext);
            Equal(new CommitResult(2), result, "both batches committed");
            Check(File.Exists(Path.Combine(directory, $"task-{taskId}.jsonl")), "live replacement cancels pending sidecar deletion");
            Equal<TaskRecord?>(liveAgain, await storage.GetTaskAsync(taskId, JsonlContext), "live task readable from sidecar");
            await storage.CloseAsync(JsonlContext);

            var reopened = await fixture.Open(directory);
            Equal<TaskRecord?>(liveAgain, await reopened.GetTaskAsync(taskId, JsonlContext), "directory reopens cleanly with live task");
        });
    }

    private static IEnumerable<(string, Func<Task>)> StorageJsonlBatchesCases()
    {
        yield return JsonlFixtureCase("storage.jsonl.batches.appends-a-batch-s-markers-in-one-write-and-recovers-a-shorter-prefix-from-a-torn-one", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var storage = await fixture.Open(directory);
            await CreateRoot(storage);
            var documentId = await storage.MintIdAsync<DocumentId>();
            await Commit(storage, new DocumentCreateWrite(SessionDocument(documentId), DocumentContent.Base(1, JO("{\"n\":0}"))));
            var mainPath = Path.Combine(directory, "main.jsonl");
            var before = File.ReadAllText(mainPath).Length;
            EntryId[] entries = [await storage.MintIdAsync<EntryId>(), await storage.MintIdAsync<EntryId>(), await storage.MintIdAsync<EntryId>()];
            IReadOnlyList<IReadOnlyList<StorageWrite>> batches = [.. entries.Select(IReadOnlyList<StorageWrite> (id, index) =>
            [
                new EntryWrite(new() { Id = id, ConversationId = ConversationId.Root, Kind = "note" }),
                new DocumentChangeWrite(documentId, DocumentContent.Delta(1, Ops($"[[\"s\",[\"n\"],{index + 1}]]"))),
            ])];
            // Reads wait for the append: a read started while it runs sees every batch durable.
            var result = storage.CommitAsync(batches, (_, _) => { }, JsonlContext);
            var read = storage.GetEntryAsync(entries[2], JsonlContext);
            Equal(new CommitResult(3), await result, "result");
            Check(await read is not null, "the read started during the append sees the last batch");
            var main = File.ReadAllText(mainPath);
            var markers = main[before..].Split('\n').Where(line => line.Length > 0).ToArray();
            Equal(3, markers.Length, "markers appended");
            await storage.CloseAsync(JsonlContext);

            // Torn inside the last marker: the two complete ones survive, and the third's sidecar record is unconfirmed.
            var tornAt = main.Length - markers[2].Length / 2;
            File.WriteAllText(mainPath, main[..tornAt]);
            var reopened = await fixture.Open(directory);
            Check(await reopened.GetEntryAsync(entries[0], JsonlContext) is not null, "the first batch's entry");
            Check(await reopened.GetEntryAsync(entries[1], JsonlContext) is not null, "the second batch's entry");
            Check(await reopened.GetEntryAsync(entries[2], JsonlContext) is null, "the torn batch's entry");
            Equal("{\"n\":2}", (await reopened.GetDocumentAsync(documentId, DocumentPoint.Current, JsonlContext))?.Value.ToJson(), "document after the torn batch");
            var seq = await Commit(reopened, new DocumentChangeWrite(documentId, DocumentContent.Delta(1, Ops("[[\"s\",[\"n\"],9]]"))));
            Equal(Ids.SeqFromNumber(5), seq, "the torn batch's sequence is reused");
            var again = await fixture.Open(directory);
            Equal("{\"n\":9}", (await again.GetDocumentAsync(documentId, DocumentPoint.Current, JsonlContext))?.Value.ToJson(), "document after reopening");
        });
    }

    /// <summary>The local file system whose path joins, once <see cref="Fail"/> is set, wait for <paramref name="gate"/> and then fail
    /// (upstream's <c>FailingJoin</c>, a NodeExecutionEnv subclass).</summary>
    private sealed class FailingJoin(string directory, Task gate) : LocalFileSystem(directory)
    {
        public bool Fail { get; set; }

        public override async Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, DurableContext context)
        {
            if (!Fail) return await base.JoinPathAsync(parts, context);
            await gate;
            return new FileError(FileErrorCode.Unknown, "injected path resolution failure");
        }
    }

    private sealed class PausingJoinFileSystem(string directory) : LocalFileSystem(directory)
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, (TaskCompletionSource Paused, Task Release)> pauses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> joinNotices = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> writeNotices = new(StringComparer.Ordinal);

        public void PauseOnce(string fileName, TaskCompletionSource paused, Task release)
        {
            lock (gate) pauses[fileName] = (paused, release);
        }

        public Task NotifyJoinOnce(string fileName)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) joinNotices[fileName] = tcs;
            return tcs.Task;
        }

        public Task NotifyWriteOnce(string fileName)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) writeNotices[fileName] = tcs;
            return tcs.Task;
        }

        public override async Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, DurableContext context)
        {
            (TaskCompletionSource Paused, Task Release)? match = null;
            TaskCompletionSource? notice = null;
            if (parts.Count > 0)
            {
                lock (gate)
                {
                    if (pauses.Remove(parts[^1], out var found)) match = found;
                    else joinNotices.Remove(parts[^1], out notice);
                }
            }
            if (match is { } foundMatch)
            {
                foundMatch.Paused.TrySetResult();
                await foundMatch.Release;
            }
            notice?.TrySetResult();
            return await base.JoinPathAsync(parts, context);
        }

        public override async Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context)
        {
            var result = await base.WriteFileAsync(path, content, context);
            var name = Path.GetFileName(path);
            TaskCompletionSource? notice = null;
            lock (gate) writeNotices.Remove(name, out notice);
            notice?.TrySetResult();
            return result;
        }
    }

    private static (string, Func<Task>) JsonlCase(string slug, Func<JsonlFixture, Task> body) => JsonlFixtureCase("storage.jsonl." + slug, body);

    /// <summary>A case with its whole id, so the id appears in this file as the port's evidence names it.</summary>
    private static (string, Func<Task>) JsonlFixtureCase(string id, Func<JsonlFixture, Task> body) => Case(id, async () =>
    {
        await using var fixture = new JsonlFixture();
        await body(fixture);
    });

    private static string Mode(Failure failure) => failure.Mode.ToString().ToLowerInvariant();

    private static Task<Seq> Commit(IStorage storage, params StorageWrite[] writes) => storage.CommitWritesAsync(writes, JsonlContext);

    private static Task CreateRoot(IStorage storage) => Commit(storage, new ConversationWrite(new(ConversationId.Root)));

    private static JsonObjectValue JO(string json) => (JsonObjectValue)J(json);

    private static TaskRecord PendingTask(TaskId id, string phase = "ready") => new()
    {
        Id = id, ConversationId = ConversationId.Root, Kind = "test.task", Version = 1, Input = JsonValue.Null,
        State = new PendingTaskState(JsonValue.Object(KeyValuePair.Create("phase", (JsonValue)phase))), Background = false, AbortRequested = false,
    };

    private static TaskRecord TerminalTask(TaskId id) => new()
    {
        Id = id, ConversationId = ConversationId.Root, Kind = "test.task", Version = 1, Input = JsonValue.Null,
        State = new TerminalTaskState(new CompletedTaskOutcome(JsonValue.Null)), Background = false, AbortRequested = false,
    };

    private static DocumentCreate SessionDocument(DocumentId id, string kind = "test.document") => new() { Id = id, Kind = kind, Scope = DocumentScope.Session };

    private static string[] ReadJsonlLines(string path)
    {
        var text = File.ReadAllText(path);
        return text.Length == 0 ? [] : text.TrimEnd().Split('\n');
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string ReclaimFiles(string directory) =>
        string.Join(",", Directory.GetFiles(directory).Select(Path.GetFileName).Where(name => name!.EndsWith(".reclaim", StringComparison.Ordinal)));

    private static void AppendBytes(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes);
    }

    private static void SameOperations(InstrumentedFileSystem env, IEnumerable<string> expected, string what) =>
        Equal(string.Join(", ", expected), string.Join(", ", env.Operations), what + " operations");

    private static async Task RejectsPoisoned(Func<Task> run, string what)
    {
        var error = await ThrowsAsync<JsonlStoragePoisonedException>(run, what);
        Check(error.Message.Contains("poisoned", StringComparison.Ordinal), what + ": " + error.Message);
    }

    private static async Task OpenRejects(string directory, string message)
    {
        var error = await ThrowsAsync<Exception>(() => JsonlStorage.OpenAsync(directory, new LocalFileSystem(directory), JsonlContext), "open");
        Check(error.Message.Contains(message, StringComparison.Ordinal), $"open: expected a message containing \"{message}\", actual \"{error.Message}\"");
    }

    /// <summary>Tracks the directories and storages of one case and releases them afterwards (upstream's afterEach).</summary>
    private sealed class JsonlFixture : IAsyncDisposable
    {
        private readonly List<string> directories = [];
        private readonly List<JsonlStorage> storages = [];

        public string NewDirectory()
        {
            var directory = Temp("jsonl");
            directories.Add(directory);
            return directory;
        }

        public async Task<JsonlStorage> Track(Task<JsonlStorage> opening)
        {
            var storage = await opening;
            storages.Add(storage);
            return storage;
        }

        public Task<JsonlStorage> Open(string directory, IFileSystem? fs = null, bool fsync = false) =>
            Track(JsonlStorage.OpenAsync(directory, fs ?? new LocalFileSystem(directory), JsonlContext, new JsonlStorageOptions { Fsync = fsync }));

        public async ValueTask DisposeAsync()
        {
            foreach (var storage in storages) await storage.CloseAsync(JsonlContext);
            foreach (var directory in directories) DeleteDirectory(directory);
        }
    }

    /// <summary>Closes and reopens the directory after every commit, so the conformance suite reads only replayed state.</summary>
    private sealed class ReopeningStorage(JsonlStorage current, string directory) : IStorage
    {
        private JsonlStorage current = current;
        private bool closed;

        public async Task<CommitResult> CommitAsync(IReadOnlyList<IReadOnlyList<StorageWrite>> batches, Action<int, Seq> receive, DurableContext context)
        {
            if (closed) return await current.CommitAsync(batches, receive, context);
            try { return await current.CommitAsync(batches, receive, context); }
            finally
            {
                await current.CloseAsync(JsonlContext);
                current = await JsonlStorage.OpenAsync(directory, new LocalFileSystem(directory), JsonlContext);
            }
        }

        public Task<TId> MintIdAsync<TId>() where TId : struct, IDurableId<TId> => current.MintIdAsync<TId>();
        public Task<ConversationRecord?> GetConversationAsync(ConversationId id, DurableContext context) => current.GetConversationAsync(id, context);
        public Task<Page<ConversationRecord>> ScanConversationsAsync(ConversationQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanConversationsAsync(query, limit, cursor, context);
        public Task<CommittedEntry?> GetEntryAsync(EntryId id, DurableContext context) => current.GetEntryAsync(id, context);
        public Task<CommittedEntry?> GetEntryAsync(ConversationId conversationId, EntryId id, DurableContext context) => current.GetEntryAsync(conversationId, id, context);
        public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId, DurableContext context) =>
            current.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId, context);
        public Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanEntriesAsync(query, limit, cursor, context);
        public Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context) => current.GetTaskAsync(id, context);
        public Task<Page<TaskRecord>> ScanTasksAsync(TaskQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanTasksAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context) => current.GetSubmissionAsync(id, context);
        public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanSubmissionsAsync(query, limit, cursor, context);
        public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId, DurableContext context) =>
            current.GetSubmissionByRequestAsync(conversationId, requestId, context);
        public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at, DurableContext context) => current.FindDocumentAsync(address, at, context);
        public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at, DurableContext context) => current.GetDocumentAsync(id, at, context);
        public Task<Page<DocumentRecord>> ScanDocumentsAsync(DocumentQuery query, int limit, JsonObjectValue? cursor, DurableContext context) =>
            current.ScanDocumentsAsync(query, limit, cursor, context);

        public async Task CloseAsync(DurableContext context)
        {
            if (closed) return;
            closed = true;
            await current.CloseAsync(context);
        }
    }
}
