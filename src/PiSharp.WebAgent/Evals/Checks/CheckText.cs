// Decision 0011 (run W5, E2): a check in one line, for the suite page's case list and the test case modal's check rows (mockup chat.html
// modal-test-case: "Contains \"1.0.0-alpha\"", "Matches regex ... (100 ms limit)", "at most $0.03 and 30 s per answer"; evals-suite's case
// rows). Long texts and patterns are cut at 60 characters with an ellipsis; the kind's own label is Check.Label.
using System.Globalization;

namespace PiSharp.WebAgent.Evals.Checks;

/// <summary>Checks in words.</summary>
public static class CheckText
{
    /// <summary>The longest text or pattern quoted in a summary.</summary>
    public const int MaxQuoted = 60;

    /// <summary><paramref name="check"/> in one line: <c>Contains "1.0.0-alpha"</c>, <c>Doesn't match /TODO/ (100 ms limit)</c>, <c>Valid JSON
    /// with name, id, tags</c>, <c>Calls create_ticket exactly once, priority = "high"</c>, <c>At most $0.03 and 30 s per answer</c>, <c>LLM
    /// judge google/gemini-3-flash, passes at 4 of 5</c>.</summary>
    public static string Summary(Check check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var where = check.Scope == CheckScope.FirstCodeBlock ? "First code block " : "";
        switch (check.Kind)
        {
            case CheckKind.Contains:
                var verb = check.Negate == true ? "doesn't contain" : "contains";
                return Sentence(where + verb) + " " + Quote(check.Text) + (check.MatchCase == false ? " (any case)" : "");
            case CheckKind.Regex:
                var match = check.Negate == true ? "doesn't match" : "matches";
                return Sentence(where + match) + " /" + Cut(check.Pattern) + "/ (100 ms limit)";
            case CheckKind.JsonSchema:
                var fields = check.Schema?["properties"] is System.Text.Json.Nodes.JsonObject properties
                    ? properties.Select(property => property.Key).ToList() : [];
                return fields.Count == 0 ? "Valid JSON (schema)"
                    : "Valid JSON with " + Cut(string.Join(", ", fields.Take(6)) + (fields.Count > 6 ? ", ..." : ""));
            case CheckKind.Tool:
                var tool = check.Tool ?? "a tool";
                var calls = check.Calls switch
                {
                    ToolCallCount.Never => "Never calls " + tool,
                    ToolCallCount.ExactlyOnce => "Calls " + tool + " exactly once",
                    _ => "Calls " + tool,
                };
                var rules = (check.Arguments ?? []).Select(Rule).ToList();
                return rules.Count == 0 ? calls : calls + ", " + string.Join(", ", rules.Take(3)) + (rules.Count > 3 ? $", and {rules.Count - 3} more" : "");
            case CheckKind.Limits:
                var limits = new List<string>(2);
                if (check.MaxCostMicros is { } cost) limits.Add(CheckRunner.Money(cost));
                if (check.MaxLatencyMs is { } latency) limits.Add(CheckRunner.Duration(latency));
                return limits.Count == 0 ? "Cost and latency" : "At most " + string.Join(" and ", limits) + " per answer";
            default:
                return check.Judge is { } judge
                    ? string.Create(CultureInfo.InvariantCulture, $"LLM judge {judge.Provider}/{judge.Model}, passes at {judge.PassAt} of {JudgeSpec.MaxScore}")
                    : "LLM judge";
        }
    }

    private static string Rule(ToolArgumentMatch rule) => rule.Rule switch
    {
        ArgumentRule.Present => rule.Name + " present",
        ArgumentRule.Regex => rule.Name + " matches /" + Cut(rule.Value) + "/",
        _ => rule.Name + " = " + Quote(rule.Value),
    };

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Quote(string? text) => "\"" + Cut(text) + "\"";

    private static string Cut(string? text)
    {
        var line = (text ?? "").ReplaceLineEndings(" ");
        return line.Length <= MaxQuoted ? line : line[..MaxQuoted] + "…";
    }
}
