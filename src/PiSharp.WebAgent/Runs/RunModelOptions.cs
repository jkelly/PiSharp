// Decision 0011 (run W4, R0; brief rule 4): which request options a model and its API take, and the request a run sends. PiSharp's
// ChatRequest carries the transcript, the thinking level and the output cap only, so the rest of a run's options travel as a JSON patch
// that RunCaptureHandler merges into the provider's request body on its way out (objects merge, anything else replaces; a zstd body, as
// Codex sends, is decoded and encoded again), with the names of the tools to mark strict, and as extra headers; the run's timeout is the
// hook's too. Only options the API has are sent: temperature is never sent to a model that rejects it (OpenAI reasoning models; Anthropic
// while thinking), service tier only to the OpenAI Responses API of provider openai (all seven of PLAN.md's values) and to the Codex
// backend of provider openai-codex (auto, default, flex and priority: Codex CLI's Fast is priority), never Azure; text verbosity, the
// reasoning summary and the session id (prompt_cache_key) only to the Responses and Codex APIs; parallel tool calls to the OpenAI APIs
// (parallel_tool_calls) and Anthropic (disable_parallel_tool_use); strict tools to the OpenAI APIs. APIs PiSharp cannot patch yet
// (Mistral, Bedrock, Vertex and the rest) take thinking, max tokens, tools, images and the timeout only (Codex: no max tokens, see
// below). An option the user set that the model does not take is never dropped: the plan lists it as refused with the reason, and the
// engine refuses the run with that message. The faux models accept the sampling options offline and do not apply them (a note says so);
// echo-thinking refuses temperature and top P while thinking, as Anthropic does, so the refusal can be seen offline.
// One rule for the output cap (the user's max tokens, or the Spend Guard's "cap the output"): it is the whole output the request may write,
// thinking included, and the max tokens that goes on the wire, so the Spend Guard's reservation (which assumes it) holds. PiSharp's
// Anthropic Messages route raises a capped request's max_tokens by the thinking budget on a budget-thinking model (no
// compat.forceAdaptiveThinking), so the run's patch sets max_tokens to the cap and a thinking budget that fits inside it (the level's,
// at most the cap less 1,024 to answer); a cap below 2,048 cannot hold budget thinking and is refused. Amazon Bedrock raises it the same
// way but its requests are signed, so a cap with thinking on a Bedrock budget-thinking Claude model is refused. The Codex backend takes
// no output cap at all, so max tokens is refused there and the Spend Guard does not offer to cap its output. Anthropic takes tool choice
// auto or none only while the model thinks. Provider options never set the output's length or count (max_tokens, max_output_tokens,
// generationConfig.maxOutputTokens, n, candidateCount and the like): Max tokens and the Spend Guard's worst case decide those.
// W4 fix 2 (the options the Playground listed as "not sent"; they ride R0's body patch, which stays: decided after W5, PiSharp.AI keeps the
// request types of the Pi version it tracks, see decision 0011): cache retention reaches the body as a cache edit (RunCacheEdit:
// Anthropic's cache_control breakpoints, set,
// given an hour or removed; OpenRouter's Anthropic models on Chat Completions the same; prompt_cache_key and prompt_cache_retention on the
// OpenAI APIs), and a capped send gets the same breakpoints back, so max tokens no longer turns prompt caching off on a patched API (the
// router sends a capped request through its summary route, which sends none). Retries and their longest delay are the capture hook's
// (it sends the model request again after a 408, 409, 429 or 5xx answer or a network error, before any output). Thinking budgets reach
// Anthropic budget thinking (thinking.budget_tokens, inside max tokens as above) and Gemini 2.5 (thinkingConfig.thinkingBudget); a model
// that thinks adaptively (by effort), Gemini 3 (by level), the OpenAI APIs (by effort) and Bedrock (signed) refuse them. The Azure API
// version is the hook's too (the api-version of the request's URL). Never taken per run: a base URL, an Azure base URL or resource name
// (each would send the account's credential to a host the page typed), an Azure deployment (it decides which model answers, while the
// Spend Guard prices the model chosen) and the Bedrock options (signed requests).
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.WebAgent.Contracts;
using ChordJson = PiSharp.Durable.Chord.JsonValue;

namespace PiSharp.WebAgent.Runs;

/// <summary>Whether a model takes one option.</summary>
/// <param name="Option">The option's wire name (<see cref="RunModelOptions"/> constants).</param>
/// <param name="Supported">Whether it is taken (with thinking off).</param>
/// <param name="Reason">Why not, or a note about how it is taken; null when there is nothing to say.</param>
/// <param name="WithThinking">Whether it is taken while thinking is on (the same as <paramref name="Supported"/> unless the API says otherwise).</param>
public sealed record RunOptionSupport(string Option, bool Supported, string? Reason = null, bool? WithThinking = null);

/// <summary>What a model takes, for the Playground's controls and View code.</summary>
public sealed record RunCapabilities
{
    /// <summary>The provider id.</summary>
    public required string Provider { get; init; }

    /// <summary>The model id.</summary>
    public required string Model { get; init; }

    /// <summary>The model's name.</summary>
    public required string Name { get; init; }

    /// <summary>The model's API.</summary>
    public required string Api { get; init; }

    /// <summary>Whether it runs offline (the faux provider).</summary>
    public bool Offline { get; init; }

    /// <summary>The thinking levels it takes (<c>off</c> first).</summary>
    public required IReadOnlyList<string> ThinkingLevels { get; init; }

    /// <summary>Whether it takes images.</summary>
    public bool Images { get; init; }

    /// <summary>Its output cap, or null.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Its context window, or null.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>One entry per option.</summary>
    public required IReadOnlyList<RunOptionSupport> Options { get; init; }

    /// <summary>The service tiers it takes (<see cref="RunSpec.ServiceTiers"/> or fewer); empty when it takes none.</summary>
    public IReadOnlyList<string> ServiceTiers { get; init; } = [];

    /// <summary>The entry of <paramref name="option"/>, or null.</summary>
    public RunOptionSupport? Find(string option) => Options.FirstOrDefault(entry => entry.Option == option);
}

/// <summary>The request a run sends, and what happened to each option the user set.</summary>
public sealed record RunRequestPlan
{
    /// <summary>The spec (normalized).</summary>
    public required RunSpec Spec { get; init; }

    /// <summary>The model.</summary>
    public required DurableModel Model { get; init; }

    /// <summary>The messages as Pi wire JSON (the system prompt and tools ride in a system message).</summary>
    public required ImmutableArray<ChordJson> Messages { get; init; }

    /// <summary>The thinking level sent (null for off).</summary>
    public string? Thinking { get; init; }

    /// <summary>The JSON merged into the provider's request body, or null when nothing is.</summary>
    public JsonObject? Patch { get; init; }

    /// <summary>The path the patched request goes to (its end: <c>/chat/completions</c>, <c>/responses</c>, <c>/messages</c>,
    /// <c>:streamGenerateContent</c>), or null.</summary>
    public string? PatchPath { get; init; }

    /// <summary>Extra headers sent with the request, or null.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>The tools marked strict in the provider's request body, or null.</summary>
    public IReadOnlyList<string>? StrictTools { get; init; }

    /// <summary>The prompt cache edit of the request body (the run's cache retention), or null.</summary>
    public RunCacheEdit? Cache { get; init; }

    /// <summary>The cache edit a capped send gets when the run sets no cache retention (the router's capped route sends no cache
    /// breakpoints; this puts the main route's back), or null when the API needs none.</summary>
    public RunCacheEdit? CappedCache { get; init; }

    /// <summary>The thinking budget sent (the run's <c>thinkingBudgets</c> entry for its level), or null for the level's own.</summary>
    public int? ThinkingBudget { get; init; }

    /// <summary>The options the user set that are sent.</summary>
    public IReadOnlyList<string> Applied { get; init; } = [];

    /// <summary>The options the user set that the model does not take, with the reason: the run is refused when there is any.</summary>
    public IReadOnlyList<RunOptionSupport> Refused { get; init; } = [];

    /// <summary>Notes about how the request is sent.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Whether the run may be sent (nothing refused).</summary>
    public bool Allowed => Refused.Count == 0;

    /// <summary>The refusal's message, or null.</summary>
    public string? RefusalMessage => Allowed ? null
        : $"{Model.Provider}/{Model.Id} does not take {string.Join("; ", Refused.Select(entry => entry.Option + (entry.Reason is { } reason ? " (" + reason + ")" : "")))}. " +
          "Remove it or choose another model; nothing was sent.";
}

/// <summary>How a run's prompt caching reaches the provider's request body (RunModelOptions.ApplyCache).</summary>
/// <param name="Retention"><c>none</c> (no breakpoint, no cache key), <c>short</c> (the providers' default) or <c>long</c> (an hour on
/// Anthropic, <c>prompt_cache_retention: 24h</c> on OpenAI).</param>
/// <param name="Markers">The request's shape: <c>anthropic</c> (the Messages API's <c>cache_control</c> breakpoints: the system prompt, the
/// last user message, the last tool), <c>completions-anthropic</c> (Anthropic models behind Chat Completions: breakpoints on the first system
/// message, the last tool and the last message with text), <c>completions</c> and <c>responses</c> (OpenAI's prompt cache key and
/// <c>prompt_cache_retention</c>), or <c>responses-explicit</c> (<c>compat.supportsExplicitPromptCacheMode</c>:
/// <c>prompt_cache_options</c>).</param>
/// <param name="ToolMarker">Whether the last tool takes a breakpoint (<c>compat.supportsCacheControlOnTools</c>).</param>
public sealed record RunCacheEdit(string Retention, string? Markers, bool ToolMarker = true);

/// <summary>A run refused for options its model does not take (400).</summary>
public sealed class RunOptionException(RunRequestPlan plan) : ArgumentException(plan.RefusalMessage)
{
    /// <summary>The plan, with <see cref="RunRequestPlan.Refused"/>.</summary>
    public RunRequestPlan Plan { get; } = plan;
}

/// <summary>Option support per API, and run plans.</summary>
public static class RunModelOptions
{
    /// <summary>Option names.</summary>
    public const string Thinking = "thinking", MaxTokens = "maxTokens", Temperature = "temperature", TopP = "topP", TopK = "topK", Stop = "stop",
        Seed = "seed", PresencePenalty = "presencePenalty", FrequencyPenalty = "frequencyPenalty", ResponseFormat = "responseFormat",
        ToolChoice = "toolChoice", ServiceTier = "serviceTier", Headers = "headers", ProviderOptions = "providerOptions", Images = "images", Tools = "tools",
        ParallelToolCalls = "parallelToolCalls", StrictTools = "strict", TextVerbosity = "textVerbosity", ReasoningSummary = "reasoningSummary",
        SessionId = "sessionId", TimeoutMs = "timeoutMs";

    /// <summary>The option names of W4 fix 2 (<see cref="Carried"/>).</summary>
    public const string CacheRetention = "cacheRetention", MaxRetries = "maxRetries", MaxRetryDelayMs = "maxRetryDelayMs", ThinkingBudgets = "thinkingBudgets",
        BaseUrl = "baseUrl", AzureBaseUrl = "azureBaseUrl", AzureResourceName = "azureResourceName", AzureApiVersion = "azureApiVersion",
        AzureDeploymentName = "azureDeploymentName", Bedrock = "bedrock";

    /// <summary>Every option, in the Playground's order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Thinking, MaxTokens, Temperature, TopP, TopK, Stop, Seed, PresencePenalty, FrequencyPenalty, ResponseFormat, ToolChoice, ServiceTier, Headers, ProviderOptions, Images, Tools,
         ParallelToolCalls, StrictTools, TextVerbosity, ReasoningSummary, SessionId, TimeoutMs];

    /// <summary>The options the run engine took in W4 fix 2, which the Playground still lists as rows of their own ("not sent" until its
    /// controls post them): cache retention, retries, the retry delay, thinking budgets and the Azure API version, and those it answers with
    /// a refusal (a base URL, the Azure base URL, resource name and deployment, the Bedrock options). <see cref="Capabilities"/> lists them
    /// after <see cref="All"/>.</summary>
    public static IReadOnlyList<string> Carried { get; } =
        [CacheRetention, MaxRetries, MaxRetryDelayMs, ThinkingBudgets, BaseUrl, AzureBaseUrl, AzureResourceName, AzureApiVersion, AzureDeploymentName, Bedrock];

    /// <summary>The APIs whose request body a run patches.</summary>
    public static IReadOnlyList<string> PatchedApis { get; } = [CompletionsApi, ResponsesApi, AzureResponsesApi, CodexApi, AnthropicApi, GoogleApi];

    /// <summary>The service tiers the Codex backend takes (Codex CLI sends its Fast mode as <c>priority</c>, and Flex as <c>flex</c>; auto and
    /// default are the Responses API's own).</summary>
    public static IReadOnlyList<string> CodexServiceTiers { get; } = ["auto", "default", "flex", "priority"];

    internal const string FauxApi = "faux", CompletionsApi = "openai-completions", ResponsesApi = "openai-responses", AzureResponsesApi = "azure-openai-responses",
        CodexApi = "openai-codex-responses", AnthropicApi = "anthropic-messages", GoogleApi = "google-generative-ai", BedrockApi = "bedrock-converse-stream";

    /// <summary>Why a spec's options are only for the Responses and Codex APIs.</summary>
    private const string ResponsesOnly = "only the OpenAI Responses and Codex APIs take it";

    /// <summary>The least output cap budget thinking takes on Anthropic: 1,024 tokens to think and 1,024 to answer.</summary>
    public const int MinThinkingOutputTokens = 2 * AnswerTokens;

    // What Anthropic budget thinking leaves to answer, as PiSharp's routes (and upstream Pi) do.
    private const int AnswerTokens = 1024;

    /// <summary>What <paramref name="model"/> takes (<paramref name="catalog"/>: its catalog row, for thinking levels and limits).</summary>
    public static RunCapabilities Capabilities(DurableModel model, CatalogModel? catalog)
    {
        ArgumentNullException.ThrowIfNull(model);
        var levels = Levels(model, catalog);
        return new RunCapabilities
        {
            Provider = model.Provider,
            Model = model.Id,
            Name = catalog?.Name ?? model.Name,
            Api = model.Api,
            Offline = model.Api == FauxApi,
            ThinkingLevels = levels,
            Images = TakesImages(model, catalog),
            MaxTokens = OutputCap(model, catalog),
            ContextWindow = catalog is { ContextWindow: > 0 } row ? row.ContextWindow : model.ContextWindow >= 1 ? (int)Math.Min(model.ContextWindow, int.MaxValue) : null,
            Options = [.. All.Concat(Carried).Select(option =>
            {
                var off = Support(option, model, catalog, thinking: false);
                var on = levels.Count > 1 ? Support(option, model, catalog, thinking: true) : off;
                return new RunOptionSupport(option, off.Ok, off.Why ?? (on.Ok ? null : on.Why), on.Ok);
            })],
            ServiceTiers = Tiers(model, catalog),
        };
    }

    /// <summary>The service tiers <paramref name="model"/> takes: none, the Codex backend's, or all of <see cref="RunSpec.ServiceTiers"/>.</summary>
    public static IReadOnlyList<string> Tiers(DurableModel model, CatalogModel? catalog)
    {
        ArgumentNullException.ThrowIfNull(model);
        return !Support(ServiceTier, model, catalog, thinking: false).Ok ? [] : model.Api == CodexApi ? CodexServiceTiers : RunSpec.ServiceTiers;
    }

    /// <summary>The request <paramref name="spec"/> (normalized) sends to <paramref name="model"/>: its messages, patch and headers, and
    /// every option the user set that the model does not take.</summary>
    public static RunRequestPlan Plan(RunSpec spec, DurableModel model, CatalogModel? catalog, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(model);
        var thinking = spec.ThinkingLevel;
        var applied = new List<string>();
        var refused = new List<RunOptionSupport>();
        var notes = new List<string>();
        void Use(string option, bool set, string? extraCheck = null)
        {
            if (!set) return;
            if (extraCheck is not null)
            {
                refused.Add(new RunOptionSupport(option, false, extraCheck));
                return;
            }
            var (ok, why) = Support(option, model, catalog, thinking is not null);
            if (ok) applied.Add(option);
            else refused.Add(new RunOptionSupport(option, false, why));
        }

        var levels = Levels(model, catalog);
        if (thinking is not null)
            Use(Thinking, true, levels.Contains(thinking) ? null
                : levels.Count <= 1 ? "this model does not think" : $"it thinks at {string.Join(", ", levels.Skip(1))}");
        var cap = OutputCap(model, catalog);
        Use(MaxTokens, spec.MaxTokens is not null, spec.MaxTokens > cap ? $"at most {cap:N0} output tokens"
            : spec.MaxTokens is { } maximum && Support(MaxTokens, model, catalog, thinking is not null).Ok ? OutputCapRefusal(model, thinking, maximum) : null);
        var anthropic = model.Api == AnthropicApi;
        Use(Temperature, spec.Temperature is not null, anthropic && spec.Temperature > 1 ? "Anthropic takes temperature 0 to 1" : null);
        Use(TopP, spec.TopP is not null, anthropic && spec.Temperature is not null && spec.TopP is not null ? "Anthropic takes temperature or top P, not both" : null);
        Use(TopK, spec.TopK is not null);
        Use(Stop, spec.Stop is not null, model.Api == CompletionsApi && spec.Stop?.Count > 4 ? "at most 4 stop sequences" : null);
        Use(Seed, spec.Seed is not null);
        Use(PresencePenalty, spec.PresencePenalty is not null);
        Use(FrequencyPenalty, spec.FrequencyPenalty is not null);
        Use(ResponseFormat, spec.ResponseFormat is not null && Format(spec.ResponseFormat) != "text");
        if (spec.ResponseFormat is not null && Format(spec.ResponseFormat) == "text") applied.Add(ResponseFormat);
        Use(ToolChoice, spec.ToolChoice is not null, anthropic && thinking is not null && spec.ToolChoice is not ("auto" or "none")
            ? "Anthropic takes only auto or none while the model thinks; turn thinking off to force a tool call" : null);
        var tiers = Tiers(model, catalog);
        Use(ServiceTier, spec.ServiceTier is not null, spec.ServiceTier is { } tier && tiers.Count > 0 && !tiers.Contains(tier)
            ? $"the {(model.Api == CodexApi ? "Codex backend" : model.Api + " API")} takes {string.Join(", ", tiers)}, not {tier}" : null);
        Use(Headers, spec.Headers is not null);
        Use(ProviderOptions, spec.ProviderOptions is not null, spec.ProviderOptions is { } options && Support(ProviderOptions, model, catalog, thinking is not null).Ok
            ? OutputOptionRefusal(options) : null);
        Use(Images, spec.Messages.Any(message => message.Images is { Count: > 0 }));
        Use(Tools, spec.Tools is { Count: > 0 });
        Use(ParallelToolCalls, spec.ParallelToolCalls is not null,
            anthropic && spec.ParallelToolCalls is not null && spec.ToolChoice == "none" ? "Anthropic takes no parallel switch with tool choice none" : null);
        var strict = spec.Tools?.Where(tool => tool.Strict == true).Select(tool => tool.Name).ToList();
        Use(StrictTools, strict is { Count: > 0 });
        Use(TextVerbosity, spec.TextVerbosity is not null);
        Use(ReasoningSummary, spec.ReasoningSummary is not null);
        Use(SessionId, spec.SessionId is not null);
        Use(TimeoutMs, spec.TimeoutMs is not null);

        // W4 fix 2: the options the Playground listed as not sent.
        bool Takes(string option) => Support(option, model, catalog, thinking is not null).Ok;
        var cache = spec.CacheRetention is { } retention && Takes(CacheRetention) ? CachePlan(spec, model, retention) : default;
        Use(CacheRetention, spec.CacheRetention is not null, cache.Why);
        Use(MaxRetries, spec.MaxRetries is not null);
        Use(MaxRetryDelayMs, spec.MaxRetryDelayMs is not null, spec.MaxRetryDelayMs is not null && Takes(MaxRetryDelayMs) && spec.MaxRetries is not > 0
            ? "it is how long a retry may wait, so it needs max retries of 1 or more" : null);
        var budgetLevel = thinking is "xhigh" or "max" ? "high" : thinking;
        int? budget = spec.ThinkingBudgets is { } budgets && budgetLevel is not null && budgets.TryGetValue(budgetLevel, out var levelBudget) ? levelBudget : null;
        var budgetTaken = spec.ThinkingBudgets is not null && Takes(ThinkingBudgets) && model.Api != FauxApi;
        Use(ThinkingBudgets, spec.ThinkingBudgets is not null, budgetTaken && budget is { } own ? BudgetRefusal(model, catalog, spec.MaxTokens, own) : null);
        if (budgetTaken && budget is null)
            notes.Add($"thinkingBudgets sets no {budgetLevel} budget, so thinking at {thinking} keeps its own.");
        Use(BaseUrl, spec.BaseUrl is not null);
        Use(AzureBaseUrl, spec.AzureBaseUrl is not null);
        Use(AzureResourceName, spec.AzureResourceName is not null);
        Use(AzureApiVersion, spec.AzureApiVersion is not null);
        Use(AzureDeploymentName, spec.AzureDeploymentName is not null);
        Use(Bedrock, spec.Bedrock is not null);

        JsonObject? patch = null;
        string? path = null;
        var headers = spec.Headers;
        RunCacheEdit? cappedCache = null;
        var sentBudget = refused.Count == 0 && budgetTaken ? budget : null;
        if (refused.Count == 0 && PatchedApis.Contains(model.Api))
        {
            patch = Patch(spec, model.Api, sentBudget);
            if (OutputCapPatch(model, thinking, spec.MaxTokens, sentBudget) is { } output)
            {
                var given = output["thinking"]!["budget_tokens"]!.GetValue<int>();
                Merge(patch, output, "", null);
                var full = sentBudget ?? LevelBudget(thinking!);
                notes.Add($"Max tokens {Count(spec.MaxTokens!.Value)} is the whole output: thinking at {thinking} gets {Count(given)} tokens" +
                    (given < full ? $" of its {Count(full)}" : "") + $", and at least {Count(AnswerTokens)} are left to answer.");
            }
            if (spec.ProviderOptions is { } extra)
            {
                var conflicts = new List<string>();
                Merge(patch, (JsonObject)extra.DeepClone(), "", conflicts);
                foreach (var conflict in conflicts)
                    refused.Add(new RunOptionSupport(ProviderOptions, false, $"providerOptions.{conflict} conflicts with an option the run sets; set it in one place"));
            }
            if (patch.Count == 0 && strict is not { Count: > 0 }) patch = null;
            if (patch is not null || strict is { Count: > 0 } || cache.Edit is not null || spec.AzureApiVersion is not null) path = PatchPathOf(model.Api);
            // A capped send goes through the router's summary route, which sends no cache breakpoints: the edit puts the main route's back.
            if (spec.CacheRetention is null) cappedCache = DefaultCache(model);
            // Codex keys its session by headers as well as by prompt_cache_key (buildSSEHeaders): a run's session id sets both.
            if (model.Api == CodexApi && spec.SessionId is { } session)
                headers = new Dictionary<string, string>(spec.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
                {
                    ["session-id"] = session,
                    ["x-client-request-id"] = session,
                };
        }
        if (model.Api == FauxApi && applied.Any(option => option is Temperature or TopP or TopK or Stop or Seed or PresencePenalty or FrequencyPenalty or ResponseFormat or ToolChoice
            or ParallelToolCalls or StrictTools or TextVerbosity or ReasoningSummary or TimeoutMs or CacheRetention or MaxRetries or MaxRetryDelayMs or ThinkingBudgets))
            notes.Add("The faux model runs offline: it accepts sampling options and does not apply them.");
        if (spec.MaxTokens is not null && model.Api != FauxApi && !PatchedApis.Contains(model.Api))
            notes.Add($"On the {model.Api} API a max tokens setting sends the request without prompt caching (PiSharp's capped route).");
        if (spec.MaxRetries is > 0 && model.Api != FauxApi)
            notes.Add($"PiSharp sends the model request again, up to {spec.MaxRetries} {(spec.MaxRetries == 1 ? "time" : "times")}, after a 408, 409, 429 or 5xx " +
                "answer or a network error, before any output; one reservation covers the run.");
        return new RunRequestPlan
        {
            Spec = spec,
            Model = model,
            Messages = Messages(spec, model, timestamp),
            Thinking = thinking,
            Patch = refused.Count == 0 ? patch : null,
            PatchPath = refused.Count == 0 ? path : null,
            Headers = refused.Count == 0 ? headers : null,
            StrictTools = refused.Count == 0 && path is not null && strict is { Count: > 0 } ? strict : null,
            Cache = refused.Count == 0 ? cache.Edit : null,
            CappedCache = refused.Count == 0 ? cappedCache : null,
            ThinkingBudget = sentBudget,
            Applied = applied,
            Refused = refused,
            Notes = notes,
        };
    }

    /// <summary>The cache edit of <paramref name="retention"/> on <paramref name="model"/>'s API, or why the API does not take it.</summary>
    private static (RunCacheEdit? Edit, string? Why) CachePlan(RunSpec spec, DurableModel model, string retention)
    {
        var api = model.Api;
        var openai = api == ResponsesApi && model.Provider == "openai" || api == CompletionsApi && model.Provider == "openai";
        const string KeyConflict = "a session id is sent as the prompt cache key, which cache retention none leaves out; clear one of them";
        switch (api)
        {
            case AnthropicApi:
                if (retention == "long" && !CompatFlag(model, "supportsLongCacheRetention", true)) return (null, $"{model.Provider} keeps no hour-long prompt cache");
                return (new RunCacheEdit(retention, "anthropic", CompatFlag(model, "supportsCacheControlOnTools", true)), null);
            case CompletionsApi:
                if (retention == "long" && !openai) return (null, "only OpenAI's own API keeps a prompt cache for 24 hours");
                return (new RunCacheEdit(retention, AnthropicCompletions(model) ? "completions-anthropic" : "completions"), null);
            case ResponsesApi or AzureResponsesApi or CodexApi:
                if (retention == "none" && spec.SessionId is not null) return (null, KeyConflict);
                var explicitMode = api != CodexApi && CompatFlag(model, "supportsExplicitPromptCacheMode", false);
                if (retention == "long" && !openai && !explicitMode)
                    return (null, api == AzureResponsesApi ? "Azure OpenAI keeps no 24-hour prompt cache"
                        : api == CodexApi ? "the Codex backend keeps its own prompt cache" : "only OpenAI's own API keeps a prompt cache for 24 hours");
                return (new RunCacheEdit(retention, explicitMode ? "responses-explicit" : "responses"), null);
            case GoogleApi:
                return retention == "short" ? (null, null) : (null, "the Gemini API caches on its own and takes no cache setting; only short, its default, is taken");
            default:
                return (null, null);
        }
    }

    /// <summary>The cache edit that gives a capped send (the router's summary route, which turns caching off) the main route's prompt
    /// caching back, or null when the API's capped route caches as its main route does.</summary>
    private static RunCacheEdit? DefaultCache(DurableModel model) => model.Api switch
    {
        AnthropicApi => new RunCacheEdit("short", "anthropic", CompatFlag(model, "supportsCacheControlOnTools", true)),
        CompletionsApi when AnthropicCompletions(model) => new RunCacheEdit("short", "completions-anthropic"),
        ResponsesApi or AzureResponsesApi when CompatFlag(model, "supportsExplicitPromptCacheMode", false) => new RunCacheEdit("short", "responses-explicit"),
        _ => null,
    };

    /// <summary>Why <paramref name="budget"/> thinking tokens do not fit <paramref name="model"/>'s output (its max tokens, or its own cap), or null.</summary>
    private static string? BudgetRefusal(DurableModel model, CatalogModel? catalog, int? maxTokens, int budget)
    {
        var cap = maxTokens ?? OutputCap(model, catalog);
        if (model.Api == AnthropicApi)
        {
            if (budget < AnswerTokens) return $"Anthropic takes a thinking budget of at least {Count(AnswerTokens)} tokens";
            if (cap is { } limit && budget > limit - AnswerTokens)
                return maxTokens is not null
                    ? $"a {Count(budget)} token budget leaves less than {Count(AnswerTokens)} of max tokens ({Count(limit)}) to answer, and max tokens is the whole " +
                      "output; lower the budget or raise max tokens"
                    : $"a {Count(budget)} token budget leaves less than {Count(AnswerTokens)} of the model's {Count(limit)} output tokens to answer";
        }
        else if (cap is { } limit && budget > limit)
            return $"a {Count(budget)} token budget is above {(maxTokens is not null ? "max tokens" : "the model's output cap")} ({Count(limit)})";
        return null;
    }

    /// <summary>A boolean of the model's <c>compat</c> (models.json), or <paramref name="fallback"/>.</summary>
    private static bool CompatFlag(DurableModel model, string name, bool fallback) =>
        model.Wire is { } wire && wire.TryGetValue("compat", out var compat) && compat is PiSharp.Durable.Chord.JsonObjectValue fields &&
        fields.TryGetValue(name, out var value) && value is PiSharp.Durable.Chord.JsonBooleanValue flag ? flag.Value : fallback;

    /// <summary>An Anthropic model behind Chat Completions, which takes Anthropic's cache breakpoints (<c>compat.cacheControlFormat</c>, or
    /// OpenRouter's <c>anthropic/</c> models), as PiSharp's Completions route decides it.</summary>
    private static bool AnthropicCompletions(DurableModel model) =>
        model.Wire is { } wire && wire.TryGetValue("compat", out var compat) && compat is PiSharp.Durable.Chord.JsonObjectValue fields &&
        fields.TryGetValue("cacheControlFormat", out var format) && format is PiSharp.Durable.Chord.JsonStringValue { Value: var text }
            ? text == "anthropic"
            : model.Provider == "openrouter" && model.Id.StartsWith("anthropic/", StringComparison.Ordinal);

    /// <summary>A Gemini model that thinks by level rather than by budget (Gemini 3 and later, Gemma 4, the flash aliases), as PiSharp's Google route
    /// decides it.</summary>
    internal static bool GeminiLevelThinking(string id)
    {
        var normalized = id.ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(normalized, @"gemini-3(?:\.[0-9]+)?-(?:pro|flash)|gemini-(?:[4-9]|[1-9][0-9])|gemma-?4") ||
            normalized is "gemini-flash-latest" or "gemini-flash-lite-latest";
    }

    /// <summary>The thinking levels <paramref name="model"/> takes, <c>off</c> first.</summary>
    public static IReadOnlyList<string> Levels(DurableModel model, CatalogModel? catalog)
    {
        var levels = catalog?.ThinkingLevels is { Count: > 0 } listed ? listed : model.Reasoning ? ["off", "minimal", "low", "medium", "high"] : (IReadOnlyList<string>)["off"];
        return levels.Contains("off") ? levels : ["off", .. levels];
    }

    private static int? OutputCap(DurableModel model, CatalogModel? catalog) =>
        catalog is { MaxTokens: > 0 } row ? row.MaxTokens : model.MaxTokens >= 1 ? (int)Math.Min(model.MaxTokens, int.MaxValue) : null;

    private static bool TakesImages(DurableModel model, CatalogModel? catalog) =>
        model.Api != FauxApi && ((catalog?.Input ?? []).Contains("image") || model.Input.Contains("image"));

    private static (bool Ok, string? Why) Support(string option, DurableModel model, CatalogModel? catalog, bool thinking)
    {
        var api = model.Api;
        var reasoningOpenAI = model.Reasoning && model.Provider is "openai" or "azure";
        switch (option)
        {
            case Thinking:
                return Levels(model, catalog).Count > 1 ? (true, null) : (false, "this model does not think");
            case MaxTokens:
                return api == FauxApi ? (true, "the faux model ignores it")
                    : api == CodexApi ? (false, NoCodexCap)
                    : thinking && BedrockBudgetThinking(model) ? (false, BedrockCap)
                    : (true, null);
            case Tools:
                return (true, null);
            case Images:
                return TakesImages(model, catalog) ? (true, null) : (false, "this model takes text only");
            case Headers:
                return api == FauxApi ? (false, "the offline faux provider sends no HTTP request")
                    : api == BedrockApi ? (false, "Bedrock requests are signed, so PiSharp adds no headers to them")
                    : (true, null);
            case TimeoutMs:
                return api == FauxApi ? (true, "the faux model ignores it") : (true, null);
            case CacheRetention:
                return api == FauxApi ? (true, "the faux model ignores it")
                    : api == BedrockApi ? (false, "Bedrock requests are signed, so PiSharp cannot change their cache points")
                    : api == GoogleApi ? (true, "only short: the Gemini API caches on its own")
                    : PatchedApis.Contains(api) ? (true, null) : (false, $"PiSharp does not send it on the {api} API yet");
            case MaxRetries or MaxRetryDelayMs:
                // The hook re-sends only the model request, which it knows by its path on the patched APIs.
                return api == FauxApi ? (true, "the faux model ignores it")
                    : PatchedApis.Contains(api) ? (true, null) : (false, $"PiSharp retries only the APIs whose model request it knows, not {api}");
            case ThinkingBudgets:
                if (api == FauxApi) return (true, "the faux model ignores it");
                if (api == BedrockApi) return (false, "Bedrock requests are signed, so PiSharp cannot change their thinking budget");
                if (!model.Reasoning) return (false, "this model does not think");
                if (api == AnthropicApi)
                    return !AnthropicBudgetThinking(model) ? (false, "this model thinks adaptively, by effort, and takes no budget")
                        : !thinking ? (false, "only while the model thinks") : (true, null);
                if (api == GoogleApi)
                    return GeminiLevelThinking(model.Id) ? (false, "Gemini 3 models think by level, not by budget")
                        : !thinking ? (false, "only while the model thinks") : (true, null);
                return api is CompletionsApi or ResponsesApi or AzureResponsesApi or CodexApi
                    ? (false, "this API takes a reasoning effort, not a budget")
                    : (false, $"PiSharp does not send it on the {api} API yet");
            case BaseUrl:
                return (false, "PiSharp sends an account's credential only to the account's own host; set the base URL on the account");
            case AzureBaseUrl or AzureResourceName:
                return api == AzureResponsesApi ? (false, "it names the host the account's key goes to; set it on the account")
                    : (false, "only Azure OpenAI takes it");
            case AzureApiVersion:
                return api == AzureResponsesApi ? (true, null) : (false, "only Azure OpenAI takes it");
            case AzureDeploymentName:
                return api == AzureResponsesApi
                    ? (false, "it decides which model answers, while the Spend Guard prices the model you chose; map deployments on the account")
                    : (false, "only Azure OpenAI takes it");
            case Bedrock:
                return api == BedrockApi ? (false, "Bedrock requests are signed, so PiSharp cannot change them per run; set the region and profile on the account")
                    : (false, "only Amazon Bedrock takes it");
        }
        if (api == FauxApi)
        {
            if (option is ServiceTier or ProviderOptions or SessionId) return (false, "the offline faux provider sends no HTTP request");
            if (thinking && option is Temperature or TopP) return (false, "not while this model thinks");
            if (!thinking && option == ReasoningSummary) return (false, "only while the model thinks");
            return (true, null);
        }
        if (!PatchedApis.Contains(api)) return (false, $"PiSharp does not send it on the {api} API yet");
        if (option == ProviderOptions) return (true, null);
        switch (api)
        {
            case CompletionsApi:
                return option switch
                {
                    TopK => (false, "the Chat Completions API has no top_k"),
                    ServiceTier => (false, "only the OpenAI Responses and Codex APIs take a service tier"),
                    TextVerbosity or ReasoningSummary or SessionId => (false, ResponsesOnly),
                    Temperature or TopP or PresencePenalty or FrequencyPenalty when reasoningOpenAI => (false, "OpenAI reasoning models reject it"),
                    _ => (true, null),
                };
            case ResponsesApi or AzureResponsesApi or CodexApi:
                return option switch
                {
                    Temperature or TopP when model.Reasoning => (false, "reasoning models on the Responses API reject it"),
                    TopK or Seed or PresencePenalty or FrequencyPenalty or Stop => (false, "the Responses API has no such parameter"),
                    ServiceTier when api == AzureResponsesApi => (false, "Azure OpenAI takes no service tier"),
                    ServiceTier when api == CodexApi && model.Provider != "openai-codex" => (false, "only provider OpenAI Codex takes a service tier on the Codex API"),
                    ServiceTier when api == ResponsesApi && model.Provider != "openai" => (false, "only provider OpenAI takes a service tier"),
                    ReasoningSummary when !thinking => (false, "only while the model thinks"),
                    _ => (true, null),
                };
            case AnthropicApi:
                return option switch
                {
                    Temperature or TopP or TopK when thinking => (false, "Anthropic rejects it while the model thinks"),
                    ToolChoice when thinking => (true, "only auto or none while the model thinks"),
                    Seed or PresencePenalty or FrequencyPenalty or ResponseFormat => (false, "the Anthropic Messages API has no such parameter"),
                    ServiceTier => (false, "only the OpenAI Responses and Codex APIs take a service tier"),
                    TextVerbosity or ReasoningSummary or SessionId => (false, ResponsesOnly),
                    StrictTools => (false, "PiSharp does not send strict tools to the Anthropic Messages API yet"),
                    _ => (true, null),
                };
            case GoogleApi:
                return option switch
                {
                    ServiceTier => (false, "only the OpenAI Responses and Codex APIs take a service tier"),
                    TextVerbosity or ReasoningSummary or SessionId => (false, ResponsesOnly),
                    ParallelToolCalls => (false, "the Gemini API has no parallel tool call switch"),
                    StrictTools => (false, "the Gemini API has no strict tools"),
                    _ => (true, null),
                };
        }
        return (false, $"PiSharp does not send it on the {api} API yet");
    }

    private const string NoCodexCap = "the Codex backend takes no output cap";

    private const string BedrockCap = "Amazon Bedrock adds the thinking budget to max tokens and PiSharp cannot change a signed Bedrock request, so the cap " +
        "would not hold; turn thinking off or clear max tokens";

    /// <summary>Why an output cap of <paramref name="maxTokens"/> (the user's max tokens, or the Spend Guard's "cap the output") cannot be the
    /// whole output of a request to <paramref name="model"/> at <paramref name="thinking"/> (null for off), or null when it can.</summary>
    public static string? OutputCapRefusal(DurableModel model, string? thinking, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Api == CodexApi) return NoCodexCap;
        if (thinking is null) return null;
        if (BedrockBudgetThinking(model)) return BedrockCap;
        if (AnthropicBudgetThinking(model) && maxTokens < MinThinkingOutputTokens)
            return $"Anthropic budget thinking needs a max tokens of at least {Count(MinThinkingOutputTokens)} ({Count(AnswerTokens)} to think and " +
                $"{Count(AnswerTokens)} to answer), and max tokens is the whole output; raise it to {Count(MinThinkingOutputTokens)} or more, or turn thinking off";
        return null;
    }

    /// <summary>The fields a request to <paramref name="model"/> at <paramref name="thinking"/> needs so that <paramref name="maxTokens"/> is
    /// its whole output: on Anthropic budget thinking, <c>max_tokens</c> and a thinking budget that fits inside it (the level's, at most
    /// <paramref name="maxTokens"/> less 1,024); null when the request needs none (no cap, thinking off, another API).</summary>
    /// <exception cref="ArgumentException">The cap cannot hold budget thinking (<see cref="OutputCapRefusal"/>).</exception>
    public static JsonObject? OutputCapPatch(DurableModel model, string? thinking, int? maxTokens) => OutputCapPatch(model, thinking, maxTokens, null);

    /// <summary><see cref="OutputCapPatch(DurableModel, string?, int?)"/> with the run's own thinking budget (<paramref name="budget"/>, null
    /// for the level's), at most <paramref name="maxTokens"/> less 1,024.</summary>
    public static JsonObject? OutputCapPatch(DurableModel model, string? thinking, int? maxTokens, int? budget)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (thinking is null || maxTokens is not { } cap || !AnthropicBudgetThinking(model)) return null;
        if (OutputCapRefusal(model, thinking, cap) is { } why) throw new ArgumentException(why);
        return new JsonObject
        {
            ["max_tokens"] = cap,
            ["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = Math.Min(budget ?? LevelBudget(thinking), cap - AnswerTokens) },
        };
    }

    /// <summary>The patch and its path a run of <paramref name="plan"/> sends when its output is capped at <paramref name="maxTokens"/> (the
    /// Spend Guard's <c>GuardedSend.MaxTokens</c>): the plan's own when that is the plan's cap, else the plan's patch with the output-cap
    /// fields of <paramref name="maxTokens"/> (<see cref="OutputCapPatch"/>).</summary>
    /// <exception cref="ArgumentException">The cap cannot be the whole output (<see cref="OutputCapRefusal"/>).</exception>
    public static (JsonObject? Patch, string? Path) SendPatch(RunRequestPlan plan, int? maxTokens)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (maxTokens == plan.Spec.MaxTokens) return (plan.Patch, plan.PatchPath);
        if (maxTokens is { } cap && OutputCapRefusal(plan.Model, plan.Thinking, cap) is { } why) throw new ArgumentException(why);
        if (OutputCapPatch(plan.Model, plan.Thinking, maxTokens, plan.ThinkingBudget) is not { } output) return (plan.Patch, plan.PatchPath);
        var patch = plan.Patch is { } own ? (JsonObject)own.DeepClone() : new JsonObject();
        Merge(patch, output, "", null);
        return (patch, PatchPathOf(plan.Model.Api));
    }

    /// <summary>What a run of <paramref name="plan"/> sends when its output is capped at <paramref name="maxTokens"/>: the patch and its path
    /// (<see cref="SendPatch"/>), and the cache edit: the run's own, or, on a capped send (the router's summary route, which sends no cache
    /// breakpoints), the main route's breakpoints back (<see cref="RunRequestPlan.CappedCache"/>).</summary>
    /// <exception cref="ArgumentException">The cap cannot be the whole output (<see cref="OutputCapRefusal"/>).</exception>
    public static (JsonObject? Patch, string? Path, RunCacheEdit? Cache) Send(RunRequestPlan plan, int? maxTokens)
    {
        var (patch, path) = SendPatch(plan, maxTokens);
        var cache = plan.Cache ?? (maxTokens is not null ? plan.CappedCache : null);
        if (cache is not null && path is null) path = PatchPathOf(plan.Model.Api);
        return (patch, path, cache);
    }

    /// <summary>Anthropic Messages budget thinking: a reasoning model without <c>compat.forceAdaptiveThinking</c>, whose capped requests
    /// PiSharp's route raises by the thinking budget.</summary>
    internal static bool AnthropicBudgetThinking(DurableModel model) =>
        model.Api == AnthropicApi && model.Reasoning &&
        !(model.Wire is { } wire && wire.TryGetValue("compat", out var compat) && compat is PiSharp.Durable.Chord.JsonObjectValue fields &&
          fields.TryGetValue("forceAdaptiveThinking", out var adaptive) && adaptive is PiSharp.Durable.Chord.JsonBooleanValue { Value: true });

    /// <summary>A Bedrock Claude model with budget thinking, whose capped requests PiSharp.AI raises by the thinking budget (as
    /// BedrockConverseRequest.Simple and SupportsAdaptiveThinking decide it: by the model's id and name).</summary>
    internal static bool BedrockBudgetThinking(DurableModel model)
    {
        if (model.Api != BedrockApi || !model.Reasoning) return false;
        var names = new[] { model.Id, model.Name }.Where(name => !string.IsNullOrEmpty(name))
            .SelectMany(name => new[] { name.ToLowerInvariant(), System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"[\s_.:]+", "-") })
            .ToList();
        if (!names.Any(name => name.Contains("claude", StringComparison.Ordinal))) return false;
        string[] adaptive = ["opus-4-6", "opus-4-7", "opus-4-8", "opus-5", "sonnet-4-6", "sonnet-5", "haiku-5", "fable-5"];
        return !names.Any(name => adaptive.Any(marker => name.Contains(marker, StringComparison.Ordinal)));
    }

    /// <summary>The thinking budget of <paramref name="level"/> (minimal 1,024, low 2,048, medium 8,192, otherwise 16,384), as PiSharp's routes.</summary>
    private static int LevelBudget(string level) => level switch { "minimal" => 1024, "low" => 2048, "medium" => 8192, _ => 16384 };

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    // Provider option names that set the output's length or how many answers are written (compared without '_' and case): Max tokens and
    // the Spend Guard's worst case decide those.
    private static readonly string[] OutputNames =
        ["maxtokens", "maxcompletiontokens", "maxoutputtokens", "maxtokenstosample", "maxnewtokens", "n", "bestof", "candidatecount"];

    /// <summary>Whether the provider option <paramref name="key"/> sets the output's length or how many answers are written (so
    /// <see cref="RunSpec.Normalize"/> leaves it to the plan's refusal rather than reading "max_tokens" as a credential).</summary>
    internal static bool OutputOptionName(string key) => OutputNames.Contains(key.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant());

    /// <summary>Why <paramref name="options"/> may not be sent, when they set the output's length or count; null otherwise.</summary>
    private static string? OutputOptionRefusal(JsonObject options)
    {
        var found = new List<string>();
        foreach (var (key, value) in options)
        {
            if (OutputOptionName(key)) found.Add(key);
            else if (key.Replace("_", "", StringComparison.Ordinal).Equals("generationconfig", StringComparison.OrdinalIgnoreCase) && value is JsonObject config)
                found.AddRange(config.Where(pair => OutputOptionName(pair.Key)).Select(pair => key + "." + pair.Key));
        }
        return found.Count == 0 ? null
            : $"providerOptions.{string.Join(", providerOptions.", found)} would change how long or how many answers the provider writes, which Max tokens " +
              "and the Spend Guard's worst case decide; use Max tokens instead (a run asks for one answer)";
    }

    private static string? Format(JsonObject? format) => format?["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : null;

    private static string PatchPathOf(string api) => api switch
    {
        CompletionsApi => "/chat/completions",
        ResponsesApi or AzureResponsesApi => "/responses",
        CodexApi => "/codex/responses",
        AnthropicApi => "/messages",
        GoogleApi => ":streamGenerateContent",
        _ => throw new ArgumentOutOfRangeException(nameof(api)),
    };

    private static JsonObject Patch(RunSpec spec, string api, int? budget)
    {
        var patch = new JsonObject();
        var format = Format(spec.ResponseFormat);
        var schema = spec.ResponseFormat?["schema"]?.DeepClone();
        var schemaName = spec.ResponseFormat?["name"] is JsonValue named && named.TryGetValue<string>(out var name) ? name : "response";
        var strict = spec.ResponseFormat?["strict"] is JsonValue strictValue && strictValue.TryGetValue<bool>(out var isStrict) ? isStrict : (bool?)null;
        var tool = spec.ToolChoice is null or "auto" or "none" or "required" ? null : spec.ToolChoice;
        switch (api)
        {
            case CompletionsApi:
                Set(patch, "temperature", spec.Temperature);
                Set(patch, "top_p", spec.TopP);
                if (spec.Stop is { } stop) patch["stop"] = Strings(stop);
                if (spec.Seed is { } seed) patch["seed"] = seed;
                Set(patch, "presence_penalty", spec.PresencePenalty);
                Set(patch, "frequency_penalty", spec.FrequencyPenalty);
                if (format is not null)
                    patch["response_format"] = format == "json_schema"
                        ? new JsonObject { ["type"] = "json_schema", ["json_schema"] = Schema(schemaName, schema, strict) }
                        : new JsonObject { ["type"] = format };
                if (spec.ToolChoice is not null)
                    patch["tool_choice"] = tool is null ? JsonValue.Create(spec.ToolChoice) : new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = tool } };
                if (spec.ParallelToolCalls is { } parallelCompletions) patch["parallel_tool_calls"] = parallelCompletions;
                break;
            case ResponsesApi or AzureResponsesApi or CodexApi:
                Set(patch, "temperature", spec.Temperature);
                Set(patch, "top_p", spec.TopP);
                if (format is not null || spec.TextVerbosity is not null)
                {
                    var text = new JsonObject();
                    if (format is not null)
                    {
                        var described = format == "json_schema" ? Schema(schemaName, schema, strict) : new JsonObject();
                        described.Insert(0, "type", format);
                        text["format"] = described;
                    }
                    if (spec.TextVerbosity is { } verbosity) text["verbosity"] = verbosity;
                    patch["text"] = text;
                }
                if (spec.ToolChoice is not null) patch["tool_choice"] = tool is null ? JsonValue.Create(spec.ToolChoice) : new JsonObject { ["type"] = "function", ["name"] = tool };
                if (spec.ParallelToolCalls is { } parallelResponses) patch["parallel_tool_calls"] = parallelResponses;
                if (spec.ServiceTier is { } tier) patch["service_tier"] = tier;
                if (spec.ReasoningSummary is { } summary) patch["reasoning"] = new JsonObject { ["summary"] = summary };
                if (spec.SessionId is { } session) patch["prompt_cache_key"] = session;
                break;
            case AnthropicApi:
                Set(patch, "temperature", spec.Temperature);
                Set(patch, "top_p", spec.TopP);
                if (spec.TopK is { } topK) patch["top_k"] = topK;
                if (spec.Stop is { } sequences) patch["stop_sequences"] = Strings(sequences);
                if (spec.ToolChoice is not null || spec.ParallelToolCalls is not null)
                {
                    var choice = spec.ToolChoice switch
                    {
                        null or "auto" => new JsonObject { ["type"] = "auto" },
                        "none" => new JsonObject { ["type"] = "none" },
                        "required" => new JsonObject { ["type"] = "any" },
                        _ => new JsonObject { ["type"] = "tool", ["name"] = tool },
                    };
                    if (spec.ParallelToolCalls is { } parallelAnthropic) choice["disable_parallel_tool_use"] = !parallelAnthropic;
                    patch["tool_choice"] = choice;
                }
                if (budget is { } anthropicBudget) patch["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = anthropicBudget };
                break;
            case GoogleApi:
                var generation = new JsonObject();
                Set(generation, "temperature", spec.Temperature);
                Set(generation, "topP", spec.TopP);
                if (spec.TopK is { } k) generation["topK"] = k;
                if (spec.Stop is { } stops) generation["stopSequences"] = Strings(stops);
                if (spec.Seed is { } googleSeed) generation["seed"] = googleSeed;
                Set(generation, "presencePenalty", spec.PresencePenalty);
                Set(generation, "frequencyPenalty", spec.FrequencyPenalty);
                if (format is "json_object" or "json_schema") generation["responseMimeType"] = "application/json";
                if (format == "json_schema" && schema is not null) generation["responseJsonSchema"] = schema;
                if (budget is { } googleBudget) generation["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = googleBudget };
                if (generation.Count > 0) patch["generationConfig"] = generation;
                if (spec.ToolChoice is not null)
                {
                    var calling = new JsonObject { ["mode"] = spec.ToolChoice switch { "none" => "NONE", "auto" => "AUTO", _ => "ANY" } };
                    if (tool is not null) calling["allowedFunctionNames"] = new JsonArray(JsonValue.Create(tool));
                    patch["toolConfig"] = new JsonObject { ["functionCallingConfig"] = calling };
                }
                break;
        }
        return patch;
    }

    private static JsonObject Schema(string name, JsonNode? schema, bool? strict)
    {
        var result = new JsonObject { ["name"] = name, ["schema"] = schema };
        if (strict is { } value) result["strict"] = value;
        return result;
    }

    private static JsonArray Strings(IEnumerable<string> values) => [.. values.Select(value => (JsonNode)JsonValue.Create(value)!)];

    private static void Set(JsonObject target, string key, double? value)
    {
        if (value is { } number) target[key] = number;
    }

    /// <summary>Merges <paramref name="source"/> into <paramref name="target"/>: objects merge, anything else replaces; a key the run already
    /// set to something other than an object is a conflict.</summary>
    internal static void Merge(JsonObject target, JsonObject source, string path, List<string>? conflicts)
    {
        foreach (var (key, value) in source.ToList())
        {
            source.Remove(key);
            var at = path.Length == 0 ? key : path + "." + key;
            if (target[key] is JsonObject existing && value is JsonObject incoming) Merge(existing, incoming, at, conflicts);
            else if (conflicts is not null && target.ContainsKey(key)) conflicts.Add(at);
            else target[key] = value;
        }
    }

    /// <summary>Applies a run's request changes to the provider's request body (<paramref name="api"/>'s shape): merges
    /// <paramref name="patch"/> (objects merge, anything else replaces) and marks the tools named in <paramref name="strictTools"/> strict.</summary>
    /// <returns>The strict tools it did not find in the body (the request must not be sent without them).</returns>
    public static IReadOnlyList<string> ApplyToBody(JsonObject body, JsonObject? patch, IReadOnlyCollection<string>? strictTools, string? api) =>
        ApplyToBody(body, patch, strictTools, api, null);

    /// <summary><see cref="ApplyToBody(JsonObject, JsonObject?, IReadOnlyCollection{string}?, string?)"/>, then the run's prompt cache edit
    /// (<see cref="ApplyCache"/>).</summary>
    public static IReadOnlyList<string> ApplyToBody(JsonObject body, JsonObject? patch, IReadOnlyCollection<string>? strictTools, string? api, RunCacheEdit? cache)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (patch is not null) Merge(body, (JsonObject)patch.DeepClone(), "", null);
        if (cache is not null) ApplyCache(body, cache);
        if (strictTools is not { Count: > 0 }) return [];
        var missing = new HashSet<string>(strictTools, StringComparer.Ordinal);
        foreach (var tool in (body["tools"] as JsonArray ?? []).OfType<JsonObject>())
        {
            // Chat Completions nests a function's definition ({type: function, function: {name, parameters, strict}}); the Responses and
            // Codex APIs keep it flat ({type: function, name, parameters, strict}).
            var definition = api == CompletionsApi ? tool["function"] as JsonObject : tool;
            if (definition?["name"] is JsonValue value && value.TryGetValue<string>(out var name) && missing.Remove(name)) definition["strict"] = true;
        }
        return [.. missing.Order(StringComparer.Ordinal)];
    }

    /// <summary>Sets the request body's prompt caching to <paramref name="cache"/>: <c>none</c> takes every breakpoint, cache key and retention
    /// out; <c>short</c> and <c>long</c> rewrite the breakpoints the route placed, or place them where PiSharp's main route does when there are
    /// none (a capped send), and set or clear the 24-hour retention.</summary>
    public static void ApplyCache(JsonObject body, RunCacheEdit cache)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(cache);
        var retention = cache.Retention;
        switch (cache.Markers)
        {
            case "anthropic":
                Breakpoints(body, retention, () => AnthropicPlaces(body, cache.ToolMarker), retention == "long" ? "1h" : null);
                break;
            case "completions" or "completions-anthropic":
                body.Remove("prompt_cache_retention");
                if (retention == "none") body.Remove("prompt_cache_key");
                if (retention == "long" && cache.Markers == "completions") body["prompt_cache_retention"] = "24h";
                if (cache.Markers == "completions-anthropic") Breakpoints(body, retention, () => CompletionsPlaces(body), null);
                else if (retention == "none") Breakpoints(body, "none", () => [], null);
                break;
            case "responses" or "responses-explicit":
                var explicitMode = cache.Markers == "responses-explicit";
                body.Remove("prompt_cache_retention");
                if (explicitMode) body.Remove("prompt_cache_options");
                if (retention == "none")
                {
                    body.Remove("prompt_cache_key");
                    if (explicitMode) body["prompt_cache_options"] = new JsonObject { ["mode"] = "explicit" };
                }
                else if (retention == "long")
                {
                    if (explicitMode) body["prompt_cache_options"] = new JsonObject { ["ttl"] = "30m" };
                    else body["prompt_cache_retention"] = "24h";
                }
                break;
        }
    }

    // Anthropic takes at most 4 cache breakpoints in a request.
    private const int MaxBreakpoints = 4;

    /// <summary>Rewrites every <c>cache_control</c> in the body to <paramref name="retention"/> (none removes them); when there are none and
    /// the retention keeps a cache, places them at <paramref name="places"/>.</summary>
    private static void Breakpoints(JsonObject body, string retention, Func<IReadOnlyList<JsonObject>> places, string? ttl)
    {
        var marked = new List<JsonObject>();
        Marked(body, marked);
        JsonObject Control() => ttl is null ? new JsonObject { ["type"] = "ephemeral" } : new JsonObject { ["type"] = "ephemeral", ["ttl"] = ttl };
        if (retention == "none")
        {
            foreach (var block in marked) block.Remove("cache_control");
            return;
        }
        var targets = marked.Count > 0 ? marked : places().Take(MaxBreakpoints).ToList();
        foreach (var block in targets) block["cache_control"] = Control();
    }

    /// <summary>The tools, system blocks, messages and message blocks that carry a breakpoint (never inside a tool's schema or a value).</summary>
    private static void Marked(JsonObject body, List<JsonObject> found)
    {
        void Check(JsonNode? node)
        {
            if (node is JsonObject value && value.ContainsKey("cache_control")) found.Add(value);
        }
        foreach (var tool in body["tools"] as JsonArray ?? []) Check(tool);
        foreach (var block in body["system"] as JsonArray ?? []) Check(block);
        foreach (var message in body["messages"] as JsonArray ?? [])
        {
            Check(message);
            if (message is JsonObject entry && entry["content"] is JsonArray content)
                foreach (var block in content) Check(block);
        }
    }

    /// <summary>Where PiSharp's Anthropic route places breakpoints: the last tool, the last system block, the last block of the last message
    /// when it is the user's.</summary>
    private static List<JsonObject> AnthropicPlaces(JsonObject body, bool toolMarker)
    {
        var places = new List<JsonObject>();
        if (toolMarker && body["tools"] is JsonArray { Count: > 0 } tools && tools[^1] is JsonObject lastTool) places.Add(lastTool);
        if (body["system"] is JsonValue systemText && systemText.TryGetValue<string>(out var text) && text.Length > 0)
            body["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        if (body["system"] is JsonArray { Count: > 0 } system && system[^1] is JsonObject lastSystem) places.Add(lastSystem);
        if (body["messages"] is JsonArray { Count: > 0 } messages && messages[^1] is JsonObject last && Role(last) == "user" && LastBlock(last) is { } block)
            places.Add(block);
        return places;
    }

    /// <summary>Where PiSharp's Completions route places Anthropic breakpoints: the first system or developer message's last text, the last
    /// tool, the last user, assistant or tool message with text.</summary>
    private static List<JsonObject> CompletionsPlaces(JsonObject body)
    {
        var places = new List<JsonObject>();
        var messages = (body["messages"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (messages.FirstOrDefault(message => Role(message) is "system" or "developer") is { } system && LastText(system) is { } systemText)
            places.Add(systemText);
        if (body["tools"] is JsonArray { Count: > 0 } tools && tools[^1] is JsonObject lastTool) places.Add(lastTool);
        for (var index = messages.Count - 1; index >= 0; index--)
            if (Role(messages[index]) is "user" or "assistant" or "tool" && LastText(messages[index]) is { } text)
            {
                places.Add(text);
                break;
            }
        return places;
    }

    private static string? Role(JsonObject message) => message["role"] is JsonValue value && value.TryGetValue<string>(out var role) ? role : null;

    /// <summary>The message's last content block (a string content becomes one text block).</summary>
    private static JsonObject? LastBlock(JsonObject message)
    {
        if (message["content"] is JsonValue value && value.TryGetValue<string>(out var text))
            message["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        return message["content"] is JsonArray { Count: > 0 } blocks ? blocks[^1] as JsonObject : null;
    }

    /// <summary>The message's last text part (a string content becomes one text part), or null.</summary>
    private static JsonObject? LastText(JsonObject message)
    {
        if (message["content"] is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (text.Length == 0) return null;
            message["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        return (message["content"] as JsonArray ?? []).OfType<JsonObject>()
            .LastOrDefault(part => part["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind == "text");
    }

    /// <summary>The path of <paramref name="api"/>'s model request (its end), or null for an API whose request a run does not change.</summary>
    public static string? ModelRequestPath(string api) => PatchedApis.Contains(api) ? PatchPathOf(api) : null;

    /// <summary>Pi wire messages of <paramref name="spec"/>: a system message with the system prompt and the tools, then the messages.</summary>
    internal static ImmutableArray<ChordJson> Messages(RunSpec spec, DurableModel model, long timestamp)
    {
        var messages = new List<JsonObject>();
        var at = timestamp;
        if (spec.SystemPrompt is not null || spec.Tools is { Count: > 0 })
        {
            var system = new JsonObject { ["role"] = "system", ["content"] = spec.SystemPrompt ?? "" };
            if (spec.Tools is { Count: > 0 } tools)
                system["toolsAdded"] = new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description ?? "",
                    ["parameters"] = tool.Parameters?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                })]);
            system["timestamp"] = at;
            messages.Add(system);
        }
        foreach (var message in spec.Messages)
        {
            at++;
            switch (message.Role)
            {
                case "system":
                    messages.Add(new JsonObject { ["role"] = "system", ["content"] = message.Text ?? "", ["timestamp"] = at });
                    break;
                case "user":
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = Content(message, plainWhenText: true), ["timestamp"] = at });
                    break;
                case "assistant":
                    var content = ImmutableArray.CreateBuilder<AssistantContent>();
                    if (!string.IsNullOrEmpty(message.Text)) content.Add(new TextContent(message.Text));
                    foreach (var call in message.ToolCalls ?? [])
                        content.Add(new ToolCallContent(call.Id, call.Name, JsonData.Parse((call.Arguments ?? new JsonObject()).ToJsonString())));
                    var assistant = new AssistantMessage(model.Api, model.Provider, model.Id, at, content.ToImmutable(), TokenUsage.Zero,
                        message.ToolCalls is { Count: > 0 } ? StopReason.ToolUse : StopReason.Stop);
                    var wire = JsonNode.Parse(PiWireJson.WriteMessage(assistant).ToString())!.AsObject();
                    Signed(wire, message);
                    messages.Add(wire);
                    break;
                case "toolResult":
                    messages.Add(new JsonObject
                    {
                        ["role"] = "toolResult",
                        ["toolCallId"] = message.ToolCallId,
                        ["toolName"] = message.ToolName,
                        ["content"] = Content(message, plainWhenText: false),
                        ["isError"] = message.IsError == true,
                        ["timestamp"] = at,
                    });
                    break;
            }
        }
        return [.. messages.Select(message => ChordJson.Parse(message.ToJsonString()))];
    }

    /// <summary>Adds an assistant message's signed parts to its Pi wire JSON: its thinking blocks first (Pi's <c>thinking</c> blocks with
    /// <c>thinkingSignature</c> and <c>redacted</c>), and each tool call's <c>thoughtSignature</c>. The message is the model's own (its
    /// API, provider and model), so the provider adapters replay them as they were streamed.</summary>
    private static void Signed(JsonObject wire, RunMessage message)
    {
        if (wire["content"] is not JsonArray content) return;
        var position = 0;
        foreach (var block in message.Thinking ?? [])
        {
            var thinking = new JsonObject { ["type"] = "thinking", ["thinking"] = block.Text };
            if (block.Signature is { } signature) thinking["thinkingSignature"] = signature;
            if (block.Redacted) thinking["redacted"] = true;
            content.Insert(position++, thinking);
        }
        var calls = (message.ToolCalls ?? []).ToList();
        var index = 0;
        foreach (var part in content.OfType<JsonObject>())
        {
            if (part["type"] is not JsonValue type || type.GetValue<string>() != "toolCall") continue;
            if (index < calls.Count && calls[index].ThoughtSignature is { } thought) part["thoughtSignature"] = thought;
            index++;
        }
    }

    private static JsonNode Content(RunMessage message, bool plainWhenText)
    {
        if (plainWhenText && message.Images is not { Count: > 0 }) return JsonValue.Create(message.Text ?? "")!;
        var parts = new JsonArray();
        if (!string.IsNullOrEmpty(message.Text) || message.Images is not { Count: > 0 }) parts.Add(new JsonObject { ["type"] = "text", ["text"] = message.Text ?? "" });
        foreach (var image in message.Images ?? [])
            parts.Add(new JsonObject { ["type"] = "image", ["mimeType"] = image.MimeType, ["data"] = image.Data });
        return parts;
    }

    /// <summary>A number as the plan's notes write it.</summary>
    internal static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
