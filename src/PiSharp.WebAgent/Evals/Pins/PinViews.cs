// Decision 0011 (run W5, E7): the model of _TestCaseModal (mockup modal-test-case) and its words. The modal is the same on every host: a
// header, Name and Tags, the Suite select with one pane per suite (what adding the case does to that suite: its targets, k, schedule, the
// answers it adds per run and the metered worst case before and after, against the suite-run cap; over the cap, "The schedule pauses until
// you raise the cap.") and the New suite pane, the Request panel (logo, provider/model, the options line, the last user message, what the
// request holds, and the account line: the account is never part of a case), the Reference output (its tokens and time, Show all) and the
// Checks to start with (each with whether it passes on the reference output, the quick Add check panel). Add mode (a suite page's Add case)
// asks for the request instead, and Edit mode (Edit case) keeps the suite and request and changes the name, tags and checks.
using System.Globalization;
using PiSharp.WebAgent.Evals.Checks;
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Evals.Pins;

/// <summary>A suite the case can go to, with its pane.</summary>
/// <param name="Id">The suite id.</param>
/// <param name="Label">The option's text (<c>Regex edge cases · 12 cases</c>).</param>
/// <param name="Pane">What adding the case does to it.</param>
/// <param name="OverCap">Whether the worst case would be above its cap.</param>
/// <param name="Pauses">Whether its active schedule would pause.</param>
/// <param name="Selected">Whether it is selected.</param>
public sealed record PinSuiteOption(string Id, string Label, string Pane, bool OverCap, bool Pauses, bool Selected);

/// <summary>A check row of the modal.</summary>
/// <param name="Json">The check as saved (JSON, for pin.js to send back).</param>
/// <param name="Label">The kind (<c>Contains</c>, <c>Regex</c>, <c>Cost and latency</c>).</param>
/// <param name="Code">The text or pattern to show as code, or null.</param>
/// <param name="Summary">The check in words (for kinds without code).</param>
/// <param name="Limit">The regex time limit note, or null.</param>
/// <param name="Verdict">What it makes of the reference output (<c>passed</c>, <c>failed</c>, <c>error</c>), or null without one.</param>
/// <param name="Detail">The verdict's detail (<c>Found in the reference output.</c>), or null.</param>
public sealed record PinCheckRow(string Json, string Label, string? Code, string Summary, string? Limit, string? Verdict, string? Detail)
{
    /// <summary>Whether it does not pass on the reference output.</summary>
    public bool Fails => Verdict is "failed" or "error";
}

/// <summary>The model of <c>Views/Shared/_TestCaseModal.cshtml</c> with content (a null model renders the empty shell a page hosts).</summary>
public sealed record TestCaseModalView
{
    /// <summary>What the modal does.</summary>
    public PinMode Mode { get; init; }

    /// <summary>Why the pin cannot be made, or null. A refused modal shows only this (no model or account names: a battle may be blind).</summary>
    public string? Refusal { get; init; }

    /// <summary>The target, as pin.js sends it back (JSON).</summary>
    public string Target { get; init; } = "{}";

    /// <summary>The case id the save uses (made when the modal opens, so a retry saves once).</summary>
    public string CaseId { get; init; } = "";

    /// <summary>The id a new suite gets (made when the modal opens, so a retry makes it once).</summary>
    public string NewSuiteId { get; init; } = "";

    /// <summary>The title.</summary>
    public string Title { get; init; } = "Pin as test case";

    /// <summary>The subtitle.</summary>
    public string Subtitle { get; init; } = "Saves the full request, with this answer as the reference output.";

    /// <summary>The name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The tags, comma-separated.</summary>
    public string Tags { get; init; } = "";

    /// <summary>The suites (Pin mode: every suite; Add and Edit: the case's own, fixed).</summary>
    public IReadOnlyList<PinSuiteOption> Suites { get; init; } = [];

    /// <summary>Whether the suite is fixed (Add and Edit modes).</summary>
    public bool SuiteFixed { get; init; }

    /// <summary>Whether New suite… is offered.</summary>
    public bool NewSuiteOffered { get; init; }

    /// <summary>Whether New suite… is selected (there is no suite yet).</summary>
    public bool NewSuiteSelected { get; init; }

    /// <summary>The provider id of the request, or null.</summary>
    public string? Provider { get; init; }

    /// <summary>The model id of the request, or null.</summary>
    public string? Model { get; init; }

    /// <summary>The provider's name, for the logo.</summary>
    public string ProviderName { get; init; } = "";

    /// <summary>The provider's logo URL, or null for its initial.</summary>
    public string? LogoUrl { get; init; }

    /// <summary>The options line (<c>thinking medium · maxTokens 2,000 · service tier default · no tools</c>).</summary>
    public string OptionsLine { get; init; } = "";

    /// <summary>The last user message's text (cut), or null.</summary>
    public string? Prompt { get; init; }

    /// <summary>What the request holds (<c>System prompt and 1 user message · 248 input tokens</c>).</summary>
    public string MessagesLine { get; init; } = "";

    /// <summary>The run the request came from, or null.</summary>
    public string? RunId { get; init; }

    /// <summary>The account line.</summary>
    public string AccountLine { get; init; } = "";

    /// <summary>Whether there is a reference output.</summary>
    public bool HasReference { get; init; }

    /// <summary>The reference's figures (<c>1,104 output tokens · 18.2 s</c>).</summary>
    public string ReferenceLine { get; init; } = "";

    /// <summary>The reference text's start (or all of it).</summary>
    public string ReferenceText { get; init; } = "";

    /// <summary>The rest of the reference text (Show all), or null.</summary>
    public string? ReferenceMore { get; init; }

    /// <summary>The tool calls of the reference (<c>get_weather</c>), or empty.</summary>
    public IReadOnlyList<string> ReferenceCalls { get; init; } = [];

    /// <summary>The checks.</summary>
    public IReadOnlyList<PinCheckRow> Checks { get; init; } = [];

    /// <summary>What the checks make of the reference (<c>all 3 pass on the reference output</c>).</summary>
    public string ChecksLine { get; init; } = "";

    /// <summary>Add mode: the suite's first target, which the typed request starts on (<c>openai/gpt-5</c>).</summary>
    public string? AddTarget { get; init; }

    /// <summary>The save button's text.</summary>
    public string SaveText { get; init; } = "Save test case";
}

/// <summary>The modal's words.</summary>
public static class PinText
{
    /// <summary>The longest reference text shown before Show all.</summary>
    public const int ReferenceShown = 1200;

    /// <summary>The longest reference text the modal carries at all (the rest is in the saved case).</summary>
    public const int ReferenceMost = 20_000;

    /// <summary>What a list of checks makes of the reference output (pin.js says the same: keep the two in step).</summary>
    /// <param name="count">How many checks.</param>
    /// <param name="passed">How many pass on the reference.</param>
    /// <param name="hasReference">Whether there is a reference output.</param>
    public static string ChecksLine(int count, int passed, bool hasReference) =>
        count == 0 ? "none yet"
        : !hasReference ? (count == 1 ? "1 check" : count.ToString(CultureInfo.InvariantCulture) + " checks") + " · no reference output to try them on"
        : passed == count ? (count == 1 ? "it passes on the reference output" : $"all {count} pass on the reference output")
        : $"{passed} of {count} pass on the reference output";

    /// <summary>The options line of a request: <c>thinking medium · maxTokens 2,000 · temperature 0.2 · service tier default · no tools</c>.</summary>
    public static string Options(RunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var parts = new List<string> { "thinking " + (spec.ThinkingLevel ?? "off") };
        if (spec.MaxTokens is { } max) parts.Add("maxTokens " + max.ToString("N0", CultureInfo.InvariantCulture));
        if (spec.Temperature is { } temperature) parts.Add("temperature " + temperature.ToString("0.##", CultureInfo.InvariantCulture));
        parts.Add("service tier " + (spec.ServiceTier ?? "default"));
        if (spec.ResponseFormat?["type"] is System.Text.Json.Nodes.JsonValue format && format.TryGetValue<string>(out var kind) && kind != "text") parts.Add("response " + kind);
        var tools = spec.Tools?.Count ?? 0;
        parts.Add(tools == 0 ? "no tools" : tools == 1 ? "1 tool" : tools.ToString(CultureInfo.InvariantCulture) + " tools");
        return string.Join(" · ", parts);
    }

    /// <summary>What a request holds: <c>System prompt and 1 user message · 248 input tokens</c>, <c>3 user messages, 2 answers and 4 tool
    /// results</c>.</summary>
    public static string Messages(RunSpec spec, double? inputTokens)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var parts = new List<string>();
        void Count(string role, string one, string many)
        {
            var count = spec.Messages.Count(message => message.Role == role);
            if (count > 0) parts.Add(count == 1 ? "1 " + one : count.ToString("N0", CultureInfo.InvariantCulture) + " " + many);
        }
        Count("user", "user message", "user messages");
        Count("assistant", "answer", "answers");
        Count("toolResult", "tool result", "tool results");
        var images = spec.Messages.Sum(message => message.Images?.Count ?? 0);
        if (images > 0) parts.Add(images == 1 ? "1 image" : images.ToString(CultureInfo.InvariantCulture) + " images");
        var system = !string.IsNullOrEmpty(spec.SystemPrompt) || spec.Messages.Any(message => message.Role == "system");
        var text = system ? (parts.Count == 0 ? "System prompt" : "System prompt and " + Join(parts)) : parts.Count == 0 ? "No messages" : Upper(Join(parts));
        return inputTokens is > 0 ? text + " · " + inputTokens.Value.ToString("N0", CultureInfo.InvariantCulture) + " input tokens" : text;
    }

    /// <summary>The reference's figures: <c>1,104 output tokens · 18.2 s</c> (with its cost when known).</summary>
    public static string Reference(TestCaseReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var parts = new List<string>();
        if (reference.OutputTokens is > 0) parts.Add(reference.OutputTokens.Value.ToString("N0", CultureInfo.InvariantCulture) + " output tokens");
        if (reference.DurationMs is { } duration) parts.Add(CheckRunner.Duration(duration));
        if (reference.CostMicros is > 0) parts.Add(DriftText.Money(reference.CostMicros.Value, reference.ListPrice));
        if (reference.StopReason is { Length: > 0 } stop && stop != "stop") parts.Add("stop " + stop);
        return parts.Count == 0 ? "no usage recorded" : string.Join(" · ", parts);
    }

    /// <summary>The pane of a suite: what adding one case does to it (mockup modal-test-case's tcase-suite panes).</summary>
    /// <param name="suite">The suite.</param>
    /// <param name="before">Its worst case now.</param>
    /// <param name="after">Its worst case with the case.</param>
    public static (string Text, bool OverCap, bool Pauses) Pane(Suite suite, SuiteEstimate before, SuiteEstimate after)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var targets = suite.Targets.Count;
        if (targets == 0) return ("Has no targets yet: add one on the suite page before it runs.", false, false);
        var text = $"Runs on {targets} target{(targets == 1 ? "" : "s")} with k {suite.K}" + suite.Schedule switch
        {
            { Enabled: true, Paused: not null } schedule => ", " + Lower(schedule.Label) + " (paused). ",
            { Enabled: true } schedule => ", " + Lower(schedule.Label) + ". ",
            _ => ", by hand only. ",
        };
        var answers = targets * suite.K;
        var added = Math.Max(0, after.WorstMicros - before.WorstMicros);
        text += $"Adds {answers} answer{(answers == 1 ? "" : "s")} per run and up to {DriftText.Money(added)} to the metered worst case, which becomes {DriftText.Money(after.WorstMicros)}";
        var over = suite.CapMicros is { } cap && !after.Fits(cap);
        var pauses = over && suite.Schedule is { Active: true };
        text += suite.CapMicros is { } limit
            ? (over ? $", above the {DriftText.Money(limit)} cap." : $", within the {DriftText.Money(limit)} cap.")
            : ". It has no suite-run cap.";
        if (pauses) text += " The schedule pauses until you raise the cap.";
        else if (over) text += " A run of it is refused until you raise the cap.";
        if (after.HasUnknownPrice) text += " Some of its models have no known price, so a scheduled run is skipped until one is entered.";
        return (text.Trim(), over, pauses);
    }

    /// <summary>The toast after a save: where the case went and when it runs.</summary>
    /// <param name="suite">The suite as saved.</param>
    /// <param name="created">Whether the suite was made by this save.</param>
    /// <param name="now">Now.</param>
    public static string Saved(Suite suite, bool created, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(suite);
        if (created) return $"Made the suite {suite.Name}, with this request's model as its only target. Add targets and a schedule on the suite page.";
        var text = $"Added to {suite.Name}.";
        switch (suite.Schedule)
        {
            case { Enabled: true, Paused: { } pause }:
                return text + " Its schedule is paused: " + pause.Reason;
            case { Enabled: true } schedule:
                try
                {
                    var zone = schedule.Zone();
                    var next = TimeZoneInfo.ConvertTime(schedule.Next(now, zone), zone);
                    var today = TimeZoneInfo.ConvertTime(now, zone).Date;
                    var day = next.Date == today ? "today" : next.Date == today.AddDays(1) ? "tomorrow" : "on " + next.ToString("ddd d MMM", CultureInfo.InvariantCulture);
                    return text + $" It runs with the suite {day} at {next.ToString("HH:mm", CultureInfo.InvariantCulture)}.";
                }
                catch (InvalidOperationException)
                {
                    return text;
                }
            default:
                return text + " It runs when you run the suite.";
        }
    }

    /// <summary>A check as a row of the modal, with what it makes of <paramref name="reference"/>.</summary>
    public static PinCheckRow Row(Check check, TestCaseReference? reference)
    {
        ArgumentNullException.ThrowIfNull(check);
        var code = check.Kind switch
        {
            CheckKind.Contains => check.Text,
            CheckKind.Regex => check.Pattern,
            _ => null,
        };
        string? verdict = null;
        string? detail = null;
        if (reference is not null)
        {
            var result = CheckRunner.Evaluate(check, reference.ToAnswer(), CheckSubject.Reference);
            verdict = EvalJson.Wire(result.Verdict);
            detail = result.Detail;
        }
        else if (check.Kind == CheckKind.Judge) detail = "An LLM judge runs only in a suite run.";
        var summary = CheckText.Summary(check);
        if (code is not null && (check.Negate == true || check.Scope == CheckScope.FirstCodeBlock || check.MatchCase == false)) code = null;
        return new PinCheckRow(EvalJson.Serialize(check), check.Label, code, summary, check.Kind == CheckKind.Regex ? "100 ms limit" : null, verdict, detail);
    }

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    private static string Upper(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
