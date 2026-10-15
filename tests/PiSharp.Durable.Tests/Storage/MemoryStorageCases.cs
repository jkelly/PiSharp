using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Testing;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/memory-storage.test.ts. The storage conformance kit runs
// against MemoryStorage as storage.memory.conformance.<case>; the prepared-commit case follows upstream's, with immutability standing in
// for upstream's frozen writes. The storage.memory.prepare-* cases are authored for the prepare/apply split JSONL builds on (P4).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StorageMemoryCases()
    {
        foreach (var testCase in StorageConformance.Create(use => use(new MemoryStorage())))
            yield return Case("storage.memory.conformance." + testCase.Slug, testCase.Run);

        yield return Case("storage.memory.does-not-expose-retained-state-through-a-prepared-commit", async () =>
        {
            var storage = new MemoryStorage();
            await storage.CommitAsync([new ConversationWrite(new(ConversationId.Root))], DurableContext.Background);
            var entryId = Ids.FromNumber<EntryId>(2);
            var prepared = storage.PrepareCommit([new EntryWrite(new()
            {
                Id = entryId, ConversationId = ConversationId.Root, Kind = "test", Data = J("{\"nested\":[1]}"),
            })]);
            var exposed = prepared.Writes[0] as EntryWrite ?? throw new InvalidOperationException("Expected an entry write");
            // Upstream: pushing into the exposed array throws because prepared writes are frozen. Here the array is immutable.
            var nested = ((JsonObjectValue)exposed.Value.Data!)["nested"];
            Check(nested is JsonArrayValue && !typeof(ICollection<JsonValue>).IsAssignableFrom(nested.GetType()) &&
                !typeof(System.Collections.IList).IsAssignableFrom(nested.GetType()), "prepared JSON is immutable");

            Equal(Ids.SeqFromNumber(2), prepared.Apply(), "applied sequence");
            Equal(Ids.SeqFromNumber(2), prepared.Apply(), "a second apply is a no-op");
            Same("{\"nested\":[1]}", (await storage.GetEntryAsync(entryId, DurableContext.Background))!.Entry.Data!, "entry data");
        });

        yield return Case("storage.memory.prepare-validates-without-changing-state", async () =>
        {
            var storage = new MemoryStorage();
            await storage.CommitAsync([new ConversationWrite(new(ConversationId.Root))], DurableContext.Background);
            var entry = new EntryRecord { Id = new EntryId(5), ConversationId = ConversationId.Root, Kind = "note" };
            var prepared = storage.PrepareCommit([new EntryWrite(entry)]);
            Equal(Ids.SeqFromNumber(2), prepared.Seq, "prepared sequence");
            Check(await storage.GetEntryAsync(entry.Id, DurableContext.Background) is null, "nothing is observable before apply");
            Equal(2L, (await storage.MintIdAsync<EntryId>()).Value, "an unapplied write does not advance minting");

            // A failing preparation has no effect and leaves the storage usable.
            Equal("ID 1 already belongs to conversation", Throws<InvalidOperationException>(
                () => storage.PrepareCommit([new ConversationWrite(new(ConversationId.Root))]), "duplicate root").Message, "rejection message");
            prepared.Apply();
            Equal<EntryRecord?>(entry, (await storage.GetEntryAsync(entry.Id, DurableContext.Background))?.Entry, "entry after apply");
            Equal(6L, (await storage.MintIdAsync<EntryId>()).Value, "applied IDs advance minting");
        });

        yield return Case("storage.memory.prepare-takes-a-recorded-sequence-that-strictly-increases", async () =>
        {
            var storage = new MemoryStorage();
            Equal(Ids.SeqFromNumber(7), storage.PrepareCommit([new ConversationWrite(new(ConversationId.Root))], Ids.SeqFromNumber(7)).Apply(), "replayed sequence");
            Equal("Commit sequence 7 does not strictly increase",
                Throws<InvalidOperationException>(() => storage.PrepareCommit([], Ids.SeqFromNumber(7)), "repeated sequence").Message, "message");
            Equal(Ids.SeqFromNumber(8), await storage.CommitAsync([], DurableContext.Background), "the next commit follows the replayed one");
        });

        yield return Case("storage.memory.prepared-copies-resolve-to-creates-of-the-stored-base", async () =>
        {
            var storage = new MemoryStorage();
            var context = DurableContext.Background;
            await storage.CommitAsync([new ConversationWrite(new(ConversationId.Root)), new ConversationWrite(new(new ConversationId(2)))], context);
            var source = new DocumentCreate
            {
                Id = new DocumentId(3), Kind = "notes", Scope = DocumentScope.Conversation(ConversationId.Root),
                History = DocumentHistory.Rewindable, Fork = DocumentFork.AsOf,
            };
            await storage.CommitAsync([new DocumentCreateWrite(source, DocumentContent.Base(2, (JsonObjectValue)J("{\"n\":1}")))], context);
            await storage.CommitAsync([new DocumentChangeWrite(source.Id, DocumentContent.Delta(2, Ops("[[\"s\",[\"n\"],2]]")))], context);
            var copy = source with { Id = new DocumentId(4), Scope = DocumentScope.Conversation(new ConversationId(2)) };
            var prepared = storage.PrepareCommit([new DocumentCopyWrite(copy, new(source.Id, DocumentPoint.Current))]);
            var create = prepared.Writes[0] as DocumentCreateWrite ?? throw new InvalidOperationException("Expected a resolved create");
            Equal(copy, create.Record, "copied record");
            Equal("{\"version\":2,\"kind\":\"base\",\"value\":{\"n\":2}}", create.Content.ToJson().ToJson(), "copied base");
        });
    }
}
