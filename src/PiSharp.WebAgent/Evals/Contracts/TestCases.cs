// Decision 0011 (run W5, E0): test cases (mockup modal-test-case, _TestCaseModal; evals-suite _SuiteCases). A case is pinned from a run (a
// chat message, Playground, Compare, an Arena reveal, History or the inspector) or added on a suite page: it keeps the full request as a
// RunSpec, the pinned answer as its reference output, the checks to run on every answer, and where it came from. The request never holds a
// secret (RunSpec refuses credential headers and option names at any depth; CaseSpec refuses a header, session id or provider option value
// that looks like a key or holds one of the app's stored credentials, W5 fix 1) and never an account: "each suite target picks its own
// provider, model and account", so Normalize clears the account, and the per-run values that are refused by design (base URL, the Azure
// endpoint, resource and deployment, Bedrock) and the run's own grouping (source, group, parent). A case belongs to one suite.
//
// EvalAnswer is what checks read: an answer's text, its tool calls, the model that answered (responseModel), its stop reason or error, and
// its cost and time, from a runs.db record (a suite run's answer) or a case's reference output (the check editor's "passes on the
// reference" line).
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Presets;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Evals;

/// <summary>Where a test case was pinned from. Sent as <c>chat</c>, <c>playground</c>, <c>compare</c>, <c>arena</c>, <c>bench</c>,
/// <c>history</c>, <c>inspector</c>, <c>suite</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PinSource>))]
public enum PinSource
{
    /// <summary>A chat message's menu.</summary>
    [JsonStringEnumMemberName("chat")] Chat,

    /// <summary>The Playground's output.</summary>
    [JsonStringEnumMemberName("playground")] Playground,

    /// <summary>A Compare column (request mode).</summary>
    [JsonStringEnumMemberName("compare")] Compare,

    /// <summary>An Arena reveal, after the vote.</summary>
    [JsonStringEnumMemberName("arena")] Arena,

    /// <summary>A Tool Bench turn.</summary>
    [JsonStringEnumMemberName("bench")] Bench,

    /// <summary>A History row's menu.</summary>
    [JsonStringEnumMemberName("history")] History,

    /// <summary>The Run inspector.</summary>
    [JsonStringEnumMemberName("inspector")] Inspector,

    /// <summary>Added on a suite page (Add case).</summary>
    [JsonStringEnumMemberName("suite")] Suite,
}

/// <summary>Where a test case came from.</summary>
/// <param name="Source">The page it was pinned on.</param>
/// <param name="RunId">The runs.db row it was pinned from, or null (a chat message whose call was not recorded, a case added by hand).</param>
/// <param name="ChatId">The chat, when pinned from a chat message.</param>
/// <param name="EntryId">The chat entry (message), when pinned from a chat message.</param>
/// <param name="BattleId">The Arena battle, when pinned from a reveal.</param>
public sealed record TestCaseOrigin(PinSource Source, string? RunId = null, string? ChatId = null, string? EntryId = null, string? BattleId = null)
{
    /// <summary>The same origin, trimmed.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public TestCaseOrigin Normalize()
    {
        if (!Enum.IsDefined(Source)) throw new ArgumentException($"Unknown pin source {(int)Source}.");
        return new TestCaseOrigin(Source, Id(RunId, "run"), Id(ChatId, "chat"), Id(EntryId, "entry"),
            BattleId is null || string.IsNullOrWhiteSpace(BattleId) ? null : EvalIds.Require(EvalIds.Battle, BattleId, "battle"));
    }

    private static string? Id(string? value, string what)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is not null && (text.Length > 200 || text.Any(char.IsControl))) throw new ArgumentException($"The {what} id is not allowed.");
        return text;
    }
}

/// <summary>A case's reference output: the pinned answer and what it cost.</summary>
public sealed record TestCaseReference
{
    /// <summary>The largest reference text kept, in characters.</summary>
    public const int MaxTextLength = 1_000_000;

    /// <summary>The answer's text (its text blocks, joined).</summary>
    public string? Text { get; init; }

    /// <summary>The final assistant message as Pi wire JSON (text, thinking and tool call blocks), or null.</summary>
    public JsonNode? Output { get; init; }

    /// <summary>The provider that answered.</summary>
    public string? Provider { get; init; }

    /// <summary>The model that was asked.</summary>
    public string? Model { get; init; }

    /// <summary>The model the provider said answered (the final message's <c>responseModel</c>, else its <c>model</c>), or null.</summary>
    public string? ResponseModel { get; init; }

    /// <summary>The stop reason (<c>stop</c>, <c>length</c>, <c>toolUse</c>, ...), or null.</summary>
    public string? StopReason { get; init; }

    /// <summary>Input tokens, or null.</summary>
    public double? InputTokens { get; init; }

    /// <summary>Output tokens, or null.</summary>
    public double? OutputTokens { get; init; }

    /// <summary>What it cost in micro-USD (list price when <see cref="ListPrice"/>), or null.</summary>
    public long? CostMicros { get; init; }

    /// <summary>Whether <see cref="CostMicros"/> is a subscription's list price.</summary>
    public bool ListPrice { get; init; }

    /// <summary>Milliseconds from sending to the last token, or null.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Milliseconds to the first token, or null.</summary>
    public long? TtftMs { get; init; }

    /// <summary>The runs.db row it came from, or null.</summary>
    public string? RunId { get; init; }

    /// <summary>The reference of a recorded run: its output, text, usage, cost (list price for a subscription) and timing.</summary>
    public static TestCaseReference FromRun(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var answer = EvalAnswer.FromRun(run);
        return new TestCaseReference
        {
            Text = answer.Text,
            Output = run.Output?.DeepClone(),
            Provider = run.Provider,
            Model = run.Model,
            ResponseModel = answer.ResponseModel,
            StopReason = run.StopReason,
            InputTokens = run.Usage.Input,
            OutputTokens = run.Usage.Output,
            CostMicros = run.CostMicros,
            ListPrice = answer.ListPrice,
            DurationMs = run.DurationMs,
            TtftMs = run.TtftMs,
            RunId = run.Id,
        };
    }

    /// <summary>The same reference, checked.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public TestCaseReference Normalize()
    {
        if (Text is { Length: > MaxTextLength }) throw new ArgumentException($"A reference output is at most {MaxTextLength:N0} characters.");
        if (Output is not (null or JsonObject)) throw new ArgumentException("A reference output's message is a JSON object.");
        if (CostMicros is < 0 || DurationMs is < 0 || TtftMs is < 0 || InputTokens is < 0 || OutputTokens is < 0)
            throw new ArgumentException("A reference output's cost, tokens and times are never negative.");
        return this with
        {
            Provider = Trim(Provider),
            Model = Trim(Model),
            ResponseModel = Trim(ResponseModel),
            StopReason = Trim(StopReason),
            RunId = Trim(RunId),
        };
    }

    /// <summary>The reference as an answer the checks can read.</summary>
    public EvalAnswer ToAnswer()
    {
        var answer = EvalAnswer.FromOutput(Output, Text);
        return answer with
        {
            ResponseModel = ResponseModel ?? answer.ResponseModel,
            StopReason = StopReason ?? answer.StopReason,
            CostMicros = CostMicros,
            ListPrice = ListPrice,
            DurationMs = DurationMs,
            TtftMs = TtftMs,
            OutputTokens = OutputTokens,
        };
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A test case: a full request, its reference output, its checks and where it came from.</summary>
public sealed record TestCase
{
    /// <summary>The longest name.</summary>
    public const int MaxName = 80;

    /// <summary>The most checks a case has.</summary>
    public const int MaxChecks = 20;

    /// <summary>The case id (<see cref="EvalIds.Case"/>).</summary>
    public required string Id { get; init; }

    /// <summary>The suite it belongs to (<see cref="EvalIds.Suite"/>).</summary>
    public required string SuiteId { get; init; }

    /// <summary>Its name (1 to <see cref="MaxName"/> characters), such as <c>SemVer 2.0.0 with pre-release and build</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Its tags (<see cref="EvalTags"/>), such as <c>coding</c>, <c>regex</c>.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The full request as pinned: messages, system prompt, tools and options; never an account or a secret. A suite target
    /// replaces its provider, model, account, thinking, temperature and service tier (<see cref="SuiteTarget.Apply"/>).</summary>
    public required RunSpec Spec { get; init; }

    /// <summary>The reference output (the pinned answer), or null for a case added without one.</summary>
    public TestCaseReference? Reference { get; init; }

    /// <summary>The checks every answer must pass, in order (at most <see cref="MaxChecks"/>).</summary>
    public IReadOnlyList<Check> Checks { get; init; } = [];

    /// <summary>Where it came from.</summary>
    public required TestCaseOrigin Origin { get; init; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset Updated { get; init; }

    /// <summary>The same case, checked and stored as it should be: the request normalized without its account, grouping or refused per-run
    /// values, and holding no credential (<see cref="CaseSpec"/>); tags normalized; every check normalized with a unique id (missing ids
    /// become <c>k1</c>, <c>k2</c>, ...).</summary>
    /// <param name="storedSecret">Whether a text holds one of the app's stored credentials (the credential vault's matcher), or null where
    /// there is no vault.</param>
    /// <exception cref="ArgumentException">Something is not allowed; the message says what, for the page to show.</exception>
    public TestCase Normalize(Func<string?, bool>? storedSecret = null)
    {
        var id = EvalIds.Require(EvalIds.Case, Id, "test case");
        var suite = EvalIds.Require(EvalIds.Suite, SuiteId, "suite");
        var name = Name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("Give the test case a name.");
        if (name.Length > MaxName || name.Any(char.IsControl)) throw new ArgumentException($"A test case's name is at most {MaxName} characters on one line.");
        var spec = CaseSpec(Spec ?? throw new ArgumentException("A test case needs its request."), storedSecret);
        var checks = (Checks ?? []).ToList();
        if (checks.Count > MaxChecks) throw new ArgumentException($"A test case has at most {MaxChecks} checks.");
        if (checks.Any(check => check is null)) throw new ArgumentException("A check is empty.");
        var normalized = checks.Select(check => check.Normalize()).ToList();
        var used = new HashSet<string>(normalized.Where(check => check.Id is not null).Select(check => check.Id!), StringComparer.Ordinal);
        if (used.Count != normalized.Count(check => check.Id is not null)) throw new ArgumentException("Two checks share an id.");
        var next = 1;
        for (var index = 0; index < normalized.Count; index++)
        {
            if (normalized[index].Id is not null) continue;
            while (used.Contains("k" + next)) next++;
            normalized[index] = normalized[index] with { Id = "k" + next };
            used.Add("k" + next);
        }
        return this with
        {
            Id = id,
            SuiteId = suite,
            Name = name,
            Tags = EvalTags.NormalizeList(Tags),
            Spec = spec,
            Reference = Reference?.Normalize(),
            Checks = normalized,
            Origin = (Origin ?? throw new ArgumentException("A test case needs to say where it came from.")).Normalize(),
        };
    }

    /// <summary><paramref name="spec"/> as a case stores it: normalized (RunSpec's rules, which refuse a credential's name in a header or at
    /// any depth of the provider options), without the account, the run's source, group, parent and title, and without the per-run values
    /// that are refused by design (base URL, Azure base URL, resource and deployment, Bedrock options). A case is also refused when a header
    /// value, the session id, or a provider option's name or text value (at any depth) looks like a credential (a bearer or basic value, or a
    /// well-known key prefix: <see cref="PresetRules.LooksSecret"/>) or holds one of the app's stored credentials
    /// (<paramref name="storedSecret"/>). A refusal names where, never the value.</summary>
    /// <param name="spec">The request.</param>
    /// <param name="storedSecret">Whether a text holds one of the app's stored credentials, or null where there is no credential vault.</param>
    /// <exception cref="ArgumentException">The request is not valid, or holds a credential.</exception>
    public static RunSpec CaseSpec(RunSpec spec, Func<string?, bool>? storedSecret = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        // Stripped before normalizing, so a request pinned from a chat (source chat, which a run may not use) is still a case.
        var normalized = (spec with
        {
            Account = null,
            Source = null,
            GroupId = null,
            ParentId = null,
            Title = null,
            BaseUrl = null,
            AzureBaseUrl = null,
            AzureResourceName = null,
            AzureDeploymentName = null,
            Bedrock = null,
        }).Normalize();
        bool Secret(string? text) => PresetRules.LooksSecret(text) || storedSecret?.Invoke(text) == true;
        foreach (var (name, value) in normalized.Headers ?? new Dictionary<string, string>())
            if (Secret(value)) throw new ArgumentException($"The header {name} holds what looks like a key or token, and a test case never stores a credential.");
        if (Secret(normalized.SessionId)) throw new ArgumentException("The session id looks like a key or token, and a test case never stores a credential.");
        if (normalized.ProviderOptions is { } options) CheckOptionValues(options, "providerOptions", Secret);
        return normalized;
    }

    private static void CheckOptionValues(JsonNode? node, string path, Func<string?, bool> secret)
    {
        switch (node)
        {
            case JsonObject json:
                foreach (var (key, value) in json)
                {
                    if (secret(key)) throw new ArgumentException($"A name in {path} looks like a key or token, and a test case never stores a credential.");
                    CheckOptionValues(value, path + "." + key, secret);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++) CheckOptionValues(array[index], $"{path}[{index}]", secret);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && secret(text):
                throw new ArgumentException($"{path} holds what looks like a key or token, and a test case never stores a credential.");
        }
    }
}

/// <summary>A tool call of an answer, as a tool check reads it.</summary>
/// <param name="Name">The tool's name.</param>
/// <param name="Arguments">Its arguments, or null.</param>
/// <param name="Id">The call id, or null.</param>
public sealed record EvalToolCall(string Name, JsonObject? Arguments, string? Id = null);

/// <summary>One answer as the checks read it.</summary>
public sealed record EvalAnswer
{
    /// <summary>The text (the text blocks, joined; empty when there were none).</summary>
    public string Text { get; init; } = "";

    /// <summary>The tool calls, in order.</summary>
    public IReadOnlyList<EvalToolCall> ToolCalls { get; init; } = [];

    /// <summary>The model the provider said answered: the final message's <c>responseModel</c> (Pi sets it when the provider names the
    /// model that answered), else its <c>model</c>; null when there is no message.</summary>
    public string? ResponseModel { get; init; }

    /// <summary>The stop reason, or null.</summary>
    public string? StopReason { get; init; }

    /// <summary>The error, when the call failed; null otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>The settled cost in micro-USD (list price when <see cref="ListPrice"/>), or null when unknown.</summary>
    public long? CostMicros { get; init; }

    /// <summary>Whether <see cref="CostMicros"/> is a subscription's list price.</summary>
    public bool ListPrice { get; init; }

    /// <summary>Milliseconds from sending the request to the last token, or null.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Milliseconds to the first token, or null.</summary>
    public long? TtftMs { get; init; }

    /// <summary>Output tokens, or null.</summary>
    public double? OutputTokens { get; init; }

    /// <summary>The text and tool calls of a final assistant message in Pi wire JSON (<c>content</c> blocks of type <c>text</c> and
    /// <c>toolCall</c>; its <c>model</c> and <c>stopReason</c>). <paramref name="text"/> is used when the message has no text block (or there
    /// is no message, as for a run that ended before one).</summary>
    public static EvalAnswer FromOutput(JsonNode? output, string? text = null)
    {
        var parts = new List<string>();
        var calls = new List<EvalToolCall>();
        if (output?["content"] is JsonArray content)
        {
            foreach (var block in content.OfType<JsonObject>())
            {
                var type = StringOf(block, "type");
                if (type == "text" && StringOf(block, "text") is { } part) parts.Add(part);
                else if (type == "toolCall" && StringOf(block, "name") is { Length: > 0 } name)
                    calls.Add(new EvalToolCall(name, block["arguments"] is JsonObject arguments ? (JsonObject)arguments.DeepClone() : null, StringOf(block, "id")));
            }
        }
        return new EvalAnswer
        {
            Text = parts.Count > 0 ? string.Concat(parts) : text ?? "",
            ToolCalls = calls,
            ResponseModel = output is JsonObject message ? StringOf(message, "responseModel") ?? StringOf(message, "model") : null,
            StopReason = output is JsonObject final ? StringOf(final, "stopReason") : null,
        };
    }

    /// <summary>The answer of a recorded run: its output, error, settled cost (list price for a subscription) and timing.</summary>
    public static EvalAnswer FromRun(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var answer = FromOutput(run.Output, run.OutputText);
        return answer with
        {
            StopReason = run.StopReason ?? answer.StopReason,
            Error = run.Status is RunStatus.Error or RunStatus.Aborted or RunStatus.Interrupted ? run.Error ?? run.Status : null,
            CostMicros = run.CostMicros,
            ListPrice = string.Equals(run.Billing, "subscription", StringComparison.Ordinal),
            DurationMs = run.DurationMs,
            TtftMs = run.TtftMs,
            OutputTokens = run.Usage.Output,
        };
    }

    private static string? StringOf(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
