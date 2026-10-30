// Decision 0011 (run W4, R7): what the Tool Bench's partials show, from a session: the toolbar (model, thinking, system prompt, Run or the
// paused chip and Stop, the auto-respond rules), the tool list (definitions, the tool choice, the parallel switch, a schema preview), the
// timeline (user messages, turns, calls with their schema check, results, the awaiting cards) and the call log. The page and every update
// render the same partials (BenchPages.FragmentsAsync, through the FragmentRenderer), so the page never builds markup from model text.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Bench;

/// <summary>A thinking level of the toolbar's select.</summary>
/// <param name="Value">The level.</param>
/// <param name="Taken">Whether the model takes it.</param>
/// <param name="Why">Why not, or null.</param>
public sealed record BenchLevel(string Value, bool Taken, string? Why);

/// <summary>The model of the bench's partials.</summary>
public sealed record BenchView
{
    /// <summary>The session.</summary>
    public required BenchSession Session { get; init; }

    /// <summary>What the model takes, or null when PiSharp cannot run it.</summary>
    public RunCapabilities? Capabilities { get; init; }

    /// <summary>The model's display name.</summary>
    public string ModelName { get; init; } = "";

    /// <summary>A notice over the page, or null.</summary>
    public string? Notice { get; init; }

    /// <summary>The setup.</summary>
    public BenchSetup Setup => Session.Setup;

    /// <summary>Whether PiSharp can run the model.</summary>
    public bool Known => Capabilities is not null;

    /// <summary>The session's state.</summary>
    public string State => Session.State;

    /// <summary>Whether there are no tools (the mockup's empty state).</summary>
    public bool NoTools => Setup.Tools.Count == 0;

    /// <summary>The page's state: empty (no tools), awaiting, running or default.</summary>
    public string PageState => NoTools ? "empty" : State switch { BenchStates.Awaiting => "awaiting", BenchStates.Running => "running", _ => "default" };

    /// <summary>What Run does.</summary>
    public BenchMode Mode => BenchRunner.Mode(Session);

    /// <summary>Why Run is disabled, or null.</summary>
    public string? RunBlocked => NoTools ? "Define a tool first"
        : Setup.Enabled.Count == 0 ? "Switch on at least one tool"
        : !Known ? "Choose a model PiSharp can run"
        : Mode == BenchMode.Empty ? "Add a user message first"
        : Mode == BenchMode.Pending ? "Send the pending results first"
        : Session.ActiveRun is not null ? "The stopped turn is still ending"
        : null;

    /// <summary>Run's tooltip.</summary>
    public string RunTitle => Mode == BenchMode.Restart && Session.Turns.Count > 0
        ? "Run the conversation again from your messages (Ctrl+Enter)"
        : "Run (Ctrl+Enter)";

    /// <summary>The calls waiting for a result.</summary>
    public IReadOnlyList<BenchCall> Pending => Session.Pending;

    /// <summary>The call the log selects: the first awaiting call, else the last call.</summary>
    public string? SelectedCall => Pending.FirstOrDefault()?.Id ?? Session.Calls.LastOrDefault()?.Id;

    /// <summary>The last turn, or null.</summary>
    public BenchTurn? LastTurn => Session.Turns.LastOrDefault();

    /// <summary>The last turn's inspector page, or null.</summary>
    public string? InspectUrl => LastTurn is { } turn ? "/runs/" + Uri.EscapeDataString(turn.RunId) : null;

    /// <summary>The thinking levels of the select.</summary>
    public IReadOnlyList<BenchLevel> Levels
    {
        get
        {
            var config = new RequestConfigView { Draft = new RunSpec { Provider = Setup.Provider, Model = Setup.Model }, Capabilities = Capabilities };
            return [.. RequestConfigView.Levels.Select(level => new BenchLevel(level, config.LevelTaken(level), config.LevelWhy(level)))];
        }
    }

    /// <summary>The thinking level the select shows.</summary>
    public string Level => Setup.Thinking ?? "off";

    /// <summary>How many tools are on.</summary>
    public int EnabledCount => Setup.Enabled.Count;

    /// <summary>The tool the schema preview shows.</summary>
    public BenchTool? PreviewTool => Setup.Tools.FirstOrDefault(tool => tool.Enabled) ?? Setup.Tools.FirstOrDefault();

    /// <summary>Whether the parallel switch can be switched off on this model's API.</summary>
    public bool ParallelSwitchable => Capabilities?.Find(RunModelOptions.ParallelToolCalls) is not { } support || support.Supported;

    /// <summary>The line under the parallel switch.</summary>
    public string ParallelNote => Setup.Parallel
        ? Capabilities?.Api switch
        {
            "faux" => "On: the faux model makes one call per turn. Results are collected before the next turn.",
            _ => "On: the provider's default (parallel calls allowed). Results are collected before the next turn.",
        }
        : Capabilities?.Api switch
        {
            "openai-completions" or "openai-responses" or "azure-openai-responses" or "openai-codex-responses" => "Sent as parallel_tool_calls: false.",
            "anthropic-messages" => "Sent as tool_choice.disable_parallel_tool_use: true.",
            "faux" => "The faux model makes one call per turn either way; nothing is sent.",
            _ => "This model's API has no switch for parallel calls: Run refuses until you switch it back on.",
        };

    /// <summary>The page's setup, as JSON (<c>data-setup</c>).</summary>
    public string SetupJson => JsonSerializer.Serialize(Setup, SettingsStore.JsonOptions);

    /// <summary>The built-in tools' names, as JSON (<c>data-builtins</c>).</summary>
    public static string BuiltinsJson => JsonSerializer.Serialize(BenchTemplates.BuiltinNames);

    /// <summary>The state chip of the conversation header: its text and tone, or null.</summary>
    public (string Text, string Tone)? StateChip => State switch
    {
        BenchStates.Running => ("running · turn " + (LastTurn?.Index ?? 1).ToString(CultureInfo.InvariantCulture), "info"),
        BenchStates.Awaiting => ("paused on " + Short(Pending.FirstOrDefault()?.Id ?? ""), "warn"),
        BenchStates.Finished => ("finished · " + (LastTurn?.StopReason ?? "stop"), "ok"),
        BenchStates.Stopped => ("stopped", "warn"),
        BenchStates.Error => ("error", "danger"),
        BenchStates.Blocked => ("Spend Guard", "warn"),
        _ when Session.Turns.Count > 0 => ("ready", "outline"),
        _ => null,
    };

    /// <summary>"4 turns · 4 tool calls · 11.9 s".</summary>
    public string Summary
    {
        get
        {
            var turns = Session.Turns.Count;
            var calls = Session.Calls.Count;
            var text = $"{turns} {(turns == 1 ? "turn" : "turns")} · {calls} tool {(calls == 1 ? "call" : "calls")}";
            return WallMs is { } ms ? text + " · " + Seconds(ms) : text;
        }
    }

    /// <summary>From the first turn's start to the last turn's end, in ms, or null.</summary>
    public long? WallMs
    {
        get
        {
            var turns = Session.Turns;
            if (turns.Count == 0 || turns[^1].DurationMs is not { } last) return null;
            return (long)(turns[^1].Started.AddMilliseconds(last) - turns[0].Started).TotalMilliseconds;
        }
    }

    /// <summary>Input tokens of every turn.</summary>
    public double Input => Session.Turns.Sum(turn => turn.Input);

    /// <summary>Output tokens of every turn.</summary>
    public double Output => Session.Turns.Sum(turn => turn.Output);

    /// <summary>Cache read tokens of every turn.</summary>
    public double Cached => Session.Turns.Sum(turn => turn.CacheRead);

    /// <summary>The cost of every turn, or null when none is known.</summary>
    public long? CostMicros => Session.Turns.Any(turn => turn.CostMicros is not null) ? Session.Turns.Sum(turn => turn.CostMicros ?? 0) : null;

    /// <summary>The time of day of <paramref name="at"/>.</summary>
    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The time of day with milliseconds.</summary>
    public static string TimeMs(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>A whole number with separators.</summary>
    public static string Number(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Milliseconds as seconds: "11.9 s".</summary>
    public static string Seconds(long ms) => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>A cost.</summary>
    public static string Money(long micros) => SpendMoney.Format(micros, micros is > 0 and < 10_000 ? 4 : 2);

    /// <summary>A call id cut for a chip: "call_Tz41…".</summary>
    public static string Short(string id) => id.Length > 9 ? id[..9] + "…" : id;

    /// <summary>A tool's definition as the schema preview shows it.</summary>
    public static string Definition(BenchTool tool) => BenchSchema.Indented(new JsonObject
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description ?? "",
        ["parameters"] = tool.Parameters?.DeepClone(),
    });

    /// <summary>A tool as the page keeps it (<c>data-tool</c>).</summary>
    public static string ToolJson(BenchTool tool) => JsonSerializer.Serialize(tool, SettingsStore.JsonOptions);

    /// <summary>The result chip of a call: text, tone and icon.</summary>
    public static (string Text, string Tone, string? Icon) ResultChip(BenchResult result) => result.Source switch
    {
        BenchResultSources.Mock => ($"auto · mock “{result.Mock}”", "ok", "bi-magic"),
        BenchResultSources.NoMock => ("auto · no mock matched", "warn", "bi-magic"),
        _ => (result.Mock is { } name ? $"manual · “{name}”" : "manual", "outline", "bi-person"),
    };

    /// <summary>The call log's status chip of a call.</summary>
    public static (string Text, string Tone) LogStatus(BenchCall call) => call.Result switch
    {
        { IsError: true } => ("isError", "danger"),
        { Source: BenchResultSources.Mock } => ("mock", "ok"),
        { Source: BenchResultSources.NoMock } => ("no mock", "warn"),
        { } => ("manual", "outline"),
        null when call.Skipped => ("skipped", "outline"),
        _ => ("awaiting", "warn"),
    };

    /// <summary>What the awaiting card says about why the bench paused.</summary>
    public static string PauseText(string? pause) => pause switch
    {
        BenchPauses.Schema => "Auto-respond skipped this call because its arguments broke the schema.",
        BenchPauses.NoMock => "No saved mock matches these arguments.",
        BenchPauses.UnknownTool => "The model called a tool the bench did not send.",
        _ => "Auto-respond is off, so every call waits for you.",
    };

    /// <summary>The short form of a call's schema check: text and tone.</summary>
    public static (string Text, string Tone, string Icon) SchemaChip(BenchCall call)
    {
        if (!call.Known) return ("unknown tool", "danger", "bi-question-octagon");
        if (call.Violations.Count == 0) return ("schema valid", "ok", "bi-check2");
        var first = call.Violations[0];
        var text = "schema: " + (first.Path == "/" ? first.Message : first.Path.TrimStart('/') + " " + first.Message);
        if (text.Length > 60) text = text[..59] + "…";
        return (call.Violations.Count > 1 ? $"{text} (+{call.Violations.Count - 1})" : text, "warn", "bi-exclamation-triangle");
    }
}

/// <summary>The model of <c>_AwaitingResultCard</c>: the call and its place among the waiting ones.</summary>
/// <param name="View">The page.</param>
/// <param name="Call">The call.</param>
/// <param name="Number">Its number among the waiting calls (for unique ids).</param>
public sealed record BenchCallView(BenchView View, BenchCall Call, int Number)
{
    /// <summary>The call's tool, or null when the bench did not send it.</summary>
    public BenchTool? Tool => View.Session.Tool(Call.Tool);

    /// <summary>An id of the card.</summary>
    public string Id(string part) => $"aw{Number}-{part}";
}

/// <summary>Builds the bench's views and renders its parts.</summary>
public sealed class BenchPages(BenchRunner runner, IServiceProvider services)
{
    /// <summary>The timeline body partial.</summary>
    public const string TimelinePartial = "/Views/Bench/_BenchTimelineBody.cshtml";

    /// <summary>The timeline header partial.</summary>
    public const string TimelineHeadPartial = "/Views/Bench/_BenchTimelineHead.cshtml";

    /// <summary>The run controls partial.</summary>
    public const string ControlsPartial = "/Views/Bench/_BenchRunControls.cshtml";

    /// <summary>The tool list partial.</summary>
    public const string ToolListPartial = "/Views/Bench/_BenchToolList.cshtml";

    /// <summary>The call log partial.</summary>
    public const string CallLogPartial = "/Views/Bench/_BenchCallLog.cshtml";

    /// <summary>The runner.</summary>
    public BenchRunner Runner => runner;

    /// <summary>The page of session <paramref name="id"/>, else the latest session (or a new one).</summary>
    public BenchView Page(string? id)
    {
        string? notice = null;
        BenchSession? session = null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            session = runner.Store.Get(id.Trim());
            if (session is null) notice = $"There is no bench session {id.Trim()}; the Tool Bench opened your latest one.";
        }
        session ??= runner.Current();
        return View(session, notice);
    }

    /// <summary>The view of <paramref name="session"/>.</summary>
    public BenchView View(BenchSession session, string? notice = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var setup = session.Setup;
        RunCapabilities? capabilities = null;
        if (setup.Provider.Length > 0 && setup.Model.Length > 0)
        {
            try { capabilities = runner.Engine.Capabilities(setup.Provider, setup.Model); }
            catch (Exception error) when (error is not OperationCanceledException) { capabilities = null; }
        }
        string? name = null;
        try { name = services.GetService<IModelCatalog>()?.FindModel(setup.Provider, setup.Model)?.Name; }
        catch (Exception error) when (error is not OperationCanceledException) { name = null; }
        return new BenchView { Session = session, Capabilities = capabilities, ModelName = name ?? capabilities?.Name ?? setup.Model, Notice = notice };
    }

    /// <summary>The page's parts for <paramref name="session"/>: {id, version, state, pageState, runId, mode, setup, messages, model,
    /// html: {controls, timelineHead, timeline, tools, log}}.</summary>
    public async Task<JsonObject> FragmentsAsync(BenchSession session)
    {
        var view = View(session);
        var renderer = services.GetRequiredService<FragmentRenderer>();
        return new JsonObject
        {
            ["id"] = session.Id,
            ["version"] = session.Version,
            ["state"] = session.State,
            ["pageState"] = view.PageState,
            ["runId"] = session.ActiveRun,
            ["mode"] = view.Mode.ToString().ToLowerInvariant(),
            ["setup"] = JsonSerializer.SerializeToNode(session.Setup, SettingsStore.JsonOptions),
            ["messages"] = new JsonArray([.. session.UserMessages.Select(text => (JsonNode?)JsonValue.Create(text))]),
            ["model"] = new JsonObject
            {
                ["provider"] = session.Setup.Provider,
                ["model"] = session.Setup.Model,
                ["name"] = view.ModelName,
                ["known"] = view.Known,
                ["levels"] = JsonSerializer.SerializeToNode(view.Levels, SettingsStore.JsonOptions),
            },
            ["html"] = new JsonObject
            {
                ["controls"] = await renderer.RenderAsync(ControlsPartial, view).ConfigureAwait(false),
                ["timelineHead"] = await renderer.RenderAsync(TimelineHeadPartial, view).ConfigureAwait(false),
                ["timeline"] = await renderer.RenderAsync(TimelinePartial, view).ConfigureAwait(false),
                ["tools"] = await renderer.RenderAsync(ToolListPartial, view).ConfigureAwait(false),
                ["log"] = await renderer.RenderAsync(CallLogPartial, view).ConfigureAwait(false),
            },
        };
    }

    /// <summary>The call log as JSON (Export as JSON): the session, its setup's tools and every call with its check and result.</summary>
    public static JsonObject LogJson(BenchSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new JsonObject
        {
            ["session"] = session.Id,
            ["exported"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["provider"] = session.Setup.Provider,
            ["model"] = session.Setup.Model,
            ["group"] = session.Group,
            ["turns"] = JsonSerializer.SerializeToNode(session.Turns.Select(turn => new
            {
                turn.Index, turn.RunId, turn.Status, turn.StopReason, turn.Started, turn.DurationMs, turn.Calls, turn.Input, turn.Output,
                turn.CacheRead, turn.CostMicros, turn.CostKind, turn.Error,
            }), SettingsStore.JsonOptions),
            ["calls"] = JsonSerializer.SerializeToNode(session.Calls, SettingsStore.JsonOptions),
        };
    }
}
