// Decision 0011 (run W4, R0; brief rule 4): which request options a model and its API take, and the request a run sends. PiSharp's
// ChatRequest carries the transcript, the thinking level and the output cap only, so the rest of a run's options travel as a JSON patch
// that RunCaptureHandler merges into the provider's request body on its way out (objects merge, anything else replaces), and as extra
// headers. Only options the API has are sent: temperature is never sent to a model that rejects it (OpenAI reasoning models; Anthropic
// while thinking), service tier only to the OpenAI Responses API of provider openai (never Azure, and not Codex, whose adapter PiSharp
// does not patch yet), and APIs PiSharp cannot patch yet (Codex, Mistral, Bedrock, Vertex and the rest) take thinking, max tokens, tools
// and images only. An option the user set that the model does not take is never dropped: the plan lists it as refused with the reason, and
// the engine refuses the run with that message. The faux models accept the sampling options offline and do not apply them (a note says
// so); echo-thinking refuses temperature and top P while thinking, as Anthropic does, so the refusal can be seen offline.
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
        ToolChoice = "toolChoice", ServiceTier = "serviceTier", Headers = "headers", ProviderOptions = "providerOptions", Images = "images", Tools = "tools";

    /// <summary>Every option, in the Playground's order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Thinking, MaxTokens, Temperature, TopP, TopK, Stop, Seed, PresencePenalty, FrequencyPenalty, ResponseFormat, ToolChoice, ServiceTier, Headers, ProviderOptions, Images, Tools];

    /// <summary>The APIs whose request body a run patches.</summary>
    public static IReadOnlyList<string> PatchedApis { get; } = [CompletionsApi, ResponsesApi, AzureResponsesApi, AnthropicApi, GoogleApi];

    internal const string FauxApi = "faux", CompletionsApi = "openai-completions", ResponsesApi = "openai-responses", AzureResponsesApi = "azure-openai-responses",
        AnthropicApi = "anthropic-messages", GoogleApi = "google-generative-ai", BedrockApi = "bedrock-converse-stream";

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
        };
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
        Use(ServiceTier, spec.ServiceTier is not null);
        Use(Headers, spec.Headers is not null);
        Use(ProviderOptions, spec.ProviderOptions is not null);
        Use(Images, spec.Messages.Any(message => message.Images is { Count: > 0 }));
        Use(Tools, spec.Tools is { Count: > 0 });

        JsonObject? patch = null;
        string? path = null;
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
            if (patch.Count == 0) patch = null;
            else path = PatchPathOf(model.Api);
        }
        if (model.Api == FauxApi && applied.Any(option => option is Temperature or TopP or TopK or Stop or Seed or PresencePenalty or FrequencyPenalty or ResponseFormat or ToolChoice))
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
            Headers = refused.Count == 0 ? spec.Headers : null,
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
        }
        if (api == FauxApi)
        {
            if (option is ServiceTier or ProviderOptions) return (false, "the offline faux provider sends no HTTP request");
            if (thinking && option is Temperature or TopP) return (false, "not while this model thinks");
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
                    ServiceTier => (false, "only the OpenAI Responses API takes a service tier"),
                    Temperature or TopP or PresencePenalty or FrequencyPenalty when reasoningOpenAI => (false, "OpenAI reasoning models reject it"),
                    _ => (true, null),
                };
            case ResponsesApi or AzureResponsesApi:
                return option switch
                {
                    Temperature or TopP when model.Reasoning => (false, "reasoning models on the Responses API reject it"),
                    TopK or Seed or PresencePenalty or FrequencyPenalty or Stop => (false, "the Responses API has no such parameter"),
                    ServiceTier when api == AzureResponsesApi => (false, "Azure OpenAI takes no service tier"),
                    ServiceTier when model.Provider != "openai" => (false, "only provider OpenAI takes a service tier"),
                    _ => (true, null),
                };
            case AnthropicApi:
                return option switch
                {
                    Temperature or TopP or TopK when thinking => (false, "Anthropic rejects it while the model thinks"),
                    ToolChoice when thinking => (true, "only auto or none while the model thinks"),
                    Seed or PresencePenalty or FrequencyPenalty or ResponseFormat => (false, "the Anthropic Messages API has no such parameter"),
                    ServiceTier => (false, "only the OpenAI Responses API takes a service tier"),
                    _ => (true, null),
                };
            case GoogleApi:
                return option == ServiceTier ? (false, "only the OpenAI Responses API takes a service tier") : (true, null);
        }
        return (false, $"PiSharp does not send it on the {api} API yet");
    }

    private static string? Format(JsonObject? format) => format?["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : null;

    private static string PatchPathOf(string api) => api switch
    {
        CompletionsApi => "/chat/completions",
        ResponsesApi or AzureResponsesApi => "/responses",
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
                break;
            case ResponsesApi or AzureResponsesApi:
                Set(patch, "temperature", spec.Temperature);
                Set(patch, "top_p", spec.TopP);
                if (format is not null)
                {
                    var described = format == "json_schema" ? Schema(schemaName, schema, strict) : new JsonObject();
                    described.Insert(0, "type", format);
                    patch["text"] = new JsonObject { ["format"] = described };
                }
                if (spec.ToolChoice is not null) patch["tool_choice"] = tool is null ? JsonValue.Create(spec.ToolChoice) : new JsonObject { ["type"] = "function", ["name"] = tool };
                if (spec.ServiceTier is { } tier) patch["service_tier"] = tier;
                break;
            case AnthropicApi:
                Set(patch, "temperature", spec.Temperature);
                Set(patch, "top_p", spec.TopP);
                if (spec.TopK is { } topK) patch["top_k"] = topK;
                if (spec.Stop is { } sequences) patch["stop_sequences"] = Strings(sequences);
                if (spec.ToolChoice is not null)
                    patch["tool_choice"] = spec.ToolChoice switch
                    {
                        "auto" => new JsonObject { ["type"] = "auto" },
                        "none" => new JsonObject { ["type"] = "none" },
                        "required" => new JsonObject { ["type"] = "any" },
                        _ => new JsonObject { ["type"] = "tool", ["name"] = tool },
                    };
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
                    messages.Add(JsonNode.Parse(PiWireJson.WriteMessage(assistant).ToString())!.AsObject());
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
