// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/documents/documents.ts, packages/chord/src/documents/handles.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Store;
using static ChordSupport;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> DocumentsHostKeyedCases() =>
    [
        Case("documents.host-keyed.definitions-fence-key-kind-without-changing-schema-fingerprint", () =>
        {
            StateDefinition Define(HostKind? kind) => StateDefinition.Define("values", new()
            {
                Version = 1, Keyed = kind is null, KeyHost = kind,
                Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 },
            });
            var host = Define(new HostKind("tenant"));
            var strings = Define(null);
            Check(host.Keyed, "host definition implies keyed");
            Equal(strings.Fingerprint, host.Fingerprint, "document schema fingerprint unchanged");
            var slot = new SlotDefinition(host);
            Throws<InvalidOperationException>(() => slot.Check(strings), "host to string cannot silently convert");
            Throws<InvalidOperationException>(() => slot.Check(Define(new HostKind("other"))), "host kind cannot silently convert");
            slot.Check(Define(new HostKind("tenant")));
        }),
        Case("documents.host-keyed.persisted-ownership-mismatch-refuses-reading", async () =>
        {
            await using var store = await MemoryStore();
            var tenant = new HostKind("tenant", new() { ["id"] = Str() });
            var address = tenant.Address(("id", "a"));
            var definition = StateDefinition.Define("values", new()
            {
                Version = 1, KeyHost = tenant, Persist = true, CheckpointWhen = (_, _, _) => false,
                Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 },
            });
            var value = new JsonObject { ["n"] = 1 };
            await store.CommitAsync([StoreInternal.CreateDocument(new("owner", "chord", "values", address.Key),
                new(1, definition.Fingerprint, value), ChordJson.Stringify(value))], CommitFence.Open, ChordContext.Background);
            var persistence = new Persistence(store, "owner", "chord", "values", tenant);
            var stored = await persistence.ReadAsync(address.Key, ChordContext.Background);
            var error = Throws<InvalidOperationException>(() => persistence.Decode(stored!, definition, ChordContext.Background), "old string document cannot become host-owned");
            Check(error.Message.Contains("convert it explicitly", StringComparison.Ordinal), "explicit conversion required");
            var found = await persistence.FindAsync(address.Key, ChordContext.Background);
            Throws<InvalidOperationException>(() => persistence.CheckHost(found!.Host, definition), "retirement also rejects mismatched ownership");
        }),
    ];
}
