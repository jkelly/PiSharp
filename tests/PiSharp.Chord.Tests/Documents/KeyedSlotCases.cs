using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/keyed-slot.test.ts
//
// Keyed slots (slice 6a §4, §10, §11): keyed reads with a context, per-key watches and at(key) sources with their pins, persisted keyed
// documents loaded on demand on the object's line (one load per key, shared), and their eviction. Upstream's cases serve app.restartable
// or app.store through createHost (restartable, storeRestartable, loaded; phase 6); here they run on the hand-built host
// (Documents/DocumentsHost.cs). Mappings:
// - vi.spyOn(store, "read") and the file's holdNextRead are the store's Reads seam (DocumentsCountReads, DocumentsHoldNextRead);
//   holdNextCommit is ChordStore.NextCommit (ChordSupport.cs); fakeTimers is a FakeTimers the host's documents time their residency with;
//   flushed() is Flushed, a short real delay (continuations run on the thread pool).
// - A cancelled context's abort carries no reason in .NET: upstream's rejects.toThrow("gone") is an OperationCanceledException.
// - Upstream's host delivers watch frames on microtasks, a frame per commit; the hand-built host's queue runs when a case calls Delivered().
// - The it.each case is two cases, its rows' names in the ids. Where upstream starts a write in a factory and waits with flushed() for it,
//   the case awaits the write's task.
// - "a still-valid waiter sharing that load": upstream's consumer reads the member notes through its reference, as the provider's own code
//   may (the object's own token); here that read is a Keyed handle of the slot under the object's own token (Documents.Own).
// - "at(key) is initial() while absent, a state member, derive and on source": its self.on half and the member waits for phase 6 (the host's
//   on and members); its derive half runs here as a pisharp- case.
internal static partial class Program
{
    private static readonly StateDefinition KeyedMemo = DocumentsDefine("notes.memo", Obj(("title", Str())), 1, () => new JsonObject { ["title"] = "" }, keyed: true);

    private static readonly StateDefinition KeyedNote = DocumentsDefine("notes.note", Obj(("title", Str())), 1, () => new JsonObject { ["title"] = "" }, keyed: true,
        persist: true);

    private static readonly JsonObject KeyedFailedBroken = new() { ["kind"] = "failed", ["cause"] = new JsonObject { ["kind"] = "broken" } };

    /// <summary>Runs <paramref name="run"/> with a fresh memory store with an outbox, closed after it (upstream's <c>freshStore</c> and
    /// <c>afterEach</c>).</summary>
    private static async Task KeyedWithStore(Func<ChordStore, Task> run)
    {
        var store = await MemoryStore(outbox: true);
        try
        {
            await run(store);
        }
        finally
        {
            store.Reads = null;
            await store.DisposeAsync();
        }
    }

    /// <summary>app.store over <paramref name="store"/> declaring <c>notes.note</c> (and what <paramref name="declare"/> adds) per attempt;
    /// <c>Notes()</c> is the latest attempt's handle (upstream <c>persisted</c>).</summary>
    private static async Task<(DocumentsObject Object, Func<IKeyedHandle> Notes)> KeyedPersisted(ChordStore store,
        Action<int, DocumentsAttempt, IKeyedHandle>? declare = null, TimeProvider? time = null)
    {
        IKeyedHandle? notes = null;
        var made = await DocumentsHost.StoreRestartable((attempt, self) =>
        {
            notes = self.KeyedState(KeyedNote);
            declare?.Invoke(attempt, self, notes);
        }, store, time: time);
        return (made, () => notes!);
    }

    /// <summary>Writes each title at its key in <paramref name="store"/>'s <c>notes.note</c> documents through a host of its own (upstream
    /// <c>seed</c>).</summary>
    private static async Task KeyedSeed(ChordStore store, IEnumerable<(string Key, string Title)> entries)
    {
        var (made, notes) = await KeyedPersisted(store);
        foreach (var (key, title) in entries) await notes().ChangeAsync(key, KeyedTitle(title), DocumentsBg);
        await made.Host.CloseAsync();
    }

    /// <summary>A change setting the draft's title.</summary>
    private static Action<JsonObject> KeyedTitle(string title) => draft => draft["title"] = title;

    /// <summary>Iterates every key of <paramref name="handle"/>.</summary>
    private static async Task KeyedAllKeys(IKeyedHandle handle)
    {
        await foreach (var _ in handle.Keys(DocumentsBg))
        {
        }
    }

    /// <summary>A weak reference to the source of <paramref name="key"/> in <paramref name="slot"/>, made in a frame of its own.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference KeyedWeakSource(KeyedSlot slot, string key) => new(slot.Source(key) ?? throw new InvalidOperationException($"no source for {key}"));

    /// <summary>A weak reference to the value the cold get of <paramref name="key"/> reads, made in a frame of its own.</summary>
    private static async Task<WeakReference> KeyedWeakGet(IKeyedHandle handle, string key) => new(await handle.GetAsync(key, DocumentsBg));

    /// <summary>The tx.retire of a cold key stored by other code (<paramref name="version"/>, and a schema with <c>n</c>) fails the owner
    /// broken as a read would, and keeps it (the it.each case).</summary>
    private static Task KeyedRetireUnreadable(long version, string message) => KeyedWithStore(async store =>
    {
        // Written by other code: a newer version, or the same version with another schema.
        var other = DocumentsDefine("notes.note", Obj(("title", Str()), ("n", Str())), version, () => new JsonObject { ["title"] = "", ["n"] = "" }, keyed: true, persist: true);
        Task? write = null;
        var writer = await DocumentsHost.StoreRestartable((_, self) => write = self.KeyedState(other).ChangeAsync("a", draft =>
        {
            draft["title"] = "kept";
            draft["n"] = "1";
        }, DocumentsBg), store);
        await write!;
        await writer.Host.CloseAsync();
        var (made, notes) = await KeyedPersisted(store);
        DocumentsRejectsWith(await Rejection(notes().RetireAsync("a", DocumentsBg)), message, "the retirement");
        Sequence(["object_failed"], made.Host.ErrorKinds, "the errors");
        Json(KeyedFailedBroken.ToJsonString(), made.State, "the owner");
        await made.Host.CloseAsync();
        var reader = await DocumentsHost.StoreRestartable((_, self) => self.Declare(other), store);
        var rows = await store.ReadAsync(executor => executor.AllAsync("SELECT key FROM chord_documents"), DocumentsBg);
        Sequence(["a"], rows.Select(row => (string)row["key"]!), "kept");
        await reader.Host.CloseAsync();
    });

    private static IEnumerable<(string, Func<Task>)> DocumentsKeyedSlotCases() =>
    [
        Case("documents.keyed-slot.keyed-reads-in-memory-get-and-keys-take-a-context-keys-come-in-code-point-order", async () =>
        {
            IKeyedHandle? memos = null;
            var made = await DocumentsHost.Restartable((_, self) => memos = self.KeyedState(KeyedMemo));
            foreach (var key in new[] { "b", "\U0001F600", "a", "￿" }) await memos!.ChangeAsync(key, KeyedTitle(key), DocumentsBg);
            Json("""{"title":"a"}""", await memos!.GetAsync("a", DocumentsBg), "a");
            Check(await memos.GetAsync("z", DocumentsBg) is null, "z is absent");
            Sequence(["a", "b", "￿", "\U0001F600"], await DocumentsKeysOf(memos), "the keys");
            await made.Host.CloseAsync();
        }),
        Case("documents.keyed-slot.migration-on-access-a-newer-version-migrates-each-in-memory-key-when-it-is-read-a-key-whose-migrate-fails-fails-the-owner-then", async () =>
        {
            var migrated = new List<string>();
            var memoV2 = DocumentsDefine("notes.memo", Obj(("title", Str())), 2, () => new JsonObject { ["title"] = "" }, keyed: true, migrate: (old, _) =>
            {
                var title = (string)old["title"]!;
                if (title == "bad") throw new InvalidOperationException("cannot migrate");
                lock (migrated) migrated.Add(title);
                return new JsonObject { ["title"] = $"{title}!" };
            });
            var memos = new List<IKeyedHandle>();
            var made = await DocumentsHost.Restartable((attempt, self) => memos.Add(self.KeyedState(attempt == 1 ? KeyedMemo : memoV2)));
            foreach (var key in new[] { "a", "b", "c" }) await memos[0].ChangeAsync(key, KeyedTitle(key == "c" ? "bad" : key), DocumentsBg);
            await made.RestartAsync();
            // The activation migrated nothing: no key was read.
            Sequence([], migrated, "after the activation");
            Json("""{"title":"a!"}""", await memos[1].GetAsync("a", DocumentsBg), "a migrated");
            Sequence(["a"], migrated, "after a's read");
            await made.Attempts[1].TxAsync(async tx => Json("""{"title":"b!"}""", await tx.DraftAsync(memos[1], "b"), "b's draft"), DocumentsBg);
            Sequence(["a", "b"], migrated, "after b's draft");
            DocumentsRejectsWith(await Rejection(memos[1].GetAsync("c", DocumentsBg)), "cannot migrate", "c");
            Sequence(["object_failed", "object_failed"], made.Host.ErrorKinds, "the errors");
            Json(KeyedFailedBroken.ToJsonString(), made.State, "the owner");
        }),
        Case("documents.keyed-slot.keys-and-the-text-rules-a-lone-surrogate-in-a-key-is-u-fffd-at-every-entry-point-in-memory-and-in-the-store-nul-is-refused", () => KeyedWithStore(async store =>
        {
            var (made, notes) = await KeyedPersisted(store);
            await notes().ChangeAsync("x\uD800", KeyedTitle("one"), DocumentsBg);
            Sequence(["x�"], await DocumentsKeysOf(notes()), "the key");
            // One key: the change through either spelling reaches the same document and record.
            await notes().ChangeAsync("x�", KeyedTitle("two"), DocumentsBg);
            await made.Attempts[0].TxAsync(async tx =>
            {
                var draft = await tx.DraftAsync(notes(), "x\uD800");
                draft["title"] = (string)draft["title"]! + "!";
            }, DocumentsBg);
            Json("""{"title":"two!"}""", await notes().GetAsync("x\uD800", DocumentsBg), "the get");
            Json("""{"title":"two!"}""", (await notes().WatchAsync("x\uD800", DocumentsBg))?.Value, "the watch");
            foreach (var (what, use) in new (string, Func<Task>)[]
            {
                ("change", () => notes().ChangeAsync("a\0b", KeyedTitle("nul"), DocumentsBg)),
                ("get", () => notes().GetAsync("a\0b", DocumentsBg)),
                ("watch", () => notes().WatchAsync("a\0b", DocumentsBg)),
                ("retire", () => notes().RetireAsync("a\0b", DocumentsBg)),
            })
                DocumentsMatches("""{"code":"invalid","reason":"args"}""", await Rejection(use()), what);
            await notes().RetireAsync("x\uD800", DocumentsBg);
            Sequence([], await DocumentsKeysOf(notes()), "retired");
            await made.Host.CloseAsync();
            // The store has the key as memory had it.
            var again = await KeyedPersisted(store);
            Check(await again.Notes().GetAsync("x�", DocumentsBg) is null, "retired in the store");
            await again.Object.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.keys-and-the-text-rules-in-memory-too-one-key-per-spelling-nul-refused", async () =>
        {
            IKeyedHandle? memos = null;
            var made = await DocumentsHost.Restartable((_, self) => memos = self.KeyedState(KeyedMemo));
            await memos!.ChangeAsync("x\uDC00", KeyedTitle("one"), DocumentsBg);
            Json("""{"title":"one"}""", await memos.GetAsync("x�", DocumentsBg), "the other spelling");
            Sequence(["x�"], await DocumentsKeysOf(memos), "the key");
            DocumentsMatches("""{"code":"invalid"}""", await Rejection(memos.ChangeAsync("\0", KeyedTitle("nul"), DocumentsBg)), "NUL");
            await made.Host.CloseAsync();
        }),
        Case("documents.keyed-slot.keys-and-the-text-rules-the-empty-key-is-a-key-listed-first-also-on-a-later-page", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("", "empty"), .. Enumerable.Range(0, 120).Select(i => ($"k{i:000}", "x"))]);
            var (made, notes) = await KeyedPersisted(store);
            var keys = await DocumentsKeysOf(notes());
            Equal(121, keys.Count, "the keys");
            Equal("", keys[0], "the first");
            Json("""{"title":"empty"}""", await notes().GetAsync("", DocumentsBg), "the empty key's document");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.keyed-watches-watch-key-is-undefined-when-absent-it-follows-the-address-through-a-retirement-and-a-recreation", async () =>
        {
            IKeyedHandle? memos = null;
            var made = await DocumentsHost.Restartable((_, self) => memos = self.KeyedState(KeyedMemo));
            Check(await memos!.WatchAsync("a", DocumentsBg) is null, "absent");
            await memos.ChangeAsync("a", KeyedTitle("x"), DocumentsBg);
            var watch = (await memos.WatchAsync("a", DocumentsBg))!;
            Json("""{"title":"x"}""", watch.Value, "the watch's value");
            var changes = ChangesOf(watch);
            await memos.ChangeAsync("a", KeyedTitle("y"), DocumentsBg);
            made.Host.Delivered();
            await memos.RetireAsync("a", DocumentsBg);
            made.Host.Delivered();
            await memos.ChangeAsync("a", KeyedTitle("z"), DocumentsBg);
            made.Host.Delivered();
            await made.Attempts[0].TxAsync(async tx =>
            {
                await tx.RetireAsync(memos, "a");
                (await tx.DraftAsync(memos, "a"))["title"] = "w";
            }, DocumentsBg);
            made.Host.Delivered();
            DocumentsCalls("""
                [{"kind":"value","value":{"title":"y"},"ops":[["s",["title"],"y"]],"retired":false},
                 {"kind":"value","value":{"title":""},"ops":[["r",{"title":""}]],"retired":true},
                 {"kind":"value","value":{"title":"z"},"ops":[["r",{"title":"z"}]],"retired":false},
                 {"kind":"value","value":{"title":"w"},"ops":[["r",{"title":"w"}]],"retired":true}]
                """, changes, "the calls");
            await made.Host.CloseAsync();
            Equal(WatchEnd.Ended, await watch.Closed, "the end");
        }),
        Case("documents.keyed-slot.keyed-watches-a-stopped-watch-unpins-its-key-its-source-is-collected", async () =>
        {
            IKeyedHandle? memos = null;
            var made = await DocumentsHost.Restartable((_, self) => memos = self.KeyedState(KeyedMemo));
            await memos!.ChangeAsync("a", KeyedTitle("x"), DocumentsBg);
            IWatchHandle<JsonNode?>? watch = (await memos.WatchAsync("a", DocumentsBg))!;
            // The watch's source: the slot's per-key source while pinned.
            var slot = ((Keyed)memos).Slot;
            var source = KeyedWeakSource(slot, "a");
            await watch.StopAsync();
            watch = null;
            await Flushed();
            await ExpectCollected(source);
            await made.Host.CloseAsync();
        }),
        Case("documents.keyed-slot.pisharp-at-key-is-initial-while-absent-a-derive-source-retirement-arrives-with-the-flag", async () =>
        {
            IKeyedHandle? memos = null;
            IWatchable<JsonNode?>? pinned = null;
            Derive? upper = null;
            var made = await DocumentsHost.Restartable((_, self) =>
            {
                memos = self.KeyedState(KeyedMemo);
                pinned = memos.At("a");
                upper = self.Derive([pinned], values => ((string)((JsonNode)values[0]!)["title"]!).ToUpperInvariant());
            });
            Json("""{"title":""}""", pinned!.Value, "initial() while absent");
            var changes = ChangesOf(await upper!.WatchAsync(DocumentsBg));
            await memos!.ChangeAsync("a", KeyedTitle("x"), DocumentsBg);
            made.Host.Delivered();
            Json("""{"title":"x"}""", pinned.Value, "written");
            await memos.RetireAsync("a", DocumentsBg);
            made.Host.Delivered();
            await Flushed();
            DocumentsCalls("""
                [{"kind":"value","value":"X","ops":[["r","X"]],"retired":false},
                 {"kind":"value","value":"","ops":[["r",""]],"retired":true}]
                """, changes, "the derive's calls");
            await made.Host.CloseAsync();
        }),
        Case("documents.keyed-slot.at-key-only-in-the-factory", async () =>
        {
            IKeyedHandle? memos = null;
            var made = await DocumentsHost.Restartable((_, self) => memos = self.KeyedState(KeyedMemo));
            var error = Throws<InvalidOperationException>(() => memos!.At("a"), "at outside the factory");
            Check(error.Message.Contains("valid only in the factory", StringComparison.Ordinal), error.Message);
            await made.Host.CloseAsync();
        }),
        Case("documents.keyed-slot.at-key-a-persisted-key-is-loaded-before-the-object-goes-live-and-stays-pinned-until-the-next-factory-returns", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x"), ("b", "y")]);
            IWatchable<JsonNode?>? pinned = null;
            var (made, notes) = await KeyedPersisted(store, (attempt, _, handle) => pinned = handle.At(attempt == 1 ? "a" : "b"));
            Json("""{"title":"x"}""", pinned!.Value, "a, loaded");
            var slot = ((Keyed)notes()).Slot;
            var first = KeyedWeakSource(slot, "a");
            await made.RestartAsync();
            Json("""{"title":"y"}""", pinned.Value, "b, loaded");
            Check(slot.Source("a") is null, "a is unpinned");
            await ExpectCollected(first);
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-load-a-cold-key-once-a-cached-get-touches-no-sqlite-an-absent-key-is-not-cached", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x")]);
            var (made, notes) = await KeyedPersisted(store);
            var reads = DocumentsCountReads(store);
            Json("""{"title":"x"}""", await notes().GetAsync("a", DocumentsBg), "a");
            Equal(1, reads(), "one read");
            Json("""{"title":"x"}""", await notes().GetAsync("a", DocumentsBg), "a cached");
            Equal(1, reads(), "still one read");
            Check(await notes().GetAsync("z", DocumentsBg) is null, "z");
            Check(await notes().GetAsync("z", DocumentsBg) is null, "z again");
            Equal(3, reads(), "absent keys read each time");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-two-readers-share-one-load-a-cancelled-waiter-rejects-alone", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x")]);
            var (made, notes) = await KeyedPersisted(store);
            var reads = DocumentsCountReads(store);
            // Hold the line so that the load waits behind it.
            var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tx = made.Attempts[0].TxAsync(_ => holding.Task, DocumentsBg);
            using var cancel = new CancellationTokenSource();
            var first = notes().GetAsync("a", ChordContext.WithAbortSignal(cancel.Token, DocumentsBg));
            var second = notes().GetAsync("a", DocumentsBg);
            cancel.Cancel();
            Check(await Rejection(first) is OperationCanceledException, "the cancelled waiter rejects");
            holding.TrySetResult();
            await tx;
            Json("""{"title":"x"}""", await second, "the other gets the document");
            Equal(1, reads(), "one read");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-a-cold-get-racing-a-draft-of-the-same-key-the-load-installs-only-while-the-key-is-absent", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x")]);
            var (made, notes) = await KeyedPersisted(store);
            Task<JsonObject?>? get = null;
            await made.Attempts[0].TxAsync(async tx =>
            {
                get = notes().GetAsync("a", DocumentsBg);
                var draft = await tx.DraftAsync(notes(), "a");
                Equal("x", (string?)draft["title"], "the draft");
                draft["title"] = "y";
            }, DocumentsBg);
            Json("""{"title":"y"}""", await get!, "the get");
            Json("""{"title":"y"}""", await notes().GetAsync("a", DocumentsBg), "after");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-keys-come-from-the-store-in-code-point-order-a-page-per-line-job", () => KeyedWithStore(async store =>
        {
            var numbered = Enumerable.Range(0, 150).Select(i => $"k{i:000}").ToList();
            await KeyedSeed(store, [.. numbered.Select(key => (key, "x")), ("\U0001F600", "x"), ("￿", "x")]);
            var (made, notes) = await KeyedPersisted(store);
            Sequence([.. numbered, "￿", "\U0001F600"], await DocumentsKeysOf(notes()), "the keys");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-tx-retire-of-a-cold-key-finds-it-without-loading-it-an-absent-key-retires-nothing", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x")]);
            var (made, notes) = await KeyedPersisted(store);
            var seq = store.LastSeq;
            await notes().RetireAsync("z", DocumentsBg);
            Equal(seq, store.LastSeq, "nothing retired");
            await notes().RetireAsync("a", DocumentsBg);
            Equal(seq + 1, store.LastSeq, "a retired");
            Check(await notes().GetAsync("a", DocumentsBg) is null, "a is absent");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-tx-retire-of-a-cold-key-stored-at-a-newer-version-fails-the-owner-broken-as-a-read-would-and-keeps-it-r15-audit",
            () => KeyedRetireUnreadable(2, "stored at version 2, newer than the code's 1")),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-tx-retire-of-a-cold-key-stored-at-another-fingerprint-at-its-version-fails-the-owner-broken-as-a-read-would-and-keeps-it-r15-audit",
            () => KeyedRetireUnreadable(1, "stored with another schema at version 1")),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-a-retirement-cut-by-its-attempts-seal-while-it-finds-the-document-fails-no-successor-audit", () => KeyedWithStore(async store =>
        {
            var newer = DocumentsDefine("notes.note", Obj(("title", Str())), 2, () => new JsonObject { ["title"] = "" }, keyed: true, persist: true);
            Task? write = null;
            var writer = await DocumentsHost.StoreRestartable((_, self) => write = self.KeyedState(newer).ChangeAsync("a", KeyedTitle("kept"), DocumentsBg), store);
            await write!;
            await writer.Host.CloseAsync();
            var (made, notes) = await KeyedPersisted(store);
            // The find's read is held past the seal: the retirement resumes on a cut transaction.
            var (entered, open) = DocumentsHoldNextRead(store);
            var retired = Rejection(notes().RetireAsync("a", DocumentsBg));
            await entered;
            made.FailAttempt();
            await made.RetryAsync();
            open();
            DocumentsMatches("""{"code":"released"}""", await retired, "the retirement");
            await Flushed();
            Json("""{"kind":"live"}""", made.State, "the successor");
            Equal(1, made.Host.ErrorKinds.Count(kind => kind == "object_failed"), "one object_failed");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-a-sealed-handle-reads-cached-documents-a-cold-read-is-refused-released", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x"), ("b", "y")]);
            var (made, notes) = await KeyedPersisted(store);
            var old = notes();
            await old.GetAsync("a", DocumentsBg);
            await made.RestartAsync();
            Json("""{"title":"x"}""", await old.GetAsync("a", DocumentsBg), "cached");
            DocumentsMatches("""{"code":"released"}""", await Rejection(old.GetAsync("b", DocumentsBg)), "cold");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-a-sealed-handle-joining-a-cold-load-in-flight-is-refused-released-rule-4", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("b", "y")]);
            var (made, notes) = await KeyedPersisted(store);
            var (reached, open) = DocumentsHoldNextRead(store);
            var old = notes();
            var first = old.GetAsync("b", DocumentsBg);
            await reached;
            var restarting = made.RestartAsync();
            await Flushed();
            DocumentsMatches("""{"code":"released"}""", await Rejection(old.GetAsync("b", DocumentsBg)), "the sealed handle joining");
            open();
            Json("""{"title":"y"}""", await first, "the load in flight");
            await restarting;
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-cold-reads-queued-on-the-line-before-the-objects-end-are-refused-released-when-they-run-after-it", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x"), ("b", "y"), ("c", "z")]);
            var (made, notes) = await KeyedPersisted(store);
            var (reached, open) = DocumentsHoldNextRead(store);
            var first = notes().GetAsync("a", DocumentsBg);
            await reached;
            var get = Rejection(notes().GetAsync("b", DocumentsBg));
            var watch = Rejection(notes().WatchAsync("c", DocumentsBg));
            var closing = made.Host.CloseAsync();
            open();
            DocumentsMatches("""{"code":"released"}""", await get, "the get");
            DocumentsMatches("""{"code":"released"}""", await watch, "the watch");
            // The load holding the line before the end finishes.
            Json("""{"title":"x"}""", await first, "the first");
            await closing;
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-cold-reads-queued-behind-an-admitted-step-are-refused-released-when-their-attempt-failed-live-meanwhile", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x")]);
            var p = DocumentsDefine("notes.p", Obj(("title", Str())), 1, () => new JsonObject { ["title"] = "" }, persist: true);
            ISingletonHandle? single = null;
            var (made, notes) = await KeyedPersisted(store, (_, self, _) => single = self.State(p));
            var (entered, release) = HoldNextCommit(store);
            var change = single!.ChangeAsync(KeyedTitle("p"), DocumentsBg);
            await entered;
            var get = Rejection(notes().GetAsync("a", DocumentsBg));
            var watch = Rejection(notes().WatchAsync("a", DocumentsBg));
            var keys = Rejection(KeyedAllKeys(notes()));
            // The attempt is sealed; the object stays open.
            made.FailLive(made.Attempts[0], new ChordError("unavailable", "boom"));
            release();
            await change;
            DocumentsMatches("""{"code":"released"}""", await get, "the get");
            DocumentsMatches("""{"code":"released"}""", await watch, "the watch");
            DocumentsMatches("""{"code":"released"}""", await keys, "the keys");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-a-still-valid-waiter-sharing-that-load-gets-the-document-only-the-sealed-handles-read-is-refused", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x")]);
            DocumentsAttempt? self = null;
            IKeyedHandle? notes = null;
            var made = await new DocumentsHost(store).GetAsync("app.store", "app.store", (_, attempt) =>
            {
                self = attempt;
                notes = attempt.KeyedState(KeyedNote);
            });
            // The member read through a reference: under the object's own token.
            var member = new Keyed(((Keyed)notes!).Slot, made.Documents.Own, KeyedNote.Version, self!);
            var (entered, release) = HoldNextCommit(store);
            var change = notes.ChangeAsync("b", KeyedTitle("y"), DocumentsBg);
            await entered;
            var sealedRead = Rejection(notes.GetAsync("a", DocumentsBg));
            // A consumer reads as the provider's own code may (the object's own token), joining the same load.
            var shared = member.GetAsync("a", DocumentsBg);
            await Flushed();
            made.FailLive(self!, new ChordError("unavailable", "boom"));
            release();
            await change;
            DocumentsMatches("""{"code":"released"}""", await sealedRead, "the sealed handle's read");
            Json("""{"title":"x"}""", await shared, "the shared read");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-a-load-cut-by-the-seal-never-installs-a-successors-retirement-stands", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x")]);
            var (made, notes) = await KeyedPersisted(store);
            var (reached, open) = DocumentsHoldNextRead(store);
            var old = notes();
            var cut = made.Attempts[0].TxAsync(async tx => { (await tx.DraftAsync(old, "a"))["title"] = "z"; }, DocumentsBg);
            await reached;
            // The seal cuts the transaction and releases the line; the successor retires the key.
            await made.RestartAsync();
            await notes().RetireAsync("a", DocumentsBg);
            open();
            DocumentsMatches("""{"code":"released"}""", await Rejection(cut), "the cut transaction");
            Check(await notes().GetAsync("a", DocumentsBg) is null, "the retirement stands");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-a-plugin-migrate-throwing-a-chorderror-fails-its-owner-like-any-other-error", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x")]);
            var v2 = DocumentsDefine("notes.note", Obj(("title", Str())), 2, () => new JsonObject { ["title"] = "" }, keyed: true, persist: true,
                migrate: (_, _) => throw new ChordError("unavailable", "plugin says no"));
            IKeyedHandle? notes = null;
            var made = await DocumentsHost.StoreRestartable((_, self) => notes = self.KeyedState(v2), store);
            DocumentsMatches("""{"message":"plugin says no"}""", await Rejection(notes!.GetAsync("a", DocumentsBg)), "the get");
            Sequence(["object_failed"], made.Host.ErrorKinds, "the errors");
            Json(KeyedFailedBroken.ToJsonString(), made.State, "the owner");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.cold-keyed-loads-on-the-line-a-key-whose-migrate-fails-fails-its-owner-broken-the-get-rejects-with-the-cause", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x")]);
            var v2 = DocumentsDefine("notes.note", Obj(("title", Str())), 2, () => new JsonObject { ["title"] = "" }, keyed: true, persist: true,
                migrate: (_, _) => throw new InvalidOperationException("cannot migrate"));
            IKeyedHandle? notes = null;
            var made = await DocumentsHost.StoreRestartable((_, self) => notes = self.KeyedState(v2), store);
            DocumentsMatches("""{"code":"unavailable","message":"cannot migrate"}""", await Rejection(notes!.GetAsync("a", DocumentsBg)), "the get", containing: true);
            Sequence(["object_failed"], made.Host.ErrorKinds, "the errors");
            Json(KeyedFailedBroken.ToJsonString(), made.State, "the owner");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.eviction-an-unpinned-key-idle-for-the-retention-leaves-memory-a-pinned-one-stays", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x"), ("b", "y")]);
            var timers = new FakeTimers();
            var (made, notes) = await KeyedPersisted(store, time: timers);
            var reads = DocumentsCountReads(store);
            await notes().GetAsync("a", DocumentsBg);
            var watch = (await notes().WatchAsync("b", DocumentsBg))!;
            Equal(2, reads(), "two loads");
            timers.AdvanceTimersByTime(Residency.RetentionMs + 1_000);
            await Flushed();
            await notes().GetAsync("a", DocumentsBg);
            await notes().GetAsync("b", DocumentsBg);
            Equal(3, reads(), "a evicted, b pinned");
            await watch.StopAsync();
            await Flushed();
            timers.AdvanceTimersByTime(Residency.RetentionMs + 1_000);
            await Flushed();
            await notes().GetAsync("b", DocumentsBg);
            Equal(4, reads(), "b evicted once unpinned");
            await made.Host.CloseAsync();
        })),
        Case("documents.keyed-slot.eviction-an-evicted-document-is-collected", () => KeyedWithStore(async store =>
        {
            await KeyedSeed(store, [("a", "x")]);
            var timers = new FakeTimers();
            var (made, notes) = await KeyedPersisted(store, time: timers);
            var collected = await KeyedWeakGet(notes(), "a");
            timers.AdvanceTimersByTime(Residency.RetentionMs + 1_000);
            await Flushed();
            await ExpectCollected(collected);
            Json("""{"title":"x"}""", await notes().GetAsync("a", DocumentsBg), "loaded again");
            await made.Host.CloseAsync();
        })),
    ];
}
