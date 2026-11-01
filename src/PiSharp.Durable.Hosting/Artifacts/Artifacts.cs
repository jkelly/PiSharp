// PiSharp-only (decision 0010): structured result cards. An artifact is an immutable, validated envelope of one of a few known kinds; its
// props are checked against a fixed schema per kind, its evidence must belong to the session, and an action it offers names a
// host-registered operation. Rendering is the clients' job, from fixed templates per kind; nothing in an artifact is executed or trusted
// as markup.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>An operation an <c>action-request</c> artifact may offer: its ID, label and input schema.</summary>
public sealed record ArtifactAction(string Id, string Label, JsonObjectValue InputSchema, string Description);

/// <summary>Artifact kinds, validation, saving and portable text.</summary>
public static partial class Artifacts
{
    /// <summary>The artifact schema version this host writes and reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The largest envelope, in UTF-8 bytes of canonical JSON.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The kinds of schema version 1.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["finding", "comparison", "evidence-list", "timeline", "action-request"];

    /// <summary>The operations an action request may name.</summary>
    public static IReadOnlyList<ArtifactAction> Actions { get; } =
    [
        new("reevaluate", "Evaluate again", Schema("""
            {"type":"object","additionalProperties":false,"required":["policyId"],"properties":{
              "policyId":{"type":"string","maxLength":40},"version":{"type":"integer","minimum":1,"maximum":100000}}}
            """), "Starts a new judgment of the item the artifact's judgment evaluated, under a policy version."),
        new("start-research", "Research this", Schema("""
            {"type":"object","additionalProperties":false,"required":["question"],"properties":{
              "question":{"type":"string","minLength":1,"maxLength":4000},
              "angles":{"type":"array","maxItems":5,"items":{"type":"string","minLength":1,"maxLength":1000}}}}
            """), "Starts supervised research with the given question."),
    ];

    private static readonly IReadOnlyDictionary<string, JsonObjectValue> PropSchemas = new Dictionary<string, JsonObjectValue>(StringComparer.Ordinal)
    {
        ["finding"] = Schema("""
            {"type":"object","additionalProperties":false,"required":["summary"],"properties":{
              "summary":{"type":"string","minLength":1,"maxLength":4000},
              "details":{"type":"string","maxLength":20000},
              "severity":{"type":"string","maxLength":10,"enum":["info","low","medium","high","critical"]},
              "label":{"type":"string","maxLength":40},
              "score":{"type":"number","minimum":0,"maximum":1},
              "confidence":{"type":"number","minimum":0,"maximum":1},
              "tags":{"type":"array","maxItems":10,"items":{"type":"string","minLength":1,"maxLength":40}},
              "links":{"type":"array","maxItems":20,"items":{"type":"object","additionalProperties":false,"required":["title","url"],"properties":{
                "title":{"type":"string","minLength":1,"maxLength":200},"url":{"type":"string","maxLength":2000,"format":"uri"}}}}}}
            """),
        ["comparison"] = Schema("""
            {"type":"object","additionalProperties":false,"required":["columns","rows"],"properties":{
              "summary":{"type":"string","maxLength":2000},
              "columns":{"type":"array","minItems":2,"maxItems":8,"items":{"type":"string","minLength":1,"maxLength":100}},
              "rows":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"object","additionalProperties":false,"required":["label","values"],"properties":{
                "label":{"type":"string","minLength":1,"maxLength":200},
                "values":{"type":"array","maxItems":8,"items":{"type":"string","maxLength":500}},
                "note":{"type":"string","maxLength":500}}}},
              "recommendation":{"type":"string","maxLength":2000}}}
            """),
        ["evidence-list"] = Schema("""
            {"type":"object","additionalProperties":false,"required":["items"],"properties":{
              "summary":{"type":"string","maxLength":2000},
              "items":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"object","additionalProperties":false,"required":["evidenceId"],"properties":{
                "evidenceId":{"type":"string","minLength":1,"maxLength":100},
                "note":{"type":"string","maxLength":1000},
                "relevance":{"type":"number","minimum":0,"maximum":1}}}}}}
            """),
        ["timeline"] = Schema("""
            {"type":"object","additionalProperties":false,"required":["events"],"properties":{
              "summary":{"type":"string","maxLength":2000},
              "events":{"type":"array","minItems":1,"maxItems":200,"items":{"type":"object","additionalProperties":false,"required":["at","title"],"properties":{
                "at":{"type":"number","minimum":0,"maximum":8640000000000000},
                "title":{"type":"string","minLength":1,"maxLength":200},
                "detail":{"type":"string","maxLength":2000},
                "evidenceId":{"type":"string","maxLength":100}}}}}}
            """),
        ["action-request"] = Schema("""
            {"type":"object","additionalProperties":false,"required":["summary","action"],"properties":{
              "summary":{"type":"string","minLength":1,"maxLength":2000},
              "action":{"type":"string","minLength":1,"maxLength":40},
              "risk":{"type":"string","maxLength":10,"enum":["low","medium","high"]},
              "confirmLabel":{"type":"string","maxLength":60}}}
            """),
    };

    /// <summary>The props schema of a kind; null for an unknown kind.</summary>
    public static JsonObjectValue? PropsSchema(string kind) => PropSchemas.TryGetValue(kind, out var schema) ? schema : null;

    /// <summary>The artifact ID of a job's <paramref name="call"/>-th artifact in a phase run.</summary>
    public static string IdOf(string sessionId, long taskId, string phaseId, int iteration, int call) =>
        CanonicalJson.Id("art_", sessionId, taskId.ToString(CultureInfo.InvariantCulture), phaseId, iteration.ToString(CultureInfo.InvariantCulture),
            call.ToString(CultureInfo.InvariantCulture));

    /// <summary>The artifact ID of a job's artifact saved under <paramref name="key"/> in a phase run (one per branch, say), whatever order
    /// they are saved in.</summary>
    public static string IdOf(string sessionId, long taskId, string phaseId, int iteration, string key) =>
        CanonicalJson.Id("art_", sessionId, taskId.ToString(CultureInfo.InvariantCulture), phaseId, iteration.ToString(CultureInfo.InvariantCulture), "key", key);

    /// <summary>Whether <paramref name="url"/> is an absolute http(s) URL of at most 2000 characters without credentials.</summary>
    public static bool IsSafeUrl(string? url) => url is { Length: > 0 and <= 2000 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;

    /// <summary>
    /// Validates an artifact's kind, title, props, evidence and provenance. <paramref name="evidenceExists"/> answers whether an evidence ID
    /// belongs to the session. Field paths are JSON Pointers into <c>{kind, title, props, evidenceIds, provenance}</c>.
    /// </summary>
    public static IReadOnlyList<FieldError> Validate(string kind, string title, JsonObjectValue props, IReadOnlyList<string> evidenceIds, JsonObjectValue? provenance,
        Func<string, bool> evidenceExists, out JsonObjectValue normalizedProps)
    {
        var errors = new List<FieldError>();
        normalizedProps = props;
        if (PropsSchema(kind) is not { } schema) errors.Add(new FieldError("/kind", "must be one of " + string.Join(", ", Kinds)));
        else
        {
            var problems = InputSchema.Validate(schema, props);
            if (problems.Count > 0) errors.AddRange(problems.Select(problem => problem with { Path = "/props" + problem.Path }));
            else normalizedProps = InputSchema.Normalize(schema, props);
        }
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200) errors.Add(new FieldError("/title", "must have 1 to 200 characters"));
        if (evidenceIds.Count > 100) errors.Add(new FieldError("/evidenceIds", "may name at most 100 evidence records"));
        foreach (var (id, index) in evidenceIds.Take(100).Select((id, index) => (id, index)))
            if (!evidenceExists(id)) errors.Add(new FieldError($"/evidenceIds/{index}", "is not evidence of this session"));
        if (errors.Count == 0)
        {
            switch (kind)
            {
                case "comparison":
                    var columns = Jobs.Items(props, "columns").Count();
                    foreach (var (row, index) in Jobs.Items(props, "rows").OfType<JsonObjectValue>().Select((row, index) => (row, index)))
                        if (Jobs.Items(row, "values").Count() != columns) errors.Add(new FieldError($"/props/rows/{index}/values", $"must have one value per column ({columns})"));
                    break;
                case "evidence-list":
                    foreach (var (item, index) in Jobs.Items(props, "items").OfType<JsonObjectValue>().Select((item, index) => (item, index)))
                        if (!evidenceIds.Contains(Jobs.String(item, "evidenceId")!)) errors.Add(new FieldError($"/props/items/{index}/evidenceId", "must be one of the artifact's evidenceIds"));
                    break;
                case "timeline":
                    foreach (var (item, index) in Jobs.Items(props, "events").OfType<JsonObjectValue>().Select((item, index) => (item, index)))
                        if (Jobs.String(item, "evidenceId") is { } cited && !evidenceIds.Contains(cited))
                            errors.Add(new FieldError($"/props/events/{index}/evidenceId", "must be one of the artifact's evidenceIds"));
                    break;
                case "action-request":
                    var action = Actions.FirstOrDefault(item => item.Id == Jobs.String(props, "action"));
                    if (action is null) errors.Add(new FieldError("/props/action", "must be one of " + string.Join(", ", Actions.Select(item => item.Id))));
                    break;
            }
        }
        if (provenance is not null)
        {
            var allowed = new[] { "policyId", "policyVersion", "judgmentId", "decision", "angleId", "synthesis", "sources" };
            foreach (var key in provenance.Keys.Where(key => !allowed.Contains(key))) errors.Add(new FieldError("/provenance/" + key, "is not a provenance field"));
            foreach (var (key, value) in provenance)
                if (value is not (JsonStringValue { Value.Length: <= 200 } or JsonNumberValue or JsonBooleanValue or JsonNullValue))
                    errors.Add(new FieldError("/provenance/" + key, "must be a short string, a number or a boolean"));
        }
        return errors;
    }

    /// <summary>Saves an artifact within a job phase; see <see cref="JobContext.SaveArtifactAsync"/>.</summary>
    internal static async Task<JsonObjectValue> SaveAsync(JobContext job, string kind, string title, JsonObjectValue props, IReadOnlyList<string> evidenceIds,
        JsonObjectValue? provenance, JsonObjectValue? actionInput = null, string? key = null)
    {
        var runtime = job.Runtime;
        var durable = job.Durable;
        if (key is { Length: 0 or > 100 }) throw new HostInvalidException("An artifact key has 1 to 100 characters.");
        var id = key is null ? IdOf(job.SessionId, job.TaskId, job.Phase.Id, job.Iteration, job.NextArtifactCall()) : IdOf(job.SessionId, job.TaskId, job.Phase.Id, job.Iteration, key);
        if (SessionRecords.Field(await runtime.SnapshotAsync(SessionRecords.ArtifactDoc, id, durable).ConfigureAwait(false), "artifact") is { } saved) return saved;
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidenceId in evidenceIds.Take(100).Distinct(StringComparer.Ordinal))
            if (SessionRecords.Field(await runtime.SnapshotAsync(SessionRecords.EvidenceDoc, evidenceId, durable).ConfigureAwait(false), "evidence") is not null)
                known.Add(evidenceId);
        var errors = Validate(kind, title, props, evidenceIds, provenance, known.Contains, out var normalized).ToList();
        if (kind == "action-request" && errors.Count == 0 && Actions.FirstOrDefault(item => item.Id == Jobs.String(props, "action")) is { } action)
        {
            var problems = InputSchema.Validate(action.InputSchema, actionInput ?? JsonObjectValue.Empty);
            errors.AddRange(problems.Select(problem => problem with { Path = "/actionInput" + problem.Path }));
        }
        if (errors.Count > 0) throw HostValidationException.Of($"Invalid {kind} artifact", errors);
        var envelope = JsonValue.Object(
        [
            new("id", id),
            new("schemaVersion", SchemaVersion),
            new("kind", kind),
            new("title", title),
            new("sessionId", job.SessionId),
            new("runTaskId", job.TaskId),
            new("conversationId", job.Running.ConversationId.Value),
            new("createdAt", runtime.Now()),
            new("revision", 1),
            new("props", normalized),
            new("actionInput", actionInput is null ? JsonValue.Null : actionInput),
            new("evidenceIds", JsonValue.Array(evidenceIds.Select(item => (JsonValue)item))),
            new("provenance", provenance is null ? JsonValue.Null : provenance),
            new("operationId", id),
        ]);
        if (Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(envelope)) > MaxBytes)
            throw new HostTooLargeException($"An artifact may have at most {MaxBytes} bytes.");
        await runtime.CommitAsync(async (tx, running) =>
        {
            var doc = await tx.DocAsync(SessionRecords.ArtifactDoc, id, JsonValue.Null).ConfigureAwait(false);
            if (doc.Get("artifact") is JsonObjectValue) return;
            doc.Set("artifact", envelope);
            await SessionRecords.IndexAsync(tx, "artifacts", id).ConfigureAwait(false);
            await Jobs.UpdateRunAsync(tx, running.ConversationId, running.Id, run => run.SetItem("artifacts",
                JsonValue.Array(Jobs.Items(run, "artifacts").Where(item => item is not JsonObjectValue existing || Jobs.String(existing, "id") != id)
                    .Append(JsonValue.Object([new("id", id), new("kind", kind), new("title", title)]))))).ConfigureAwait(false);
        }, durable).ConfigureAwait(false);
        return envelope;
    }

    /// <summary>Plain text for terminals and accessible fallbacks. Unknown kinds or versions get a bounded JSON rendering.</summary>
    public static string ToText(JsonObjectValue artifact)
    {
        var text = new StringBuilder();
        var kind = Jobs.String(artifact, "kind") ?? "artifact";
        text.Append('[').Append(kind).Append("] ").AppendLine(Jobs.String(artifact, "title") ?? "");
        var props = artifact.TryGetValue("props", out var value) && value is JsonObjectValue json ? json : JsonObjectValue.Empty;
        if (Jobs.Long(artifact, "schemaVersion") != SchemaVersion || PropsSchema(kind) is null)
        {
            var raw = props.ToJson();
            return text.AppendLine(raw.Length > 4000 ? raw[..4000] + "…" : raw).ToString();
        }
        if (Jobs.String(props, "summary") is { } summary) text.AppendLine(summary);
        switch (kind)
        {
            case "finding":
                if (Jobs.String(props, "severity") is { } severity) text.AppendLine("Severity: " + severity);
                if (props.TryGetValue("score", out var score) && score is JsonNumberValue scored) text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Score: {scored.Value:0.##}"));
                if (Jobs.String(props, "details") is { } details) text.AppendLine().AppendLine(details);
                foreach (var link in Jobs.Items(props, "links").OfType<JsonObjectValue>()) text.AppendLine($"- {Jobs.String(link, "title")}: {Jobs.String(link, "url")}");
                break;
            case "comparison":
                text.AppendLine("| " + string.Join(" | ", new[] { "" }.Concat(Jobs.Items(props, "columns").OfType<JsonStringValue>().Select(column => column.Value))) + " |");
                foreach (var row in Jobs.Items(props, "rows").OfType<JsonObjectValue>())
                    text.AppendLine("| " + string.Join(" | ", new[] { Jobs.String(row, "label") ?? "" }.Concat(Jobs.Items(row, "values").OfType<JsonStringValue>().Select(cell => cell.Value))) + " |");
                if (Jobs.String(props, "recommendation") is { } recommendation) text.AppendLine("Recommendation: " + recommendation);
                break;
            case "evidence-list":
                foreach (var item in Jobs.Items(props, "items").OfType<JsonObjectValue>())
                    text.AppendLine($"- {Jobs.String(item, "evidenceId")}{(Jobs.String(item, "note") is { } note ? ": " + note : "")}");
                break;
            case "timeline":
                foreach (var item in Jobs.Items(props, "events").OfType<JsonObjectValue>())
                    text.AppendLine($"- {DateTimeOffset.FromUnixTimeMilliseconds(Jobs.Long(item, "at") ?? 0):u} {Jobs.String(item, "title")}");
                break;
            case "action-request":
                text.AppendLine($"Action: {Jobs.String(props, "action")} (needs confirmation)");
                break;
        }
        var evidence = Jobs.Items(artifact, "evidenceIds").OfType<JsonStringValue>().Select(item => item.Value).ToList();
        if (evidence.Count > 0) text.AppendLine("Evidence: " + string.Join(", ", evidence));
        return text.ToString();
    }

    /// <summary>The artifact for download: its envelope and evidence records, with secret references redacted.</summary>
    public static JsonObjectValue Export(JsonObjectValue artifact, IReadOnlyList<JsonObjectValue> evidence) => (JsonObjectValue)Redact(JsonValue.Object(
    [
        new("format", "pisharp.artifact-export"),
        new("version", 1),
        new("artifact", artifact),
        new("evidence", JsonValue.Array(evidence)),
    ]));

    private static JsonValue Redact(JsonValue value) => value switch
    {
        JsonStringValue text when SecretPattern().IsMatch(text.Value) => "[redacted]",
        JsonObjectValue json => JsonValue.Object(json.Select(pair => new KeyValuePair<string, JsonValue>(pair.Key, Redact(pair.Value)))),
        JsonArrayValue array => JsonValue.Array(array.Items.Select(Redact)),
        _ => value,
    };

    private static JsonObjectValue Schema(string json) => (JsonObjectValue)JsonValue.Parse(json);

    [GeneratedRegex("secret:[A-Za-z0-9._-]{1,100}", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
