// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/documents.ts (in part)
//
// An object's documents (§9, P2, slice 6a §1.2, §10): its slots across attempts, its line (its transactions and line jobs run one at a
// time), the seal fence over the transaction holding the line, the factory's pins between attempts, the activation's loads and the
// residency of keyed documents. Area cv2-documents-core ported the engine around the slots: check, hydrate, load, onLine, fence, the
// liveness and admission checks, seal, settled, end and released. Three members make another area's classes and land with them (area
// cv2-documents-slots): declare (slot.ts's SingletonSlot, keyed-slot.ts's KeyedSlot, persistence.ts's Persistence and the handles'
// Singleton and Keyed), tx (transaction.ts's Transaction) and #persistence. Mappings:
// - Upstream's slot union SingletonSlot | KeyedSlot is IDeclaredSlot, what the engine calls of either (its persisted !== undefined is
//   IsPersisted). The transaction holding the line is an ILineHolder (its token and its cut); tx takes the line for it through Hold and
//   gives it up through Unhold, upstream's two assignments of #holder. #add is Add, internal for declare and the tests.
// - DocumentsOwner is IDocumentsOwner, its store { store, owner } a DocumentsStore. Promises are Tasks; load returns null for upstream's
//   undefined (nothing persisted: an activation takes no extra turn). `released` is ChordError released/ended.
// - The residency's clock and timers are a TimeProvider (the system's unless one is given), the C# form of upstream's fake timers.
// - Threads: an object's documents belong to its host, which serializes the calls that reach them, as the state layer's do; the line, the
//   seal tokens and the state flag are safe on any thread (an attempt may end on another one), and the residency's timer fires on the thread
//   pool and only queues a sweep on the line.
using PiSharp.Chord.Data;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Documents;

/// <summary>The host's store and the object's owner name in it (its type, R2): upstream's <c>DocumentsOwner.store</c>.</summary>
/// <param name="Store">The host's store.</param>
/// <param name="Owner">The object's type, the owner of its documents in the store.</param>
internal sealed record DocumentsStore(ChordStore Store, string Owner);

/// <summary>Who owns an object's documents (upstream <c>DocumentsOwner</c>): the object's name, batch and context, its store address, its
/// live failure.</summary>
internal interface IDocumentsOwner
{
    /// <summary>The owner's name in errors.</summary>
    string Label { get; }

    Batch Batch { get; }

    /// <summary>The object's context: shared cold loads run under it (rule 11).</summary>
    ChordContext Context { get; }

    /// <summary>The host's store and the object's owner name in it, when it has one.</summary>
    DocumentsStore? Store { get; }

    /// <summary>A document the code cannot read fails the live object <c>broken</c> (slice 6a §1.4); ignored when it is not live.</summary>
    void Fail(ChordError error);
}

/// <summary>A declared slot as an object's documents hold it (upstream's <c>SingletonSlot | KeyedSlot</c>): one per declared name, kept
/// across attempts.</summary>
internal interface IDeclaredSlot
{
    /// <summary>The definition its values are of.</summary>
    SlotDefinition Definition { get; }

    /// <summary>Whether its documents are persisted (upstream <c>persisted !== undefined</c>).</summary>
    bool IsPersisted { get; }

    /// <summary>Takes an attempt's declaration <paramref name="definition"/> (the check accepted it): <c>initial()</c>, migrations.</summary>
    void Hydrate(StateDefinition definition, ChordContext context);

    /// <summary>Loads what the activation needs (the caller holds the line): a persisted singleton, a keyed slot's pinned keys.</summary>
    Task LoadDeclaredAsync(ChordContext context);

    /// <summary>Its object ended or no attempt declares it any more: its documents go and its watches end.</summary>
    void Drop();
}

/// <summary>What holds an object's line under a seal token and is cut at its seal (upstream: the <c>Transaction</c> holding the line).</summary>
internal interface ILineHolder
{
    /// <summary>The token it runs under.</summary>
    SealToken Token { get; }

    /// <summary>Its token was sealed while it held the line: it discards everything and releases the line.</summary>
    void Cut();
}

/// <summary>
/// The documents of one object, from its first activation to its end (upstream <c>Documents</c>): one slot per declared name, kept across
/// attempts, and the line. Each attempt declares its handles under its own seal token; the object's own transactions (job outcomes) run
/// under <see cref="Own"/>, sealed only by the object's end.
/// </summary>
internal sealed class Documents
{
    private readonly IDocumentsOwner owner;
    private readonly OrderedDictionary<string, IDeclaredSlot> slots = new(StringComparer.Ordinal);

    // The transaction holding the line, until it settles or its token is sealed.
    private ILineHolder? holder;

    // Sealed: the object is ending; its documents stay readable, its transactions throw released.
    private volatile DocumentsState state = DocumentsState.Open;

    // The pins the last factory that returned declared (at(key)): held until the next one returns (rule 6).
    private IReadOnlyList<Action> pins = [];

    /// <summary>The documents of <paramref name="owner"/>'s object; <paramref name="time"/> times the residency (by default the system's
    /// clock and timers).</summary>
    public Documents(IDocumentsOwner owner, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Label = owner.Label;
        Batch = owner.Batch;
        Context = owner.Context;
        this.owner = owner;
        // A sweep's only rejection is OnLineAsync's released once the object ended (a sweep itself drops trackers in memory and does not
        // throw): nothing to report.
        Residency = new Residency(sweep => _ = StoreInternal.Settled(OnLineAsync(() =>
        {
            sweep();
            return Task.CompletedTask;
        })), time);
    }

    private enum DocumentsState
    {
        Open,
        Sealed,
        Ended,
    }

    /// <summary>The owner's name in errors.</summary>
    public string Label { get; }

    public Batch Batch { get; }

    /// <summary>The object's context.</summary>
    public ChordContext Context { get; }

    public Line Line { get; } = new();

    /// <summary>The idle persisted keyed documents and their eviction.</summary>
    public Residency Residency { get; }

    /// <summary>The token of the object's own transactions: sealed with the object (<see cref="Seal"/>).</summary>
    public SealToken Own { get; } = new();

    /// <summary>Checks a declaration of <paramref name="definition"/> as declare would, changing nothing (a reload's staging: a recording
    /// factory declares against the documents the object has).</summary>
    public void Check(StateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (slots.TryGetValue(definition.Name, out var slot)) slot.Definition.Check(definition);
    }

    /// <summary>
    /// Takes an attempt's declarations once its factory returned: its at(key) <paramref name="pins"/> replace the previous factory's, the
    /// slots it no longer declares are dropped (their watches end; persisted ones load again when declared again), every declared one
    /// hydrated, in declaration order (<c>initial()</c>, migrations; a throw fails the activation).
    /// </summary>
    public void Hydrate(IReadOnlyDictionary<string, StateDefinition> declared, IReadOnlyList<Action> pins, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(pins);
        var previous = this.pins;
        this.pins = pins;
        foreach (var release in previous) release();
        // A dropped persisted slot goes whole, its loaded documents too: observably the same as keeping them, since every committed write
        // is in the store and a later declaration reloads from there.
        foreach (var (name, slot) in slots.ToList())
        {
            if (declared.ContainsKey(name)) continue;
            slots.Remove(name);
            slot.Drop();
        }
        foreach (var (name, definition) in declared) slots[name].Hydrate(definition, context);
    }

    /// <summary>
    /// Loads, in one line job, every persisted singleton not loaded yet and every pinned key not loaded since it was pinned (an attempt's
    /// activation, before it goes live, rule 6); null when nothing is persisted. A failure fails the activation.
    /// </summary>
    public Task? Load(ChordContext context)
    {
        var persisted = slots.Values.Where(slot => slot.IsPersisted).ToList();
        // In memory only: nothing to wait for (an activation without persisted documents takes no extra turn).
        if (persisted.Count == 0) return null;
        return OnLineAsync(async () =>
        {
            foreach (var slot in persisted) await slot.LoadDeclaredAsync(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Runs <paramref name="job"/> on the line (a cold load, a page of keys, an eviction sweep); refused <c>released</c> once the object is
    /// ending or <paramref name="token"/> (the handle's that asks, when the job is one handle's) is sealed, a job queued before either
    /// included (anything taking the line after the seal is refused, rule 4).
    /// </summary>
    public async Task<T> OnLineAsync<T>(Func<Task<T>> job, SealToken? token = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (state != DocumentsState.Open || token?.Sealed == true) throw Released();
        var release = await Line.AcquireAsync().ConfigureAwait(false);
        try
        {
            if (state != DocumentsState.Open || token?.Sealed == true) throw Released();
            return await job().ConfigureAwait(false);
        }
        finally
        {
            release();
        }
    }

    /// <inheritdoc cref="OnLineAsync{T}(Func{Task{T}}, SealToken?)"/>
    public Task OnLineAsync(Func<Task> job, SealToken? token = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        return OnLineAsync(async () =>
        {
            await job().ConfigureAwait(false);
            return true;
        }, token);
    }

    /// <summary>Seals <paramref name="token"/> (the seal fence): its transactions are refused from now on, and the one holding the line is
    /// discarded and the line released.</summary>
    public void Fence(SealToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        token.Seal();
        if (ReferenceEquals(holder?.Token, token)) Cut();
    }

    /// <summary>Throws <c>released</c> once the object ended.</summary>
    public void AssertLive()
    {
        if (state == DocumentsState.Ended) throw Released();
    }

    /// <summary>Throws <c>released</c> once <paramref name="token"/> is sealed or the object is ending: no new work takes the line.</summary>
    public void AssertAdmitted(SealToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Sealed || state != DocumentsState.Open) throw Released();
    }

    /// <summary>The live object fails <c>broken</c> with <paramref name="error"/> (a document the code cannot read); ignored when it is not
    /// live.</summary>
    public void Fail(ChordError error) => owner.Fail(error);

    /// <summary>The object is ending: every transaction is refused, the one holding the line discarded; documents stay readable.</summary>
    public void Seal()
    {
        if (state != DocumentsState.Open) return;
        state = DocumentsState.Sealed;
        Own.Seal();
        Cut();
    }

    /// <summary>
    /// Completes once the line is free. After <see cref="Seal"/> only an admitted step can hold it (every other holder was cut): the object's
    /// teardown waits here, so the step adopts into documents that still exist (slice 6a §1.3).
    /// </summary>
    public async Task SettledAsync()
    {
        var release = await Line.AcquireAsync().ConfigureAwait(false);
        release();
    }

    /// <summary>Drops every document and pin; watches end <c>ended</c>, and every later use throws <c>released</c>.</summary>
    public void End()
    {
        state = DocumentsState.Ended;
        Residency.End();
        foreach (var slot in slots.Values.ToList()) slot.Drop();
        slots.Clear();
        pins = [];
    }

    /// <summary>The <c>released</c> error of this object's documents.</summary>
    public ChordError Released() => new("released", $"{Label}: ended", "ended");

    /// <summary>Adds <paramref name="slot"/> under its definition's name (upstream <c>#add</c>): declare's first declaration of a name.</summary>
    internal TSlot Add<TSlot>(TSlot slot)
        where TSlot : IDeclaredSlot
    {
        ArgumentNullException.ThrowIfNull(slot);
        slots[slot.Definition.Current.Name] = slot;
        return slot;
    }

    /// <summary><paramref name="transaction"/> holds the line from now on (tx, once admitted): the seal fence cuts it.</summary>
    internal void Hold(ILineHolder transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        holder = transaction;
    }

    /// <summary><paramref name="transaction"/> settled (tx's end): it no longer holds the line, unless a cut already let it go.</summary>
    internal void Unhold(ILineHolder transaction)
    {
        if (ReferenceEquals(holder, transaction)) holder = null;
    }

    /// <summary>Discards the transaction holding the line and releases the line.</summary>
    private void Cut()
    {
        var cut = holder;
        holder = null;
        cut?.Cut();
    }
}
