// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/registries/value.ts
//
// Registry values (slice 8 §1 "Reading", S2, S3): a registry's composed value as the readers of one version see it, a native source. The
// provider's version reads the composition itself; an older one reads it mapped back with accepts[old].value, once per composition, kept
// only while someone reads or follows it; a version the provider cannot serve fails contract_mismatch. Its readers (attempts that declared
// it) keep the mapping; its waiters (attempts parked on it, a waiting host.registry) are woken at every change of whether it is usable, and
// of its failure while it is not (dependency model §9). Mappings:
// - RegistryValueSource<V> is a FollowedSource<V> typed by its readers' token (upstream's is FollowedSource<unknown>); the record and the
//   mapping work on objects, and Start casts the composition (or its mapping) to V. One that is no V fails the version with a
//   contract_mismatch ChordError (PiSharp-only: JavaScript has no casts; a ChordError, compared by value, so waiters are not woken again by
//   the same mismatch). IRegistryValueSource is what the record calls on a value of any type.
// - The readers and waiters are Followers of tokens, one per read or wait (one object may read twice), in insertion order.
// - "The same composition" is JavaScript's === (RegistriesInternal.Same); a mapping's adapter by identity.
// - Registered (open on its record, or closed with its last start) is the record field, null once closed, and the start it closed with.
using System.Runtime.ExceptionServices;
using PiSharp.Chord.Data;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Registries;

/// <summary>Who waits for a registry's value (upstream <c>RegistryWaiter</c>: a parked attempt, a <c>host.registry</c>): told when it may
/// have changed.</summary>
internal interface IRegistryWaiter
{
    /// <summary>The value may have changed (inside its batch: it only records).</summary>
    void DependencyMoved();

    /// <summary>The value closed with its host: the wait ends with <paramref name="error"/> (<c>released/closed</c>).</summary>
    void Closed(ChordError error);
}

/// <summary>What a registry record calls on the value of a version, whatever its readers' type.</summary>
internal interface IRegistryValueSource
{
    /// <summary>The value type its readers read.</summary>
    Type ValueType { get; }

    /// <summary>The registry's table changed (inside a batch).</summary>
    void Recomposed();

    /// <summary>The registry's table changed again while not composed since (inside a batch).</summary>
    void Relisted();

    /// <summary>The host closed.</summary>
    void Close();
}

/// <summary>One version's view of a registry's composed value (upstream <c>RegistryValueSource</c>).</summary>
/// <typeparam name="V">The value type of the version's readers.</typeparam>
internal sealed class RegistryValueSource<V> : FollowedSource<V>, IRegistryValue<V>, IRegistryValueSource
{
    // Its record while the host is open; null once closed, with the start it closed at.
    private RegistryRecord? record;
    private NativeStart<V>? closedAt;
    // Its readers, each a token of its own (one object may read twice), until released.
    private readonly Followers<object> readers = new();
    // Its waiters, each a token of its own, until released.
    private readonly Followers<WaiterToken> waiters = new();
    // What its waiters were last told: usable (a value, or a failure with a last good one), or the error it has none for; a change wakes
    // them. Null while nothing was told.
    private Told? told;
    private Mapped? mapped;
    private MapFailure? mapFailure;

    public RegistryValueSource(RegistryRecord record, long version, Batch batch)
        : base(batch)
    {
        ArgumentNullException.ThrowIfNull(record);
        this.record = record;
        Name = record.Name;
        Version = version;
    }

    public string Name { get; }

    public long Version { get; }

    /// <inheritdoc/>
    public Type ValueType => typeof(V);

    /// <summary>The composition (or its mapping) at its last good value; throws the failure when there is none.</summary>
    public override V Value
    {
        get
        {
            var start = Start();
            if (start.Failed && !start.HasLast) ExceptionDispatchInfo.Throw(start.Error!);
            return start.Value;
        }
    }

    /// <summary>
    /// Its composition (mapped for an older version), composed now if its table changed; a change of whether it is usable, or of its failure
    /// while it is not, wakes its waiters.
    /// </summary>
    public override NativeStart<V> Start()
    {
        if (record is not { } open) return closedAt!;
        var start = Typed(StartOf(open));
        if (waiters.Count > 0) Tell(start);
        return start;
    }

    /// <summary>Its composition (or mapping) now; throws while it is failed, though <see cref="Value"/> keeps the last good one.</summary>
    public override V Current()
    {
        var start = Start();
        if (start.Failed) ExceptionDispatchInfo.Throw(start.Error!);
        return start.Value;
    }

    /// <summary>Followed while someone watches or observes it, and while waiters wait on it.</summary>
    public override bool Watched => base.Watched || waiters.Count > 0;

    /// <summary>Registers a reader (an attempt that declared it) until the returned release: its mapping is kept meanwhile.</summary>
    public Action Read()
    {
        var token = new object();
        readers.Add(token);
        return () =>
        {
            readers.Remove(token);
            if (readers.Count == 0 && !base.Watched) DropMapping();
        };
    }

    /// <summary>
    /// Registers <paramref name="waiter"/> (a parked attempt's object, a waiting <c>host.registry</c>) until the returned release: it is told
    /// of every change of whether this value is usable (inside the batch that made it: it only records).
    /// </summary>
    public Action Wait(IRegistryWaiter waiter)
    {
        ArgumentNullException.ThrowIfNull(waiter);
        var token = new WaiterToken(waiter);
        if (waiters.Count == 0) told = null;
        waiters.Add(token);
        try { _ = Start(); }
        catch { waiters.Remove(token); throw; }
        return () =>
        {
            waiters.Remove(token);
            if (waiters.Count == 0) { told = null; record?.Clock?.Stop(this); }
        };
    }

    /// <summary>The registry's table changed (inside a batch): its dependents are marked, its followers get the next frame.</summary>
    public void Recomposed() => Changed();

    /// <summary>
    /// The registry's table changed again while not composed since (inside a batch): its dependents are marked already (none read it
    /// meanwhile, or it would be composed); its followers get the next frame. Marking stops here, so a cycle of registries through their
    /// values ends.
    /// </summary>
    public void Relisted() => List();

    /// <summary>The host closed: its watches and observers end, its readers and mapping go; it keeps its last start only.</summary>
    public void Close()
    {
        NativeStart<V> last;
        try
        {
            last = Start();
        }
        catch (Exception error)
        {
            last = NativeStart<V>.Failure(error, hasLast: false);
        }
        record?.Clock?.Stop(this);
        record = null;
        closedAt = last;
        readers.Clear();
        var all = waiters.Clear();
        var closed = new ChordError("released", $"registry {Name}: closed", "closed");
        foreach (var token in all) token.Waiter.Closed(closed);
        told = null;
        DropMapping();
        End();
    }

    /// <summary>Nobody follows it: without readers, its mapping goes (computed again on the next read).</summary>
    protected override void Unfollowed()
    {
        if (readers.Count == 0) DropMapping();
    }

    /// <summary>Wakes the waiters when <paramref name="start"/> says something else than they were told (by usability, then by the
    /// error).</summary>
    private void Tell(NativeStart<V> start)
    {
        var previous = told;
        var usable = !start.Failed || start.HasLast;
        if (usable ? previous is { Usable: true } : previous is { Usable: false } && SameError(previous.Error, start.Error)) return;
        told = usable ? Told.UsableNow : new Told(false, start.Error);
        if (usable) record?.Clock?.Stop(this);
        else if (start.Error is { } failure)
        {
            var error = DataInternal.AsChordError(failure, Name);
            if (previous is { Usable: false }) record?.Clock?.Failed(this, error);
            else record?.Clock?.Start(this, "registry " + Name, error);
        }
        // The first start of a wait wakes nobody: the waiter reads it itself.
        if (previous is null) return;
        foreach (var token in waiters.Items) token.Waiter.DependencyMoved();
    }

    private void DropMapping()
    {
        mapped = null;
        mapFailure = null;
    }

    /// <summary>Its start as the record (or its mapping) gives it: untyped.</summary>
    private NativeStart<object?> StartOf(RegistryRecord open)
    {
        var provided = open.ProvidedVersion;
        if (provided is null || provided == Version) return open.Start();
        var map = provided > Version ? open.ValueAdapter(Version) : null;
        if (map is null)
        {
            var message = $"registry {Name}: version {Version} is not served here (version {provided} is)";
            return NativeStart<object?>.Failure(new ChordError("contract_mismatch", message), hasLast: false);
        }
        return MappedStart(open, open.Start(), map);
    }

    /// <summary>
    /// The composition <paramref name="current"/> mapped back with <paramref name="map"/>, once per composition; a throwing mapping fails
    /// this version only (reported once per episode), keeping its last good mapping; a composition mapped before heals it.
    /// </summary>
    private NativeStart<object?> MappedStart(RegistryRecord open, NativeStart<object?> current, Delegate map)
    {
        if (current.Failed && !current.HasLast) return current;
        var from = current.Value;
        // A null composition is a composition too: the records, not their fields, say whether there is one.
        // A mapping built from a contribution that left is no last good value any more (decision 5).
        if (mapped?.Contributions is { } kept && !open.InTable(kept)) mapped = null;
        var last = mapped;
        var failure = mapFailure;
        if (last is not null && RegistriesInternal.Same(last.From, from) && ReferenceEquals(last.Map, map))
        {
            mapFailure = null;
        }
        else if (failure is null || !RegistriesInternal.Same(failure.From, from) || !ReferenceEquals(failure.Map, map))
        {
            try
            {
                var value = open.Adapt(() => open.MapValue(map, from));
                mapped = new Mapped(from, map, value, open.ComposedFrom);
                mapFailure = null;
            }
            catch (Exception error)
            {
                if (failure is null) open.Failed(error);
                mapFailure = new MapFailure(from, map, error);
            }
        }
        var good = mapped;
        if (mapFailure is not null) return NativeStart<object?>.Failure(mapFailure.Error, good is not null, good is null ? null : good.Value);
        if (current.Failed) return NativeStart<object?>.Failure(current.Error!, good is not null, good is null ? null : good.Value);
        return NativeStart<object?>.Of(good!.Value);
    }

    /// <summary>The start as its readers' <typeparamref name="V"/>: a composition that is none fails the version
    /// (<c>contract_mismatch</c>, PiSharp-only).</summary>
    private NativeStart<V> Typed(NativeStart<object?> start)
    {
        if (!start.Failed)
            return RegistriesInternal.TryCast<V>(start.Value, out var value) ? NativeStart<V>.Of(value) : NativeStart<V>.Failure(Mismatch(start.Value), hasLast: false);
        if (!start.HasLast) return NativeStart<V>.Failure(start.Error!, hasLast: false);
        return RegistriesInternal.TryCast<V>(start.Value, out var last)
            ? NativeStart<V>.Failure(start.Error!, hasLast: true, last)
            : NativeStart<V>.Failure(Mismatch(start.Value), hasLast: false);
    }

    private ChordError Mismatch(object? value) =>
        new("contract_mismatch", $"registry {Name}: version {Version} reads {typeof(V).Name}, not {RegistriesInternal.TypeName(value)}");

    /// <summary>Whether two failures say the same: the same error, or ChordErrors alike (a version mismatch is made per read).</summary>
    private static bool SameError(object? a, object? b) =>
        ReferenceEquals(a, b) || a is ChordError left && b is ChordError right && left.Code == right.Code && left.Reason == right.Reason && left.Message == right.Message;

    /// <summary>A waiter's registration: a token of its own (one waiter may wait twice).</summary>
    private sealed class WaiterToken(IRegistryWaiter waiter)
    {
        public IRegistryWaiter Waiter { get; } = waiter;
    }

    /// <summary>What the waiters were last told: usable, or the error it has no value for.</summary>
    private sealed record Told(bool Usable, Exception? Error)
    {
        public static Told UsableNow { get; } = new(true, null);
    }

    /// <summary>
    /// The last good mapping for an older version, the composition it was made from, the adapter it was made with (a reload's provider brings
    /// another, which maps again), and the contributions that composition was built from (kept only while all are in the table, decision 5).
    /// </summary>
    private sealed record Mapped(object? From, Delegate Map, object? Value, RegistryContribution[]? Contributions);

    /// <summary>A failed mapping of composition <paramref name="From"/> by <paramref name="Map"/>, until another composition or adapter
    /// maps.</summary>
    private sealed record MapFailure(object? From, Delegate Map, Exception Error);
}

internal static partial class RegistriesInternal
{
    /// <summary>Whether <paramref name="value"/> is a <typeparamref name="T"/>, null being one when <typeparamref name="T"/> takes null.</summary>
    internal static bool TryCast<T>(object? value, out T typed)
    {
        if (value is T cast)
        {
            typed = cast;
            return true;
        }
        typed = default!;
        return value is null && default(T) is null;
    }

    /// <summary><paramref name="value"/> as a <typeparamref name="T"/>; throws an <see cref="InvalidCastException"/> when it is none.</summary>
    internal static T Cast<T>(object? value) =>
        TryCast<T>(value, out var typed) ? typed : throw new InvalidCastException($"{TypeName(value)}, not {typeof(T).Name}");

    /// <summary>The type name of <paramref name="value"/> in an error: <c>null</c> for null.</summary>
    internal static string TypeName(object? value) => value?.GetType().Name ?? "null";
}
