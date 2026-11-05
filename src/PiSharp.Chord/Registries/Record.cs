// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/registries/record.ts
//
// A registry on a host (slice 8 §1): its provider (replaced or removed by a reload, slice 7b §3), its contributions in table order
// (contribution.ts), the table and its lazy composition (marked when the table changes, composed when read or followed, in the batch's
// deliveries), the reports of conflicts and failures (once per episode), and the values its readers follow by version. Mappings:
// - RegistryError is a record per kind, as BatchError is (the host's onError, HostError, is phase 6's); an error is an Exception, which is
//   all .NET throws.
// - The record is untyped, as upstream's is: the provider is an IRegistryProvider (RegistryProvider<C, V> without its types), the table an
//   object[] the provider made of its own entries, and a composition an object. Its values are typed by their readers: Value<V>(version)
//   makes the RegistryValueSource<V> of a version, and refuses to give it as another type (PiSharp-only: two tokens of one version whose
//   value types differ).
// - The values by version are a map and a list in insertion order, walked by index: a value made while the table is marked is walked too,
//   as a JavaScript Map's iteration visits it.
// - Table order compares plugin names and types as JavaScript's < does (UTF-16 code units, string.CompareOrdinal).
using PiSharp.Chord.Data;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Registries;

/// <summary>What a registry reports to its host's <c>onError</c> (upstream <c>RegistryError["kind"]</c>).</summary>
internal enum RegistryErrorKind
{
    /// <summary><c>registry_conflict</c>: <c>compose</c> reported a conflict it resolved.</summary>
    RegistryConflict,

    /// <summary><c>registry_failed</c>: <c>compose</c> (or an older version's value adapter) threw; the last good composition stays.</summary>
    RegistryFailed,

    /// <summary><c>contribution_failed</c>: a watchable contribution failed or ended, or its adapter threw; it is left out until it heals.</summary>
    ContributionFailed,

    /// <summary><c>contribution_refused</c>: a contribution of a version the provider does not accept is left out.</summary>
    ContributionRefused,
}

/// <summary>What a registry reports to its host's <c>onError</c>, by kind; each once per episode (upstream <c>RegistryError</c>).</summary>
/// <param name="Kind">Its kind.</param>
/// <param name="Registry">The registry's name.</param>
internal abstract record RegistryError(RegistryErrorKind Kind, string Registry);

/// <summary><c>compose</c> reported a conflict it resolved (<c>registry_conflict</c>).</summary>
internal sealed record RegistryConflictError(string Registry, string Message, IReadOnlyList<string> Plugins)
    : RegistryError(RegistryErrorKind.RegistryConflict, Registry);

/// <summary><c>compose</c> (or an older version's value adapter) threw: the last good composition stays (<c>registry_failed</c>).</summary>
internal sealed record RegistryFailedError(string Registry, Exception Error) : RegistryError(RegistryErrorKind.RegistryFailed, Registry);

/// <summary>A watchable contribution failed or ended, or its contribution adapter threw: it is left out until it heals
/// (<c>contribution_failed</c>).</summary>
internal sealed record ContributionFailedError(string Registry, string Plugin, string? Type, Exception Error)
    : RegistryError(RegistryErrorKind.ContributionFailed, Registry);

/// <summary>A contribution of a version the provider does not accept (newer, or older without an adapter) is left out
/// (<c>contribution_refused</c>).</summary>
internal sealed record ContributionRefusedError(string Registry, string Plugin, string? Type, long Version)
    : RegistryError(RegistryErrorKind.ContributionRefused, Registry);

internal static partial class RegistriesInternal
{
    /// <summary>
    /// Checks a registry provider as a setup served it (upstream <c>checkProvider</c>; declaration policy: plugin code is bundled without type
    /// checks): <c>compose</c> is a function.
    /// </summary>
    internal static void CheckProvider(string name, IRegistryProvider? provider)
    {
        if (provider?.HasCompose != true) throw new ArgumentException($"registry {name}: compose is a function");
    }
}

/// <summary>One registry of a host (upstream <c>RegistryRecord</c>; <c>ChordHost.registryRecord(name)</c>, made by its first use).</summary>
internal sealed class RegistryRecord
{
    private static readonly Composition Never = new(null, null);

    private readonly Batch batch;
    private readonly Action<RegistryError> report;
    // The provider as served: the defining plugin's version, compose and adapters; null while none serves it.
    private Provider? provider;
    // The contributions of live contributors, sorted by plugin, then type (a facet's first).
    private RegistryContribution[] contributions = [];
    // The last table composed, kept so an unchanged table composes nothing.
    private object[]? table;
    private bool dirty = true;
    private Composition composition = Never;
    // Set while its table is built and composed: a read then is a cycle of registries.
    private bool composing;
    // The conflicts the last good composition reported, by key: a conflict is reported once per episode.
    private HashSet<string> conflicts = new(StringComparer.Ordinal);
    // The values readers follow, by version; made by the first reader of a version.
    private readonly Dictionary<long, IRegistryValueSource> values = [];
    private readonly List<IRegistryValueSource> valuesInOrder = [];

    /// <param name="name">The registry's name.</param>
    /// <param name="batch">The host's batch.</param>
    /// <param name="report">The host's <c>onError</c>.</param>
    public RegistryRecord(string name, Batch batch, Action<RegistryError> report)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(report);
        Name = name;
        this.batch = batch;
        this.report = report;
    }

    public string Name { get; }

    /// <summary>The provider's version; null while none serves it.</summary>
    public long? ProvidedVersion => provider?.Version;

    /// <summary>The contributions the current good composition was built from; null while there is none.</summary>
    public RegistryContribution[]? ComposedFrom => composition.Good?.From;

    /// <summary>The value readers of <paramref name="version"/> follow, typed as their token's <typeparamref name="V"/>.</summary>
    public RegistryValueSource<V> Value<V>(long version)
    {
        if (values.TryGetValue(version, out var existing))
        {
            return existing as RegistryValueSource<V> ?? throw new ArgumentException(
                $"registry {Name}: version {version} is read as {existing.ValueType.Name} already, not as {typeof(V).Name}");
        }
        var value = new RegistryValueSource<V>(this, version, batch);
        values.Add(version, value);
        valuesInOrder.Add(value);
        return value;
    }

    /// <summary>
    /// Serves the registry with <paramref name="served"/> at <paramref name="version"/> (the defining plugin's facet, checked by
    /// <see cref="RegistriesInternal.CheckProvider"/> when its setup ran), replacing the provider a reload changed, inside a batch: the table
    /// composes again (lazily).
    /// </summary>
    public void Serve(long version, IRegistryProvider served)
    {
        ArgumentNullException.ThrowIfNull(served);
        provider = new Provider(version, served);
        table = null;
        Invalidate();
    }

    /// <summary>
    /// A reload removed the defining plugin, inside a batch: no provider serves it; its readers' values fail <c>unavailable/undeclared</c>
    /// (a reader parked on it waits <c>not_served</c>), its contributions stay in the table.
    /// </summary>
    public void Unserve()
    {
        provider = null;
        table = null;
        composition = Never;
        Invalidate();
    }

    /// <summary>The first composition of a served registry, at <c>load</c>'s publication or a reload's commit, inside its batch.</summary>
    public void ComposeServed()
    {
        if (provider is null) return;
        Start();
    }

    /// <summary>Adds a live contributor's contribution, inside a batch: the table changes.</summary>
    public void Add(RegistryContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        var at = Array.FindIndex(contributions, other => CompareContributors(contribution, other) < 0);
        // A new list per change (contributors come and go rarely): a composition keeps the one it was built from.
        var next = new List<RegistryContribution>(contributions);
        next.Insert(at < 0 ? contributions.Length : at, contribution);
        contributions = [.. next];
        contribution.Follow();
        Invalidate();
    }

    /// <summary>Removes an ending contributor's contribution, inside a batch: the table changes.</summary>
    public void Remove(RegistryContribution contribution)
    {
        var at = Array.IndexOf(contributions, contribution);
        if (at < 0) return;
        var next = new List<RegistryContribution>(contributions);
        next.RemoveAt(at);
        contributions = [.. next];
        contribution.Unfollow();
        Invalidate();
    }

    /// <summary>A followed contribution changed, inside a batch: the table is marked, composed when read or followed.</summary>
    public void ContributionChanged() => Invalidate();

    /// <summary>
    /// The current composition, composed now when the table changed: the value, or <c>compose</c>'s failure with the last good one;
    /// <c>unavailable/undeclared</c> while no provider serves it. A read while it composes is a cycle (<c>invalid/cycle</c>, thrown).
    /// </summary>
    public NativeStart<object?> Start()
    {
        if (composing) throw DataInternal.Invalid("cycle", $"registry {Name} is read while it composes");
        if (provider is not { } served)
        {
            var error = new ChordError("unavailable", $"registry {Name}: no plugin serves it here", "undeclared");
            return NativeStart<object?>.Failure(error, hasLast: false);
        }
        if (dirty) Recompose(served);
        var (good, failure) = composition;
        if (failure is not null) return NativeStart<object?>.Failure(failure, good is not null, good is null ? null : good.Value);
        // A composition without a failure has a value.
        return NativeStart<object?>.Of(good!.Value);
    }

    /// <summary>
    /// Whether every contribution of <paramref name="from"/> (what a composition, or a mapping of it, was built from) is still in the table
    /// (decision 5: one that left may have closed the native its entry held).
    /// </summary>
    public bool InTable(RegistryContribution[] from)
    {
        var current = contributions;
        if (ReferenceEquals(from, current)) return true;
        foreach (var each in from)
            if (Array.IndexOf(current, each) < 0) return false;
        return true;
    }

    /// <summary>The adapter mapping the composed value back for readers of <paramref name="version"/>; null when there is none.</summary>
    public Delegate? ValueAdapter(long version) => provider?.Served.ValueAdapter(version);

    /// <summary>Maps the composition <paramref name="current"/> with <paramref name="adapter"/>, one of the provider's value adapters.</summary>
    public object? MapValue(Delegate adapter, object? current) => provider!.Served.MapValue(adapter, current);

    /// <summary>Runs an adapter of the provider as a pure compute, counted (<c>chord.registry.adapts</c>).</summary>
    public T Adapt<T>(Func<T> adapter)
    {
        batch.Count("chord.registry.adapts");
        return batch.Compute(adapter);
    }

    /// <summary>Reports a contribution left out (<c>contribution_failed</c>, <c>contribution_refused</c>; the caller keeps the episode).</summary>
    public void ReportContribution(RegistryError error) => report(error);

    /// <summary>Reports a throw of <c>compose</c> or of a value adapter (<c>registry_failed</c>; the caller keeps the episode).</summary>
    public void Failed(Exception error) => report(new RegistryFailedError(Name, StateInternal.HandedOut(error)));

    /// <summary>
    /// The host closed: the contributions stop following their sources, the values end (a value kept by a reader still reads the last
    /// composition).
    /// </summary>
    public void Close()
    {
        foreach (var contribution in contributions) contribution.Unfollow();
        contributions = [];
        table = null;
        dirty = false;
        foreach (var value in valuesInOrder) value.Close();
        values.Clear();
        valuesInOrder.Clear();
    }

    /// <summary>
    /// The table changed: every version's value is marked and listed (each lists itself once per batch); while it is not composed since an
    /// earlier change, only listed (its dependents are marked already).
    /// </summary>
    private void Invalidate()
    {
        if (dirty)
        {
            for (var index = 0; index < valuesInOrder.Count; index++) valuesInOrder[index].Relisted();
            return;
        }
        dirty = true;
        for (var index = 0; index < valuesInOrder.Count; index++) valuesInOrder[index].Recomposed();
    }

    /// <summary>
    /// Builds the table and composes it, unless it is the last one composed, entry for entry (<c>compose</c> is pure). A throw is a failure
    /// episode: reported once, the last good composition kept while every contribution it was built from is still in the table (decision 5:
    /// one whose contributor ended may hold a closed native); conflicts are reported once per episode, as their message and plugins.
    /// </summary>
    private void Recompose(Provider served)
    {
        dirty = false;
        composing = true;
        try
        {
            var built = Build(served.Version, served.Served);
            if (ReferenceEquals(built, table) && !ReferenceEquals(composition, Never)) return;
            table = built;
            // A Map by key: a conflict reported again keeps its place and takes the last report.
            var found = new OrderedDictionary<string, RegistryConflict>(StringComparer.Ordinal);
            var context = new ComposeContext(conflict => found[$"{conflict.Message}\u0000{string.Join('\u0000', conflict.Plugins)}"] = conflict);
            object? value;
            try
            {
                batch.Count("chord.registry.composes");
                value = batch.Compute(() => served.Served.Compose(built, context));
            }
            catch (Exception error)
            {
                if (composition.Failure is null) Failed(error);
                var good = composition.Good;
                composition = new Composition(good is not null && InTable(good.From) ? good : null, error);
                return;
            }
            composition = new Composition(new Good(value, contributions), null);
            foreach (var (key, conflict) in found)
            {
                if (conflicts.Contains(key)) continue;
                report(new RegistryConflictError(Name, conflict.Message, conflict.Plugins));
            }
            conflicts = new HashSet<string>(found.Keys, StringComparer.Ordinal);
        }
        finally
        {
            composing = false;
        }
    }

    /// <summary>The table of the live contributions now; the last one itself when every entry is the same object.</summary>
    private object[] Build(long version, IRegistryProvider served)
    {
        var previous = table;
        var entries = new List<object>();
        var same = previous is not null;
        foreach (var contribution in contributions)
        {
            var entry = contribution.Entry(version, served);
            if (entry is null) continue;
            if (same && (entries.Count >= previous!.Length || !ReferenceEquals(previous[entries.Count], entry))) same = false;
            entries.Add(entry);
        }
        return same && previous!.Length == entries.Count ? previous : served.Table(entries);
    }

    /// <summary>Table order: by plugin name, then type, a facet (no type) first; code unit order, never load order.</summary>
    private static int CompareContributors(RegistryContribution a, RegistryContribution b)
    {
        var left = a.Contributor;
        var right = b.Contributor;
        if (left.Plugin != right.Plugin) return string.CompareOrdinal(left.Plugin, right.Plugin) < 0 ? -1 : 1;
        var leftType = left.Type ?? "";
        var rightType = right.Type ?? "";
        if (leftType == rightType) return 0;
        return string.CompareOrdinal(leftType, rightType) < 0 ? -1 : 1;
    }

    /// <summary>The provider as served: the defining plugin's version, and its compose and adapters.</summary>
    private sealed record Provider(long Version, IRegistryProvider Served);

    /// <summary>A last good composition: its value and the contributions it was built from.</summary>
    private sealed record Good(object? Value, RegistryContribution[] From);

    /// <summary>Where a composition stands: never composed, its last good value, and its failure while <c>compose</c> throws.</summary>
    private sealed record Composition(Good? Good, Exception? Failure);
}
