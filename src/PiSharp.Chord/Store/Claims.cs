// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/claims.ts
//
// Claims (slice 6b "owner names and claims", "claims per table and per document namespace"): what can collide in a store is claimed once,
// in memory: a table by its SQL name, documents by (owner, namespace). A claim is held by its claimant (an object, a StoreStorage) until
// its end releases every claim it holds. While a batch applies, nothing is claimed: batch apply and owners exclude each other both ways.
// Claimants are told apart by identity, as upstream's Maps key them.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>Who holds claims: named in the refusal of a second claim (upstream <c>Claimant</c>, which <c>next/index.ts</c> exports for
/// adapters). Claimants are compared by reference.</summary>
public interface IClaimant
{
    /// <summary>The claimant's name in refusals.</summary>
    string Label { get; }
}

/// <summary>A store's claims, by key; one claimant per key (upstream <c>Claims</c>). Public as far as upstream's <c>ChordStore.claims</c>
/// serves adapters (Durable's StoreStorage claims its document namespaces and releases them at close): <see cref="Documents"/> and
/// <see cref="Release"/>; tables are claimed through <see cref="StoreOwnerTables.Declare"/>.</summary>
public sealed class Claims
{
    private readonly Dictionary<string, IClaimant> holders = new(StringComparer.Ordinal);
    private readonly Dictionary<IClaimant, List<string>> keys = new(ReferenceEqualityComparer.Instance);
    /// <summary>Whether a batch applies now: no claim is taken meanwhile.</summary>
    private bool locked;

    /// <summary>Whether any claimant holds a claim (batch apply is refused then: its memory would go stale).</summary>
    internal bool Held => keys.Count > 0;

    /// <summary>Claims table <paramref name="table"/> (its SQL name) for <paramref name="claimant"/>; held by another:
    /// <c>invalid/args</c>.</summary>
    internal void Table(string table, IClaimant claimant) => Claim(TableKey(table), claimant);

    /// <summary>Claims the documents of <paramref name="owner"/> in <paramref name="namespace"/> for <paramref name="claimant"/>; held by
    /// another: <c>invalid/args</c>.</summary>
    public void Documents(string owner, string @namespace, IClaimant claimant) => Claim(DocumentsKey(owner, @namespace), claimant);

    /// <summary>Throws as <see cref="Table"/> would for <paramref name="claimant"/>, claiming nothing (a reload's staging checks its
    /// declarations).</summary>
    internal void AssertTable(string table, IClaimant claimant) => Assert(TableKey(table), claimant);

    /// <summary>Throws as <see cref="Documents"/> would for <paramref name="claimant"/>, claiming nothing.</summary>
    internal void AssertDocuments(string owner, string @namespace, IClaimant claimant) => Assert(DocumentsKey(owner, @namespace), claimant);

    /// <summary>Refuses every claim (<c>invalid/args</c>) until the returned unlock: a batch applies meanwhile.</summary>
    internal Action Lock()
    {
        locked = true;
        return () => locked = false;
    }

    /// <summary>Releases every claim <paramref name="claimant"/> holds.</summary>
    public void Release(IClaimant claimant)
    {
        ArgumentNullException.ThrowIfNull(claimant);
        if (keys.Remove(claimant, out var held))
        {
            foreach (var key in held) holders.Remove(key);
        }
    }

    /// <summary><c>table "&lt;name&gt;"</c>, the name as JSON.stringify writes it.</summary>
    private static string TableKey(string table) => $"table {JsonUtf16.Quote(table)}";

    /// <summary><c>documents ["&lt;owner&gt;","&lt;namespace&gt;"]</c>, the pair as JSON.stringify writes it.</summary>
    private static string DocumentsKey(string owner, string @namespace) => $"documents {ChordJson.Stringify(new JsonArray(owner, @namespace))}";

    private void Claim(string key, IClaimant claimant)
    {
        Assert(key, claimant);
        if (holders.TryGetValue(key, out var holder) && ReferenceEquals(holder, claimant)) return;
        holders[key] = claimant;
        if (keys.TryGetValue(claimant, out var held)) held.Add(key);
        else keys[claimant] = [key];
    }

    /// <summary>Refuses <paramref name="key"/> to <paramref name="claimant"/> while a batch applies or another claimant holds it.</summary>
    private void Assert(string key, IClaimant claimant)
    {
        if (locked) throw DataInternal.Invalid("args", $"store: {key} cannot be claimed while a batch applies");
        if (holders.TryGetValue(key, out var holder) && !ReferenceEquals(holder, claimant))
            throw DataInternal.Invalid("args", $"store: {key} is claimed by {holder.Label}");
    }
}
