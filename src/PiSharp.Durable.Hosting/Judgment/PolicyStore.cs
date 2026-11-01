// PiSharp-only (decision 0010): judgment policies in the catalog. Versions are immutable; the active version is a separately revisioned
// pointer, so activating another version never changes what an admitted run already froze.
using Microsoft.Data.Sqlite;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>One immutable policy version.</summary>
/// <param name="Hash">SHA-256 of the normalized spec's canonical JSON.</param>
public sealed record PolicyVersion(string PolicyId, int Version, JsonObjectValue Spec, string Hash, long CreatedAt)
{
    /// <summary>The version as JSON.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object([new("policyId", PolicyId), new("version", Version), new("hash", Hash), new("createdAt", CreatedAt),
        new("spec", Spec)]);
}

/// <summary>A policy: its active version pointer and the newest version's title.</summary>
public sealed record PolicySummary(string Id, int? ActiveVersion, long Revision, int LatestVersion, string Title, string Kind, long CreatedAt, long UpdatedAt)
{
    /// <summary>The summary as JSON.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object([new("id", Id), new("activeVersion", ActiveVersion is { } active ? active : JsonValue.Null),
        new("revision", Revision), new("latestVersion", LatestVersion), new("title", Title), new("kind", Kind), new("createdAt", CreatedAt),
        new("updatedAt", UpdatedAt)]);
}

/// <summary>Publishes, activates and reads judgment policies.</summary>
public sealed class PolicyStore
{
    private readonly SessionCatalog catalog;
    private readonly IDurableModels models;
    private readonly TimeProvider time;

    internal PolicyStore(SessionCatalog catalog, IDurableModels models, TimeProvider time)
    {
        this.catalog = catalog;
        this.models = models;
        this.time = time;
    }

    /// <summary>
    /// Publishes a version of <paramref name="policyId"/>. The first version of a new policy becomes active (revision 1); later versions
    /// wait for <see cref="Activate"/>. A spec identical to the newest version returns that version. With an
    /// <paramref name="idempotencyKey"/>, a repeat returns the first result and a different spec under the same key conflicts.
    /// </summary>
    /// <exception cref="HostValidationException">The spec is invalid.</exception>
    public (PolicyVersion Version, bool Replayed) Publish(string policyId, JsonObjectValue spec, string? idempotencyKey = null)
    {
        CheckId(policyId);
        var policy = JudgmentPolicy.Parse(spec, models);
        var hash = CanonicalJson.Hash(policy.Spec);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        string Apply(SqliteTransaction transaction)
        {
            var latest = Latest(transaction, policyId);
            if (latest is not null && latest.Hash == hash) return Encode(latest);
            var version = (latest?.Version ?? 0) + 1;
            catalog.Run(transaction, "INSERT INTO policy_versions (policy_id, version, spec, hash, created_at) VALUES ($id, $version, $spec, $hash, $at);",
                ("$id", policyId), ("$version", (long)version), ("$spec", CanonicalJson.Serialize(policy.Spec)), ("$hash", hash), ("$at", now));
            if (latest is null)
                catalog.Run(transaction, "INSERT INTO policies (id, active_version, revision, latest_version, created_at, updated_at) VALUES ($id, 1, 1, 1, $at, $at);",
                    ("$id", policyId), ("$at", now));
            else
                catalog.Run(transaction, "UPDATE policies SET latest_version = $version, updated_at = $at WHERE id = $id;",
                    ("$id", policyId), ("$version", (long)version), ("$at", now));
            return Encode(Find(transaction, policyId, version)!);
        }
        if (idempotencyKey is null)
            return (Decode(catalog.Transact(Apply)), false);
        var result = catalog.RunOperation(idempotencyKey, "policy.publish:" + policyId, CanonicalJson.Hash(spec), now, Apply);
        return (Decode(result.Result), result.Replayed);
    }

    /// <summary>Points the policy at <paramref name="version"/>.</summary>
    /// <exception cref="HostConflictException">The pointer changed since <paramref name="expectedRevision"/>.</exception>
    public PolicySummary Activate(string policyId, int version, long expectedRevision)
    {
        CheckId(policyId);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        return catalog.Transact(transaction =>
        {
            var summary = Summary(transaction, policyId) ?? throw new HostNotFoundException($"No policy {policyId}.");
            if (Find(transaction, policyId, version) is null) throw new HostNotFoundException($"Policy {policyId} has no version {version}.");
            if (summary.Revision != expectedRevision)
                throw new HostConflictException($"Policy {policyId} is at revision {summary.Revision}, not {expectedRevision}.");
            if (summary.ActiveVersion == version) return summary;
            catalog.Run(transaction, "UPDATE policies SET active_version = $version, revision = revision + 1, updated_at = $at WHERE id = $id;",
                ("$id", policyId), ("$version", (long)version), ("$at", now));
            return Summary(transaction, policyId)!;
        });
    }

    /// <summary>Every policy, by ID.</summary>
    public IReadOnlyList<PolicySummary> List() => catalog.Read(_ =>
        catalog.Rows(null, "SELECT id FROM policies ORDER BY id;", reader => reader.GetString(0)).Select(id => Summary(null, id)!).ToList());

    /// <summary>A policy; null when there is none.</summary>
    public PolicySummary? Get(string policyId) => catalog.Read(_ => Summary(null, policyId));

    /// <summary>A policy's versions, newest first.</summary>
    public IReadOnlyList<PolicyVersion> Versions(string policyId) => catalog.Read(_ =>
        catalog.Rows(null, "SELECT policy_id, version, spec, hash, created_at FROM policy_versions WHERE policy_id = $id ORDER BY version DESC;", Map,
            ("$id", policyId)));

    /// <summary>One version; null when there is none.</summary>
    public PolicyVersion? Version(string policyId, int version) => catalog.Read(_ => Find(null, policyId, version));

    /// <summary>The version a new evaluation uses: <paramref name="version"/>, or the active one.</summary>
    /// <exception cref="HostNotFoundException">No such policy or version, or the policy has no active version.</exception>
    public PolicyVersion Resolve(string policyId, int? version = null)
    {
        var summary = Get(policyId) ?? throw new HostNotFoundException($"No policy {policyId}.");
        var chosen = version ?? summary.ActiveVersion ?? throw new HostNotFoundException($"Policy {policyId} has no active version.");
        return Version(policyId, chosen) ?? throw new HostNotFoundException($"Policy {policyId} has no version {chosen}.");
    }

    private PolicySummary? Summary(SqliteTransaction? transaction, string policyId)
    {
        var row = catalog.Rows(transaction, "SELECT id, active_version, revision, latest_version, created_at, updated_at FROM policies WHERE id = $id;",
            reader => (Id: reader.GetString(0), Active: reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1), Revision: reader.GetInt64(2),
                Latest: reader.GetInt32(3), Created: reader.GetInt64(4), Updated: reader.GetInt64(5)), ("$id", policyId)).FirstOrDefault();
        if (row.Id is null) return null;
        var latest = Find(transaction, policyId, row.Latest)!;
        return new PolicySummary(row.Id, row.Active, row.Revision, row.Latest, Jobs.String(latest.Spec, "title") ?? row.Id,
            Jobs.String(latest.Spec, "kind") ?? "", row.Created, row.Updated);
    }

    private PolicyVersion? Latest(SqliteTransaction transaction, string policyId) =>
        catalog.Rows(transaction, "SELECT policy_id, version, spec, hash, created_at FROM policy_versions WHERE policy_id = $id ORDER BY version DESC LIMIT 1;",
            Map, ("$id", policyId)).FirstOrDefault();

    private PolicyVersion? Find(SqliteTransaction? transaction, string policyId, int version) =>
        catalog.Rows(transaction, "SELECT policy_id, version, spec, hash, created_at FROM policy_versions WHERE policy_id = $id AND version = $version;",
            Map, ("$id", policyId), ("$version", (long)version)).FirstOrDefault();

    private static PolicyVersion Map(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt32(1), (JsonObjectValue)JsonValue.Parse(reader.GetString(2)), reader.GetString(3), reader.GetInt64(4));

    private static string Encode(PolicyVersion version) => CanonicalJson.Serialize(version.ToJson());

    private static PolicyVersion Decode(string json)
    {
        var value = (JsonObjectValue)JsonValue.Parse(json);
        return new PolicyVersion(Jobs.String(value, "policyId")!, (int)Jobs.Long(value, "version")!.Value, (JsonObjectValue)value["spec"],
            Jobs.String(value, "hash")!, Jobs.Long(value, "createdAt")!.Value);
    }

    private static void CheckId(string policyId)
    {
        if (!JudgmentPolicy.IsId(policyId)) throw new HostInvalidException($"A policy ID must match ^[a-z0-9][a-z0-9_-]{{0,39}}$: {policyId}");
    }
}
