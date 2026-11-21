// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/admission.ts, and the admission parts of
// packages/chord/src/host/{attempt,object}.ts (the partials of Attempt and ChordObject below).
// Admission (slice 14 §2): the decision made about a caller before anything is built or started. A remote type's provider declares it with
// `Admit`, run first in every attempt: it reads data about the caller (references with keyed pins, registries, derives, live queries) and
// returns a watchable of `Admission.Allow(value)` or `Admission.Deny(reason)`; the factory is handed the value. A route asks the same way
// before it resolves or forwards a hold (Host/RouteAdmission.cs). This file has the app's types and one Decision: what one admit declared,
// what it says now, and how its owner follows it. What follows from a decision is its owner's: an attempt parks, goes on or is sealed
// (the partials below), a route holds back, forwards or releases.
//
// Native shape. An admission's decision is plain JSON (`{ "kind": "allow", "value": ... }` or `{ "kind": "deny", "reason": "..." }`): it
// flows through `self.Derive`, whose values are JSON, and is checked when it is read, as upstream checks it. There are no microtasks: an
// attempt's activation is always posted to the host's dispatcher, so `admit` runs in a later turn than the flush that sealed its
// predecessor (AI_DOCS/agent-reports/sync-1.6.0-chord-admission.md, section 2).
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Host;

/// <summary>An admission's two decisions, as the JSON an <see cref="Admit"/>'s watchable carries.</summary>
public static class Admission
{
    /// <summary>Allows the caller: <paramref name="value"/> is what the factory is handed about it, plain JSON the contract's
    /// <c>Admission</c> schema describes (never validated; null is JSON null, what a route's admission allows with). Small and rarely
    /// changing (a mode, a role): another value rebuilds the caller's object.</summary>
    public static JsonObject Allow(JsonNode? value = null) =>
        new() { ["kind"] = "allow", ["value"] = value is { Parent: not null } ? value.DeepClone() : value };

    /// <summary>Denies the caller. <paramref name="reason"/> reaches it as the message of its <c>forbidden</c> error: it is user-facing.</summary>
    public static JsonObject Deny(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new() { ["kind"] = "deny", ["reason"] = reason };
    }
}

/// <summary>What an admission declares with, valid only while it runs: references (and <c>At(key)</c> on their keyed members), registries,
/// derives and live queries. An admission decides and owns nothing: no documents, natives, routines or jobs.</summary>
public interface IAdmitSelf
{
    IReference Get(Contract type, JsonObject parameters);
    IRegistryValue<V> Registry<C, V>(Registry<C, V> registry);
    IWatchable<JsonNode?> Derive(IReadOnlyList<object> sources, Func<IReadOnlyList<object?>, JsonNode?> compute);
    IWatchable<JsonNode?> LiveQuery(INamedQuery query, object? args, long first);
}

/// <summary>A remote type's admission (<c>host.Serve(type, factory, new ServeOptions(Admit: admit))</c>): synchronous and declarative, run
/// first in every attempt. Its attempt parks until what it declared is live and hydrated, then reads the decision and follows it: an equal
/// value changes nothing, another one rebuilds the object, a denial shows <c>failed</c> with a <c>Denied</c> cause. It returns a JSON
/// watchable (a derive, a reference's member, a constant) of <see cref="Admission.Allow"/> or <see cref="Admission.Deny"/>.</summary>
public delegate IWatchable<JsonNode?> Admit(JsonObject parameters, JsonObject principal, IAdmitSelf self);

/// <summary>The factory of a remote type whose contract declares an <c>Admission</c>: it gets what its admission allowed after the
/// principal (it selects the variant and the members; <c>self.Job</c> is refused there).</summary>
public delegate HostImplementation AdmittedHostFactory(JsonObject parameters, JsonObject principal, JsonNode? admitted, ISelf self);

/// <summary>A route's admission (<c>RouteOptions.Admit</c>): asked once per principal and address with holds on the route; it allows with
/// <c>Admission.Allow()</c> (no value).</summary>
public delegate IWatchable<JsonNode?> RouteAdmit(JsonObject principal, Address address, IAdmitSelf self);

/// <summary>A served type's admission: <c>admit</c>, plugin code run first in every attempt (what it returns is checked there), the
/// fingerprint of its contract's admission schema, which a reload's staging compares, and the factory that takes the admitted value.</summary>
internal sealed record ServedAdmission(Admit Admit, string Fingerprint, AdmittedHostFactory Factory);

/// <summary>What a decision says at one check: allowed with a value, denied, or not decided (parked on what it reads, or broken). An allow
/// and a denial are the same object for as long as the value (canonical JSON) or the reason stays: its owner compares by reference.</summary>
internal abstract class Verdict
{
    private Verdict()
    {
    }

    internal sealed class Allowed(JsonNode? value) : Verdict
    {
        internal JsonNode? Value => value;
    }

    internal sealed class Denied(ChordError error) : Verdict
    {
        internal ChordError Error => error;
    }

    /// <summary>Not decided: <see cref="Parking.Parked"/> on what it reads (<c>loading</c>, or <c>failed</c> waiting on a source that
    /// failed or a decision that cannot be read), or <see cref="Parking.Broken"/> (a cycle, an ended source, a value that is no decision).</summary>
    internal sealed class Undecided(Parking parking) : Verdict
    {
        internal Parking Parking => parking;
    }
}

/// <summary>Who a decision is made for and what it tells. <paramref name="Label"/>: what it decides about, in errors and reports (an
/// object's label, or a route's address). <paramref name="Caller"/>: whom its references act as (a remote object's principal, the host for a
/// route). <paramref name="Dependent"/>: the object whose attempt it admits (cycles); none for a route. <paramref name="Moved"/>: something
/// it reads changed (a source's state, the decision's value): check it again.</summary>
internal sealed record DecisionOwner(ChordHost Host, string Label, Caller Caller, ChordObject? Dependent, Action Moved);

internal static class AdmissionInternal
{
    /// <summary>Refuses <c>self.Job</c> in an admitted type's factory (slice 14 §2.1), in an attempt and in staging: work for a person
    /// outlives the person's access, so it lives in a local store, carries the person in its arguments and asks the person's access again
    /// on every run.</summary>
    internal static void RefuseAdmittedJob(ServedType served, string label)
    {
        if (served.Admission is null) return;
        throw new ArgumentException($"{label}: self.job is not declared in an admitted object: work for a person lives in a local store");
    }

    /// <summary>What a denial rejects the calls running for the denied caller with (slice 14 §2.2): <c>forbidden/revoked</c>, naming
    /// <paramref name="label"/> and the denial's reason. The denied object itself shows <paramref name="denial"/>, <c>forbidden</c> with
    /// the reason alone.</summary>
    internal static ChordError Revoked(string label, ChordError denial) => new("forbidden", $"{label}: {denial.Message}", "revoked");

    /// <summary>What calls cut by a failure of kind <paramref name="cause"/> are answered: its error, <c>forbidden/revoked</c> for a
    /// denial (access withdrawn under a call already made).</summary>
    internal static ChordError Cut(string label, FailureCause cause) => cause.Kind == FailureKind.Denied ? Revoked(label, cause.Error) : cause.Error;
}

/// <summary>
/// One decision: the <c>self</c> its <c>admit</c> declares with, the requests it made (held until <see cref="Release"/>), and what it says.
/// <see cref="SettleAsync"/> brings it to a verdict (its requests live, their pinned keys loaded, its live queries opened);
/// <see cref="Now"/> reads it again. A source that only loads keeps the last decision; one that failed or ended, and a decision that throws
/// when read, do not (fail closed: slice 14 §2.2).
/// </summary>
internal sealed class Decision : IAdmitSelf, IDependent, IRegistryWaiter
{
    private sealed record RegistryRead(string Name, Func<Exception?> Error, Func<Action> Read, Func<IRegistryWaiter, Action> Wait);
    private readonly DecisionOwner owner;
    // The context admit declares under, while it runs.
    private ChordContext? declaring;
    private readonly List<Binding> references = [];
    // Its requests in declaration order (a reference or a registry): what its owner parks on.
    private readonly List<(Binding? Binding, RegistryRead? Registry)> dependencies = [];
    private readonly List<Derive> derives = [];
    private readonly List<Reader> readers = [];
    private readonly List<Action> releases = [];
    // The watchable admit returned, as Chord follows it: a Source or a Constant.
    private object? source;
    // The last allow or denial it read, with an allow's value as canonical JSON: an equal one is the same object.
    private (Verdict Verdict, string Key)? decided;
    // Whether a change of its source waits for the current batch's flush: one check per batch.
    private bool flushing;
    private bool released;
    private readonly Action referenceMoved;
    // Follows a live query it waits on: its next adoption checks again.
    private readonly ReaderWake readerWake;

    internal Decision(DecisionOwner owner)
    {
        this.owner = owner; referenceMoved = owner.Moved; readerWake = new(owner);
    }

    /// <summary>The references it declared (bound when admit ran), until its release.</summary>
    internal IReadOnlyList<Binding> Bindings => references;

    public IReference Get(Contract type, JsonObject parameters)
    {
        var context = AssertDeclaring("get");
        var binding = owner.Host.Acquire(type, parameters, owner.Caller, context, dependent: owner.Dependent);
        references.Add(binding); dependencies.Add((binding, null));
        return binding;
    }

    public IRegistryValue<V> Registry<C, V>(Registry<C, V> registry)
    {
        AssertDeclaring("registry");
        var value = owner.Host.RegistryRecord(registry.Name).Value<V>(registry.Version);
        dependencies.Add((null, new(registry.Name, () => { var start = value.Start(); return start.Failed && !start.HasLast ? start.Error : null; }, value.Read, value.Wait)));
        return new DispatchRegistryValue<V>(value, owner.Host.Dispatcher);
    }

    public IWatchable<JsonNode?> Derive(IReadOnlyList<object> sources, Func<IReadOnlyList<object?>, JsonNode?> compute)
    {
        AssertDeclaring("derive");
        var derive = new Derive(sources, compute, owner.Host.Batch);
        derives.Add(derive);
        return derive;
    }

    public IWatchable<JsonNode?> LiveQuery(INamedQuery query, object? args, long first)
    {
        AssertDeclaring("liveQuery");
        var at = ReadDeclarations.ReaderSite(owner.Host, owner.Label, owner.Moved);
        var (reader, live) = ReadDeclarations.RowsReader(at, query, args, first);
        readers.Add(reader);
        return live;
    }

    /// <summary>
    /// Runs <paramref name="admit"/> with declarations open under <paramref name="context"/> (its references are acquired as it declares
    /// them), then follows what it declared: its references, its registries (read until its release), its derives, and the watchable it
    /// returned. Throws what admit throws, a refused request's error, and for a result that is no Chord watchable; its caller then releases
    /// it.
    /// </summary>
    internal void Declare(Func<IAdmitSelf, object?> admit, ChordContext context)
    {
        declaring = context;
        object? returned;
        try { returned = admit(this); }
        finally { declaring = null; }
        var resolved = ChordHost.ResolveSource(returned);
        if (resolved is not Source && resolved is not Constant)
            throw new ArgumentException($"{owner.Label}: admit returns a JSON watchable of allow(value) or deny(reason)");
        source = resolved;
        foreach (var (binding, registry) in dependencies)
        {
            // Checked at every change of the reference until the release: its owner checks it again.
            if (binding is not null) { binding.Changed += referenceMoved; continue; }
            // Read until its release: an older version's mapping is kept meanwhile.
            releases.Add(registry!.Read()); releases.Add(registry.Wait(this));
        }
        foreach (var derive in derives)
            derive.Attach((declared, _) => ChordHost.ResolveSource(declared) ?? throw new ArgumentException("not a Chord source"),
                error => owner.Host.Report(new("derive_failed", error, owner.Label)));
        if (resolved is Source changing) changing.Dependents.Add(this);
    }

    /// <summary>
    /// Brings it to a verdict: once every request is live (a reference with keys still loading is not), its live queries are opened (each
    /// named to <paramref name="episode"/> while it blocks), then the decision is read. Nothing blocks <paramref name="episode"/> for an
    /// allow or a denial: <c>parked_long</c> reports no denial.
    /// </summary>
    internal async Task<Verdict> SettleAsync(IBlocked episode, ChordContext context)
    {
        if (Check(out var blocker) is { } waiting)
        {
            var unsettled = Unsettled(waiting);
            episode.BlockedBy(unsettled is Verdict.Undecided { Parking: Parking.Parked } ? blocker : null);
            return unsettled;
        }
        var label = $"{owner.Label}: a live query of its admission";
        var reading = await ReadDeclarations.OpenReadersAsync(readers, readerWake, episode, label, context);
        if (released) return new Verdict.Undecided(new Parking.Parked(RefState.Loading, true, null));
        if (reading is not Parking.Ready)
        {
            // A live query whose read has not run yet keeps the last decision, as a source that only loads does.
            var loading = reading is Parking.Parked { Shown.Kind: "loading" };
            return loading && decided is { } last ? last.Verdict : new Verdict.Undecided(reading);
        }
        var verdict = Read(context);
        episode.BlockedBy(null);
        return verdict;
    }

    /// <summary>What it says now, after it settled once (its live queries open): read synchronously, in the flush of the batch that
    /// changed it. The same object as before means nothing changed.</summary>
    internal Verdict Now(ChordContext context) => Check(out _) is { } waiting ? Unsettled(waiting) : Read(context);

    /// <summary>Lets go of everything it declared (once): its references and registry reads released, its derives and live queries
    /// detached. Its owner calls it once the decision that replaces it has bound its own requests, so a source held only by decisions never
    /// loses its last holder across a rebuild (slice 14 §2.2, rule 4). Never inside a batch (a reference is not released there).</summary>
    internal void Release()
    {
        if (released) return;
        released = true;
        var ended = new ChordError("released", $"{owner.Label}: its admission ended", "ended");
        if (source is Source changing) changing.Dependents.Remove(this);
        source = null;
        foreach (var reader in readers) { reader.Source.Dependents.Remove(readerWake); reader.Close(ended); }
        readers.Clear();
        foreach (var derive in derives) derive.Detach(ended);
        derives.Clear();
        foreach (var release in releases) release();
        releases.Clear();
        foreach (var binding in references) { binding.Changed -= referenceMoved; binding.Release(); }
        references.Clear(); dependencies.Clear();
    }

    /// <summary>Its source changed in the current batch: its owner checks it in that batch's flush, once however many changed.</summary>
    public void Invalidate()
    {
        if (flushing || released) return;
        flushing = true;
        var batch = owner.Host.Batch;
        batch.Flushing(_ =>
        {
            flushing = false;
            if (!released) owner.Moved();
        });
        // A change made outside any commit (a reference binding late) has no flush of its own: one commit soon runs it.
        if (!batch.Running) batch.Soon(static () => { }, ChordContext.Background);
    }

    /// <summary>A retired document under its source is a change of its value, which <see cref="Invalidate"/> tells.</summary>
    public void SourceRetired(ReferenceSet<IDependent> visited)
    {
    }

    /// <summary>A registry it reads changed whether its value is usable (inside the registry's batch: checked in its flush).</summary>
    public void DependencyMoved() => Invalidate();

    /// <summary>A registry it reads closed with its host: the host ends its owner.</summary>
    public void Closed(ChordError error)
    {
    }

    // What its requests say, in declaration order: null when every one is live; else what its owner shows while one is not (loading, or
    // failed naming the first failed one; a denied dependency shows its denial and names no blocker), or broken (a cycle, authority, an
    // ended dependency). The first blocker is what parked_long names.
    private Parking? Check(out Blocker? blocker)
    {
        blocker = null;
        RefState? failed = null; var firstPass = true; var parked = false;
        foreach (var (binding, registry) in dependencies)
        {
            if (registry is not null)
            {
                if (registry.Error() is not { } error) continue;
                var name = $"registry {registry.Name}";
                failed ??= RefState.Failed(DataInternal.WaitingFor(registry.Name, DataInternal.AsChordError(error, registry.Name)));
                blocker ??= new(name, "failed"); parked = true;
                continue;
            }
            var target = binding!.Target;
            if (owner.Dependent is { } dependent && (target?.Reaches(dependent, new(ReferenceEqualityComparer.Instance)) == true ||
                target?.ActivationId is { } activation && dependent.ChainContains(activation)))
                return new Parking.Broken(DataInternal.Invalid("cycle", $"{owner.Label}: dependency cycle"));
            var state = binding.RefState;
            if (state.Kind == "live") continue;
            if (state.Kind == "ended") return new Parking.Broken(state.Error!);
            parked = true;
            if (state.Cause is { } cause)
            {
                // Before the authority check: a denial's error is forbidden, and it heals when the dependency is admitted.
                if (cause.Kind == FailureKind.Denied) { failed ??= RefState.Failed(cause); continue; }
                if (cause.Error.Code == "forbidden" || cause.Error.Reason == "cycle") return new Parking.Broken(cause.Error);
                failed ??= RefState.Failed(DataInternal.WaitingFor(binding.Label, cause.Error));
                blocker ??= new(binding.Label, "failed");
            }
            else blocker ??= new(binding.Label, "loading");
            if (target?.BlocksFirstPass == true) firstPass = false;
        }
        if (!parked) return null;
        return new Parking.Parked(failed ?? RefState.Loading, firstPass, failed?.Cause);
    }

    // What a check that found a request not live says: broken; parked on a failure; or, only loading, the last decision (a provider
    // restarting or a link down changes no access), loading before the first.
    private Verdict Unsettled(Parking check)
    {
        if (check is Parking.Parked { Shown.Kind: "loading" } && decided is { } last) return last.Verdict;
        return new Verdict.Undecided(check);
    }

    // Reads the decision. A read that throws (a derive of admit, a failed live query) is a failed source: parked failed waiting on it. A
    // value that is neither allow(value) nor deny(reason) breaks.
    private Verdict Read(ChordContext context)
    {
        var label = owner.Label;
        JsonNode? decision;
        try { decision = source switch { Source changing => changing.Value, Constant constant => constant.Value, _ => null }; }
        catch (Exception error)
        {
            var cause = DataInternal.WaitingFor($"{label}: its admission", DataInternal.AsChordError(error, label));
            return new Verdict.Undecided(new Parking.Parked(RefState.Failed(cause), true, cause));
        }
        if (decision is JsonObject said && ChordJson.AsString(said["kind"]) is { } kind)
        {
            if (kind == "allow" && said.TryGetPropertyValue("value", out var value))
                return Decide("allow " + DataInternal.CanonicalJson(value, context), () => new Verdict.Allowed(value?.DeepClone()));
            if (kind == "deny" && ChordJson.AsString(said["reason"]) is { } reason)
                return Decide("deny " + reason, () => new Verdict.Denied(Errors.Forbidden(reason)));
        }
        return new Verdict.Undecided(new Parking.Broken(new ChordError("unavailable", $"{label}: its admission's value is neither allow(value) nor deny(reason)")));
    }

    // The decision with key: the one it had when the key is the same, else the one make makes.
    private Verdict Decide(string key, Func<Verdict> make)
    {
        if (decided is not { } last || last.Key != key) decided = (make(), key);
        return decided.Value.Verdict;
    }

    // The context admit runs under; outside it, a declaration is refused.
    private ChordContext AssertDeclaring(string declaration) =>
        declaring ?? throw new InvalidOperationException($"{owner.Label}: self.{declaration} is valid only in admit");

    private sealed class ReaderWake(DecisionOwner owner) : IDependent
    {
        public void Invalidate() => owner.Moved();
        public void SourceRetired(ReferenceSet<IDependent> visited)
        {
        }
    }
}

internal sealed partial class Attempt
{
    // An admitted type's decision, from its admit until its object releases it (ReleaseAdmission).
    private Decision? decision;
    // What its decision allowed, from its first allow on: its factory's value.
    private Verdict.Allowed? admitted;
    // What its object was last told of its decision, from the allow on: the same object means nothing changed.
    private Verdict? followed;
    // What its running calls reject with when a change of its admission sealed it (ChordObject.Readmit): the callers' waits, which a seal
    // cancels, end with this instead.
    private ChordError? cut;

    /// <summary>What its admission allowed (its factory's value); null before its first allow and without an admission.</summary>
    internal Verdict.Allowed? Admitted => admitted;

    /// <summary>The references its admission declared, while it holds them: what a parked attempt waits on before its factory ran.</summary>
    internal IReadOnlyList<Binding> AdmissionBindings => decision?.Bindings ?? [];

    /// <summary>Releases what its admission declared (its references, registry reads, derives and live queries); once.</summary>
    internal void ReleaseAdmission() => decision?.Release();

    /// <summary>Whether it is an admitted type's attempt whose admission has not allowed (yet, or again): it has run no factory, and what
    /// it parks on is what its object shows.</summary>
    internal bool Deciding => Served.Admission is not null && admitted is null && !Sealed;

    // Completed by a cut: what waits for its old work (a restart's drain) goes on at once.
    private TaskCompletionSource? cutSignal;
    internal Task CutTask => (cutSignal ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    internal bool WasCut => cut is not null;

    /// <summary>Seals it for a change of its admission: its running calls reject with <paramref name="error"/> from here on.</summary>
    internal void SealCut(ChordError error)
    {
        if (Sealed) return;
        cut = error;
        Seal(error, keepSources: true);
        (cutSignal ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// The admission phase (slice 14 §2.2): in a turn of its own (the activation is posted: a seal's flush has ended by then), admit
    /// declares against this attempt's decision, whose requests are bound as it declares them (its object then lets its predecessors' go).
    /// The attempt parks on them, and on a denial showing <c>failed</c> denied, with no timer, and goes on with the first allow. A throwing
    /// admit fails the attempt broken; its seal ends the wait.
    /// </summary>
    private async Task RunAdmissionAsync(ServedAdmission admission)
    {
        Cancellation.Token.ThrowIfCancellationRequested();
        var made = new Decision(new(Owner.Host, Owner.Label, Self.Caller, Owner, AdmissionMoved));
        try
        {
            using var lease = Owner.Host.RetainCallback(admission.Admit, Served.DeclaringFacet);
            // A remote object's identity has its principal.
            made.Declare(self => admission.Admit(Owner.Parameters.Value, Owner.Caller.Principal!, self), OpeningContext);
        }
        catch (Exception error)
        {
            made.Release();
            throw new ActivationException(new(FailureKind.Broken, DataInternal.AsChordError(error, Owner.Label)));
        }
        decision = made;
        Owner.Admitting(this);
        await ParkAsync(episode => DecideAsync(made, episode));
    }

    // One check of its admission while it parks on it: allowed, it goes on; denied, it shows failed denied.
    private async Task<Parking> DecideAsync(Decision deciding, IBlocked episode)
    {
        admitted = null;
        var verdict = await deciding.SettleAsync(episode, OpeningContext);
        switch (verdict)
        {
            case Verdict.Allowed allowed:
                admitted = allowed; followed = allowed;
                return Parking.Go;
            case Verdict.Denied denied:
                var cause = new FailureCause(FailureKind.Denied, denied.Error);
                if (activation is { Ended: false } tracing) ChordTelemetry.Admitted(tracing.Span, false);
                return new Parking.Parked(RefState.Failed(cause), true, cause);
            default:
                return ((Verdict.Undecided)verdict).Parking;
        }
    }

    /// <summary>
    /// Something its admission reads changed. While it still decides, its parked check runs again. From its allow on it follows the
    /// decision (slice 14 §2.2, "Watch"), live, opening its natives or draining for a reload alike: an equal decision changes nothing;
    /// anything else (another value, a denial, a source that failed or ended) is its object's to act on, which seals it at once and admits
    /// afresh. It goes on following after its own failure, so its failed object still shows a denial; a later attempt's admission takes
    /// over once it bound.
    /// </summary>
    private void AdmissionMoved()
    {
        if (decision is not { } deciding) return;
        if (admitted is null) { Wake(); return; }
        var verdict = deciding.Now(Owner.Context);
        if (ReferenceEquals(verdict, followed)) return;
        followed = verdict;
        Owner.Readmit(this, verdict);
    }
}

internal sealed partial class ChordObject
{
    // The attempts whose admission holds its requests, in the order they bound them (slice 14 §2.2, rule 4): a sealed one's are released
    // once a later one bound its own, so a source held only by admissions never ends across a rebuild. Made by the first.
    private List<Attempt>? admissions;
    // Whether presence lists show an admitted object's holders: from its first allow until a denial.
    private bool allowed;

    /// <summary>Whether presence lists show its holders now. An admitted object lists them from its first allow until a denial or its end,
    /// whatever it shows in between (a rebuild to another value, a source that failed); an object without an admission while it is live.</summary>
    internal bool ListsHolders => state.Kind != "ended" && (Served.Admission is null ? state.Kind == "live" : allowed);

    /// <summary>What its live attempt's admission allowed, with the fingerprint of the admission schema it was decided under: a reload's
    /// staging rehearses its new factory with it (slice 14 §2.5); null without one.</summary>
    internal (JsonNode? Value, string Fingerprint)? AdmittedValue =>
        Current is { Live: true, Admitted: { } value, Served.Admission: { } admission } ? (value.Value, admission.Fingerprint) : null;

    /// <summary><paramref name="attempt"/>'s admission bound its requests: the admissions of the sealed attempts before it are let go
    /// (slice 14 §2.2, rule 4).</summary>
    internal void Admitting(Attempt attempt)
    {
        (admissions ??= []).Add(attempt);
        ReleaseSuperseded();
    }

    // Releases the admission of every sealed attempt a later one's followed (its requests are bound: a source held only by them stays
    // held). The last one's stays, sealed or not, until a later attempt's binds or the object ends.
    private void ReleaseSuperseded()
    {
        if (admissions is not { Count: > 1 } all) return;
        var last = all[^1];
        foreach (var attempt in all.ToArray())
        {
            if (ReferenceEquals(attempt, last) || !attempt.Sealed) continue;
            all.Remove(attempt);
            attempt.ReleaseAdmission();
        }
    }

    // Last, as its references: nothing of it is admitted any more.
    private void ReleaseAdmissions()
    {
        if (admissions is not { } all) return;
        admissions = null;
        foreach (var attempt in all) attempt.ReleaseAdmission();
    }

    // An admitted object's first allow, or its first since a denial: presence lists its holders from now on.
    private void Allow()
    {
        if (allowed || Served.Admission is null) return;
        allowed = true;
    }

    /// <summary>
    /// The admission of <paramref name="attempt"/> no longer says what it allowed (slice 14 §2.2, "Watch"): <paramref name="verdict"/> is
    /// another value, a denial, or no decision (a source that failed or ended). What follows depends on where the attempt stands: the
    /// object's current attempt is sealed and a new one admits (<see cref="Rebuild"/>); a restart's old attempt that still drains is sealed
    /// (<see cref="SealDraining"/>); the last attempt of a failed object makes the object follow a denial without running its failed
    /// factory again (<see cref="FollowFailed"/>). An attempt a later one replaced tells nothing.
    /// </summary>
    internal void Readmit(Attempt attempt, Verdict verdict)
    {
        if (ending || Host.Closed) return;
        var denial = (verdict as Verdict.Denied)?.Error;
        if (ReferenceEquals(Current, attempt) && !attempt.Sealed) Rebuild(attempt, denial);
        else if (ReferenceEquals(openOld, attempt) && attempt.Draining) SealDraining(attempt, denial);
        else if (state.Kind == "failed" && admissions is { Count: > 0 } all && ReferenceEquals(all[^1], attempt)) FollowFailed(attempt, verdict);
    }

    // Seals the object's attempt at once, with no drain, and admits afresh: its running calls are rejected forbidden/revoked for a denial
    // and unavailable otherwise, nothing of it is published from here on (its holders stop following it in this flush: they keep their last
    // values while it loads, none for a denial, which the object shows at once), and it is torn down in the background. A new attempt
    // admits, in a later turn, and a call that crossed the seal waits for it at the gate. A lingering object without holders ends instead.
    private void Rebuild(Attempt attempt, ChordError? denial)
    {
        if (linger is not null)
        {
            // In a later turn: an end releases references, which is never done inside a batch. A holder that came meanwhile gets the
            // rebuild instead.
            Host.Dispatcher.Post(() =>
            {
                if (ending || Host.Closed) return;
                if (linger is not null) _ = EndAsync(new("released", $"{Label}: ended", "ended"), NativeCloseReason.End);
                else if (ReferenceEquals(Current, attempt) && !attempt.Sealed) Rebuild(attempt, denial);
            });
            return;
        }
        attempt.SealCut(Readmitted(denial));
        retry?.Cancel(); retry?.Dispose(); retry = null;
        var next = new Attempt(this, Served);
        retryPending = false;
        Current = next;
        state = denial is null ? RefState.Loading : Denied(denial);
        Cut(attempt);
        CloseLater(attempt);
        Activate(next);
    }

    // A restart's old attempt, still draining, is sealed at once (its successor admits by itself): the old work on it is rejected as
    // Rebuild rejects calls, and its holders leave it in this flush; a denial is shown now, not when the successor's own admission finds
    // it. Its end (RestartAsync) goes on from here: it closes the attempt and lets its admission's requests go (never here: a reference is
    // not released inside a batch).
    private void SealDraining(Attempt attempt, ChordError? denial)
    {
        attempt.SealCut(Readmitted(denial));
        if (denial is not null) state = Denied(denial);
        Cut(attempt);
    }

    // A failed object follows its last attempt's admission (the factory of that attempt broke, or it failed live): a denial is shown at
    // once, failed denied in place of its failure, with no value kept and its holders out of presence, and the failed factory is not run
    // for it; another allowed value retries the object, as a wake signal does; a source that failed changes nothing.
    private void FollowFailed(Attempt attempt, Verdict verdict)
    {
        if (verdict is Verdict.Allowed) { BeginAttempt(); return; }
        if (verdict is not Verdict.Denied denied) return;
        // Denied: no backoff, woken by the decision.
        retry?.Cancel(); retry?.Dispose(); retry = null;
        state = Denied(denied.Error);
        Cut(attempt);
    }

    // What a change of its admission rejects running calls with: forbidden/revoked for a denial, else unavailable.
    private ChordError Readmitted(ChordError? denial) =>
        denial is not null ? AdmissionInternal.Revoked(Label, denial) : new ChordError("unavailable", $"{Label}: its admission changed");

    // The state of a denial: failed denied, never retried by a timer; presence lists its holders no more, until its next allow.
    private RefState Denied(ChordError denial)
    {
        allowed = false;
        return RefState.Failed(new(FailureKind.Denied, denial));
    }

    // Nothing of attempt is published from here on: the references that follow it leave it now, taking what the object shows (their last
    // values while it loads, none for a denial), and every holder is told.
    private void Cut(Attempt attempt)
    {
        if (ReferenceEquals(swappingSource, attempt)) swappingSource = null;
        Notify();
    }

    // Closes a sealed attempt in a later turn (a close releases references, which is never done inside a batch), behind whatever was
    // closing before it: the next factory waits for all of it.
    private void CloseLater(Attempt attempt)
    {
        var before = previousClose;
        previousClose = Host.Dispatcher.RunAsync(async () =>
        {
            try { await before; } catch { }
            try { await attempt.CloseAsync(NativeCloseReason.End); }
            catch (Exception error) { Host.Report(new("close_failed", error, Label)); }
        });
    }
}
