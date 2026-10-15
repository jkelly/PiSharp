// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/session-document-retention.test.ts. The Session's document
// cache is bounded by use: unattached documents leave it after the retention period and load from Storage again; documents with a watch or a
// document state never leave it. Verbatim apart from the adaptations in SessionSupport.cs, HarnessSupport.cs and ChatSupport.cs, and these:
// - A retention is a double?, so upstream's `"soon" as unknown as number` (a value of the wrong type) has no counterpart; NaN and -5 remain.
// - "keeps no timer where timers cannot be unreferenced, and evicts on commits": .NET timers never keep a process alive, so the Session
//   always keeps one (see Session.cs) and has no timerless mode to enter. The case checks the part that holds either way: the end of each
//   commit evicts what expired, before any timer could fire.
// - "writes after a reload continue the stored deltas and bases": the batches are ControlledStorage.Commits, as upstream's patched commit
//   records them.
// - "gives the same events ...": the run flushes the Session line before it stops the event stream, as the inbox cases do (a C# wait
//   resumes on its own listener, upstream's after all listeners and the deliveries they queued).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionDocumentRetentionCases() => DocumentRetentionTests.Cases("session.document-retention.");
}

internal static class DocumentRetentionTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly ConversationDocToken CounterDoc =
        Documents.DefineConversationDoc("test.retention.counter", 1, DocumentFork.Initial, () => O("""{"count":0}"""));
    private static readonly RewindableConversationDocToken RewindableDoc =
        Documents.DefineRewindableConversationDoc("test.retention.rewindable", 1, DocumentFork.AsOf, () => O("""{"count":0}"""));
    /// <summary>A third-party conversation family: one member per event.</summary>
    private static readonly ConversationDocFamilyToken EventDoc =
        Documents.DefineConversationDocFamily("test.retention.event", 1, DocumentFork.Initial, seq => Obj(("seq", seq)));
    private static readonly SessionDocToken SessionCounterDoc = Documents.DefineSessionDoc("test.retention.session-counter", 1, () => O("""{"count":0}"""));
    /// <summary>A session document whose checkpoint predicate throws, so the commit that changes it fails while it settles.</summary>
    private static readonly SessionDocToken Failing = Documents.DefineSessionDoc("test.retention.failing", 1, () => O("""{"count":0}"""),
        checkpointWhen: (_, _, _) => throw new InvalidOperationException("checkpoint failed"));
    /// <summary>A conversation document any Harness test can read.</summary>
    private static readonly ConversationDocToken TestDoc =
        Documents.DefineConversationDoc("test.retention.harness", 1, DocumentFork.Initial, () => O("""{"count":0}"""));

    /// <summary>The default retention of this run, as Program.cs set it from PI_DURABLE_DOCUMENT_RETENTION_MS.</summary>
    private static double DefaultForRun => SetupDocumentRetention.DefaultForRun;

    private sealed class Clock
    {
        public double Now = 1_000;
    }

    private sealed record Opened(ControlledStorage Storage, Session Session, Clock Clock);

    /// <summary>A Session with a controllable clock and retention.</summary>
    private static Opened Open(double retentionMs)
    {
        var clock = new Clock();
        var storage = new ControlledStorage(new MemoryStorage()) { KeepOpenForReopen = true };
        var session = new Session(storage, () => clock.Now, () => retentionMs);
        return new Opened(storage, session, clock);
    }

    /// <summary>Lets the line run what is queued on it: an eviction job, then return.</summary>
    private static Task SettleLine(Session session) => session.ReadOnLineAsync(() => Task.FromResult(true));

    private static Task Bump(Session session, ConversationId conversationId, double by = 1) =>
        session.CommitAsync(async tx =>
        {
            var draft = await tx.DocAsync(CounterDoc, conversationId);
            draft["count"] = N(draft["count"]) + by;
        }, Context);

    private static Task TouchSessionCounter(Session session) => session.CommitAsync(async tx => await tx.DocAsync(SessionCounterDoc), Context);

    private static Task NoOp(Session session) => session.CommitAsync(_ => Task.CompletedTask, Context);

    private static async Task Evicted(Session session)
    {
        await Eventually(async () =>
        {
            await SettleLine(session);
            return session.DocumentCacheSizes.Cached == 0;
        });
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "evicts-an-unattached-document-once-its-retention-passed-and-loads-it-again-with-the-same-value", async () =>
        {
            var (storage, session, clock) = Open(60_000);
            var conversationId = await CreateConversation(session);
            await Bump(session, conversationId, 3);
            Check(session.DocumentCacheSizes.Cached > 0, "cached");
            // Not yet: inside the retention period.
            clock.Now += 59_000;
            await Bump(session, conversationId, 0);
            await SettleLine(session);
            Check(session.DocumentCacheSizes.Cached > 0, "still cached");
            // Past it, with no use since: the next commit evicts it.
            clock.Now += 61_000;
            await NoOp(session);
            await TouchSessionCounter(session);
            await SettleLine(session);
            var reads = storage.DocumentReadCount;
            SameJson("""{"count":3}""", await session.SnapshotAsync(CounterDoc, conversationId, Context), "reloaded");
            Check(storage.DocumentReadCount > reads, "read from Storage");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "evicts-with-retention-0-right-after-use-on-the-line", async () =>
        {
            var (_, session, _) = Open(0);
            var conversationId = await CreateConversation(session);
            await Bump(session, conversationId);
            await Evicted(session);
            SameJson("""{"count":1}""", await session.SnapshotAsync(CounterDoc, conversationId, Context), "reloaded");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "never-evicts-a-document-a-watch-or-a-document-state-observes-and-evicts-it-once-the-last-detaches", async () =>
        {
            var (storage, session, clock) = Open(1_000);
            var conversationId = await CreateConversation(session);
            await Bump(session, conversationId);
            var watch = (await session.WatchDocAsync(CounterDoc, conversationId, Context))!;
            var state = (await session.DocumentStateAsync(CounterDoc, conversationId, Context))!;
            var seen = new SyncList<double>();
            watch.Start((value, _, _) =>
            {
                seen.Add(value is null ? -1 : N(value["count"]));
                return Task.CompletedTask;
            });
            clock.Now += 1_000_000;
            await Bump(session, conversationId);
            await SettleLine(session);
            // Still cached: a read long after its retention passed loads nothing from Storage.
            Equal(2.0, N(state.Value!["count"]), "state");
            await Eventually(() => seen.Snapshot().Contains(2));
            var reads = storage.DocumentReadCount;
            clock.Now += 1_000_000;
            await TouchSessionCounter(session);
            await SettleLine(session);
            SameJson("""{"count":2}""", await session.SnapshotAsync(CounterDoc, conversationId, Context), "snapshot");
            Equal(reads, storage.DocumentReadCount, "reads");
            await watch.StopAsync();
            clock.Now += 1_000_000;
            await Bump(session, conversationId);
            await SettleLine(session);
            Equal(3.0, N(state.Value!["count"]), "state after the watch stopped");
            state.Dispose();
            clock.Now += 1_000_000;
            await NoOp(session);
            await TouchSessionCounter(session);
            await SettleLine(session);
            // The session document was just used; the counter is gone.
            Equal(1, session.DocumentCacheSizes.Cached, "cached");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "never-takes-a-tracker-from-a-transaction-that-holds-it", async () =>
        {
            var (_, session, clock) = Open(0);
            var conversationId = await CreateConversation(session);
            await Bump(session, conversationId);
            var inside = new Deferred();
            var release = new Deferred();
            var commit = session.CommitAsync(async tx =>
            {
                var draft = await tx.DocAsync(CounterDoc, conversationId);
                inside.Resolve();
                await release.Task;
                draft["count"] = N(draft["count"]) + 10;
            }, Context);
            await inside.Task;
            // Retention passes while the transaction holds the document; eviction waits on the line behind it.
            clock.Now += 1_000_000;
            release.Resolve();
            await commit;
            SameJson("""{"count":11}""", await session.SnapshotAsync(CounterDoc, conversationId, Context), "after the held commit");
            await Bump(session, conversationId);
            SameJson("""{"count":12}""", await session.SnapshotAsync(CounterDoc, conversationId, Context), "after another bump");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "writes-after-a-reload-continue-the-stored-deltas-and-bases", async () =>
        {
            var (storage, session, _) = Open(0);
            var conversationId = await CreateConversation(session);
            for (var n = 0; n < 5; n++)
            {
                await Bump(session, conversationId);
                await SettleLine(session);
            }
            SameJson("""{"count":5}""", await session.SnapshotAsync(CounterDoc, conversationId, Context), "count");
            // Every bump after the first is a change of the same incarnation, never a second creation.
            var creations = storage.Commits.SelectMany(batch => batch)
                .Count(write => write is DocumentCreateWrite create && create.Record.Kind == CounterDoc.Definition.Kind);
            Equal(1, creations, "creations");
            await session.CloseAsync(Context);
            // And a fresh Session over the same storage reads the same value.
            var reopened = new Session(storage.Reopen(), null, () => 0);
            SameJson("""{"count":5}""", await reopened.SnapshotAsync(CounterDoc, conversationId, Context), "reopened");
            await reopened.CloseAsync(Context);
            await storage.Inner.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-history-and-forks-of-rewindable-documents-after-eviction", async () =>
        {
            var (_, session, _) = Open(0);
            var conversationId = await CreateConversation(session);
            var first = await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(RewindableDoc, conversationId))["count"] = 1;
                return (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "note", Data = JsonValue.Null })).Id;
            }, Context);
            await SettleLine(session);
            var second = await session.CommitAsync(async tx =>
            {
                (await tx.DocAsync(RewindableDoc, conversationId))["count"] = 2;
                return (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "note", Data = JsonValue.Null })).Id;
            }, Context);
            await SettleLine(session);
            SameJson("""{"count":1}""", await session.SnapshotAsOfAsync(RewindableDoc, conversationId, first, Context), "as of first");
            SameJson("""{"count":2}""", await session.SnapshotAsOfAsync(RewindableDoc, conversationId, second, Context), "as of second");
            var fork = await session.CommitAsync(async tx => (await tx.ForkConversationAsync(conversationId, first, ConversationOwnership.Ownerless)).Id,
                Context);
            await SettleLine(session);
            SameJson("""{"count":1}""", await session.SnapshotAsync(RewindableDoc, fork, Context), "fork");
            SameJson("""{"count":2}""", await session.SnapshotAsync(RewindableDoc, conversationId, Context), "parent");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "bounds-a-third-party-family-with-a-member-per-event", async () =>
        {
            var (_, session, _) = Open(0);
            var conversationId = await CreateConversation(session);
            for (var seq = 0; seq < 200; seq++)
            {
                var member = seq;
                await session.CommitAsync(async tx => await tx.DocAsync(EventDoc, conversationId, member.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    member), Context);
            }
            await Evicted(session);
            SameJson("""{"seq":123}""", await session.SnapshotAsync(EventDoc, conversationId, "123", Context), "member");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-the-cache-of-a-long-lived-session-bounded-across-many-conversations", async () =>
        {
            var (_, session, clock) = Open(10_000);
            for (var n = 0; n < 300; n++)
            {
                var conversationId = await CreateConversation(session);
                await Bump(session, conversationId);
                clock.Now += 1_000;
            }
            await SettleLine(session);
            // Only what was used in the last 10 s, about ten conversations' worth.
            Check(session.DocumentCacheSizes.Cached <= 12, $"cached {session.DocumentCacheSizes.Cached}");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "never-changes-the-outcome-of-a-commit-when-the-clock-throws", async () =>
        {
            var clock = new Clock();
            var fail = false;
            var storage = new ControlledStorage(new MemoryStorage());
            var session = new Session(storage, () => Volatile.Read(ref fail) ? throw new InvalidOperationException("clock") : clock.Now, () => 0);
            var conversationId = await CreateConversation(session);
            Volatile.Write(ref fail, true);
            await Bump(session, conversationId, 42);
            Volatile.Write(ref fail, false);
            SameJson("""{"count":42}""", await session.SnapshotAsync(CounterDoc, conversationId, Context), "count");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "evicts-after-a-commit-whose-settlement-fails", async () =>
        {
            var (_, session, clock) = Open(10_000);
            var conversationId = await CreateConversation(session);
            await Bump(session, conversationId);
            // Created first: the predicate runs only for changes to an existing document.
            await session.CommitAsync(async tx => await tx.DocAsync(Failing), Context);
            await SettleLine(session);
            // Retention passes inside a commit whose checkpoint predicate throws while it settles.
            await RejectsAsync(() => session.CommitAsync(async tx =>
            {
                var draft = await tx.DocAsync(Failing);
                draft["count"] = N(draft["count"]) + 1;
                clock.Now += 1_000_000;
            }, Context), "checkpoint failed", "commit");
            Equal(0, session.DocumentCacheSizes.Cached, "cached");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "evicts-promptly-once-the-retention-is-shortened", async () =>
        {
            var retention = 600_000.0;
            var storage = new ControlledStorage(new MemoryStorage());
            var clock = new Clock();
            var session = new Session(storage, () => clock.Now, () => Volatile.Read(ref retention));
            var conversationId = await CreateConversation(session);
            await Bump(session, conversationId);
            Volatile.Write(ref retention, 0);
            clock.Now += 2_000;
            // A read, no commit: the sweep pending for ten minutes from now moves up.
            await session.SnapshotAsync(CounterDoc, conversationId, Context);
            await Evicted(session);
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "does-not-keep-a-replacement-cached-for-an-observer-of-the-retired-incarnation", async () =>
        {
            var (_, session, _) = Open(0);
            var conversationId = await CreateConversation(session);
            await Bump(session, conversationId);
            var state = (await session.DocumentStateAsync(CounterDoc, conversationId, Context))!;
            await session.CommitAsync(tx => tx.RetireDocAsync(CounterDoc, conversationId), Context);
            Check(state.Value is null, "retired");
            await Bump(session, conversationId, 5);
            await Evicted(session);
            SameJson("""{"count":5}""", await session.SnapshotAsync(CounterDoc, conversationId, Context), "replacement");
            state.Dispose();
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "leaves-no-pin-behind-when-an-observers-load-loses-a-race-with-close", async () =>
        {
            var (storage, session, _) = Open(0);
            var conversationId = await CreateConversation(session);
            await Bump(session, conversationId);
            await Evicted(session);
            var gate = storage.HoldFindDocument();
            var attaching = session.DocumentStateAsync(CounterDoc, conversationId, Context);
            await gate.Entered;
            var closing = session.CloseAsync(Context);
            gate.Release();
            await ThrowsAsync<Exception>(() => attaching, "attaching");
            await closing;
            Equal((0, 0, 0), (session.DocumentCacheSizes.Cached, session.DocumentCacheSizes.Idle, session.DocumentCacheSizes.Attached), "sizes");
            await storage.Inner.CloseAsync(Context);
        });

        yield return Case(prefix + "evicts-on-its-timer-without-a-commit-and-tolerates-a-detach-called-twice", async () =>
        {
            var storage = new ControlledStorage(new MemoryStorage());
            var session = new Session(storage, null, () => 20);
            var conversationId = await CreateConversation(session);
            await Bump(session, conversationId);
            var state = (await session.DocumentStateAsync(CounterDoc, conversationId, Context))!;
            state.Dispose();
            state.Dispose();
            var until = DateTime.UtcNow.AddSeconds(2);
            while (session.DocumentCacheSizes.Cached > 0 && DateTime.UtcNow < until) await Task.Delay(10);
            Equal(0, session.DocumentCacheSizes.Cached, "cached");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "migrates-a-document-again-after-its-eviction", async () =>
        {
            var v1 = Documents.DefineSessionDoc("test.retention.migrating", 1, () => O("""{"count":1}"""));
            var migrations = 0;
            var v2 = Documents.DefineSessionDoc("test.retention.migrating", 2, () => O("""{"count":0,"migrated":true}"""),
                migrate: (value, _) =>
                {
                    Interlocked.Increment(ref migrations);
                    return Obj(("count", value["count"]), ("migrated", true));
                });
            var (_, session, _) = Open(0);
            await session.CommitAsync(async tx => await tx.DocAsync(v1), Context);
            await SettleLine(session);
            SameJson("""{"count":1,"migrated":true}""", await session.SnapshotAsync(v2, Context), "first");
            await SettleLine(session);
            SameJson("""{"count":1,"migrated":true}""", await session.SnapshotAsync(v2, Context), "second");
            // Not written back, so each cold load migrates again.
            Equal(2, Volatile.Read(ref migrations), "migrations");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "treats-a-retention-that-is-not-a-number-of-milliseconds-as-the-default-and-infinity-as-never", async () =>
        {
            // The default as shipped, also in the suite run that forces another.
            Session.SetDefaultDocumentRetention(600_000);
            try
            {
                foreach (var retention in new[] { double.NaN, -5 })
                {
                    var (_, session, clock) = Open(retention);
                    var conversationId = await CreateConversation(session);
                    await Bump(session, conversationId);
                    clock.Now += 60_000;
                    await NoOp(session);
                    await SettleLine(session);
                    // Not dropped at once, as a retention of 0 would: the ten-minute default holds it.
                    Equal(1, session.DocumentCacheSizes.Cached, $"cached with {retention}");
                    await session.CloseAsync(Context);
                }
                var (_, forever, foreverClock) = Open(double.PositiveInfinity);
                var id = await CreateConversation(forever);
                await Bump(forever, id);
                foreverClock.Now += 1e15;
                await NoOp(forever);
                await SettleLine(forever);
                Equal(1, forever.DocumentCacheSizes.Cached, "cached with Infinity");
                await forever.CloseAsync(Context);
            }
            finally
            {
                Session.SetDefaultDocumentRetention(DefaultForRun);
            }
        });

        yield return Case(prefix + "keeps-no-timer-where-timers-cannot-be-unreferenced-and-evicts-on-commits", async () =>
        {
            var (_, session, clock) = Open(1_000);
            var conversationId = await CreateConversation(session);
            await Bump(session, conversationId);
            await Bump(session, conversationId);
            clock.Now += 10_000;
            await NoOp(session);
            await SettleLine(session);
            Equal(0, session.DocumentCacheSizes.Cached, "cached");
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "follows-a-harness-settings-change-at-the-next-use", async () =>
        {
            Session.SetDefaultDocumentRetention(600_000);
            try
            {
                var setup = Setup();
                var (harness, root) = await OpenChat(new MemoryStorage(), setup);
                await root.CommitAsync(async tx =>
                {
                    var draft = await tx.DocAsync(TestDoc, root.Id);
                    draft["count"] = N(draft["count"]) + 1;
                }, Context);
                var session = (Session)harness;
                Check(session.DocumentCacheSizes.Cached > 0, "cached");
                setup.DocumentRetentionMs = 0;
                await harness.SnapshotAsync(TestDoc, root.Id, Context);
                await Evicted(session);
                await harness.CloseAsync(Context);
            }
            finally
            {
                Session.SetDefaultDocumentRetention(DefaultForRun);
            }
        });

        yield return Case(prefix + "gives-the-same-events-with-documents-evicted-right-after-use-as-with-documents-kept", async () =>
        {
            static async Task<List<string>> Run(double documentRetentionMs)
            {
                var setup = Setup();
                setup.DocumentRetentionMs = documentRetentionMs;
                setup.Progress = new ProgressSettings { OutputIntervalMs = 0 };
                AddTool(setup.Registry, Define.Tool(new ToolRegistration
                {
                    Name = "steps",
                    Description = "Streams three steps",
                    Parameters = O("""{"type":"object","properties":{}}"""),
                    Execute = async (_, api, _) =>
                    {
                        foreach (var step in new[] { "a\n", "b\n", "c\n" })
                        {
                            api.Output(step);
                            await Task.Delay(5);
                        }
                        return new ToolExecutionResult();
                    },
                }));
                setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("steps", JsonObjectValue.Empty, "s1")], stopReason: "toolUse"),
                    FauxAssistantMessage([FauxText("done")]));
                var (harness, root) = await OpenChat(new MemoryStorage(), setup);
                var stream = await harness.WatchEventsAsync(root.Id, Context);
                var events = new SyncList<AgentEvent>();
                stream.Start((batch, _) =>
                {
                    foreach (var item in batch) events.Add(item);
                    return Task.CompletedTask;
                });
                await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
                await harness.WaitForIdleAsync(Context);
                // A C# wait resumes on its own listener, upstream's after all listeners and the deliveries they queued: flush the line so
                // the last commit's events are delivered before the stream stops.
                await ((Session)harness).FlushLineAsync();
                await stream.StopAsync();
                await harness.CloseAsync(Context);
                // Ids and times differ between runs; the sequence of event types and tool outputs must not.
                return [.. events.Snapshot().Select(item => item.Type == "tool_execution_update"
                    ? "update:" + (item.ToJson().TryGetValue("output", out var output) ? output.ToJson() : "undefined")
                    : item.Type)];
            }
            Seq(await Run(600_000), await Run(0), "events");
        });

        yield return Case(prefix + "keeps-a-reloaded-newer-version-cached-while-an-observer-of-the-older-one-is-attached", async () =>
        {
            var v1 = Documents.DefineSessionDoc("test.retention.versioned", 1, () => O("""{"count":3}"""));
            var v2 = Documents.DefineSessionDoc("test.retention.versioned", 2, () => O("""{"count":0,"v2":true}"""),
                migrate: (value, _) => Obj(("count", value["count"]), ("v2", true)));
            var (_, session, _) = Open(0);
            await session.CommitAsync(async tx => await tx.DocAsync(v1), Context);
            var state = (await session.DocumentStateAsync(v1, Context))!;
            SameJson("""{"count":3,"v2":true}""", await session.SnapshotAsync(v2, Context), "v2");
            await SettleLine(session);
            Equal(1, session.DocumentCacheSizes.Cached, "cached while attached");
            state.Dispose();
            await Evicted(session);
            await session.CloseAsync(Context);
        });

        yield return Case(prefix + "queues-one-sweep-job-not-one-per-document-while-a-commit-holds-the-line", async () =>
        {
            var (_, session, _) = Open(0);
            var conversationIds = new List<ConversationId>();
            for (var n = 0; n < 50; n++)
            {
                var id = await CreateConversation(session);
                await Bump(session, id);
                conversationIds.Add(id);
            }
            await Evicted(session);
            var before = session.DocumentCacheSizes.Sweeps;
            // One commit loads 50 evicted documents from storage, each touched as it loads; the sweep job stays pending while the line is held.
            await session.CommitAsync(async tx =>
            {
                foreach (var id in conversationIds)
                {
                    var draft = await tx.DocAsync(CounterDoc, id);
                    draft["count"] = N(draft["count"]) + 1;
                    await Task.Yield();
                }
            }, Context);
            await SettleLine(session);
            Equal(0, session.DocumentCacheSizes.Cached, "cached");
            Check(session.DocumentCacheSizes.Sweeps - before <= 2, $"sweeps {session.DocumentCacheSizes.Sweeps - before}");
            await session.CloseAsync(Context);
        });
    }
}
