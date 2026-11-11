// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/loaded.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;

namespace PiSharp.Chord.Surfaces;

public abstract record PreparedFacet;
public sealed record Prepared(Facet Facet, string Digest) : PreparedFacet;
public sealed record PreparationFailed(Exception Error) : PreparedFacet;
public sealed record SetEntry(string Name, string? Digest, Func<PreparedFacet> Prepare);
public sealed record LoadedFacetsResult(LoadedFacets Loaded, LoadReport Report);

/// <summary>Complete-set loading with stable facet identity, failure retention and serialized reloads.</summary>
public sealed class LoadedFacets
{
    private readonly IReadOnlyList<PluginFacet> core;
    private Dictionary<string, Prepared> loaded;
    private readonly SemaphoreSlim reloadGate = new(1, 1);

    private LoadedFacets(IReadOnlyList<PluginFacet> core, Dictionary<string, Prepared> loaded)
    { this.core = core.ToArray(); this.loaded = loaded; }

    public IReadOnlyDictionary<string, string> Digests { get { lock (this) return loaded.ToDictionary(e => e.Key, e => e.Value.Digest, StringComparer.Ordinal); } }

    public static async Task<LoadedFacetsResult> LoadAsync(ChordHost host, IReadOnlyList<PluginFacet> core,
        IReadOnlyList<SetEntry> entries, ChordContext context)
    {
        ValidateEntries(entries);
        var prepared = entries.Select(entry => (entry.Name, Value: Prepare(entry))).ToArray();
        var failures = prepared.Where(e => e.Value is PreparationFailed).Select(e => new PluginFailure(e.Name, ((PreparationFailed)e.Value).Error)).ToArray();
        var facets = core.Concat(prepared.Where(e => e.Value is Prepared).Select(e => new PluginFacet(e.Name, ((Prepared)e.Value).Facet))).ToArray();
        var report = await host.LoadAsync(facets, context).ConfigureAwait(false);
        var failed = report.Failed.Select(e => e.Plugin).ToHashSet(StringComparer.Ordinal);
        var kept = prepared.Where(e => e.Value is Prepared && !failed.Contains(e.Name)).ToDictionary(e => e.Name, e => (Prepared)e.Value, StringComparer.Ordinal);
        return new(new(core, kept), new(failures.Concat(report.Failed).ToArray()));
    }

    public async Task<ReloadReport> ReloadAsync(ChordHost host, IReadOnlyList<SetEntry> entries, ChordContext context)
    {
        ValidateEntries(entries);
        // Waiting is part of the full-set transaction. Even a cancelled turn cannot overtake its predecessor.
        await reloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var next = new Dictionary<string, Prepared>(StringComparer.Ordinal);
            var failures = new List<PluginFailure>();
            foreach (var entry in entries)
            {
                loaded.TryGetValue(entry.Name, out var old);
                if (entry.Digest is not null && old?.Digest == entry.Digest) { next.Add(entry.Name, old); continue; }
                var prepared = Prepare(entry);
                if (prepared is Prepared value) next.Add(entry.Name, value);
                else
                {
                    // Prepare only once: evaluation is observable user code.
                    var failure = ((PreparationFailed)prepared).Error;
                    failures.Add(new(entry.Name, failure));
                    if (old is not null) next.Add(entry.Name, old);
                }
            }
            var facets = core.Concat(next.Select(e => new PluginFacet(e.Key, e.Value.Facet))).ToArray();
            try
            {
                var report = await host.ReloadAsync(facets, context, new(failures)).ConfigureAwait(false);
                Adopt(next, report.Failed.Select(e => e.Plugin));
                return report;
            }
            catch (ChordError error) when (error.Code == "aborted" && error.Details?["committed"] is JsonObject committed)
            {
                Adopt(next, (committed["failed"] as JsonArray ?? []).Select(e => e!.GetValue<string>()));
                throw;
            }
        }
        finally { reloadGate.Release(); }
    }

    private static PreparedFacet Prepare(SetEntry entry)
    {
        PreparedFacet prepared;
        try { prepared = entry.Prepare() ?? new PreparationFailed(new InvalidOperationException("Preparation returned no result")); }
        catch (Exception error) { prepared = new PreparationFailed(error); }
        return prepared;
    }
    private void Adopt(Dictionary<string, Prepared> next, IEnumerable<string> failed)
    {
        foreach (var name in failed)
            if (loaded.TryGetValue(name, out var old)) next[name] = old;
            else next.Remove(name);
        lock (this) loaded = next;
    }
    private static void ValidateEntries(IReadOnlyList<SetEntry> entries)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            PiSharp.Chord.Contracts.ContractsInternal.CheckName(entry.Name, "A plugin name");
            if (!names.Add(entry.Name)) throw new ArgumentException($"Plugin {entry.Name} appears twice", nameof(entries));
        }
    }
}
