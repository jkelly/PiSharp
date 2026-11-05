// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/session-conversation-tails.test.ts, new in Pi v1.5.0
// (e5c6182ba, sync area publication-caches). The newest entry and head marker per conversation (Session/ConversationTails.cs). Verbatim
// apart from these: upstream's undefined (none) is null; "freezes the markers it keeps" checks that the kept marker is the written entry and
// that an entry record cannot change (InitOnly, SessionCachedStorageCases.cs), as records are immutable here and need no freeze.
using PiSharp.Durable;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionConversationTailsCases() => ConversationTailsTests.Cases("session.conversation-tails.");
}

internal static class ConversationTailsTests
{
    private static ConversationId Conversation(long id) => new(id);

    private static EntryId EntryIdOf(long id) => new(id);

    private static EntryWrite Entry(long id, long conversationId, long? head = null) => new(new EntryRecord
    {
        Id = EntryIdOf(id),
        ConversationId = Conversation(conversationId),
        Kind = "note",
        Head = head is { } at ? EntryIdOf(at) : null,
    });

    private static Task<T> Never<T>() => Task.FromException<T>(new InvalidOperationException("read"));

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "reads-once-then-follows-the-commits", async () =>
        {
            var tails = new ConversationTails();
            var reads = 0;
            Task<EntryId?> Read()
            {
                reads++;
                return Task.FromResult<EntryId?>(EntryIdOf(5));
            }
            Equal(EntryIdOf(5), await tails.LatestAsync(Conversation(1), Read), "first");
            Equal(EntryIdOf(5), await tails.LatestAsync(Conversation(1), Read), "second");
            Equal(1, reads, "reads");
            tails.Apply([Entry(9, 1)]);
            Equal(EntryIdOf(9), await tails.LatestAsync(Conversation(1), Never<EntryId?>), "applied");
        });

        yield return Case(prefix + "knows-a-new-root-conversation-is-empty-and-a-fork-only-once-read-or-written", async () =>
        {
            var tails = new ConversationTails();
            tails.Apply([
                new ConversationWrite(new ConversationRecord(Conversation(2))),
                new ConversationWrite(new ConversationRecord(Conversation(3), new ConversationParent(Conversation(2), EntryIdOf(4)))),
            ]);
            Equal(null, await tails.LatestAsync(Conversation(2), Never<EntryId?>), "root latest");
            Equal(null, await tails.MarkerAsync(Conversation(2), null, Never<EntryRecord?>), "root marker");
            Equal(EntryIdOf(4), await tails.LatestAsync(Conversation(3), () => Task.FromResult<EntryId?>(EntryIdOf(4))), "fork");
        });

        yield return Case(prefix + "answers-a-cutoff-from-the-newest-marker-only-at-or-after-it", async () =>
        {
            var tails = new ConversationTails();
            tails.Apply([new ConversationWrite(new ConversationRecord(Conversation(1))), Entry(10, 1), Entry(11, 1, 10)]);
            var marker = await tails.MarkerAsync(Conversation(1), null, Never<EntryRecord?>);
            Equal(EntryIdOf(11), marker?.Id, "marker");
            Equal(EntryIdOf(11), (await tails.MarkerAsync(Conversation(1), EntryIdOf(12), Never<EntryRecord?>))?.Id, "later cutoff");
            // An older cutoff may have an older marker: read, and not kept.
            var reads = 0;
            Task<EntryRecord?> Older()
            {
                reads++;
                return Task.FromResult<EntryRecord?>(null);
            }
            Equal(null, await tails.MarkerAsync(Conversation(1), EntryIdOf(10), Older), "older cutoff");
            Equal(null, await tails.MarkerAsync(Conversation(1), EntryIdOf(10), Older), "older cutoff again");
            Equal(2, reads, "reads");
            Equal(EntryIdOf(11), (await tails.MarkerAsync(Conversation(1), null, Never<EntryRecord?>))?.Id, "newest still kept");
        });

        yield return Case(prefix + "keeps-a-bounded-number-of-conversations-fewer-with-markers", () =>
        {
            var tails = new ConversationTails();
            for (var id = 1; id <= 5000; id++) tails.Apply([Entry(2 * id, id), Entry(2 * id + 1, id, 2 * id)]);
            Equal(4096, tails.Count, "size");
            Equal(256, tails.Markers, "markers");
        });

        yield return Case(prefix + "freezes-the-markers-it-keeps", async () =>
        {
            var tails = new ConversationTails();
            var write = Entry(2, 1, 1);
            tails.Apply([write]);
            Check(ReferenceEquals(write.Value, await tails.MarkerAsync(Conversation(1), null, Never<EntryRecord?>)), "kept marker: not the written entry");
            // Frozen by construction: an entry record cannot change.
            CachedStorageTests.InitOnly(typeof(EntryRecord), "entry record");
        });
    }
}
