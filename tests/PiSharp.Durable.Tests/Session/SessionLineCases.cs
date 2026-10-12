// Authored for P3 (no upstream counterpart): the .NET guarantees of the Session line that upstream's single JavaScript thread provides
// implicitly (see the concurrency model in src/PiSharp.Durable/Session/Session.cs).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static Program;

internal static partial class SessionTests
{
    private static IEnumerable<(string, Func<Task>)> Line(string prefix, Func<IStorage> storageFactory)
    {
        yield return Case(prefix + "runs-commits-one-at-a-time-in-enqueue-order-across-threads", async () =>
        {
            var (_, session, publications) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var active = 0;
            var overlapped = false;
            var enqueued = new List<int>[8];
            var commits = new List<Task<EntryRecord>>();
            using var start = new ManualResetEventSlim();
            var threads = Enumerable.Range(0, enqueued.Length).Select(thread => new Thread(() =>
            {
                enqueued[thread] = [];
                start.Wait();
                for (var index = 0; index < 50; index++)
                {
                    var value = thread * 1000 + index;
                    var commit = session.CommitAsync(async tx =>
                    {
                        if (Interlocked.Increment(ref active) != 1) overlapped = true;
                        await Task.Yield();
                        var entry = await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "line", Data = value });
                        Interlocked.Decrement(ref active);
                        return entry;
                    }, Context);
                    lock (commits) commits.Add(commit);
                }
            })).ToList();
            foreach (var thread in threads) thread.Start();
            start.Set();
            foreach (var thread in threads) thread.Join();
            Task<EntryRecord>[] all;
            lock (commits) all = [.. commits];
            await Task.WhenAll(all);
            Check(!overlapped, "commit callbacks never overlap");
            // Entry IDs follow commit order, so each thread's commits ran in the order that thread enqueued them.
            foreach (var group in all.Select(task => task.Result).GroupBy(entry => (int)N(entry.Data) / 1000))
            {
                var ordered = group.OrderBy(entry => entry.Id.Value).Select(entry => (int)N(entry.Data)).ToList();
                Seq(ordered.Order(), ordered, $"thread {group.Key} order");
            }
            List<long> sequences;
            lock (publications) sequences = [.. publications.Select(publication => publication.Seq.Value)];
            Equal(401, sequences.Count, "publications");
            Check(sequences.Zip(sequences.Skip(1)).All(pair => pair.First < pair.Second), "sequences increase in publication order");
        });

        yield return Case(prefix + "completes-caller-tasks-off-the-line", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            var conversationId = await CreateConversation(session);
            var first = session.CommitAsync(tx => tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "first" }), Context);
            // A continuation that blocks on a later commit does not hold the line, so the later commit still runs.
            var second = first.ContinueWith(_ =>
                session.CommitAsync(tx => tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "second" }), Context).GetAwaiter().GetResult(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var entry = await second.WaitAsync(TimeSpan.FromSeconds(30));
            Equal("second", entry.Kind, "second commit");
        });

        yield return Case(prefix + "starts-watch-delivery-in-its-own-line-job", async () =>
        {
            var doc = Documents.DefineSessionDoc("line.watch", 1, () => O("{\"value\":0}"));
            var (_, session, _) = Open(storageFactory);
            await session.CommitAsync(tx => tx.DocAsync(doc), Context);
            var watch = (await session.WatchDocAsync(doc, Context))!;
            await session.CommitAsync(async tx => (await tx.DocAsync(doc))["value"] = 1, Context);
            var gate = new Deferred();
            var held = session.CommitAsync(async _ => await gate.Task, Context);
            var delivered = new Deferred(inline: false);
            watch.Start((_, _, _) =>
            {
                delivered.Resolve();
                return Task.CompletedTask;
            });
            // The delivery is queued behind the held commit and runs once it settles.
            await Task.Yield();
            Check(!delivered.Task.IsCompleted, "delivery waits for the line");
            gate.Resolve();
            await held;
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await watch.StopAsync();
        });
    }
}
