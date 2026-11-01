// PiSharp-only (decision 0010, M3): source definitions and what checking them learned, in the catalog. A definition is revisioned for
// optimistic changes; items, fetch receipts, the conditional-request cache and origin health belong to the device, and a check's run links to
// them through its run key.
using System.Globalization;
using Microsoft.Data.Sqlite;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>What checking a source learned.</summary>
/// <param name="Failures">Consecutive failed checks; a success resets it. A source is never disabled because of them.</param>
/// <param name="NextEligibleAt">When the source's origin may be fetched again after a failure.</param>
public sealed record SourceState(long? LastCheckedAt, long? LastChangedAt, long? LastSuccessAt, string? LastError, long? LastErrorAt, int Failures,
    long? NextEligibleAt, string? LastRunKey, int Items)
{
    /// <summary>The state as JSON; <c>status</c> is <c>new</c>, <c>ok</c> or <c>degraded</c>.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object(
    [
        new("status", LastCheckedAt is null ? "new" : Failures > 0 ? "degraded" : "ok"),
        new("lastCheckedAt", Number(LastCheckedAt)),
        new("lastChangedAt", Number(LastChangedAt)),
        new("lastSuccessAt", Number(LastSuccessAt)),
        new("lastError", LastError is null ? JsonValue.Null : LastError),
        new("lastErrorAt", Number(LastErrorAt)),
        new("failures", Failures),
        new("nextEligibleAt", Number(NextEligibleAt)),
        new("lastRunKey", LastRunKey is null ? JsonValue.Null : LastRunKey),
        new("items", Items),
    ]);

    private static JsonValue Number(long? value) => value is { } number ? number : JsonValue.Null;
}

/// <summary>A source definition and its state.</summary>
public sealed record SourceRecord(string Id, JsonObjectValue Spec, long Revision, long CreatedAt, long UpdatedAt, SourceState State)
{
    /// <summary><c>feed</c>, <c>github-releases</c> or <c>page</c>.</summary>
    public string Kind => Jobs.String(Spec, "kind")!;

    /// <summary>Whether checks of every enabled source include it.</summary>
    public bool Enabled => Spec.TryGetValue("enabled", out var enabled) && enabled is JsonBooleanValue { Value: true };

    /// <summary>The definition with its revision, times and state.</summary>
    public JsonObjectValue ToJson() => Spec.SetItem("revision", Revision).SetItem("createdAt", CreatedAt).SetItem("updatedAt", UpdatedAt).SetItem("state", State.ToJson());
}

/// <summary>Creates, changes and reads the device's sources.</summary>
public sealed class SourceStore
{
    /// <summary>The definition's shape; <c>url</c> is required for feeds and pages, <c>repository</c> (<c>owner/name</c>) for releases.</summary>
    public static readonly JsonObjectValue Shape = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","additionalProperties":false,"required":["id","kind"],"properties":{
          "id":{"type":"string","minLength":1,"maxLength":40,"pattern":"^[a-z0-9][a-z0-9_-]{0,39}$"},
          "kind":{"type":"string","maxLength":20,"enum":["feed","github-releases","page"]},
          "url":{"type":"string","maxLength":2000,"format":"uri"},
          "repository":{"type":"string","maxLength":141,"pattern":"^[A-Za-z0-9_.-]{1,39}/[A-Za-z0-9_.-]{1,100}$"},
          "title":{"type":"string","minLength":1,"maxLength":200},
          "enabled":{"type":"boolean","default":true},
          "maxItems":{"type":"integer","minimum":1,"maximum":500,"default":100},
          "maxPages":{"type":"integer","minimum":1,"maximum":20,"default":1},
          "includePrereleases":{"type":"boolean","default":false},
          "credentialRef":{"type":"string","minLength":1,"maxLength":64,"pattern":"^[a-z0-9][a-z0-9_.-]{0,63}$"},
          "allowFallback":{"type":"boolean","default":false}}}
        """);

    private readonly SessionCatalog catalog;
    private readonly RetrievalOptions options;
    private readonly TimeProvider time;

    internal SourceStore(SessionCatalog catalog, RetrievalOptions options, TimeProvider time)
    {
        this.catalog = catalog;
        this.options = options;
        this.time = time;
    }

    /// <summary>
    /// Creates a source at revision 1, or brings a deleted one back at its next revision (its items stay, so they are not announced
    /// again). With an <paramref name="idempotencyKey"/>, a repeat returns the first result and a different definition conflicts.
    /// </summary>
    /// <exception cref="HostValidationException">The definition is invalid.</exception>
    /// <exception cref="HostConflictException">A source with the ID exists.</exception>
    public (SourceRecord Source, bool Replayed) Create(JsonObjectValue spec, string? idempotencyKey = null)
    {
        var normalized = Validate(spec);
        var id = Jobs.String(normalized, "id")!;
        var now = Now();
        string Apply(SqliteTransaction transaction)
        {
            var existing = catalog.Rows(transaction, "SELECT revision, deleted_at FROM sources WHERE id = $id;",
                reader => (Revision: reader.GetInt64(0), Deleted: !reader.IsDBNull(1)), ("$id", id)).FirstOrDefault();
            if (existing.Revision > 0 && !existing.Deleted) throw new HostConflictException($"A source {id} exists.");
            if (existing.Revision > 0)
                catalog.Run(transaction, "UPDATE sources SET spec = $spec, revision = revision + 1, updated_at = $at, deleted_at = NULL WHERE id = $id;",
                    ("$id", id), ("$spec", CanonicalJson.Serialize(normalized)), ("$at", now));
            else
                catalog.Run(transaction, "INSERT INTO sources (id, spec, revision, created_at, updated_at, deleted_at) VALUES ($id, $spec, 1, $at, $at, NULL);",
                    ("$id", id), ("$spec", CanonicalJson.Serialize(normalized)), ("$at", now));
            return id;
        }
        if (idempotencyKey is null)
        {
            catalog.Transact(Apply);
            return (Get(id)!, false);
        }
        var result = catalog.RunOperation(idempotencyKey, "source.create:" + id, CanonicalJson.Hash(spec), now, Apply);
        return (Get(result.Result) ?? throw new HostNotFoundException($"Source {result.Result} was deleted."), result.Replayed);
    }

    /// <summary>Changes a source: <paramref name="changes"/> merges into the definition (a null removes an optional field). The ID and
    /// kind cannot change.</summary>
    /// <exception cref="HostConflictException">The source changed since <paramref name="expectedRevision"/>.</exception>
    public SourceRecord Update(string id, JsonObjectValue changes, long expectedRevision)
    {
        var current = Get(id) ?? throw new HostNotFoundException($"No source {id}.");
        var merged = current.Spec;
        foreach (var (name, value) in changes)
            merged = value is JsonNullValue ? merged.Remove(name) : merged.SetItem(name, value);
        var errors = new List<FieldError>();
        if (Jobs.String(merged, "id") != id) errors.Add(new FieldError("/id", "cannot change"));
        if (Jobs.String(merged, "kind") != current.Kind) errors.Add(new FieldError("/kind", "cannot change; create another source"));
        if (errors.Count > 0) throw HostValidationException.Of("Invalid source", errors);
        var normalized = Validate(merged);
        var now = Now();
        catalog.Transact(transaction =>
        {
            var revision = catalog.Value(transaction, "SELECT revision FROM sources WHERE id = $id AND deleted_at IS NULL;", ("$id", id)) as long?
                ?? throw new HostNotFoundException($"No source {id}.");
            if (revision != expectedRevision) throw new HostConflictException($"Source {id} is at revision {revision}, not {expectedRevision}.");
            catalog.Run(transaction, "UPDATE sources SET spec = $spec, revision = revision + 1, updated_at = $at WHERE id = $id;",
                ("$id", id), ("$spec", CanonicalJson.Serialize(normalized)), ("$at", now));
            return 0;
        });
        return Get(id)!;
    }

    /// <summary>Deletes a source. Its row stays as a tombstone with its items, so a source created again with the ID does not announce
    /// them again.</summary>
    /// <exception cref="HostConflictException">The source changed since <paramref name="expectedRevision"/>.</exception>
    public void Delete(string id, long expectedRevision)
    {
        var now = Now();
        catalog.Transact(transaction =>
        {
            var revision = catalog.Value(transaction, "SELECT revision FROM sources WHERE id = $id AND deleted_at IS NULL;", ("$id", id)) as long?
                ?? throw new HostNotFoundException($"No source {id}.");
            if (revision != expectedRevision) throw new HostConflictException($"Source {id} is at revision {revision}, not {expectedRevision}.");
            catalog.Run(transaction, "UPDATE sources SET revision = revision + 1, updated_at = $at, deleted_at = $at WHERE id = $id;", ("$id", id), ("$at", now));
            return 0;
        });
    }

    /// <summary>Every source, by ID.</summary>
    public IReadOnlyList<SourceRecord> List() => catalog.Read(_ =>
        catalog.Rows(null, Select + " WHERE deleted_at IS NULL ORDER BY id;", reader => Map(null, reader)));

    /// <summary>A source; null when there is none.</summary>
    public SourceRecord? Get(string id) => catalog.Read(_ =>
        catalog.Rows(null, Select + " WHERE id = $id AND deleted_at IS NULL;", reader => Map(null, reader), ("$id", id)).FirstOrDefault());

    /// <summary>A source with the health of its origin for each provider and its most recent fetch receipts.</summary>
    /// <exception cref="HostNotFoundException">No such source.</exception>
    public JsonObjectValue Detail(string id, int receipts = 10)
    {
        var source = Get(id) ?? throw new HostNotFoundException($"No source {id}.");
        var url = source.Kind == "github-releases" ? new Uri(options.GitHubApi) : new Uri(Jobs.String(source.Spec, "url")!);
        var origin = DirectRetrievalProvider.Origin(url);
        var health = catalog.Read(_ => catalog.Rows(null, HealthSelect + " WHERE origin = $origin ORDER BY provider;", MapHealth, ("$origin", origin)));
        return source.ToJson()
            .SetItem("origin", origin)
            .SetItem("health", JsonValue.Array(health.Select(item => (JsonValue)item.ToJson())))
            .SetItem("receipts", JsonValue.Array(Receipts(id, receipts)));
    }

    /// <summary>A page of a source's items, most recently changed first: <c>{items, next}</c>. Absent items (gone from the source) stay,
    /// with <c>absentAt</c>.</summary>
    public JsonObjectValue Items(string id, int limit = 50, string? cursor = null)
    {
        if (Get(id) is null) throw new HostNotFoundException($"No source {id}.");
        limit = Math.Clamp(limit, 1, 200);
        var offset = cursor is null ? 0 : int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : throw new HostInvalidException("Invalid cursor.");
        var rows = catalog.Read(_ => catalog.Rows(null, """
            SELECT item_id, url, title, published_at, content_hash, parser, version, first_seen_at, last_seen_at, changed_at, absent_at, run_key
            FROM source_items WHERE source_id = $id ORDER BY changed_at DESC, item_id LIMIT $limit OFFSET $offset;
            """, reader => (JsonValue)JsonValue.Object(
            [
                new("itemId", reader.GetString(0)),
                new("url", reader.IsDBNull(1) ? JsonValue.Null : reader.GetString(1)),
                new("title", reader.GetString(2)),
                new("publishedAt", reader.IsDBNull(3) ? JsonValue.Null : reader.GetInt64(3)),
                new("contentHash", reader.GetString(4)),
                new("parser", reader.GetString(5)),
                new("version", reader.GetInt64(6)),
                new("firstSeenAt", reader.GetInt64(7)),
                new("lastSeenAt", reader.GetInt64(8)),
                new("changedAt", reader.GetInt64(9)),
                new("absentAt", reader.IsDBNull(10) ? JsonValue.Null : reader.GetInt64(10)),
                new("runKey", reader.IsDBNull(11) ? JsonValue.Null : reader.GetString(11)),
            ]), ("$id", id), ("$limit", (long)limit + 1), ("$offset", (long)offset)));
        var more = rows.Count > limit;
        return JsonValue.Object([new("items", JsonValue.Array(rows.Take(limit))),
            new("next", more ? (offset + limit).ToString(CultureInfo.InvariantCulture) : JsonValue.Null)]);
    }

    /// <summary>A source's most recent fetch receipts, newest first.</summary>
    public IReadOnlyList<JsonObjectValue> Receipts(string id, int limit = 20) => catalog.Read(_ =>
        catalog.Rows(null, ReceiptSelect + " WHERE source_id = $id ORDER BY created_at DESC, operation_id DESC LIMIT $limit;", MapReceipt,
            ("$id", id), ("$limit", (long)Math.Clamp(limit, 1, 100))).Select(receipt => receipt.ToJson()).ToList());

    /// <summary>The health of every provider and origin that failed: <c>{provider, origin, failures, cooldownUntil, retryAfterUntil,
    /// lastStatus, lastFailure, updatedAt}</c>.</summary>
    public IReadOnlyList<JsonObjectValue> Health() => catalog.Read(_ =>
        catalog.Rows(null, HealthSelect + " ORDER BY provider, origin;", MapHealth).Select(health => health.ToJson()).ToList());

    // ----- what checks use -----

    internal RetrievalOptions Options => options;

    internal long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    internal SessionCatalog Catalog => catalog;

    internal FetchReceipt? Receipt(string operationId) => catalog.Read(_ =>
        catalog.Rows(null, ReceiptSelect + " WHERE operation_id = $id;", MapReceipt, ("$id", operationId)).FirstOrDefault());

    /// <summary>The newest fetched receipt of a page of a source in a run.</summary>
    internal FetchReceipt? FetchedPage(string runKey, string sourceId, int page)
    {
        var prefix = $"fetch:{runKey}:{sourceId}:p{page}:";
        return catalog.Read(_ => catalog.Rows(null, ReceiptSelect +
            " WHERE substr(operation_id, 1, length($prefix)) = $prefix AND status = 'fetched' ORDER BY updated_at DESC, operation_id DESC LIMIT 1;",
            MapReceipt, ("$prefix", prefix)).FirstOrDefault());
    }

    /// <summary>Records that a fetch is about to call its provider: a later run that finds it still calling knows the outcome is
    /// unknown.</summary>
    internal void BeginReceipt(string operationId, string sourceId, string runKey, string provider, Uri url)
    {
        var now = Now();
        catalog.Transact(transaction =>
        {
            catalog.Run(transaction, """
                INSERT INTO fetch_receipts (operation_id, source_id, run_key, provider, url, status, created_at, updated_at)
                VALUES ($id, $source, $run, $provider, $url, 'calling', $at, $at) ON CONFLICT (operation_id) DO NOTHING;
                """, ("$id", operationId), ("$source", sourceId), ("$run", runKey), ("$provider", provider), ("$url", url.ToString()), ("$at", now));
            return 0;
        });
    }

    /// <summary>Settles a receipt: <c>fetched</c> (with the response and its body's blob), <c>failed</c> or <c>uncertain</c>.</summary>
    internal void FinishReceipt(string operationId, string status, RetrievalResponse? response, string? blobHash, string? error, int? httpStatus = null)
    {
        var now = Now();
        catalog.Transact(transaction =>
        {
            catalog.Run(transaction, """
                UPDATE fetch_receipts SET status = $status, http_status = $code, blob_hash = $blob, content_type = $type, etag = $etag,
                  last_modified = $modified, link = $link, final_url = $final, error = $error, updated_at = $at WHERE operation_id = $id;
                """, ("$id", operationId), ("$status", status), ("$code", (long?)(response?.Status ?? httpStatus)), ("$blob", blobHash),
                ("$type", response?.ContentType), ("$etag", response?.ETag), ("$modified", response?.LastModified), ("$link", response?.Link),
                ("$final", response?.FinalUrl.ToString()), ("$error", error is { Length: > 2000 } ? error[..2000] : error), ("$at", now));
            return 0;
        });
    }

    /// <summary>The conditional-request cache entry of a URL as one provider fetched it for one authorization identity, so content fetched
    /// with a credential never answers an anonymous request.</summary>
    internal (string? ETag, string? LastModified) Cached(string provider, Uri url, string identity) => catalog.Read(_ =>
        catalog.Rows(null, "SELECT etag, last_modified FROM retrieval_cache WHERE key = $key;",
            reader => (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)),
            ("$key", CacheKey(provider, url, identity))).FirstOrDefault());

    internal static string CacheKey(string provider, Uri url, string identity) => CanonicalJson.Id("rc_", provider, url.ToString(), identity);

    internal void Cache(SqliteTransaction transaction, string provider, Uri url, string identity, RetrievalResponse response, string? blobHash)
    {
        if (response.ETag is null && response.LastModified is null) return;
        catalog.Run(transaction, """
            INSERT INTO retrieval_cache (key, provider, url, identity, etag, last_modified, blob_hash, content_type, fetched_at)
            VALUES ($key, $provider, $url, $identity, $etag, $modified, $blob, $type, $at)
            ON CONFLICT (key) DO UPDATE SET etag = excluded.etag, last_modified = excluded.last_modified, blob_hash = excluded.blob_hash,
              content_type = excluded.content_type, fetched_at = excluded.fetched_at;
            """, ("$key", CacheKey(provider, url, identity)), ("$provider", provider), ("$url", url.ToString()), ("$identity", identity),
            ("$etag", response.ETag), ("$modified", response.LastModified), ("$blob", blobHash), ("$type", response.ContentType), ("$at", Now()));
    }

    internal OriginHealth? HealthOf(string provider, string origin) => catalog.Read(_ =>
        catalog.Rows(null, HealthSelect + " WHERE provider = $provider AND origin = $origin;", MapHealth, ("$provider", provider), ("$origin", origin))
            .FirstOrDefault());

    /// <summary>
    /// Records a transient failure of an origin: it cools down for the server's <c>Retry-After</c> (at most 24 hours), or else 2 minutes
    /// doubling to 30 minutes with ±10% jitter. 403 and 429 always cool down, so they are never retried at once.
    /// </summary>
    internal OriginHealth Fail(string provider, string origin, RetrievalException error)
    {
        var now = Now();
        var failure = $"{error.Failure}: {error.Message}";
        if (failure.Length > 500) failure = failure[..500];
        return catalog.Transact(transaction =>
        {
            var failures = (int)((catalog.Value(transaction, "SELECT failures FROM retrieval_health WHERE provider = $provider AND origin = $origin;",
                ("$provider", provider), ("$origin", origin)) as long?) ?? 0) + 1;
            var backoff = Backoff(provider, origin, failures);
            var retryAfter = error.RetryAfter is { } wait ? Math.Min((long)wait.TotalMilliseconds, 24 * 3_600_000L) : (long?)null;
            var until = now + Math.Max(backoff, retryAfter ?? 0);
            catalog.Run(transaction, """
                INSERT INTO retrieval_health (provider, origin, failures, cooldown_until, retry_after_until, last_status, last_failure, updated_at)
                VALUES ($provider, $origin, $failures, $until, $after, $status, $failure, $at)
                ON CONFLICT (provider, origin) DO UPDATE SET failures = excluded.failures, cooldown_until = excluded.cooldown_until,
                  retry_after_until = excluded.retry_after_until, last_status = excluded.last_status, last_failure = excluded.last_failure,
                  updated_at = excluded.updated_at;
                """, ("$provider", provider), ("$origin", origin), ("$failures", (long)failures), ("$until", until),
                ("$after", retryAfter is { } after ? now + after : null), ("$status", (long?)error.Status),
                ("$failure", failure), ("$at", now));
            return catalog.Rows(transaction, HealthSelect + " WHERE provider = $provider AND origin = $origin;", MapHealth,
                ("$provider", provider), ("$origin", origin)).First();
        });
    }

    /// <summary>Clears an origin's failures after a success.</summary>
    internal void Succeed(string provider, string origin) => catalog.Transact(transaction =>
    {
        catalog.Run(transaction, "DELETE FROM retrieval_health WHERE provider = $provider AND origin = $origin;", ("$provider", provider), ("$origin", origin));
        return 0;
    });

    /// <summary>The cooldown after <paramref name="failures"/> failures, in milliseconds: deterministic jitter per origin and count.</summary>
    internal static long Backoff(string provider, string origin, int failures)
    {
        var baseMs = Math.Min(120_000.0 * Math.Pow(2, Math.Min(failures, 16) - 1), 1_800_000.0);
        var hash = Convert.FromHexString(CanonicalJson.HashText($"{provider}|{origin}|{failures}")[7..15]);
        var fraction = BitConverter.ToUInt32(hash) / (double)uint.MaxValue;
        return (long)(baseMs * (0.9 + 0.2 * fraction));
    }

    /// <summary>Records a check's failure on the source's state.</summary>
    internal void RecordFailure(string sourceId, string runKey, string error, long? nextEligibleAt) => catalog.Transact(transaction =>
    {
        var now = Now();
        EnsureState(transaction, sourceId);
        catalog.Run(transaction, """
            UPDATE source_state SET last_checked_at = $at, last_error = $error, last_error_at = $at, failures = failures + 1,
              next_eligible_at = $next, last_run_key = $run WHERE source_id = $id;
            """, ("$id", sourceId), ("$at", now), ("$error", error.Length > 1000 ? error[..1000] : error), ("$next", nextEligibleAt), ("$run", runKey));
        return 0;
    });

    internal void EnsureState(SqliteTransaction transaction, string sourceId) =>
        catalog.Run(transaction, "INSERT INTO source_state (source_id, failures) VALUES ($id, 0) ON CONFLICT (source_id) DO NOTHING;", ("$id", sourceId));

    internal string? StateContentHash(SqliteTransaction? transaction, string sourceId) =>
        catalog.Value(transaction, "SELECT content_hash FROM source_state WHERE source_id = $id;", ("$id", sourceId)) as string;

    // ----- validation and mapping -----

    private JsonObjectValue Validate(JsonObjectValue spec)
    {
        var errors = new List<FieldError>(InputSchema.Validate(Shape, spec));
        var kind = Jobs.String(spec, "kind");
        if (kind is "feed" or "page" && !spec.ContainsKey("url")) errors.Add(new FieldError("/url", $"is required for a {kind} source"));
        if (kind is "feed" or "page" && spec.ContainsKey("repository")) errors.Add(new FieldError("/repository", $"is only for github-releases sources"));
        if (kind == "github-releases" && !spec.ContainsKey("repository")) errors.Add(new FieldError("/repository", "is required for a github-releases source"));
        if (kind == "github-releases" && spec.ContainsKey("url")) errors.Add(new FieldError("/url", "is not used by a github-releases source; name the repository"));
        if (Jobs.String(spec, "url") is { } url && !Artifacts.IsSafeUrl(url)) errors.Add(new FieldError("/url", "must be an absolute http(s) URL"));
        if (Jobs.Long(spec, "maxPages") is { } pages && pages > options.Limits.MaxPages)
            errors.Add(new FieldError("/maxPages", $"may be at most {options.Limits.MaxPages} on this device"));
        if (Jobs.String(spec, "credentialRef") is { } reference && !options.Credentials.ContainsKey(reference))
            errors.Add(new FieldError("/credentialRef", "is not a credential this device has"));
        if (errors.Count > 0) throw HostValidationException.Of("Invalid source", errors);
        var normalized = InputSchema.Normalize(Shape, spec, "Invalid source");
        return normalized.ContainsKey("title") ? normalized : normalized.SetItem("title", Jobs.String(normalized, "id")!);
    }

    private const string Select = "SELECT id, spec, revision, created_at, updated_at FROM sources";

    private SourceRecord Map(SqliteTransaction? transaction, SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var state = catalog.Rows(transaction, """
            SELECT last_checked_at, last_changed_at, last_success_at, last_error, last_error_at, failures, next_eligible_at, last_run_key
            FROM source_state WHERE source_id = $id;
            """, row => (Checked: Long(row, 0), Changed: Long(row, 1), Success: Long(row, 2), Error: row.IsDBNull(3) ? null : row.GetString(3),
                ErrorAt: Long(row, 4), Failures: row.GetInt32(5), Next: Long(row, 6), Run: row.IsDBNull(7) ? null : row.GetString(7)), ("$id", id))
            .FirstOrDefault();
        var items = Convert.ToInt32(catalog.Value(transaction, "SELECT COUNT(*) FROM source_items WHERE source_id = $id AND absent_at IS NULL;", ("$id", id)),
            CultureInfo.InvariantCulture);
        return new SourceRecord(id, (JsonObjectValue)JsonValue.Parse(reader.GetString(1)), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
            new SourceState(state.Checked, state.Changed, state.Success, state.Error, state.ErrorAt, state.Failures, state.Next, state.Run, items));
    }

    private static long? Long(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);

    private const string ReceiptSelect = """
        SELECT operation_id, source_id, run_key, provider, url, status, http_status, blob_hash, content_type, etag, last_modified, link, final_url, error,
          created_at, updated_at FROM fetch_receipts
        """;

    private static FetchReceipt MapReceipt(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), Text(reader, 7), Text(reader, 8), Text(reader, 9),
        Text(reader, 10), Text(reader, 11), Text(reader, 12), Text(reader, 13), reader.GetInt64(14), reader.GetInt64(15));

    private const string HealthSelect = "SELECT provider, origin, failures, cooldown_until, retry_after_until, last_status, last_failure, updated_at FROM retrieval_health";

    private static OriginHealth MapHealth(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), Long(reader, 3),
        Long(reader, 4), reader.IsDBNull(5) ? null : reader.GetInt32(5), Text(reader, 6), reader.GetInt64(7));

    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
}

/// <summary>One fetch attempt.</summary>
/// <param name="Status"><c>calling</c>, <c>fetched</c>, <c>failed</c> or <c>uncertain</c>.</param>
internal sealed record FetchReceipt(string OperationId, string SourceId, string RunKey, string Provider, string Url, string Status, int? HttpStatus,
    string? BlobHash, string? ContentType, string? ETag, string? LastModified, string? Link, string? FinalUrl, string? Error, long CreatedAt, long UpdatedAt)
{
    public JsonObjectValue ToJson() => JsonValue.Object(
    [
        new("operationId", OperationId), new("sourceId", SourceId), new("runKey", RunKey), new("provider", Provider), new("url", Url),
        new("status", Status), new("httpStatus", HttpStatus is { } code ? code : JsonValue.Null), new("contentType", Nullable(ContentType)),
        new("etag", Nullable(ETag)), new("lastModified", Nullable(LastModified)), new("finalUrl", Nullable(FinalUrl)), new("error", Nullable(Error)),
        new("stored", BlobHash is not null), new("createdAt", CreatedAt), new("updatedAt", UpdatedAt),
    ]);

    private static JsonValue Nullable(string? value) => value is null ? JsonValue.Null : value;
}

/// <summary>A provider's failures at one origin.</summary>
/// <param name="RetryAfterUntil">The end of the server's <c>Retry-After</c>, which even a forced check waits for.</param>
internal sealed record OriginHealth(string Provider, string Origin, int Failures, long? CooldownUntil, long? RetryAfterUntil, int? LastStatus,
    string? LastFailure, long UpdatedAt)
{
    public JsonObjectValue ToJson() => JsonValue.Object(
    [
        new("provider", Provider), new("origin", Origin), new("failures", Failures),
        new("cooldownUntil", CooldownUntil is { } until ? until : JsonValue.Null),
        new("retryAfterUntil", RetryAfterUntil is { } after ? after : JsonValue.Null),
        new("lastStatus", LastStatus is { } status ? status : JsonValue.Null),
        new("lastFailure", LastFailure is null ? JsonValue.Null : LastFailure), new("updatedAt", UpdatedAt),
    ]);
}
