// PiSharp-only (decision 0010): a judgment policy version. A spec is validated and normalized once, at publication (defaults filled in,
// score weights normalized), and the normalized JSON is what is stored and hashed, so a version means exactly one behavior.
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>One label of a policy's choice question.</summary>
public sealed record PolicyLabel(string Id, string Description);

/// <summary>One scored dimension: ordered levels, lowest first, and its normalized weight.</summary>
public sealed record PolicyScore(string Id, string Instructions, IReadOnlyList<string> Levels, double Weight);

/// <summary>One yes/no flag.</summary>
public sealed record PolicyFlag(string Id, string Instructions, string True, string False);

/// <summary>A validated, normalized policy version.</summary>
public sealed partial record JudgmentPolicy
{
    /// <summary>The longest instructions.</summary>
    public const int MaxInstructions = 8000;

    public required string Title { get; init; }
    public string Description { get; init; } = "";
    /// <summary><c>classifier</c> (the model's classifier operation) or <c>structured</c> (a chat model answering bounded JSON).</summary>
    public required string Kind { get; init; }
    public required ModelRef Model { get; init; }
    public required string Instructions { get; init; }
    public JsonObjectValue? InputSchema { get; init; }
    public required IReadOnlyList<PolicyLabel> Labels { get; init; }
    public IReadOnlyList<PolicyScore> Scores { get; init; } = [];
    public IReadOnlyList<PolicyFlag> Flags { get; init; } = [];
    /// <summary>Confidence at or above which a result is accepted without a person.</summary>
    public double Auto { get; init; } = 0.90;
    /// <summary>Confidence at or above which a person confirms the proposed result; below it a person decides.</summary>
    public double Confirm { get; init; } = 0.50;
    /// <summary><c>none</c>: a policy with flags always routes to a person; <c>margin</c>: a flag's confidence is max(p, 1 − p).</summary>
    public string BoolConfidence { get; init; } = "none";
    public int RequiredEvidence { get; init; }
    public int MaxOutputTokens { get; init; } = 1000;
    public int CacheTtlSeconds { get; init; } = 86_400;
    public int? ReviewTimeoutHours { get; init; }
    public string? PreferenceProfile { get; init; }

    /// <summary>The normalized spec as stored and hashed.</summary>
    public required JsonObjectValue Spec { get; init; }

    /// <summary>Validates and normalizes a spec. Model references are checked against <paramref name="models"/>; null skips that check, for
    /// a stored version whose model may have gone since.</summary>
    /// <exception cref="HostValidationException">The spec is invalid; fields are JSON Pointers into it.</exception>
    public static JudgmentPolicy Parse(JsonObjectValue spec, IDurableModels? models)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var errors = new List<FieldError>();
        var known = new HashSet<string>(StringComparer.Ordinal) { "title", "description", "kind", "model", "instructions", "inputSchema", "labels", "scores",
            "flags", "thresholds", "boolConfidence", "requiredEvidence", "maxOutputTokens", "cacheTtlSeconds", "reviewTimeoutHours", "preferenceProfile" };
        foreach (var key in spec.Keys.Where(key => !known.Contains(key))) errors.Add(new FieldError("/" + key, "is not a policy field"));
        var title = Text(spec, "title", 1, 200, errors) ?? "";
        var description = Text(spec, "description", 0, 2000, errors, required: false) ?? "";
        var kind = Text(spec, "kind", 1, 20, errors) ?? "";
        if (kind.Length > 0 && kind is not ("classifier" or "structured")) errors.Add(new FieldError("/kind", "must be classifier or structured"));
        var instructions = Text(spec, "instructions", 1, MaxInstructions, errors) ?? "";

        ModelRef? model = null;
        if (spec.TryGetValue("model", out var modelValue) && modelValue is JsonObjectValue modelJson &&
            Jobs.String(modelJson, "provider") is { Length: > 0 } provider && Jobs.String(modelJson, "id") is { Length: > 0 } id && modelJson.Count == 2)
        {
            model = new ModelRef(provider, id);
            var exists = models is null || (kind == "classifier" ? models.GetModelOfType("classifier", provider, id) is not null : models.GetModel(provider, id) is not null);
            if (kind.Length > 0 && !exists) errors.Add(new FieldError("/model", $"is not a {(kind == "classifier" ? "classifier" : "chat")} model of this device"));
        }
        else errors.Add(new FieldError("/model", "must be {provider, id}"));

        JsonObjectValue? inputSchema = null;
        if (spec.TryGetValue("inputSchema", out var schemaValue) && schemaValue is not JsonNullValue)
        {
            if (schemaValue is JsonObjectValue schema)
            {
                inputSchema = schema;
                errors.AddRange(Hosting.InputSchema.Check(schema).Select(error => error with { Path = "/inputSchema" + error.Path }));
            }
            else errors.Add(new FieldError("/inputSchema", "must be an object schema"));
        }

        var labels = new List<PolicyLabel>();
        var labelItems = Array(spec, "labels", errors, required: true);
        if (labelItems is not null && labelItems.Count is < 1 or > 20) errors.Add(new FieldError("/labels", "must have 1 to 20 labels"));
        for (var index = 0; index < (labelItems?.Count ?? 0); index++)
        {
            var path = $"/labels/{index}";
            if (labelItems![index] is not JsonObjectValue label) { errors.Add(new FieldError(path, "must be {id, description}")); continue; }
            var labelId = Id(label, "id", path, errors);
            var meaning = Text(label, "description", 0, 1000, errors, required: false, prefix: path) ?? "";
            if (labelId is not null && labels.Any(existing => existing.Id == labelId)) errors.Add(new FieldError(path + "/id", "repeats a label"));
            else if (labelId is not null) labels.Add(new PolicyLabel(labelId, meaning));
        }

        var scores = new List<PolicyScore>();
        var scoreItems = Array(spec, "scores", errors, required: false);
        if (scoreItems is not null && scoreItems.Count > 8) errors.Add(new FieldError("/scores", "may have at most 8 scores"));
        for (var index = 0; index < Math.Min(scoreItems?.Count ?? 0, 8); index++)
        {
            var path = $"/scores/{index}";
            if (scoreItems![index] is not JsonObjectValue score) { errors.Add(new FieldError(path, "must be {id, instructions, levels, weight}")); continue; }
            var scoreId = Id(score, "id", path, errors);
            var scoreInstructions = Text(score, "instructions", 1, 2000, errors, prefix: path) ?? "";
            var levels = Array(score, "levels", errors, required: true, prefix: path);
            if (levels is not null && (levels.Count is < 2 or > 10 || levels.Any(level => level is not JsonStringValue { Value.Length: > 0 and <= 500 })))
                errors.Add(new FieldError(path + "/levels", "must list 2 to 10 nonempty levels, lowest first"));
            var weight = score.TryGetValue("weight", out var weightValue) ? weightValue is JsonNumberValue number ? number.Value : double.NaN : 1;
            if (!double.IsFinite(weight) || weight <= 0 || weight > 1000) errors.Add(new FieldError(path + "/weight", "must be a positive number"));
            if (scoreId is not null && scores.Any(existing => existing.Id == scoreId))
                errors.Add(new FieldError(path + "/id", "repeats an ID"));
            else if (scoreId is not null && levels is not null)
                scores.Add(new PolicyScore(scoreId, scoreInstructions, [.. levels.OfType<JsonStringValue>().Select(level => level.Value)], weight));
        }
        var total = scores.Sum(score => score.Weight);
        if (total > 0) scores = [.. scores.Select(score => score with { Weight = score.Weight / total })];

        var flags = new List<PolicyFlag>();
        var flagItems = Array(spec, "flags", errors, required: false);
        if (flagItems is not null && flagItems.Count > 8) errors.Add(new FieldError("/flags", "may have at most 8 flags"));
        for (var index = 0; index < Math.Min(flagItems?.Count ?? 0, 8); index++)
        {
            var path = $"/flags/{index}";
            if (flagItems![index] is not JsonObjectValue flag) { errors.Add(new FieldError(path, "must be {id, instructions, true, false}")); continue; }
            var flagId = Id(flag, "id", path, errors);
            var flagInstructions = Text(flag, "instructions", 1, 2000, errors, prefix: path) ?? "";
            var yes = Text(flag, "true", 1, 500, errors, prefix: path) ?? "";
            var no = Text(flag, "false", 1, 500, errors, prefix: path) ?? "";
            if (flagId is not null && (flags.Any(existing => existing.Id == flagId) || scores.Any(score => score.Id == flagId)))
                errors.Add(new FieldError(path + "/id", "repeats an ID"));
            else if (flagId is not null) flags.Add(new PolicyFlag(flagId, flagInstructions, yes, no));
        }

        double auto = 0.90, confirm = 0.50;
        if (spec.TryGetValue("thresholds", out var thresholdsValue))
        {
            if (thresholdsValue is not JsonObjectValue thresholds || thresholds.Keys.Any(key => key is not ("auto" or "confirm")))
                errors.Add(new FieldError("/thresholds", "must be {auto, confirm}"));
            else
            {
                auto = Probability(thresholds, "auto", 0.90, "/thresholds", errors);
                confirm = Probability(thresholds, "confirm", 0.50, "/thresholds", errors);
                if (confirm > auto) errors.Add(new FieldError("/thresholds/confirm", "must not exceed auto"));
            }
        }
        var boolConfidence = Text(spec, "boolConfidence", 1, 10, errors, required: false) ?? "none";
        if (boolConfidence is not ("none" or "margin")) errors.Add(new FieldError("/boolConfidence", "must be none or margin"));
        var requiredEvidence = Whole(spec, "requiredEvidence", 0, 0, 20, errors);
        var maxOutputTokens = Whole(spec, "maxOutputTokens", 1000, 1, 4000, errors);
        var cacheTtl = Whole(spec, "cacheTtlSeconds", 86_400, 0, 604_800, errors);
        int? reviewTimeout = spec.TryGetValue("reviewTimeoutHours", out var timeoutValue) && timeoutValue is not JsonNullValue
            ? Whole(spec, "reviewTimeoutHours", 0, 1, 720, errors) : null;
        string? profile = null;
        if (spec.TryGetValue("preferenceProfile", out var profileValue) && profileValue is not JsonNullValue)
        {
            profile = profileValue is JsonStringValue profileText && IdPattern().IsMatch(profileText.Value) ? profileText.Value : null;
            if (profile is null) errors.Add(new FieldError("/preferenceProfile", "must be a profile ID"));
        }
        if (errors.Count > 0) throw HostValidationException.Of("Invalid policy", errors);

        var normalized = JsonValue.Object(
        [
            new("title", title),
            new("description", description),
            new("kind", kind),
            new("model", JsonValue.Object([new("provider", model!.Provider), new("id", model.ModelId)])),
            new("instructions", instructions),
            new("inputSchema", inputSchema is null ? JsonValue.Null : inputSchema),
            new("labels", JsonValue.Array(labels.Select(label => (JsonValue)JsonValue.Object([new("id", label.Id), new("description", label.Description)])))),
            new("scores", JsonValue.Array(scores.Select(score => (JsonValue)JsonValue.Object([new("id", score.Id), new("instructions", score.Instructions),
                new("levels", JsonValue.Array(score.Levels.Select(level => (JsonValue)level))), new("weight", score.Weight)])))),
            new("flags", JsonValue.Array(flags.Select(flag => (JsonValue)JsonValue.Object([new("id", flag.Id), new("instructions", flag.Instructions),
                new("true", flag.True), new("false", flag.False)])))),
            new("thresholds", JsonValue.Object([new("auto", auto), new("confirm", confirm)])),
            new("boolConfidence", boolConfidence),
            new("requiredEvidence", requiredEvidence),
            new("maxOutputTokens", maxOutputTokens),
            new("cacheTtlSeconds", cacheTtl),
            new("reviewTimeoutHours", reviewTimeout is { } hours ? hours : JsonValue.Null),
            new("preferenceProfile", profile is null ? JsonValue.Null : profile),
        ]);
        return new JudgmentPolicy
        {
            Title = title, Description = description, Kind = kind, Model = model, Instructions = instructions, InputSchema = inputSchema, Labels = labels,
            Scores = scores, Flags = flags, Auto = auto, Confirm = confirm, BoolConfidence = boolConfidence, RequiredEvidence = requiredEvidence,
            MaxOutputTokens = maxOutputTokens, CacheTtlSeconds = cacheTtl, ReviewTimeoutHours = reviewTimeout, PreferenceProfile = profile, Spec = normalized,
        };
    }

    /// <summary>Whether <paramref name="id"/> is a valid policy, label, score or profile ID.</summary>
    public static bool IsId(string? id) => id is not null && IdPattern().IsMatch(id);

    private static string? Text(JsonObjectValue json, string key, int min, int max, List<FieldError> errors, bool required = true, string prefix = "")
    {
        if (!json.TryGetValue(key, out var value) || value is JsonNullValue)
        {
            if (required) errors.Add(new FieldError($"{prefix}/{key}", "is required"));
            return null;
        }
        if (value is not JsonStringValue text || text.Value.Length < min || text.Value.Length > max)
        {
            errors.Add(new FieldError($"{prefix}/{key}", $"must be a string of {min} to {max} characters"));
            return null;
        }
        return text.Value;
    }

    private static string? Id(JsonObjectValue json, string key, string prefix, List<FieldError> errors)
    {
        if (json.TryGetValue(key, out var value) && value is JsonStringValue text && IdPattern().IsMatch(text.Value)) return text.Value;
        errors.Add(new FieldError($"{prefix}/{key}", "must match ^[a-z0-9][a-z0-9_-]{0,39}$"));
        return null;
    }

    private static IReadOnlyList<JsonValue>? Array(JsonObjectValue json, string key, List<FieldError> errors, bool required, string prefix = "")
    {
        if (!json.TryGetValue(key, out var value) || value is JsonNullValue)
        {
            if (required) errors.Add(new FieldError($"{prefix}/{key}", "is required"));
            return required ? null : [];
        }
        if (value is JsonArrayValue array) return [.. array.Items];
        errors.Add(new FieldError($"{prefix}/{key}", "must be an array"));
        return null;
    }

    private static double Probability(JsonObjectValue json, string key, double fallback, string prefix, List<FieldError> errors)
    {
        if (!json.TryGetValue(key, out var value)) return fallback;
        if (value is JsonNumberValue number && double.IsFinite(number.Value) && number.Value is >= 0 and <= 1) return number.Value;
        errors.Add(new FieldError($"{prefix}/{key}", "must be a number from 0 to 1"));
        return fallback;
    }

    private static int Whole(JsonObjectValue json, string key, int fallback, int min, int max, List<FieldError> errors)
    {
        if (!json.TryGetValue(key, out var value) || value is JsonNullValue) return fallback;
        if (value is JsonNumberValue number && number.Value == Math.Floor(number.Value) && number.Value >= min && number.Value <= max) return (int)number.Value;
        errors.Add(new FieldError("/" + key, $"must be a whole number from {min} to {max}"));
        return fallback;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
