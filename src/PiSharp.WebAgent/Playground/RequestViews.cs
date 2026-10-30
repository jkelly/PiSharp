// Decision 0011 (run W4, R1): the models of the shared request partials, which the Playground renders and Compare (R2), Tool Bench (R7),
// History and the Run inspector (R3) and Presets (R8) reuse: _RequestConfig (every request option, in the mockups' accordions, with what
// the chosen model takes), _MessageListEditor (role-based messages), _ModelPickerModal, _ToolEditorModal and _ViewCodeModal. Every option
// row says whether the model takes it from RunModelOptions' capabilities (the same source the run engine refuses by), so a row is enabled
// only when the run would send it. An option the user set that the model does not take stays set (its draft value is kept) and the row
// says the run will be refused until it is cleared: nothing is dropped silently (brief rule 4). Every option RunSpec takes has a live
// control (W4 fix: text verbosity, the reasoning summary, the session id, the timeout, parallel tool calls and a tool's strict flag joined
// the rest; W5 L4: cache retention, retries, the retry delay, thinking budgets and the Azure API version). Options a run refuses by design
// (a base URL, the Azure base URL, resource name and deployment, the Bedrock options) are shown disabled with why and where they are set
// instead, never sent; an API key is never taken from the page (W2 rule 3: secrets stay on the server). Sampling's read-only samplingParams
// row shows the model's catalog sampling values (RequestSamplingView) and whether its API applies them.
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
    /// <summary>The account the draft names (W4 fix 2: a run, a preset or the user chose it), or null for the provider's active account.</summary>
    public string? Chosen { get; init; }

    /// <summary>The provider's accounts the request may name (empty offline, and for a provider whose accounts PiSharp does not list).</summary>
    public IReadOnlyList<RequestAccount> Accounts { get; init; } = [];

    /// <summary>The label of the provider's active account, or null when it has none.</summary>
    public string? ActiveLabel { get; init; }

    /// <summary>Where the credential comes from.</summary>
    public string Source => Offline ? "None · runs offline"
        : Chosen is not null ? AccountLabel is null ? "Account not on this machine" : "The account you chose"
        : AccountLabel is null ? "No account connected" : "The provider's active account";

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

    /// <summary>Whether Model &amp; auth offers the account choice: online, when the provider has two accounts or more, or the draft names
    /// one (so it shows, and can go back to the active account). With one account the mockups' read-only Account row is enough.</summary>
    public bool AccountChoice => !Offline && (Accounts.Count >= 2 || Chosen is not null);

    /// <summary>The choice's options: the active account first (no id), then each account; an account the draft names that the provider does
    /// not list stays listed (the run engine refuses it with a clear message; never a silent switch to another account).</summary>
    public IReadOnlyList<RequestAccountOption> AccountOptions => RequestAccounts.Options(Accounts, Chosen, ActiveLabel);
}

/// <summary>A provider account a request may name (Model &amp; auth's account choice, a Compare column's account override): its id and label,
/// never a secret.</summary>
/// <param name="Id">The account id a run spec names.</param>
/// <param name="Label">The name shown.</param>
/// <param name="Active">Whether it is the provider's active account.</param>
/// <param name="Usable">Whether it can be used (connected); an expired or unavailable account is listed, and refused when a run names it.</param>
/// <param name="Status">Its status' wire name: <c>connected</c>, <c>expired</c> or <c>unavailable</c>.</param>
public sealed record RequestAccount(string Id, string Label, bool Active, bool Usable, string Status);

/// <summary>One option of an account choice.</summary>
/// <param name="Value">The account id, or empty for the provider's active account.</param>
/// <param name="Text">What the option says.</param>
/// <param name="Label">The account's own label (for the Account row and a column's chip), or null.</param>
/// <param name="Selected">Whether it is chosen.</param>
/// <param name="Disabled">Whether it cannot be chosen (an account that is not usable, unless the draft names it).</param>
public sealed record RequestAccountOption(string Value, string Text, string? Label, bool Selected, bool Disabled);

/// <summary>The accounts a request may name, from the credentials area's account store (ids and labels only).</summary>
public static class RequestAccounts
{
    /// <summary>The accounts of <paramref name="provider"/>, oldest first (the store's order), or none when the model runs offline, the provider
    /// is a custom one (its requests use its own key) or no account store is registered.</summary>
    public static IReadOnlyList<RequestAccount> List(IServiceProvider services, string provider, bool offline, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (offline || string.IsNullOrWhiteSpace(provider) || !CustomProviders.CustomProviderJson.IsBuiltin(provider.Trim())) return [];
        if (services.GetService<Credentials.IAccountStore>() is not { } store) return [];
        try
        {
            var active = store.Active(provider.Trim())?.Id;
            return [.. store.List(provider.Trim()).Select(account => new RequestAccount(account.Id, account.Label, account.Id == active,
                account.Status == Credentials.AccountStatus.Connected, Wire(account.Status)))];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log?.LogDebug(error, "The accounts of {Provider} could not be listed.", provider);
            return [];
        }
    }

    /// <summary>The options of an account choice over <paramref name="accounts"/>, with <paramref name="chosen"/> selected (none: the active
    /// account).</summary>
    public static IReadOnlyList<RequestAccountOption> Options(IReadOnlyList<RequestAccount> accounts, string? chosen, string? activeLabel)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var listed = chosen is not null && accounts.Any(account => account.Id == chosen);
        var options = new List<RequestAccountOption>
        {
            new("", activeLabel is { Length: > 0 } label ? $"Active account ({label})" : "Active account", activeLabel, chosen is null, false),
        };
        options.AddRange(accounts.Select(account => new RequestAccountOption(account.Id, account.Usable ? account.Label : $"{account.Label} · {account.Status}",
            account.Label, account.Id == chosen, !account.Usable && account.Id != chosen)));
        if (chosen is not null && !listed) options.Add(new RequestAccountOption(chosen, $"{Settings.SettingsText.ShortId(chosen)} · not on this machine", null, true, false));
        return options;
    }

    private static string Wire(Credentials.AccountStatus status) => status switch
    {
        Credentials.AccountStatus.Expired => "expired",
        Credentials.AccountStatus.Unavailable => "unavailable",
        _ => "connected",
    };
}

/// <summary>A service tier the Speed / service tier list shows.</summary>
/// <param name="Value">The wire value (empty: the configured default).</param>
/// <param name="Label">What the list says.</param>
/// <param name="Sent">Whether the run engine sends it (<see cref="RunSpec.ServiceTiers"/>).</param>
public sealed record RequestTier(string Value, string Label, bool Sent);

/// <summary>An option shown for completeness that a run never takes, by design (it would send the account's credential elsewhere, change
/// which model answers, or alter a signed request): always disabled, never sent. Its reason says why, and where it is set instead.</summary>
/// <param name="Name">The option's name (Pi's spelling).</param>
/// <param name="Reason">Why a run refuses it, and where it is set.</param>
public sealed record RequestUnsentOption(string Name, string Reason);

/// <summary>The model's catalog sampling values (pi-ai <c>samplingParams</c> and <c>samplingParamsByThinkingLevel</c>), as Sampling's
/// read-only samplingParams row shows them. The OpenAI Chat Completions, OpenAI Responses and Azure OpenAI Responses request factories merge
/// them into the request body (the model's values, then the thinking level's, then the request's own options, per key); other APIs never
/// send them. Nothing here is editable: they come from the catalog or a models.json override.</summary>
/// <param name="Model">The model id, or empty when the draft names no model PiSharp knows.</param>
/// <param name="Api">The model's API, or empty.</param>
/// <param name="Values">The model's <c>samplingParams</c>, or null.</param>
/// <param name="ByLevel">The model's <c>samplingParamsByThinkingLevel</c>, or null.</param>
public sealed record RequestSamplingView(string Model, string Api, JsonObject? Values, JsonObject? ByLevel)
{
    /// <summary>The APIs whose request factories merge a model's sampling values into the body.</summary>
    public static IReadOnlyList<string> ApplyingApis { get; } = ["openai-completions", "openai-responses", "azure-openai-responses"];

    /// <summary>No model: the row says to choose one.</summary>
    public static RequestSamplingView None { get; } = new("", "", null, null);

    /// <summary>The sampling values of <paramref name="model"/>'s catalog entry (its pi-ai wire JSON), or <see cref="None"/>.</summary>
    public static RequestSamplingView Of(PiSharp.Durable.DurableModel? model) =>
        model is null ? None : new RequestSamplingView(model.Id, model.Api, Field(model, "samplingParams"), Field(model, "samplingParamsByThinkingLevel"));

    private static JsonObject? Field(PiSharp.Durable.DurableModel model, string name) =>
        model.Wire is { } wire && wire.TryGetValue(name, out var value) && value is PiSharp.Durable.Chord.JsonObjectValue
            && JsonNode.Parse(value.ToJson()) is JsonObject json && json.Count > 0 ? json : null;

    /// <summary>Whether the draft names a model PiSharp knows.</summary>
    public bool Known => Model.Length > 0;

    /// <summary>Whether the model's API merges catalog sampling values into the request body.</summary>
    public bool Applied => ApplyingApis.Contains(Api);

    /// <summary>Whether the catalog sets any.</summary>
    public bool Any => Values is not null || ByLevel is not null;

    /// <summary>The row's chip.</summary>
    public string Chip => Applied ? "applied by this api" : "not applied by this api";

    /// <summary>The model's values on one line, or empty.</summary>
    public string ValuesText => Values is null ? "" : RequestJson.Compact(Values);

    /// <summary>The values by thinking level on one line, or empty.</summary>
    public string ByLevelText => ByLevel is null ? "" : RequestJson.Compact(ByLevel);

    /// <summary>What the row says under the values.</summary>
    public string Note => !Known ? "Choose a model PiSharp knows."
        : !Any ? $"{Model} has none."
        : Applied ? $"{Api} merges them into the request body (the model's values, then the thinking level's); an option set here replaces the same key."
        : $"{Api} does not apply them, so they are never sent.";
}

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
        RunModelOptions.ReasoningSummary, RunModelOptions.SessionId, RunModelOptions.TimeoutMs, RunModelOptions.CacheRetention, RunModelOptions.MaxRetries,
        RunModelOptions.MaxRetryDelayMs, RunModelOptions.ThinkingBudgets, RunModelOptions.AzureApiVersion,
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
        RunModelOptions.CacheRetention => !string.IsNullOrWhiteSpace(Draft.CacheRetention),
        RunModelOptions.MaxRetries => Draft.MaxRetries is not null,
        RunModelOptions.MaxRetryDelayMs => Draft.MaxRetryDelayMs is not null,
        RunModelOptions.ThinkingBudgets => Draft.ThinkingBudgets is { Count: > 0 },
        RunModelOptions.AzureApiVersion => !string.IsNullOrWhiteSpace(Draft.AzureApiVersion),
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

    /// <summary>The options this page shows and never sends, by RunModelOptions' name, because a run refuses them by design (RunModelOptions
    /// refuses the same): a base URL, the Azure base URL, resource name and deployment, and the Bedrock options (the Bedrock pane, whose rows
    /// are <see cref="RunSpec.BedrockOptionNames"/>); and the API key, which is never taken from the page. Each reason says why, and where
    /// the value is set instead. Every option a run can take has a live control.</summary>
    public static IReadOnlyDictionary<string, RequestUnsentOption> Unsent { get; } = new Dictionary<string, RequestUnsentOption>(StringComparer.Ordinal)
    {
        [RunModelOptions.BaseUrl] = new(RunModelOptions.BaseUrl, "Refused per run by design: PiSharp sends an account's credential only to the account's own host. Set the base URL on the provider in Settings › Providers."),
        ["apiKey"] = new("apiKey", "The Playground never takes a key from the page: runs use the account below, whose credential stays on the server."),
        [RunModelOptions.AzureBaseUrl] = new(RunModelOptions.AzureBaseUrl, AzureHost),
        [RunModelOptions.AzureResourceName] = new(RunModelOptions.AzureResourceName, AzureHost),
        [RunModelOptions.AzureDeploymentName] = new(RunModelOptions.AzureDeploymentName, "Refused per run by design: it decides which model answers, while the Spend Guard prices the model you chose. Map deployments on the provider in Settings › Providers."),
        [RunModelOptions.Bedrock] = new(RunModelOptions.Bedrock, "Bedrock requests are signed, so these options are refused per run by design: they come from the provider's AWS profile, set in Settings › Providers."),
    };

    private const string AzureHost = "Refused per run by design: it names the host the account's key goes to. Set it on the provider in Settings › Providers.";

    /// <summary>The prompt cache retentions the cacheRetention row offers (<see cref="RunSpec.CacheRetentions"/>).</summary>
    public static IReadOnlyList<string> CacheRetentions => RunSpec.CacheRetentions;

    /// <summary>The levels the thinkingBudgets row has a budget for (<see cref="RunSpec.BudgetLevels"/>; xhigh and max use high's).</summary>
    public static IReadOnlyList<string> BudgetLevels => RunSpec.BudgetLevels;

    /// <summary>The draft's budget for thinking level <paramref name="level"/>, as the control's value, or empty.</summary>
    public string Budget(string level) =>
        Draft.ThinkingBudgets is { } budgets && budgets.TryGetValue(level, out var budget) ? budget.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>The model's catalog sampling values (Sampling's read-only samplingParams row), or null to leave the row out (the Playground
    /// always sets it).</summary>
    public RequestSamplingView? Sampling { get; init; }

    /// <summary>The longest Azure API version a run takes (RunSpec's limit).</summary>
    public const int MaxAzureApiVersion = 40;

    /// <summary>The retry delay's range as the Transport row says it (0 to 3,600,000 ms).</summary>
    public static string RetryDelayRange { get; } =
        string.Create(CultureInfo.InvariantCulture, $"0 to {RunSpec.MaxRetryDelayLimitMs:N0} ms");

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

    /// <summary>The Azure OpenAI Responses options a run refuses by design (set on the provider; <see cref="Unsent"/> says why). The API
    /// version is not one of them: it has a live row.</summary>
    public static IReadOnlyList<(string Name, string Placeholder, string Help)> AzureOptions { get; } =
    [
        ("azureBaseUrl", "https://{resource}.openai.azure.com/openai/v1", "Full endpoint base. Takes precedence over azureResourceName."),
        ("azureResourceName", "AZURE_OPENAI_RESOURCE_NAME", "Falls back to AZURE_OPENAI_RESOURCE_NAME."),
        ("azureDeploymentName", "model id", "Falls back to AZURE_OPENAI_DEPLOYMENT_NAME_MAP, then the model id."),
    ];

    /// <summary>Whether the model is on the Azure OpenAI Responses API.</summary>
    public bool OnAzure => Api == "azure-openai-responses";

    /// <summary>Whether the model is on Amazon Bedrock.</summary>
    public bool OnBedrock => Api == "bedrock-converse-stream";

    /// <summary>Whether the model's API is one the run engine patches (providerOptions can reach its body).</summary>
    public bool Patched => RunModelOptions.PatchedApis.Contains(Api);

    /// <summary>The Provider-specific pane shown first: the model's API's, or Azure's when the draft sets an Azure API version the model does
    /// not take (so the marked row is in view), unless it sets providerOptions too.</summary>
    public string ProviderPane => OnAzure ? "azure" : OnBedrock ? "bedrock"
        : Conflict(RunModelOptions.AzureApiVersion) && !IsSet(RunModelOptions.ProviderOptions) ? "azure" : "current";

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
