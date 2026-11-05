using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Store;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/slot.test.ts
//
// Singleton slots (slice 6a §4, §10, §11): retirement, slots kept across attempts, persisted singletons loaded once. Upstream's cases serve
// app.restartable or app.store through createHost (restartable, storeRestartable, loaded; phase 6); here they run on the hand-built host
// (Documents/DocumentsHost.cs), whose attempts declare through Documents.Declare and activate as upstream's attempt.ts does. Ten of the
// eleven cases are ported as they are. The last ("refuse persisting without a stable identity or a store, and a flipped persist") checks
// two of the host's P3 checks (attempt.ts's #assertPersistable: a type with params, a host without a store), which wait for phase 6; its
// third check, a flipped persist, is the slot's (SlotDefinition.Check) and runs here as a pisharp- case. vi.spyOn(store, "read") is the
// store's Reads seam (DocumentsCountReads). Upstream's host delivers watch frames on microtasks, a frame per commit; the hand-built host's
// queue runs when a case calls Delivered(), so a case calls it where upstream's awaits would have delivered one.
internal static partial class Program
{
    private static readonly StateDefinition SlotListV1 = DocumentsDefine("list", Obj(("items", Arr(Str()))), 1, () => new JsonObject { ["items"] = new JsonArray() });

    private static readonly StateDefinition SlotPrefs = DocumentsDefine("prefs", Obj(("theme", Str()), ("n", Int())), 1,
        () => new JsonObject { ["theme"] = "light", ["n"] = 0 }, persist: true);

    /// <summary>A store host declaring <paramref name="definition"/> per attempt; <c>Prefs()</c> is the latest attempt's handle (upstream
    /// <c>prefsIn</c>).</summary>
    private static async Task<(DocumentsObject Object, Func<ISingletonHandle> Prefs)> SlotPrefsIn(ChordStore store, StateDefinition? definition = null)
    {
        ISingletonHandle? prefs = null;
        var made = await DocumentsHost.StoreRestartable((_, self) => prefs = self.State(definition ?? SlotPrefs), store);
        return (made, () => prefs!);
    }

    private static IEnumerable<(string, Func<Task>)> DocumentsSlotCases() =>
    [
        Case("documents.slot.retirement-reaches-watches-as-initial-with-retired-true", async () =>
        {
            ISingletonHandle? list = null;
            var made = await DocumentsHost.Restartable((_, self) => list = self.State(SlotListV1));
            var initial = list!.Value!.ToJsonString();
            var changes = ChangesOf(await list.WatchAsync(DocumentsBg));
            await list.ChangeAsync(draft => DocumentsArray(draft, "items").Add("a"), DocumentsBg);
            // Upstream's host delivers a frame per commit on a microtask: here the queue runs between the commits.
            made.Host.Delivered();
            await list.RetireAsync(DocumentsBg);
            made.Host.Delivered();
            DocumentsCalls($$"""
                [{"kind":"value","value":{"items":["a"]},"ops":[["p",["items"],0,0,["a"]]],"retired":false},
                 {"kind":"value","value":{{initial}},"ops":[["r",{{initial}}]],"retired":true}]
                """, changes, "the calls");
        }),
        Case("documents.slot.retirement-a-retire-and-a-recreate-of-a-present-document-in-one-transaction-one-frame-retired-the-new-value", async () =>
        {
            ISingletonHandle? list = null;
            var made = await DocumentsHost.Restartable((_, self) => list = self.State(SlotListV1));
            await list!.ChangeAsync(draft => DocumentsArray(draft, "items").Add("a"), DocumentsBg);
            var changes = ChangesOf(await list.WatchAsync(DocumentsBg));
            await made.Attempts[0].TxAsync(async tx =>
            {
                await tx.RetireAsync(list);
                DocumentsArray(await tx.DraftAsync(list), "items").Add("b");
            }, DocumentsBg);
            made.Host.Delivered();
            DocumentsCalls("""[{"kind":"value","value":{"items":["b"]},"ops":[["r",{"items":["b"]}]],"retired":true}]""", changes, "one frame");
        }),
        Case("documents.slot.across-attempts-keep-in-memory-documents-across-a-failure-and-retry-initial-is-taken-once", async () =>
        {
            var initials = 0;
            var list = DocumentsDefine("list", Obj(("items", Arr(Str()))), 1, () =>
            {
                initials++;
                return new JsonObject { ["items"] = new JsonArray() };
            });
            var handles = new List<ISingletonHandle>();
            var made = await DocumentsHost.Restartable((_, self) => handles.Add(self.State(list)));
            var changes = ChangesOf(await handles[0].WatchAsync(DocumentsBg));
            await handles[0].ChangeAsync(draft => DocumentsArray(draft, "items").Add("a"), DocumentsBg);
            made.Host.Delivered();
            await made.RestartAsync();
            Json("""{"items":["a"]}""", handles[1].Value, "the next attempt's value");
            await handles[1].ChangeAsync(draft => DocumentsArray(draft, "items").Add("b"), DocumentsBg);
            made.Host.Delivered();
            // The watch taken through the first attempt's handle follows the slot across attempts.
            Json("""[{"items":["a"]},{"items":["a","b"]}]""", new JsonArray([.. ValuesOf(changes).Select(value => ((JsonNode?)value)?.DeepClone())]), "the values");
            Equal(1, initials, "initial() calls");
        }),
        Case("documents.slot.across-attempts-drop-the-in-memory-documents-a-later-factory-no-longer-declares-their-watches-end", async () =>
        {
            ISingletonHandle? list = null;
            var made = await DocumentsHost.Restartable((attempt, self) =>
            {
                if (attempt == 1) list = self.State(SlotListV1);
            });
            await list!.ChangeAsync(draft => DocumentsArray(draft, "items").Add("a"), DocumentsBg);
            var watch = await list.WatchAsync(DocumentsBg);
            await made.RestartAsync();
            Equal(WatchEnd.Ended, await watch.Closed, "the watch's end");
        }),
        Case("documents.slot.across-attempts-keep-them-across-a-throwing-factory-only-a-factory-that-returns-drops-what-it-does-not-declare", async () =>
        {
            var handles = new List<ISingletonHandle>();
            var made = await DocumentsHost.Restartable((attempt, self) =>
            {
                if (attempt == 2) throw new InvalidOperationException("factory threw");
                handles.Add(self.State(SlotListV1));
            });
            await handles[0].ChangeAsync(draft => DocumentsArray(draft, "items").Add("a"), DocumentsBg);
            made.FailAttempt();
            await Rejection(made.RetryAsync());
            Equal("app.restartable(1): factory threw", made.Host.Failure, "the failure");
            await made.RetryAsync();
            Json("""{"items":["a"]}""", handles[1].Value, "kept");
        }),
        Case("documents.slot.across-attempts-migrate-in-memory-when-a-later-attempt-declares-a-newer-version-the-old-handles-are-refused", async () =>
        {
            var initials = 0;
            var listV2 = DocumentsDefine("list", Obj(("entries", Arr(Obj(("text", Str()))))), 2, () =>
            {
                initials++;
                return new JsonObject { ["entries"] = new JsonArray() };
            }, migrate: (old, _) => new JsonObject
            {
                ["entries"] = new JsonArray([.. ((JsonArray)old["items"]!).Select(text => (JsonNode)new JsonObject { ["text"] = text?.DeepClone() })]),
            });
            var notes1 = DocumentsDefine("notes", Obj(("n", Int())), 1, () => new JsonObject { ["n"] = 0 }, keyed: true);
            var notes2 = DocumentsDefine("notes", Obj(("n", Int()), ("m", Int())), 2, () => new JsonObject { ["n"] = 0, ["m"] = 0 }, keyed: true,
                migrate: (old, _) => new JsonObject { ["n"] = old["n"]?.DeepClone(), ["m"] = 1 });
            var lists = new List<ISingletonHandle>();
            var notes = new List<IKeyedHandle>();
            var inFactory = new List<object?>();
            var made = await DocumentsHost.Restartable((attempt, self) =>
            {
                lists.Add(self.State(attempt == 1 ? SlotListV1 : listV2));
                notes.Add(self.KeyedState(attempt == 1 ? notes1 : notes2));
                // The new code's handle in its own factory: the values are still at version 1 until the activation.
                if (attempt == 2) inFactory.Add(DocumentsThrown(() => _ = lists[1].Value));
            });
            await lists[0].ChangeAsync(draft => DocumentsArray(draft, "items").Add("a"), DocumentsBg);
            await notes[0].ChangeAsync("k", draft => { draft["n"] = 5; }, DocumentsBg);
            var changes = ChangesOf(await lists[0].WatchAsync(DocumentsBg));
            await made.RestartAsync();
            Json("""{"entries":[{"text":"a"}]}""", lists[1].Value, "migrated");
            Json("""{"n":5,"m":1}""", await notes[1].GetAsync("k", DocumentsBg), "the keyed document migrated");
            var stale = "list: a handle of version 1; the documents are at version 2";
            Check(Throws<ChordError>(() => _ = lists[0].Value, "the old handle").Message.Contains(stale, StringComparison.Ordinal), "the old handle's refusal");
            DocumentsMatches("""{"code":"released"}""", await Rejection(notes[0].GetAsync("k", DocumentsBg)), "the old keyed handle");
            made.Host.Delivered();
            DocumentsCalls("""
                [{"kind":"value","value":{"entries":[{"text":"a"}]},"ops":[["r",{"entries":[{"text":"a"}]}]],"retired":false}]
                """, changes, "the replacement");
            Equal(1, initials, "initial() calls");
            Equal(1, inFactory.Count, "one read in the factory");
            DocumentsMatches("""{"code":"not_ready"}""", inFactory[0]!, "the factory's read");
        }),
        Case("documents.slot.across-attempts-fail-the-attempt-broken-for-a-flipped-keyed-an-older-version-or-a-schema-changed-without-a-bump", async () =>
        {
            var keyedList = DocumentsDefine("list", Obj(("items", Arr(Str()))), 1, () => new JsonObject { ["items"] = new JsonArray() }, keyed: true);
            var changed = DocumentsDefine("list", Obj(("items", Arr(Int()))), 1, () => new JsonObject { ["items"] = new JsonArray() });
            var newer = DocumentsDefine("list", Obj(("items", Arr(Str()))), 2, () => new JsonObject { ["items"] = new JsonArray() }, migrate: (old, _) => old);
            (StateDefinition First, StateDefinition Later, string Message)[] cases =
            [
                (SlotListV1, keyedList, "list: declared keyed, its documents are not"),
                (SlotListV1, changed, "list: the schema changed without a version bump (version 1)"),
                (newer, SlotListV1, "list: version 1 is older than its documents' (2)"),
            ];
            foreach (var (first, later, message) in cases)
            {
                var made = await DocumentsHost.Restartable((attempt, self) => self.Declare(attempt == 1 ? first : later));
                made.FailAttempt();
                await Rejection(made.RetryAsync());
                Sequence(["object_failed", "activation_failed"], made.Host.ErrorKinds, message);
                Equal($"app.restartable(1): {message}", made.Host.Failure, message);
            }
        }),
        Case("documents.slot.persisted-singletons-load-once-per-object-at-activation-not-again-on-a-retry-and-survive-the-host", async () =>
        {
            var store = await MemoryStore(outbox: true);
            var first = await SlotPrefsIn(store);
            Json("""{"theme":"light","n":0}""", first.Prefs().Value, "initial");
            await first.Prefs().ChangeAsync(draft => { draft["theme"] = "dark"; }, DocumentsBg);
            await first.Prefs().ChangeAsync(draft => { draft["n"] = 1; }, DocumentsBg);
            await first.Object.Host.CloseAsync();
            var reads = DocumentsCountReads(store);
            var second = await SlotPrefsIn(store);
            Json("""{"theme":"dark","n":1}""", second.Prefs().Value, "loaded");
            Equal(1, reads(), "one read");
            await second.Object.RestartAsync();
            Json("""{"theme":"dark","n":1}""", second.Prefs().Value, "after a retry");
            Equal(1, reads(), "no read on the retry");
            await second.Prefs().RetireAsync(DocumentsBg);
            Json("""{"theme":"light","n":0}""", second.Prefs().Value, "retired");
            Sequence(["document.create", "change:delta", "document.retire"], await PersistenceOutboxKinds(store), "the outbox");
            store.Reads = null;
            await second.Object.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.slot.persisted-singletons-migrate-a-stored-older-version-at-load-the-next-change-writes-the-migrated-base-back", async () =>
        {
            var store = await MemoryStore(outbox: true);
            var old = await SlotPrefsIn(store);
            await old.Prefs().ChangeAsync(draft => { draft["n"] = 1; }, DocumentsBg);
            await old.Object.Host.CloseAsync();
            var v2 = DocumentsDefine("prefs", Obj(("theme", Str()), ("n", Int())), 2, () => new JsonObject { ["theme"] = "light", ["n"] = 0 }, persist: true,
                migrate: (value, _) => new JsonObject { ["theme"] = "migrated", ["n"] = ChordJson.AsNumber(value["n"]) });
            var (made, prefs) = await SlotPrefsIn(store, v2);
            Json("""{"theme":"migrated","n":1}""", prefs().Value, "migrated at load");
            await prefs().ChangeAsync(draft => { draft["n"] = 2; }, DocumentsBg);
            Equal("change:base", (await PersistenceOutboxKinds(store))[^1], "the migrated base written back");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
        Case("documents.slot.persisted-singletons-fail-the-activation-broken-for-a-newer-stored-version-or-another-fingerprint-at-the-same-version-d9", async () =>
        {
            var store = await MemoryStore();
            var v2 = DocumentsDefine("prefs", Obj(("theme", Str()), ("n", Int())), 2, () => new JsonObject { ["theme"] = "light", ["n"] = 0 }, persist: true);
            var edited = DocumentsDefine("prefs", Obj(("theme", Str())), 2, () => new JsonObject { ["theme"] = "light" }, persist: true);
            var writer = await SlotPrefsIn(store, v2);
            await writer.Prefs().ChangeAsync(draft => { draft["n"] = 1; }, DocumentsBg);
            await writer.Object.Host.CloseAsync();
            foreach (var (definition, message) in new[]
            {
                (SlotPrefs, "prefs: stored at version 2, newer than the code's 1"),
                (edited, "prefs: stored with another schema at version 2 (a schema changed without a bump)"),
            })
            {
                var host = new DocumentsHost(store);
                await Rejection(host.GetAsync("app.store", "app.store", (_, self) => self.Declare(definition)));
                Equal($"app.store: {message}", host.Failure, message);
                await host.CloseAsync();
            }
            await store.DisposeAsync();
        }),
        Case("documents.slot.pisharp-persisted-singletons-a-flipped-persist-fails-the-activation-broken", async () =>
        {
            var inMemory = DocumentsDefine("prefs", Obj(("theme", Str()), ("n", Int())), 1, () => new JsonObject { ["theme"] = "light", ["n"] = 0 });
            var store = await MemoryStore();
            var made = await DocumentsHost.StoreRestartable((attempt, self) => self.Declare(attempt == 1 ? SlotPrefs : inMemory), store);
            made.FailAttempt();
            await Rejection(made.RetryAsync());
            Equal("app.store: prefs: declared in memory, its documents are not", made.Host.Failure, "the failure");
            await made.Host.CloseAsync();
            await store.DisposeAsync();
        }),
    ];
}
