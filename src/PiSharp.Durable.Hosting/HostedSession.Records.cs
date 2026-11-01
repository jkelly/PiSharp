// PiSharp-only (decision 0010): the session's application records: judgments, artifacts, evidence, run usage and artifact actions.
using System.Globalization;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

public sealed partial class HostedSession
{
    /// <summary>A judgment record.</summary>
    /// <exception cref="HostNotFoundException">No such judgment in this session.</exception>
    public async Task<JsonObjectValue> JudgmentAsync(string judgmentId, CancellationToken cancellationToken = default) =>
        SessionRecords.Field(await GetHarness().SnapshotAsync(SessionRecords.JudgmentDoc, CheckRecordId(judgmentId), Context(cancellationToken)).ConfigureAwait(false),
            "judgment") ?? throw new HostNotFoundException($"No judgment {judgmentId} in {Name}.");

    /// <summary>
    /// A person's decision on a judgment waiting for review: <c>approve</c> or <c>reject</c>, appended to the judgment (the model's result is
    /// unchanged) and resuming its job. The same choice again does nothing; the other one, or a decision after the review expired or the
    /// run ended, conflicts.
    /// </summary>
    /// <exception cref="HostConflictException">The judgment does not wait for this decision.</exception>
    public async Task<JsonObjectValue> DecideJudgmentAsync(string judgmentId, string choice, string actor, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (choice is not ("approve" or "reject")) throw new HostInvalidException("A decision is approve or reject.");
        var judgment = await JudgmentAsync(judgmentId, cancellationToken).ConfigureAwait(false);
        var decided = Jobs.String(judgment, "decision");
        var wanted = choice == "approve" ? "approved" : "rejected";
        if (decided == wanted) return judgment;
        if (decided is not "pending")
            throw new HostConflictException(decided is null ? $"Judgment {judgmentId} does not wait for a decision." : $"Judgment {judgmentId} was already {decided}.");
        var agentId = Jobs.Long(judgment, "conversationId")!.Value;
        var taskId = Jobs.Long(judgment, "runTaskId")!.Value;
        try
        {
            await DecideJobAsync(agentId, taskId, choice == "approve", actor, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (AppStateConflictException conflict)
        {
            throw new HostConflictException(conflict.Message);
        }
        return await JudgmentAsync(judgmentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An artifact envelope.</summary>
    /// <exception cref="HostNotFoundException">No such artifact in this session.</exception>
    public async Task<JsonObjectValue> ArtifactAsync(string artifactId, CancellationToken cancellationToken = default) =>
        SessionRecords.Field(await GetHarness().SnapshotAsync(SessionRecords.ArtifactDoc, CheckRecordId(artifactId), Context(cancellationToken)).ConfigureAwait(false),
            "artifact") ?? throw new HostNotFoundException($"No artifact {artifactId} in {Name}.");

    /// <summary>A profile's current feedback on an artifact of this session; see <see cref="PreferenceStore.Feedback"/>.</summary>
    /// <exception cref="HostNotFoundException">No such artifact in this session.</exception>
    public async Task<JsonObjectValue> FeedbackAsync(string artifactId, string? profileId, CancellationToken cancellationToken = default)
    {
        var artifact = await ArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        return host.Preferences.Feedback(profileId ?? PreferenceStore.DefaultProfile, Id, Jobs.String(artifact, "id")!);
    }

    /// <summary>
    /// Records feedback on an artifact of this session in a preference profile (decision 0010, M5); see <see cref="PreferenceStore.Record"/>.
    /// The target is resolved here and the actor is the caller's credential. A <c>more</c> or <c>less</c> without labels is about the
    /// artifact's judged label and its tags.
    /// </summary>
    /// <exception cref="HostNotFoundException">No such artifact in this session.</exception>
    public async Task<(JsonObjectValue Event, JsonObjectValue Profile, bool Replayed)> RecordFeedbackAsync(string artifactId, string? profileId, string requestId,
        string action, IReadOnlyList<string>? labels, string? note, bool example, string? reverses, string actor, CancellationToken cancellationToken = default)
    {
        var artifact = await ArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        var about = labels ?? [];
        if (about.Count == 0 && action is "more" or "less")
        {
            var derived = new List<string>();
            if (artifact.TryGetValue("provenance", out var provenance) && provenance is JsonObjectValue origin && Jobs.String(origin, "judgmentId") is { } judgmentId)
            {
                try
                {
                    if ((await JudgmentAsync(judgmentId, cancellationToken).ConfigureAwait(false)).TryGetValue("result", out var result) && result is JsonObjectValue judged &&
                        Jobs.String(judged, "label") is { } label)
                        derived.Add(label);
                }
                catch (HostNotFoundException) { /* A judgment of another session; the artifact's tags still say what it is about. */ }
            }
            if (artifact.TryGetValue("props", out var props) && props is JsonObjectValue values)
                derived.AddRange(Jobs.Items(values, "tags").OfType<JsonStringValue>().Select(tag => tag.Value.Trim().ToLowerInvariant()));
            about = [.. derived.Where(PreferenceStore.IsLabel).Distinct(StringComparer.Ordinal).Take(PreferenceStore.MaxLabels)];
        }
        return host.Preferences.Record(new FeedbackRequest(profileId ?? PreferenceStore.DefaultProfile, requestId, action)
        {
            SessionId = Id,
            ArtifactId = Jobs.String(artifact, "id")!,
            Title = Jobs.String(artifact, "title"),
            Labels = about,
            Note = note,
            Example = example,
            Reverses = reverses,
            Actor = actor,
        });
    }

    /// <summary>An evidence record.</summary>
    /// <exception cref="HostNotFoundException">No such evidence in this session.</exception>
    public async Task<JsonObjectValue> EvidenceAsync(string evidenceId, CancellationToken cancellationToken = default) =>
        SessionRecords.Field(await GetHarness().SnapshotAsync(SessionRecords.EvidenceDoc, CheckRecordId(evidenceId), Context(cancellationToken)).ConfigureAwait(false),
            "evidence") ?? throw new HostNotFoundException($"No evidence {evidenceId} in {Name}.");

    /// <summary>The full text of evidence: its blob when it has one, else its excerpt.</summary>
    public async Task<string> EvidenceTextAsync(string evidenceId, CancellationToken cancellationToken = default)
    {
        var evidence = await EvidenceAsync(evidenceId, cancellationToken).ConfigureAwait(false);
        if (Jobs.String(evidence, "blobHash") is { } hash && host.Blobs.Read(hash) is { } bytes) return System.Text.Encoding.UTF8.GetString(bytes);
        return Jobs.String(evidence, "excerpt") ?? "";
    }

    /// <summary>Keeps content as evidence of this session, outside any job; the same URL and content is one record.</summary>
    public async Task<JsonObjectValue> AddEvidenceAsync(EvidenceDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Evidence.Check(draft);
        var hash = Evidence.HashOf(draft);
        var blob = Evidence.StoreBody(host, Id, draft, hash);
        var now = host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return await GetHarness().CommitAsync(tx => Evidence.WriteAsync(tx, Id, null, draft, hash, blob, now), Context(cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The newest record IDs of one kind (<c>artifacts</c>, <c>judgments</c> or <c>evidence</c>) with the records, newest first:
    /// <c>{items, next}</c>; <paramref name="cursor"/> is the opaque <c>next</c> of the previous page.</summary>
    public async Task<JsonObjectValue> RecordsAsync(string kind, int limit = 20, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (kind is not ("artifacts" or "judgments" or "evidence")) throw new HostInvalidException("Records are artifacts, judgments or evidence.");
        if (limit is < 1 or > 100) throw new HostInvalidException("Use a limit of 1–100.");
        var offset = 0;
        if (cursor is { Length: > 0 } && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
            throw new HostInvalidException("Invalid cursor.");
        var context = Context(cancellationToken);
        var index = await GetHarness().SnapshotAsync(SessionRecords.IndexDoc, context).ConfigureAwait(false);
        var ids = Jobs.Items(index, kind).OfType<JsonStringValue>().Select(id => id.Value).ToList();
        var token = kind switch { "artifacts" => SessionRecords.ArtifactDoc, "judgments" => SessionRecords.JudgmentDoc, _ => SessionRecords.EvidenceDoc };
        var field = kind switch { "artifacts" => "artifact", "judgments" => "judgment", _ => "evidence" };
        var items = new List<JsonValue>();
        foreach (var id in ids.Skip(offset).Take(limit))
            if (SessionRecords.Field(await GetHarness().SnapshotAsync(token, id, context).ConfigureAwait(false), field) is { } record) items.Add(record);
        var more = offset + limit < ids.Count;
        return JsonValue.Object([new("items", JsonValue.Array(items)),
            new("next", more ? (offset + limit).ToString(CultureInfo.InvariantCulture) : JsonValue.Null)]);
    }

    /// <summary>The artifacts of an execution, in the order they were saved.</summary>
    public async Task<JsonArrayValue> ExecutionArtifactsAsync(long taskId, CancellationToken cancellationToken = default)
    {
        var execution = await ExecutionAsync(taskId, cancellationToken).ConfigureAwait(false);
        var run = execution.TryGetValue("run", out var value) ? value as JsonObjectValue : null;
        var items = new List<JsonValue>();
        foreach (var reference in Jobs.Items(run, "artifacts").OfType<JsonObjectValue>())
            if (SessionRecords.Field(await GetHarness().SnapshotAsync(SessionRecords.ArtifactDoc, Jobs.String(reference, "id")!, Context(cancellationToken))
                .ConfigureAwait(false), "artifact") is { } artifact) items.Add(artifact);
        return JsonValue.Array(items);
    }

    /// <summary>An execution's ledger: its reservations, totals and whether every cost is known.</summary>
    public async Task<JsonObjectValue> ExecutionUsageAsync(long taskId, CancellationToken cancellationToken = default)
    {
        var execution = await ExecutionAsync(taskId, cancellationToken).ConfigureAwait(false);
        return host.Budgets.Usage(RunKey(taskId)).SetItem("recorded", execution["usage"]);
    }

    /// <summary>
    /// Runs the action an <c>action-request</c> artifact offers, after the person confirmed it: <c>reevaluate</c> starts a judgment of the
    /// item the artifact's judgment evaluated; <c>start-research</c> starts research. Rendering an artifact never does this.
    /// </summary>
    /// <exception cref="HostInvalidException">The artifact offers no such action, or <paramref name="confirmed"/> is false.</exception>
    public async Task<ExecutionStart> InvokeArtifactActionAsync(string artifactId, string actionId, bool confirmed, string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed) throw new HostInvalidException("An artifact action needs confirm: true.");
        var artifact = await ArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        var props = (JsonObjectValue)artifact["props"];
        if (Jobs.String(artifact, "kind") != "action-request" || Jobs.String(props, "action") != actionId)
            throw new HostInvalidException($"Artifact {artifactId} does not offer {actionId}.");
        var input = artifact.TryGetValue("actionInput", out var given) && given is JsonObjectValue stored ? stored : JsonObjectValue.Empty;
        var agentId = Jobs.Long(artifact, "conversationId")!.Value;
        switch (actionId)
        {
            case "reevaluate":
                var judgmentId = artifact.TryGetValue("provenance", out var provenance) && provenance is JsonObjectValue origin ? Jobs.String(origin, "judgmentId") : null;
                if (judgmentId is null) throw new HostInvalidException($"Artifact {artifactId} has no judgment to evaluate again.");
                var judgment = await JudgmentAsync(judgmentId, cancellationToken).ConfigureAwait(false);
                var request = JsonValue.Object([new("policyId", input["policyId"]), new("input", judgment["input"]), new("evidenceIds", judgment["evidenceIds"])]);
                if (input.TryGetValue("version", out var version)) request = request.SetItem("version", version);
                return await StartExecutionAsync(agentId, ApplicationJobDefinitions.Judge, request, null, idempotencyKey, cancellationToken).ConfigureAwait(false);
            default:
                var job = host.ApplicationJobs.FirstOrDefault(item => item.Id == ApplicationJobDefinitions.Prefix + "research")
                    ?? throw new HostInvalidException($"This device cannot run {actionId}.");
                return await StartExecutionAsync(agentId, job.Id, input, null, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }
    }

    internal string RunKey(long taskId) => Id + "/" + taskId.ToString(CultureInfo.InvariantCulture);

    private static string CheckRecordId(string id) =>
        id is { Length: > 0 and <= 100 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? id : throw new HostNotFoundException($"No record {id}.");
}
