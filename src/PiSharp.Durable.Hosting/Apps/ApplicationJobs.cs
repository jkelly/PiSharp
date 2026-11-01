// PiSharp-only (decision 0010): the host's application jobs. They are ordinary jobs (phases, checkpoints, approvals, audits) built on the
// public JobContext services, so a sample or a device's own job can do everything they do; their IDs are reserved.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>The application jobs every host defines.</summary>
internal static class ApplicationJobDefinitions
{
    /// <summary>The prefix reserved for application job IDs.</summary>
    public const string Prefix = "pisharp-";

    /// <summary>The judgment job's ID.</summary>
    public const string Judge = "pisharp-judge";

    public static IReadOnlyList<JobDefinition> Create(DurableDeviceHost host) => [JudgeJob(host), SourceChecks.Job(host), Research.Job(host)];

    /// <summary>
    /// <c>pisharp-judge {policyId, version?, input, evidenceIds?}</c>: evaluates the item under the policy (the active version when the run is
    /// admitted, unless a version is given), waits for a person when the policy routes it to one, and keeps the result as a finding.
    /// </summary>
    private static JobDefinition JudgeJob(DurableDeviceHost host) => new()
    {
        Id = Judge,
        Title = "Judgment",
        Summary = "Evaluates an item under a versioned judgment policy, asks a person when the policy's confidence routing requires it, and keeps the result.",
        Application = true,
        EstimatedSeconds = 15,
        InputSchema = Shape,
        RequiredChecks = ["decision", "artifact:finding"],
        Admit = input => AdmitJudge(host, input),
        Freeze = input => JsonValue.Object([new("policyVersion", host.Policies.Resolve(Jobs.String(input, "policyId")!,
            Jobs.Long(input, "version") is { } version ? (int)version : null).Version)]),
        Phases =
        [
            new JobPhase("evaluate", "Evaluate", "Judges the item under the policy.") { Run = EvaluateAsync },
            new JobPhase("review", "Review", "A person confirms or decides the proposed result.", JobPhaseKind.Approval)
            {
                When = data => Jobs.String(data, "route") is "confirm" or "human",
            },
            new JobPhase("record", "Record", "Keeps the decision and the result card.") { Run = RecordAsync },
        ],
    };

    /// <summary>The judge input as far as the schema subset can say it; the item itself is checked against the policy's schema.</summary>
    private static readonly JsonObjectValue Shape = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","additionalProperties":false,"required":["policyId"],"properties":{
          "policyId":{"type":"string","minLength":1,"maxLength":40},
          "version":{"type":"integer","minimum":1,"maximum":100000},
          "title":{"type":"string","maxLength":200},
          "evidenceIds":{"type":"array","maxItems":100,"items":{"type":"string","minLength":1,"maxLength":100}}}}
        """);

    private static JsonObjectValue AdmitJudge(DurableDeviceHost host, JsonObjectValue? input)
    {
        input ??= JsonObjectValue.Empty;
        var item = input.TryGetValue("input", out var given) ? given : JsonObjectValue.Empty;
        var errors = new List<FieldError>();
        if (item is not JsonObjectValue itemObject) { errors.Add(new FieldError("/input", "must be an object")); itemObject = JsonObjectValue.Empty; }
        var rest = input.Remove("input");
        errors.AddRange(InputSchema.Validate(Shape, rest));
        if (errors.Count > 0) throw HostValidationException.Of("Invalid judgment input", errors);
        var normalized = InputSchema.Normalize(Shape, rest);
        PolicyVersion policy;
        try
        {
            policy = host.Policies.Resolve(Jobs.String(normalized, "policyId")!, Jobs.Long(normalized, "version") is { } version ? (int)version : null);
        }
        catch (HostNotFoundException missing)
        {
            throw HostValidationException.Of("Invalid judgment input", [new FieldError("/policyId", missing.Message)]);
        }
        var parsed = JudgmentPolicy.Parse(policy.Spec, null);
        if (parsed.InputSchema is { } schema)
        {
            var problems = InputSchema.Validate(schema, itemObject);
            if (problems.Count > 0) throw HostValidationException.Of("Invalid judgment input", [.. problems.Select(problem => problem with { Path = "/input" + problem.Path })]);
            itemObject = InputSchema.Normalize(schema, itemObject);
        }
        return normalized.SetItem("input", itemObject);
    }

    private static async Task<JobPhaseResult> EvaluateAsync(JobContext context)
    {
        var policyId = Jobs.String(context.Input, "policyId")!;
        var version = Jobs.Long(context.Frozen, "policyVersion") ?? Jobs.Long(context.Input, "version");
        var item = context.Input.TryGetValue("input", out var given) && given is JsonObjectValue value ? value : JsonObjectValue.Empty;
        var evidence = Jobs.Items(context.Input, "evidenceIds").OfType<JsonStringValue>().Select(id => id.Value).ToList();
        var judgment = await context.JudgeAsync(policyId, item, evidence, version is { } frozen ? (int)frozen : null).ConfigureAwait(false);
        if (Jobs.String(judgment, "status") != "judged")
            throw new InvalidOperationException($"The judgment {Jobs.String(judgment, "status")}: {Jobs.String(judgment, "error")}");
        var result = (JsonObjectValue)judgment["result"];
        var route = Jobs.String(judgment, "route")!;
        var data = JsonValue.Object([new("judgmentId", judgment["id"]), new("route", route), new("label", result["label"]),
            new("score", result["score"]), new("confidence", result["confidence"])]);
        var policyRef = (JsonObjectValue)judgment["policy"];
        if (route != "auto" && context.Host.Policies.Version(policyId, (int)Jobs.Long(policyRef, "version")!.Value) is { } policy &&
            Jobs.Long(policy.Spec, "reviewTimeoutHours") is { } hours)
            data = data.SetItem("reviewDeadline", context.Runtime.Now() + hours * 3_600_000.0);
        return JobPhaseResult.Next($"{result["label"].ToJson()} — {route}", data);
    }

    private static async Task<JobPhaseResult> RecordAsync(JobContext context)
    {
        var judgmentId = Jobs.String(context.Data, "judgmentId")!;
        var judgment = SessionRecords.Field(await context.Runtime.SnapshotAsync(SessionRecords.JudgmentDoc, judgmentId, context.Durable).ConfigureAwait(false), "judgment")
            ?? throw new InvalidOperationException($"Judgment {judgmentId} is gone.");
        var decision = Jobs.String(judgment, "decision");
        await context.CheckAsync("decision", decision == "approved", decision).ConfigureAwait(false);
        var result = (JsonObjectValue)judgment["result"];
        var policyRef = (JsonObjectValue)judgment["policy"];
        var props = JsonValue.Object([new("summary", Jobs.String(result, "rationale") is { Length: > 0 } rationale ? rationale : $"Judged {result["label"].ToJson()}"),
            new("label", result["label"])]);
        if (result.TryGetValue("score", out var score) && score is JsonNumberValue) props = props.SetItem("score", score);
        if (result.TryGetValue("confidence", out var confidence) && confidence is JsonNumberValue) props = props.SetItem("confidence", confidence);
        var title = Jobs.String(context.Input, "title") is { Length: > 0 } given ? given : $"{Jobs.String(policyRef, "title") ?? Jobs.String(policyRef, "id")}: {Jobs.String(result, "label")}";
        var artifact = await context.SaveArtifactAsync("finding", title, props,
            [.. Jobs.Items(judgment, "evidenceIds").OfType<JsonStringValue>().Select(id => id.Value)],
            JsonValue.Object([new("policyId", policyRef["id"]), new("policyVersion", policyRef["version"]), new("judgmentId", judgmentId),
                new("decision", decision is null ? JsonValue.Null : decision)])).ConfigureAwait(false);
        return JobPhaseResult.Next("Recorded", JsonValue.Object([new("judgmentId", judgmentId), new("artifactId", artifact["id"]), new("label", result["label"]),
            new("score", result.TryGetValue("score", out var kept) ? kept : JsonValue.Null), new("route", judgment["route"]),
            new("decision", decision is null ? JsonValue.Null : decision)]));
    }
}
