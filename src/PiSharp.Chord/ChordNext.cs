// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/index.ts, which only re-exports. This assembly is the
// @earendil-works/chord/next entry point: each layer's index.ts is the public surface of its namespace, under PiSharp.Chord (the contracts
// layer, src/next/contracts, is PiSharp.Chord.Contracts; data, documents, state, the store and registries follow as PiSharp.Chord.Data and
// so on, in folders named after src/next's). Each layer's internal.ts, which upstream's own layers import, is the internal static partial
// class <Layer>Internal of that namespace (ContractsInternal). The store's surface for adapters, which next/index.ts exports marked
// @internal (StoreOwnerTables, OwnerTable, the documents engine's reads and writes, the commit step, applyBatch), is for
// PiSharp.Durable.Store, the Durable adapter (decision 0013). Ported so far: the contracts, data, documents, state, store (with its SQLite
// backend, PiSharp.Chord.Sqlite) and registries layers, and the watch handle (Watch.cs). The host, jobs and link layers, which
// next/index.ts also re-exports, are phase 6.
namespace PiSharp.Chord;

/// <summary>The upstream source of the Chord v2 port (decision 0013, phase 4): Pi's <c>packages/chord/src/next</c>, the experimental
/// <c>@earendil-works/chord/next</c> entry point, at the pinned Pi v1.5.0 commit. Upstream builds Chord v2 slice by slice (v1.5.0 has slices 1
/// to 8, 7a to 7c and 10), so the API follows a moving target and every public type is experimental (<c>PISHARP_CHORD001</c>).</summary>
public static class ChordNext
{
    /// <summary>The pinned Pi commit.</summary>
    public const string Commit = "f9a26365568127461c47a07a37dcb42c140a3ac5";

    /// <summary>The upstream package and version.</summary>
    public const string Package = "@earendil-works/chord@1.5.0";

    /// <summary>The upstream module this assembly ports: the package's <c>./next</c> export.</summary>
    public const string Module = "@earendil-works/chord/next";
}
