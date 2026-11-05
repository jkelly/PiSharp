// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-idle-scopes.test.ts (Pi v1.5.0 sync, area
// scheduler-idle; added by 35dfa7573). Idle checks without per-commit scans: a task change settles only the conversations above it (idle
// waiters and kept context ranges), expiry runs oldest first from an ordered index, and Wake() reads only sleeping tasks while work is
// active. Every case checks the incremental idle state against a full recompute (DurableHarness.SchedulerIdleState, upstream
// SCHEDULER_IDLE_STATE). On MemoryStorage, as upstream, with the adaptations of HarnessSupport.cs and these:
// - Upstream keeps the gates and the read barriers at module scope and clears them after each test; here each case builds a fresh
//   IdleFixture, whose Work definition closes over it. Work races its gate with its signal through SchedulerGates.Opened.
// - idleState(harness) is IdleState(harness): upstream reads the getter right after an await, its passes having run in microtasks it has
//   already drained. Here reconcile passes, reservations and the sweep job run on the thread pool, so IdleState first waits until none is
//   scheduled or running and the line is flushed (DurableTaskScheduler.QuiesceAsync), then reads the state, as SchedulerTests.Sizes does.
//   quiet() (four flushes) is Quiet(): four Flush() turns, then the same barrier.
// - The storage Proxy counting the entry rows each scan returns is HarnessConversationTests.CountingStorage over MemoryStorage.
// - declineBackground declines background timers and arms foreground ones on the system's timers (NativeTimer.System, upstream
//   setTimeout).
// - The clock and the retention a case changes are read on thread-pool threads, so they are boxes read and written with Volatile.
// - toMatchObject compares the named fields; toEqual({...before, idleContexts: 1}) compares the whole record with that field replaced.
// - seeded(42) computes in doubles, as JavaScript numbers, so the random walk is upstream's; `live`, an insertion-ordered Map whose keys
//   are never reused, is a list.
// - wake()'s seeded records are TaskRecords whose states parse from upstream's JSON (TaskState.FromJson); its `extra` fields are `with`
//   expressions, and commitWrites is CommitWritesAsync.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Testing;
using static HarnessTests;
using static Program;
using static SchedulerTests;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessIdleScopesCases() => IdleScopesTests.Cases();
}

/// <summary>The harness.idle-scopes.* cases and their support.</summary>
internal static class IdleScopesTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>A double a case changes while the Harness reads it on other threads.</summary>
    private sealed class Box(double initial)
    {
        private double current = initial;

        public double Value
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }
    }

    /// <summary>One case's gates and read barriers, and the Work task over them.</summary>
    private sealed class IdleFixture
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Deferred> gates = new(StringComparer.Ordinal);
        /// <summary>Resolves once the named task's context read returned.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Deferred> reads = new(StringComparer.Ordinal);

        public IdleFixture()
        {
            Work = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.idle.work",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (task, runtime, ctx) =>
                    {
                        var input = (JsonObjectValue)task.Input;
                        var name = NameOf(input);
                        if (input.TryGetValue("read", out var read))
                        {
                            await runtime.ContextAsync(new ConversationId((long)((JsonNumberValue)read).Value), ctx);
                            Read(name).Resolve();
                        }
                        if (!await SchedulerGates.Opened(Gate(name).Task, runtime.Signal)) return;
                        await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx);
                    },
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
            });
        }

        /// <summary>
        /// Reads the context of conversation <c>read</c> (when given), then holds until its gate opens and completes; its abort handler ends
        /// it <c>aborted</c> (upstream <c>Work</c>).
        /// </summary>
        public DurableTask Work { get; }

        public Deferred Gate(string name) => gates.GetOrAdd(name, _ => new Deferred());

        public Deferred Read(string name) => reads.GetOrAdd(name, _ => new Deferred());

        public void OpenAll()
        {
            foreach (var gate in gates.Values) gate.Resolve();
        }
    }

    /// <summary>The idle state once the scheduler's pending passes have run (upstream <c>idleState</c>; see the file header).</summary>
    private static async Task<SchedulerIdleState> IdleState(IHarness harness)
    {
        var durable = (DurableHarness)harness;
        await durable.Scheduler.QuiesceAsync();
        return durable.SchedulerIdleState;
    }

    /// <summary>Upstream <c>quiet()</c>: flushes until scheduler passes started by the last changes settled.</summary>
    private static async Task Quiet(IHarness harness)
    {
        for (var round = 0; round < 4; round++) await Flush();
        await ((DurableHarness)harness).Scheduler.QuiesceAsync();
    }

    /// <summary>Background timers declined, as Cloudflare Workers do; foreground ones on the system's timers (upstream
    /// <c>declineBackground</c>).</summary>
    private static readonly SetTimer DeclineBackground = (callback, delayMs, options) =>
        options.Background ? null : NativeTimer.System(callback, delayMs, options);

    /// <summary>A resumed Harness running Work and its helpers (upstream <c>open</c>'s result).</summary>
    private sealed class Opened(IdleFixture fixture, IHarness harness, IConversation root, HarnessConversationTests.CountingStorage storage)
    {
        public IHarness Harness { get; } = harness;
        public IConversation Root { get; } = root;

        /// <summary>A new ownerless conversation with <paramref name="entries"/> entries.</summary>
        public async Task<IConversation> Conversation(int entries = 2)
        {
            var created = await Harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            for (var index = 0; index < entries; index++)
            {
                var at = index;
                await created.CommitAsync(tx => tx.AppendEntryAsync(created.Id, new EntryDraft { Kind = "note", Data = Obj(("index", at)) }), Context);
            }
            return created;
        }

        /// <summary>Starts Work named <paramref name="name"/> in <paramref name="into"/>, reading conversation <paramref name="read"/>, and
        /// waits for its read.</summary>
        public async Task<TaskId> Work(string name, IConversation into, ConversationId? read = null)
        {
            var input = read is { } id ? Obj(("name", name), ("read", id.Value)) : Named(name);
            var task = await into.CommitAsync(tx => tx.CreateTaskAsync(fixture.Work, input, OwnConversation(into.Id)), Context);
            if (read is not null) await fixture.Read(name).Task;
            return task;
        }

        /// <summary>Opens the gate of <paramref name="name"/> and waits for its task, <paramref name="id"/>, to end.</summary>
        public async Task End(string name, TaskId id)
        {
            fixture.Gate(name).Resolve();
            await Harness.WaitForTaskAsync(id, Context);
        }

        /// <summary>The entry rows a read of <paramref name="read"/> from a new task in <paramref name="into"/> scans.</summary>
        public async Task<int> RowsOf(string name, IConversation into, ConversationId read)
        {
            var before = storage.Rows;
            var id = await Work(name, into, read);
            var rows = storage.Rows - before;
            await End(name, id);
            return rows;
        }
    }

    /// <summary>
    /// A resumed Harness running Work, over MemoryStorage counting the entry rows each scan returns, with <paramref name="clock"/> as its
    /// time, the retention <paramref name="retention"/> holds (ten minutes without it), and background timers declined unless
    /// <paramref name="timers"/> (upstream <c>open</c>).
    /// </summary>
    private static async Task<Opened> Open(IdleFixture fixture, Box? clock = null, Box? retention = null, bool timers = false)
    {
        var storage = new HarnessConversationTests.CountingStorage(new MemoryStorage());
        var registry = Registry.Create();
        registry.Install(Define.Extension(new Extension { Name = "tasks", Tasks = [fixture.Work] }));
        var harness = await PiSharp.Durable.Harness.OpenAsync(storage, new HarnessOptions
        {
            Models = EmptyDurableModels.Instance,
            Registry = registry,
            CurrentSettings = () => new HarnessSettings { ContextRetentionMs = retention?.Value ?? 600_000 },
            Now = clock is null ? null : () => clock.Value,
            SetTimer = timers ? null : DeclineBackground,
        }, Context);
        harness.Resume();
        var root = await harness.RootAsync(Context);
        return new Opened(fixture, harness, root, storage);
    }

    private static async Task<DurableTaskStatus?> StatusOf(IHarness harness, TaskId id) => (await harness.GetTaskAsync(id, Context))?.State.Status;

    private static void Matches(SchedulerIdleState actual, string what, int? contexts = null, int? idleContexts = null, int? idleMismatches = null)
    {
        if (contexts is { } c) Equal(c, actual.Contexts, $"{what}: contexts");
        if (idleContexts is { } i) Equal(i, actual.IdleContexts, $"{what}: idleContexts");
        if (idleMismatches is { } m) Equal(m, actual.IdleMismatches, $"{what}: idleMismatches");
    }

    /// <summary>A random source with a fixed seed (upstream <c>seeded</c>), in doubles as JavaScript computes it.</summary>
    private static Func<double> Seeded(double seed)
    {
        var state = seed;
        return () =>
        {
            state = (state * 1103515245.0 + 12345.0) % 2147483648.0;
            return state / 2147483648.0;
        };
    }

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        const string prefix = "harness.idle-scopes.";

        // ─── idle waiters by scope ──────────────────────────────────────────────

        const string waiters = prefix + "idle-waiters-by-scope.";

        yield return Case(waiters + "resolve-through-owner-chains-owned-conversations-and-ended-tasks-between-ignoring-background-work", async () =>
        {
            var fixture = new IdleFixture();
            var opened = await Open(fixture);
            var (harness, root) = (opened.Harness, opened.Root);
            var tree = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Work, Named("p"), OwnConversation());
                var b = await tx.CreateTaskAsync(fixture.Work, Named("b"), OwnedBy(p));
                var a = await tx.CreateTaskAsync(fixture.Work, Named("a"), OwnedBy(b));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(a));
                var d = await tx.CreateTaskAsync(fixture.Work, Named("d"), OwnConversation(c.Id));
                var g = await tx.CreateTaskAsync(fixture.Work, Named("g"), OwnConversation(c.Id, background: true));
                return (P: p, B: b, A: a, C: c.Id, D: d, G: g);
            }, Context);
            var conversationC = (await harness.ConversationAsync(tree.C, Context))!;
            var idleC = conversationC.WaitForIdleAsync(Context);
            var idleRoot = root.WaitForIdleAsync(Context);
            var idleAll = harness.WaitForIdleAsync(Context);
            // The owners A and B hold for D: live, though their handlers returned.
            fixture.Gate("a").Resolve();
            fixture.Gate("b").Resolve();
            await Eventually(async () => await StatusOf(harness, tree.B) == DurableTaskStatus.Completing);
            Check(!await Settled(idleC), "c not idle while d runs");
            Check(!await Settled(idleRoot), "root not idle while d runs");
            Check(!await Settled(idleAll), "harness not idle while d runs");
            // D ends: C is idle (G is background); A and B finalize; P still holds the root.
            fixture.Gate("d").Resolve();
            await idleC;
            await harness.WaitForTaskAsync(tree.B, Context);
            Check(!await Settled(idleRoot), "root not idle while p runs");
            Check(!await Settled(idleAll), "harness not idle while p runs");
            fixture.Gate("p").Resolve();
            await idleRoot;
            await idleAll;
            Check(await StatusOf(harness, tree.G) != DurableTaskStatus.Terminal, "background g still live");
            await harness.CloseAsync(Context);
        });

        yield return Case(waiters + "count-work-whose-chain-is-not-loaded-yet-as-inside-until-the-open-reconcile-loads-it", async () =>
        {
            var fixture = new IdleFixture();
            var storage = new ControlledStorage(new MemoryStorage()) { KeepOpenForReopen = true };
            var first = await OpenTasks(storage, [fixture.Work]);
            var firstRoot = await first.Harness.RootAsync(Context);
            var tree = await firstRoot.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Work, Named("p"), OwnConversation());
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(p));
                var d = await tx.CreateTaskAsync(fixture.Work, Named("d"), OwnConversation(c.Id));
                var other = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
                return (P: p, C: c.Id, D: d, Other: other.Id);
            }, Context);
            await first.Harness.CloseAsync(Context);
            // Reopened, no chain is loaded: P and D count as inside every scope until the reconcile pass loads theirs.
            var harness = (await OpenTasks(storage.Reopen(), [fixture.Work])).Harness;
            var idleOther = (await harness.ConversationAsync(tree.Other, Context))!.WaitForIdleAsync(Context);
            var idleRoot = (await harness.RootAsync(Context)).WaitForIdleAsync(Context);
            await idleOther;
            Check(!await Settled(idleRoot), "root not idle before resume");
            harness.Resume();
            fixture.Gate("d").Resolve();
            await harness.WaitForTaskAsync(tree.D, Context);
            Check(!await Settled(idleRoot), "root not idle while p runs");
            fixture.Gate("p").Resolve();
            await idleRoot;
            await harness.CloseAsync(Context);
            await storage.Inner.CloseAsync(Context);
        });

        yield return Case(waiters + "resolve-a-waiter-on-an-unrelated-conversation-without-settling-the-busy-one", async () =>
        {
            var opened = await Open(new IdleFixture());
            var (busy, other) = (await opened.Conversation(0), await opened.Conversation(0));
            var held = await opened.Work("held", busy);
            var quick = await opened.Work("quick", other);
            var idleBusy = busy.WaitForIdleAsync(Context);
            var idleOther = other.WaitForIdleAsync(Context);
            await opened.End("quick", quick);
            await idleOther;
            Check(!await Settled(idleBusy), "busy not idle");
            await opened.End("held", held);
            await idleBusy;
            await opened.Harness.CloseAsync(Context);
        });

        // ─── kept context ranges ────────────────────────────────────────────────

        const string ranges = prefix + "kept-context-ranges.";

        yield return Case(ranges + "settles-a-run-end-whose-sweep-drops-the-chain-without-a-check-of-every-scope", async () =>
        {
            var fixture = new IdleFixture();
            var opened = await Open(fixture);
            var (harness, root) = (opened.Harness, opened.Root);
            var tree = await root.CommitAsync(async tx =>
            {
                var p = await tx.CreateTaskAsync(fixture.Work, Named("p"), OwnConversation());
                var t = await tx.CreateTaskAsync(fixture.Work, Named("t"), OwnedBy(p));
                var c = await tx.CreateConversationAsync(ConversationOwnership.Task(t));
                // A context range keeps entries: an empty conversation has none to keep.
                await tx.AppendEntryAsync(c.Id, new EntryDraft { Kind = "note" });
                return (P: p, T: t, C: c.Id);
            }, Context);
            var conversationC = (await harness.ConversationAsync(tree.C, Context))!;
            var d = await conversationC.CommitAsync(tx => tx.CreateTaskAsync(fixture.Work, Obj(("name", "d"), ("read", tree.C.Value)), OwnConversation(tree.C)),
                Context);
            await fixture.Read("d").Task;
            fixture.Gate("t").Resolve();
            await Eventually(async () => await StatusOf(harness, tree.T) == DurableTaskStatus.Completing);
            await Quiet(harness);
            var before = await IdleState(harness);
            Matches(before, "before", contexts: 1, idleContexts: 0, idleMismatches: 0);
            // D's end makes C idle, T finalizes, and the sweep forgets C's chain: none of it checks every scope.
            fixture.Gate("d").Resolve();
            await harness.WaitForTaskAsync(d, Context);
            await harness.WaitForTaskAsync(tree.T, Context);
            await Quiet(harness);
            Equal(before with { IdleContexts = 1 }, await IdleState(harness), "after");
            fixture.Gate("p").Resolve();
            await harness.WaitForTaskAsync(tree.P, Context);
            await harness.CloseAsync(Context);
        });

        yield return Case(ranges + "drops-a-range-past-its-retention-when-a-task-change-makes-it-busy-again-with-background-timers-declined", async () =>
        {
            var clock = new Box(1_000);
            var opened = await Open(new IdleFixture(), clock, new Box(1_000));
            var (x, z) = (await opened.Conversation(), await opened.Conversation(0));
            await opened.End("reader", await opened.Work("reader", z, x.Id));
            Matches(await IdleState(opened.Harness), "kept", contexts: 1, idleContexts: 1, idleMismatches: 0);
            clock.Value = 2_500;
            // No timer expired it; the task change that makes X busy checks expiry first.
            var busy = await opened.Work("busy", x);
            await Quiet(opened.Harness);
            Matches(await IdleState(opened.Harness), "dropped", contexts: 0, idleContexts: 0, idleMismatches: 0);
            await opened.End("busy", busy);
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(ranges + "drops-a-range-past-its-retention-at-a-reconcile-pass-with-nothing-to-do-with-background-timers-declined-review", async () =>
        {
            var clock = new Box(1_000);
            var opened = await Open(new IdleFixture(), clock, new Box(1_000));
            var (x, z) = (await opened.Conversation(), await opened.Conversation(0));
            await opened.End("reader", await opened.Work("reader", z, x.Id));
            var held = await opened.Work("held", z);
            // Held runs: no task changes from here.
            await Quiet(opened.Harness);
            Matches(await IdleState(opened.Harness), "kept", contexts: 1, idleContexts: 1);
            clock.Value = 2_500;
            // A wait on busy Z asks for a reconcile pass, which has no work and changes no task: it checks expiry.
            var idle = z.WaitForIdleAsync(Context);
            await Quiet(opened.Harness);
            Matches(await IdleState(opened.Harness), "dropped", contexts: 0, idleMismatches: 0);
            await opened.End("held", held);
            await idle;
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(ranges + "expires-in-idle-order-when-the-clock-goes-back", async () =>
        {
            var clock = new Box(1_000);
            var opened = await Open(new IdleFixture(), clock, new Box(1_000));
            var (a, b, z) = (await opened.Conversation(3), await opened.Conversation(5), await opened.Conversation(0));
            await opened.End("read a", await opened.Work("read a", z, a.Id));
            clock.Value = 500;
            await opened.End("read b", await opened.Work("read b", z, b.Id));
            Matches(await IdleState(opened.Harness), "both kept", contexts: 2, idleContexts: 2, idleMismatches: 0);
            // B went idle at 500 and expires first, though it went idle last.
            clock.Value = 1_600;
            await opened.End("unrelated", await opened.Work("unrelated", z));
            Matches(await IdleState(opened.Harness), "b expired", contexts: 1, idleMismatches: 0);
            Equal(5, await opened.RowsOf("again b", z, b.Id), "rows of b again");
            Equal(0, await opened.RowsOf("again a", z, a.Id), "rows of a again");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(ranges + "drops-every-idle-range-when-retention-becomes-zero-also-one-that-went-idle-after-the-clock-s-now-review", async () =>
        {
            var clock = new Box(2_000);
            var retention = new Box(1_000);
            var opened = await Open(new IdleFixture(), clock, retention);
            var (a, z) = (await opened.Conversation(), await opened.Conversation(0));
            await opened.End("read", await opened.Work("read", z, a.Id));
            Matches(await IdleState(opened.Harness), "kept", contexts: 1, idleContexts: 1);
            // The clock goes back below A's idle start; then retention 0 and an unrelated task change.
            clock.Value = 1_000;
            retention.Value = 0;
            await opened.End("unrelated", await opened.Work("unrelated", z));
            Matches(await IdleState(opened.Harness), "dropped", contexts: 0, idleMismatches: 0);
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(ranges + "keeps-an-idle-range-s-expiry-when-another-task-reads-it-again", async () =>
        {
            var clock = new Box(1_000);
            var opened = await Open(new IdleFixture(), clock, new Box(1_000));
            var (a, b, z) = (await opened.Conversation(3), await opened.Conversation(5), await opened.Conversation(0));
            await opened.End("read a", await opened.Work("read a", z, a.Id));
            clock.Value = 1_500;
            await opened.End("read b", await opened.Work("read b", z, b.Id));
            clock.Value = 1_800;
            Equal(0, await opened.RowsOf("again a", z, a.Id), "rows of a again");
            clock.Value = 2_100;
            await opened.End("unrelated", await opened.Work("unrelated", z));
            Matches(await IdleState(opened.Harness), "a expired", contexts: 1, idleMismatches: 0);
            Equal(0, await opened.RowsOf("late b", z, b.Id), "rows of b late");
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(ranges + "drops-a-range-from-the-expiry-order-when-an-entry-commit-finds-it-expired", async () =>
        {
            var clock = new Box(1_000);
            var opened = await Open(new IdleFixture(), clock, new Box(1_000));
            var (a, z) = (await opened.Conversation(), await opened.Conversation(0));
            await opened.End("read", await opened.Work("read", z, a.Id));
            clock.Value = 2_500;
            // An entry-only commit settles no idleness: the range is dropped where it would be extended.
            await a.CommitAsync(tx => tx.AppendEntryAsync(a.Id, new EntryDraft { Kind = "note", Data = Obj(("late", true)) }), Context);
            Matches(await IdleState(opened.Harness), "dropped", contexts: 0, idleContexts: 0, idleMismatches: 0);
            await opened.Harness.CloseAsync(Context);
        });

        yield return Case(ranges + "drops-a-range-from-the-expiry-order-when-an-entry-commit-cannot-extend-it", async () =>
        {
            var opened = await Open(new IdleFixture());
            var (a, z) = (await opened.Conversation(0), await opened.Conversation(0));
            var first = await a.CommitAsync(tx => tx.AppendEntryAsync(a.Id, new EntryDraft { Kind = "note" }), Context);
            var second = await a.CommitAsync(tx => tx.AppendEntryAsync(a.Id, new EntryDraft { Kind = "note" }), Context);
            await a.CommitAsync(tx => tx.AppendEntryAsync(a.Id, new EntryDraft { Kind = "summary", Head = EntryHead.At(second.Id) }), Context);
            await opened.End("read", await opened.Work("read", z, a.Id));
            Matches(await IdleState(opened.Harness), "kept", contexts: 1, idleContexts: 1, idleMismatches: 0);
            // A marker whose head is older than the range's: the range cannot be extended and is dropped.
            await a.CommitAsync(tx => tx.AppendEntryAsync(a.Id, new EntryDraft { Kind = "summary", Head = EntryHead.At(first.Id) }), Context);
            Matches(await IdleState(opened.Harness), "dropped", contexts: 0, idleContexts: 0, idleMismatches: 0);
            await opened.Harness.CloseAsync(Context);
        });

        foreach (var (name, changed, kept) in new[] { ("shorter", 400.0, 0), ("longer", 10_000.0, 1), ("zero", 0.0, 0) })
        {
            yield return Case(ranges + $"applies-a-{name}-retention-to-idle-ranges-at-an-unrelated-commit", async () =>
            {
                var clock = new Box(1_000);
                var retention = new Box(1_000);
                var opened = await Open(new IdleFixture(), clock, retention);
                var (a, z) = (await opened.Conversation(), await opened.Conversation(0));
                await opened.End("read", await opened.Work("read", z, a.Id));
                clock.Value = 1_500;
                retention.Value = changed;
                await opened.End("unrelated", await opened.Work("unrelated", z));
                Matches(await IdleState(opened.Harness), name, contexts: kept, idleMismatches: 0);
                await opened.Harness.CloseAsync(Context);
            });
        }

        yield return Case(ranges + "drops-a-range-that-goes-idle-with-zero-retention", async () =>
        {
            var opened = await Open(new IdleFixture(), retention: new Box(0));
            var a = await opened.Conversation();
            var own = await opened.Work("own", a, a.Id);
            await Quiet(opened.Harness);
            Matches(await IdleState(opened.Harness), "busy", contexts: 1, idleContexts: 0, idleMismatches: 0);
            await opened.End("own", own);
            await Quiet(opened.Harness);
            Matches(await IdleState(opened.Harness), "idle", contexts: 0, idleMismatches: 0);
            await opened.Harness.CloseAsync(Context);
        });

        // ─── incremental idle state ─────────────────────────────────────────────

        yield return Case(prefix + "incremental-idle-state.equals-a-full-recompute-after-every-step-of-random-nested-work", async () =>
        {
            var random = Seeded(42);
            T Pick<T>(IReadOnlyList<T> items) => items[(int)Math.Floor(random() * items.Count)];
            var fixture = new IdleFixture();
            var opened = await Open(fixture);
            var (harness, root) = (opened.Harness, opened.Root);
            // A context range keeps entries: every conversation gets one.
            await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            var conversations = new List<ConversationId> { root.Id };
            var live = new List<(string Name, TaskId Id, ConversationId Conversation)>();
            var count = 0;
            var fullChecks = (await IdleState(harness)).FullIdleChecks;
            async Task<IConversation> Handle(ConversationId id) => (await harness.ConversationAsync(id, Context))!;
            for (var step = 0; step < 200; step++)
            {
                var action = random();
                var name = $"t{count++}";
                if (action < 0.1)
                {
                    var created = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
                    await created.CommitAsync(tx => tx.AppendEntryAsync(created.Id, new EntryDraft { Kind = "note" }), Context);
                    conversations.Add(created.Id);
                }
                else if (action < 0.45 || live.Count == 0)
                {
                    // A task in any conversation, also one whose owner chain ended and was swept; it may read a context.
                    var into = Pick(conversations);
                    ConversationId? read = random() < 0.6 ? Pick(conversations) : null;
                    var background = random() < 0.15;
                    var input = read is { } readId ? Obj(("name", name), ("read", readId.Value)) : Named(name);
                    var target = await Handle(into);
                    var id = await target.CommitAsync(tx => tx.CreateTaskAsync(fixture.Work, input, OwnConversation(into, background)), Context);
                    live.Add((name, id, into));
                    if (read is not null) await fixture.Read(name).Task;
                }
                else if (action < 0.6)
                {
                    // Work a live task owns: a child task, or an owned conversation with a task that reads it.
                    var owner = Pick(live);
                    var state = await StatusOf(harness, owner.Id);
                    if (state is null or DurableTaskStatus.Terminal or DurableTaskStatus.Completing)
                    {
                        live.RemoveAll(entry => entry.Name == owner.Name);
                        continue;
                    }
                    var conversation = random() < 0.5;
                    var created = await (await Handle(owner.Conversation)).CommitAsync(async tx =>
                    {
                        if (!conversation)
                        {
                            var task = await tx.CreateTaskAsync(fixture.Work, Named(name), OwnedBy(owner.Id));
                            return (Id: task, Into: owner.Conversation, Read: false);
                        }
                        var child = await tx.CreateConversationAsync(ConversationOwnership.Task(owner.Id));
                        await tx.AppendEntryAsync(child.Id, new EntryDraft { Kind = "note" });
                        var reader = await tx.CreateTaskAsync(fixture.Work, Obj(("name", name), ("read", child.Id.Value)), OwnConversation(child.Id));
                        return (Id: reader, Into: child.Id, Read: true);
                    }, Context);
                    if (created.Read) conversations.Add(created.Into);
                    live.Add((name, created.Id, created.Into));
                    if (created.Read) await fixture.Read(name).Task;
                }
                else
                {
                    var ended = Pick(live).Name;
                    live.RemoveAll(entry => entry.Name == ended);
                    fixture.Gate(ended).Resolve();
                }
                await Quiet(harness);
                var current = await IdleState(harness);
                Equal(0, current.IdleMismatches, $"step {step}: idleMismatches");
                fullChecks = current.FullIdleChecks;
            }
            var final = await IdleState(harness);
            Check(final.Contexts > 5, $"more than 5 contexts kept ({final.Contexts})");
            Check(final.IdleContexts > 0, $"some idle contexts ({final.IdleContexts})");
            Check(final.IdleContexts < final.Contexts, $"some busy contexts ({final.IdleContexts} of {final.Contexts} idle)");
            Equal(0, fullChecks, "full idle checks");
            fixture.OpenAll();
            await harness.CloseAsync(Context);
        });

        // ─── wake() ─────────────────────────────────────────────────────────────

        const string wake = prefix + "wake.";
        const string kind = "test.idle.work";

        static TaskRecord Record(long id, string state) => new()
        {
            Id = new TaskId(id),
            ConversationId = ConversationId.Root,
            Kind = kind,
            Version = 1,
            Input = Obj(("name", $"seeded{id}")),
            State = TaskState.FromJson(O(state)),
            Background = false,
            AbortRequested = false,
        };
        static string Sleeping(double until) => $$"""{"status":"sleeping","checkpoint":{"phase":"run"},"until":{{until}}}""";
        var sleeps = Record(10, Sleeping(5_000));

        (string Name, TaskRecord[] Writes, Wake Expected)[] each =
        [
            ("a-sleeping-task", [sleeps], new Wake(false, 5_000)),
            ("an-abort-marked-sleeping-task-which-runs-its-handler", [sleeps, Record(11, Sleeping(3_000)) with { AbortRequested = true }],
                new Wake(true, 5_000)),
            ("a-sleeping-task-that-is-due", [Record(11, Sleeping(500))], new Wake(true)),
            ("a-task-waiting-on-a-live-one",
                [sleeps, Record(11, """{"status":"waiting","checkpoint":{"phase":"run"},"on":[10],"policy":"allSettled"}""")], new Wake(false, 5_000)),
            ("a-completing-task-holding-for-a-sleeping-child",
                [Record(11, """{"status":"completing","outcome":{"status":"completed","result":null}}"""), Record(10, Sleeping(5_000)) with { Owner = new TaskId(11) }],
                new Wake(false, 5_000)),
            ("a-task-no-definition-can-take",
                [sleeps, Record(11, """{"status":"pending","checkpoint":{"phase":"run"}}""") with { Kind = "test.idle.missing" }], new Wake(false, 5_000)),
            ("a-pending-task", [sleeps, Record(11, """{"status":"pending","checkpoint":{"phase":"run"}}""")], new Wake(true, 5_000)),
        ];
        foreach (var (name, writes, expected) in each)
        {
            yield return Case(wake + "with-" + name, async () =>
            {
                var storage = new MemoryStorage();
                await storage.CommitWritesAsync(
                    [new ConversationWrite(new ConversationRecord(ConversationId.Root)), .. writes.Select(record => (StorageWrite)new TaskWrite(record))], Context);
                var harness = (await OpenTasks(storage, [new IdleFixture().Work], now: () => 1_000)).Harness;
                await Quiet(harness);
                Equal(expected, harness.Wake(), "wake");
                await harness.CloseAsync(Context);
            });
        }

        yield return Case(wake + "with-an-invocation-running-active-and-the-earliest-sleep-still-found", async () =>
        {
            var fixture = new IdleFixture();
            var storage = new MemoryStorage();
            await storage.CommitWritesAsync([new ConversationWrite(new ConversationRecord(ConversationId.Root)), new TaskWrite(sleeps)], Context);
            var harness = (await OpenTasks(storage, [fixture.Work], now: () => 1_000)).Harness;
            harness.Resume();
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(fixture.Work, Named("running"), OwnConversation()), Context);
            await Eventually(async () => await StatusOf(harness, id) == DurableTaskStatus.Running);
            Equal(new Wake(true, 5_000), harness.Wake(), "wake while running");
            fixture.Gate("running").Resolve();
            await harness.WaitForTaskAsync(id, Context);
            await Quiet(harness);
            Equal(new Wake(false, 5_000), harness.Wake(), "wake once ended");
            await harness.CloseAsync(Context);
        });
    }
}
