using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/support.ts, the test support every Chord v2 test imports.
// Ported here: the GC check (expectCollected) and the shared declarations of the slice tests (Server, SERVER_ADDRESS, Notes and its
// variants, Session, PlacedNotes, NoteStore, Restartable, StoreRestartable), and wireNames without its `named` (which reads link parts).
// Equal<A, B> is a type-level check with no C# counterpart. The rest needs layers later areas bring, and each comes with the first area
// whose tests use it: the counters (counted, metered; the data layer, cv2-data), watch frames and changes (delivered, framesOf,
// changesOf, valuesOf; the state layer, cv2-state), the store helpers (memoryStore, faultyDatabase, faultyStore, holdNextCommit,
// tableOwner, tableIn, ArchiveTables, archiveStore, exported, countInflated; the store areas), the remaining declarations (NotesDoc; the
// documents areas), rejection (ChordError JSON; cv2-data), and the hosts, links, relays and raw peers (failLive, restartable,
// storeRestartable, loaded, loadedPlugins, fakeTimers, flushed, failures, linked, RawPeer, partValue, CODECS, forEachCodec, rawLinked,
// transported, serveNotes, addNote, rawProvider, rawParent, statesOf, EDITOR, bindNext, bindHold, instancesOf, relayed, rawWorkers,
// incarnationOf, settledHeap), which build on createHost and the link layer (phase 6).
internal static class ChordSupport
{
    /// <summary>Expects every referent to be collected once nothing else holds it (upstream runs gc() between macrotasks).</summary>
    internal static async Task ExpectCollected(params WeakReference[] references)
    {
        for (var round = 0; round < 10 && references.Any(reference => reference.IsAlive); round++)
        {
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Program.Sequence(references.Select(_ => true), references.Select(reference => !reference.IsAlive), "every referent is collected");
    }

    /// <summary>WeakReferences to the values <paramref name="make"/> returns, made in a frame of its own so no local keeps them alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static WeakReference[] Weakly(Func<object[]> make) => [.. make().Select(value => new WeakReference(value))];

    /// <summary>The variants of the notes types: an editor variant for owners, a reader one for guests.</summary>
    private static OrderedDictionary<string, VariantDeclaration> NotesVariants() => new()
    {
        ["editor"] = new()
        {
            State = { ["notes"] = Arr(Obj(("text", Str()))), ["count"] = Int() },
            Methods =
            {
                ["add"] = Method(Obj(("text", Str())), Int()),
                ["fail"] = Method(Obj(("how", Str()))),
                ["wait"] = Method(),
                ["echo"] = Method(Obj(("data", Bytes())), Bytes()),
            },
        },
        ["reader"] = new() { State = { ["notes"] = Arr(Obj(("text", Str()))), ["count"] = Int() }, Methods = { ["wait"] = Method() } },
    };

    /// <summary>The host kind of the test servers, where every test type but <see cref="PlacedNotes"/> lives.</summary>
    internal static readonly HostKind Server = new("server");

    /// <summary><c>Server()</c> as a hold names it on the wire: the address of every test type served on the server.</summary>
    internal const string ServerAddress = """{"kind":"server","params":{}}""";

    /// <summary>The remote type of the link tests: notes per session, an editor variant for owners, a reader one for guests.</summary>
    internal static readonly Contract Notes = Contract.Remote("app.notes", new()
    {
        Version = 1,
        Params = new() { ["session"] = Str() },
        HostAddress = _ => Server.Address(),
        Variants = NotesVariants(),
    });

    /// <summary>The kind of the relay tests' workers: one per session.</summary>
    internal static readonly HostKind Session = new("session", new() { ["id"] = Str() });

    /// <summary><see cref="Notes"/> placed on the session's worker: the relay tests' type.</summary>
    internal static readonly Contract PlacedNotes = Contract.Remote("app.placed-notes", new()
    {
        Version = 1,
        Params = new() { ["session"] = Str() },
        HostAddress = values => Session.Address(("id", values["session"])),
        Variants = NotesVariants(),
    });

    /// <summary>The shared store behind <see cref="Notes"/> (§6): the notes of a session, once, whoever views them.</summary>
    internal static readonly Contract NoteStore = Contract.Local("app.note-store", new()
    {
        Version = 1,
        Params = new() { ["session"] = Str() },
        Members = new() { ["list"] = Arr(Obj(("text", Str()))), ["add"] = Method(Obj(("text", Str())), Int()) },
    });

    /// <summary>A local type whose objects' attempts a test ends by hand (upstream's <c>restartable</c>, phase 6); it ends with its last
    /// reference.</summary>
    internal static readonly Contract Restartable = Contract.Local("app.restartable", new()
    {
        Version = 1,
        Params = new() { ["id"] = Int() },
        Members = new() { ["touch"] = Method() },
    });

    /// <summary>A local type with empty params whose attempts a test ends by hand (<c>storeRestartable</c>, phase 6): it may persist (P3).</summary>
    internal static readonly Contract StoreRestartable = Contract.Local("app.store", new()
    {
        Version = 1,
        Params = new(),
        Members = new() { ["touch"] = Method() },
    });

    /// <summary>The wire's indexes of a remote contract's variant (2b): the variant's, and its state members' and methods' by name.</summary>
    internal static (int Variant, IReadOnlyDictionary<string, int> State, IReadOnlyDictionary<string, int> Methods) WireNames(Contract type, string variant)
    {
        var names = ContractsInternal.VariantNamesOf(ContractsInternal.CurrentVariants(type)[variant]);
        static IReadOnlyDictionary<string, int> Indexes(IReadOnlyList<string> list) => list.Select((name, index) => (name, index)).ToDictionary(entry => entry.name, entry => entry.index);
        return (ContractsInternal.VariantOrder(type).ToList().IndexOf(variant), Indexes(names.State), Indexes(names.Methods));
    }

    /// <summary>A JSON node from its text (the expected values of a case).</summary>
    internal static JsonNode Parse(string json) => JsonNode.Parse(json)!;
}
