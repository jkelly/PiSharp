// Decision 0011 (run W4, R8): what a preset is. A preset is a saved bundle of a model, its parameters, the prompts and the tools, captured
// from the Playground, Compare or the Tool Bench (its kind) and loaded into any of the three. It is stored in webagent.db (PresetStore) and
// exported as JSON in exactly this shape (camelCase, SettingsStore.JsonOptions). It never holds a secret: the account is an id, a header or
// provider option that could carry a credential is refused (PresetRules), and a field PiSharp does not know (an "apiKey" in a hand-written
// file) is dropped on read. Each part can be left out when saving; loading a preset changes only the parts it has.
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Bench;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Presets;

/// <summary>The pages a preset is captured from and loaded into.</summary>
public static class PresetPages
{
    /// <summary>The Playground (<c>/playground</c>).</summary>
    public const string Playground = "playground";

    /// <summary>Compare (<c>/compare</c>, request mode).</summary>
    public const string Compare = "compare";

    /// <summary>The Tool Bench (<c>/bench</c>).</summary>
    public const string Bench = "bench";

    /// <summary>Every page, in the sidebar's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Playground, Compare, Bench];

    /// <summary>Whether <paramref name="page"/> is one of <see cref="All"/>.</summary>
    public static bool Known(string? page) => page is Playground or Compare or Bench;

    /// <summary>The page's name: Playground, Compare or Tool Bench.</summary>
    public static string Title(string page) => page switch { Compare => "Compare", Bench => "Tool Bench", _ => "Playground" };

    /// <summary>The page's Bootstrap icon.</summary>
    public static string Icon(string page) => page switch { Compare => "bi-layout-three-columns", Bench => "bi-tools", _ => "bi-sliders2" };

    /// <summary>The page's address, loading preset <paramref name="id"/>.</summary>
    public static string Url(string page, string id) => "/" + page + "?preset=" + Uri.EscapeDataString(id);
}

/// <summary>The parts of a preset, each of which can be left out when saving.</summary>
public static class PresetParts
{
    /// <summary>The model (Compare: the columns' models and overrides) and its account id.</summary>
    public const string Model = "model";

    /// <summary>The request options (thinking, max tokens, temperature, ...) and the Tool Bench's auto-respond rules.</summary>
    public const string Parameters = "parameters";

    /// <summary>The extra request headers (saved as typed; never one that could carry a credential).</summary>
    public const string Headers = "headers";

    /// <summary>The system prompt.</summary>
    public const string SystemPrompt = "systemPrompt";

    /// <summary>The messages (Compare: the shared prompt; Tool Bench: the user messages).</summary>
    public const string Messages = "messages";

    /// <summary>The tools, their mock results, the tool choice and parallel calls.</summary>
    public const string Tools = "tools";

    /// <summary>Every part, in the save modal's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Model, Parameters, SystemPrompt, Messages, Tools, Headers];

    /// <summary>The part's label in the save modal.</summary>
    public static string Label(string part) => part switch
    {
        Model => "Model",
        Parameters => "Parameters",
        Headers => "Request headers",
        SystemPrompt => "System prompt",
        Messages => "Messages",
        Tools => "Tools",
        _ => part,
    };
}

/// <summary>A preset's request options (any of them; an unset one is not saved).</summary>
public sealed record PresetOptions
{
    /// <summary>The thinking level, or null for off.</summary>
    public string? Thinking { get; init; }

    /// <summary>The output cap.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>The temperature.</summary>
    public double? Temperature { get; init; }

    /// <summary>Top P.</summary>
    public double? TopP { get; init; }

    /// <summary>Top K.</summary>
    public int? TopK { get; init; }

    /// <summary>The stop sequences.</summary>
    public IReadOnlyList<string>? Stop { get; init; }

    /// <summary>The seed.</summary>
    public long? Seed { get; init; }

    /// <summary>The presence penalty.</summary>
    public double? PresencePenalty { get; init; }

    /// <summary>The frequency penalty.</summary>
    public double? FrequencyPenalty { get; init; }

    /// <summary>The response format.</summary>
    public JsonObject? ResponseFormat { get; init; }

    /// <summary>The service tier.</summary>
    public string? ServiceTier { get; init; }

    /// <summary>Provider options (never one that names a credential).</summary>
    public JsonObject? ProviderOptions { get; init; }

    /// <summary>Whether no option is set.</summary>
    [JsonIgnore]
    public bool Empty => Thinking is null && MaxTokens is null && Temperature is null && TopP is null && TopK is null && Stop is not { Count: > 0 }
        && Seed is null && PresencePenalty is null && FrequencyPenalty is null && ResponseFormat is null && ServiceTier is null && ProviderOptions is not { Count: > 0 };

    /// <summary>The options set, as (name, text) pairs for the cards and the modals, in the Playground's order.</summary>
    public IReadOnlyList<(string Name, string Text)> Entries()
    {
        var entries = new List<(string, string)>();
        if (Thinking is { } thinking) entries.Add(("thinking", thinking));
        if (MaxTokens is { } max) entries.Add(("maxTokens", max.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)));
        if (Temperature is { } temperature) entries.Add(("temperature", Number(temperature)));
        if (TopP is { } topP) entries.Add(("topP", Number(topP)));
        if (TopK is { } topK) entries.Add(("topK", topK.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (Stop is { Count: > 0 } stop) entries.Add(("stop", stop.Count == 1 ? "1 sequence" : $"{stop.Count} sequences"));
        if (Seed is { } seed) entries.Add(("seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (PresencePenalty is { } presence) entries.Add(("presencePenalty", Number(presence)));
        if (FrequencyPenalty is { } frequency) entries.Add(("frequencyPenalty", Number(frequency)));
        if (ResponseFormat is { } format) entries.Add(("responseFormat", format["type"] is JsonValue type && type.TryGetValue<string>(out var kind) ? kind : "set"));
        if (ServiceTier is { } tier) entries.Add(("serviceTier", tier));
        if (ProviderOptions is { Count: > 0 } extra) entries.Add(("providerOptions", string.Join(", ", extra.Select(pair => pair.Key))));
        return entries;
    }

    /// <summary>A number as the pages show it (invariant, no trailing zeros).</summary>
    public static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A saved preset.</summary>
public sealed record Preset
{
    /// <summary>The longest name.</summary>
    public const int MaxName = 80;

    /// <summary>The longest description.</summary>
    public const int MaxDescription = 2000;

    /// <summary>The most tags.</summary>
    public const int MaxTags = 20;

    /// <summary>The longest tag.</summary>
    public const int MaxTag = 40;

    /// <summary>The id: lower-case letters, digits and '-', made from the name when it is first saved and kept on rename.</summary>
    public string Id { get; init; } = "";

    /// <summary>The name.</summary>
    public string Name { get; init; } = "";

    /// <summary>What it is for, or null.</summary>
    public string? Description { get; init; }

    /// <summary>The tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Whether it is pinned to the top of Presets.</summary>
    public bool Pinned { get; init; }

    /// <summary>The page it was captured from (<see cref="PresetPages"/>).</summary>
    public string Kind { get; init; } = PresetPages.Playground;

    /// <summary>The provider id (Playground and Tool Bench presets), or null when the model was left out.</summary>
    public string? Provider { get; init; }

    /// <summary>The model id, or null when the model was left out.</summary>
    public string? Model { get; init; }

    /// <summary>The account id (never a key), or null for the provider's active account.</summary>
    public string? Account { get; init; }

    /// <summary>A Compare preset's columns (models, accounts and overrides), or null.</summary>
    public IReadOnlyList<CompareColumnDraft>? Columns { get; init; }

    /// <summary>The request options, or null when the parameters were left out.</summary>
    public PresetOptions? Options { get; init; }

    /// <summary>The extra request headers, or null.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>The system prompt, or null.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The messages (Compare: one user message, the shared prompt; Tool Bench: user messages), or null.</summary>
    public IReadOnlyList<RunMessage>? Messages { get; init; }

    /// <summary>The tools (definitions, switched on or off, with their mock results), or null.</summary>
    public IReadOnlyList<BenchTool>? Tools { get; init; }

    /// <summary>The tool choice (auto, none, required, any or a tool's name), or null.</summary>
    public string? ToolChoice { get; init; }

    /// <summary>Whether tool calls run in parallel (Playground's execution mode, the Tool Bench's switch), or null.</summary>
    public bool? Parallel { get; init; }

    /// <summary>The Tool Bench's auto-respond rules, or null.</summary>
    public BenchRules? Rules { get; init; }

    /// <summary>How many times it was loaded.</summary>
    public int Uses { get; init; }

    /// <summary>When it was last loaded, or null.</summary>
    public DateTimeOffset? LastUsed { get; init; }

    /// <summary>The pages it was loaded into.</summary>
    public IReadOnlyList<string> UsedIn { get; init; } = [];

    /// <summary>When it was first saved.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset Updated { get; init; }

    /// <summary>The parts it has, in <see cref="PresetParts.All"/>'s order.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Parts => [.. PresetParts.All.Where(Has)];

    /// <summary>Whether it has <paramref name="part"/>.</summary>
    public bool Has(string part) => part switch
    {
        PresetParts.Model => Columns is { Count: > 0 } || !string.IsNullOrEmpty(Provider) && !string.IsNullOrEmpty(Model),
        PresetParts.Parameters => Options is { Empty: false } || Rules is not null,
        PresetParts.Headers => Headers is { Count: > 0 },
        PresetParts.SystemPrompt => !string.IsNullOrEmpty(SystemPrompt),
        PresetParts.Messages => Messages is { Count: > 0 },
        PresetParts.Tools => Tools is { Count: > 0 } || ToolChoice is not null || Parallel is not null,
        _ => false,
    };

    /// <summary>The same preset without <paramref name="part"/>.</summary>
    public Preset Without(string part) => part switch
    {
        PresetParts.Model => this with { Provider = null, Model = null, Account = null, Columns = null },
        PresetParts.Parameters => this with { Options = null, Rules = null },
        PresetParts.Headers => this with { Headers = null },
        PresetParts.SystemPrompt => this with { SystemPrompt = null },
        PresetParts.Messages => this with { Messages = null },
        PresetParts.Tools => this with { Tools = null, ToolChoice = null, Parallel = null },
        _ => this,
    };

    /// <summary>The models it names (provider, model): the columns' for Compare, else its own; empty when the model was left out.</summary>
    [JsonIgnore]
    public IReadOnlyList<(string Provider, string Model)> Models => Columns is { Count: > 0 } columns
        ? [.. columns.Select(column => (column.Provider, column.Model))]
        : !string.IsNullOrEmpty(Provider) && !string.IsNullOrEmpty(Model) ? [(Provider, Model)] : [];

    /// <summary>The first model as <c>provider/model</c>, or null.</summary>
    [JsonIgnore]
    public string? ModelId => Models.Count > 0 ? Models[0].Provider + "/" + Models[0].Model : null;

    /// <summary>The preset as a file holds it: without what only this machine knows (uses, when it was last used, where).</summary>
    public Preset Portable() => this with { Uses = 0, LastUsed = null, UsedIn = [] };
}
