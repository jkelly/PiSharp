// PiSharp-only (decision 0010, M3): the source check application job. Each fetch attempt has a stable operation ID, a budget reservation
// and a receipt written before the provider is called, so a rerun after a crash reuses a stored response, retries a free call, or leaves a
// paid call's charge uncertain rather than forgetting it. A source's items change in one catalog transaction per run (a stored operation),
// so a rerun gets the same changes back instead of finding everything already seen. Recoverable failures cool their origin down and are
// retried after a durable sleep, within the attempt limit; nothing disables a source.
using System.Text;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

internal static class SourceChecks
{
    /// <summary>The job's ID.</summary>
    public const string JobId = "pisharp-source-check";

    private static readonly JsonObjectValue Shape = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","additionalProperties":false,"properties":{
          "sourceIds":{"type":"array","minItems":1,"maxItems":50,"items":{"type":"string","minLength":1,"maxLength":40}},
          "policyId":{"type":"string","minLength":1,"maxLength":40},
          "version":{"type":"integer","minimum":1,"maximum":100000},
          "reevaluate":{"type":"boolean","default":false},
          "force":{"type":"boolean","default":false},
          "maxJudgments":{"type":"integer","minimum":1,"maximum":100,"default":20}}}
        """);

    /// <summary>
    /// <c>pisharp-source-check {sourceIds?, policyId?, version?, reevaluate?, force?, maxJudgments?}</c>: checks the named sources (every
    /// enabled one when none are named) as they were when the run was admitted, keeps new and changed items as evidence, judges them under
    /// the policy when one is named (<c>reevaluate</c> judges the current items again, for a new policy version), and lists the changes. A
    /// forced check ignores an origin's cooldown, but not a server's <c>Retry-After</c>.
    /// </summary>
    public static JobDefinition Job(DurableDeviceHost host) => new()
    {
        Id = JobId,
        Title = "Source check",
        Summary = "Checks sources for new and changed items, keeps them as evidence, optionally judges them, and lists what changed.",
        Application = true,
        EstimatedSeconds = 30,
        InputSchema = Shape,
        Admit = input => Admit(host, input),
        Freeze = input => Freeze(host, input),
        Phases =
        [
            new JobPhase("fetch", "Fetch", "Fetches each source within its limits; recoverable failures are retried after a durable wait.") { Run = FetchAsync },
            new JobPhase("judge", "Judge", "Judges new and changed items under the policy.")
            {
                Run = JudgeAsync,
                When = data => Jobs.Items(data, "judge").Any(),
            },
            new JobPhase("record", "Record", "Lists what changed, with its evidence.") { Run = RecordAsync },
        ],
    };

    private static JsonObjectValue Admit(DurableDeviceHost host, JsonObjectValue? input)
    {
        var normalized = InputSchema.Normalize(Shape, input, "Invalid source check");
        var errors = new List<FieldError>();
        foreach (var (id, index) in Jobs.Items(normalized, "sourceIds").OfType<JsonStringValue>().Select((id, index) => (id.Value, index)))
            if (host.Sources.Get(id) is null) errors.Add(new FieldError($"/sourceIds/{index}", $"no source {id}"));
        if (Jobs.String(normalized, "policyId") is { } policyId)
        {
            try { host.Policies.Resolve(policyId, Jobs.Long(normalized, "version") is { } version ? (int)version : null); }
            catch (HostNotFoundException missing) { errors.Add(new FieldError("/policyId", missing.Message)); }
        }
        else if (Flag(normalized, "reevaluate")) errors.Add(new FieldError("/reevaluate", "needs a policyId"));
        if (errors.Count > 0) throw HostValidationException.Of("Invalid source check", errors);
        return normalized;
    }

    /// <summary>The sources as they are at admission, and the policy version.</summary>
    private static JsonObjectValue Freeze(DurableDeviceHost host, JsonObjectValue input)
    {
        var named = Jobs.Items(input, "sourceIds").OfType<JsonStringValue>().Select(id => id.Value).ToList();
        var sources = named.Count > 0 ? named.Select(id => host.Sources.Get(id)!).ToList() : [.. host.Sources.List().Where(source => source.Enabled)];
        var frozen = JsonValue.Object([new("sources", JsonValue.Array(sources.Select(source => (JsonValue)JsonValue.Object(
            [new("id", source.Id), new("revision", source.Revision), new("spec", source.Spec)]))))]);
        return Jobs.String(input, "policyId") is { } policyId
            ? frozen.SetItem("policyVersion", host.Policies.Resolve(policyId, Jobs.Long(input, "version") is { } version ? (int)version : null).Version)
            : frozen;
    }

    // ----- fetch -----

    private static async Task<JobPhaseResult> FetchAsync(JobContext context)
    {
        var store = context.Host.Sources;
        var results = context.Iteration > 0 && context.Data.TryGetValue("results", out var carried) && carried is JsonObjectValue kept ? kept : JsonObjectValue.Empty;
        var sources = Jobs.Items(context.Frozen, "sources").OfType<JsonObjectValue>().ToList();
        foreach (var source in sources)
        {
            var id = Jobs.String(source, "id")!;
            var previous = results.TryGetValue(id, out var earlier) ? (JsonObjectValue)earlier : null;
            if (previous is not null && Jobs.String(previous, "status") != "retrying") continue;
            var attempts = previous is null ? 0 : (int)(Jobs.Long(previous, "attempts") ?? 0);
            var result = await CheckAsync(context, (JsonObjectValue)source["spec"], attempts).ConfigureAwait(false);
            results = results.SetItem(id, result);
            if (Jobs.String(result, "status") != "retrying")
                await context.NoteAsync($"{id}: {Jobs.String(result, "status")}" + (Jobs.String(result, "error") is { } error ? " — " + error : $" ({Jobs.Long(result, "new") ?? 0} new, {Jobs.Long(result, "changed") ?? 0} changed)"))
                    .ConfigureAwait(false);
        }
        var retrying = results.Values.OfType<JsonObjectValue>().Where(result => Jobs.String(result, "status") == "retrying").ToList();
        if (retrying.Count > 0)
        {
            var wake = retrying.Min(result => Jobs.Long(result, "retryAt") ?? 0);
            return JobPhaseResult.Again($"Retrying {retrying.Count} source(s) after their cooldown", Math.Max(0, wake - store.Now()),
                JsonValue.Object([new("results", results)]));
        }
        var judge = Jobs.String(context.Input, "policyId") is null ? [] : await ToJudgeAsync(context, sources, results).ConfigureAwait(false);
        return JobPhaseResult.Next(Summary(sources, results), JsonValue.Object(
        [
            new("results", results),
            new("order", JsonValue.Array(sources.Select(source => source["id"]))),
            new("judge", JsonValue.Array(judge)),
        ]));
    }

    /// <summary>Checks one source: its stored result when this run already committed it, else its pages through the provider chain.</summary>
    private static async Task<JsonObjectValue> CheckAsync(JobContext context, JsonObjectValue spec, int attempts)
    {
        var host = context.Host;
        var store = host.Sources;
        var options = store.Options;
        var id = Jobs.String(spec, "id")!;
        var kind = Jobs.String(spec, "kind")!;
        var commitKey = $"source-check:{context.RunKey}:{id}";
        if (store.Catalog.Read(_ => store.Catalog.Value(null, "SELECT result FROM operations WHERE key = $key;", ("$key", commitKey))) is string stored)
            return await WithEvidenceAsync(context, (JsonObjectValue)JsonValue.Parse(stored)).ConfigureAwait(false);

        string? authorization = null;
        var identity = "anonymous";
        if (Jobs.String(spec, "credentialRef") is { } reference)
        {
            if (!options.Credentials.TryGetValue(reference, out authorization))
                return Failed(store, context, id, attempts, "credential", $"The credential {reference} is not configured on this device.");
            identity = "credential:" + reference;
        }
        var (parser, parserVersion) = SourceParsers.Of(kind);
        identity += $"|{parser}/{parserVersion}";
        var chain = new List<IRetrievalProvider> { host.DirectRetrieval };
        // A fallback never sees a credential, and is used only when both the host configured it and the source allows it.
        if (Flag(spec, "allowFallback") && authorization is null) chain.AddRange(options.Fallbacks);
        var maxItems = (int)(Jobs.Long(spec, "maxItems") ?? 100);
        var maxPages = (int)Math.Min(Jobs.Long(spec, "maxPages") ?? 1, options.Limits.MaxPages);
        var start = kind == "github-releases"
            ? new Uri($"{options.GitHubApi.TrimEnd('/')}/repos/{Jobs.String(spec, "repository")}/releases?per_page={Math.Min(maxItems, 100)}")
            : new Uri(Jobs.String(spec, "url")!);
        Uri? url = start;
        var accept = kind switch
        {
            "feed" => "application/atom+xml, application/rss+xml, application/rdf+xml;q=0.9, application/xml;q=0.8, text/xml;q=0.8, */*;q=0.5",
            "github-releases" => "application/vnd.github+json",
            _ => "text/html, application/xhtml+xml, text/plain;q=0.9, */*;q=0.5",
        };
        var items = new List<SourceItemDraft>();
        var bodies = new StringBuilder();
        var complete = true;
        RetrievalResponse? first = null;
        string? firstProvider = null;
        string? firstBlob = null;
        for (var page = 0; url is not null; page++)
        {
            if (page >= maxPages || items.Count >= maxItems) { complete = false; break; }
            var fetch = await FetchPageAsync(context, id, page, url, chain, accept, authorization, identity, attempts).ConfigureAwait(false);
            attempts = fetch.Attempts;
            if (fetch.Response is not { } response)
            {
                if (fetch.Limited) return Ended(store, context, id, attempts, "limited", fetch.Error!, null);
                if (!fetch.Transient) return Failed(store, context, id, attempts, fetch.Failure ?? "http", fetch.Error!);
                var retryAt = fetch.RetryAt ?? store.Now();
                if (attempts < options.Limits.MaxAttempts && retryAt - store.Now() <= options.Limits.MaxRetryWait.TotalMilliseconds)
                    return JsonValue.Object([new("sourceId", id), new("status", "retrying"), new("attempts", attempts), new("retryAt", retryAt),
                        new("error", fetch.Error ?? "cooling down")]);
                return Ended(store, context, id, attempts, "deferred", fetch.Error ?? "The origin is cooling down.", retryAt);
            }
            if (page == 0) (first, firstProvider, firstBlob) = (response, fetch.Provider, fetch.Blob);
            if (response.NotModified) break;
            ParsedSource parsed;
            try
            {
                parsed = kind switch
                {
                    "feed" => SourceParsers.Feed(response.Body, response.FinalUrl, options.Limits.MaxTextCharacters),
                    "github-releases" => SourceParsers.GitHubReleases(response.Body, response.Link, Flag(spec, "includePrereleases"), options.Limits.MaxTextCharacters),
                    _ => SourceParsers.Page(response.Body, response.ContentType, new Uri(Jobs.String(spec, "url")!), options.Limits.MaxTextCharacters),
                };
            }
            catch (RetrievalException invalid)
            {
                return Failed(store, context, id, attempts, invalid.Failure, invalid.Message);
            }
            bodies.Append(CanonicalJson.HashBytes(response.Body)).Append('\n');
            foreach (var item in parsed.Items)
            {
                if (items.Count >= maxItems) { complete = false; break; }
                if (items.All(existing => existing.ItemId != item.ItemId)) items.Add(item);
            }
            url = parsed.NextPage;
        }
        var notModified = first!.NotModified;
        var hashes = items.ToDictionary(item => item.ItemId, item => ItemHash(parser, parserVersion, item), StringComparer.Ordinal);
        // Keep the text of new and changed items before the commit that records them; content addressing makes a repeat free.
        var known = store.Catalog.Read(_ => store.Catalog.Rows(null, "SELECT item_id, content_hash FROM source_items WHERE source_id = $id;",
            reader => (reader.GetString(0), reader.GetString(1)), ("$id", id))).ToDictionary(row => row.Item1, row => row.Item2, StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items.Where(item => !known.TryGetValue(item.ItemId, out var hash) || hash != hashes[item.ItemId]))
            texts[item.ItemId] = host.Blobs.Put(Encoding.UTF8.GetBytes(item.Text), "text/plain; charset=utf-8", $"source-item:{id}:{item.ItemId}").Hash;
        var bodyHash = notModified ? null : CanonicalJson.HashText($"{parser}/{parserVersion}\n{bodies}");
        var committed = store.Catalog.RunOperation(commitKey, "source.check", commitKey, store.Now(), transaction =>
        {
            var now = store.Now();
            store.EnsureState(transaction, id);
            var rows = store.Catalog.Rows(transaction, "SELECT item_id, content_hash, absent_at FROM source_items WHERE source_id = $id;",
                reader => (Id: reader.GetString(0), Hash: reader.GetString(1), Absent: !reader.IsDBNull(2)), ("$id", id)).ToDictionary(row => row.Id, StringComparer.Ordinal);
            var changes = new List<JsonValue>();
            var removed = 0;
            foreach (var item in items)
            {
                var hash = hashes[item.ItemId];
                if (!rows.TryGetValue(item.ItemId, out var row))
                {
                    store.Catalog.Run(transaction, """
                        INSERT INTO source_items (source_id, item_id, url, title, published_at, content_hash, parser, text_hash, version, first_seen_at,
                          last_seen_at, changed_at, absent_at, run_key) VALUES ($source, $item, $url, $title, $published, $hash, $parser, $text, 1, $at, $at, $at, NULL, $run);
                        """, ("$source", id), ("$item", item.ItemId), ("$url", item.Url), ("$title", item.Title), ("$published", (long?)item.PublishedAt),
                        ("$hash", hash), ("$parser", $"{parser}/{parserVersion}"), ("$text", texts.GetValueOrDefault(item.ItemId)), ("$at", now), ("$run", context.RunKey));
                    changes.Add(Change(item, "new", hash, texts.GetValueOrDefault(item.ItemId)));
                }
                else if (row.Hash != hash)
                {
                    store.Catalog.Run(transaction, """
                        UPDATE source_items SET url = $url, title = $title, published_at = $published, content_hash = $hash, parser = $parser, text_hash = $text,
                          version = version + 1, last_seen_at = $at, changed_at = $at, absent_at = NULL, run_key = $run WHERE source_id = $source AND item_id = $item;
                        """, ("$source", id), ("$item", item.ItemId), ("$url", item.Url), ("$title", item.Title), ("$published", (long?)item.PublishedAt),
                        ("$hash", hash), ("$parser", $"{parser}/{parserVersion}"), ("$text", texts.GetValueOrDefault(item.ItemId)), ("$at", now), ("$run", context.RunKey));
                    changes.Add(Change(item, "changed", hash, texts.GetValueOrDefault(item.ItemId)));
                }
                else
                {
                    // Seen before with the same content: a tombstoned item that came back is not announced again.
                    store.Catalog.Run(transaction, "UPDATE source_items SET last_seen_at = $at, absent_at = NULL WHERE source_id = $source AND item_id = $item;",
                        ("$source", id), ("$item", item.ItemId), ("$at", now));
                }
            }
            if (!notModified && complete)
            {
                var present = items.Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
                foreach (var row in rows.Values.Where(row => !row.Absent && !present.Contains(row.Id)))
                {
                    store.Catalog.Run(transaction, "UPDATE source_items SET absent_at = $at WHERE source_id = $source AND item_id = $item;",
                        ("$source", id), ("$item", row.Id), ("$at", now));
                    removed++;
                }
            }
            store.Catalog.Run(transaction, """
                UPDATE source_state SET content_hash = COALESCE($hash, content_hash), last_checked_at = $at, last_success_at = $at,
                  last_changed_at = CASE WHEN $changed THEN $at ELSE last_changed_at END, failures = 0, next_eligible_at = NULL, last_run_key = $run
                WHERE source_id = $id;
                """, ("$id", id), ("$hash", bodyHash), ("$at", now), ("$changed", changes.Count > 0 || removed > 0 ? 1L : 0L), ("$run", context.RunKey));
            if (!notModified && firstProvider is not null) store.Cache(transaction, firstProvider, start, identity, first, firstBlob);
            var newCount = changes.Count(change => Jobs.String((JsonObjectValue)change, "change") == "new");
            return CanonicalJson.Serialize(JsonValue.Object(
            [
                new("sourceId", id),
                new("status", changes.Count > 0 ? "changed" : "unchanged"),
                new("notModified", notModified),
                new("new", newCount),
                new("changed", changes.Count - newCount),
                new("removed", removed),
                new("seen", items.Count),
                new("complete", complete),
                new("provider", firstProvider is null ? JsonValue.Null : firstProvider),
                new("attempts", attempts),
                new("items", JsonValue.Array(changes)),
            ]));
        });
        return await WithEvidenceAsync(context, (JsonObjectValue)JsonValue.Parse(committed.Result)).ConfigureAwait(false);
    }

    private static JsonValue Change(SourceItemDraft item, string change, string hash, string? textHash) => JsonValue.Object(
    [
        new("itemId", item.ItemId),
        new("title", item.Title),
        new("url", item.Url is { } url ? url : JsonValue.Null),
        new("publishedAt", item.PublishedAt is { } published ? published : JsonValue.Null),
        new("change", change),
        new("contentHash", hash),
        new("textHash", textHash is null ? JsonValue.Null : textHash),
    ]);

    /// <summary>Keeps every changed item of a committed result as evidence of this session and adds the evidence IDs.</summary>
    private static async Task<JsonObjectValue> WithEvidenceAsync(JobContext context, JsonObjectValue result)
    {
        var items = new List<JsonValue>();
        foreach (var item in Jobs.Items(result, "items").OfType<JsonObjectValue>())
        {
            var evidence = await EvidenceAsync(context, Jobs.String(result, "sourceId")!, item).ConfigureAwait(false);
            items.Add(item.SetItem("evidenceId", evidence));
        }
        return result.SetItem("items", JsonValue.Array(items));
    }

    private static async Task<string> EvidenceAsync(JobContext context, string sourceId, JsonObjectValue item)
    {
        var text = Jobs.String(item, "textHash") is { } textHash && context.Host.Blobs.Read(textHash) is { } bytes ? Encoding.UTF8.GetString(bytes) : "";
        var title = Jobs.String(item, "title")!;
        var record = await context.SaveEvidenceAsync(new EvidenceDraft(title, Jobs.String(item, "url"), text.Length > 0 ? text : title,
            Parser: "source", ParserVersion: "1", SourceId: sourceId, ItemId: Jobs.String(item, "itemId"))).ConfigureAwait(false);
        return Jobs.String(record, "id")!;
    }

    /// <summary>The outcome of fetching one page.</summary>
    private sealed record PageFetch(RetrievalResponse? Response, string? Provider, string? Blob, int Attempts, string? Error = null, string? Failure = null,
        bool Transient = false, bool Limited = false, long? RetryAt = null);

    /// <summary>
    /// Fetches one page through the chain. Each attempt (at most <see cref="RetrievalLimits.MaxAttempts"/> per source and run) has the next
    /// operation ID; it reserves its estimate, writes a calling receipt, calls the provider, stores the body, and settles the receipt before
    /// the reservation. A rerun first replays the attempts it finds receipts for, in order, so it never re-decides them by the origins' current
    /// health: a page already fetched is reused, an interrupted free call is made again under its operation, and an interrupted paid call
    /// becomes uncertain and stays counted. New attempts skip providers whose origin is cooling down.
    /// </summary>
    private static async Task<PageFetch> FetchPageAsync(JobContext context, string sourceId, int page, Uri url, List<IRetrievalProvider> chain, string accept,
        string? authorization, string identity, int attempts)
    {
        var host = context.Host;
        var store = host.Sources;
        var limits = store.Options.Limits;
        var force = Flag(context.Input, "force");
        var origin = DirectRetrievalProvider.Origin(url);
        // A page this run already fetched (before a crash, or before a later page failed) is never fetched again.
        if (store.FetchedPage(context.RunKey, sourceId, page) is { } done)
        {
            if (host.Budgets.Get(done.OperationId) is { Status: "reserved" } open) host.Budgets.Settle(done.OperationId, open.Requested);
            return new PageFetch(new RetrievalResponse(done.HttpStatus ?? 200, new Uri(done.FinalUrl ?? done.Url), done.BlobHash is { } stored ? host.Blobs.Read(stored) ?? [] : [],
                done.ContentType, done.ETag, done.LastModified, null, done.Link), done.Provider, done.BlobHash, attempts);
        }
        string Operation(int attempt) => $"fetch:{context.RunKey}:{sourceId}:p{page}:a{attempt}";
        RetrievalRequest RequestFor(IRetrievalProvider provider)
        {
            var (etag, modified) = page == 0 ? store.Cached(provider.Id, url, identity) : (null, null);
            return new RetrievalRequest(url, accept, provider == host.DirectRetrieval ? authorization : null, etag, modified, limits);
        }
        long? retryAt = null;
        string? error = null;

        // Calls a provider under an operation; a transient failure cools the origin down and leaves the page unfetched (null).
        async Task<PageFetch?> CallAsync(IRetrievalProvider provider, string operationId)
        {
            var request = RequestFor(provider);
            var estimate = provider.Estimate(request);
            try
            {
                host.Budgets.Reserve(operationId, context.RunKey, provider.Id, estimate, context.Budget);
            }
            catch (HostLimitException limited)
            {
                return new PageFetch(null, null, null, attempts - 1, limited.Message, "limited", Limited: true);
            }
            store.BeginReceipt(operationId, sourceId, context.RunKey, provider.Id, url);
            try
            {
                var response = await provider.FetchAsync(request, context.Cancellation).ConfigureAwait(false);
                var blob = response.Body.Length > 0
                    ? host.Blobs.Put(response.Body, response.ContentType is { Length: > 0 and <= 200 } type ? type : "application/octet-stream", "fetch:" + operationId).Hash
                    : null;
                store.FinishReceipt(operationId, "fetched", response, blob, null);
                host.Budgets.Settle(operationId, response.Usage ?? estimate);
                store.Succeed(provider.Id, origin);
                return new PageFetch(response, provider.Id, blob, attempts);
            }
            catch (RetrievalException failed)
            {
                store.FinishReceipt(operationId, "failed", null, null, $"{failed.Failure}: {failed.Message}", failed.Status);
                if (failed.Failure == "blocked") host.Budgets.Release(operationId);
                else if (Paid(estimate)) host.Budgets.MarkUncertain(operationId, $"{failed.Failure}: {failed.Message}");
                else host.Budgets.Settle(operationId, estimate);
                error = $"{failed.Failure}: {failed.Message}";
                if (!failed.Transient) return new PageFetch(null, null, null, attempts, error, failed.Failure);
                var cooled = store.Fail(provider.Id, origin, failed);
                retryAt = Math.Min(retryAt ?? long.MaxValue, cooled.CooldownUntil ?? store.Now());
                return null;
            }
        }

        var tried = new HashSet<string>(StringComparer.Ordinal);
        while (attempts < limits.MaxAttempts && store.Receipt(Operation(attempts)) is { } receipt)
        {
            var operationId = Operation(attempts);
            attempts++;
            tried.Add(receipt.Provider);
            var provider = chain.FirstOrDefault(item => item.Id == receipt.Provider);
            if (receipt.Status == "calling")
            {
                if (provider is not null && !Paid(provider.Estimate(RequestFor(provider))))
                {
                    if (await CallAsync(provider, operationId).ConfigureAwait(false) is { } outcome) return outcome;
                    continue;
                }
                // A paid call whose outcome nobody knows: the attempt is spent, and its possible charge stays counted until reconciled.
                if (host.Budgets.Get(operationId) is { Status: "reserved" }) host.Budgets.MarkUncertain(operationId, "The device stopped during the call.");
                store.FinishReceipt(operationId, "uncertain", null, null, "uncertain: the device stopped during the call");
                error = "uncertain: the device stopped during the call";
                continue;
            }
            error = receipt.Error ?? receipt.Status;
            var failure = error.Split(':')[0].Trim();
            if (failure is not ("network" or "timeout" or "forbidden" or "rate_limited" or "server" or "uncertain"))
                return new PageFetch(null, null, null, attempts, error, failure);
        }
        foreach (var provider in chain.Where(provider => !tried.Contains(provider.Id)))
        {
            if (store.HealthOf(provider.Id, origin) is { } health && (force ? health.RetryAfterUntil : health.CooldownUntil) is { } until && until > store.Now())
            {
                retryAt = Math.Min(retryAt ?? long.MaxValue, until);
                error ??= $"{origin} is cooling down after {health.LastFailure}";
                continue;
            }
            if (attempts >= limits.MaxAttempts) break;
            var operationId = Operation(attempts);
            attempts++;
            if (await CallAsync(provider, operationId).ConfigureAwait(false) is { } outcome) return outcome;
        }
        if (retryAt is null && tried.Count > 0)
        {
            // Replayed failures cooled their origins when they happened; a replayed provider without a cooldown (an uncertain call) may be
            // tried again at once.
            foreach (var provider in tried)
                retryAt = Math.Min(retryAt ?? long.MaxValue, store.HealthOf(provider, origin)?.CooldownUntil ?? store.Now());
        }
        return new PageFetch(null, null, null, attempts, error ?? "Every provider is cooling down.", Transient: true, RetryAt: retryAt);
    }

    private static bool Paid(IReadOnlyDictionary<string, long> estimate) =>
        estimate.Keys.Any(meter => meter.StartsWith("money:", StringComparison.Ordinal) || meter.StartsWith("credits:", StringComparison.Ordinal));

    private static JsonObjectValue Failed(SourceStore store, JobContext context, string sourceId, int attempts, string failure, string message) =>
        Ended(store, context, sourceId, attempts, "failed", $"{failure}: {message}", null);

    private static JsonObjectValue Ended(SourceStore store, JobContext context, string sourceId, int attempts, string status, string error, long? nextEligibleAt)
    {
        store.RecordFailure(sourceId, context.RunKey, error, nextEligibleAt);
        return JsonValue.Object([new("sourceId", sourceId), new("status", status), new("attempts", attempts), new("error", error),
            new("nextEligibleAt", nextEligibleAt is { } next ? next : JsonValue.Null), new("items", JsonArrayValue.Empty)]);
    }

    /// <summary>The items to judge, in source then item order: the changed ones, or with <c>reevaluate</c> every current one.</summary>
    private static async Task<List<JsonValue>> ToJudgeAsync(JobContext context, List<JsonObjectValue> sources, JsonObjectValue results)
    {
        var max = (int)(Jobs.Long(context.Input, "maxJudgments") ?? 20);
        var reevaluate = Flag(context.Input, "reevaluate");
        var judge = new List<JsonValue>();
        foreach (var source in sources)
        {
            var id = Jobs.String(source, "id")!;
            var result = (JsonObjectValue)results[id];
            if (Jobs.String(result, "status") is not ("changed" or "unchanged")) continue;
            IEnumerable<JsonObjectValue> items = Jobs.Items(result, "items").OfType<JsonObjectValue>();
            if (reevaluate)
            {
                var store = context.Host.Sources;
                var current = store.Catalog.Read(_ => store.Catalog.Rows(null, """
                    SELECT item_id, title, url, published_at, content_hash, text_hash FROM source_items
                    WHERE source_id = $id AND absent_at IS NULL ORDER BY changed_at DESC, item_id LIMIT $limit;
                    """, reader => JsonValue.Object(
                    [
                        new("itemId", reader.GetString(0)), new("title", reader.GetString(1)), new("url", reader.IsDBNull(2) ? JsonValue.Null : reader.GetString(2)),
                        new("publishedAt", reader.IsDBNull(3) ? JsonValue.Null : reader.GetInt64(3)), new("change", "current"),
                        new("contentHash", reader.GetString(4)), new("textHash", reader.IsDBNull(5) ? JsonValue.Null : reader.GetString(5)),
                    ]), ("$id", id), ("$limit", (long)max)));
                var withEvidence = new List<JsonObjectValue>();
                foreach (var item in current) withEvidence.Add(item.SetItem("evidenceId", await EvidenceAsync(context, id, item).ConfigureAwait(false)));
                items = withEvidence;
            }
            foreach (var item in items)
            {
                if (judge.Count >= max) return judge;
                judge.Add(item.SetItem("sourceId", id));
            }
        }
        return judge;
    }

    private static string Summary(List<JsonObjectValue> sources, JsonObjectValue results)
    {
        if (sources.Count == 0) return "No sources to check";
        var counts = results.Values.OfType<JsonObjectValue>().GroupBy(result => Jobs.String(result, "status")!).Select(group => $"{group.Count()} {group.Key}");
        return $"{sources.Count} source(s): " + string.Join(", ", counts);
    }

    // ----- judge -----

    private static async Task<JobPhaseResult> JudgeAsync(JobContext context)
    {
        var policyId = Jobs.String(context.Input, "policyId")!;
        var version = (int?)Jobs.Long(context.Frozen, "policyVersion");
        var policy = context.Host.Policies.Resolve(policyId, version);
        var fields = JudgmentPolicy.Parse(policy.Spec, null).InputSchema is { } schema && schema.TryGetValue("properties", out var properties) &&
            properties is JsonObjectValue declared ? declared.Keys.ToHashSet(StringComparer.Ordinal) : null;
        var judgments = new List<JsonValue>();
        foreach (var item in Jobs.Items(context.Data, "judge").OfType<JsonObjectValue>())
        {
            var text = Jobs.String(item, "textHash") is { } textHash && context.Host.Blobs.Read(textHash) is { } bytes ? Encoding.UTF8.GetString(bytes) : "";
            var full = JsonValue.Object(
            [
                new("title", item["title"]),
                new("url", item.TryGetValue("url", out var url) && url is JsonStringValue ? url : ""),
                new("summary", text.Length <= 2000 ? text : text[..2000] + "…"),
                new("sourceId", item["sourceId"]),
                new("itemId", item["itemId"]),
                new("change", item["change"]),
            ]);
            if (item.TryGetValue("publishedAt", out var published) && published is JsonNumberValue) full = full.SetItem("publishedAt", published);
            var input = fields is null ? full : JsonValue.Object(full.Where(pair => fields.Contains(pair.Key)));
            var entry = JsonValue.Object([new("sourceId", item["sourceId"]), new("itemId", item["itemId"]), new("evidenceId", item["evidenceId"]),
                new("title", item["title"]), new("change", item["change"])]);
            try
            {
                var judgment = await context.JudgeAsync(policyId, input, [Jobs.String(item, "evidenceId")!], policy.Version).ConfigureAwait(false);
                var result = judgment.TryGetValue("result", out var given) && given is JsonObjectValue value ? value : JsonObjectValue.Empty;
                entry = entry.SetItem("judgmentId", judgment["id"]).SetItem("status", judgment["status"])
                    .SetItem("route", judgment.TryGetValue("route", out var route) ? route : JsonValue.Null)
                    .SetItem("label", result.TryGetValue("label", out var label) ? label : JsonValue.Null)
                    .SetItem("score", result.TryGetValue("score", out var score) ? score : JsonValue.Null)
                    .SetItem("confidence", result.TryGetValue("confidence", out var confidence) ? confidence : JsonValue.Null);
            }
            catch (HostValidationException invalid)
            {
                entry = entry.SetItem("status", "invalid").SetItem("error", invalid.Message);
            }
            catch (HostLimitException limited)
            {
                judgments.Add(entry.SetItem("status", "limited").SetItem("error", limited.Message));
                break;
            }
            judgments.Add(entry);
        }
        var judged = judgments.OfType<JsonObjectValue>().Count(entry => Jobs.String(entry, "status") == "judged");
        return JobPhaseResult.Next($"Judged {judged} of {judgments.Count} item(s)", context.Data.SetItem("judgments", JsonValue.Array(judgments)));
    }

    // ----- record -----

    private static async Task<JobPhaseResult> RecordAsync(JobContext context)
    {
        var results = context.Data.TryGetValue("results", out var given) && given is JsonObjectValue value ? value : JsonObjectValue.Empty;
        var order = Jobs.Items(context.Data, "order").OfType<JsonStringValue>().Select(id => id.Value).ToList();
        var judgments = Jobs.Items(context.Data, "judgments").OfType<JsonObjectValue>().GroupBy(entry => Jobs.String(entry, "evidenceId")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var titles = Jobs.Items(context.Frozen, "sources").OfType<JsonObjectValue>()
            .ToDictionary(source => Jobs.String(source, "id")!, source => Jobs.String((JsonObjectValue)source["spec"], "title") ?? Jobs.String(source, "id")!, StringComparer.Ordinal);
        var listed = new List<JsonValue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void List(string sourceId, JsonObjectValue item)
        {
            var evidenceId = Jobs.String(item, "evidenceId")!;
            if (listed.Count >= 100 || !seen.Add(evidenceId)) return;
            var note = $"{Jobs.String(item, "change")} · {titles.GetValueOrDefault(sourceId, sourceId)}: {Jobs.String(item, "title")}";
            var entry = JsonValue.Object([new("evidenceId", evidenceId)]);
            if (judgments.TryGetValue(evidenceId, out var judgment))
            {
                if (Jobs.String(judgment, "label") is { } label) note += $" — {label}";
                else if (judgment.TryGetValue("label", out var choice) && choice is not JsonNullValue) note += $" — {choice.ToJson()}";
                if (judgment.TryGetValue("score", out var score) && score is JsonNumberValue { Value: >= 0 and <= 1 }) entry = entry.SetItem("relevance", score);
                else if (judgment.TryGetValue("confidence", out var confidence) && confidence is JsonNumberValue { Value: >= 0 and <= 1 }) entry = entry.SetItem("relevance", confidence);
            }
            listed.Add(entry.SetItem("note", note.Length <= 1000 ? note : note[..999] + "…"));
        }
        foreach (var id in order)
            foreach (var item in Jobs.Items((JsonObjectValue)results[id], "items").OfType<JsonObjectValue>()) List(id, item);
        foreach (var judgment in judgments.Values) List(Jobs.String(judgment, "sourceId")!, judgment);

        var statuses = order.Select(id => Jobs.String((JsonObjectValue)results[id], "status")).ToList();
        var succeeded = statuses.Count(status => status is "changed" or "unchanged");
        var hard = statuses.Count(status => status is "failed" or "limited");
        await context.CheckAsync("sources", hard == 0, string.Join("; ", order.Select(id => $"{id}: {Jobs.String((JsonObjectValue)results[id], "status")}")))
            .ConfigureAwait(false);
        if (order.Count > 0 && succeeded == 0 && hard > 0)
            throw new InvalidOperationException("No source could be checked: " + string.Join("; ", order.Select(id => $"{id} {Jobs.String((JsonObjectValue)results[id], "error")}")));
        var newCount = order.Sum(id => Jobs.Long((JsonObjectValue)results[id], "new") ?? 0);
        var changedCount = order.Sum(id => Jobs.Long((JsonObjectValue)results[id], "changed") ?? 0);
        JsonValue artifactId = JsonValue.Null;
        if (listed.Count > 0)
        {
            var summary = $"{newCount} new and {changedCount} changed item(s) from {order.Count} source(s)" +
                (judgments.Count > 0 ? $"; {judgments.Values.Count(entry => Jobs.String(entry, "status") == "judged")} judged" : "");
            var artifact = await context.SaveArtifactAsync("evidence-list", $"Source check: {summary}", JsonValue.Object(
                [new("summary", summary), new("items", JsonValue.Array(listed))]), [.. seen],
                JsonValue.Object([new("sources", string.Join(", ", order) is { Length: > 200 } many ? many[..199] + "…" : string.Join(", ", order)),
                    new("policyId", Jobs.String(context.Input, "policyId") is { } policy ? policy : JsonValue.Null),
                    new("policyVersion", context.Frozen.TryGetValue("policyVersion", out var version) ? version : JsonValue.Null)])).ConfigureAwait(false);
            artifactId = artifact["id"];
        }
        return JobPhaseResult.Next(listed.Count > 0 ? $"Listed {listed.Count} item(s)" : "Nothing changed", JsonValue.Object(
        [
            new("sources", JsonValue.Array(order.Select(id => (JsonValue)((JsonObjectValue)results[id]).Remove("items")))),
            new("new", newCount),
            new("changed", changedCount),
            new("judged", judgments.Values.Count(entry => Jobs.String(entry, "status") == "judged")),
            new("artifactId", artifactId),
        ]));
    }

    private static string ItemHash(string parser, string version, SourceItemDraft item) =>
        CanonicalJson.HashText($"{parser}/{version}\n{item.Title}\n{item.Url}\n{item.Text}");

    private static bool Flag(JsonObjectValue value, string name) => value.TryGetValue(name, out var flag) && flag is JsonBooleanValue { Value: true };
}
