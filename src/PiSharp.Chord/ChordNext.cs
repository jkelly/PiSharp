// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/index.ts, which only re-exports. This assembly is the
// @earendil-works/chord metadata: each layer's index.ts is the public surface of its namespace, under PiSharp.Chord (the contracts
// layer, src/contracts, is PiSharp.Chord.Contracts; data, documents, state, the store and registries follow as PiSharp.Chord.Data and
// so on, in folders named after src). Each layer's internal.ts, which upstream's own layers import, is the internal static partial
// class <Layer>Internal of that namespace (ContractsInternal). The store's surface for adapters, which next/index.ts exports marked
// @internal (StoreOwnerTables, OwnerTable, the documents engine's reads and writes, the commit step, applyBatch), is for
// PiSharp.Durable.Store, the Durable adapter. Current source additionally includes host, jobs and links. The assembly also contains
// the distinct legacy protocol-4 runtime; these constants identify only the current source target and never relabel legacy frames.
// Local package admission is prepared; final runtime/package validation and external publication remain separate gates.
namespace PiSharp.Chord;

/// <summary>The upstream source target of the Chord v2 port: Pi v1.5.2's <c>packages/chord/src</c>, now exported from the package root.
/// The CLR name is retained for compatibility; separate Legacy APIs retain their original source identity.
/// Every public type remains experimental (<c>PISHARP_CHORD001</c>). See the v1.5.2 applicability and validation records.</summary>
public static class ChordNext
{
    /// <summary>The pinned Pi commit.</summary>
    public const string Commit = "b4afdddf44e059c67ccf5097e5317d913521b470";

    /// <summary>The upstream package and version.</summary>
    public const string Package = "@earendil-works/chord@1.5.2";

    /// <summary>The upstream module this assembly targets: the package root.</summary>
    public const string Module = "@earendil-works/chord";
}
