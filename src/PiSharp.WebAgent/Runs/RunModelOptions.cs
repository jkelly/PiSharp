// Decision 0011 (run W4, R0; brief rule 4): which request options a model and its API take, and the request a run sends. PiSharp's
// ChatRequest carries the transcript, the thinking level and the output cap only, so the rest of a run's options travel as a JSON patch
// that RunCaptureHandler merges into the provider's request body on its way out (objects merge, anything else replaces; a zstd body, as
// Codex sends, is decoded and encoded again), with the names of the tools to mark strict, and as extra headers; the run's timeout is the
// hook's too. Only options the API has are sent: temperature is never sent to a model that rejects it (OpenAI reasoning models; Anthropic
// while thinking), service tier only to the OpenAI Responses API of provider openai (all seven of PLAN.md's values) and to the Codex
// backend of provider openai-codex (auto, default, flex and priority: Codex CLI's Fast is priority), never Azure; text verbosity, the
// reasoning summary and the session id (prompt_cache_key) only to the Responses and Codex APIs; parallel tool calls to the OpenAI APIs
// (parallel_tool_calls) and Anthropic (disable_parallel_tool_use); strict tools to the OpenAI APIs. APIs PiSharp cannot patch yet
// (Mistral, Bedrock, Vertex and the rest) take thinking, max tokens, tools, images and the timeout only. An option the user set that the
// model does not take is never dropped: the plan lists it as refused with the reason, and the engine refuses the run with that message.
// The faux models accept the sampling options offline and do not apply them (a note says so); echo-thinking refuses temperature and top P
// while thinking, as Anthropic does, so the refusal can be seen offline.
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

    /// <summary>Every option, in the Playground's order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Thinking, MaxTokens, Temperature, TopP, TopK, Stop, Seed, PresencePenalty, FrequencyPenalty, ResponseFormat, ToolChoice, ServiceTier, Headers, ProviderOptions, Images, Tools,
         ParallelToolCalls, StrictTools, TextVerbosity, ReasoningSummary, SessionId, TimeoutMs];

    /// <summary>The APIs whose request body a run patches.</summary>
    public static IReadOnlyList<string> PatchedApis { get; } = [CompletionsApi, ResponsesApi, AzureResponsesApi, CodexApi, AnthropicApi, GoogleApi];

    /// <summary>The service tiers the Codex backend takes (Codex CLI sends its Fast mode as <c>priority</c>, and Flex as <c>flex</c>; auto and
    /// default are the Responses API's own).</summary>
    public static IReadOnlyList<string> CodexServiceTiers { get; } = ["auto", "default", "flex", "priority"];

    internal const string FauxApi = "faux", CompletionsApi = "openai-completions", ResponsesApi = "openai-responses", AzureResponsesApi = "azure-openai-responses",
        CodexApi = "openai-codex-responses", AnthropicApi = "anthropic-messages", GoogleApi = "google-generative-ai", BedrockApi = "bedrock-converse-stream";

    /// <summary>Why a spec's options are only for the Responses and Codex APIs.</summary>
    private const string ResponsesOnly = "only the OpenAI Responses and Codex APIs take it";

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
            Options = [.. All.Select(option =>
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
        Use(MaxTokens, spec.MaxTokens is not null, spec.MaxTokens > cap ? $"at most {cap:N0} output tokens" : null);
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
        Use(ToolChoice, spec.ToolChoice is not null);
        var tiers = Tiers(model, catalog);
        Use(ServiceTier, spec.ServiceTier is not null, spec.ServiceTier is { } tier && tiers.Count > 0 && !tiers.Contains(tier)
            ? $"the {(model.Api == CodexApi ? "Codex backend" : model.Api + " API")} takes {string.Join(", ", tiers)}, not {tier}" : null);
        Use(Headers, spec.Headers is not null);
        Use(ProviderOptions, spec.ProviderOptions is not null);
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

        JsonObject? patch = null;
        string? path = null;
        var headers = spec.Headers;
        if (refused.Count == 0 && PatchedApis.Contains(model.Api))
        {
            patch = Patch(spec, model.Api);
            if (spec.ProviderOptions is { } extra)
            {
                var conflicts = new List<string>();
                Merge(patch, (JsonObject)extra.DeepClone(), "", conflicts);
                foreach (var conflict in conflicts)
                    refused.Add(new RunOptionSupport(ProviderOptions, false, $"providerOptions.{conflict} conflicts with an option the run sets; set it in one place"));
            }
            if (patch.Count == 0 && strict is not { Count: > 0 }) patch = null;
            else path = PatchPathOf(model.Api);
            // Codex keys its session by headers as well as by prompt_cache_key (buildSSEHeaders): a run's session id sets both.
            if (model.Api == CodexApi && spec.SessionId is { } session)
                headers = new Dictionary<string, string>(spec.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
                {
                    ["session-id"] = session,
                    ["x-client-request-id"] = session,
                };
        }
        if (model.Api == FauxApi && applied.Any(option => option is Temperature or TopP or TopK or Stop or Seed or PresencePenalty or FrequencyPenalty or ResponseFormat or ToolChoice
            or ParallelToolCalls or StrictTools or TextVerbosity or ReasoningSummary or TimeoutMs))
            notes.Add("The faux model runs offline: it accepts sampling options and does not apply them.");
        if (spec.MaxTokens is not null && model.Api != FauxApi)
            notes.Add("A max tokens setting sends the request without prompt caching (PiSharp's capped route).");
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
            Applied = applied,
            Refused = refused,
            Notes = notes,
        };
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
                return api == FauxApi ? (true, "the faux model ignores it") : (true, null);
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

    private static JsonObject Patch(RunSpec spec, string api)
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
    public static IReadOnlyList<string> ApplyToBody(JsonObject body, JsonObject? patch, IReadOnlyCollection<string>? strictTools, string? api)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (patch is not null) Merge(body, (JsonObject)patch.DeepClone(), "", null);
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
