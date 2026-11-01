// The release watcher (decision 0010, M5). It is an ordinary client: every step is a public v1 route through DurableDeviceClient, with the
// administrative credential, except the scheduled tick, which a trigger credential restricted to the watcher's schedule makes. The device
// does the durable work:
//   pisharp-source-check (scheduled)  fetches the sources, dedupes items by content hash, keeps new and changed ones as evidence, lists them;
//   pisharp-judge (one per release)   judges a release under the policy, waits for a person when its confidence asks for one, keeps a finding;
//   pisharp-research (risky releases) researches an approved release from its evidence and keeps a finding per angle and a synthesis.
// The watcher starts each judgment with the release's evidence ID as its Idempotency-Key. Evidence is content addressed, so an unchanged
// release, a repeated pass or a pass after a restart replays the first judgment instead of starting another; a changed release has new
// evidence and is judged again. Research is keyed by its judgment the same way.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Durable.Client;

namespace PiSharp.Durable.ReleaseWatcher;

/// <summary>What setup found or made.</summary>
public sealed record WatcherSetup(string SessionId, string ScheduleId, int PolicyVersion, string? TriggerToken);

/// <summary>What one processing pass did.</summary>
public sealed record WatcherPass(int Releases, int Judged, int Replayed, int Pending, int Researched, int ResearchReplayed);

/// <summary>The release watcher over a device's public API.</summary>
public sealed class ReleaseWatcher(DurableDeviceClient device, WatcherConfig config, TextWriter log)
{
    /// <summary>The source check job's ID.</summary>
    public const string SourceCheckJob = "pisharp-source-check";

    /// <summary>The judgment job's ID.</summary>
    public const string JudgeJob = "pisharp-judge";

    /// <summary>The research job's ID.</summary>
    public const string ResearchJob = "pisharp-research";

    private string? session;

    /// <summary>
    /// Makes the watcher's pieces, or finds them: the sources (<c>POST /v1/sources</c>), the policy (<c>POST /v1/policies/{id}/versions</c>
    /// and <c>PUT .../active</c> when the configured spec is not active), the session (<c>POST /v1/sessions</c> with an Idempotency-Key, so
    /// the same configuration finds the same session), and the source check schedule (<c>POST /v1/schedules</c>). With
    /// <paramref name="issueToken"/> it also issues a trigger credential restricted to that schedule; its token is shown once.
    /// </summary>
    public async Task<WatcherSetup> SetupAsync(bool issueToken, CancellationToken cancellationToken = default)
    {
        var info = await device.DeviceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var feature in new[] { "sources", "policies", "judgments", "research", "feedback" })
            if (!info.Supports(feature)) throw new InvalidOperationException($"The device does not offer {feature}; update its PiSharp Durable host.");

        foreach (var source in config.Sources)
        {
            var id = source["id"]!.GetValue<string>();
            try
            {
                await device.SourceAsync(id, cancellationToken).ConfigureAwait(false);
                log.WriteLine($"Source {id}: exists");
            }
            catch (DurableApiException missing) when (missing.Code == DurableErrorCodes.NotFound)
            {
                await device.CreateSourceAsync(source, "watch-source:" + id, cancellationToken).ConfigureAwait(false);
                log.WriteLine($"Source {id}: created");
            }
        }

        var version = await PolicyAsync(cancellationToken).ConfigureAwait(false);
        var sessionId = await SessionAsync(cancellationToken).ConfigureAwait(false);
        var schedules = (await device.SchedulesAsync(cancellationToken).ConfigureAwait(false))!.AsArray().OfType<JsonObject>();
        var schedule = schedules.FirstOrDefault(item => Text(item, "sessionId") == sessionId && Text(item, "jobId") == SourceCheckJob);
        if (schedule is null)
        {
            schedule = (await device.CreateScheduleAsync(new JsonObject
            {
                ["sessionId"] = sessionId,
                ["jobId"] = SourceCheckJob,
                ["everyMinutes"] = config.EveryMinutes,
                ["input"] = new JsonObject { ["sourceIds"] = new JsonArray([.. config.Sources.Select(source => (JsonNode?)source["id"]!.GetValue<string>())]) },
            }, cancellationToken).ConfigureAwait(false))!.AsObject();
            log.WriteLine($"Schedule {Text(schedule, "id")}: a source check every {config.EveryMinutes} minute(s)");
        }
        var scheduleId = Text(schedule, "id")!;
        string? token = null;
        if (issueToken)
        {
            var issued = await device.SendAsync(HttpMethod.Post, "/v1/trigger-credentials",
                new JsonObject { ["label"] = "Release watcher", ["scheduleId"] = scheduleId, ["expiresInHours"] = 24 * 30 }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            token = issued!["token"]!.GetValue<string>();
            log.WriteLine($"Trigger credential {issued["credential"]!["id"]}: restricted to schedule {scheduleId}");
        }
        return new WatcherSetup(sessionId, scheduleId, version, token);
    }

    /// <summary>
    /// One pass: reads the recent source checks' evidence lists (<c>GET .../executions</c>, <c>.../artifacts</c>, <c>.../evidence/{id}</c>),
    /// starts a judgment for each release not judged yet (<c>POST .../judgments</c>, keyed by the evidence ID), and starts research for each
    /// approved judgment whose label is configured for it (<c>POST .../research</c>, keyed by the judgment ID).
    /// </summary>
    public async Task<WatcherPass> ProcessAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = await SessionAsync(cancellationToken).ConfigureAwait(false);
        var judged = await JobRunsAsync(JudgeJob, cancellationToken).ConfigureAwait(false);
        var releases = new List<string>();
        foreach (var check in await JobRunsAsync(SourceCheckJob, cancellationToken).ConfigureAwait(false))
        {
            if (Text(check, "status") != "completed") continue;
            var artifacts = (await device.ExecutionArtifactsAsync(sessionId, check["id"]!.GetValue<long>(), cancellationToken).ConfigureAwait(false))!["items"]!.AsArray();
            foreach (var list in artifacts.OfType<JsonObject>().Where(artifact => Text(artifact, "kind") == "evidence-list"))
                foreach (var item in (list["props"]?["items"] as JsonArray ?? []).OfType<JsonObject>())
                    if (Text(item, "evidenceId") is { } evidenceId && !releases.Contains(evidenceId)) releases.Add(evidenceId);
        }

        int started = 0, replayed = 0;
        foreach (var evidenceId in releases)
        {
            var evidence = (await device.EvidenceAsync(sessionId, evidenceId, cancellationToken).ConfigureAwait(false))!.AsObject();
            var text = await device.EvidenceTextAsync(sessionId, evidenceId, cancellationToken).ConfigureAwait(false);
            var title = Text(evidence, "title") ?? evidenceId;
            var input = new JsonObject { ["title"] = Cut(title, 200), ["summary"] = Cut(text, 4000) };
            if (Text(evidence, "url") is { } url) input["url"] = Cut(url, 2000);
            var accepted = (await device.StartJudgmentAsync(sessionId, new JsonObject
            {
                ["policyId"] = config.PolicyId,
                ["title"] = Cut("Release: " + title, 200),
                ["input"] = input,
                ["evidenceIds"] = new JsonArray(evidenceId),
            }, "watch-judge:" + evidenceId, cancellationToken).ConfigureAwait(false))!;
            if (judged.Any(run => run["id"]!.GetValue<long>() == accepted["taskId"]!.GetValue<long>())) replayed++;
            else
            {
                started++;
                log.WriteLine($"Judging {title} (execution {accepted["taskId"]})");
            }
        }

        var researched = await JobRunsAsync(ResearchJob, cancellationToken).ConfigureAwait(false);
        int pending = 0, research = 0, researchReplayed = 0;
        foreach (var judgment in await JudgmentsAsync(cancellationToken).ConfigureAwait(false))
        {
            var decision = Text(judgment, "decision");
            if (decision == "pending") pending++;
            var label = judgment["result"]?["label"] is JsonValue value && value.TryGetValue<string>(out var chosen) ? chosen : null;
            if (decision != "approved" || label is null || !config.ResearchLabels.Contains(label)) continue;
            var judgmentId = Text(judgment, "id")!;
            var title = judgment["input"]?["title"]?.GetValue<string>() ?? judgmentId;
            var request = new JsonObject
            {
                ["question"] = $"What do we need to check or change before upgrading to {title}?",
                ["evidenceIds"] = new JsonArray([.. (judgment["evidenceIds"] as JsonArray ?? []).Select(id => (JsonNode?)id!.GetValue<string>())]),
                ["budget"] = new JsonObject { ["requests"] = config.ResearchRequests },
            };
            if (config.ResearchAngles.Count > 0)
                request["angles"] = new JsonArray([.. config.ResearchAngles.Select(angle => (JsonNode?)new JsonObject { ["title"] = angle.Title, ["prompt"] = angle.Prompt })]);
            var accepted = (await device.StartResearchAsync(sessionId, request, "watch-research:" + judgmentId, cancellationToken).ConfigureAwait(false))!;
            if (researched.Any(run => run["id"]!.GetValue<long>() == accepted["taskId"]!.GetValue<long>())) researchReplayed++;
            else
            {
                research++;
                log.WriteLine($"Researching {title} (execution {accepted["taskId"]})");
            }
        }
        log.WriteLine($"Pass: {releases.Count} release(s), {started} judged, {replayed} already judged, {pending} waiting for review, {research} researched");
        return new WatcherPass(releases.Count, started, replayed, pending, research, researchReplayed);
    }

    /// <summary>The judgments waiting for a person (<c>GET .../judgments</c>).</summary>
    public async Task<IReadOnlyList<JsonObject>> PendingAsync(CancellationToken cancellationToken = default) =>
        [.. (await JudgmentsAsync(cancellationToken).ConfigureAwait(false)).Where(judgment => Text(judgment, "decision") == "pending")];

    /// <summary>Approves or rejects a judgment as the person holding the device credential (<c>POST .../judgments/{id}/decision</c>).</summary>
    public async Task<JsonObject> DecideAsync(string judgmentId, bool approve, string? reason = null, CancellationToken cancellationToken = default) =>
        (await device.DecideJudgmentAsync(await SessionAsync(cancellationToken).ConfigureAwait(false), judgmentId, approve ? "approve" : "reject", reason,
            cancellationToken).ConfigureAwait(false))!.AsObject();

    /// <summary>
    /// Records feedback on an artifact in the configured profile (<c>POST .../artifacts/{id}/feedback</c>). A more or less without labels is
    /// about the artifact's judged label. Retrying with the same <paramref name="requestId"/> cannot record it twice.
    /// </summary>
    public async Task<JsonObject> FeedbackAsync(string artifactId, string action, IReadOnlyList<string>? labels = null, string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["action"] = action, ["profileId"] = config.Profile };
        if (labels is { Count: > 0 }) body["labels"] = new JsonArray([.. labels.Select(label => (JsonNode?)label)]);
        return (await device.SendFeedbackAsync(await SessionAsync(cancellationToken).ConfigureAwait(false), artifactId, body, requestId ?? Guid.NewGuid().ToString("n"),
            cancellationToken).ConfigureAwait(false))!.AsObject();
    }

    /// <summary>The session's artifacts, newest first (<c>GET .../artifacts</c>), at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<JsonObject>> ArtifactsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var sessionId = await SessionAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var page = (await device.ArtifactsAsync(sessionId, Math.Min(100, limit - items.Count), cursor, cancellationToken).ConfigureAwait(false))!;
            items.AddRange(page["items"]!.AsArray().OfType<JsonObject>());
            cursor = page["next"]?.GetValue<string>();
        } while (cursor is not null && items.Count < limit);
        return items;
    }

    /// <summary>The watcher's executions of <paramref name="jobId"/>, newest first (<c>GET .../executions?kind=job&amp;q=</c>), at most 100.</summary>
    public async Task<IReadOnlyList<JsonObject>> JobRunsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var sessionId = await SessionAsync(cancellationToken).ConfigureAwait(false);
        var page = (await device.ExecutionsAsync(sessionId, "job", null, jobId, 100, cancellationToken: cancellationToken).ConfigureAwait(false))!;
        return [.. page["items"]!.AsArray().OfType<JsonObject>().Where(run => run["run"]?["job"]?.GetValue<string>() == jobId)];
    }

    /// <summary>The watcher session's ID: <c>POST /v1/sessions</c> with an Idempotency-Key made from the configuration replays the session
    /// the first call made.</summary>
    public async Task<string> SessionAsync(CancellationToken cancellationToken = default)
    {
        if (session is not null) return session;
        var body = new JsonObject { ["name"] = config.Session, ["cwd"] = config.Cwd };
        if (config.Model is not null) body["model"] = config.Model.DeepClone();
        var created = await device.SendAsync(HttpMethod.Post, "/v1/sessions", body, "release-watcher:" + config.Session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return session = created!["id"]!.GetValue<string>();
    }

    /// <summary>Publishes the configured spec keyed by its hash, so the same spec replays its version, and activates that version.</summary>
    private async Task<int> PolicyAsync(CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(config.PolicySpec)!.ToJsonString())))[..32].ToLowerInvariant();
        var published = (await device.PublishPolicyAsync(config.PolicyId, config.PolicySpec, "watch-policy:" + hash, cancellationToken).ConfigureAwait(false))!;
        var version = published["version"]!.GetValue<int>();
        var detail = (await device.PolicyAsync(config.PolicyId, cancellationToken).ConfigureAwait(false))!;
        if (detail["activeVersion"]?.GetValue<int>() != version)
            await device.ActivatePolicyAsync(config.PolicyId, version, detail["revision"]!.GetValue<long>(), cancellationToken).ConfigureAwait(false);
        log.WriteLine($"Policy {config.PolicyId}: version {version} is active");
        return version;
    }

    /// <summary>The judgments under the watcher's policy, newest first (<c>GET .../judgments</c>), at most 1000.</summary>
    public async Task<IReadOnlyList<JsonObject>> JudgmentsAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = await SessionAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var page = (await device.JudgmentsAsync(sessionId, 100, cursor, cancellationToken).ConfigureAwait(false))!;
            items.AddRange(page["items"]!.AsArray().OfType<JsonObject>().Where(judgment => judgment["policy"]?["id"]?.GetValue<string>() == config.PolicyId));
            cursor = page["next"]?.GetValue<string>();
        } while (cursor is not null && items.Count < 1000);
        return items;
    }

    /// <summary>The JSON with its keys sorted, so key order does not count as a change.</summary>
    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject json => new JsonObject(json.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, Canonical(pair.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Canonical)]),
        _ => node?.DeepClone(),
    };

    private static string? Text(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
