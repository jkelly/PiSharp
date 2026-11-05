using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/documents.test.ts,
// packages/chord/src/next/documents/documents.ts and slot.ts (SlotDefinition), what the `pisharp-` cases check.
//
// Upstream's four cases (singleton documents read initial() until written and throw not_ready in the factory; keyed documents' get, keys
// and retire; every use refused released once the owner ends) serve app.owner through createHost (loaded, phase 6); here the object is the
// hand-built host's (Documents/DocumentsHost.cs), its factory declaring list and drafts, and reference.release (the last reference gone)
// is the object's end (EndAsync). A watch's frames are delivered between commits (host.Delivered()), as upstream delivers one per commit.
// The `pisharp-` cases check the engine against hand-built parts: an owner (IDocumentsOwner), slots that log what the engine calls
// (IDeclaredSlot), transactions holding the line (ILineHolder), and the residency's fake timers (FakeTimers.cs); and SlotDefinition's
// checks and version fence, which upstream's slot.test.ts reaches through createHost.
internal static partial class Program
{
    private const string DocumentsReleased = """{"code":"released","reason":"ended","message":"app.owner(1): ended"}""";

    /// <summary>An object's owner: its name, batch and context, no store, and the failures it was given.</summary>
    private sealed class DocumentsTestOwner(Batch batch) : IDocumentsOwner
    {
        public string Label => "app.owner(1)";

        public Batch Batch => batch;

        public ChordContext Context => ChordContext.Background;

        public DocumentsStore? Store => null;

        public List<ChordError> Failures { get; } = [];

        public void Fail(ChordError error) => Failures.Add(error);
    }

    /// <summary>A declared slot that logs what the engine calls; its load runs <see cref="Loading"/> when set.</summary>
    private sealed class DocumentsTestSlot(StateDefinition definition, List<string> log) : IDeclaredSlot
    {
        public SlotDefinition Definition { get; } = new(definition);

        public bool IsPersisted => Definition.Current.Persist;

        public Func<Task>? Loading { get; set; }

        public void Hydrate(StateDefinition declared, ChordContext context)
        {
            Definition.Hydrate(declared);
            lock (log) log.Add($"hydrate {declared.Name} v{declared.Version}");
        }

        public async Task LoadDeclaredAsync(ChordContext context)
        {
            lock (log) log.Add($"load {Definition.Current.Name}");
            if (Loading is { } loading) await loading();
        }

        public void Drop()
        {
            lock (log) log.Add($"drop {Definition.Current.Name}");
        }
    }

    /// <summary>A transaction holding the line under <paramref name="token"/>; counts its cuts.</summary>
    private sealed class DocumentsTestHolder(SealToken token) : ILineHolder
    {
        public SealToken Token => token;

        public int Cuts { get; private set; }

        public void Cut() => Cuts++;
    }

    /// <summary>A resident's slot whose first eviction completes <see cref="Evicted"/> (a sweep runs on the line, off the test's
    /// thread).</summary>
    private sealed class DocumentsTestEvictions : IResidentSlot
    {
        public TaskCompletionSource<string> Evicted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Evict(Resident resident) => Evicted.TrySetResult(resident.Key);
    }

    /// <summary>The documents of a hand-built object, and the log of its slots.</summary>
    private static (Documents Documents, DocumentsTestOwner Owner, List<string> Log) DocumentsEngine(TimeProvider? time = null)
    {
        var owner = new DocumentsTestOwner(new StateHost().Batch);
        return (new Documents(owner, time), owner, []);
    }

    /// <summary>A definition of <c>{ n: int }</c> (or <paramref name="schema"/>) named <paramref name="name"/>.</summary>
    private static StateDefinition DocumentsDefinition(string name, long version = 1, bool keyed = false, bool persist = false, JsonNode? schema = null) =>
        StateDefinition.Define(name, new()
        {
            Schema = schema ?? Obj(("n", Int())),
            Version = version,
            Initial = () => DocumentsN(0),
            Keyed = keyed,
            Persist = persist,
            CheckpointWhen = persist ? (_, _, _) => false : null,
        });

    /// <summary>Expects <paramref name="task"/> to fail <c>released</c> as <paramref name="expected"/> says.</summary>
    private static async Task DocumentsRejects(string expected, Task task, string what)
    {
        var rejected = await Rejection(task);
        if (rejected is Exception error) throw new InvalidOperationException($"{what}: expected a ChordError, actual {error.GetType().Name}: {error.Message}");
        Json(expected, (JsonNode)rejected, what);
    }

    /// <summary>Expects <paramref name="run"/> to throw a ChordError as <paramref name="expected"/> says.</summary>
    private static void DocumentsThrows(string expected, Action run, string what) => Json(expected, Throws<ChordError>(run, what).ToJson(), what);

    private static readonly StateDefinition DocumentsList = DocumentsDefine("list", Obj(("items", Arr(Str()))), 1, () => new JsonObject { ["items"] = new JsonArray() });

    private static readonly StateDefinition DocumentsDrafts = DocumentsDefine("drafts", Obj(("body", Str())), 1, () => new JsonObject { ["body"] = "" }, keyed: true);

    /// <summary>app.owner(1) live, its factory declaring list and drafts (upstream <c>owner</c>).</summary>
    private static async Task<(DocumentsObject Object, ISingletonHandle List, IKeyedHandle Drafts)> DocumentsOwner()
    {
        ISingletonHandle? list = null;
        IKeyedHandle? drafts = null;
        var made = await new DocumentsHost().GetAsync("app.owner", "app.owner(1)", (_, self) =>
        {
            list = self.State(DocumentsList);
            drafts = self.KeyedState(DocumentsDrafts);
        });
        return (made, list!, drafts!);
    }

    private static IEnumerable<(string, Func<Task>)> DocumentsDocumentsCases() =>
    [
        Case("documents.documents.singleton-documents-read-initial-until-written-and-again-after-a-retire-whose-frame-replaces-the-root", async () =>
        {
            var (made, list, _) = await DocumentsOwner();
            var initial = list.Value;
            Json("""{"items":[]}""", initial, "initial");
            var frames = FramesOf(await list.WatchAsync(DocumentsBg));
            await list.ChangeAsync(draft => DocumentsArray(draft, "items").Add("a"), DocumentsBg);
            Json("""{"items":["a"]}""", list.Value, "after the change");
            made.Host.Delivered();
            await list.RetireAsync(DocumentsBg);
            Check(ReferenceEquals(initial, list.Value), "initial() again after the retire");
            await list.RetireAsync(DocumentsBg);
            made.Host.Delivered();
            DocumentsFrames("""[[{"items":["a"]},[["p",["items"],0,0,["a"]]]],[{"items":[]},[["r",{"items":[]}]]]]""", frames, "the frames");
        }),
        Case("documents.documents.singleton-documents-throw-not-ready-when-read-in-the-factory", async () =>
        {
            var host = new DocumentsHost();
            var rejected = await Rejection(host.GetAsync("app.owner", "app.owner(1)", (number, self) => _ = self.State(DocumentsList).Value));
            DocumentsRejectsWith(rejected, "list: read before activation", "the activation");
        }),
        Case("documents.documents.keyed-documents-are-absent-until-written-get-keys-and-retire-by-key", async () =>
        {
            var (_, _, drafts) = await DocumentsOwner();
            Check(await drafts.GetAsync("a", DocumentsBg) is null, "absent before a write");
            await drafts.ChangeAsync("a", draft => { draft["body"] = "x"; }, DocumentsBg);
            await drafts.ChangeAsync("b", draft => { draft["body"] = "y"; }, DocumentsBg);
            await drafts.ChangeAsync("c", _ => { }, DocumentsBg);
            Json("""{"body":"x"}""", await drafts.GetAsync("a", DocumentsBg), "a");
            Sequence(["a", "b"], await DocumentsKeysOf(drafts), "the keys");
            await drafts.RetireAsync("a", DocumentsBg);
            await drafts.RetireAsync("missing", DocumentsBg);
            Check(await drafts.GetAsync("a", DocumentsBg) is null, "absent after the retire");
            Sequence(["b"], await DocumentsKeysOf(drafts), "the keys after the retire");
        }),
        Case("documents.documents.when-the-owner-ends-documents-are-dropped-every-use-throws-released-and-watches-end", async () =>
        {
            var (made, list, drafts) = await DocumentsOwner();
            var watch = await list.WatchAsync(DocumentsBg);
            await made.EndAsync();
            Equal(WatchEnd.Ended, await watch.Closed, "the watch's end");
            Check(DocumentsThrown(() => _ = list.Value) is JsonObject thrown && (string?)thrown["message"] == "app.owner(1): ended", "a read throws");
            Json(DocumentsReleased, (JsonNode)await Rejection(drafts.GetAsync("a", DocumentsBg)), "a keyed read");
            Json(DocumentsReleased, (JsonNode)await Rejection(DocumentsKeysOf(drafts)), "the keys");
            Json(DocumentsReleased, (JsonNode)await Rejection(list.ChangeAsync(_ => { }, DocumentsBg)), "a change");
        }),
        Case("documents.documents.pisharp-check-refuses-what-a-slot-refuses-and-changes-nothing", () =>
        {
            var (documents, _, log) = DocumentsEngine();
            var list = DocumentsDefinition("list", version: 2);
            var slot = documents.Add(new DocumentsTestSlot(list, log));
            var drafts = documents.Add(new DocumentsTestSlot(DocumentsDefinition("drafts", keyed: true, persist: true), log));
            Accepts(() => documents.Check(DocumentsDefinition("list", version: 2)), "the same declaration");
            Accepts(() => documents.Check(DocumentsDefinition("list", version: 3, schema: Obj(("n", Str())))), "a newer version");
            Accepts(() => documents.Check(DocumentsDefinition("other", keyed: true)), "a name no slot has");
            DocumentsFails(() => documents.Check(DocumentsDefinition("list", version: 2, keyed: true)), "list: declared keyed, its documents are not");
            DocumentsFails(() => documents.Check(DocumentsDefinition("drafts", persist: true)), "drafts: declared singleton, its documents are not");
            DocumentsFails(() => documents.Check(DocumentsDefinition("list", version: 2, persist: true)), "list: declared persisted, its documents are not");
            DocumentsFails(() => documents.Check(DocumentsDefinition("drafts", keyed: true)), "drafts: declared in memory, its documents are not");
            DocumentsFails(() => documents.Check(DocumentsDefinition("list", version: 1)), "list: version 1 is older than its documents' (2)");
            DocumentsFails(() => documents.Check(DocumentsDefinition("list", version: 2, schema: Obj(("n", Str())))),
                "list: the schema changed without a version bump (version 2)");
            Check(ReferenceEquals(list, slot.Definition.Current) && slot.Definition.Hydrated is null && drafts.Definition.Hydrated is null,
                "the checks hydrate nothing");
            Sequence([], log, "the slots were not called");
        }),
        Case("documents.documents.pisharp-hydrate-releases-the-previous-pins-drops-the-undeclared-slots-and-hydrates-the-declared-ones-in-order", () =>
        {
            var (documents, _, log) = DocumentsEngine();
            var a = DocumentsDefinition("a");
            var slot = documents.Add(new DocumentsTestSlot(a, log));
            documents.Add(new DocumentsTestSlot(DocumentsDefinition("b"), log));
            var c = DocumentsDefinition("c");
            documents.Add(new DocumentsTestSlot(c, log));
            documents.Hydrate(new OrderedDictionary<string, StateDefinition>(StringComparer.Ordinal) { ["c"] = c, ["a"] = a }, [() => log.Add("release 1")],
                ChordContext.Background);
            Sequence(["drop b", "hydrate c v1", "hydrate a v1"], log, "the first factory's");
            log.Clear();
            var a2 = DocumentsDefinition("a", version: 2);
            documents.Hydrate(new OrderedDictionary<string, StateDefinition>(StringComparer.Ordinal) { ["a"] = a2 }, [() => log.Add("release 2")],
                ChordContext.Background);
            Sequence(["release 1", "drop c", "hydrate a v2"], log, "the next factory's");
            Check(ReferenceEquals(a2, slot.Definition.Current) && ReferenceEquals(a2, slot.Definition.Hydrated), "a's definition");
            log.Clear();
            // The end drops the slots and forgets the pins without releasing them: everything they pinned is dropped with the slots.
            documents.End();
            Sequence(["drop a"], log, "the end");
        }),
        Case("documents.documents.pisharp-load-is-null-without-a-persisted-slot-else-one-line-job-loading-each-in-order", async () =>
        {
            var (documents, _, log) = DocumentsEngine();
            documents.Add(new DocumentsTestSlot(DocumentsDefinition("memory"), log));
            Check(documents.Load(ChordContext.Background) is null, "no load without a persisted slot");
            documents.Add(new DocumentsTestSlot(DocumentsDefinition("p", persist: true), log));
            documents.Add(new DocumentsTestSlot(DocumentsDefinition("later"), log));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            documents.Add(new DocumentsTestSlot(DocumentsDefinition("q", keyed: true, persist: true), log)).Loading = () => gate.Task;
            var held = await documents.Line.AcquireAsync();
            var load = documents.Load(ChordContext.Background)!;
            await FakeTimers.Flushed();
            lock (log) Sequence([], log, "nothing loads while another job holds the line");
            held();
            var after = documents.OnLineAsync(() =>
            {
                lock (log) log.Add("next job");
                return Task.CompletedTask;
            });
            await FakeTimers.Flushed();
            lock (log) Sequence(["load p", "load q"], log, "the load holds the line");
            gate.SetResult();
            await load;
            await after;
            Sequence(["load p", "load q", "next job"], log, "the jobs");
        }),
        Case("documents.documents.pisharp-on-line-runs-jobs-one-at-a-time-in-the-order-they-asked-and-returns-their-results", async () =>
        {
            var (documents, _, _) = DocumentsEngine();
            var order = new List<string>();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = documents.OnLineAsync(async () =>
            {
                lock (order) order.Add("first starts");
                await gate.Task;
                lock (order) order.Add("first ends");
                return 1;
            });
            var second = documents.OnLineAsync(() =>
            {
                lock (order) order.Add("second");
                return Task.FromResult(2);
            });
            await FakeTimers.Flushed();
            lock (order) Sequence(["first starts"], order, "while the first holds the line");
            gate.SetResult();
            Sequence([1, 2], [await first, await second], "the results");
            Sequence(["first starts", "first ends", "second"], order, "the order");
            // A job's failure is its caller's; the line goes on.
            var failed = documents.OnLineAsync<int>(() => throw new InvalidOperationException("boom"));
            Equal("boom", ((Exception)await Rejection(failed)).Message, "a failed job");
            Equal(3, await documents.OnLineAsync(() => Task.FromResult(3)), "the job after it");
        }),
        Case("documents.documents.pisharp-on-line-refuses-released-a-sealed-tokens-job-and-every-job-once-sealed-a-queued-one-included", async () =>
        {
            var (documents, _, _) = DocumentsEngine();
            var ran = new List<string>();
            var token = new SealToken();
            var held = await documents.Line.AcquireAsync();
            var queued = documents.OnLineAsync(() =>
            {
                lock (ran) ran.Add("queued");
                return Task.CompletedTask;
            }, token);
            token.Seal();
            await DocumentsRejects(DocumentsReleased, documents.OnLineAsync(() => Task.CompletedTask, token), "a sealed token's job");
            DocumentsThrows(DocumentsReleased, () => documents.AssertAdmitted(token), "a sealed token's admission");
            held();
            await DocumentsRejects(DocumentsReleased, queued, "a job queued before its token's seal");
            held = await documents.Line.AcquireAsync();
            var beforeSeal = documents.OnLineAsync(() =>
            {
                lock (ran) ran.Add("before the seal");
                return Task.CompletedTask;
            });
            documents.Seal();
            await DocumentsRejects(DocumentsReleased, documents.OnLineAsync(() => Task.CompletedTask), "a job after the seal");
            DocumentsThrows(DocumentsReleased, () => documents.AssertAdmitted(new SealToken()), "an admission after the seal");
            // Sealed is not ended: the documents stay readable.
            Accepts(documents.AssertLive, "the liveness check after the seal");
            held();
            await DocumentsRejects(DocumentsReleased, beforeSeal, "a job queued before the seal");
            Sequence([], ran, "the jobs that ran");
        }),
        Case("documents.documents.pisharp-fence-seals-the-token-and-cuts-the-transaction-holding-the-line-under-it-only", () =>
        {
            var (documents, _, _) = DocumentsEngine();
            var holder = new DocumentsTestHolder(new SealToken());
            documents.Hold(holder);
            var other = new SealToken();
            documents.Fence(other);
            Check(other.Sealed && holder.Cuts == 0, "another token's fence seals it and cuts nothing");
            documents.Fence(holder.Token);
            Check(holder.Token.Sealed && holder.Cuts == 1, "the holder's token's fence cuts it");
            documents.Fence(holder.Token);
            documents.Unhold(holder);
            Equal(1, holder.Cuts, "a cut holder no longer holds the line");
            var settled = new DocumentsTestHolder(new SealToken());
            documents.Hold(settled);
            documents.Unhold(settled);
            documents.Fence(settled.Token);
            Equal(0, settled.Cuts, "a settled transaction is not cut");
            Accepts(() => documents.AssertAdmitted(new SealToken()), "an open token's admission");
        }),
        Case("documents.documents.pisharp-seal-seals-the-objects-own-token-and-cuts-the-holder-once", () =>
        {
            var (documents, owner, _) = DocumentsEngine();
            var holder = new DocumentsTestHolder(new SealToken());
            documents.Hold(holder);
            Equal(false, documents.Own.Sealed, "the object's own token before the seal");
            documents.Seal();
            documents.Seal();
            Check(documents.Own.Sealed && !holder.Token.Sealed && holder.Cuts == 1, "the seal: own sealed, the holder cut once");
            var error = new ChordError("gone", "list: unreadable");
            documents.Fail(error);
            Check(owner.Failures.Count == 1 && ReferenceEquals(error, owner.Failures[0]), "a failure reaches the owner");
        }),
        Case("documents.documents.pisharp-settled-completes-once-the-line-is-free", async () =>
        {
            var (documents, _, _) = DocumentsEngine();
            var held = await documents.Line.AcquireAsync();
            var settled = documents.SettledAsync();
            await FakeTimers.Flushed();
            Equal(false, settled.IsCompleted, "settled while a job holds the line");
            held();
            await settled;
            await documents.SettledAsync();
        }),
        Case("documents.documents.pisharp-end-drops-every-slot-ends-the-residency-and-every-later-use-throws-released", async () =>
        {
            var timers = new FakeTimers();
            var (documents, _, log) = DocumentsEngine(timers);
            documents.Add(new DocumentsTestSlot(DocumentsDefinition("a"), log));
            documents.Add(new DocumentsTestSlot(DocumentsDefinition("b", persist: true), log));
            documents.Residency.Touch(new Resident(new DocumentsTestEvictions(), "k", Tracker.Track(new JsonObject()), null, 1));
            Equal(1, timers.TimerCount, "the residency's timer");
            documents.End();
            Sequence(["drop a", "drop b"], log, "the slots dropped");
            Equal(0, timers.TimerCount, "the timer after the end");
            DocumentsThrows(DocumentsReleased, documents.AssertLive, "the liveness check");
            DocumentsThrows(DocumentsReleased, () => documents.AssertAdmitted(documents.Own), "an admission");
            await DocumentsRejects(DocumentsReleased, documents.OnLineAsync(() => Task.CompletedTask), "a job");
            Check(documents.Load(ChordContext.Background) is null, "nothing left to load");
            Json(DocumentsReleased, documents.Released().ToJson(), "the released error");
        }),
        Case("documents.documents.pisharp-the-residency-sweeps-on-the-line-and-not-after-the-end", async () =>
        {
            var timers = new FakeTimers();
            var (documents, _, _) = DocumentsEngine(timers);
            var slot = new DocumentsTestEvictions();
            var held = await documents.Line.AcquireAsync();
            documents.Residency.Touch(new Resident(slot, "k", Tracker.Track(new JsonObject()), null, 1));
            timers.AdvanceTimersByTime(Residency.RetentionMs);
            await FakeTimers.Flushed();
            Equal(false, slot.Evicted.Task.IsCompleted, "no eviction while a job holds the line");
            held();
            Equal("k", await slot.Evicted.Task.WaitAsync(TimeSpan.FromSeconds(10)), "the eviction on the line");
            var ended = new DocumentsTestEvictions();
            held = await documents.Line.AcquireAsync();
            documents.Residency.Touch(new Resident(ended, "j", Tracker.Track(new JsonObject()), null, 1));
            timers.AdvanceTimersByTime(Residency.RetentionMs);
            documents.End();
            held();
            await documents.SettledAsync();
            await FakeTimers.Flushed();
            Equal(false, ended.Evicted.Task.IsCompleted, "a sweep queued before the end evicts nothing");
        }),
        Case("documents.documents.pisharp-slot-definitions-refuse-an-old-handle-released-version-and-a-newer-one-not-ready", () =>
        {
            var slot = new SlotDefinition(DocumentsDefinition("list", version: 2));
            Check(slot.Hydrated is null, "not hydrated before the first hydration");
            Accepts(() => slot.AssertVersion(2), "a handle of the documents' version");
            DocumentsThrows("""{"code":"released","reason":"version","message":"list: a handle of version 1; the documents are at version 2"}""",
                () => slot.AssertVersion(1), "an old handle");
            DocumentsThrows("""{"code":"not_ready","message":"list: a handle of version 3; the documents are at version 2 until the activation migrates them"}""",
                () => slot.AssertVersion(3), "a newer handle");
            var newer = DocumentsDefinition("list", version: 3);
            slot.Hydrate(newer);
            Check(ReferenceEquals(newer, slot.Current) && ReferenceEquals(newer, slot.Hydrated), "the hydrated definition");
            Accepts(() => slot.AssertVersion(3), "the newer handle after the migration");
            DocumentsThrows("""{"code":"released","reason":"version","message":"list: a handle of version 2; the documents are at version 3"}""",
                () => slot.AssertVersion(2), "the old handle after the migration");
        }),
    ];
}
