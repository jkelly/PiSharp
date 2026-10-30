// Decision 0011 (run W5, E2): how a test case's checks run on an answer (mockup modal-check-editor: one pane per kind, each with a line that
// says what the check makes of the reference output: "Found in the reference output.", "Matches the reference output in 0.3 ms.", "The
// reference output is valid.", "The reference output calls create_ticket once, with matching arguments.", "Reference output: ≈ $0.0115 at
// list price, 18.2 s."). Every check but the judge is deterministic and free, so Evaluate runs it on any answer, a suite run's or a case's
// reference output; the LLM judge is a metered call, so it runs only in RunAsync with a JudgeContext (a suite run's target, group and spend
// answer) and never in a preview. A check that cannot run is an error, never a pass: a regex that stops at its 100 ms limit fails ("Timed out
// after 100 ms."), a limits check whose answer has no cost or time is an error, and a judge call the Spend Guard refused or that failed is an
// error. Contains is plain text (ordinal, or ordinal ignoring case); regexes are .NET's, compiled with the check timeout; JSON schemas are
// InputSchema's subset (an unknown field is an error, as additionalProperties false); tool checks read the answer's tool calls (suite runs
// never execute tools) and match arguments by dotted path (location.city, items.0.id).
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;
using ChordJson = PiSharp.Durable.Chord.JsonValue;

namespace PiSharp.WebAgent.Evals.Checks;

/// <summary>Whose output a check reads, for its result's wording.</summary>
public enum CheckSubject
{
    /// <summary>A suite run's answer: "the answer".</summary>
    Answer,

    /// <summary>A case's reference output (the check editor's line): "the reference output".</summary>
    Reference,
}

/// <summary>What a judge check needs to run: the target whose answer it judges (whose names it hides), the suite run's group (the Spend
/// Guard's run key, so the run's cap covers its judgements) and the run's spend answer.</summary>
public sealed record JudgeContext
{
    /// <summary>The target that answered: its provider and model are hidden from the judge.</summary>
    public required ModelIdentity Target { get; init; }

    /// <summary>The suite run's group id (letters, digits, '-' and '_'), the judge run's <c>groupId</c>; null for a judge call of its own.</summary>
    public string? GroupId { get; init; }

    /// <summary>The suite run's answer to the Spend Guard (proceed once, or cap the output), or null to be checked as usual.</summary>
    public RunSpendAnswer? Spend { get; init; }

    /// <summary>The answer's runs.db row, or null: the judge run is recorded as its child (<c>parentId</c>), so History and the Run inspector
    /// show a judgement under the answer it judged, and a judge run is told apart from the target's answers of the same group.</summary>
    public string? ParentRunId { get; init; }

    /// <summary>The suite run's holds for this answer's judgements, one per judge check in the case's order (W5 integration fix 1): each
    /// judgement is sent on its hold (RunEngine.StartReservedAsync) instead of a reservation of its own. Null or short: reserved as usual.</summary>
    public IReadOnlyList<GuardedLease>? Holds { get; init; }
}

/// <summary>Runs a test case's checks on an answer, and checks checks before they are saved.</summary>
public sealed class CheckRunner
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string RegexLimit = ((long)CheckLimits.RegexTimeout.TotalMilliseconds).ToString(Invariant) + " ms";
    private readonly RubricJudge judge;

    /// <summary>A runner whose judge checks go through <paramref name="judge"/>.</summary>
    public CheckRunner(RubricJudge judge)
    {
        ArgumentNullException.ThrowIfNull(judge);
        this.judge = judge;
    }

    /// <summary>The judge the runner's judge checks go through.</summary>
    public RubricJudge Judge => judge;

    /// <summary><paramref name="check"/> as it is saved: normalized (<see cref="Check.Normalize"/>), and for a judge, its rubric validated as
    /// a judgment policy (<see cref="RubricJudge.Validate"/>: the model exists, the rubric fits the judgement's input budget).</summary>
    /// <exception cref="ArgumentException">The check is not allowed; the message says why, for the editor to show.</exception>
    public Check Validate(Check check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var normalized = check.Normalize();
        return normalized.Kind == CheckKind.Judge ? normalized with { Judge = judge.Validate(normalized.Judge!) } : normalized;
    }

    /// <summary><paramref name="testCase"/> as it is saved: normalized (<see cref="TestCase.Normalize"/>), with every judge check validated
    /// as <see cref="Validate(Check)"/> does.</summary>
    /// <exception cref="ArgumentException">Something is not allowed; the message says what (and which check).</exception>
    public TestCase Validate(TestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var normalized = testCase.Normalize();
        var checks = new List<Check>(normalized.Checks.Count);
        foreach (var check in normalized.Checks)
        {
            try
            {
                checks.Add(check.Kind == CheckKind.Judge ? check with { Judge = judge.Validate(check.Judge!) } : check);
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException($"Check {check.Id}: {error.Message}");
            }
        }
        return normalized with { Checks = checks };
    }

    /// <summary>The judge checks of <paramref name="testCase"/> (each a metered call per answer), for a suite run's estimate.</summary>
    public static IReadOnlyList<Check> JudgeChecks(TestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        return [.. (testCase.Checks ?? []).Where(check => check is { Kind: CheckKind.Judge, Judge: not null })];
    }

    /// <summary>Runs every check of <paramref name="testCase"/> on <paramref name="answer"/>, in the case's order: the free checks first,
    /// then the judge checks one at a time through <see cref="RubricJudge"/> (each a guarded, recorded call of source <c>drift</c>). Without
    /// <paramref name="context"/> a judge check is an error (judges run only in suite runs). A judge is not called for an answer whose call
    /// failed (an error: there is nothing to judge) or that is empty (failed).</summary>
    /// <returns>One result per check, in the case's order (the combined verdict is <see cref="CheckResult.Combine"/>).</returns>
    public async Task<IReadOnlyList<CheckResult>> RunAsync(TestCase testCase, EvalAnswer answer, JudgeContext? context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(answer);
        var checks = (testCase.Checks ?? []).ToList();
        var results = new CheckResult?[checks.Count];
        for (var index = 0; index < checks.Count; index++)
            if (checks[index].Kind != CheckKind.Judge) results[index] = Evaluate(checks[index] with { Id = IdOf(checks[index], index) }, answer);
        var judged = 0;
        for (var index = 0; index < checks.Count; index++)
        {
            if (checks[index].Kind != CheckKind.Judge) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var check = checks[index];
            var id = IdOf(check, index);
            // The suite run's hold for this judgement: holds follow JudgeChecks (the judge checks that name a judge), in order.
            var hold = check.Judge is not null && context?.Holds is { } holds && judged < holds.Count ? holds[judged] : null;
            if (check.Judge is not null) judged++;
            results[index] = context is null
                ? new CheckResult(id, CheckKind.Judge, CheckVerdict.Error, "An LLM judge runs only in a suite run.")
                : answer.Error is not null
                    ? new CheckResult(id, CheckKind.Judge, CheckVerdict.Error, "Not judged: the call failed, so there is no answer to judge.")
                    : answer.Text.Trim().Length == 0 && answer.ToolCalls.Count == 0
                        ? new CheckResult(id, CheckKind.Judge, CheckVerdict.Failed, "Not judged: the answer is empty.")
                        : await judge.ScoreAsync(check with { Id = id }, testCase, answer, context with { Holds = null }, cancellationToken, hold).ConfigureAwait(false);
        }
        return [.. results.Select(result => result!)];
    }

    /// <summary>The result of a free check on <paramref name="answer"/> (every kind but the judge, which is an error here: it runs only in
    /// <see cref="RunAsync"/>). A check that does not normalize is an error that says why.</summary>
    /// <param name="check">The check.</param>
    /// <param name="answer">The answer, or a reference output (<see cref="TestCaseReference.ToAnswer"/>).</param>
    /// <param name="subject">Whose output it is, for the wording.</param>
    public static CheckResult Evaluate(Check check, EvalAnswer answer, CheckSubject subject = CheckSubject.Answer)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(answer);
        var id = string.IsNullOrWhiteSpace(check.Id) ? "k1" : check.Id.Trim();
        Check normalized;
        try
        {
            normalized = check.Normalize();
        }
        catch (ArgumentException error)
        {
            return new CheckResult(id, check.Kind, CheckVerdict.Error, error.Message);
        }
        var text = Subject(subject);
        return normalized.Kind switch
        {
            CheckKind.Contains => Contains(id, normalized, answer, text),
            CheckKind.Regex => Matches(id, normalized, answer, text),
            CheckKind.JsonSchema => Schema(id, normalized, answer, text),
            CheckKind.Tool => Tool(id, normalized, answer, text),
            CheckKind.Limits => Limits(id, normalized, answer, subject),
            _ => new CheckResult(id, CheckKind.Judge, CheckVerdict.Error, "An LLM judge runs only in a suite run."),
        };
    }

    /// <summary><c>the answer</c> or <c>the reference output</c>.</summary>
    public static string Subject(CheckSubject subject) => subject == CheckSubject.Reference ? "the reference output" : "the answer";

    /// <summary>A duration as the check lines say it: <c>850 ms</c> under a second, else <c>18.2 s</c>, <c>30 s</c>.</summary>
    public static string Duration(long milliseconds) => milliseconds < 1000
        ? milliseconds.ToString(Invariant) + " ms"
        : (milliseconds / 1000.0).ToString("#,0.#", Invariant) + " s";

    /// <summary>An amount as the check lines say it: <c>$0.03</c> for whole cents, else four decimals under a dollar (<c>$0.0115</c>).</summary>
    public static string Money(long micros) => SpendMoney.Format(micros, micros % 10_000 == 0 ? 2 : null);

    private static string IdOf(Check check, int index) => check.Id ?? "k" + (index + 1).ToString(Invariant);

    private static string Upper(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ─── Contains and regex ────────────────────────────────────────────────

    /// <summary>The text a contains or regex check reads, and how to say where it is; null text when its scope has nothing.</summary>
    private static (string? Text, string Where) Scoped(Check check, EvalAnswer answer, string subject)
    {
        if (check.Scope != CheckScope.FirstCodeBlock) return (answer.Text, subject);
        return AnswerParts.FirstCodeBlock(answer.Text) is { } block ? (block.Content, "the first code block of " + subject) : (null, subject);
    }

    private static CheckResult Contains(string id, Check check, EvalAnswer answer, string subject)
    {
        var (text, where) = Scoped(check, answer, subject);
        if (text is null) return new CheckResult(id, CheckKind.Contains, CheckVerdict.Failed, $"{Upper(subject)} has no code block.");
        var found = text.Contains(check.Text!, check.MatchCase == false ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return check.Negate == true
            ? found
                ? new CheckResult(id, CheckKind.Contains, CheckVerdict.Failed, $"Found in {where}, which must not contain it.")
                : new CheckResult(id, CheckKind.Contains, CheckVerdict.Passed, $"Not in {where}, as required.")
            : found
                ? new CheckResult(id, CheckKind.Contains, CheckVerdict.Passed, $"Found in {where}.")
                : new CheckResult(id, CheckKind.Contains, CheckVerdict.Failed, $"Not found in {where}.");
    }

    private static CheckResult Matches(string id, Check check, EvalAnswer answer, string subject)
    {
        var (text, where) = Scoped(check, answer, subject);
        if (text is null) return new CheckResult(id, CheckKind.Regex, CheckVerdict.Failed, $"{Upper(subject)} has no code block.");
        var regex = Check.Compile(check.Pattern!, "The pattern");
        Match match;
        var clock = Stopwatch.StartNew();
        try
        {
            match = regex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return new CheckResult(id, CheckKind.Regex, CheckVerdict.Failed, $"Timed out after {RegexLimit}.");
        }
        var took = clock.Elapsed.TotalMilliseconds;
        var time = took < 0.1 ? "under 0.1 ms" : took.ToString("0.0", Invariant) + " ms";
        return check.Negate == true
            ? match.Success
                ? new CheckResult(id, CheckKind.Regex, CheckVerdict.Failed, $"Matches {where} (\"{Excerpt(match.Value)}\"), which it must not.")
                : new CheckResult(id, CheckKind.Regex, CheckVerdict.Passed, $"Doesn't match {where}, as required ({time}).")
            : match.Success
                ? new CheckResult(id, CheckKind.Regex, CheckVerdict.Passed, $"Matches {where} in {time}.")
                : new CheckResult(id, CheckKind.Regex, CheckVerdict.Failed, $"Doesn't match {where}.");
    }

    private static string Excerpt(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= 40 ? line : line[..40] + "…";
    }

    // ─── JSON schema ───────────────────────────────────────────────────────

    private static CheckResult Schema(string id, Check check, EvalAnswer answer, string subject)
    {
        var found = AnswerParts.FindJson(answer.Text);
        if (found.Object is null)
            return new CheckResult(id, CheckKind.JsonSchema, CheckVerdict.Failed, found.Problem is { } problem
                ? $"In {subject}, {problem}." : $"No JSON object in {subject}.");
        IReadOnlyList<FieldError> errors;
        try
        {
            var value = ChordJson.Parse(found.Object.ToJsonString());
            errors = InputSchema.Validate(Check.ToInputSchema(check.Schema!), value);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new CheckResult(id, CheckKind.JsonSchema, CheckVerdict.Error, $"The JSON in {subject} could not be checked: {error.Message}");
        }
        if (errors.Count == 0) return new CheckResult(id, CheckKind.JsonSchema, CheckVerdict.Passed, $"{Upper(subject)} is valid.");
        var listed = string.Join("; ", errors.Take(3).Select(error => (error.Path.Length == 0 ? "(root)" : error.Path) + " " + error.Message));
        return new CheckResult(id, CheckKind.JsonSchema, CheckVerdict.Failed,
            $"{Upper(subject)} is not valid: {listed}{(errors.Count > 3 ? $"; and {errors.Count - 3} more" : "")}.");
    }

    // ─── Tool ──────────────────────────────────────────────────────────────

    private static CheckResult Tool(string id, Check check, EvalAnswer answer, string subject)
    {
        var tool = check.Tool!;
        var calls = answer.ToolCalls.Where(call => string.Equals(call.Name, tool, StringComparison.Ordinal)).ToList();
        var rules = check.Arguments ?? [];
        var who = Upper(subject);
        switch (check.Calls)
        {
            case ToolCallCount.Never:
                return calls.Count == 0
                    ? new CheckResult(id, CheckKind.Tool, CheckVerdict.Passed, $"{who} doesn't call {tool}.")
                    : new CheckResult(id, CheckKind.Tool, CheckVerdict.Failed, $"{who} calls {tool} {Times(calls.Count)}, which it must not.");
            case ToolCallCount.ExactlyOnce:
                if (calls.Count == 0) return new CheckResult(id, CheckKind.Tool, CheckVerdict.Failed, NotCalled(who, tool, answer));
                if (calls.Count > 1) return new CheckResult(id, CheckKind.Tool, CheckVerdict.Failed, $"{who} calls {tool} {Times(calls.Count)}, not once.");
                return Mismatch(calls[0].Arguments, rules) is { } wrong
                    ? new CheckResult(id, CheckKind.Tool, CheckVerdict.Failed, $"{who} calls {tool} once, but {wrong}.")
                    : new CheckResult(id, CheckKind.Tool, CheckVerdict.Passed, $"{who} calls {tool} once{(rules.Count > 0 ? ", with matching arguments" : "")}.");
            default:
                if (calls.Count == 0) return new CheckResult(id, CheckKind.Tool, CheckVerdict.Failed, NotCalled(who, tool, answer));
                if (rules.Count == 0 || calls.Any(call => Mismatch(call.Arguments, rules) is null))
                    return new CheckResult(id, CheckKind.Tool, CheckVerdict.Passed,
                        $"{who} calls {tool} {Times(calls.Count)}{(rules.Count > 0 ? ", with matching arguments" : "")}.");
                return new CheckResult(id, CheckKind.Tool, CheckVerdict.Failed, calls.Count == 1
                    ? $"{who} calls {tool} once, but {Mismatch(calls[0].Arguments, rules)}."
                    : $"{who} calls {tool} {Times(calls.Count)}, but no call matches: in the first, {Mismatch(calls[0].Arguments, rules)}.");
        }
    }

    private static string Times(int count) => count == 1 ? "once" : count == 2 ? "twice" : count.ToString(Invariant) + " times";

    private static string NotCalled(string who, string tool, EvalAnswer answer)
    {
        var others = answer.ToolCalls.Select(call => call.Name).Distinct(StringComparer.Ordinal).Take(5).ToList();
        return others.Count == 0 ? $"{who} doesn't call {tool}." : $"{who} doesn't call {tool} (it calls {string.Join(", ", others)}).";
    }

    /// <summary>What is wrong with <paramref name="arguments"/> under <paramref name="rules"/> (the first rule it breaks), or null when every
    /// rule holds.</summary>
    private static string? Mismatch(JsonObject? arguments, IReadOnlyList<ToolArgumentMatch> rules)
    {
        foreach (var rule in rules)
        {
            var present = Argument(arguments, rule.Name, out var value);
            switch (rule.Rule)
            {
                case ArgumentRule.Present:
                    if (!present || value is null) return $"{rule.Name} is missing";
                    break;
                case ArgumentRule.Regex:
                    if (!present || value is null) return $"{rule.Name} is missing";
                    var regex = Check.Compile(rule.Value!, $"The regex for {rule.Name}");
                    try
                    {
                        if (!regex.IsMatch(Text(value))) return $"{rule.Name} ({Shown(value)}) doesn't match /{rule.Value}/";
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return $"the regex for {rule.Name} timed out after {RegexLimit}";
                    }
                    break;
                default:
                    if (!present) return $"{rule.Name} is missing";
                    if (!Same(value, rule.Value!)) return $"{rule.Name} is {Shown(value)}, not {Expected(value, rule.Value!)}";
                    break;
            }
        }
        return null;
    }

    /// <summary>The argument at <paramref name="name"/>: a top-level name as it is (<c>a.b</c> may be a name), else a dotted path into
    /// objects and arrays (<c>location.city</c>, <c>items.0.id</c>). False when it is not there; a JSON null is there, with a null value.</summary>
    private static bool Argument(JsonObject? arguments, string name, out JsonNode? value)
    {
        value = null;
        if (arguments is null) return false;
        if (arguments.TryGetPropertyValue(name, out value)) return true;
        JsonNode? current = arguments;
        foreach (var part in name.Split('.'))
        {
            switch (current)
            {
                case JsonObject json when json.TryGetPropertyValue(part, out var next):
                    current = next;
                    break;
                case JsonArray array when int.TryParse(part, NumberStyles.None, Invariant, out var index) && index < array.Count:
                    current = array[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }
        value = current;
        return true;
    }

    /// <summary>An argument as text: a string as it is, anything else as its JSON.</summary>
    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "null";

    /// <summary>An argument as the check line shows it: its JSON, at most 60 characters.</summary>
    private static string Shown(JsonNode? value)
    {
        var json = value?.ToJsonString() ?? "null";
        return json.Length <= 60 ? json : json[..60] + "…";
    }

    /// <summary>The expected value as the check line shows it: as JSON text when the argument is a string (<c>"high"</c>), else as typed.</summary>
    private static string Expected(JsonNode? actual, string expected)
    {
        var shown = actual is JsonValue scalar && scalar.TryGetValue<string>(out _) ? System.Text.Json.JsonSerializer.Serialize(expected) : expected;
        return shown.Length <= 60 ? shown : shown[..60] + "…";
    }

    /// <summary>Whether an argument equals the rule's value: a string argument as text (ordinal); a number numerically when the value is a
    /// number; anything else as JSON (the value parsed, compared deeply), else by its JSON text.</summary>
    private static bool Same(JsonNode? actual, string expected)
    {
        if (actual is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text)) return string.Equals(text, expected, StringComparison.Ordinal);
            if (scalar.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
                decimal.TryParse(expected.Trim(), NumberStyles.Float, Invariant, out var number) &&
                decimal.TryParse(scalar.ToJsonString(), NumberStyles.Float, Invariant, out var given))
                return number == given;
        }
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(expected);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return string.Equals(actual?.ToJsonString() ?? "null", expected.Trim(), StringComparison.Ordinal);
        }
        return JsonNode.DeepEquals(actual, parsed);
    }

    // ─── Limits ────────────────────────────────────────────────────────────

    private static CheckResult Limits(string id, Check check, EvalAnswer answer, CheckSubject subject)
    {
        var parts = new List<string>(2);
        var over = false;
        var unknown = false;
        if (check.MaxCostMicros is { } maxCost)
        {
            if (answer.CostMicros is { } cost)
            {
                var amount = answer.ListPrice ? $"≈ {Money(cost)} at list price" : Money(cost);
                if (cost > maxCost) { over = true; parts.Add($"{amount} (over the {Money(maxCost)} limit)"); }
                else parts.Add(amount);
            }
            else
            {
                unknown = true;
                parts.Add("cost unknown");
            }
        }
        if (check.MaxLatencyMs is { } maxLatency)
        {
            if (answer.DurationMs is { } duration)
            {
                if (duration > maxLatency) { over = true; parts.Add($"{Duration(duration)} (over the {Duration(maxLatency)} limit)"); }
                else parts.Add(Duration(duration));
            }
            else
            {
                unknown = true;
                parts.Add("time unknown");
            }
        }
        var detail = (subject == CheckSubject.Reference ? "Reference output: " : "Answer: ") + string.Join(", ", parts) + ".";
        return new CheckResult(id, CheckKind.Limits, over ? CheckVerdict.Failed : unknown ? CheckVerdict.Error : CheckVerdict.Passed, detail);
    }
}
