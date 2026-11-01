// Decision 0011 (run W5, E2): how a judge's answer becomes a check result. The confidence routing is JudgmentService.Route's (decision 0010),
// copied here because that method is private to the durable judgment service and JudgeAsync is never called for an eval (it caches results for
// a day; a judgement here is never cached): the label must be a policy label, every confidence must be a finite 0..1, a missing one routes
// human, and the lowest one routes auto at or above the auto threshold, confirm at or above the confirm threshold, else human. An auto route
// passes at or above the judge's passing score and fails below it; confirm and human are a review (contracts: "sends low-confidence scores to
// review instead of passing or failing them"). An answer that is not the policy's shape is an error with no repair call, so a judgement is
// exactly one call and never costs more than its estimate. A score given as a number ({"score": 4}, {"label": 4}) is read as its label.
using System.Globalization;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;

namespace PiSharp.WebAgent.Evals.Checks;

/// <summary>A judge's answer, read and routed.</summary>
/// <param name="Verdict">Passed or failed (auto route), review (confirm or human), or error (not the policy's shape).</param>
/// <param name="Score">The score, 1 to 5, or null when the answer has none.</param>
/// <param name="Confidence">The lowest confidence the judge gave, or null.</param>
/// <param name="Route">The route: <c>auto</c>, <c>confirm</c>, <c>human</c>, or null for an invalid answer.</param>
/// <param name="Rationale">The judge's rationale, at most <see cref="JudgeRouting.MaxRationale"/> characters, or null.</param>
/// <param name="Detail">The result's line: <c>Score 4 of 5 (passes at 4). ...</c>.</param>
/// <param name="Result">The routed result as JudgmentService builds it (label, probabilities, scores, score, flags, confidence, rationale,
/// citedEvidenceIds, route, routeReason), or null for an invalid answer.</param>
public sealed record JudgeReading(CheckVerdict Verdict, int? Score, double? Confidence, string? Route, string? Rationale, string Detail, JsonObjectValue? Result);

/// <summary>Reads and routes a judge's answer.</summary>
public static class JudgeRouting
{
    /// <summary>The longest rationale kept in a result's detail.</summary>
    public const int MaxRationale = 300;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The judge's answer <paramref name="text"/> read against <paramref name="policy"/>: routed, and passed or failed at
    /// <paramref name="passAt"/> when the route is auto.</summary>
    public static JudgeReading Read(JudgmentPolicy policy, string? text, int passAt)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (ParseObject(text ?? "") is not { } json)
            return new JudgeReading(CheckVerdict.Error, null, null, null, null, "The judge's answer is not valid: it is not a JSON object.", null);
        var routed = Route(policy, Labelled(json));
        if (routed.Result is not { } result)
            return new JudgeReading(CheckVerdict.Error, null, null, null, null, $"The judge's answer is not valid: {routed.Error}", null);
        var label = Jobs.String(result, "label")!;
        int? score = int.TryParse(label, NumberStyles.None, Invariant, out var parsed) ? parsed : null;
        double? confidence = result.TryGetValue("confidence", out var value) && value is JsonNumberValue number ? number.Value : null;
        var route = Jobs.String(result, "route") ?? "human";
        var rationale = Jobs.String(result, "rationale") is { Length: > 0 } given ? Bound(given.Trim().ReplaceLineEndings(" ")) : null;
        var scored = string.Create(Invariant, $"Score {label} of {JudgeSpec.MaxScore}");
        var why = rationale is null ? "" : " " + rationale;
        if (route == "auto" && score is { } known)
        {
            var passed = known >= passAt;
            return new JudgeReading(passed ? CheckVerdict.Passed : CheckVerdict.Failed, score, confidence, route, rationale,
                string.Create(Invariant, $"{scored} (passes at {passAt}).{why}"), result);
        }
        var reason = confidence is { } low
            ? string.Create(Invariant, $"{scored}, but confidence {low:0.00} is below {policy.Auto:0.00}: flagged for review.")
            : $"{scored}, but the judge gave no valid confidence: flagged for review.";
        return new JudgeReading(CheckVerdict.Review, score, confidence, route, rationale, reason + why, result);
    }

    /// <summary>The first JSON object in a model's answer, with or without a code fence (JudgmentService.ParseObject's rule: from the first
    /// '{' to the last '}').</summary>
    public static JsonObjectValue? ParseObject(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonValue.Parse(text[start..(end + 1)]) as JsonObjectValue; }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The answer with its score as the label: a numeric <c>label</c>, or a numeric <c>score</c> when there is no label, becomes
    /// the label text (<c>4</c>).</summary>
    private static JsonObjectValue Labelled(JsonObjectValue json)
    {
        if (json.TryGetValue("label", out var label))
            return label is JsonNumberValue number && Whole(number.Value) is { } whole ? json.SetItem("label", whole) : json;
        return json.TryGetValue("score", out var score) && score is JsonNumberValue given && Whole(given.Value) is { } level
            ? json.SetItem("label", level) : json;
    }

    private static string? Whole(double value) =>
        double.IsFinite(value) && value == Math.Floor(value) && Math.Abs(value) < 1000 ? ((long)value).ToString(Invariant) : null;

    private static string Bound(string text) => text.Length <= MaxRationale ? text : text[..MaxRationale] + "…";

    // ─── Copied from JudgmentService (PiSharp.Durable.Hosting, decision 0010) ───

    private sealed record Routed(JsonObjectValue? Result, string? Error);

    /// <summary>Checks the answer against the policy and routes it: auto at or above the auto threshold, confirm at or above the confirm
    /// threshold, otherwise human. Missing or non-finite confidence routes human. Invalid output is an error and never routes.</summary>
    private static Routed Route(JudgmentPolicy policy, JsonObjectValue answer)
    {
        var label = Jobs.String(answer, "label");
        if (label is null || policy.Labels.All(item => item.Id != label)) return new Routed(null, $"Invalid output: the label {label ?? "(none)"} is not a policy label.");
        var confidences = new List<(string Source, double? Value)> { ("label", Number(answer, "confidence")) };
        var scores = new List<KeyValuePair<string, JsonValue>>();
        double weighted = 0;
        var scoreJson = answer.TryGetValue("scores", out var scoreValue) ? scoreValue as JsonObjectValue : null;
        foreach (var score in policy.Scores)
        {
            if (scoreJson is null || !scoreJson.TryGetValue(score.Id, out var given) || given is not JsonObjectValue item || Number(item, "level") is not { } level ||
                level < 0 || level > score.Levels.Count - 1)
                return new Routed(null, $"Invalid output: score {score.Id} is missing or out of range.");
            var value = level / (score.Levels.Count - 1);
            weighted += score.Weight * value;
            confidences.Add(("score " + score.Id, Number(item, "confidence")));
            scores.Add(new(score.Id, JsonValue.Object([new("level", level), new("value", value), new("weight", score.Weight),
                new("confidence", Number(item, "confidence") is { } reported ? reported : JsonValue.Null)])));
        }
        var flags = new List<KeyValuePair<string, JsonValue>>();
        var flagJson = answer.TryGetValue("flags", out var flagValue) ? flagValue as JsonObjectValue : null;
        foreach (var flag in policy.Flags)
        {
            if (flagJson is null || !flagJson.TryGetValue(flag.Id, out var given) || given is not JsonNumberValue probability || !double.IsFinite(probability.Value) ||
                probability.Value is < 0 or > 1)
                return new Routed(null, $"Invalid output: flag {flag.Id} is missing or not a probability.");
            flags.Add(new(flag.Id, probability.Value));
            confidences.Add(("flag " + flag.Id, policy.BoolConfidence == "margin" ? Math.Max(probability.Value, 1 - probability.Value) : null));
        }
        string route;
        string reason;
        var missing = confidences.FirstOrDefault(item => item.Value is not { } value || !double.IsFinite(value) || value is < 0 or > 1);
        double? confidence = null;
        if (missing.Source is not null)
        {
            route = "human";
            reason = policy.BoolConfidence == "none" && missing.Source.StartsWith("flag ", StringComparison.Ordinal)
                ? "The policy's yes/no flags carry no confidence (boolConfidence none)."
                : $"The {missing.Source} has no valid confidence.";
        }
        else
        {
            confidence = confidences.Min(item => item.Value!.Value);
            route = confidence >= policy.Auto ? "auto" : confidence >= policy.Confirm ? "confirm" : "human";
            reason = string.Create(CultureInfo.InvariantCulture,
                $"Confidence {confidence:0.###} against thresholds auto {policy.Auto:0.###} and confirm {policy.Confirm:0.###}.");
        }
        var result = JsonValue.Object(
        [
            new("label", label),
            new("probabilities", answer.TryGetValue("probabilities", out var probabilities) ? probabilities : JsonObjectValue.Empty),
            new("scores", JsonValue.Object(scores)),
            new("score", policy.Scores.Count > 0 ? weighted : JsonValue.Null),
            new("flags", JsonValue.Object(flags)),
            new("confidence", confidence is { } known ? known : JsonValue.Null),
            new("rationale", Jobs.String(answer, "rationale") is { } rationale ? RouteBound(rationale, 4000) : JsonValue.Null),
            new("citedEvidenceIds", answer.TryGetValue("evidenceIds", out var cited) && cited is JsonArrayValue citedIds
                ? JsonValue.Array(citedIds.Items.OfType<JsonStringValue>().Take(20)) : JsonArrayValue.Empty),
            new("route", route),
            new("routeReason", reason),
        ]);
        return new Routed(result, null);
    }

    private static double? Number(JsonObjectValue json, string key) => json.TryGetValue(key, out var value) && value is JsonNumberValue number ? number.Value : null;

    private static string RouteBound(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
