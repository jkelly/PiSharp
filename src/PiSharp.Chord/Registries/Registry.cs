// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/registries/registry.ts
//
// Registry tokens (design §17, slice 8 D1): defineRegistry, shared by every plugin as a contract is (identity and types only), and what the
// defining plugin's facet serves it with (compose, accepts); the table compose gets and the composed value readers follow. Mappings:
// - Registry<C, V> is a class whose types are its type parameters (upstream's "~types" field is type-only); defineRegistry is
//   Registry.Define, which takes its options as RegistryOptions.
// - RegistryEntry<C> is a class, not a record: the table is compared entry by entry by identity (an entry is the same object while its
//   value is), never by value. RegistryTable<C> is IReadOnlyList<RegistryEntry<C>>; its helpers are RegistryTable.KeyedEntries and
//   RegistryTable.OrderedEntries (entries.ts).
// - RegistryAccepts<C, V> is a dictionary by version of RegistryAdapters<C, V>. An older contribution's type is the adapter's to know
//   (upstream's plugin annotates it): Contribution takes an object. Value maps the composed V back for an older reader, whose type the
//   reader's token says: it returns an object, which the reader's value casts.
// - Contribution<C> (C | Watchable<C> | NativeWatchable<C>) has no C# type: the host's contribute takes the value as an object and follows
//   it when it is a Chord watchable (host/members.ts, phase 6; ContributionContent, contribution.ts).
// - RegistryValue<V> is the public interface IRegistryValue<V>; Chord's own is RegistryValueSource<V> (value.ts).
// - The records the registries layer keeps are untyped, as upstream's RegistryProvider<unknown, unknown>: a provider is served as the
//   internal IRegistryProvider, which RegistryProvider<C, V> implements, making its table's entries as RegistryEntry<C> and running its
//   compose and adapters on them.
using PiSharp.Chord.Contracts;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Registries;

/// <summary>
/// A registry token (upstream <c>Registry</c>): its name (as type names, <c>[a-z0-9@/._~-]</c>), the version the bundling plugin was built
/// against, the contribution type <typeparamref name="C"/> and the composed value type <typeparamref name="V"/>. Shared: every contributor
/// and reader bundles its own copy. Made by <see cref="Registry.Define{C, V}"/>.
/// </summary>
/// <typeparam name="C">What contributors give.</typeparam>
/// <typeparam name="V">What readers get.</typeparam>
public sealed class Registry<C, V>
{
    internal Registry(string name, long version)
    {
        Name = name;
        Version = version;
    }

    /// <summary>Always <c>"registry"</c>.</summary>
    public string Kind => "registry";

    /// <summary>The registry's name, shared by every copy of the token.</summary>
    public string Name { get; }

    /// <summary>The registry version this plugin is built against.</summary>
    public long Version { get; }

    /// <inheritdoc/>
    public override string ToString() => $"registry {Name} v{Version}";
}

/// <summary>The options of <see cref="Registry.Define{C, V}"/> (upstream's <c>{ version }</c>).</summary>
public sealed class RegistryOptions
{
    /// <summary>The version this plugin is built against: a positive integer.</summary>
    public required long Version { get; init; }
}

/// <summary>Registry tokens (upstream <c>defineRegistry</c>).</summary>
public static class Registry
{
    /// <summary>Declares a registry token: <paramref name="name"/> and the version this plugin is built against (upstream
    /// <c>defineRegistry</c>). Throws an <see cref="ArgumentException"/> for a name that is no lowercase type name, and for a version that is
    /// no positive integer.</summary>
    public static Registry<C, V> Define<C, V>(string name, RegistryOptions options)
    {
        ContractsInternal.CheckName(name, "registry");
        ArgumentNullException.ThrowIfNull(options);
        var version = options.Version;
        // Declaration policy: Number.isSafeInteger(version) && version >= 1.
        if (version < 1 || version > 9007199254740991) throw new ArgumentException($"{name}.version: expected a positive integer");
        return new Registry<C, V>(name, version);
    }
}

/// <summary>One contribution in a registry's table (upstream <c>RegistryEntry</c>): its plugin, its object type for an object contributor,
/// its version, its value. Chord makes one per contribution value and keeps it while the value is the same.</summary>
/// <typeparam name="C">The contribution type.</typeparam>
public sealed class RegistryEntry<C>
{
    /// <summary>The contributing plugin.</summary>
    public required string Plugin { get; init; }

    /// <summary>The contributing object's type; null for a facet.</summary>
    public string? Type { get; init; }

    /// <summary>The registry version its contributor was built against (an older one adapted to <typeparamref name="C"/>).</summary>
    public required long Version { get; init; }

    /// <summary>The contributed value (adapted, for an older version).</summary>
    public required C Value { get; init; }

    /// <summary>Optional immutable identity of the code owner that declared this contribution. Native plugin
    /// integrations use it when callbacks are constructed by shared libraries; ordinary registries leave it null.</summary>
    public object? CallbackOwner { get; init; }
}

/// <summary>A conflict <c>compose</c> resolved with its fallback (upstream <c>RegistryConflict</c>): reported as <c>registry_conflict</c>,
/// once per conflict episode.</summary>
/// <param name="Message">What conflicts.</param>
/// <param name="Plugins">The plugins involved.</param>
public sealed record RegistryConflict(string Message, IReadOnlyList<string> Plugins);

/// <summary>What <c>compose</c> gets besides the table (upstream <c>ComposeContext</c>).</summary>
public sealed class ComposeContext
{
    private readonly Action<RegistryConflict> report;

    /// <param name="report">Gets the conflicts <c>compose</c> reports.</param>
    public ComposeContext(Action<RegistryConflict> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        this.report = report;
    }

    /// <summary>Reports a conflict <c>compose</c> resolved (the first by plugin name wins, for example).</summary>
    public void Report(RegistryConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        report(conflict);
    }
}

/// <summary>The adapters of one older version (upstream's entry of <c>RegistryAccepts</c>), the provider's code.</summary>
/// <typeparam name="C">The provider's contribution type.</typeparam>
/// <typeparam name="V">The provider's composed value type.</typeparam>
public sealed class RegistryAdapters<C, V>
{
    /// <summary>Maps an older contribution (its contributor's type, which the adapter knows) to <typeparamref name="C"/>, once per
    /// contribution value; without it, that version's contributions are refused.</summary>
    public Func<object?, C>? Contribution { get; init; }

    /// <summary>Maps the composed value back for a reader of that version, once per composition; without it, that version's readers get
    /// <c>contract_mismatch</c>. Its result is the older reader's value type.</summary>
    public Func<V, object?>? Value { get; init; }
}

/// <summary>Adapters of older versions, by version (upstream <c>RegistryAccepts</c>), the provider's code.</summary>
/// <typeparam name="C">The provider's contribution type.</typeparam>
/// <typeparam name="V">The provider's composed value type.</typeparam>
public sealed class RegistryAccepts<C, V> : Dictionary<long, RegistryAdapters<C, V>>
{
}

/// <summary>What the defining plugin's facet serves a registry with (upstream <c>RegistryProvider</c>, <c>host.serve(R, provider)</c>).</summary>
/// <typeparam name="C">The contribution type.</typeparam>
/// <typeparam name="V">The composed value type.</typeparam>
public sealed class RegistryProvider<C, V> : IRegistryProvider
{
    /// <summary>
    /// Composes the table: pure and synchronous, always a value (a fallback built in), conflicts reported. Runs when the table changed and
    /// someone reads or follows the value; a throw is the owner's bug (<c>registry_failed</c>, the last good composition stays).
    /// </summary>
    public required Func<IReadOnlyList<RegistryEntry<C>>, ComposeContext, V> Compose { get; init; }

    /// <summary>The adapters of older versions; none by default.</summary>
    public RegistryAccepts<C, V>? Accepts { get; init; }

    bool IRegistryProvider.HasCompose => Compose is not null;
    Delegate IRegistryProvider.ComposeCallback => Compose;

    Delegate? IRegistryProvider.ContributionAdapter(long version) => AdaptersOf(version)?.Contribution;

    Delegate? IRegistryProvider.ValueAdapter(long version) => AdaptersOf(version)?.Value;

    object? IRegistryProvider.AdaptContribution(Delegate adapter, object? old) => ((Func<object?, C>)adapter)(old);

    object? IRegistryProvider.MapValue(Delegate adapter, object? current) => ((Func<V, object?>)adapter)(RegistriesInternal.Cast<V>(current));

    object IRegistryProvider.Entry(Contributor contributor, long version, object? value, object? callbackOwner)
    {
        if (!RegistriesInternal.TryCast<C>(value, out var typed))
            throw new InvalidCastException($"a contribution of {RegistriesInternal.TypeName(value)}, not of {typeof(C).Name}");
        return new RegistryEntry<C> { Plugin = contributor.Plugin, Type = contributor.Type, Version = version, Value = typed, CallbackOwner = callbackOwner };
    }

    bool IRegistryProvider.Made(object entry) => entry is RegistryEntry<C>;

    object[] IRegistryProvider.Table(List<object> entries)
    {
        // An array of the entries' own type: compose gets it as it is (array covariance keeps it an object[] for the record).
        var table = new RegistryEntry<C>[entries.Count];
        for (var index = 0; index < table.Length; index++) table[index] = (RegistryEntry<C>)entries[index];
        return table;
    }

    object? IRegistryProvider.Compose(object[] table, ComposeContext context) => Compose((RegistryEntry<C>[])table, context);

    private RegistryAdapters<C, V>? AdaptersOf(long version) => Accepts is { } accepts && accepts.TryGetValue(version, out var adapters) ? adapters : null;
}

/// <summary>
/// A registry's composed value as a reader of one version sees it (upstream <c>RegistryValue</c>; <c>self.registry</c>,
/// <c>host.registry</c>): a native watchable. <see cref="INativeWatchable{T}.Value"/> is the last good composition (a later throwing
/// <c>compose</c> keeps it; it throws the failure while there is none), a watch gets each new one.
/// </summary>
/// <typeparam name="V">The composed value type of the reader's version.</typeparam>
public interface IRegistryValue<V> : INativeWatchable<V>
{
    /// <summary>
    /// A synchronous observer of its compositions (as Durable's <c>observeXXX</c>): <see cref="INativeObserver{T}.Advance"/> runs inside the
    /// batch that composed, so it only records; an attached observer counts as watching, so the composition runs for it. Throws the failure
    /// when there is no value to start at; aborting <paramref name="context"/> detaches it (<c>aborted</c>), the host's close ends it
    /// (<c>ended</c>).
    /// </summary>
    NativeObservation<V> Observe(INativeObserver<V> observer, ChordContext context);
}

/// <summary>
/// A provider as a registry record serves it (upstream's <c>RegistryProvider&lt;unknown, unknown&gt;</c>): <see cref="RegistryProvider{C, V}"/>
/// without its types. Its adapters are compared by identity (a reload's provider brings others, which adapt again).
/// </summary>
internal interface IRegistryProvider
{
    Delegate ComposeCallback { get; }
    /// <summary>Whether it has a <c>compose</c> (checkProvider).</summary>
    bool HasCompose { get; }

    /// <summary>The adapter of contributions of <paramref name="version"/>; null when there is none.</summary>
    Delegate? ContributionAdapter(long version);

    /// <summary>The adapter mapping the composed value back for readers of <paramref name="version"/>; null when there is none.</summary>
    Delegate? ValueAdapter(long version);

    /// <summary>Runs <paramref name="adapter"/>, one of its contribution adapters, on <paramref name="old"/>.</summary>
    object? AdaptContribution(Delegate adapter, object? old);

    /// <summary>Runs <paramref name="adapter"/>, one of its value adapters, on the composition <paramref name="current"/>.</summary>
    object? MapValue(Delegate adapter, object? current);

    /// <summary>A table entry of <paramref name="value"/>; throws an <see cref="InvalidCastException"/> when it is no contribution of its
    /// type (a token of another type: PiSharp-only, as JavaScript has no casts).</summary>
    object Entry(Contributor contributor, long version, object? value, object? callbackOwner);

    /// <summary>Whether <paramref name="entry"/> is one of its type (an entry made for another provider's type is made again).</summary>
    bool Made(object entry);

    /// <summary>The table of <paramref name="entries"/> (each one it made), as its compose takes it.</summary>
    object[] Table(List<object> entries);

    /// <summary>Runs its <c>compose</c> on <paramref name="table"/>, one <see cref="Table"/> made.</summary>
    object? Compose(object[] table, ComposeContext context);
}
