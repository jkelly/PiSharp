using System.Numerics;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Store;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/persistence.test.ts
//
// Persisted documents' store side (slice 6a §8, §10): loads with the version and fingerprint checks and migrate, finds, keys, and the writes
// one commit stages (creation, base or delta by checkpointWhen, retirement). Every case runs on hand-built values, as upstream's do: a
// Persistence over a memory store, trackers, drafts and prepared changes made by hand, commits with an open fence. Upstream's beforeEach and
// afterEach are each case's own store, closed at its end. The serialization case's BigInt (JSON.stringify throws a TypeError) is a JSON
// value no JSON text can hold, a BigInteger: ChordJson.Stringify refuses it with an ArgumentException.
internal static partial class Program
{
    private static readonly StateDefinition PersistenceNote = StateDefinition.Define("notes.note", new()
    {
        Schema = Obj(("title", Str()), ("n", Int())),
        Version = 1,
        Initial = () => new JsonObject { ["title"] = "", ["n"] = 0 },
        Persist = true,
        CheckpointWhen = (_, _, info) => info.DeltasSinceBase >= 2,
    });

    /// <summary>A persisted definition named <c>notes.note</c> that never checkpoints.</summary>
    private static StateDefinition PersistenceDefinition(long version, JsonNode schema, Func<JsonObject> initial, Func<JsonObject, long, JsonObject>? migrate = null) =>
        StateDefinition.Define("notes.note", new()
        {
            Schema = schema,
            Version = version,
            Initial = initial,
            Migrate = migrate,
            Persist = true,
            CheckpointWhen = (_, _, _) => false,
        });

    /// <summary>Each case's store and the persistence of <c>notes.note</c> in it.</summary>
    private static async Task PersistenceCase(Func<ChordStore, Persistence, Task> run)
    {
        var store = await MemoryStore(outbox: true);
        try
        {
            await run(store, new Persistence(store, "app.store", "chord", "notes.note"));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    /// <summary>Stages the change of <paramref name="before"/> made by <paramref name="after"/> at <paramref name="key"/> and commits it;
    /// returns what is stored afterwards (upstream <c>change</c>).</summary>
    private static async Task<Stored?> PersistenceChange(ChordStore store, Persistence notes, string key, JsonObject? before, Action<JsonObject> after, Stored? stored,
        StateDefinition? definition = null)
    {
        definition ??= PersistenceNote;
        var tracker = Tracker.Track(before ?? definition.Initial(ChordContext.Background));
        var draft = tracker.BeginChange();
        after(draft.State);
        var prepared = draft.Prepare();
        var writes = new List<IStagedWrite>();
        var result = notes.Stage(key, definition, stored, new DocumentChange(prepared, false), writes);
        await store.CommitAsync(writes, CommitFence.Open, ChordContext.Background);
        tracker.Adopt(prepared);
        return DocumentsInternal.StoredAfter(result);
    }

    /// <summary>The document at <paramref name="key"/> read and decoded by <paramref name="definition"/>, null when absent (upstream
    /// <c>load</c>).</summary>
    private static async Task<Loaded?> PersistenceLoad(Persistence notes, string key, StateDefinition definition, ChordContext context)
    {
        var document = await notes.ReadAsync(key, context);
        return document is null ? null : notes.Decode(document, definition, context);
    }

    /// <summary>What <paramref name="loaded"/> is, as upstream's toEqual sees it.</summary>
    private static JsonObject PersistenceLoadedJson(Loaded loaded) => new()
    {
        ["value"] = loaded.Value.DeepClone(),
        ["stored"] = PersistenceStoredJson(loaded.Stored),
    };

    private static JsonObject PersistenceStoredJson(Stored stored) => new()
    {
        ["id"] = stored.Id,
        ["revision"] = stored.Revision,
        ["version"] = stored.Version,
        ["deltasSinceBase"] = stored.DeltasSinceBase,
    };

    /// <summary>The kinds of the outbox's writes, per commit (a change by its content's kind; upstream <c>outboxKinds</c>).</summary>
    private static async Task<List<string>> PersistenceOutboxKinds(ChordStore store)
    {
        var commits = await store.Outbox!.ReadAsync(0, 100, ChordContext.Background);
        return [.. commits.Select(commit => string.Join(",", commit.Writes.Select(write =>
        {
            var kind = (string)write!["kind"]!;
            return kind == "document.change" ? $"change:{(string)write["content"]!["kind"]!}" : kind;
        })))];
    }

    private static IEnumerable<(string, Func<Task>)> DocumentsPersistenceCases() =>
    [
        Case("documents.persistence.loads-load-an-absent-document-as-undefined-and-a-stored-one-with-its-record", () => PersistenceCase(async (store, notes) =>
        {
            Check(await PersistenceLoad(notes, "a", PersistenceNote, ChordContext.Background) is null, "absent");
            var stored = await PersistenceChange(store, notes, "a", null, d => d["title"] = "x", null);
            var (_, counting, counts) = await Metered();
            var loaded = await PersistenceLoad(notes, "a", PersistenceNote, counting);
            Json($$$"""{"value":{"title":"x","n":0},"stored":{"id":{{{stored!.Id}}},"revision":1,"version":1,"deltasSinceBase":0}}""",
                PersistenceLoadedJson(loaded!), "the load");
            Equal(1.0, counts().GetValueOrDefault("chord.store.loads"), "loads counted");
        })),
        Case("documents.persistence.loads-migrate-an-older-stored-version-and-check-the-result-once", () => PersistenceCase(async (store, notes) =>
        {
            var v1 = PersistenceDefinition(1, Obj(("title", Str())), () => new JsonObject { ["title"] = "" });
            await PersistenceChange(store, notes, "a", null, d => d["title"] = "x", null, v1);
            var v2 = PersistenceDefinition(2, Obj(("title", Str()), ("n", Int())), () => new JsonObject { ["title"] = "", ["n"] = 0 },
                (old, _) => new JsonObject { ["title"] = ChordJson.ToJavaScriptString(old["title"]), ["n"] = 7 });
            var (_, counting, counts) = await Metered();
            var loaded = (await PersistenceLoad(notes, "a", v2, counting))!;
            Json("""{"title":"x","n":7}""", loaded.Value, "the migrated value");
            Equal(1L, loaded.Stored.Version, "the stored version");
            Equal(1.0, counts().GetValueOrDefault("chord.data.validations"), "one validation");
            // A migrate returning a wrong shape: the load is refused.
            var bad = PersistenceDefinition(2, Obj(("title", Str()), ("n", Int())), () => new JsonObject { ["title"] = "", ["n"] = 0 },
                (old, _) => new JsonObject { ["title"] = ChordJson.ToJavaScriptString(old["title"]) });
            PersistenceRejects(await Rejection(PersistenceLoad(notes, "a", bad, ChordContext.Background)), "migrate() from version 1 does not match", "a wrong shape");
        })),
        Case("documents.persistence.loads-refuse-a-stored-version-newer-than-the-codes-and-the-same-version-with-another-fingerprint-d9", () => PersistenceCase(async (store, notes) =>
        {
            await PersistenceChange(store, notes, "a", null, d => d["title"] = "x", null);
            var old = PersistenceDefinition(0, Obj(("title", Str()), ("n", Int())), () => new JsonObject { ["title"] = "", ["n"] = 0 });
            PersistenceRejects(await Rejection(PersistenceLoad(notes, "a", old, ChordContext.Background)), "stored at version 1, newer than the code's 0", "newer");
            var edited = PersistenceDefinition(1, Obj(("title", Str()), ("n", Int()), ("extra", Str())), () => new JsonObject { ["title"] = "", ["n"] = 0, ["extra"] = "" });
            PersistenceRejects(await Rejection(PersistenceLoad(notes, "a", edited, ChordContext.Background)), "stored with another schema at version 1", "edited");
        })),
        Case("documents.persistence.loads-find-a-live-record-without-loading-it-and-list-keys-in-code-point-order-by-pages", () => PersistenceCase(async (store, notes) =>
        {
            Check(await notes.FindAsync("a", ChordContext.Background) is null, "absent");
            foreach (var key in new[] { "b", "\U0001F600", "a", "￿" }) await PersistenceChange(store, notes, key, null, d => d["n"] = 1, null);
            var found = (await notes.FindAsync("a", ChordContext.Background))!;
            Equal((1L, 1L), (found.Stored.Revision, found.Stored.Version), "the found record");
            Check(found.Fingerprint.Length > 0, "a fingerprint");
            Sequence(["a", "b", "￿", "\U0001F600"], await notes.KeysAsync(null, 10, ChordContext.Background), "every key");
            Sequence(["￿"], await notes.KeysAsync("b", 1, ChordContext.Background), "a page after b");
        })),
        Case("documents.persistence.staged-writes-store-a-creation-as-a-base-changes-as-deltas-until-checkpointwhen-asks-for-a-base", () => PersistenceCase(async (store, notes) =>
        {
            var stored = await PersistenceChange(store, notes, "a", null, d => d["n"] = 1, null);
            Equal((1L, 0), (stored!.Revision, stored.DeltasSinceBase), "the creation");
            var value = (await PersistenceLoad(notes, "a", PersistenceNote, ChordContext.Background))!.Value;
            foreach (var n in new[] { 2, 3, 4 })
            {
                stored = await PersistenceChange(store, notes, "a", value, d => d["n"] = n, stored);
                value = (await PersistenceLoad(notes, "a", PersistenceNote, ChordContext.Background))!.Value;
            }
            Equal((4L, 0), (stored!.Revision, stored.DeltasSinceBase), "after the base");
            Json("""{"title":"","n":4}""", value, "the value");
            Sequence(["document.create", "change:delta", "change:delta", "change:base"], await PersistenceOutboxKinds(store), "the outbox");
            Equal(0, (await PersistenceLoad(notes, "a", PersistenceNote, ChordContext.Background))!.Stored.DeltasSinceBase, "loaded at the base");
        })),
        Case("documents.persistence.staged-writes-store-a-base-when-the-stored-version-is-older-than-the-definitions-the-migrated-base-written-back", () => PersistenceCase(async (store, notes) =>
        {
            var stored = await PersistenceChange(store, notes, "a", null, d => d["n"] = 1, null);
            var v2 = PersistenceDefinition(2, Obj(("title", Str()), ("n", Int())), () => new JsonObject { ["title"] = "", ["n"] = 0 }, (old, _) => old);
            var after = await PersistenceChange(store, notes, "a", new JsonObject { ["title"] = "", ["n"] = 1 }, d => d["n"] = 2, stored, v2);
            Equal((2L, 2L, 0), (after!.Version, after.Revision, after.DeltasSinceBase), "a base at version 2");
            Equal("change:base", (await PersistenceOutboxKinds(store))[^1], "the last commit");
            var loaded = (await PersistenceLoad(notes, "a", v2, ChordContext.Background))!;
            Equal(((double?)2, 2L), (ChordJson.AsNumber(loaded.Value["n"]), loaded.Stored.Version), "loaded at version 2");
        })),
        Case("documents.persistence.staged-writes-stage-a-retirement-before-a-recreation-and-number-creations-by-their-order-in-the-commit", () => PersistenceCase(async (store, notes) =>
        {
            var stored = (await PersistenceChange(store, notes, "a", null, d => d["n"] = 1, null))!;
            var writes = new List<IStagedWrite>();
            Prepared<JsonObject> Fresh(int n)
            {
                var tracker = Tracker.Track(PersistenceNote.Initial(ChordContext.Background));
                var draft = tracker.BeginChange();
                draft.State["n"] = n;
                return draft.Prepare();
            }
            var first = notes.Stage("b", PersistenceNote, null, new DocumentChange(Fresh(2), false), writes);
            var again = notes.Stage("a", PersistenceNote, stored, new DocumentChange(Fresh(3), true), writes);
            var gone = notes.Stage("c", PersistenceNote, null, new DocumentChange(null, true), writes);
            Check(first is StoredChange.Create { Version: 1 } && again is StoredChange.Create { Version: 1 } && gone is StoredChange.Retire, "two creations and a retirement");
            IReadOnlyList<long> ids = [];
            await store.CommitAsync(writes, new CommitFence(() => true, (_, created) => ids = created), ChordContext.Background);
            Sequence([stored.Id + 1, stored.Id + 2], ids, "the created ids");
            Json(PersistenceStoredJson(new Stored(stored.Id + 2, 1, 1, 0)).ToJsonString(), PersistenceStoredJson(DocumentsInternal.StoredAfter(again)!), "a's new record");
            Equal("document.retire,document.create,document.create", (await PersistenceOutboxKinds(store))[^1], "the last commit");
        })),
        Case("documents.persistence.staged-writes-serialize-at-staging-a-value-json-cannot-encode-fails-there-never-in-the-step-rule-8", () => PersistenceCase((_, notes) =>
        {
            // Drafts refuse such values when placed; a prepared change made by hand shows where the text is made.
            var value = new JsonObject { ["title"] = "", ["n"] = JsonValue.Create(new BigInteger(1)) };
            var prepared = new Prepared<JsonObject>(value, value, [], 0, () => { });
            Throws<ArgumentException>(() => notes.Stage("a", PersistenceNote, null, new DocumentChange(prepared, false), []), "a BigInteger");
            return Task.CompletedTask;
        })),
    ];

    /// <summary>Expects <paramref name="rejected"/> (a <see cref="Rejection"/>) to carry a message containing <paramref name="message"/>.</summary>
    private static void PersistenceRejects(object rejected, string message, string what)
    {
        var text = rejected is Exception error ? error.Message : (string?)((JsonNode)rejected)["message"] ?? "";
        Check(text.Contains(message, StringComparison.Ordinal), $"{what}: expected a message containing <{message}>, actual <{text}>.");
    }
}
