// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/codemode-checkpoints.test.ts ("built-in document checkpoint
// policies"), as codemode-checkpoints.<case>, ported in the Pi v1.5.0 sync (area checkpoint-when). Verbatim apart from the adaptations in
// SessionSupport.cs, and these: CodemodeRunDoc, CodemodeJournalDoc and CodemodeStoreDoc are DurableCodemode.RunDoc, JournalDoc and StoreDoc;
// NestedCallDoc is HarnessTasks.NestedCallDoc; storage.findDocument and storage.document are ControlledStorage's FindDocumentAsync and
// GetDocumentAsync; storage.commits.at(-1) is ControlledStorage.LastCommit, and toContainEqual compares each write's JSON with JavaScript's
// deep equality (JsonValue.DeepEquals). Like upstream's, the second case runs on MemoryStorage only.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CodemodeCheckpointsCases() => CodemodeCheckpointsTests.Cases();
}

/// <summary>The codemode-checkpoints.* cases (upstream codemode-checkpoints.test.ts).</summary>
internal static class CodemodeCheckpointsTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return Case("codemode-checkpoints.keeps-task-scoped-documents-as-deltas-until-their-task-retires", () =>
        {
            var info = new CheckpointInfo(1000);
            Check(!HarnessTasks.NestedCallDoc.Definition.CheckpointWhen(JsonObjectValue.Empty, [], info), "NestedCallDoc");
            Check(!DurableCodemode.RunDoc.Definition.CheckpointWhen(JsonObjectValue.Empty, [], info), "CodemodeRunDoc");
            Check(!DurableCodemode.JournalDoc.Definition.CheckpointWhen(O("""{"entries":[]}"""), [], info), "CodemodeJournalDoc");
        });

        yield return Case("codemode-checkpoints.checkpoints-the-codemode-store-after-64-deltas-without-changing-historical-reads-or-forks", async () =>
        {
            var (storage, session, _) = Open(() => new MemoryStorage());
            try
            {
                var conversationId = await CreateConversation(session);
                var cutoff = await session.CommitAsync(async tx =>
                {
                    (await tx.DocAsync(DurableCodemode.StoreDoc, conversationId)).GetObject("values")["counter"] = 0;
                    return (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "test.counter" })).Id;
                }, Context);
                var record = await storage.FindDocumentAsync(
                    new DocumentAddress(DurableCodemode.StoreDoc.Definition.Kind, DocumentScope.Conversation(conversationId)),
                    DocumentPoint.Current,
                    Context) ?? throw new InvalidOperationException("Expected codemode store");
                for (var counter = 1; counter <= 64; counter++)
                {
                    var value = counter;
                    var entry = await session.CommitAsync(async tx =>
                    {
                        (await tx.DocAsync(DurableCodemode.StoreDoc, conversationId)).GetObject("values")["counter"] = value;
                        return (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "test.counter" })).Id;
                    }, Context);
                    if (counter == 32) cutoff = entry;
                }
                Equal(64, (await storage.GetDocumentAsync(record.Id, DocumentPoint.Current, Context))?.DeltasSinceBase, "deltas before the base");
                await session.CommitAsync(async tx =>
                    (await tx.DocAsync(DurableCodemode.StoreDoc, conversationId)).GetObject("values")["counter"] = 65, Context);
                Equal(0, (await storage.GetDocumentAsync(record.Id, DocumentPoint.Current, Context))?.DeltasSinceBase, "deltas after the base");
                var expected = Obj(("type", "document.change"), ("id", record.Id.ToJson()),
                    ("content", J("""{"kind":"base","version":1,"value":{"values":{"counter":65}}}""")));
                Check(storage.LastCommit.Any(write => JsonValue.DeepEquals(expected, write.ToJson())),
                    $"the last commit holds {expected.ToJson()}: {string.Join(", ", storage.LastCommit.Select(write => write.ToJson().ToJson()))}");
                SameJson("""{"values":{"counter":32}}""", await session.SnapshotAsOfAsync(DurableCodemode.StoreDoc, conversationId, cutoff, Context),
                    "snapshot as of the cutoff");
                var fork = await session.CommitAsync(tx => tx.ForkConversationAsync(conversationId, cutoff, ConversationOwnership.Ownerless), Context);
                SameJson("""{"values":{"counter":32}}""", await session.SnapshotAsync(DurableCodemode.StoreDoc, fork.Id, Context), "fork snapshot");
                await session.CommitAsync(async tx =>
                    (await tx.DocAsync(DurableCodemode.StoreDoc, conversationId)).GetObject("values")["counter"] = 66, Context);
                Equal(1, (await storage.GetDocumentAsync(record.Id, DocumentPoint.Current, Context))?.DeltasSinceBase, "deltas after the next change");
            }
            finally
            {
                await session.CloseAsync(Context);
            }
        });
    }
}
