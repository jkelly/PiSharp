using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime;

public sealed partial class ExtensionRegistry
{
    /// <summary>The owner's current tool registrations (registration id and descriptor) in registration order, for a host that
    /// publishes an owner activated after its session bound.</summary>
    public ImmutableArray<(string RegistrationId, ExtensionToolDescriptor Descriptor)> CaptureOwnedTools(RegistrationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
        {
            EnsureScope(scope, "capture-owned-tools");
            return [.. scope.Staged.Entries.Where(entry => entry.Kind == RegistrationKind.Tool)
                .Select(entry => (entry.RegistrationId, (ExtensionToolDescriptor)entry.Descriptor))];
        }
    }

    /// <summary>Stages an exact owned tool subset replacement. Preview has future entries and cannot
    /// dispatch until committed. No callbacks, publication or current registration changes occur here.</summary>
    public ToolCatalogReplacementPlan PrepareToolCatalogReplacement(RegistrationScope scope,
        ImmutableArray<string> previousIds, ImmutableArray<ExtensionToolDescriptor> descriptors,
        ExtensionRegistrySnapshot expectedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(expectedSnapshot);
        const string operation = "prepare-tool-catalog";
        lock (gate)
        {
            EnsureScope(scope, operation);
            if (!ReferenceEquals(scope.Registry, this) || !ReferenceEquals(expectedSnapshot.RegistryIdentity, identity) ||
                !ReferenceEquals(expectedSnapshot, snapshot) && !options.FollowCurrentSnapshot)
                throw Failure(ExtensionRegistrationFailure.StaleSnapshot, scope.OwnerId, operation);
            // A registry that follows its current snapshot prepares over the current registrations (its commit rebases as well).
            expectedSnapshot = snapshot;
            if (previousIds.IsDefault || descriptors.IsDefault || previousIds.Length > options.MaximumRegistrationsPerOwner ||
                descriptors.Length > options.MaximumRegistrationsPerOwner)
                throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var previous = ImmutableArray.CreateBuilder<RegistrationEntry>(previousIds.Length);
            foreach (var id in previousIds)
            {
                if (id is null || !ids.Add(id)) throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
                var entry = scope.Staged.Entries.FirstOrDefault(row => row.RegistrationId == id && row.Kind == RegistrationKind.Tool);
                if (entry is null) throw Failure(ExtensionRegistrationFailure.StaleSnapshot, scope.OwnerId, operation);
                previous.Add(entry);
            }
            var old = previous.MoveToImmutable();
            var staged = new StagedRegistrationSet();
            foreach (var entry in scope.Staged.Entries) if (!old.Contains(entry)) staged.Add(entry);
            var added = ImmutableArray.CreateBuilder<RegistrationEntry>(descriptors.Length);
            var names = new HashSet<string>(StringComparer.Ordinal);
            long addedCharacters = 0;
            foreach (var descriptor in descriptors)
            {
                if (descriptor is null || !ValidNames(descriptor.RegistrationId, descriptor.Name) ||
                    !RegistrationPolicy.Description(descriptor.Description, options) || descriptor.ExecuteAsync is null ||
                    descriptor.PrepareInitialArgumentsAsync?.GetInvocationList().Length > 1 ||
                    descriptor.PrepareLoadout?.GetInvocationList().Length > 1 || !Enum.IsDefined(descriptor.Exposure) ||
                    descriptor.Namespace is { } grouping && (!RegistrationPolicy.Description(grouping.Name, options) ||
                        grouping.Description is not null && !RegistrationPolicy.Description(grouping.Description, options)) ||
                    !RegistrationPolicy.Json(descriptor.Parameters, options, requireObject: true))
                    throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, scope.OwnerId, operation);
                if (staged.ContainsId(descriptor.RegistrationId))
                    throw Failure(ExtensionRegistrationFailure.DuplicateRegistrationId, scope.OwnerId, operation);
                if (options.ReservedToolNames.Contains(descriptor.Name, StringComparer.Ordinal))
                    throw Failure(ExtensionRegistrationFailure.ReservedName, scope.OwnerId, operation);
                if (!names.Add(descriptor.Name) || ownerOrder.Any(owner => owner.Staged.Entries.Any(entry =>
                    entry.Kind == RegistrationKind.Tool && entry.Name == descriptor.Name && !old.Contains(entry))))
                    throw Failure(ExtensionRegistrationFailure.DuplicateName, scope.OwnerId, operation);
                var characters = checked((long)descriptor.RegistrationId.Length + descriptor.Name.Length + descriptor.Description.Length +
                    descriptor.Parameters.ToString().Length + (descriptor.Namespace?.Name.Length ?? 0) + (descriptor.Namespace?.Description?.Length ?? 0));
                if (characters > options.MaximumMetadataCharacters || characters > int.MaxValue)
                    throw Failure(ExtensionRegistrationFailure.LimitExceeded, scope.OwnerId, operation);
                addedCharacters = checked(addedCharacters + characters);
                var entry = new RegistrationEntry { OwnerId = scope.OwnerId, OwnerGeneration = scope.OwnerGeneration,
                    RegistrationId = descriptor.RegistrationId, Name = descriptor.Name, Kind = RegistrationKind.Tool,
                    Descriptor = descriptor, MetadataCharacters = (int)characters };
                staged.Add(entry); added.Add(entry);
            }
            var futureEntries = ownerOrder.Where(owner => owner.State == RegistrationScopeState.Active)
                .SelectMany(owner => ReferenceEquals(owner, scope) ? staged.Entries : owner.Staged.Entries).ToImmutableArray();
            var preview = new ExtensionRegistrySnapshot(identity, checked(revision + 1), futureEntries, CommandInvocationNames(futureEntries));
            var plan = new ToolCatalogReplacementPlan(this, scope, expectedSnapshot, old, added.MoveToImmutable(),
                staged, preview, addedCharacters);
            plan.ValidateCharges();
            return plan;
        }
    }

    /// <summary>Opaque, single-attempt transaction. Host must retain its session/generation reservation
    /// across preparation, durable catalog acknowledgment and Commit. Failure gives no published receipt.</summary>
    public sealed class ToolCatalogReplacementPlan
    {
        private readonly ExtensionRegistry registry;
        private readonly RegistrationScope scope;
        private readonly ExtensionRegistrySnapshot expected;
        private readonly ImmutableArray<RegistrationEntry> previous, added;
        private readonly StagedRegistrationSet staged;
        private readonly long addedCharacters;
        private readonly bool[] release;
        private bool attempted;
        private ExtensionRegistrySnapshot? published;
        public ExtensionRegistrySnapshot PreviewSnapshot { get; }
        public ExtensionRegistrySnapshot? PublishedSnapshot => Volatile.Read(ref published);
        internal ToolCatalogReplacementPlan(ExtensionRegistry registry, RegistrationScope scope,
            ExtensionRegistrySnapshot expected, ImmutableArray<RegistrationEntry> previous,
            ImmutableArray<RegistrationEntry> added, StagedRegistrationSet staged,
            ExtensionRegistrySnapshot preview, long addedCharacters)
        {
            this.registry = registry; this.scope = scope; this.expected = expected; this.previous = previous;
            this.added = added; this.staged = staged; PreviewSnapshot = preview; this.addedCharacters = addedCharacters;
            release = new bool[previous.Length];
        }
        internal (int Global, int Owner, long Characters) ValidateCharges()
        {
            var count = 0; long characters = 0;
            for (var index = 0; index < previous.Length; index++)
            {
                var entry = previous[index]; release[index] = entry.Charged && entry.Leases == 0;
                if (release[index]) { count++; characters = checked(characters + entry.MetadataCharacters); }
            }
            var global = checked(registry.chargedRegistrations - count + added.Length);
            var owner = checked(scope.ChargedRegistrations - count + added.Length);
            var metadata = checked(registry.metadataCharacters - characters + addedCharacters);
            if (global > registry.options.MaximumRegistrations || owner > registry.options.MaximumRegistrationsPerOwner ||
                metadata > registry.options.MaximumMetadataCharacters)
                throw Failure(ExtensionRegistrationFailure.LimitExceeded, scope.OwnerId, "commit-tool-catalog");
            return (global, owner, metadata);
        }
        public ExtensionRegistrySnapshot Commit()
        {
            lock (registry.gate)
            {
                if (published is not null) return published;
                if (attempted) throw new InvalidOperationException("Tool catalog replacement has already failed an attempt.");
                attempted = true;
                registry.EnsureScope(scope, "commit-tool-catalog");
                if (previous.Any(entry => !scope.Staged.Contains(entry)))
                    throw Failure(ExtensionRegistrationFailure.StaleSnapshot, scope.OwnerId, "commit-tool-catalog");
                if (!ReferenceEquals(registry.snapshot, expected) || registry.revision != expected.Revision)
                {
                    // A registry that follows its current snapshot (Pi's live runner) keeps other registrations made meanwhile: the
                    // replacement is rebased onto the owner's current entries when its tool names are still free.
                    if (!registry.options.FollowCurrentSnapshot) throw Failure(ExtensionRegistrationFailure.StaleSnapshot, scope.OwnerId, "commit-tool-catalog");
                    return CommitRebased();
                }
                var totals = ValidateCharges();
                // All validation, arithmetic and allocations precede the first registry mutation.
                for (var index = 0; index < previous.Length; index++)
                { previous[index].Registered = false; if (release[index]) previous[index].Charged = false; }
                scope.Staged = staged;
                registry.chargedRegistrations = totals.Global;
                scope.ChargedRegistrations = totals.Owner;
                registry.metadataCharacters = totals.Characters;
                registry.revision = PreviewSnapshot.Revision;
                // One publication: use the completely allocated snapshot, rather than rebuilding it.
                Volatile.Write(ref registry.snapshot, PreviewSnapshot);
                Volatile.Write(ref published, PreviewSnapshot);
                return PreviewSnapshot;
            }
        }

        /// <summary>Commit over a registry that changed since preparation (called under the registry gate).</summary>
        private ExtensionRegistrySnapshot CommitRebased()
        {
            var rebased = new StagedRegistrationSet();
            foreach (var entry in scope.Staged.Entries) if (!previous.Contains(entry)) rebased.Add(entry);
            foreach (var entry in added)
            {
                if (rebased.ContainsId(entry.RegistrationId))
                    throw Failure(ExtensionRegistrationFailure.DuplicateRegistrationId, scope.OwnerId, "commit-tool-catalog");
                if (registry.ownerOrder.Any(owner => !ReferenceEquals(owner, scope) && owner.Staged.ContainsName(RegistrationKind.Tool, entry.Name)) ||
                    rebased.ContainsName(RegistrationKind.Tool, entry.Name))
                    throw Failure(ExtensionRegistrationFailure.DuplicateName, scope.OwnerId, "commit-tool-catalog");
                rebased.Add(entry);
            }
            var totals = ValidateCharges();
            for (var index = 0; index < previous.Length; index++)
            { previous[index].Registered = false; if (release[index]) previous[index].Charged = false; }
            scope.Staged = rebased;
            registry.chargedRegistrations = totals.Global;
            scope.ChargedRegistrations = totals.Owner;
            registry.metadataCharacters = totals.Characters;
            registry.Publish();
            var current = registry.snapshot;
            Volatile.Write(ref published, current);
            return current;
        }
    }
}
