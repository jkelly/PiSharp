// PiSharp-only (decision 0010). The application routes: capabilities, budgets and usage, judgment policies, judgments, artifacts,
// evidence, sources, research, feedback and preference profiles. Every call needs the administrative device credential; check DeviceInfo.Supports before offering a feature.
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace PiSharp.Durable.Client;

public sealed partial class DurableDeviceClient
{
    /// <summary>The device's capability manifest: features, versions and their bindings.</summary>
    public Task<JsonNode?> CapabilitiesAsync(CancellationToken cancellationToken = default) => GetAsync("/v1/capabilities", cancellationToken: cancellationToken);

    // ─── Budgets ────────────────────────────────────────────────────────────

    /// <summary>Today's limits and use with the active reservations: <c>{day, limits, used, active, uncertain, reservations}</c>.</summary>
    public Task<JsonNode?> BudgetsAsync(CancellationToken cancellationToken = default) => GetAsync("/v1/budgets", cancellationToken: cancellationToken);

    /// <summary>Changes daily limits (<c>{meter: limit | null}</c>, null removes one) at <paramref name="expectedRevision"/>.</summary>
    public Task<JsonNode?> UpdateBudgetsAsync(JsonObject perDay, long expectedRevision, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, "/v1/budgets", new JsonObject { ["perDay"] = perDay.DeepClone(), ["expectedRevision"] = expectedRevision },
            cancellationToken: cancellationToken);

    /// <summary>What a retention purge would remove now, and what it keeps (a dry run; <c>GET /v1/retention</c>).</summary>
    public Task<JsonNode?> RetentionAsync(CancellationToken cancellationToken = default) => GetAsync("/v1/retention", cancellationToken: cancellationToken);

    /// <summary>Removes up to <paramref name="batch"/> records per category past their retention, then collects unreferenced blobs; the
    /// report's <c>more</c> says whether to call again.</summary>
    public Task<JsonNode?> PurgeRetentionAsync(int batch = 500, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/v1/retention", new JsonObject { ["batch"] = batch }, cancellationToken: cancellationToken);

    /// <summary>Reconciles an uncertain reservation as <c>settled</c> (with the actual amounts when known) or <c>released</c>.</summary>
    public Task<JsonNode?> ResolveReservationAsync(string operationId, string outcome, JsonObject? actual = null, string? detail = null,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["outcome"] = outcome };
        if (actual is not null) body["actual"] = actual.DeepClone();
        if (detail is not null) body["detail"] = detail;
        return SendAsync(HttpMethod.Post, $"/v1/budgets/reservations/{Uri.EscapeDataString(operationId)}/resolve", body, cancellationToken: cancellationToken);
    }

    /// <summary>An execution's ledger: reservations, totals, whether every cost is known, and the recorded model usage.</summary>
    public Task<JsonNode?> ExecutionUsageAsync(string session, long taskId, CancellationToken cancellationToken = default) =>
        GetAsync($"{Session(session)}/executions/{Number(taskId)}/usage", cancellationToken: cancellationToken);

    // ─── Policies ───────────────────────────────────────────────────────────

    /// <summary>The judgment policies: <c>{items, next}</c>.</summary>
    public Task<JsonNode?> PoliciesAsync(CancellationToken cancellationToken = default) => GetAsync("/v1/policies", cancellationToken: cancellationToken);

    /// <summary>A policy's summary, its versions and its active version's spec.</summary>
    public Task<JsonNode?> PolicyAsync(string policyId, CancellationToken cancellationToken = default) =>
        GetAsync($"/v1/policies/{Uri.EscapeDataString(policyId)}", cancellationToken: cancellationToken);

    /// <summary>One immutable policy version.</summary>
    public Task<JsonNode?> PolicyVersionAsync(string policyId, int version, CancellationToken cancellationToken = default) =>
        GetAsync($"/v1/policies/{Uri.EscapeDataString(policyId)}/versions/{Number(version)}", cancellationToken: cancellationToken);

    /// <summary>Publishes <paramref name="spec"/> as the policy's next version; the first version becomes active. An identical spec returns
    /// the latest version.</summary>
    public Task<JsonNode?> PublishPolicyAsync(string policyId, JsonObject spec, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/v1/policies/{Uri.EscapeDataString(policyId)}/versions", spec, idempotencyKey, cancellationToken: cancellationToken);

    /// <summary>Makes <paramref name="version"/> the active one; a stale <paramref name="expectedRevision"/> conflicts.</summary>
    public Task<JsonNode?> ActivatePolicyAsync(string policyId, int version, long expectedRevision, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"/v1/policies/{Uri.EscapeDataString(policyId)}/active",
            new JsonObject { ["version"] = version, ["expectedRevision"] = expectedRevision }, cancellationToken: cancellationToken);

    // ─── Judgments ──────────────────────────────────────────────────────────

    /// <summary>Starts a judgment from <c>{agentId?, policyId, version?, title?, input, evidenceIds?, budget?}</c>:
    /// <c>{sessionId, agentId, taskId, executionUrl, judgmentId}</c>.</summary>
    public Task<JsonNode?> StartJudgmentAsync(string session, JsonObject request, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{Session(session)}/judgments", request, idempotencyKey, cancellationToken: cancellationToken);

    /// <summary>The session's judgments, newest first: <c>{items, next}</c>.</summary>
    public Task<JsonNode?> JudgmentsAsync(string session, int limit = 20, string? cursor = null, CancellationToken cancellationToken = default) =>
        GetAsync($"{Session(session)}/judgments{Page(limit, cursor)}", cancellationToken: cancellationToken);

    /// <summary>A judgment record: the model's result, its route and the people's decisions.</summary>
    public Task<JsonNode?> JudgmentAsync(string session, string judgmentId, CancellationToken cancellationToken = default) =>
        GetAsync($"{Session(session)}/judgments/{Uri.EscapeDataString(judgmentId)}", cancellationToken: cancellationToken);

    /// <summary>Decides a judgment waiting for review: <c>approve</c> or <c>reject</c>.</summary>
    public Task<JsonNode?> DecideJudgmentAsync(string session, string judgmentId, string choice, string? reason = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["choice"] = choice };
        if (reason is not null) body["reason"] = reason;
        return SendAsync(HttpMethod.Post, $"{Session(session)}/judgments/{Uri.EscapeDataString(judgmentId)}/decision", body, cancellationToken: cancellationToken);
    }

    // ─── Artifacts and evidence ─────────────────────────────────────────────

    /// <summary>The artifacts an execution saved, in order: <c>{items}</c>.</summary>
    public Task<JsonNode?> ExecutionArtifactsAsync(string session, long taskId, CancellationToken cancellationToken = default) =>
        GetAsync($"{Session(session)}/executions/{Number(taskId)}/artifacts", cancellationToken: cancellationToken);

    /// <summary>The session's artifacts, newest first: <c>{items, next}</c>.</summary>
    public Task<JsonNode?> ArtifactsAsync(string session, int limit = 20, string? cursor = null, CancellationToken cancellationToken = default) =>
        GetAsync($"{Session(session)}/artifacts{Page(limit, cursor)}", cancellationToken: cancellationToken);

    /// <summary>An artifact envelope.</summary>
    public Task<JsonNode?> ArtifactAsync(string session, string artifactId, CancellationToken cancellationToken = default) =>
        GetAsync($"{Session(session)}/artifacts/{Uri.EscapeDataString(artifactId)}", cancellationToken: cancellationToken);

    /// <summary>An artifact with its evidence, secret references redacted.</summary>
    public Task<JsonNode?> ExportArtifactAsync(string session, string artifactId, CancellationToken cancellationToken = default) =>
        GetAsync($"{Session(session)}/artifacts/{Uri.EscapeDataString(artifactId)}/export", cancellationToken: cancellationToken);

    /// <summary>An artifact as plain text.</summary>
    public Task<string> ArtifactTextAsync(string session, string artifactId, CancellationToken cancellationToken = default) =>
        TextAsync($"{Session(session)}/artifacts/{Uri.EscapeDataString(artifactId)}/export?format=text", cancellationToken);

    /// <summary>Runs the action an action-request artifact offers, after the person confirmed it. The key makes a retry return the same
    /// execution.</summary>
    public Task<JsonNode?> InvokeArtifactActionAsync(string session, string artifactId, string action, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        return SendAsync(HttpMethod.Post, $"{Session(session)}/artifacts/{Uri.EscapeDataString(artifactId)}/actions/{Uri.EscapeDataString(action)}",
            new JsonObject { ["confirm"] = true }, idempotencyKey, cancellationToken: cancellationToken);
    }

    /// <summary>The session's evidence, newest first: <c>{items, next}</c>.</summary>
    public Task<JsonNode?> EvidenceListAsync(string session, int limit = 20, string? cursor = null, CancellationToken cancellationToken = default) =>
        GetAsync($"{Session(session)}/evidence{Page(limit, cursor)}", cancellationToken: cancellationToken);

    /// <summary>Keeps <c>{title, url?, text, contentType?}</c> as evidence; the same URL and text is one record.</summary>
    public Task<JsonNode?> AddEvidenceAsync(string session, JsonObject evidence, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{Session(session)}/evidence", evidence, cancellationToken: cancellationToken);

    /// <summary>An evidence record.</summary>
    public Task<JsonNode?> EvidenceAsync(string session, string evidenceId, CancellationToken cancellationToken = default) =>
        GetAsync($"{Session(session)}/evidence/{Uri.EscapeDataString(evidenceId)}", cancellationToken: cancellationToken);

    /// <summary>The full text of evidence.</summary>
    public Task<string> EvidenceTextAsync(string session, string evidenceId, CancellationToken cancellationToken = default) =>
        TextAsync($"{Session(session)}/evidence/{Uri.EscapeDataString(evidenceId)}/text", cancellationToken);

    // ─── Sources ────────────────────────────────────────────────────────────

    /// <summary>The device's sources with their state: <c>{items, next}</c>.</summary>
    public Task<JsonNode?> SourcesAsync(CancellationToken cancellationToken = default) => GetAsync("/v1/sources", cancellationToken: cancellationToken);

    /// <summary>Creates a source from <c>{id, kind, url | repository, title?, enabled?, maxItems?, maxPages?, includePrereleases?,
    /// credentialRef?, allowFallback?}</c>.</summary>
    public Task<JsonNode?> CreateSourceAsync(JsonObject source, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/v1/sources", source, idempotencyKey, cancellationToken: cancellationToken);

    /// <summary>A source with its state, its origin's health and its recent fetch receipts.</summary>
    public Task<JsonNode?> SourceAsync(string sourceId, CancellationToken cancellationToken = default) =>
        GetAsync($"/v1/sources/{Uri.EscapeDataString(sourceId)}", cancellationToken: cancellationToken);

    /// <summary>Merges <paramref name="changes"/> into a source (a null removes an optional field) at <paramref name="expectedRevision"/>.</summary>
    public Task<JsonNode?> UpdateSourceAsync(string sourceId, JsonObject changes, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var body = (JsonObject)changes.DeepClone();
        body["expectedRevision"] = expectedRevision;
        return SendAsync(HttpMethod.Patch, $"/v1/sources/{Uri.EscapeDataString(sourceId)}", body, cancellationToken: cancellationToken);
    }

    /// <summary>Deletes a source at <paramref name="expectedRevision"/>; its items stay, so recreating it announces nothing old.</summary>
    public Task<JsonNode?> DeleteSourceAsync(string sourceId, long expectedRevision, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"/v1/sources/{Uri.EscapeDataString(sourceId)}?expectedRevision={Number(expectedRevision)}", null,
            cancellationToken: cancellationToken);

    /// <summary>A source's items, most recently changed first: <c>{items, next}</c>.</summary>
    public Task<JsonNode?> SourceItemsAsync(string sourceId, int limit = 50, string? cursor = null, CancellationToken cancellationToken = default) =>
        GetAsync($"/v1/sources/{Uri.EscapeDataString(sourceId)}/items{Page(limit, cursor)}", cancellationToken: cancellationToken);

    /// <summary>Every provider and origin that is failing: <c>{items, next}</c>.</summary>
    public Task<JsonNode?> RetrievalHealthAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/v1/retrieval/health", cancellationToken: cancellationToken);

    /// <summary>Starts a source check from <c>{agentId?, sourceIds?, policyId?, version?, reevaluate?, force?, maxJudgments?, budget?}</c>:
    /// <c>{sessionId, agentId, taskId, executionUrl}</c>.</summary>
    public Task<JsonNode?> StartSourceCheckAsync(string session, JsonObject request, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{Session(session)}/source-checks", request, idempotencyKey, cancellationToken: cancellationToken);

    /// <summary>Starts supervised research from <c>{agentId?, question, angles?, sourceIds?, evidenceIds?, research?, synthesis?, concurrency?,
    /// requireAll?, maxAttempts?, angleDeadlineSeconds?, retrieval?, policyId?, version?, budget?}</c>: <c>{sessionId, agentId, taskId, executionUrl}</c>.
    /// <c>retrieval</c> is <c>{maxFetches?, origins?}</c>: how many pages each angle may fetch (0 to 10, default 3) from the URLs its material
    /// lists, and origins any page of which it may fetch as well. The execution's run lists each angle's progress under <c>branches</c>.</summary>
    public Task<JsonNode?> StartResearchAsync(string session, JsonObject request, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"{Session(session)}/research", request, idempotencyKey, cancellationToken: cancellationToken);

    // ─── Feedback and preferences ───────────────────────────────────────────

    /// <summary>Records feedback on an artifact from <c>{action, profileId?, labels?, note?, example?, reverses?}</c>; the action is more, less,
    /// dismiss, label or undo. <paramref name="requestId"/> makes a retry return the same event: <c>{event, profile}</c>.</summary>
    public Task<JsonNode?> SendFeedbackAsync(string session, string artifactId, JsonObject feedback, string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        return SendAsync(HttpMethod.Post, $"{Session(session)}/artifacts/{Uri.EscapeDataString(artifactId)}/feedback", feedback, requestId,
            cancellationToken: cancellationToken);
    }

    /// <summary>A profile's current feedback on an artifact: <c>{profileId, revision, target, vote, labels, example, undo}</c>.</summary>
    public Task<JsonNode?> ArtifactFeedbackAsync(string session, string artifactId, string? profileId = null, CancellationToken cancellationToken = default) =>
        GetAsync($"{Session(session)}/artifacts/{Uri.EscapeDataString(artifactId)}/feedback"
            + (profileId is null ? "" : "?profileId=" + Uri.EscapeDataString(profileId)), cancellationToken: cancellationToken);

    /// <summary>The preference profiles: <c>{items, next}</c>.</summary>
    public Task<JsonNode?> PreferencesAsync(CancellationToken cancellationToken = default) => GetAsync("/v1/preferences", cancellationToken: cancellationToken);

    /// <summary>A preference profile: its revision, settings, label weights and examples.</summary>
    public Task<JsonNode?> PreferenceAsync(string profileId, CancellationToken cancellationToken = default) =>
        GetAsync($"/v1/preferences/{Uri.EscapeDataString(profileId)}", cancellationToken: cancellationToken);

    /// <summary>Changes a profile's settings and forgets examples (by feedback event ID) at <paramref name="expectedRevision"/>.</summary>
    public Task<JsonNode?> UpdatePreferenceAsync(string profileId, JsonObject? settings, IReadOnlyList<string>? forgetExamples, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["expectedRevision"] = expectedRevision };
        if (settings is not null) body["settings"] = settings.DeepClone();
        if (forgetExamples is { Count: > 0 }) body["forgetExamples"] = new JsonArray([.. forgetExamples.Select(id => (JsonNode?)id)]);
        return SendAsync(HttpMethod.Patch, $"/v1/preferences/{Uri.EscapeDataString(profileId)}", body, cancellationToken: cancellationToken);
    }

    /// <summary>Starts a profile over at <paramref name="expectedRevision"/>; its history stays for export.</summary>
    public Task<JsonNode?> ResetPreferenceAsync(string profileId, long expectedRevision, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/v1/preferences/{Uri.EscapeDataString(profileId)}/reset", new JsonObject { ["expectedRevision"] = expectedRevision },
            cancellationToken: cancellationToken);

    /// <summary>A profile with every feedback event: <c>{profile, events, exportedAt}</c>.</summary>
    public Task<JsonNode?> ExportPreferenceAsync(string profileId, CancellationToken cancellationToken = default) =>
        GetAsync($"/v1/preferences/{Uri.EscapeDataString(profileId)}/export", cancellationToken: cancellationToken);

    /// <summary>Deletes a profile and its feedback at <paramref name="expectedRevision"/>.</summary>
    public Task<JsonNode?> DeletePreferenceAsync(string profileId, long expectedRevision, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, $"/v1/preferences/{Uri.EscapeDataString(profileId)}?expectedRevision={Number(expectedRevision)}", null,
            cancellationToken: cancellationToken);

    private static string Session(string session) => "/v1/sessions/" + Uri.EscapeDataString(session);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Page(int limit, string? cursor) =>
        "?limit=" + Number(limit) + (cursor is { Length: > 0 } ? "&cursor=" + Uri.EscapeDataString(cursor) : "");

    private async Task<string> TextAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, text);
            return text;
        }
        catch (HttpRequestException failure)
        {
            throw new DurableApiException(DurableErrorCodes.Offline, $"{BaseUrl} is unreachable: {failure.Message}", 0, failure);
        }
    }
}
