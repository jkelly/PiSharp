// Pi v1.5.0 sync (area deferred-commits): streams.an-agent-feed-shows-deferred-partials-before-they-are-stored. Partial commits are deferred
// (durable@1.5.0 Breaking Changes#10): Storage, and so the session's change notifications (SubscribeCommits), get them later, while an agent
// feed (views, which follow memory) shows each at once. With a window that never ends, nothing is stored while the answer streams.
using PiSharp.Durable;
using PiSharp.Durable.Hosting;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> StreamCases() =>
    [
        Case("streams.an-agent-feed-starts-with-a-snapshot", async () =>
        {
            await using var device = await TestDevice.OpenAsync("streams-agent");
            var session = await device.CreateAsync("feed");
            await using var feed = await session.WatchAgentAsync(1);
            Check(feed.Snapshot.Type == "snapshot", "the first event is a snapshot");
            var send = session.SendAsync(1, new SendRequest("watch me", "watch-1") { Wait = Answer });
            var seen = new List<string>();
            using var limit = new CancellationTokenSource(Answer);
            await foreach (var batch in feed.ReadAllAsync(limit.Token))
            {
                seen.AddRange(batch.Select(item => item.Type));
                if (seen.Contains("run_end")) break;
            }
            Check(seen.Contains("run_start") && seen.Contains("message_end"), "the run's events: " + string.Join(", ", seen));
            Equal("Echo: watch me", (await send).AnswerText, "answer");
            await ThrowsAsync<HostNotFoundException>(() => session.WatchAgentAsync(99999), "an unknown agent");
        }),

        Case("streams.an-agent-feed-shows-deferred-partials-before-they-are-stored", async () =>
        {
            await using var device = await TestDevice.OpenAsync("streams-deferred", new DeviceSetup
            {
                Configure = options => options with
                {
                    Settings = new HarnessSettings { Progress = new ProgressSettings { PartialIntervalMs = 0, PersistWithinMs = double.PositiveInfinity } },
                },
            });
            var session = await device.CreateAsync("deferred");
            await using var feed = await session.WatchAgentAsync(1);
            var stored = 0;
            using var changes = session.SubscribeChanges(change =>
            {
                if (change.Kind == DeviceChangeKind.Session) Interlocked.Increment(ref stored);
            });
            var words = string.Join(" ", Enumerable.Range(1, 12).Select(index => $"w{index}"));
            var send = session.SendAsync(1, new SendRequest("slow: " + words, "deferred-1") { Wait = Answer });
            // The stored commits counted at each partial the feed shows, and the partial texts.
            var updates = new List<(int Stored, string Text)>();
            using var limit = new CancellationTokenSource(Answer);
            await foreach (var batch in feed.ReadAllAsync(limit.Token))
            {
                foreach (var item in batch.Where(item => item.Type == "message_update"))
                {
                    var json = item.ToJson().ToJson();
                    updates.Add((Volatile.Read(ref stored), json));
                }
                if (batch.Any(item => item.Type == "run_end")) break;
            }
            Check(updates.Count >= 2, $"the feed shows partials as they stream: {updates.Count}");
            Check(updates.Any(update => update.Text.Contains("w10", StringComparison.Ordinal)), "the feed shows the late partials");
            // Between the first partial and the last, nothing reached Storage: the feed showed held commits.
            Equal(updates[0].Stored, updates[^1].Stored, "stored commits while the answer streamed");
            Equal(words, (await send).AnswerText, "answer");
        }),

        Case("streams.a-late-feed-sees-the-finished-run-in-its-snapshot", async () =>
        {
            await using var device = await TestDevice.OpenAsync("streams-late");
            var session = await device.CreateAsync("late");
            await session.SendAsync(1, new SendRequest("earlier", "late-1") { Wait = Answer });
            await using var feed = await session.WatchAgentAsync(1);
            var json = feed.Snapshot.ToJson().ToJson();
            Check(json.Contains("Echo: earlier"), "nothing is replayed; the snapshot holds the transcript");
        }),

        Case("streams.the-task-graph-feed", async () =>
        {
            var gate = new TestGate();
            await using var device = await TestDevice.OpenAsync("streams-graph", new DeviceSetup { Registry = RegistryWith(gate) });
            var session = await device.CreateAsync("graph");
            await using var feed = await session.WatchTaskGraphAsync();
            Equal(0, feed.Initial.Tasks.Count, "no live task at first");
            await session.SendAsync(1, new SendRequest("tool: hang {}", "graph-1"));
            using var limit = new CancellationTokenSource(Answer);
            await foreach (var graph in feed.ReadAllAsync(limit.Token))
                if (graph.Tasks.Values.Any(node => node.Kind == "pi.tool")) break;
            gate.Release.TrySetResult();
        }),

        Case("streams.device-changes-are-coalesced", async () =>
        {
            await using var device = await TestDevice.OpenAsync("streams-device");
            var session = await device.CreateAsync("changes");
            using var limit = new CancellationTokenSource(Answer);
            await using var changes = device.Host.WatchChangesAsync(TimeSpan.FromMilliseconds(100), limit.Token).GetAsyncEnumerator(limit.Token);
            var next = changes.MoveNextAsync();
            await device.Host.RenameSessionAsync(session.Id, "one");
            await device.Host.RenameSessionAsync(session.Id, "two");
            Check(await next, "a batch");
            Equal(1, changes.Current.Count, "two renames in one batch: " + string.Join(", ", changes.Current));
            Equal(new DeviceChange(DeviceChangeKind.Catalog, session.Id), changes.Current.Single(), "a catalog change");

            next = changes.MoveNextAsync();
            await session.SendAsync(1, new SendRequest("commit something", "changes-1"));
            Check(await next, "another batch");
            Check(changes.Current.Contains(new DeviceChange(DeviceChangeKind.Session, session.Id)), "a session change");
        }),
    ];
}
