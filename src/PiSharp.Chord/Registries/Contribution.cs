// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/registries/contribution.ts
//
// One contributor's contribution to a registry (slice 8 §1): its table entry, made once per value (an older version's adapted once per
// value), followed when it is a Chord watchable, left out and reported when it fails or is refused. Mappings:
// - Contributor is a record struct (Type null for upstream's undefined: a facet).
// - ContributionContent is a class of three kinds: a plain value, a Chord JSON watchable (DeriveSource: a Source, or a Constant, which never
//   changes and is not followed) or a native source (NativeSource, read through UntypedCurrent, which throws while it is failed).
// - "The same value" is JavaScript's ===: JSON values by DeltaDiff.Strict (containers by reference, primitives by value, so a derive that
//   computes the same boolean again keeps its entry), anything else by StateInternal.Identical (RegistriesInternal.Same).
// - The entry is made by the provider (IRegistryProvider.Entry), typed as its table's; a value that is no contribution of that type (a token
//   of another type) throws there, inside the same catch as a throwing adapter, so it is left out and reported contribution_failed
//   (PiSharp-only: JavaScript has no casts). An entry made for another provider's type (a reload's) is made again.
using System.Text.Json.Nodes;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Registries;

/// <summary>Who contributes (upstream <c>Contributor</c>): a facet (by plugin, <see cref="Type"/> null) or a host-lifetime object (by plugin
/// and type).</summary>
/// <param name="Plugin">The contributing plugin.</param>
/// <param name="Type">The contributing object's type; null for a facet.</param>
internal readonly record struct Contributor(string Plugin, string? Type);

/// <summary>What a contribution is (upstream <c>ContributionContent</c>): a plain value, a Chord JSON watchable (a source or a constant), or a
/// native source; the watchables followed for their value.</summary>
internal sealed class ContributionContent
{
    private ContributionContent(ContributionKind kind, object? value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>Which kind it is.</summary>
    public ContributionKind Kind { get; }

    /// <summary>The plain value, or the source (a <see cref="State.Source"/>, a <see cref="Constant"/> or a <see cref="NativeSource"/>).</summary>
    public object? Value { get; }

    /// <summary>A plain value, whatever methods it has (upstream <c>{ kind: "plain", value }</c>).</summary>
    public static ContributionContent Plain(object? value) => new(ContributionKind.Plain, value);

    /// <summary>A Chord JSON source, followed (upstream <c>{ kind: "json", source }</c>).</summary>
    public static ContributionContent Json(Source source) => new(ContributionKind.Json, source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>A Chord constant, never followed (upstream <c>{ kind: "json", source }</c> of a <c>Constant</c>).</summary>
    public static ContributionContent Json(Constant constant) => new(ContributionKind.Json, constant ?? throw new ArgumentNullException(nameof(constant)));

    /// <summary>A native source, followed (upstream <c>{ kind: "native", source }</c>): a writable, another registry's value.</summary>
    public static ContributionContent Native(NativeSource source) => new(ContributionKind.Native, source ?? throw new ArgumentNullException(nameof(source)));
}

/// <summary>The kinds of <see cref="ContributionContent"/>.</summary>
internal enum ContributionKind
{
    /// <summary>A plain value.</summary>
    Plain,

    /// <summary>A Chord JSON watchable.</summary>
    Json,

    /// <summary>A native source.</summary>
    Native,
}

/// <summary>
/// One contributor's contribution to a registry (upstream <c>RegistryContribution</c>): a plain value, or a Chord source it follows as a
/// dependent (a change marks the registry's table). Its table entry is made once per value (an older version's adapted once per value); a
/// failure or refusal leaves it out, reported once per episode.
/// </summary>
internal sealed class RegistryContribution : IDependent
{
    private readonly ContributionContent content;
    // The last adaptation of an older contribution: the value and adapter it was made from, and its result.
    private Adapted? adapted;
    // Its entry and the value it was made from (null entry: none yet; a null value is a value).
    private object? entry;
    private object? entryValue;
    // Left out since its last entry: failing (reported once), or refused (reported once).
    private Out @out = Out.None;

    public RegistryContribution(RegistryRecord record, Contributor contributor, long version, ContributionContent content)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(content);
        Record = record;
        Contributor = contributor;
        Version = version;
        this.content = content;
    }

    private enum Out
    {
        None,
        Failed,
        Refused,
    }

    /// <summary>The registry it contributes to.</summary>
    public RegistryRecord Record { get; }

    public Contributor Contributor { get; }

    /// <summary>The registry version its contributor was built against.</summary>
    public long Version { get; }

    /// <summary>Its source changed (inside a batch): the registry's table is marked.</summary>
    public void Invalidate() => Record.ContributionChanged();

    /// <summary>A retirement of its source's document changes nothing beyond the value.</summary>
    public void SourceRetired(ReferenceSet<IDependent> visited)
    {
    }

    /// <summary>Starts following its source, if it has one.</summary>
    public void Follow() => Followed()?.Add(this);

    /// <summary>Stops following its source.</summary>
    public void Unfollow() => Followed()?.Remove(this);

    /// <summary>
    /// Its entry in a table of <paramref name="provider"/> at <paramref name="version"/>: the same object while its value is the same; null
    /// when left out (a refused version, a failing source, a throwing adapter, a value of another type).
    /// </summary>
    public object? Entry(long version, IRegistryProvider provider)
    {
        var adapter = Version < version ? provider.ContributionAdapter(Version) : null;
        if (Version > version || (Version < version && adapter is null))
        {
            if (@out != Out.Refused) Refused();
            return null;
        }
        try
        {
            var raw = Raw();
            var value = adapter is null ? raw : Adapt(raw, adapter, provider);
            // A null value is a value: the entry, not its value, says whether there was one.
            if (entry is null || !RegistriesInternal.Same(entryValue, value) || !provider.Made(entry))
            {
                entry = provider.Entry(Contributor, Version, value);
                entryValue = value;
            }
        }
        catch (Exception error)
        {
            if (@out != Out.Failed) Failed(error);
            return null;
        }
        @out = Out.None;
        return entry;
    }

    /// <summary>The dependents of its source when it changes (a constant never does).</summary>
    private Followers<IDependent>? Followed() => content.Value switch
    {
        NativeSource native when content.Kind == ContributionKind.Native => native.Dependents,
        Source json when content.Kind == ContributionKind.Json => json.Dependents,
        _ => null,
    };

    /// <summary>
    /// The contributed value now: a JSON source's value throws while it is failed or ended; a native source that failed (a registry whose
    /// <c>compose</c> threw) keeps a last good value for its readers, but is left out here as failed.
    /// </summary>
    private object? Raw() => content.Kind switch
    {
        ContributionKind.Plain => content.Value,
        ContributionKind.Json => content.Value switch
        {
            Source source => source.Value,
            Constant constant => constant.Value,
            _ => throw new InvalidOperationException("a JSON contribution is a source or a constant"),
        },
        _ => ((NativeSource)content.Value!).UntypedCurrent(),
    };

    private object? Adapt(object? raw, Delegate adapter, IRegistryProvider provider)
    {
        if (adapted is { } last && RegistriesInternal.Same(last.From, raw) && ReferenceEquals(last.Adapter, adapter)) return last.Value;
        var value = Record.Adapt(() => provider.AdaptContribution(adapter, raw));
        adapted = new Adapted(raw, adapter, value);
        return value;
    }

    private void Refused()
    {
        @out = Out.Refused;
        Record.ReportContribution(new ContributionRefusedError(Record.Name, Contributor.Plugin, Contributor.Type, Version));
    }

    private void Failed(Exception error)
    {
        @out = Out.Failed;
        Record.ReportContribution(new ContributionFailedError(Record.Name, Contributor.Plugin, Contributor.Type, StateInternal.HandedOut(error)));
    }

    /// <summary>An adaptation: the value and adapter it was made from, and its result.</summary>
    private sealed record Adapted(object? From, Delegate Adapter, object? Value);
}

internal static partial class RegistriesInternal
{
    /// <summary>JavaScript's <c>a === b</c> on contributions and compositions: JSON values by <see cref="Delta.DeltaDiff.Strict"/>
    /// (containers by reference, primitives by value), anything else by <see cref="StateInternal.Identical{T}"/>.</summary>
    internal static bool Same(object? a, object? b) =>
        a is null or JsonNode && b is null or JsonNode ? Delta.DeltaDiff.Strict((JsonNode?)a, (JsonNode?)b) : StateInternal.Identical(a, b);
}
