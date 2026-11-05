// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-context-range.test.ts, new in Pi v1.5.0 (e5c6182ba, sync
// area publication-caches). A kept context range is current only if no entry was committed while its scan ran off the line, so a later
// publication does not advance it past the entry it missed (Harness/Context.cs). Verbatim apart from these: PausedStorage is a decorator
// over MemoryStorage (sealed here) whose first scan after Pause is set waits, once it read its page, until released; toEqual of views is
// SameView (HarnessConversationsSupport.cs).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessContextRangeCases() => ContextRangeTests.Cases("harness.context-range.");
}

internal static class ContextRangeTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Memory storage whose first entry scan after <see cref="Pause"/> is set waits, once it read its page, until
    /// <see cref="Release"/> completes (upstream <c>PausedStorage</c>).</summary>
    private sealed class PausedStorage() : ControlledStorage(new MemoryStorage())
    {
        private int pause;

        public bool Pause
        {
            set => Volatile.Write(ref pause, value ? 1 : 0);
        }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Page<EntryRecord>> ScanEntriesAsync(EntryQuery query, int limit, JsonObjectValue? cursor, DurableContext context)
        {
            var page = await base.ScanEntriesAsync(query, limit, cursor, context);
            if (Interlocked.Exchange(ref pause, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task;
            }
            return page;
        }
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "is-not-advanced-when-an-entry-was-committed-while-its-scan-ran-off-the-line", async () =>
        {
            var storage = new PausedStorage();
            var session = new Session(storage);
            var conversation = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);
            await session.CommitAsync(tx => tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = "first" }), Context);
            storage.Pause = true;
            var read = ConversationContext.ReadContextFromAsync(session, storage, conversation.Id, Context, null, null);
            await storage.Entered.Task;
            await session.CommitAsync(tx => tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = "missed" }), Context);
            storage.Release.TrySetResult();
            var (_, range) = await read;
            Equal(false, range?.Current, "current");
            var last = await session.CommitAsync(tx => tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = "last" }), Context);
            // Not current: left as it is, so the next read extends it from Storage instead of skipping "missed".
            Check(ReferenceEquals(range, ConversationContext.AdvanceRange(range!, [last])), "advanced: not the same range");
            var again = await ConversationContext.ReadContextFromAsync(session, storage, conversation.Id, Context, null, range);
            var fresh = await ConversationContext.ReadContextAsync(session, storage, conversation.Id, Context);
            Equal("first,missed,last", string.Join(",", again.View.Entries.Select(entry => entry.Kind)), "kinds");
            HarnessConversationTests.SameView(fresh, again.View, "view");
            Equal(true, again.Range?.Current, "current again");
            await session.CloseAsync(Context);
        });
    }
}
