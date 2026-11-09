// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): cluster.ts / host.ts.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

/// <summary>The legacy kernel's two-phase reload boundary. It never substitutes a protocol 2 host.</summary>
public interface ILegacyReloadHost
{
    string Name { get; }
    string Kind { get; }
    int CodeVersion { get; }
    bool Reloading { get; }
    int HighestRetiredCodeVersion { get; }
    event Action<int>? Retired;
    Task<ILegacyPreparedReload> BeginReloadAsync(IReadOnlyList<string> modules, LegacyReloadOptions options,
        int? codeVersion = null, CancellationToken cancellationToken = default);
    void EndReload();
    void AllowRetirement(int codeVersion);
}

public interface ILegacyPreparedReload
{
    Task WaitForRetirementAsync(CancellationToken cancellationToken = default);
    Task PrepareAsync(CancellationToken cancellationToken = default);
    LegacyReloadReport Summary();
    /// <summary>Installs locally, runs the callback, then starts work that may make new-version calls.</summary>
    LegacyReloadReport Commit(Action? beforeNewWork = null);
    Task RollbackAsync();
}

/// <summary>Installed module ownership outlives the version in which an unchanged facet first appeared.</summary>
public interface ILegacyFacetLifetimeHost
{
    bool IsFacetInstalled(LegacyFacet facet);
}

public sealed record LegacyRoute
{
    public required Func<JsonNode?, string, Task<bool>> Authorize { get; init; }
    public required Func<string, Task<ILegacyLink>> Resolve { get; init; }
    public required Func<string, JsonNode?> Principal { get; init; }
}

public sealed record LegacyAcceptOptions
{
    public JsonNode? Principal { get; init; }
    public string Kind { get; init; } = "client";
    public string? Name { get; init; }
    public LegacyConnectionOptions? LinkOptions { get; init; }
    public Func<JsonNode?, JsonNode?>? Restamp { get; init; }
}

/// <summary>One admitted host or client. A host is addressable only after catch-up has committed.</summary>
public sealed class LegacyClusterPeer
{
    private readonly object gate = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal LegacyClusterPeer(LegacyConnection connection, string role, string kind, string? name, Func<JsonNode?, JsonNode?>? restamp = null)
    { Connection = connection; Role = role; Kind = kind; Name = name; Restamp = restamp; }
    public LegacyConnection Connection { get; }
    public string Role { get; }
    public string Kind { get; }
    public string? Name { get; }
    public bool Synced { get; private set; }
    public int? CodeVersion { get; internal set; }
    public HashSet<int> FencedCodeVersions { get; } = [];
    internal Func<JsonNode?, JsonNode?>? Restamp { get; }
    public Task WhenSyncedAsync(CancellationToken cancellationToken = default) => ready.Task.WaitAsync(cancellationToken);
    internal void MarkSynced(int codeVersion)
    { lock (gate) { CodeVersion = codeVersion; Synced = true; ready.TrySetResult(); } }
    internal void End(string reason)
    { lock (gate) { Synced = false; ready.TrySetException(new InvalidOperationException("Host link closed: " + reason)); } }
}
