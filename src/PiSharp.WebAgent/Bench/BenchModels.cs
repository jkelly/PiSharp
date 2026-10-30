// Decision 0011 (run W4, R7): what the Tool Bench keeps. A bench session is one conversation with its setup: the model, thinking, the
// system prompt, the tools (JSON Schema definitions, each switched on or off, each with saved mock results), the tool choice, the parallel
// switch and the auto-respond rules. The conversation is the user's messages and the model's turns, in order; each turn is one guarded,
// recorded run of the run engine (runs.db, source "bench", one group per conversation), and the tool calls it made are kept with their
// schema check and their result: a mock (auto-respond), a result the user typed, or nothing yet (the bench paused on it). The bench never
// runs a tool: every result is a mock or the user's text. Sessions are stored as JSON (BenchStore) with SettingsStore.JsonOptions.
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Bench;

/// <summary>The states of a bench session.</summary>
public static class BenchStates
{
    /// <summary>Nothing runs; the conversation can be run (or continued).</summary>
    public const string Idle = "idle";

    /// <summary>A turn is running (or its calls are being answered).</summary>
    public const string Running = "running";

    /// <summary>Paused on tool calls that need a result.</summary>
    public const string Awaiting = "awaiting";

    /// <summary>The last turn answered without calling a tool.</summary>
    public const string Finished = "finished";

    /// <summary>The user stopped the run (or the automatic turn limit did).</summary>
    public const string Stopped = "stopped";

    /// <summary>The last turn failed.</summary>
    public const string Error = "error";

    /// <summary>The Spend Guard did not let an automatic turn through; Run asks again.</summary>
    public const string Blocked = "blocked";

    /// <summary>Every state.</summary>
    public static IReadOnlyList<string> All { get; } = [Idle, Running, Awaiting, Finished, Stopped, Error, Blocked];
}

/// <summary>Why the bench paused on a call.</summary>
public static class BenchPauses
{
    /// <summary>Auto-respond is off.</summary>
    public const string Manual = "manual";

    /// <summary>The arguments broke the schema ("Pause on schema violations").</summary>
    public const string Schema = "schema";

    /// <summary>No mock matched ("Pause when no mock matches").</summary>
    public const string NoMock = "no-mock";

    /// <summary>The model called a tool the bench does not offer.</summary>
    public const string UnknownTool = "unknown-tool";
}

/// <summary>Where a result came from.</summary>
public static class BenchResultSources
{
    /// <summary>A saved mock, by auto-respond.</summary>
    public const string Mock = "mock";

    /// <summary>The user's text (perhaps filled from a mock).</summary>
    public const string Manual = "manual";

    /// <summary>The bench's own "no mock matches" error result (auto-respond with "Pause when no mock matches" off).</summary>
    public const string NoMock = "no-mock";
}

/// <summary>The simulated tool latencies.</summary>
public static class BenchLatency
{
    /// <summary>No delay.</summary>
    public const string None = "none";

    /// <summary>250 ms.</summary>
    public const string Short = "250";

    /// <summary>1 s.</summary>
    public const string Long = "1000";

    /// <summary>100 to 1,500 ms at random.</summary>
    public const string Random = "random";

    /// <summary>Every choice with its label.</summary>
    public static IReadOnlyList<(string Value, string Label)> All { get; } =
        [(None, "none"), (Short, "250 ms"), (Long, "1 s"), (Random, "random 100–1500 ms")];

    /// <summary>A delay for one call.</summary>
    public static int Delay(string? latency) => latency switch
    {
        Short => 250,
        Long => 1000,
        Random => System.Random.Shared.Next(100, 1501),
        _ => 0,
    };
}

/// <summary>A saved mock result of a tool.</summary>
public sealed record BenchMock
{
    /// <summary>The most mocks a tool keeps.</summary>
    public const int MaxPerTool = 50;

    /// <summary>The largest mock text.</summary>
    public const int MaxText = 256 * 1024;

    /// <summary>The name shown on quick-fill buttons and result chips.</summary>
    public string Name { get; init; } = "";

    /// <summary>Arguments a call must contain (a subset match), or null for the fallback.</summary>
    public JsonObject? Match { get; init; }

    /// <summary>The result text sent as the toolResult.</summary>
    public string Text { get; init; } = "";

    /// <summary>Whether the result is sent with isError.</summary>
    public bool IsError { get; init; }

    /// <summary>Whether this is a fallback (no match).</summary>
    [JsonIgnore]
    public bool Fallback => Match is null || Match.Count == 0;
}

/// <summary>A tool of the bench.</summary>
public sealed record BenchTool
{
    /// <summary>The name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The description.</summary>
    public string? Description { get; init; }

    /// <summary>The parameters' JSON Schema (an object schema), or null for none.</summary>
    public JsonObject? Parameters { get; init; }

    /// <summary>Whether it is sent.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether it is PiSharp's own declaration (read, grep, find, ls, bash, edit, write): read-only, and never run here.</summary>
    public bool Builtin { get; init; }

    /// <summary>Saved mock results, in match order.</summary>
    public IReadOnlyList<BenchMock> Mocks { get; init; } = [];
}

/// <summary>The auto-respond rules.</summary>
public sealed record BenchRules
{
    /// <summary>Whether calls are answered with mocks ("Auto-respond with mock results").</summary>
    public bool AutoRespond { get; init; } = true;

    /// <summary>Pause on a call whose arguments break the schema.</summary>
    public bool PauseOnSchema { get; init; } = true;

    /// <summary>Pause on a call no mock matches (off: the bench answers it with a "no mock" error result).</summary>
    public bool PauseOnNoMock { get; init; } = true;

    /// <summary>The simulated tool latency (<see cref="BenchLatency"/>).</summary>
    public string Latency { get; init; } = BenchLatency.None;
}

/// <summary>What the bench sends: the model, thinking, system prompt, tools, tool choice, parallel calls and the auto-respond rules.</summary>
public sealed record BenchSetup
{
    /// <summary>The longest system prompt.</summary>
    public const int MaxSystemPrompt = 200_000;

    /// <summary>The provider id.</summary>
    public string Provider { get; init; } = "";

    /// <summary>The model id.</summary>
    public string Model { get; init; } = "";

    /// <summary>The account id, or null for the provider's active account.</summary>
    public string? Account { get; init; }

    /// <summary>The thinking level, or null for off.</summary>
    public string? Thinking { get; init; }

    /// <summary>The system prompt, or null.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The tools.</summary>
    public IReadOnlyList<BenchTool> Tools { get; init; } = [];

    /// <summary><c>auto</c> (null), <c>none</c>, <c>required</c>, <c>any</c> (sent as required) or an enabled tool's name.</summary>
    public string? ToolChoice { get; init; }

    /// <summary>Whether the model may call tools in parallel (on: the provider's default; off: sent where the API takes it).</summary>
    public bool Parallel { get; init; } = true;

    /// <summary>The auto-respond rules.</summary>
    public BenchRules Rules { get; init; } = new();

    /// <summary>The enabled tools.</summary>
    [JsonIgnore]
    public IReadOnlyList<BenchTool> Enabled => [.. Tools.Where(tool => tool.Enabled)];

    /// <summary>The tool choice as the select shows it: auto, none, required, any or tool.</summary>
    [JsonIgnore]
    public string ChoiceKind => ToolChoice switch { null or "auto" => "auto", "none" => "none", "required" => "required", "any" => "any", _ => "tool" };

    /// <summary>The same setup, checked: names, schemas, mocks, the tool choice, thinking and the rules.</summary>
    /// <exception cref="ArgumentException">Something is not allowed; the message says what.</exception>
    public BenchSetup Normalize()
    {
        var tools = new List<BenchTool>();
        foreach (var raw in Tools ?? [])
        {
            if (raw is null) throw new ArgumentException("A tool is empty.");
            tools.Add(BenchSchema.NormalizeTool(raw));
        }
        if (tools.Count > Runs.RunSpec.MaxTools) throw new ArgumentException($"The bench offers at most {Runs.RunSpec.MaxTools} tools.");
        var duplicate = tools.GroupBy(tool => tool.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"There are two tools named {duplicate.Key}.");
        var thinking = string.IsNullOrWhiteSpace(Thinking) || Thinking.Trim() == "off" ? null : Thinking.Trim();
        if (thinking is not null && !Runs.RunSpec.ThinkingLevels.Contains(thinking))
            throw new ArgumentException($"Thinking is {string.Join(", ", Runs.RunSpec.ThinkingLevels)}, not \"{thinking}\".");
        if (SystemPrompt is { Length: > MaxSystemPrompt }) throw new ArgumentException($"The system prompt is at most {MaxSystemPrompt:N0} characters.");
        var choice = string.IsNullOrWhiteSpace(ToolChoice) ? null : ToolChoice.Trim();
        if (choice == "auto") choice = null;
        if (choice is not (null or "none" or "required" or "any") && !tools.Any(tool => tool.Enabled && tool.Name == choice))
            throw new ArgumentException($"The tool choice \"{choice}\" is auto, none, required, any or one of the tools that are switched on.");
        var rules = Rules ?? new BenchRules();
        var latency = string.IsNullOrWhiteSpace(rules.Latency) ? BenchLatency.None : rules.Latency.Trim();
        if (!BenchLatency.All.Any(item => item.Value == latency)) throw new ArgumentException("The simulated latency is none, 250, 1000 or random.");
        return this with
        {
            Provider = Provider?.Trim() ?? "",
            Model = Model?.Trim() ?? "",
            Account = string.IsNullOrWhiteSpace(Account) ? null : Account.Trim(),
            Thinking = thinking,
            SystemPrompt = string.IsNullOrEmpty(SystemPrompt) ? null : SystemPrompt,
            Tools = tools,
            ToolChoice = choice,
            Rules = rules with { Latency = latency },
        };
    }
}

/// <summary>One schema violation of a call's arguments.</summary>
/// <param name="Path">The JSON pointer of the value (<c>/unit</c>; <c>/</c> for the arguments themselves).</param>
/// <param name="Message">What is wrong.</param>
public sealed record BenchViolation(string Path, string Message);

/// <summary>The result a call got.</summary>
public sealed record BenchResult
{
    /// <summary>The text sent as the toolResult.</summary>
    public string Text { get; init; } = "";

    /// <summary>Whether it was sent with isError.</summary>
    public bool IsError { get; init; }

    /// <summary>Where it came from (<see cref="BenchResultSources"/>).</summary>
    public string Source { get; init; } = BenchResultSources.Manual;

    /// <summary>The mock's name (a mock, or a manual result filled from one), or null.</summary>
    public string? Mock { get; init; }

    /// <summary>When it was recorded.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>How long the call waited for it, in ms (the simulated latency, or the time paused).</summary>
    public long Ms { get; init; }
}

/// <summary>A tool call a turn made.</summary>
public sealed record BenchCall
{
    /// <summary>The call id, as the model sent it.</summary>
    public string Id { get; init; } = "";

    /// <summary>The tool's name.</summary>
    public string Tool { get; init; } = "";

    /// <summary>The turn (1-based) that made it.</summary>
    public int Turn { get; init; }

    /// <summary>Its place among the turn's calls (1-based).</summary>
    public int Index { get; init; }

    /// <summary>How many calls the turn made.</summary>
    public int Count { get; init; }

    /// <summary>The arguments (an object), or null when the model sent something else.</summary>
    public JsonObject? Arguments { get; init; }

    /// <summary>The arguments as sent when they are not an object, or null.</summary>
    public string? RawArguments { get; init; }

    /// <summary>The provider's signature of the call (Google's thought signature), sent back with it on the next turn; null when it had none.</summary>
    public string? ThoughtSignature { get; init; }

    /// <summary>Whether it names a tool the bench offered.</summary>
    public bool Known { get; init; }

    /// <summary>Its schema violations (Pi's argument check).</summary>
    public IReadOnlyList<BenchViolation> Violations { get; init; } = [];

    /// <summary>Why the bench paused on it (<see cref="BenchPauses"/>), or null.</summary>
    public string? Pause { get; init; }

    /// <summary>When the turn made it.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Its result, or null.</summary>
    public BenchResult? Result { get; init; }

    /// <summary>Whether the run was stopped before it got a result (it never will).</summary>
    public bool Skipped { get; init; }

    /// <summary>Whether it waits for a result.</summary>
    [JsonIgnore]
    public bool Pending => Result is null && !Skipped;
}

/// <summary>A model turn: one run of the run engine.</summary>
public sealed record BenchTurn
{
    /// <summary>The turn number (1-based within the conversation).</summary>
    public int Index { get; init; }

    /// <summary>The run's id (runs.db).</summary>
    public string RunId { get; init; } = "";

    /// <summary>The run's status (<see cref="Runs.RunStatus"/>).</summary>
    public string Status { get; init; } = Runs.RunStatus.Running;

    /// <summary>The stop reason (toolUse, stop, length, …), or null.</summary>
    public string? StopReason { get; init; }

    /// <summary>When it started.</summary>
    public DateTimeOffset Started { get; init; }

    /// <summary>How long it took, in ms, or null while it runs.</summary>
    public long? DurationMs { get; init; }

    /// <summary>The answer's text.</summary>
    public string? Text { get; init; }

    /// <summary>The thinking text, or null.</summary>
    public string? Thinking { get; init; }

    /// <summary>The thinking blocks as the provider sent them (with their signatures), sent back with the turn's calls on the next turn:
    /// Anthropic refuses a thinking model's tool loop without them, and OpenAI Responses keeps its reasoning item there. Null for none.</summary>
    public IReadOnlyList<Runs.RunThinking>? ThinkingBlocks { get; init; }

    /// <summary>The ids of the calls it made, in order.</summary>
    public IReadOnlyList<string> Calls { get; init; } = [];

    /// <summary>Input tokens.</summary>
    public double Input { get; init; }

    /// <summary>Output tokens.</summary>
    public double Output { get; init; }

    /// <summary>Cache read tokens.</summary>
    public double CacheRead { get; init; }

    /// <summary>The cost in micro-USD (its ledger row), or null.</summary>
    public long? CostMicros { get; init; }

    /// <summary>How the cost is known (actual, estimate, …), or null.</summary>
    public string? CostKind { get; init; }

    /// <summary>The error, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Whether it ended with an answer the conversation keeps.</summary>
    [JsonIgnore]
    public bool Completed => Status == Runs.RunStatus.Completed;
}

/// <summary>One entry of the conversation: a user message or a model turn.</summary>
public sealed record BenchEntry
{
    /// <summary><c>user</c> or <c>assistant</c>.</summary>
    public string Kind { get; init; } = "user";

    /// <summary>A user message's text.</summary>
    public string? Text { get; init; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>A model turn.</summary>
    public BenchTurn? Turn { get; init; }
}

/// <summary>A bench session: the setup and one conversation.</summary>
public sealed record BenchSession
{
    /// <summary>The longest user message.</summary>
    public const int MaxMessage = 200_000;

    /// <summary>The most entries a conversation keeps.</summary>
    public const int MaxEntries = 400;

    /// <summary>The id (<c>b</c> + 32 hex digits).</summary>
    public string Id { get; init; } = "";

    /// <summary>When it was created.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset Updated { get; init; }

    /// <summary>Bumped on every change (the page's event stream carries it).</summary>
    public long Version { get; init; }

    /// <summary>The setup.</summary>
    public BenchSetup Setup { get; init; } = new();

    /// <summary>The runs.db group of the current conversation, or null before its first turn.</summary>
    public string? Group { get; init; }

    /// <summary>The state (<see cref="BenchStates"/>).</summary>
    public string State { get; init; } = BenchStates.Idle;

    /// <summary>The conversation, oldest first.</summary>
    public IReadOnlyList<BenchEntry> Entries { get; init; } = [];

    /// <summary>The tool calls of the conversation's turns, in order.</summary>
    public IReadOnlyList<BenchCall> Calls { get; init; } = [];

    /// <summary>A line about the state (why it stopped, failed or paused), or null.</summary>
    public string? Note { get; init; }

    /// <summary>The Spend Guard's code when an automatic turn was refused, or null.</summary>
    public string? SpendCode { get; init; }

    /// <summary>When it paused, or null.</summary>
    public DateTimeOffset? PausedAt { get; init; }

    /// <summary>The run of the running turn, or null.</summary>
    public string? ActiveRun { get; init; }

    /// <summary>Turns started by auto-respond since the user last acted.</summary>
    public int AutoTurns { get; init; }

    /// <summary>The model turns, oldest first.</summary>
    [JsonIgnore]
    public IReadOnlyList<BenchTurn> Turns => [.. Entries.Where(entry => entry.Turn is not null).Select(entry => entry.Turn!)];

    /// <summary>The calls waiting for a result.</summary>
    [JsonIgnore]
    public IReadOnlyList<BenchCall> Pending => [.. Calls.Where(call => call.Pending)];

    /// <summary>The user messages.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> UserMessages => [.. Entries.Where(entry => entry.Kind == "user").Select(entry => entry.Text ?? "")];

    /// <summary>The call <paramref name="id"/>, or null.</summary>
    public BenchCall? Call(string id) => Calls.FirstOrDefault(call => call.Id == id);

    /// <summary>The calls of turn <paramref name="turn"/>, in order.</summary>
    public IReadOnlyList<BenchCall> CallsOf(int turn) => [.. Calls.Where(call => call.Turn == turn).OrderBy(call => call.Index)];

    /// <summary>The tool <paramref name="name"/>, or null.</summary>
    public BenchTool? Tool(string name) => Setup.Tools.FirstOrDefault(tool => tool.Name == name);
}
