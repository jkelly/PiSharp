// Decision 0011 (run W4, R1): the models of the shared request partials, which the Playground renders and Compare (R2), Tool Bench (R7),
// History and the Run inspector (R3) and Presets (R8) reuse: _RequestConfig (every request option, in the mockups' accordions, with what
// the chosen model takes), _MessageListEditor (role-based messages), _ModelPickerModal, _ToolEditorModal and _ViewCodeModal. Every option
// row says whether the model takes it from RunModelOptions' capabilities (the same source the run engine refuses by), so a row is enabled
// only when the run would send it. An option the user set that the model does not take stays set (its draft value is kept) and the row
// says the run will be refused until it is cleared: nothing is dropped silently (brief rule 4). Every option RunSpec takes has a live
// control (W4 fix: text verbosity, the reasoning summary, the session id, the timeout, parallel tool calls and a tool's strict flag joined
// the rest). Options PiSharp's run engine has no field for yet (baseUrl, thinking budgets, cache retention, retries, Azure and Bedrock)
// are shown disabled with the reason, never sent; an API key is never taken from the page (W2 rule 3: secrets stay on the server).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Shell;

namespace PiSharp.WebAgent.Playground;

/// <summary>How a request's account is resolved, as Model &amp; auth shows it (a masked hint at most, never a credential).</summary>
/// <param name="ProviderName">The provider's display name.</param>
/// <param name="AccountLabel">The bound account's label (a masked hint such as <c>key sk-…Q7x2</c>), or null.</param>
/// <param name="Billing">How the provider bills.</param>
/// <param name="Offline">Whether the model runs offline (the faux provider): no account at all.</param>
public sealed record RequestAuthView(string ProviderName, string? AccountLabel, BillingKind Billing, bool Offline)
{
    /// <summary>Where the credential comes from.</summary>
    public string Source => Offline ? "None · runs offline" : AccountLabel is null ? "No account connected" : "The provider's active account";

    /// <summary>The billing line.</summary>
    public string BillingText => Billing switch
    {
        BillingKind.Metered => "metered · counted toward your caps",
        BillingKind.Subscription => "subscription · list price, not counted toward caps",
        BillingKind.Free => "free",
        _ => "unknown",
    };

    /// <summary>The chip tone of <see cref="Source"/>.</summary>
    public string Tone => Offline || AccountLabel is not null ? "ok" : "warn";
}

/// <summary>A service tier the Speed / service tier list shows.</summary>
/// <param name="Value">The wire value (empty: the configured default).</param>
/// <param name="Label">What the list says.</param>
/// <param name="Sent">Whether the run engine sends it (<see cref="RunSpec.ServiceTiers"/>).</param>
public sealed record RequestTier(string Value, string Label, bool Sent);

/// <summary>An option shown for completeness that the run engine has no field for yet: always disabled, never sent.</summary>
/// <param name="Name">The option's name (Pi's spelling).</param>
/// <param name="Reason">Why it is not sent.</param>
public sealed record RequestUnsentOption(string Name, string Reason);

/// <summary>The model of <c>Views/Shared/_RequestConfig.cshtml</c>: the request options of one draft, for one model.</summary>
public sealed record RequestConfigView
{
    /// <summary>The prefix of every element id (so several configs can share a page).</summary>
    public string IdPrefix { get; init; } = "pg";

    /// <summary>The draft whose values the controls show (not necessarily valid).</summary>
    public required RunSpec Draft { get; init; }

    /// <summary>What the draft's model takes, or null when it is not a model PiSharp knows (every option is then disabled).</summary>
    public RunCapabilities? Capabilities { get; init; }

    /// <summary>The account and billing of the draft's provider, or null.</summary>
    public RequestAuthView? Auth { get; init; }

    /// <summary>Whether to show the Model &amp; auth section (a Compare column picks its model in its own header).</summary>
    public bool ShowModel { get; init; } = true;

    /// <summary>Where the "defaults" link of the pane header goes.</summary>
    public string DefaultsUrl { get; init; } = "/settings/defaults";

    /// <summary>The thinking levels the Reasoning control lists: the run engine's (<see cref="RunSpec.ThinkingLevels"/>, <c>max</c> last).</summary>
    public static IReadOnlyList<string> Levels { get; } = RunSpec.ThinkingLevels;

    /// <summary>The service tiers the Speed list shows, PLAN.md "Request speed" order; each is sent when <see cref="RunSpec.ServiceTiers"/>
    /// names it, and taken when the model's API does (<see cref="TierTaken"/>).</summary>
    public static IReadOnlyList<RequestTier> Tiers { get; } =
    [
        Tier("", "Use default"), Tier("auto", "Auto"), Tier("default", "Standard"), Tier("flex", "Flex"),
        Tier("scale", "Scale"), Tier("priority", "Priority"), Tier("fast", "Fast"), Tier("ultrafast", "UltraFast"),
    ];

    private static RequestTier Tier(string value, string label) => new(value, label, value.Length == 0 || RunSpec.ServiceTiers.Contains(value));

    /// <summary>Whether the model takes service tier <paramref name="value"/> (the empty value, the configured default, always).</summary>
    public bool TierTaken(string value) => value.Length == 0 || (Capabilities?.ServiceTiers.Contains(value) ?? false);

    /// <summary>Why a listed tier or level is not sent.</summary>
    public const string NotSentYet = "PiSharp's run engine does not send it yet.";

    /// <summary>The options a run sends that count as "modified" (set away from the provider's default): every option with its own row
    /// (request-config.js counts the same list).</summary>
    public static IReadOnlyList<string> Counted { get; } =
    [
        RunModelOptions.Thinking, RunModelOptions.MaxTokens, RunModelOptions.Temperature, RunModelOptions.TopP, RunModelOptions.TopK, RunModelOptions.Stop,
        RunModelOptions.Seed, RunModelOptions.PresencePenalty, RunModelOptions.FrequencyPenalty, RunModelOptions.ResponseFormat, RunModelOptions.ToolChoice,
        RunModelOptions.ServiceTier, RunModelOptions.Headers, RunModelOptions.ProviderOptions, RunModelOptions.ParallelToolCalls, RunModelOptions.TextVerbosity,
        RunModelOptions.ReasoningSummary, RunModelOptions.SessionId, RunModelOptions.TimeoutMs,
    ];

    /// <summary>The element id of <paramref name="name"/>.</summary>
    public string Id(string name) => IdPrefix + "-" + name;

    /// <summary>Whether the draft thinks (a level other than off).</summary>
    public bool Thinking => Draft.ThinkingLevel is not null;

    /// <summary>The model's API, or empty.</summary>
    public string Api => Capabilities?.Api ?? "";

    /// <summary>The capabilities as request-config.js reads them (<c>data-capabilities</c>), or null.</summary>
    public string? CapabilitiesJson => Capabilities is null ? null : JsonSerializer.Serialize(Capabilities, SettingsStore.JsonOptions);

    /// <summary>The model as <c>provider/model</c>.</summary>
    public string ModelRef => $"{Draft.Provider}/{Draft.Model}";

    /// <summary>The Provider → Model dropdowns of the Model &amp; auth section.</summary>
    public ProviderModelSelect Picker => new(IdPrefix) { Provider = Draft.Provider, Model = Draft.Model, Small = true };

    /// <summary>What the model says about <paramref name="option"/>, or null.</summary>
    public RunOptionSupport? Support(string option) => Capabilities?.Find(option);

    /// <summary>Whether the model takes <paramref name="option"/> with the draft's thinking level (the row is enabled).</summary>
    public bool Takes(string option) => Support(option) is { } support && (Thinking ? support.WithThinking ?? support.Supported : support.Supported);

    /// <summary>Why the model does not take <paramref name="option"/> (or how it does), or null.</summary>
    public string? Why(string option)
    {
        if (Capabilities is null) return "Choose a model PiSharp knows.";
        var support = Support(option);
        if (support is null) return null;
        if (!Takes(option) && support.Reason is null) return Thinking && support.Supported ? "not while the model thinks" : "this model does not take it";
        return support.Reason;
    }

    /// <summary>Whether the draft sets <paramref name="option"/>.</summary>
    public bool IsSet(string option) => option switch
    {
        RunModelOptions.Thinking => Draft.ThinkingLevel is not null,
        RunModelOptions.MaxTokens => Draft.MaxTokens is not null,
        RunModelOptions.Temperature => Draft.Temperature is not null,
        RunModelOptions.TopP => Draft.TopP is not null,
        RunModelOptions.TopK => Draft.TopK is not null,
        RunModelOptions.Stop => Draft.Stop is { Count: > 0 },
        RunModelOptions.Seed => Draft.Seed is not null,
        RunModelOptions.PresencePenalty => Draft.PresencePenalty is not null,
        RunModelOptions.FrequencyPenalty => Draft.FrequencyPenalty is not null,
        RunModelOptions.ResponseFormat => Draft.ResponseFormat is not null,
        RunModelOptions.ToolChoice => Draft.ToolChoice is not null,
        RunModelOptions.ServiceTier => Draft.ServiceTier is not null,
        RunModelOptions.Headers => Draft.Headers is { Count: > 0 },
        RunModelOptions.ProviderOptions => Draft.ProviderOptions is { Count: > 0 },
        RunModelOptions.Tools => Draft.Tools is { Count: > 0 },
        RunModelOptions.Images => Draft.Messages.Any(message => message.Images is { Count: > 0 }),
        RunModelOptions.ParallelToolCalls => Draft.ParallelToolCalls is not null,
        RunModelOptions.StrictTools => Draft.Tools?.Any(tool => tool.Strict == true) == true,
        RunModelOptions.TextVerbosity => !string.IsNullOrWhiteSpace(Draft.TextVerbosity),
        RunModelOptions.ReasoningSummary => !string.IsNullOrWhiteSpace(Draft.ReasoningSummary),
        RunModelOptions.SessionId => !string.IsNullOrWhiteSpace(Draft.SessionId),
        RunModelOptions.TimeoutMs => Draft.TimeoutMs is not null,
        _ => false,
    };

    /// <summary>Whether <paramref name="option"/>'s controls are disabled: the model does not take it and the draft does not set it (a set
    /// option the model does not take stays editable, so it can be cleared).</summary>
    public bool Off(string option) => !Takes(option) && !IsSet(option);

    /// <summary>Whether the draft sets <paramref name="option"/> and the model does not take it: the run would be refused.</summary>
    public bool Conflict(string option) => IsSet(option) && !Takes(option);

    /// <summary>How many options the draft sets.</summary>
    public int Modified => Counted.Count(IsSet);

    /// <summary>How many options of <paramref name="options"/> the draft sets (an accordion's dot).</summary>
    public int ModifiedOf(params string[] options) => options.Count(IsSet);

    /// <summary>Whether the model takes thinking level <paramref name="level"/> (and the run engine sends it).</summary>
    public bool LevelTaken(string level) => RunSpec.ThinkingLevels.Contains(level) && (Capabilities?.ThinkingLevels.Contains(level) ?? level == "off");

    /// <summary>Why level <paramref name="level"/> is not offered, or null.</summary>
    public string? LevelWhy(string level) => LevelTaken(level) ? null
        : !RunSpec.ThinkingLevels.Contains(level) ? NotSentYet
        : Capabilities is null || Capabilities.ThinkingLevels.Count <= 1 ? "This model does not think." : $"{Draft.Model} thinks at {string.Join(", ", Capabilities.ThinkingLevels.Skip(1))}.";

    /// <summary>The thinking level the control shows checked.</summary>
    public string Level => Draft.ThinkingLevel ?? "off";

    /// <summary>The model's capability chips: reasoning, images, context window, output cap.</summary>
    public IReadOnlyList<(string Text, string? Kind, string? Icon)> Chips
    {
        get
        {
            var chips = new List<(string, string?, string?)>();
            if (Capabilities is not { } caps) return chips;
            if (caps.ThinkingLevels.Count > 1) chips.Add(("reasoning", "cap-reasoning", "bi-lightbulb"));
            if (caps.Images) chips.Add(("image", "cap-image", "bi-image"));
            if (caps.ContextWindow is { } window) chips.Add((CatalogText.Tokens(window) + " ctx", null, null));
            if (caps.MaxTokens is { } output) chips.Add((CatalogText.Tokens(output) + " out", null, null));
            if (caps.Offline) chips.Add(("offline", null, "bi-plug"));
            return chips;
        }
    }

    /// <summary>The draft's stop sequences, one per line.</summary>
    public string StopText => Draft.Stop is { Count: > 0 } stop ? string.Join("\n", stop) : "";

    /// <summary>The response format's type (<c>text</c> when none is set).</summary>
    public string FormatType => Draft.ResponseFormat?["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : "text";

    /// <summary>The json_schema format's name.</summary>
    public string FormatName => Draft.ResponseFormat?["name"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : "";

    /// <summary>The json_schema format's schema, indented.</summary>
    public string FormatSchema => Draft.ResponseFormat?["schema"] is { } schema ? RequestJson.Indented(schema) : "";

    /// <summary>Whether the json_schema format is strict.</summary>
    public bool FormatStrict => Draft.ResponseFormat?["strict"] is JsonValue value && value.TryGetValue<bool>(out var strict) && strict;

    /// <summary>The provider options, indented.</summary>
    public string ProviderOptionsText => Draft.ProviderOptions is { Count: > 0 } extra ? RequestJson.Indented(extra) : "";

    /// <summary>The tool choice's kind: auto, none, required or tool.</summary>
    public string ChoiceKind => Draft.ToolChoice switch { null or "auto" => "auto", "none" => "none", "required" => "required", _ => "tool" };

    /// <summary>The tool a <c>tool</c> choice names.</summary>
    public string? ChoiceTool => ChoiceKind == "tool" ? Draft.ToolChoice : null;

    /// <summary>The options this page does not send, by section: options the run engine has no field for yet (every option RunSpec takes
    /// has a live control).</summary>
    public static IReadOnlyDictionary<string, RequestUnsentOption> Unsent { get; } = new Dictionary<string, RequestUnsentOption>(StringComparer.Ordinal)
    {
        ["baseUrl"] = new("baseUrl", "Runs use the provider's base URL from Settings › Providers; a per-run override is not sent yet."),
        ["apiKey"] = new("apiKey", "The Playground never takes a key from the page: runs use the account below, whose credential stays on the server."),
        ["thinkingBudgets"] = new("thinkingBudgets", "The run engine sends the thinking level only; per-level token budgets are not sent yet."),
        ["cacheRetention"] = new("cacheRetention", "Runs use PiSharp's prompt-cache defaults; a per-run value is not sent yet."),
        ["maxRetries"] = new("maxRetries", "Runs use PiSharp's transport defaults; per-run retries are not sent yet."),
        ["maxRetryDelayMs"] = new("maxRetryDelayMs", "Runs use PiSharp's transport defaults; a per-run retry delay is not sent yet."),
    };

    /// <summary>The timeout's range as the Transport row says it (1,000 to 3,600,000 ms).</summary>
    public static string TimeoutRange { get; } =
        string.Create(CultureInfo.InvariantCulture, $"{RunSpec.MinTimeoutMs:N0} to {RunSpec.MaxTimeoutMs:N0} ms");

    /// <summary>The draft's tools marked strict.</summary>
    public int StrictTools => Draft.Tools?.Count(tool => tool.Strict == true) ?? 0;

    /// <summary>Whether the tools row is marked: the model takes no tools, or a tool is strict and the model takes no strict tools.</summary>
    public bool ToolsConflict => Conflict(RunModelOptions.Tools) || Conflict(RunModelOptions.StrictTools);

    /// <summary>What the tools row says when it is marked, or null.</summary>
    public string? ToolsWhy => Conflict(RunModelOptions.Tools) ? $"{Why(RunModelOptions.Tools)}. {Refused}"
        : Conflict(RunModelOptions.StrictTools) ? $"{StrictTools} strict {(StrictTools == 1 ? "tool" : "tools")}: {Why(RunModelOptions.StrictTools)}. {Refused}"
        : null;

    /// <summary>What a marked row adds after its reason (request-config.js says the same).</summary>
    public const string Refused = "Set: the run will be refused until you clear it.";

    /// <summary>A tool as the tool list keeps it (<c>data-tool</c>): its name, description and parameters, and <c>strict</c> when set.</summary>
    public static string ToolJson(RunTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var json = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description ?? "", ["parameters"] = tool.Parameters?.DeepClone() };
        if (tool.Strict == true) json["strict"] = true;
        return json.ToJsonString();
    }

    /// <summary>The Azure OpenAI Responses options (set on the provider; not sent per run yet).</summary>
    public static IReadOnlyList<(string Name, string Placeholder, string Help)> AzureOptions { get; } =
    [
        ("azureBaseUrl", "https://{resource}.openai.azure.com/openai/v1", "Full endpoint base. Takes precedence over azureResourceName."),
        ("azureResourceName", "AZURE_OPENAI_RESOURCE_NAME", "Falls back to AZURE_OPENAI_RESOURCE_NAME."),
        ("azureApiVersion", "v1", "Sent as ?api-version=. Falls back to AZURE_OPENAI_API_VERSION, then v1."),
        ("azureDeploymentName", "model id", "Falls back to AZURE_OPENAI_DEPLOYMENT_NAME_MAP, then the model id."),
    ];

    /// <summary>Whether the model is on the Azure OpenAI Responses API.</summary>
    public bool OnAzure => Api == "azure-openai-responses";

    /// <summary>Whether the model is on Amazon Bedrock.</summary>
    public bool OnBedrock => Api == "bedrock-converse-stream";

    /// <summary>Whether the model's API is one the run engine patches (providerOptions can reach its body).</summary>
    public bool Patched => RunModelOptions.PatchedApis.Contains(Api);

    /// <summary>The Provider-specific pane shown first.</summary>
    public string ProviderPane => OnAzure ? "azure" : OnBedrock ? "bedrock" : "current";

    /// <summary>A number as a control's value (invariant culture), or empty.</summary>
    public static string Number(double? value) => value is { } number ? number.ToString("0.###", CultureInfo.InvariantCulture) : "";

    /// <summary>A whole number as a control's value, or empty.</summary>
    public static string Number(long? value) => value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "";
}

/// <summary>One message editor of <c>_MessageListEditor</c>.</summary>
/// <param name="Role">system, user, assistant or toolResult.</param>
/// <param name="Message">The message (the system prompt as a system message).</param>
/// <param name="Prompt">Whether it is the request's system prompt (the first system editor).</param>
public sealed record MessageEditorItem(string Role, RunMessage Message, bool Prompt = false)
{
    /// <summary>The role's CSS class (<c>role-tool</c> for a tool result).</summary>
    public string RoleClass => "role-" + (Role == "toolResult" ? "tool" : Role);
}

/// <summary>The model of <c>Views/Shared/_MessageListEditor.cshtml</c>.</summary>
public sealed record MessageListView
{
    /// <summary>The prefix of every element id.</summary>
    public string IdPrefix { get; init; } = "pg";

    /// <summary>The request's system prompt, shown as the first (system) message; null shows an empty one.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The messages after it.</summary>
    public IReadOnlyList<RunMessage> Messages { get; init; } = [];

    /// <summary>Whether the model takes images (Attach image is enabled).</summary>
    public bool Images { get; init; }

    /// <summary>The model's context window, for the footer's share, or null.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>Where "From run history…" of the Import menu goes.</summary>
    public string HistoryUrl { get; init; } = "/history?source=playground";

    /// <summary>The editors in order: the system prompt first (always, so it can be filled in), then the messages.</summary>
    public IReadOnlyList<MessageEditorItem> Items =>
    [
        new MessageEditorItem("system", new RunMessage { Role = "system", Text = SystemPrompt ?? "" }, Prompt: true),
        .. Messages.Select(message => new MessageEditorItem(message.Role, message)),
    ];

    /// <summary>The tool call ids of the assistant messages before index <paramref name="index"/> of <see cref="Items"/> (a tool result's
    /// choices), with their tool names.</summary>
    public IReadOnlyList<RunToolCall> CallsBefore(int index) =>
        [.. Items.Take(index).SelectMany(item => item.Message.ToolCalls ?? [])];
}

/// <summary>The model of <c>Views/Shared/_ModelPickerModal.cshtml</c> (#modal-model-picker, Ctrl+K): the two linked dropdowns.</summary>
/// <param name="Select">The Provider → Model dropdowns (prefix <c>mp</c> by convention).</param>
public sealed record ModelPickerView(ProviderModelSelect Select)
{
    /// <summary>A line under the dropdowns, or null.</summary>
    public string? Note { get; init; }

    /// <summary>The submit button's text.</summary>
    public string Submit { get; init; } = "Use model";
}

/// <summary>A template of the tool editor (a real definition: PiSharp's own tools, or a common example).</summary>
/// <param name="Id">The radio's id part.</param>
/// <param name="Label">What the button says.</param>
/// <param name="Icon">The Bootstrap Icons class.</param>
/// <param name="Tool">The definition: name, description, parameters.</param>
/// <param name="Mock">A sample mock response, or null.</param>
public sealed record ToolTemplate(string Id, string Label, string Icon, RunTool Tool, string? Mock = null)
{
    /// <summary>The template as the editor reads it (<c>data-template</c>).</summary>
    public string Json => new JsonObject
    {
        ["name"] = Tool.Name,
        ["description"] = Tool.Description ?? "",
        ["parameters"] = Tool.Parameters?.DeepClone(),
        ["mock"] = Mock,
    }.ToJsonString();
}

/// <summary>The model of <c>Views/Shared/_ToolEditorModal.cshtml</c> (#modal-tool-editor).</summary>
public sealed record ToolEditorView
{
    /// <summary>The templates offered.</summary>
    public IReadOnlyList<ToolTemplate> Templates { get; init; } = [];

    /// <summary>Whether the mock response fields show (the Playground and Compare answer tool calls with them).</summary>
    public bool Mock { get; init; } = true;

    /// <summary>Whether the Strict switch is live: the page sends a tool's strict flag (<see cref="RunTool.Strict"/>). A page that does not
    /// send it leaves this off, and the switch shows disabled with why, so a strict flag is never set and then dropped.</summary>
    public bool Strict { get; init; }

    /// <summary>What the Strict switch's note says.</summary>
    public string StrictNote => Strict
        ? "Sends strict: true, so the provider keeps the arguments to the schema (OpenAI's APIs; strict mode needs every property in required and " +
          "additionalProperties: false). Anthropic and Google take no strict tools: a run with one is refused, never sent without it."
        : "This page does not send a tool's strict flag.";

    /// <summary>Where "Open in Tool Bench" goes, or null to leave it out.</summary>
    public string? BenchUrl { get; init; } = "/bench";

    /// <summary>The primary button's text.</summary>
    public string Submit { get; init; } = "Add to request";
}

/// <summary>The model of <c>Views/Shared/_ViewCodeModal.cshtml</c> (#modal-view-code); null renders the empty shell request-code.js fills.</summary>
/// <param name="Subtitle">What the code is of.</param>
/// <param name="CSharp">The C# (PiSharp) tab.</param>
/// <param name="Curl">The cURL tab.</param>
/// <param name="Json">The JSON tab (the run request, <c>run.json</c>).</param>
/// <param name="Provider">The provider-request part of the JSON tab: what the run adds to the provider's request, or null.</param>
public sealed record ViewCodeView(string Subtitle, string CSharp, string Curl, string Json, string? Provider)
{
    /// <summary>Where "Open last run in inspector" goes, or null to leave it out.</summary>
    public string? InspectorUrl { get; init; }

    /// <summary>Notes under the tabs (how the request is sent, what is left out).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The model of <c>Views/Shared/_ErrorDetailsModal.cshtml</c> (#modal-error-details); null renders an empty shell.</summary>
public sealed record ErrorDetailsView
{
    /// <summary>The heading.</summary>
    public string Title { get; init; } = "Request failed";

    /// <summary>The model, time and run, or null.</summary>
    public string? Subtitle { get; init; }

    /// <summary>The failure kind (Provider, Spend Guard, Cancelled, Interrupted, Error).</summary>
    public string Kind { get; init; } = "Error";

    /// <summary>The HTTP status from the stored response, or null when none was stored.</summary>
    public string? HttpStatus { get; init; }

    /// <summary>The stop reason, or null.</summary>
    public string? StopReason { get; init; }

    /// <summary>What went wrong.</summary>
    public string Message { get; init; } = "";

    /// <summary>The provider's raw response (redacted), or null.</summary>
    public string? RawBody { get; init; }

    /// <summary>Why there is no raw response, or null.</summary>
    public string? RawNote { get; init; }

    /// <summary>The run's inspector page, or null.</summary>
    public string? InspectorUrl { get; init; }

    /// <summary>Whether Retry shows.</summary>
    public bool CanRetry { get; init; } = true;
}

/// <summary>JSON as the request partials show it.</summary>
public static class RequestJson
{
    private static readonly JsonSerializerOptions IndentedOptions = new(SettingsStore.JsonOptions)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactOptions = new(SettingsStore.JsonOptions)
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary><paramref name="node"/> indented (HTML-encoded by Razor where it is shown).</summary>
    public static string Indented(JsonNode? node) => node is null ? "null" : node.ToJsonString(IndentedOptions);

    /// <summary><paramref name="node"/> on one line.</summary>
    public static string Compact(JsonNode? node) => node is null ? "null" : node.ToJsonString(CompactOptions);

    /// <summary><paramref name="value"/> serialized the app's way (camelCase, nulls left out), indented.</summary>
    public static string Indented<T>(T value) => JsonSerializer.Serialize(value, IndentedOptions);

    /// <summary>The names of a JSON Schema object's properties (a tool's summary line).</summary>
    public static string Properties(JsonObject? schema) =>
        schema?["properties"] is JsonObject properties ? string.Join(", ", properties.Select(pair => pair.Key)) : "";
}
