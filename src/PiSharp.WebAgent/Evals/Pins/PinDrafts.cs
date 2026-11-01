// Decision 0011 (run W5, E7): what Pin as test case works from. A pin names where it comes from (PinTarget: a runs.db run from the
// Playground, a Compare column, an Arena reveal, History or the inspector; a chat message; or, on a suite page, the suite to add a case to or
// the case to edit), and the server rebuilds the draft from that every time (the modal, a check preview, the save), so the page never
// carries the request and a save stores exactly what runs.db or the chat holds. A run's request is the stored one (PlaygroundPages.FromRun);
// a chat message's is the chat's transcript up to the answer, replayed as the provider received it: the system messages' text and sections
// (Pi's system-prompt sections, integer keys first, then the rest in order) and tool declarations, then every message, without assistant
// messages that ended in an error or were aborted (Pi never sends those). The case's request is TestCase.CaseSpec of it: never an account,
// a base URL or a run's grouping, and RunSpec refuses credential headers, so a case never holds a secret.
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Evals.Pins;

/// <summary>What the test case modal does.</summary>
public enum PinMode
{
    /// <summary>Pin a run or a chat message (its request and answer).</summary>
    Pin,

    /// <summary>Add a case to a suite by hand (evals-suite's Add case): a request typed in the modal, no reference output.</summary>
    Add,

    /// <summary>Edit a case's name, tags and checks (evals-suite's Edit case).</summary>
    Edit,
}

/// <summary>Where a pin comes from: a run, a chat message, a suite (Add case) or a case (Edit case).</summary>
/// <param name="Source">The page it was opened on.</param>
/// <param name="RunId">The runs.db run, or null.</param>
/// <param name="ChatId">The chat, or null.</param>
/// <param name="AgentId">The chat's branch (agent), or null for the chat's current one.</param>
/// <param name="EntryId">The chat entry of the answer, or null.</param>
/// <param name="SuiteId">The suite to add a case to, or null.</param>
/// <param name="CaseId">The case to edit, or null.</param>
public sealed record PinTarget(PinSource Source, string? RunId = null, string? ChatId = null, long? AgentId = null, long? EntryId = null,
    string? SuiteId = null, string? CaseId = null)
{
    /// <summary>What the modal does for this target.</summary>
    public PinMode Mode => CaseId is not null ? PinMode.Edit : RunId is null && ChatId is null && SuiteId is not null ? PinMode.Add : PinMode.Pin;

    /// <summary>The target, checked: exactly one of a run, a chat message, a suite or a case.</summary>
    /// <exception cref="ArgumentException">It names none, or more than one.</exception>
    public PinTarget Normalize()
    {
        static string? Id(string? value, string what)
        {
            var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (text is not null && (text.Length > 200 || text.Any(char.IsControl))) throw new ArgumentException($"The {what} id is not allowed.");
            return text;
        }
        if (!Enum.IsDefined(Source)) throw new ArgumentException($"Unknown pin source {(int)Source}.");
        var target = new PinTarget(Source, Id(RunId, "run"), Id(ChatId, "chat"), AgentId, EntryId, Id(SuiteId, "suite"), Id(CaseId, "case"));
        var named = (target.RunId is null ? 0 : 1) + (target.ChatId is null ? 0 : 1) + (target.CaseId is null ? 0 : 1);
        if (named > 1) throw new ArgumentException("Pin one run, one chat message or one case at a time.");
        if (named == 0 && target.SuiteId is null) throw new ArgumentException("Say what to pin: a run, a chat message, or the suite to add a case to.");
        if (target.ChatId is not null && target.EntryId is null) throw new ArgumentException("Say which message of the chat to pin.");
        if (target.CaseId is not null) EvalIds.Require(EvalIds.Case, target.CaseId, "test case");
        if (target.SuiteId is not null) EvalIds.Require(EvalIds.Suite, target.SuiteId, "suite");
        return target;
    }

    /// <summary>A target from a JSON object (<c>{source, run?, chat?, agent?, entry?, suite?, case?}</c>), as pin.js sends it.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public static PinTarget FromJson(JsonObject? json)
    {
        if (json is null) throw new ArgumentException("Say what to pin.");
        return From(Text(json, "source"), Text(json, "run"), Text(json, "chat"), Text(json, "agent"), Text(json, "entry"), Text(json, "suite"), Text(json, "case"));
    }

    /// <summary>A target from its parts as text (a query string's or a JSON body's).</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public static PinTarget From(string? source, string? run, string? chat, string? agent, string? entry, string? suite, string? testCase)
    {
        var parsed = source is null or "" ? (testCase is not null || (suite is not null && run is null && chat is null) ? PinSource.Suite : chat is not null ? PinSource.Chat : PinSource.History)
            : EvalJson.FromWire<PinSource>(source) ?? throw new ArgumentException($"There is no pin source \"{source}\".");
        return new PinTarget(parsed, run, chat, Number(agent, "agent"), Number(entry, "entry"), suite, testCase).Normalize();
    }

    /// <summary>The target as pin.js sends it back.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["source"] = EvalJson.Wire(Source) };
        if (RunId is not null) json["run"] = RunId;
        if (ChatId is not null) json["chat"] = ChatId;
        if (AgentId is { } agent) json["agent"] = agent.ToString(CultureInfo.InvariantCulture);
        if (EntryId is { } entry) json["entry"] = entry.ToString(CultureInfo.InvariantCulture);
        if (SuiteId is not null) json["suite"] = SuiteId;
        if (CaseId is not null) json["case"] = CaseId;
        return json;
    }

    private static long? Number(string? text, string what) =>
        string.IsNullOrWhiteSpace(text) ? null
            : long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value : throw new ArgumentException($"The {what} id is a number.");

    private static string? Text(JsonObject json, string key) => json[key] switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
        null => null,
        _ => throw new ArgumentException($"{key} is text."),
    };
}

/// <summary>A pin that cannot be made, with the reason the modal shows (never a model, provider or account name: an Arena battle may still
/// be blind).</summary>
public sealed class PinRefusedException(string message) : Exception(message);

/// <summary>What the modal shows and a save stores: the request, the reference output, where it came from and what to start with.</summary>
public sealed record PinDraft
{
    /// <summary>What the modal does.</summary>
    public required PinMode Mode { get; init; }

    /// <summary>Where it comes from.</summary>
    public required PinTarget Target { get; init; }

    /// <summary>The request as the case stores it (<see cref="TestCase.CaseSpec"/>), or null in Add mode before one is typed.</summary>
    public RunSpec? Spec { get; init; }

    /// <summary>The reference output, or null (Add mode, or a case without one).</summary>
    public TestCaseReference? Reference { get; init; }

    /// <summary>Where the case comes from, as it is stored.</summary>
    public required TestCaseOrigin Origin { get; init; }

    /// <summary>The name to start with.</summary>
    public string Name { get; init; } = "";

    /// <summary>The tags to start with.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The checks to start with.</summary>
    public IReadOnlyList<Check> Checks { get; init; } = [];

    /// <summary>The account the pinned request used (its id, for the "not part of the case" line), or null.</summary>
    public string? Account { get; init; }

    /// <summary>The case being edited (Edit mode).</summary>
    public TestCase? Existing { get; init; }

    /// <summary>The suite a case is added to or belongs to (Add and Edit modes).</summary>
    public Suite? Suite { get; init; }

    /// <summary>The draft as a case of <paramref name="suiteId"/> with id <paramref name="caseId"/> (for estimates and the save).</summary>
    public TestCase ToCase(string caseId, string suiteId) => new()
    {
        Id = caseId,
        SuiteId = suiteId,
        Name = Name.Length == 0 ? "Pinned case" : Name,
        Tags = Tags,
        Spec = Spec ?? throw new InvalidOperationException("The draft has no request."),
        Reference = Reference,
        Checks = Checks,
        Origin = Origin,
    };
}

/// <summary>Which runs can be pinned.</summary>
public static class PinRules
{
    /// <summary>Why agent runs are not pinned (mockup agent-blind-voted).</summary>
    public const string AgentLater = "Agent runs can't be pinned yet: agent test cases (task, base commit and check command) come later.";

    /// <summary>Whether <paramref name="run"/> can be pinned as far as its row says (an Arena battle's vote is checked when it is pinned).</summary>
    public static bool Offered(RunRecord? run) => run is not null && Refusal(run) is null;

    /// <summary>Why <paramref name="run"/> cannot be pinned, or null when its row allows it: one call that completed, with a stored request
    /// (a Playground, Compare, Arena or Tool Bench run) or a chat's call (its request is the chat's transcript).</summary>
    public static string? Refusal(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        // An agent battle's model calls are arena rows under their model's row (W5 integration fix 1).
        if (run.Kind == RunKind.Parent || run.Source == "agent-compare" || (run.Source == "arena" && run.ParentId is not null)) return AgentLater;
        if (run.Status == RunStatus.Running) return "This run is still going. Pin it once it finishes.";
        if (run.Status != RunStatus.Completed)
            return $"This run ended without an answer to keep as the reference output (it {(run.Status == RunStatus.Error ? "failed" : run.Status == RunStatus.Aborted ? "was stopped" : "was interrupted")}).";
        if (run.Source == "chat") return string.IsNullOrEmpty(run.Session) ? "This chat call doesn't name its chat, so its request can't be read." : null;
        return RunHistoryText.Replayable(run) ? null : "This run didn't store its request, so it can't be a test case.";
    }
}

/// <summary>A chat transcript (Pi wire messages) as a run request.</summary>
public static class PinMessages
{
    /// <summary>The request the provider received for the answer at <paramref name="answer"/>: the system prompt and tools the system
    /// messages before it set, and every message before it (assistant messages that failed or were aborted left out, as Pi leaves them out).</summary>
    /// <param name="entries">The branch's active entries (wire JSON), oldest first.</param>
    /// <param name="answer">The index of the answer's entry.</param>
    public static (string? SystemPrompt, IReadOnlyList<RunMessage> Messages, IReadOnlyList<RunTool> Tools) Request(IReadOnlyList<JsonObject> entries, int answer)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var prompts = new List<string>();
        var sections = new List<KeyValuePair<string, string>>();
        var tools = new List<JsonObject>();
        var messages = new List<RunMessage>();
        for (var index = 0; index < answer && index < entries.Count; index++)
        {
            foreach (var message in (entries[index]["model"] as JsonArray ?? []).OfType<JsonObject>())
            {
                switch (Str(message, "role"))
                {
                    case "system":
                        ApplySystem(message, prompts, sections, tools);
                        break;
                    case "assistant":
                        if (Str(message, "stopReason") is "error" or "aborted") break;
                        messages.Add(Assistant(message));
                        break;
                    case "toolResult":
                        var (resultText, resultImages) = Content(message["content"]);
                        messages.Add(new RunMessage
                        {
                            Role = "toolResult", Text = resultText, Images = resultImages, ToolCallId = Str(message, "toolCallId"),
                            ToolName = Str(message, "toolName"), IsError = message["isError"] is JsonValue error && error.TryGetValue<bool>(out var flag) ? flag : null,
                        });
                        break;
                    default:
                        // User messages, and anything else with text (a compaction summary is a user message already).
                        var (text, images) = Content(message["content"]);
                        if (text.Length > 0 || images is { Count: > 0 }) messages.Add(new RunMessage { Role = "user", Text = text, Images = images });
                        break;
                }
            }
        }
        var prompt = string.Join("\n\n", prompts);
        foreach (var section in sections.Where(pair => IsIndex(pair.Key)).OrderBy(pair => uint.Parse(pair.Key, CultureInfo.InvariantCulture))
            .Concat(sections.Where(pair => !IsIndex(pair.Key))))
        {
            if (section.Value.Length == 0) continue;
            prompt = prompt.Length == 0 ? section.Value : prompt + "\n\n" + section.Value;
        }
        return (prompt.Length == 0 ? null : prompt, messages, [.. tools.Select(Tool)]);
    }

    /// <summary>An assistant message's text (its text blocks, joined).</summary>
    public static string Text(JsonObject? message) =>
        string.Concat((message?["content"] as JsonArray ?? []).OfType<JsonObject>().Where(block => Str(block, "type") == "text").Select(block => Str(block, "text") ?? ""));

    private static void ApplySystem(JsonObject message, List<string> prompts, List<KeyValuePair<string, string>> sections, List<JsonObject> tools)
    {
        var (text, _) = Content(message["content"]);
        if (text.Length > 0) prompts.Add(text);
        if (message["sections"] is JsonObject patches)
        {
            foreach (var (key, value) in patches)
            {
                var at = sections.FindIndex(pair => pair.Key == key);
                if (value is JsonValue section && section.TryGetValue<string>(out var sectionText))
                {
                    if (at < 0) sections.Add(new(key, sectionText));
                    else sections[at] = new(key, sectionText);
                }
                else if (at >= 0) sections.RemoveAt(at);
            }
        }
        foreach (var removed in (message["toolsRemoved"] as JsonArray ?? []).OfType<JsonObject>())
            tools.RemoveAll(tool => Str(tool, "name") == Str(removed, "name"));
        foreach (var added in (message["toolsAdded"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var at = tools.FindIndex(tool => Str(tool, "name") == Str(added, "name"));
            if (at < 0) tools.Add(added);
            else tools[at] = added;
        }
    }

    private static RunMessage Assistant(JsonObject message)
    {
        var thinking = new List<RunThinking>();
        var calls = new List<RunToolCall>();
        foreach (var block in (message["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (Str(block, "type"))
            {
                case "thinking":
                    var redacted = block["redacted"] is JsonValue flag && flag.TryGetValue<bool>(out var isRedacted) && isRedacted;
                    var signature = Str(block, "thinkingSignature");
                    if (redacted && string.IsNullOrEmpty(signature)) break;
                    thinking.Add(new RunThinking(Str(block, "thinking") ?? "", signature, redacted));
                    break;
                case "toolCall":
                    calls.Add(new RunToolCall(Str(block, "id") ?? "", Str(block, "name") ?? "", block["arguments"] is JsonObject arguments ? (JsonObject)arguments.DeepClone() : null,
                        Str(block, "thoughtSignature")));
                    break;
            }
        }
        return new RunMessage
        {
            Role = "assistant",
            Text = Text(message),
            Thinking = thinking.Count == 0 ? null : thinking,
            ToolCalls = calls.Count == 0 ? null : calls,
        };
    }

    private static (string Text, IReadOnlyList<RunImage>? Images) Content(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var plain)) return (plain, null);
        var texts = new List<string>();
        var images = new List<RunImage>();
        foreach (var block in (content as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (Str(block, "type"))
            {
                case "text":
                    if (Str(block, "text") is { } text) texts.Add(text);
                    break;
                case "image":
                    if (Str(block, "data") is { Length: > 0 } data && Str(block, "mimeType") is { Length: > 0 } mime) images.Add(new RunImage(mime, data));
                    break;
            }
        }
        return (string.Join("\n", texts), images.Count == 0 ? null : images);
    }

    private static RunTool Tool(JsonObject declaration) =>
        new(Str(declaration, "name") ?? "", Str(declaration, "description"), declaration["parameters"] is JsonObject parameters ? (JsonObject)parameters.DeepClone() : null);

    private static bool IsIndex(string key) => key.Length > 0 && key.All(char.IsAsciiDigit) && uint.TryParse(key, CultureInfo.InvariantCulture, out _);

    private static string? Str(JsonObject? json, string key) => json?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
