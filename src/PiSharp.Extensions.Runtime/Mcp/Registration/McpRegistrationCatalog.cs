using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Registration;

namespace PiSharp.Extensions.Runtime.Mcp.Registration;

/// <summary>One host's ordered metadata catalog and owner-bound facades. The required publisher
/// is the integration seam for existing admitted registry/catalog publication, not a transport factory.
/// A failed or uncertain publication fences further use rather than replaying possibly committed effects.</summary>
public sealed class McpRegistrationCatalog
{
    private sealed record Entry(IExtensionRegistry Owner, McpRegisteredServer Server);
    private sealed record OwnerBinding(string Id, long Generation, string ExtensionPath);
    private readonly object gate = new();
    private readonly McpLoadedConfiguration configured;
    private readonly Action<IExtensionRegistry> assertActive;
    private readonly McpAdmittedRegistrationPublisher publish;
    private readonly Dictionary<IExtensionRegistry, OwnerBinding> owners = new(ReferenceEqualityComparer.Instance);
    private ImmutableArray<Entry> entries = [];
    private long revision;
    private bool publishing;
    private bool assertingOwner;
    private Exception? publicationFailure;

    public McpRegistrationCatalog(McpLoadedConfiguration configured, Action<IExtensionRegistry> assertActive,
        McpAdmittedRegistrationPublisher publish)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(assertActive);
        ArgumentNullException.ThrowIfNull(publish);
        if (configured.Servers.IsDefault || configured.Errors.IsDefault)
            throw new ArgumentException("Initialized configuration is required.", nameof(configured));
        if (assertActive.GetInvocationList().Length != 1 || publish.GetInvocationList().Length != 1)
            throw new ArgumentException("One admitted owner assertion and one owning publisher are required.");
        this.configured = configured;
        this.assertActive = assertActive;
        this.publish = publish;
    }

    public IMcpServerRegistrationFacade BindOwner(IExtensionRegistry owner, string extensionPath)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrWhiteSpace(extensionPath) || !Path.IsPathFullyQualified(extensionPath))
            throw new ArgumentException("An explicitly admitted absolute extension path is required.", nameof(extensionPath));
        lock (gate)
        {
            CheckOwner(owner);
            if (owners.TryGetValue(owner, out var bound))
            {
                if (!StringComparer.Ordinal.Equals(bound.ExtensionPath, extensionPath))
                    throw new ArgumentException("An owner cannot change its admitted extension path.", nameof(extensionPath));
            }
            else owners.Add(owner, new(owner.OwnerId, owner.OwnerGeneration, extensionPath));
            return new Facade(this, owner, extensionPath);
        }
    }

    private void CheckOwner(IExtensionRegistry owner)
    {
        CheckPublicationState();
        if (string.IsNullOrWhiteSpace(owner.OwnerId) || owner.OwnerGeneration < 1 ||
            owners.TryGetValue(owner, out var identity) &&
            (identity.Id != owner.OwnerId || identity.Generation != owner.OwnerGeneration))
            throw new InvalidOperationException("The captured MCP owner identity changed or is invalid.");
        assertingOwner = true;
        try { assertActive(owner); }
        finally { assertingOwner = false; }
        owner.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
    }

    private void CheckPublicationState()
    {
        if (publishing) throw new InvalidOperationException("MCP registration publication reentry is not admitted.");
        if (assertingOwner) throw new InvalidOperationException("MCP owner assertion reentry is not admitted.");
        if (publicationFailure is not null)
            throw new IOException("MCP registration publication previously failed; reconciliation by the host is required.", publicationFailure);
    }

    /// <summary>Owning host cleanup removes this exact captured scope's metadata, including after
    /// lifetime cancellation. A different scope with the same public strings cannot retire it.</summary>
    public void RetireOwner(IExtensionRegistry owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (gate)
        {
            CheckPublicationState();
            if (!owners.ContainsKey(owner)) return;
            var next = entries.Where(entry => !ReferenceEquals(entry.Owner, owner)).ToImmutableArray();
            if (next.Length != entries.Length) Commit(owner, next, retiring: true);
            owners.Remove(owner);
        }
    }

    /// <summary>Publish one validated initializer batch only after the actual native owner is active.
    /// Existing foreign entries are preserved; the owning host reconciles uncertain publisher failures.</summary>
    public void CommitInitialOwner(IExtensionRegistry owner, string extensionPath,
        ImmutableArray<McpRegisteredServer> staged)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (staged.IsDefault || staged.Length > 128) throw new ArgumentException("Finite initialized staged rows required.", nameof(staged));
        lock (gate)
        {
            CheckOwner(owner);
            if (!owners.TryGetValue(owner, out var binding) ||
                !StringComparer.Ordinal.Equals(binding.ExtensionPath, extensionPath) ||
                entries.Any(entry => ReferenceEquals(entry.Owner, owner)))
                throw new InvalidOperationException("Initializer commit requires this exact fresh bound owner/path.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var next = entries;
            foreach (var server in staged)
            {
                if (server is null || !StringComparer.Ordinal.Equals(server.ExtensionPath, extensionPath) ||
                    !names.Add(server.Name) || Find(server.Name) >= 0)
                    throw new InvalidOperationException("Initializer batch duplicates or replaces a foreign row.");
                var copy = CopyConfiguration(server.Name, server.Config.Raw);
                ThrowIfConflicting(next, server.Name);
                next = next.Add(new(owner, new(server.Name, copy, extensionPath)));
            }
            if (!staged.IsEmpty) Commit(owner, next); // All validation/allocation precedes one publication.
        }
    }
    private static McpServerConfiguration CopyConfiguration(string name, JsonData configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var validated = McpConfigurationReader.Validate(name, configuration.Value);
        return validated.Config ?? throw new ArgumentException(validated.Error, nameof(configuration));
    }

    private static ImmutableArray<McpRegisteredServer> Snapshot(ImmutableArray<Entry> values) =>
        values.Select(entry => entry.Server with
        {
            Config = CopyConfiguration(entry.Server.Name, entry.Server.Config.Raw)
        }).ToImmutableArray();

    private void Register(IExtensionRegistry owner, string path, string name, JsonData configuration)
    {
        lock (gate)
        {
            CheckOwner(owner);
            var copy = CopyConfiguration(name, configuration);
            var index = Find(name);
            if (index >= 0 && !ReferenceEquals(entries[index].Owner, owner))
                throw new InvalidOperationException($"MCP server \"{name}\" is already registered by extension \"{entries[index].Server.ExtensionPath}\".");
            ThrowIfConflicting(entries, name);
            var row = new Entry(owner, new McpRegisteredServer(name, copy, path));
            var next = index < 0 ? entries.Add(row) : entries.SetItem(index, row);
            Commit(owner, next);
        }
    }

    private void Unregister(IExtensionRegistry owner, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (gate)
        {
            CheckOwner(owner);
            var index = Find(name);
            // Original unregister is harmless for absent names and another extension's registrations.
            if (index < 0 || !ReferenceEquals(entries[index].Owner, owner)) return;
            Commit(owner, entries.RemoveAt(index));
        }
    }

    private ImmutableArray<McpRegisteredServer> List(IExtensionRegistry owner)
    {
        lock (gate) { CheckOwner(owner); return Snapshot(entries); }
    }

    // Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts registerMcpServer.
    // Names that differ only in `-` and `_` would share a namespace.
    private static void ThrowIfConflicting(ImmutableArray<Entry> rows, string name)
    {
        foreach (var row in rows)
            if (!StringComparer.Ordinal.Equals(row.Server.Name, name) && McpCatalogPlanner.Namespace(row.Server.Name) == McpCatalogPlanner.Namespace(name))
                throw new InvalidOperationException($"MCP server \"{name}\" conflicts with registered server \"{row.Server.Name}\"");
    }

    private int Find(string name)
    {
        for (var index = 0; index < entries.Length; index++)
            if (StringComparer.Ordinal.Equals(entries[index].Server.Name, name)) return index;
        return -1;
    }

    private void Commit(IExtensionRegistry owner, ImmutableArray<Entry> next, bool retiring = false)
    {
        var previous = Snapshot(entries);
        var current = Snapshot(next);
        var catalog = McpCatalogPlanner.ComposeServers(configured, current);
        var nextRevision = checked(revision + 1);
        var change = new McpRegistrationPublication(owner, nextRevision, previous, current, catalog);
        // The owner assertion may have host code; assert again before entering the publication fence.
        if (retiring) CheckPublicationState();
        else CheckOwner(owner);
        publishing = true;
        try
        {
            var receipt = publish(change);
            if (receipt is null || !ReferenceEquals(receipt.Owner, owner) ||
                receipt.Revision != nextRevision || !receipt.Published)
                throw new InvalidOperationException("MCP publication returned no matching owner/revision commit receipt.");
            entries = next;
            revision = nextRevision;
        }
        catch (Exception error) { publicationFailure = error; throw; }
        finally { publishing = false; }
    }

    private sealed class Facade(McpRegistrationCatalog catalog, IExtensionRegistry owner, string path)
        : IMcpServerRegistrationFacade
    {
        public string OwnerId => owner.OwnerId;
        public long OwnerGeneration => owner.OwnerGeneration;
        public void RegisterMcpServer(string name, JsonData configuration) => catalog.Register(owner, path, name, configuration);
        public void UnregisterMcpServer(string name) => catalog.Unregister(owner, name);
        public ImmutableArray<McpRegisteredServer> GetMcpServers() => catalog.List(owner);
    }
}
