using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/documents/address-source.test.ts. Upstream's batch and
// delivered() are the state layer's hand-built host (State/StateHost.cs): its Batch and Delivered. A Prepared is built by hand as upstream's
// prepared() does (its constructor is internal to Delta). The unwatched case's WeakRef on the ops array is a WeakReference to its op (an
// ImmutableArray is a struct over the array the op is in), made and written in a frame of its own so no local keeps it.
internal static partial class Program
{
    /// <summary>An address whose value the test sets (upstream <c>Address</c>).</summary>
    private sealed class DocumentsAddress(Batch batch) : AddressSource(batch)
    {
        public JsonObject Current { get; set; } = DocumentsN(0);

        public override JsonObject Value => Current;
    }

    /// <summary>The document <c>{ n }</c>.</summary>
    private static JsonObject DocumentsN(int n) => new() { ["n"] = n };

    /// <summary>The op <c>["s", ["n"], n]</c>.</summary>
    private static SetOp DocumentsSetN(int n) => new(DeltaPath.Of("n"), n);

    /// <summary>A prepared write of <paramref name="value"/> over <paramref name="base"/> with <paramref name="ops"/> (upstream
    /// <c>prepared</c>).</summary>
    private static Prepared<JsonObject> DocumentsPrepared(JsonObject @base, JsonObject value, ImmutableArray<Op> ops) =>
        new(@base, value, ops, 0, () => { });

    /// <summary>A watched address on its own batch, and the changes its watch got (upstream <c>watched</c>).</summary>
    private static async Task<(StateHost Host, DocumentsAddress Address, List<WatchCall<JsonNode?>> Changes)> DocumentsWatched()
    {
        var host = new StateHost();
        var address = new DocumentsAddress(host.Batch);
        var changes = ChangesOf(await address.WatchAsync(ChordContext.Background));
        return (host, address, changes);
    }

    /// <summary>Commits a write of <c>{ n: 1 }</c> to the unwatched <paramref name="address"/> and returns a weak reference to its op.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DocumentsWriteUnwatched(StateHost host, DocumentsAddress address)
    {
        var op = DocumentsSetN(1);
        var @base = address.Current;
        host.Batch.Commit(() =>
        {
            address.Current = DocumentsN(1);
            address.Written(@base, DocumentsPrepared(@base, address.Current, [op]));
        }, ChordContext.Background);
        return new WeakReference(op);
    }

    private static IEnumerable<(string, Func<Task>)> DocumentsAddressSourceCases() =>
    [
        Case("documents.address-source.address-frames-a-write-over-the-read-value-carries-its-ops-over-another-base-a-root-replacement", async () =>
        {
            var (host, address, changes) = await DocumentsWatched();
            var first = address.Current;
            host.Batch.Commit(() =>
            {
                address.Current = DocumentsN(1);
                address.Written(first, DocumentsPrepared(first, address.Current, [DocumentsSetN(1)]));
            }, ChordContext.Background);
            host.Delivered();
            var second = address.Current;
            host.Batch.Commit(() =>
            {
                address.Current = DocumentsN(2);
                address.Written(second, DocumentsPrepared(DocumentsN(7), address.Current, [DocumentsSetN(2)]));
            }, ChordContext.Background);
            host.Delivered();
            Json("""
                [{"kind":"value","value":{"n":1},"ops":[["s",["n"],1]],"retired":false},
                 {"kind":"value","value":{"n":2},"ops":[["r",{"n":2}]],"retired":false}]
                """, StateCalls(changes), "the changes");
        }),
        Case("documents.address-source.address-frames-a-retirement-reads-initial-with-the-flag-a-recreation-in-the-same-batch-is-one-root-replacement-flagged", async () =>
        {
            var (host, address, changes) = await DocumentsWatched();
            host.Batch.Commit(() =>
            {
                address.Current = DocumentsN(0);
                address.Retired(address.Current);
            }, ChordContext.Background);
            host.Delivered();
            host.Batch.Commit(() =>
            {
                var initial = address.Current;
                address.Retired(initial);
                address.Current = DocumentsN(5);
                address.Written(initial, DocumentsPrepared(initial, address.Current, [DocumentsSetN(5)]));
            }, ChordContext.Background);
            host.Delivered();
            var recreated = address.Current;
            host.Batch.Commit(() =>
            {
                address.Current = DocumentsN(6);
                address.Written(recreated, DocumentsPrepared(recreated, address.Current, [DocumentsSetN(6)]));
            }, ChordContext.Background);
            host.Delivered();
            // The flag was reset by the frame that carried it.
            Json("""
                [{"kind":"value","value":{"n":0},"ops":[["r",{"n":0}]],"retired":true},
                 {"kind":"value","value":{"n":5},"ops":[["r",{"n":5}]],"retired":true},
                 {"kind":"value","value":{"n":6},"ops":[["s",["n"],6]],"retired":false}]
                """, StateCalls(changes), "the changes");
        }),
        Case("documents.address-source.address-frames-a-replacement-outside-a-commit-a-load-a-migration-is-a-root-replacement", async () =>
        {
            var (host, address, changes) = await DocumentsWatched();
            host.Batch.Commit(() =>
            {
                address.Current = DocumentsN(3);
                address.Replaced();
            }, ChordContext.Background);
            host.Delivered();
            Json("""[{"kind":"value","value":{"n":3},"ops":[["r",{"n":3}]],"retired":false}]""", StateCalls(changes), "the changes");
        }),
        Case("documents.address-source.unwatched-addresses-keep-no-ops-of-a-write-no-frame-takes-them", async () =>
        {
            var host = new StateHost();
            var address = new DocumentsAddress(host.Batch);
            var collected = DocumentsWriteUnwatched(host, address);
            await ExpectCollected(collected);
            // The address itself is still here.
            Json("""{"n":1}""", address.Value, "the address's value");
        }),
    ];
}
