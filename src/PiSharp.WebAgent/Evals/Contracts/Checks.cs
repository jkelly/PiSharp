// Decision 0011 (run W5, E0): a test case's checks (mockup modal-check-editor, _CheckEditorModal: one pane per kind, ce-contains, ce-regex,
// ce-schema, ce-tool, ce-limits, ce-judge; modal-test-case's "Add check"). A check is one flat record with a kind and the fields of that
// kind, so a page posts plain JSON ({kind: "regex", pattern, scope, negate}); Normalize keeps only its kind's fields and refuses at save
// time what could never run: an empty text, a regex that does not compile, a JSON schema outside InputSchema's subset (the brief: no new
// package; so an object at the root and a maxLength on every string, as the mockup's example schema has), a tool rule without a value, a
// limits check without a limit, a judge without a rubric. How each check is evaluated is the
// Checks unit's (E2): regexes with a 100 ms timeout (a timeout fails the check), the JSON check reads the answer's first JSON object or json
// code block, the tool check reads the answer's tool calls (suite runs never execute tools), max cost uses the settled cost (list price for
// a subscription), max latency is sending to the last token, and the LLM judge (opt-in: a judge check exists only when the user added one)
// never sees the target's provider or model name, is never cached, and sends low-confidence scores to review instead of passing or
// failing them. A check's result is passed, failed, review (a judge unsure) or error (the check could not run, such as a judge call that
// failed or was refused by the Spend Guard). An answer fails when any check fails (CheckResult.Combine).
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PiSharp.Durable.Hosting;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

namespace PiSharp.WebAgent.Evals;

/// <summary>The kind of a check. Sent as <c>contains</c>, <c>regex</c>, <c>json-schema</c>, <c>tool</c>, <c>limits</c>, <c>judge</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckKind>))]
public enum CheckKind
{
    /// <summary>The answer contains (or must not contain) a text.</summary>
    [JsonStringEnumMemberName("contains")] Contains,

    /// <summary>The answer matches (or must not match) a .NET regular expression, each match stopped after 100 ms.</summary>
    [JsonStringEnumMemberName("regex")] Regex,

    /// <summary>The answer's first JSON object (or json code block) is valid against a schema of InputSchema's subset.</summary>
    [JsonStringEnumMemberName("json-schema")] JsonSchema,

    /// <summary>The answer calls a tool (at least once, exactly once, or never), with matching arguments.</summary>
    [JsonStringEnumMemberName("tool")] Tool,

    /// <summary>The answer costs at most and takes at most a limit.</summary>
    [JsonStringEnumMemberName("limits")] Limits,

    /// <summary>An LLM judge scores the answer against a rubric (opt-in; a metered call per judgement).</summary>
    [JsonStringEnumMemberName("judge")] Judge,
}

/// <summary>Where a contains or regex check looks. Sent as <c>answer</c>, <c>first-code-block</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckScope>))]
public enum CheckScope
{
    /// <summary>The answer's text.</summary>
    [JsonStringEnumMemberName("answer")] Answer,

    /// <summary>The first fenced code block of the answer (an answer without one fails).</summary>
    [JsonStringEnumMemberName("first-code-block")] FirstCodeBlock,
}

/// <summary>How often a tool check's tool must be called. Sent as <c>at-least-once</c>, <c>exactly-once</c>, <c>never</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolCallCount>))]
public enum ToolCallCount
{
    /// <summary>At least one call whose arguments match.</summary>
    [JsonStringEnumMemberName("at-least-once")] AtLeastOnce,

    /// <summary>Exactly one call of the tool, and its arguments match.</summary>
    [JsonStringEnumMemberName("exactly-once")] ExactlyOnce,

    /// <summary>No call of the tool (a never check has no argument rules).</summary>
    [JsonStringEnumMemberName("never")] Never,
}

/// <summary>How a tool call's argument is matched. Sent as <c>equals</c>, <c>regex</c>, <c>present</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ArgumentRule>))]
public enum ArgumentRule
{
    /// <summary>The argument equals the value (a string argument as it is; any other JSON value by its JSON text).</summary>
    [JsonStringEnumMemberName("equals")] EqualsValue,

    /// <summary>The argument (as text) matches the regex in the value, with the same 100 ms limit.</summary>
    [JsonStringEnumMemberName("regex")] Regex,

    /// <summary>The argument is present (the value is not used).</summary>
    [JsonStringEnumMemberName("present")] Present,
}

/// <summary>A check's result. Sent as <c>passed</c>, <c>failed</c>, <c>review</c>, <c>error</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckVerdict>))]
public enum CheckVerdict
{
    /// <summary>The answer passes.</summary>
    [JsonStringEnumMemberName("passed")] Passed,

    /// <summary>The answer fails (a regex timeout fails too).</summary>
    [JsonStringEnumMemberName("failed")] Failed,

    /// <summary>A judge was not confident enough: flagged for a person instead of passing or failing.</summary>
    [JsonStringEnumMemberName("review")] Review,

    /// <summary>The check could not run (a judge call failed, or the Spend Guard refused it).</summary>
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>Limits every check keeps.</summary>
public static class CheckLimits
{
    /// <summary>How long one regex match may run (contains is plain text; regex checks and regex argument rules stop here and fail).</summary>
    public static TimeSpan RegexTimeout { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest contains text or regex pattern.</summary>
    public const int MaxPattern = 2000;

    /// <summary>The largest JSON schema, as JSON text.</summary>
    public const int MaxSchemaChars = 64 * 1024;

    /// <summary>The most argument rules of a tool check.</summary>
    public const int MaxArguments = 20;

    /// <summary>The longest argument name (a top-level name, or a dotted path such as <c>location.city</c>).</summary>
    public const int MaxArgumentName = 200;

    /// <summary>The longest latency limit: one hour.</summary>
    public const long MaxLatencyMs = 3_600_000;
}

/// <summary>One argument rule of a tool check.</summary>
/// <param name="Name">The argument's name, or a dotted path into nested objects (<c>location.city</c>).</param>
/// <param name="Rule">Equals, regex or present.</param>
/// <param name="Value">The value to equal or the regex to match; null for <see cref="ArgumentRule.Present"/>.</param>
public sealed record ToolArgumentMatch(string Name, ArgumentRule Rule, string? Value = null)
{
    /// <summary>The same rule, checked.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public ToolArgumentMatch Normalize()
    {
        var name = Name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("Name the argument to check.");
        if (name.Length > CheckLimits.MaxArgumentName || name.Any(char.IsControl) || name.Split('.').Any(part => part.Length == 0))
            throw new ArgumentException($"\"{Name}\" is not an argument name.");
        if (!Enum.IsDefined(Rule)) throw new ArgumentException($"Unknown argument rule {(int)Rule}.");
        switch (Rule)
        {
            case ArgumentRule.Present:
                return new ToolArgumentMatch(name, Rule);
            case ArgumentRule.Regex:
                if (string.IsNullOrEmpty(Value)) throw new ArgumentException($"Give the regex the {name} argument must match.");
                Check.Compile(Value, $"The regex for {name}");
                return new ToolArgumentMatch(name, Rule, Value);
            default:
                if (Value is null) throw new ArgumentException($"Give the value the {name} argument must equal.");
                if (Value.Length > CheckLimits.MaxPattern) throw new ArgumentException($"A value is at most {CheckLimits.MaxPattern} characters.");
                return new ToolArgumentMatch(name, Rule, Value);
        }
    }
}

/// <summary>An LLM judge: who judges, the rubric, and the score that passes.</summary>
/// <param name="Provider">The judge's provider id.</param>
/// <param name="Model">The judge's model id.</param>
/// <param name="Rubric">How to score, 1 to 5 (the policy's instructions; the Checks unit validates it with <c>JudgmentPolicy.Parse</c>).</param>
/// <param name="PassAt">The lowest score that passes, 1 to <see cref="MaxScore"/>; default 4.</param>
/// <param name="Account">The judge's account, or null for the provider's active account.</param>
public sealed record JudgeSpec(string Provider, string Model, string Rubric, int PassAt = JudgeSpec.DefaultPassAt, string? Account = null)
{
    /// <summary>The highest score (scores are 1 to 5).</summary>
    public const int MaxScore = 5;

    /// <summary>The default passing score.</summary>
    public const int DefaultPassAt = 4;

    /// <summary>The input tokens one judgement reserves: the prompt, an answer of up to <see cref="MaxAnswerTokens"/> and the rubric.</summary>
    public const int MaxInputTokens = 2500;

    /// <summary>The output tokens one judgement may write (its maxTokens).</summary>
    public const int MaxOutputTokens = 512;

    /// <summary>The most answer tokens a judge reads (a longer answer is cut).</summary>
    public const int MaxAnswerTokens = 2000;

    /// <summary>The same judge, checked.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public JudgeSpec Normalize()
    {
        var provider = Provider?.Trim() ?? "";
        var model = Model?.Trim() ?? "";
        if (provider.Length == 0 || model.Length == 0) throw new ArgumentException("Choose the judge's provider and model.");
        var rubric = Rubric?.Trim() ?? "";
        if (rubric.Length == 0) throw new ArgumentException("Write the judge's rubric.");
        if (rubric.Length > JudgmentPolicy.MaxInstructions) throw new ArgumentException($"A rubric is at most {JudgmentPolicy.MaxInstructions:N0} characters.");
        if (PassAt is < 1 or > MaxScore) throw new ArgumentException($"A judge passes at a score of 1 to {MaxScore}.");
        return new JudgeSpec(provider, model, rubric, PassAt, string.IsNullOrWhiteSpace(Account) ? null : Account.Trim());
    }
}

/// <summary>One check of a test case. Only the fields of its <see cref="Kind"/> are kept (<see cref="Normalize"/>).</summary>
public sealed partial record Check
{
    /// <summary>The check's id within its case (<c>k1</c>, ...; assigned by <see cref="TestCase.Normalize"/> when missing): letters, digits and
    /// '-', at most 20.</summary>
    public string? Id { get; init; }

    /// <summary>The kind.</summary>
    public CheckKind Kind { get; init; }

    /// <summary>Where a contains or regex check looks (default the answer).</summary>
    public CheckScope? Scope { get; init; }

    /// <summary>Contains: the text.</summary>
    public string? Text { get; init; }

    /// <summary>Contains: whether case matters (default true).</summary>
    public bool? MatchCase { get; init; }

    /// <summary>Contains and regex: whether the answer must not contain the text or match the pattern (default false).</summary>
    public bool? Negate { get; init; }

    /// <summary>Regex: the pattern (.NET syntax; inline options such as <c>(?is)</c> work).</summary>
    public string? Pattern { get; init; }

    /// <summary>JSON schema: the schema (InputSchema's subset: an object at the root, every string with maxLength, every array with items and
    /// maxItems).</summary>
    public JsonObject? Schema { get; init; }

    /// <summary>Tool: the tool's name.</summary>
    public string? Tool { get; init; }

    /// <summary>Tool: how often it must be called (default at least once).</summary>
    public ToolCallCount? Calls { get; init; }

    /// <summary>Tool: the argument rules, all of which a call must meet.</summary>
    public IReadOnlyList<ToolArgumentMatch>? Arguments { get; init; }

    /// <summary>Limits: the most an answer may cost, in micro-USD (null for no cost limit).</summary>
    public long? MaxCostMicros { get; init; }

    /// <summary>Limits: the longest an answer may take, sending to the last token, in milliseconds (null for no time limit).</summary>
    public long? MaxLatencyMs { get; init; }

    /// <summary>Judge: who judges and how.</summary>
    public JudgeSpec? Judge { get; init; }

    /// <summary>The kind as the suite page's case list names it: <c>Contains</c>, <c>Regex</c>, <c>JSON schema</c>, <c>Tool call</c>,
    /// <c>Cost and latency</c>, <c>LLM judge</c>.</summary>
    [JsonIgnore]
    public string Label => KindLabel(Kind);

    /// <summary>The label of <paramref name="kind"/> on the suite page (see <see cref="Label"/>).</summary>
    public static string KindLabel(CheckKind kind) => kind switch
    {
        CheckKind.Contains => "Contains",
        CheckKind.Regex => "Regex",
        CheckKind.JsonSchema => "JSON schema",
        CheckKind.Tool => "Tool call",
        CheckKind.Limits => "Cost and latency",
        _ => "LLM judge",
    };

    /// <summary>The label of <paramref name="kind"/> in the check editor's Check select: <c>Contains text</c>, <c>Matches regex</c>,
    /// <c>Valid JSON (schema)</c>, <c>Calls a tool</c>, <c>Max cost and latency</c>, <c>LLM judge (opt-in)</c>.</summary>
    public static string EditorLabel(CheckKind kind) => kind switch
    {
        CheckKind.Contains => "Contains text",
        CheckKind.Regex => "Matches regex",
        CheckKind.JsonSchema => "Valid JSON (schema)",
        CheckKind.Tool => "Calls a tool",
        CheckKind.Limits => "Max cost and latency",
        _ => "LLM judge (opt-in)",
    };

    /// <summary>The same check with only its kind's fields, defaults filled in, and everything checked.</summary>
    /// <exception cref="ArgumentException">Something is not allowed; the message says what, for the editor to show.</exception>
    public Check Normalize()
    {
        var id = string.IsNullOrWhiteSpace(Id) ? null : Id.Trim();
        if (id is not null && !IdPattern().IsMatch(id)) throw new ArgumentException($"\"{Id}\" is not a check id.");
        var check = new Check { Id = id, Kind = Kind };
        switch (Kind)
        {
            case CheckKind.Contains:
                if (string.IsNullOrEmpty(Text)) throw new ArgumentException("Give the text the answer must contain.");
                if (Text.Length > CheckLimits.MaxPattern) throw new ArgumentException($"The text is at most {CheckLimits.MaxPattern} characters.");
                return check with { Text = Text, MatchCase = MatchCase ?? true, Negate = Negate ?? false, Scope = ScopeOrDefault() };
            case CheckKind.Regex:
                if (string.IsNullOrEmpty(Pattern)) throw new ArgumentException("Give the pattern the answer must match.");
                Compile(Pattern, "The pattern");
                return check with { Pattern = Pattern, Negate = Negate ?? false, Scope = ScopeOrDefault() };
            case CheckKind.JsonSchema:
                return check with { Schema = CheckSchema(Schema) };
            case CheckKind.Tool:
                var tool = Tool?.Trim() ?? "";
                if (!ToolPattern().IsMatch(tool)) throw new ArgumentException("Choose the tool: its name is letters, digits, '_' and '-' (at most 64).");
                var calls = Calls ?? ToolCallCount.AtLeastOnce;
                if (!Enum.IsDefined(calls)) throw new ArgumentException($"Unknown call count {(int)calls}.");
                var arguments = (Arguments ?? []).ToList();
                if (arguments.Count > CheckLimits.MaxArguments) throw new ArgumentException($"A tool check has at most {CheckLimits.MaxArguments} argument rules.");
                if (calls == ToolCallCount.Never && arguments.Count > 0) throw new ArgumentException("A tool that must never be called has no argument rules.");
                if (arguments.Any(argument => argument is null)) throw new ArgumentException("An argument rule is empty.");
                return check with { Tool = tool, Calls = calls, Arguments = [.. arguments.Select(argument => argument.Normalize())] };
            case CheckKind.Limits:
                if (MaxCostMicros is null && MaxLatencyMs is null) throw new ArgumentException("Set a maximum cost, a maximum latency, or both.");
                if (MaxCostMicros is { } cost && (cost <= 0 || cost > Spend.SpendMoney.MaxMicros))
                    throw new ArgumentException("The maximum cost is more than $0.00.");
                if (MaxLatencyMs is { } latency && (latency <= 0 || latency > CheckLimits.MaxLatencyMs))
                    throw new ArgumentException("The maximum latency is more than 0 s and at most an hour.");
                return check with { MaxCostMicros = MaxCostMicros, MaxLatencyMs = MaxLatencyMs };
            case CheckKind.Judge:
                return check with { Judge = (Judge ?? throw new ArgumentException("Choose a judge and write its rubric.")).Normalize() };
            default:
                throw new ArgumentException($"Unknown check kind {(int)Kind}.");
        }
    }

    /// <summary>A regex for a check, compiled with the check timeout (<see cref="CheckLimits.RegexTimeout"/>), or an
    /// <see cref="ArgumentException"/> that says why it does not compile.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="what">What the pattern is, for the message (<c>The pattern</c>).</param>
    public static Regex Compile(string pattern, string what)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length > CheckLimits.MaxPattern) throw new ArgumentException($"{what} is at most {CheckLimits.MaxPattern} characters.");
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, CheckLimits.RegexTimeout);
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException($"{what} is not a valid regular expression: {error.Message}");
        }
    }

    /// <summary><paramref name="schema"/> as InputSchema reads it (PiSharp.Durable's JSON values).</summary>
    /// <exception cref="ArgumentException">It is not a JSON object.</exception>
    public static ChordObject ToInputSchema(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return ChordJson.Parse(schema.ToJsonString()) as ChordObject ?? throw new ArgumentException("A JSON schema is an object.");
    }

    private CheckScope ScopeOrDefault()
    {
        var scope = Scope ?? CheckScope.Answer;
        return Enum.IsDefined(scope) ? scope : throw new ArgumentException($"Unknown check scope {(int)scope}.");
    }

    private static JsonObject CheckSchema(JsonObject? schema)
    {
        if (schema is null) throw new ArgumentException("Give the JSON schema the answer must match.");
        var text = schema.ToJsonString();
        if (text.Length > CheckLimits.MaxSchemaChars) throw new ArgumentException($"A JSON schema is at most {CheckLimits.MaxSchemaChars / 1024} KB.");
        var errors = InputSchema.Check(ToInputSchema(schema));
        if (errors.Count > 0)
            throw new ArgumentException("The schema is outside the supported subset: " + string.Join("; ", errors.Take(5).Select(error =>
                (error.Path is { Length: > 0 } path ? path : "/") + " " + error.Message)) + ".");
        return (JsonObject)schema.DeepClone();
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,20}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ToolPattern();
}

/// <summary>One check's result on one answer.</summary>
/// <param name="CheckId">The check's id in its case.</param>
/// <param name="Kind">The check's kind.</param>
/// <param name="Verdict">Passed, failed, review or error.</param>
/// <param name="Detail">A short reason for the page (<c>Matches the reference output in 0.3 ms.</c>, <c>timed out after 100 ms</c>), or
/// null.</param>
/// <param name="Score">A judge's score (1 to 5), or null.</param>
/// <param name="Confidence">A judge's confidence (0 to 1), or null.</param>
/// <param name="JudgeRunId">The judge call's runs.db row, or null.</param>
public sealed record CheckResult(string CheckId, CheckKind Kind, CheckVerdict Verdict, string? Detail = null, double? Score = null, double? Confidence = null,
    string? JudgeRunId = null)
{
    /// <summary>The verdict of a whole answer from its checks' results: failed when any check failed (whatever the others say), else error
    /// when any could not run, else review when any is under review, else passed. An answer with no checks passes when its call completed; the
    /// caller decides that (a failed call is an error whatever its checks say).</summary>
    public static CheckVerdict Combine(IEnumerable<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var verdicts = results.Select(result => result.Verdict).ToList();
        return verdicts.Contains(CheckVerdict.Failed) ? CheckVerdict.Failed
            : verdicts.Contains(CheckVerdict.Error) ? CheckVerdict.Error
            : verdicts.Contains(CheckVerdict.Review) ? CheckVerdict.Review
            : CheckVerdict.Passed;
    }
}
