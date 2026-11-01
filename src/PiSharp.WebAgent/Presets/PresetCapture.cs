// Decision 0011 (run W4, R8): a preset from what a page posts when the user saves one. presets-host.js reads the page with the page's own
// readers and posts it as "capture":
//   playground  {spec: RunSpec (collectSpec), tools: [{name, description, parameters, enabled, strict?, mock?, mockError?}], parallel: bool}
//               (W5 L4: every option the spec carries is kept, and a tool's strict flag)
//   compare     {draft: CompareDraft (prompt, systemPrompt, thinking, maxTokens, temperature, columns)}
//   bench       {setup: BenchSetup (readSetup), messages: [user message texts]}
//   presets     {provider, model, systemPrompt?}  (the Presets page's New preset: a model and a system prompt)
// Every value is read through the page's own types (RunSpec.Parse, CompareDraft.Parse, BenchSetup), so a field they do not know (an
// apiKey) is never kept, and the result is checked by PresetRules like any preset.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Bench;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Presets;

/// <summary>Presets from the pages' captures.</summary>
public static class PresetCapture
{
    /// <summary>The page name of the Presets page's own New preset.</summary>
    public const string PresetsPage = "presets";

    /// <summary>The preset <paramref name="capture"/> of <paramref name="page"/> describes, with every part and a placeholder name (not yet
    /// checked: <see cref="PresetRules.Normalize"/> does that once the name is known).</summary>
    /// <exception cref="ArgumentException">The capture is not one of the page's.</exception>
    public static Preset From(string? page, JsonNode? capture)
    {
        if (capture is not JsonObject json) throw new ArgumentException("Send what the page has as a JSON object (capture).");
        return page switch
        {
            PresetPages.Playground => Playground(json),
            PresetPages.Compare => Compare(json),
            PresetPages.Bench => Bench(json),
            PresetsPage => Blank(json),
            _ => throw new ArgumentException("Presets are saved from the Playground, Compare or the Tool Bench."),
        };
    }

    private static Preset Playground(JsonObject json)
    {
        if (json["spec"] is not JsonObject specJson) throw new ArgumentException("Send the Playground's request (capture.spec).");
        var copy = (JsonObject)specJson.DeepClone();
        copy.Remove("tools");
        var spec = RunSpec.Parse(copy);
        var tools = new List<BenchTool>();
        if (json["tools"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is not JsonObject tool) throw new ArgumentException("capture.tools is a list of tools.");
                var mock = tool["mock"] is JsonValue mockValue && mockValue.TryGetValue<string>(out var mockText) ? mockText : null;
                tools.Add(new BenchTool
                {
                    Name = Str(tool, "name") ?? "",
                    Description = Str(tool, "description"),
                    Parameters = tool["parameters"] as JsonObject is { } parameters ? (JsonObject)parameters.DeepClone() : null,
                    Enabled = tool["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var on) || on,
                    Strict = tool["strict"] is JsonValue strict && strict.TryGetValue<bool>(out var isStrict) && isStrict,
                    Mocks = mock is null ? [] : [new BenchMock { Name = "Mock 1", Text = mock, IsError = tool["mockError"] is JsonValue error && error.TryGetValue<bool>(out var isError) && isError }],
                });
            }
        }
        else if (json["tools"] is not null) throw new ArgumentException("capture.tools is a list of tools.");
        return new Preset
        {
            Name = "Playground preset",
            Kind = PresetPages.Playground,
            Provider = spec.Provider,
            Model = spec.Model,
            Account = spec.Account,
            Options = new PresetOptions
            {
                Thinking = spec.Thinking,
                MaxTokens = spec.MaxTokens,
                Temperature = spec.Temperature,
                TopP = spec.TopP,
                TopK = spec.TopK,
                Stop = spec.Stop,
                Seed = spec.Seed,
                PresencePenalty = spec.PresencePenalty,
                FrequencyPenalty = spec.FrequencyPenalty,
                ResponseFormat = spec.ResponseFormat,
                ServiceTier = spec.ServiceTier,
                ProviderOptions = spec.ProviderOptions,
                TextVerbosity = spec.TextVerbosity,
                ReasoningSummary = spec.ReasoningSummary,
                SessionId = spec.SessionId,
                TimeoutMs = spec.TimeoutMs,
                ParallelToolCalls = spec.ParallelToolCalls,
                CacheRetention = spec.CacheRetention,
                MaxRetries = spec.MaxRetries,
                MaxRetryDelayMs = spec.MaxRetryDelayMs,
                ThinkingBudgets = spec.ThinkingBudgets is { Count: > 0 } budgets ? budgets : null,
                AzureApiVersion = spec.AzureApiVersion,
            },
            Headers = spec.Headers,
            SystemPrompt = spec.SystemPrompt,
            Messages = spec.Messages is { Count: > 0 } messages ? messages : null,
            Tools = tools.Count > 0 ? tools : null,
            ToolChoice = spec.ToolChoice,
            // The execution mode goes with the tools (the page always has one): a preset without tools keeps none.
            Parallel = tools.Count > 0 && json["parallel"] is JsonValue parallel && parallel.TryGetValue<bool>(out var value) ? value : null,
        };
    }

    private static Preset Compare(JsonObject json)
    {
        var draft = CompareDraft.Parse(json["draft"] ?? throw new ArgumentException("Send Compare's request (capture.draft)."));
        return new Preset
        {
            Name = "Compare preset",
            Kind = PresetPages.Compare,
            Columns = draft.Columns.Count > 0 ? draft.Columns : null,
            Options = new PresetOptions { Thinking = draft.Thinking, MaxTokens = draft.MaxTokens, Temperature = draft.Temperature },
            SystemPrompt = draft.SystemPrompt,
            Messages = string.IsNullOrWhiteSpace(draft.Prompt) ? null : [new RunMessage { Role = "user", Text = draft.Prompt }],
        };
    }

    private static Preset Bench(JsonObject json)
    {
        if (json["setup"] is not JsonObject setupJson) throw new ArgumentException("Send the Tool Bench's setup (capture.setup).");
        BenchSetup setup;
        try
        {
            setup = setupJson.Deserialize<BenchSetup>(SettingsStore.JsonOptions) ?? throw new ArgumentException("Send the Tool Bench's setup (capture.setup).");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The Tool Bench's setup is not valid: " + (error.Path is { Length: > 0 } path ? path : error.Message));
        }
        var messages = new List<RunMessage>();
        if (json["messages"] is JsonArray list)
        {
            foreach (var item in list)
                if (item is JsonValue value && value.TryGetValue<string>(out var text)) { if (!string.IsNullOrWhiteSpace(text)) messages.Add(new RunMessage { Role = "user", Text = text }); }
                else throw new ArgumentException("capture.messages is a list of strings.");
        }
        return new Preset
        {
            Name = "Tool Bench preset",
            Kind = PresetPages.Bench,
            Provider = string.IsNullOrWhiteSpace(setup.Provider) ? null : setup.Provider,
            Model = string.IsNullOrWhiteSpace(setup.Model) ? null : setup.Model,
            Account = setup.Account,
            Options = new PresetOptions { Thinking = setup.Thinking },
            SystemPrompt = setup.SystemPrompt,
            Messages = messages.Count > 0 ? messages : null,
            Tools = setup.Tools is { Count: > 0 } tools ? tools : null,
            ToolChoice = setup.Tools is { Count: > 0 } ? setup.ToolChoice : null,
            Parallel = setup.Tools is { Count: > 0 } ? setup.Parallel : null,
            Rules = setup.Rules ?? new BenchRules(),
        };
    }

    private static Preset Blank(JsonObject json)
    {
        var provider = Str(json, "provider");
        var model = Str(json, "model");
        if (provider is null || model is null) throw new ArgumentException("Choose a provider and a model.");
        var system = json["systemPrompt"] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
        return new Preset { Name = "Preset", Kind = PresetPages.Playground, Provider = provider, Model = model, SystemPrompt = system };
    }

    private static string? Str(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
