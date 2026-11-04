using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using PiSharp.Telemetry;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/support.ts (restartable, storeRestartable, failLive,
// instancesOf, loaded) and packages/chord/src/next/host/attempt.ts, what this file stands in for until the host is ported.
//
// The documents' hand-built host (area cv2-documents-slots), on the state layer's (State/StateHost.cs). Upstream's documents tests build
// their objects through createHost (packages/chord/src/next/host, phase 6): restartable and storeRestartable (test/next/support.ts) serve
// app.restartable or app.store with a factory per attempt and get object 1 live; fail() fails the live attempt as a runtime failure of its
// owner does (failLive), retry() asks for the next attempt, restart() does both, host.close ends every object. Until the host lands, the
// cases run against the documents layer with the host's wiring done by hand, as upstream's attempt.ts does it:
// - An attempt declares in its factory only (self.state, self.table, self.derive, handle.at(key), "valid only in the factory" outside it):
//   state takes the declaration through Documents.Declare under the attempt's seal token, the attempt being the handles' IKeyDeclarer;
//   at(key) pins the key (KeyedSlot.Pin) and returns its source. "Declared twice" is the attempt's check.
// - Its activation: the factory, then Documents.Hydrate with what it declared and its pins (the object holds them from then on), the load
//   (Documents.Load), the tables' preparation, then the derives attached (a handle resolves to its slot, as a member or a derive source
//   resolves it). A throw anywhere fails the activation: activation_failed with the error as a ChordError of the object's label
//   (DataInternal.AsChordError), the attempt's token sealed and its own pins released.
// - The object is the documents' IDocumentsOwner (its Fail fails the live attempt broken: object_failed, the attempt's token fenced) and
//   its tables' ITableOwner (scope: its documents). Its end (host.close, reference.release) seals its documents, waits for the line
//   (SettledAsync) and ends them.
// What only the host has (the P3 checks of persisted declarations, self.on, members as references, a handler's drain and closeTimeout,
// retries' backoff, the object's state machine beyond live and failed) waits for phase 6; each case file says which of its cases do.
internal sealed class DocumentsHost
{
    private readonly List<DocumentsObject> objects = [];

    public DocumentsHost(ChordStore? store = null, ITelemetryMetrics? metrics = null, TimeProvider? time = null)
    {
        State = new StateHost(metrics);
        Store = store;
        Time = time;
    }

    public StateHost State { get; }

    public Batch Batch => State.Batch;

    /// <summary>The host's store (upstream <c>createHost({ store })</c>); null without one.</summary>
    public ChordStore? Store { get; }

    /// <summary>The residency's clock and timers (upstream's fake timers); the system's when null.</summary>
    public TimeProvider? Time { get; }

    /// <summary>What the host reported (its <c>onError</c>): <c>object_failed</c> and <c>activation_failed</c>, with their errors.</summary>
    public List<(string Kind, ChordError Error)> Errors { get; } = [];

    /// <summary>The kinds of <see cref="Errors"/>, in order.</summary>
    public List<string> ErrorKinds
    {
        get
        {
            lock (Errors) return [.. Errors.Select(error => error.Kind)];
        }
    }

    /// <summary>The message of the last activation failure's error (upstream's <c>failure(errors)</c>).</summary>
    public string? Failure
    {
        get
        {
            lock (Errors) return Errors.LastOrDefault(error => error.Kind == "activation_failed").Error?.Message;
        }
    }

    /// <summary>Upstream's <c>delivered()</c>: runs every queued watch delivery.</summary>
    public void Delivered() => State.Delivered();

    /// <summary>An object of type <paramref name="type"/> labelled <paramref name="label"/>, its attempts made by <paramref name="factory"/>
    /// (their numbers from 1), not activated yet.</summary>
    public DocumentsObject Serve(string type, string label, Action<int, DocumentsAttempt> factory)
    {
        var made = new DocumentsObject(this, type, label, factory);
        objects.Add(made);
        return made;
    }

    /// <summary>Serves <paramref name="type"/> and gets the object live (upstream <c>host.get</c>); its activation's failure rethrown.</summary>
    public async Task<DocumentsObject> GetAsync(string type, string label, Action<int, DocumentsAttempt> factory)
    {
        var made = Serve(type, label, factory);
        await made.ActivateAsync();
        return made;
    }

    /// <summary>Ends every object (upstream <c>host.close</c>).</summary>
    public async Task CloseAsync()
    {
        foreach (var each in objects) await each.EndAsync();
    }

    /// <summary>
    /// Upstream's <c>restartable</c>: <c>app.restartable(1)</c> live, on a host of its own (with <paramref name="store"/>, whose objects
    /// may persist), its attempts made by <paramref name="factory"/>.
    /// </summary>
    public static Task<DocumentsObject> Restartable(Action<int, DocumentsAttempt> factory, ChordStore? store = null, ITelemetryMetrics? metrics = null, TimeProvider? time = null) =>
        new DocumentsHost(store, metrics, time).GetAsync("app.restartable", "app.restartable(1)", factory);

    /// <summary>Upstream's <c>storeRestartable</c>: as <see cref="Restartable"/>, for <c>app.store</c> (empty params: it may persist).</summary>
    public static Task<DocumentsObject> StoreRestartable(Action<int, DocumentsAttempt> factory, ChordStore? store = null, ITelemetryMetrics? metrics = null, TimeProvider? time = null) =>
        new DocumentsHost(store, metrics, time).GetAsync("app.store", "app.store", factory);
}

/// <summary>An object of the hand-built host: its documents, attempts, live attempt and state.</summary>
internal sealed class DocumentsObject : IDocumentsOwner, ITableOwner
{
    private readonly Action<int, DocumentsAttempt> factory;
    private readonly Lock gate = new();
    private DocumentsAttempt? live;
    private string? failed;
    private bool ended;
    private int started;

    public DocumentsObject(DocumentsHost host, string type, string label, Action<int, DocumentsAttempt> factory)
    {
        Host = host;
        Type = type;
        Label = label;
        this.factory = factory;
        Documents = new Documents(this, host.Time);
    }

    public DocumentsHost Host { get; }

    /// <summary>Its type: its documents' owner in the store and its tables' prefix.</summary>
    public string Type { get; }

    public string Label { get; }

    public Batch Batch => Host.Batch;

    public ChordContext Context => ChordContext.Background;

    public DocumentsStore? Store => Host.Store is { } store ? new DocumentsStore(store, Type) : null;

    public Documents Documents { get; }

    object ITableOwner.Scope => Documents;

    /// <summary>Its attempts whose factory returned, in order (upstream's <c>attempts</c>).</summary>
    public List<DocumentsAttempt> Attempts { get; } = [];

    /// <summary>Its state as upstream's <c>reference.state.value</c> reads: <c>{"kind":"live"}</c>, or failed with its cause's kind.</summary>
    public JsonObject State
    {
        get
        {
            lock (gate)
            {
                if (live is not null) return new JsonObject { ["kind"] = "live" };
                if (failed is not null) return new JsonObject { ["kind"] = "failed", ["cause"] = new JsonObject { ["kind"] = failed } };
                return new JsonObject { ["kind"] = ended ? "ended" : "activating" };
            }
        }
    }

    void ITableOwner.AssertLive() => Documents.AssertLive();

    /// <summary>A document the code cannot read fails the live attempt broken; ignored when none is live.</summary>
    public void Fail(ChordError error)
    {
        DocumentsAttempt? attempt;
        lock (gate) attempt = live;
        if (attempt is not null) FailLive(attempt, error);
    }

    /// <summary>Fails <paramref name="attempt"/> broken when it is the live one (upstream <c>failLive</c>): <c>object_failed</c>, its token
    /// fenced (the transaction holding the line under it cut).</summary>
    public void FailLive(DocumentsAttempt attempt, ChordError error)
    {
        lock (gate)
        {
            if (!ReferenceEquals(live, attempt)) return;
            live = null;
            failed = "broken";
        }
        lock (Host.Errors) Host.Errors.Add(("object_failed", error));
        Documents.Fence(attempt.Token);
    }

    /// <summary>Upstream's <c>fail()</c>: the last attempt fails as a runtime failure of its owner does.</summary>
    public void FailAttempt() => FailLive(Attempts[^1], new ChordError("unavailable", "boom"));

    /// <summary>Upstream's <c>retry()</c>: the next attempt, live once it activated.</summary>
    public Task RetryAsync() => ActivateAsync();

    /// <summary>Upstream's <c>restart()</c>: <see cref="FailAttempt"/>, then <see cref="RetryAsync"/>.</summary>
    public Task RestartAsync()
    {
        FailAttempt();
        return RetryAsync();
    }

    /// <summary>One attempt's activation: the factory, hydration, load, tables and derives; its failure reported and rethrown.</summary>
    public async Task ActivateAsync()
    {
        var attempt = new DocumentsAttempt(this, Interlocked.Increment(ref started));
        var hydrated = false;
        try
        {
            attempt.Declaring = true;
            try
            {
                factory(attempt.Number, attempt);
            }
            finally
            {
                attempt.Declaring = false;
            }
            Attempts.Add(attempt);
            // The object holds the pins from now on (upstream splices them out of the attempt).
            Documents.Hydrate(attempt.Declared, [.. attempt.Pins], Context);
            attempt.Pins.Clear();
            hydrated = true;
            if (Documents.Load(Context) is { } loading) await loading;
            await attempt.PrepareTablesAsync();
            attempt.AttachDerives();
            lock (gate)
            {
                live = attempt;
                failed = null;
            }
        }
        catch (Exception error)
        {
            var failure = DataInternal.AsChordError(error, Label);
            lock (Host.Errors) Host.Errors.Add(("activation_failed", failure));
            Documents.Fence(attempt.Token);
            if (!hydrated) foreach (var release in attempt.Pins) release();
            lock (gate) failed = "broken";
            throw failure;
        }
    }

    /// <summary>The object's end (host.close, reference.release): its documents sealed, the line waited for, its documents ended.</summary>
    public async Task EndAsync()
    {
        lock (gate)
        {
            if (ended) return;
            ended = true;
            live = null;
        }
        Documents.Seal();
        await Documents.SettledAsync();
        Documents.End();
    }
}

/// <summary>One attempt of a hand-built object: what its factory declares, under its seal token (upstream's <c>Attempt</c> as
/// <c>self</c>).</summary>
internal sealed class DocumentsAttempt : IKeyDeclarer
{
    private readonly List<(TableDefinition Definition, string SqlName)> tables = [];
    private readonly List<Derive> derives = [];

    public DocumentsAttempt(DocumentsObject owner, int number)
    {
        Object = owner;
        Number = number;
    }

    public DocumentsObject Object { get; }

    public int Number { get; }

    public SealToken Token { get; } = new();

    /// <summary>Whether its factory runs.</summary>
    public bool Declaring { get; set; }

    /// <summary>Its documents, by name, in declaration order.</summary>
    public OrderedDictionary<string, StateDefinition> Declared { get; } = new(StringComparer.Ordinal);

    /// <summary>Its factory's at(key) pins.</summary>
    public List<Action> Pins { get; } = [];

    /// <summary><c>self.state(definition)</c> of a singleton definition.</summary>
    public ISingletonHandle State(StateDefinition definition) => (ISingletonHandle)Declare(definition);

    /// <summary><c>self.state(definition)</c> of a keyed definition.</summary>
    public IKeyedHandle KeyedState(StateDefinition definition) => (IKeyedHandle)Declare(definition);

    public KeySource At(KeyedSlot slot, string key)
    {
        AssertDeclaring("state(...).at");
        Pins.Add(slot.Pin(key, Object.Context));
        return slot.Source(slot.Key(key))!;
    }

    /// <summary><c>self.table(definition)</c>: a handle to <c>&lt;type&gt;:&lt;name&gt;</c> in the host's store, prepared at activation.</summary>
    public TableHandle Table(TableDefinition definition)
    {
        AssertDeclaring("table");
        var store = Object.Host.Store ?? throw new InvalidOperationException($"{Object.Label}: table \"{definition.Name}\" needs a store");
        var sqlName = $"{Object.Type}:{definition.Name}";
        tables.Add((definition, sqlName));
        return new TableHandle(definition, sqlName, store, Object);
    }

    /// <summary><c>self.derive(sources, compute)</c>, attached at activation (a handle resolves to its slot).</summary>
    public Derive Derive(object[] sources, Func<IReadOnlyList<object?>, JsonNode?> compute)
    {
        AssertDeclaring("derive");
        var derive = new Derive(sources, compute, Object.Batch);
        derives.Add(derive);
        return derive;
    }

    /// <summary><c>self.tx(fn, context)</c>: one transaction under this attempt's token.</summary>
    public Task<TResult> TxAsync<TResult>(Func<Transaction, Task<TResult>> fn, ChordContext context) => Object.Documents.TxAsync(fn, Token, context);

    /// <inheritdoc cref="TxAsync{TResult}(Func{Transaction, Task{TResult}}, ChordContext)"/>
    public Task TxAsync(Func<Transaction, Task> fn, ChordContext context) => Object.Documents.TxAsync(async tx =>
    {
        await fn(tx);
        return true;
    }, Token, context);

    internal async Task PrepareTablesAsync()
    {
        if (tables.Count == 0) return;
        var declared = tables.Select(table => new DeclaredTable(table.Definition, table.SqlName)).ToList();
        await StoreInternal.PrepareTablesAsync(declared, Object.Host.Store!, CommitFence.Open, Object.Type, Object.Context);
    }

    internal void AttachDerives()
    {
        foreach (var derive in derives) derive.Attach((source, _) => source is Singleton singleton ? singleton.Slot : source, _ => { });
    }

    /// <summary><c>self.state(definition)</c>: the handle its definition's Keyed picks.</summary>
    public object Declare(StateDefinition definition)
    {
        AssertDeclaring("state");
        if (Declared.ContainsKey(definition.Name)) throw new InvalidOperationException($"{Object.Label}: state \"{definition.Name}\" is declared twice");
        Declared[definition.Name] = definition;
        return Object.Documents.Declare(definition, Token, this);
    }

    private void AssertDeclaring(string declaration)
    {
        if (!Declaring) throw new InvalidOperationException($"{Object.Label}: self.{declaration} is valid only in the factory");
    }
}

// The documents cases' shared helpers (SlotCases.cs, KeyedSlotCases.cs, TransactionCases.cs, HandlesCases.cs, DocumentsCases.cs).
internal static partial class Program
{
    private static ChordContext DocumentsBg => ChordContext.Background;

    /// <summary>A definition (upstream <c>defineState</c>); a persisted one never checkpoints (<c>checkpointWhen: () =&gt; false</c>).</summary>
    private static StateDefinition DocumentsDefine(string name, JsonNode schema, long version, Func<JsonObject> initial, bool keyed = false, bool persist = false,
        Func<JsonObject, long, JsonObject>? migrate = null) =>
        StateDefinition.Define(name, new()
        {
            Schema = schema,
            Version = version,
            Initial = initial,
            Migrate = migrate,
            Keyed = keyed,
            Persist = persist,
            CheckpointWhen = persist ? (_, _, _) => false : null,
        });

    /// <summary>Expects <paramref name="calls"/> (a <see cref="ChangesOf"/>) to be <paramref name="expected"/>, a JSON array of
    /// <see cref="CallJson"/>'s shape.</summary>
    private static void DocumentsCalls(string expected, IEnumerable<WatchCall<JsonNode?>> calls, string what) =>
        Json(expected, new JsonArray([.. calls.ToList().Select(call => JsonNode.Parse(CallJson(call)))]), what);

    /// <summary>Expects <paramref name="frames"/> (a <see cref="FramesOf"/>) to be <paramref name="expected"/>, a JSON array of
    /// <c>[value, ops]</c>.</summary>
    private static void DocumentsFrames(string expected, IEnumerable<(JsonNode? Value, System.Collections.Immutable.ImmutableArray<PiSharp.Chord.Delta.Op> Ops)> frames, string what) =>
        Json(expected, new JsonArray([.. frames.ToList().Select(frame => JsonNode.Parse(FrameJson(frame)))]), what);

    /// <summary>Expects <paramref name="rejected"/> (a <see cref="Rejection"/>) to be a ChordError matching <paramref name="expected"/>'s
    /// fields (upstream's toMatchObject); a message field matches by containment when <paramref name="containing"/>.</summary>
    private static void DocumentsMatches(string expected, object rejected, string what, bool containing = false)
    {
        if (rejected is Exception error) throw new InvalidOperationException($"{what}: expected a ChordError, actual {error.GetType().Name}: {error.Message}");
        var actual = (JsonObject)rejected;
        foreach (var (key, value) in JsonNode.Parse(expected)!.AsObject())
        {
            var field = actual[key];
            if (containing && key == "message")
            {
                var text = (string?)field ?? "";
                Check(text.Contains((string)value!, StringComparison.Ordinal), $"{what}: expected a message containing <{value}>, actual <{text}>.");
            }
            else Json(value!.ToJsonString(), field, $"{what} ({key})");
        }
    }

    /// <summary>Expects <paramref name="rejected"/> (a <see cref="Rejection"/>) to carry a message containing <paramref name="message"/>,
    /// a ChordError's or an exception's.</summary>
    private static void DocumentsRejectsWith(object rejected, string message, string what)
    {
        var text = rejected is Exception error ? error.Message : (string?)((JsonNode)rejected)["message"] ?? "";
        Check(text.Contains(message, StringComparison.Ordinal), $"{what}: expected a message containing <{message}>, actual <{text}>.");
    }

    /// <summary>What <paramref name="read"/> throws, as ChordError JSON when it is one (upstream's <c>rejectionOf</c>); null when nothing.</summary>
    private static object? DocumentsThrown(Action read)
    {
        try
        {
            read();
        }
        catch (Exception error)
        {
            return error is ChordError chord ? chord.ToJson() : error;
        }
        return null;
    }

    /// <summary>Every key a keyed handle lists, in order (upstream <c>keysOf</c>).</summary>
    private static async Task<List<string>> DocumentsKeysOf(IKeyedHandle handle)
    {
        var keys = new List<string>();
        await foreach (var key in handle.Keys(DocumentsBg)) keys.Add(key);
        return keys;
    }

    /// <summary>A draft's array <paramref name="name"/>.</summary>
    private static JsonArray DocumentsArray(JsonObject draft, string name) => (JsonArray)draft[name]!;

    /// <summary>A draft's number <paramref name="name"/>.</summary>
    private static double DocumentsNumber(JsonNode? value, string name) => ChordJson.AsNumber(value![name]) ?? throw new InvalidOperationException($"{name} is not a number");

    /// <summary>Counts <paramref name="store"/>'s reads from now on (upstream's <c>vi.spyOn(store, "read")</c>).</summary>
    private static Func<int> DocumentsCountReads(ChordStore store)
    {
        var reads = 0;
        store.Reads = read =>
        {
            Interlocked.Increment(ref reads);
            return read;
        };
        return () => Volatile.Read(ref reads);
    }

    /// <summary>Holds <paramref name="store"/>'s next read after the backend returned its result, until <c>Open</c> (keyed-slot.test.ts's
    /// <c>holdNextRead</c>); later reads run as usual.</summary>
    private static (Task Reached, Action Open) DocumentsHoldNextRead(ChordStore store)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taken = 0;
        store.Reads = async read =>
        {
            if (Interlocked.Exchange(ref taken, 1) != 0) return;
            store.Reads = null;
            await read;
            reached.TrySetResult();
            await gate.Task;
        };
        return (reached.Task, () => gate.TrySetResult());
    }
}
