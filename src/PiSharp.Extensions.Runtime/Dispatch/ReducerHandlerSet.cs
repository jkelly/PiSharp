using System.Collections.Immutable;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions.Runtime.Dispatch;

public sealed record ExtensionReducerRegistrationInfo(string OwnerId, long OwnerGeneration, string RegistrationId,
    long OwnerDispatchOrder);

/// <summary>Removal affects future captures; this immutable snapshot keeps its original ordered callbacks.</summary>
public sealed class ExtensionReducerSnapshot<TEvent, TPatch> where TPatch : class
{
    public long Revision { get; }
    public ImmutableArray<ExtensionReducerRegistrationInfo> Registrations { get; }
    internal object SetIdentity { get; }
    internal ImmutableArray<ReducerRegistration<TEvent, TPatch>> Entries { get; }
    internal ExtensionReducerSnapshot(object identity, long revision,
        ImmutableArray<ReducerRegistration<TEvent, TPatch>> entries)
    {
        SetIdentity = identity;
        Revision = revision;
        Entries = entries;
        Registrations = entries.Select(row => new ExtensionReducerRegistrationInfo(
            row.OwnerId, row.OwnerGeneration, row.RegistrationId, row.OwnerDispatchOrder)).ToImmutableArray();
    }
}

/// <summary>Standalone experimental handler storage, not transactional ExtensionRegistry integration.</summary>
public sealed class ExtensionReducerHandlerSet<TEvent, TPatch> where TPatch : class
{
    private readonly object sync = new();
    private readonly int maximumHandlers;
    private readonly int maximumOwnerGroups;
    private readonly Dictionary<(string OwnerId, long Generation), (long Order, long Encounter)> owners = [];
    private ImmutableArray<ReducerRegistration<TEvent, TPatch>> entries = [];
    private long revision, registrationSequence;
    public ExtensionReducerHandlerSet(int maximumHandlers = 256, int maximumOwnerGroups = 1024)
    {
        if (maximumHandlers <= 0 || maximumHandlers > 4096) throw new ArgumentOutOfRangeException(nameof(maximumHandlers));
        if (maximumOwnerGroups <= 0 || maximumOwnerGroups > 4096) throw new ArgumentOutOfRangeException(nameof(maximumOwnerGroups));
        this.maximumHandlers = maximumHandlers;
        this.maximumOwnerGroups = maximumOwnerGroups;
    }
    public IExtensionRegistration Register(string ownerId, long ownerGeneration, string registrationId,
        ExtensionReducerCallback<TEvent, TPatch> callback, CancellationToken extensionLifetimeCancellationToken = default,
        long? ownerDispatchOrder = null)
    {
        if (!RegistrationPolicy.Identifier(ownerId, 128) || !RegistrationPolicy.Identifier(registrationId, 128) || ownerGeneration <= 0)
            throw new ArgumentException("Reducer registration requires bounded identifiers and a positive generation.");
        if (ownerDispatchOrder is < 0) throw new ArgumentOutOfRangeException(nameof(ownerDispatchOrder));
        ArgumentNullException.ThrowIfNull(callback);
        lock (sync)
        {
            if (entries.Length >= maximumHandlers) throw new InvalidOperationException("Reducer handler limit reached.");
            if (entries.Any(row => row.OwnerId == ownerId && row.OwnerGeneration == ownerGeneration && row.RegistrationId == registrationId))
                throw new InvalidOperationException("Duplicate reducer registration identity.");
            var ownerKey = (ownerId, ownerGeneration);
            if (!owners.TryGetValue(ownerKey, out var owner))
            {
                if (owners.Count >= maximumOwnerGroups) throw new InvalidOperationException("Reducer cumulative owner-group limit reached.");
                owner = (ownerDispatchOrder ?? owners.Count, owners.Count);
                owners.Add(ownerKey, owner);
            }
            else if (ownerDispatchOrder is { } explicitOrder && explicitOrder != owner.Order)
                throw new ArgumentException("An owner's dispatch order cannot change within this handler set.");
            var entry = new ReducerRegistration<TEvent, TPatch>(ownerId, ownerGeneration, registrationId,
                callback, extensionLifetimeCancellationToken, owner.Order, owner.Encounter, registrationSequence++);
            entries = entries.Add(entry).OrderBy(row => row.OwnerDispatchOrder).ThenBy(row => row.OwnerEncounter)
                .ThenBy(row => row.RegistrationSequence).ToImmutableArray();
            revision++;
            return new Handle(this, entry);
        }
    }
    public ExtensionReducerSnapshot<TEvent, TPatch> CaptureSnapshot()
    {
        lock (sync) return new(this, revision, entries);
    }
    internal void Validate(ExtensionReducerSnapshot<TEvent, TPatch> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(snapshot.SetIdentity, this)) throw new ArgumentException("Snapshot belongs to another handler set.");
    }
    private void Remove(ReducerRegistration<TEvent, TPatch> entry)
    {
        lock (sync)
        {
            var index = entries.IndexOf(entry);
            if (index >= 0) { entries = entries.RemoveAt(index); revision++; }
        }
    }
    private sealed class Handle(ExtensionReducerHandlerSet<TEvent, TPatch> set,
        ReducerRegistration<TEvent, TPatch> entry) : IExtensionRegistration
    {
        private int disposed;
        public string OwnerId => entry.OwnerId;
        public long OwnerGeneration => entry.OwnerGeneration;
        public string RegistrationId => entry.RegistrationId;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) set.Remove(entry); }
    }
}

internal sealed class ReducerRegistration<TEvent, TPatch>(string ownerId, long ownerGeneration,
    string registrationId, ExtensionReducerCallback<TEvent, TPatch> callback,
    CancellationToken lifetimeCancellationToken, long ownerDispatchOrder, long ownerEncounter,
    long registrationSequence) where TPatch : class
{
    internal string OwnerId { get; } = ownerId;
    internal long OwnerGeneration { get; } = ownerGeneration;
    internal string RegistrationId { get; } = registrationId;
    internal ExtensionReducerCallback<TEvent, TPatch> Callback { get; } = callback;
    internal CancellationToken LifetimeCancellationToken { get; } = lifetimeCancellationToken;
    internal long OwnerDispatchOrder { get; } = ownerDispatchOrder;
    internal long OwnerEncounter { get; } = ownerEncounter;
    internal long RegistrationSequence { get; } = registrationSequence;
    internal ExtensionEventDiagnostic Diagnostic(string eventName, ExtensionEventFailure failure) =>
        new(eventName, OwnerId, OwnerGeneration, RegistrationId, failure);
}

internal sealed class ReducerContext(string ownerId, long ownerGeneration, CancellationToken operation,
    CancellationToken session, CancellationToken lifetime) : IExtensionContext
{
    public string OwnerId { get; } = ownerId;
    public long OwnerGeneration { get; } = ownerGeneration;
    public CancellationToken OperationCancellationToken { get; } = operation;
    public CancellationToken SessionCancellationToken { get; } = session;
    public CancellationToken ExtensionLifetimeCancellationToken { get; } = lifetime;
}
