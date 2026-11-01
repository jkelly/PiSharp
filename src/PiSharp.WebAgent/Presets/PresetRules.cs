// Decision 0011 (run W4, R8): what a preset may hold, checked on every save and import. The checks are the pages' own: a Playground preset's
// options, headers, messages and tools go through RunSpec.Normalize (ranges, roles, tool names, credential headers, provider options that
// would replace the request), a tool through BenchSchema (names, JSON Schema, mocks), a Tool Bench preset through BenchSetup.Normalize and a
// Compare preset's columns through CompareDraft. On top of that, a preset never holds a secret: no provider option, at any depth, whose name
// looks like a credential, and no header or provider-option value that looks like a key or a bearer token (PresetRules.LooksSecret).
// W5 L4: a Playground preset keeps every option the Playground sends (text verbosity, the reasoning summary, the session id, the timeout,
// parallel tool calls, cache retention, retries, the retry delay, thinking budgets, the Azure API version) and a tool's strict flag, each
// through the same RunSpec checks.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.Bench;
using PiSharp.WebAgent.Compare;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Presets;

/// <summary>The checks of a preset.</summary>
public static partial class PresetRules
{
    /// <summary>The largest preset, as JSON (images in messages count).</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>The options a Compare preset keeps (Compare sends only these).</summary>
    public static IReadOnlyList<string> CompareOptions { get; } = ["thinking", "maxTokens", "temperature"];

    /// <summary>The same preset, trimmed and checked.</summary>
    /// <exception cref="ArgumentException">Something is not allowed; the message says what.</exception>
    public static Preset Normalize(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var name = (preset.Name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Give the preset a name.");
        if (name.Length > Preset.MaxName) throw new ArgumentException($"A preset's name is at most {Preset.MaxName} characters.");
        var description = string.IsNullOrWhiteSpace(preset.Description) ? null : preset.Description.Trim();
        if (description is { Length: > Preset.MaxDescription }) throw new ArgumentException($"A preset's description is at most {Preset.MaxDescription:N0} characters.");
        var kind = (preset.Kind ?? "").Trim();
        if (!PresetPages.Known(kind)) throw new ArgumentException($"A preset is a playground, compare or bench preset, not \"{preset.Kind}\".");
        var id = (preset.Id ?? "").Trim();
        if (id.Length > 0 && !IdPattern().IsMatch(id)) throw new ArgumentException($"\"{id}\" is not a preset id (lower-case letters, digits and '-', at most 64).");
        if (id.Length > 0) id = PresetStore.Usable(id);

        var normalized = preset with
        {
            Id = id,
            Name = name,
            Description = description,
            Kind = kind,
            Tags = Tags(preset.Tags),
            Provider = Text(preset.Provider),
            Model = Text(preset.Model),
            Account = Text(preset.Account),
            SystemPrompt = string.IsNullOrEmpty(preset.SystemPrompt) ? null : preset.SystemPrompt,
            Options = preset.Options is { Empty: false } options ? Options(options) : null,
            Headers = preset.Headers is { Count: > 0 } ? preset.Headers : null,
            Messages = preset.Messages is { Count: > 0 } ? preset.Messages : null,
            Tools = preset.Tools is { Count: > 0 } ? preset.Tools : null,
            ToolChoice = Text(preset.ToolChoice),
            Columns = preset.Columns is { Count: > 0 } ? preset.Columns : null,
            Uses = Math.Max(0, preset.Uses),
            UsedIn = [.. (preset.UsedIn ?? []).Where(PresetPages.Known).Distinct(StringComparer.Ordinal)],
        };
        if (normalized.ToolChoice == "auto") normalized = normalized with { ToolChoice = null };
        if (normalized.Account is { Length: > 200 }) throw new ArgumentException("An account id is at most 200 characters.");
        if (normalized.Provider is null != normalized.Model is null) throw new ArgumentException("A preset's model names both the provider and the model.");
        normalized = kind switch
        {
            PresetPages.Compare => CheckCompare(normalized),
            PresetPages.Bench => CheckBench(normalized),
            _ => CheckPlayground(normalized),
        };
        CheckSecrets(normalized);
        if (normalized.Parts.Count == 0) throw new ArgumentException("A preset keeps at least one part: the model, parameters, system prompt, messages or tools.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, SettingsStore.JsonOptions).Length;
        if (bytes > MaxBytes) throw new ArgumentException($"A preset is at most {MaxBytes / (1024 * 1024)} MB of JSON; this one is {bytes / (1024.0 * 1024):0.#} MB (images in the messages count).");
        return normalized;
    }

    /// <summary>Whether a header or option value looks like a credential: a bearer or basic authorization value, or a well-known key prefix.</summary>
    public static bool LooksSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        return text.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("basic ", StringComparison.OrdinalIgnoreCase)
            || KeyPattern().IsMatch(text);
    }

    /// <summary>The tags, trimmed, without duplicates (ignoring case) and checked.</summary>
    public static IReadOnlyList<string> Tags(IEnumerable<string>? tags)
    {
        var list = new List<string>();
        foreach (var raw in tags ?? [])
        {
            var tag = (raw ?? "").Trim();
            if (tag.Length == 0 || list.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            if (tag.Length > Preset.MaxTag) throw new ArgumentException($"A tag is at most {Preset.MaxTag} characters.");
            if (tag.Contains(',', StringComparison.Ordinal)) throw new ArgumentException("A tag has no comma.");
            list.Add(tag);
        }
        if (list.Count > Preset.MaxTags) throw new ArgumentException($"A preset has at most {Preset.MaxTags} tags.");
        return list;
    }

    /// <summary>Tags from the modal's comma-separated text.</summary>
    public static IReadOnlyList<string> SplitTags(string? text) => Tags((text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PresetOptions Options(PresetOptions options) => options with
    {
        Thinking = string.IsNullOrWhiteSpace(options.Thinking) || options.Thinking.Trim() == "off" ? null : options.Thinking.Trim(),
        ServiceTier = Text(options.ServiceTier),
        Stop = options.Stop is { Count: > 0 } stop ? stop : null,
        ProviderOptions = options.ProviderOptions is { Count: > 0 } extra ? extra : null,
    };

    private static Preset CheckPlayground(Preset preset)
    {
        if (preset.Columns is not null) throw new ArgumentException("Only a Compare preset has columns.");
        if (preset.Rules is not null) throw new ArgumentException("Only a Tool Bench preset has auto-respond rules.");
        var tools = NormalizeTools(preset.Tools);
        var probe = Probe(preset, tools);
        if (preset.ToolChoice is "any") throw new ArgumentException("The tool choice \"any\" is the Tool Bench's; a Playground preset uses required.");
        return preset with
        {
            Tools = tools,
            Options = preset.Options is null ? null : OptionsOf(probe) with { ParallelToolCalls = preset.Options.ParallelToolCalls },
            Headers = probe.Headers,
        };
    }

    private static Preset CheckBench(Preset preset)
    {
        if (preset.Columns is not null) throw new ArgumentException("Only a Compare preset has columns.");
        if (preset.Headers is not null) throw new ArgumentException("The Tool Bench sends no extra headers.");
        if (preset.Options is { } options && options with { Thinking = null } is { Empty: false })
            throw new ArgumentException("A Tool Bench preset keeps thinking only of the request options.");
        foreach (var message in preset.Messages ?? [])
            if (message is not { Role: "user" } || string.IsNullOrWhiteSpace(message.Text) || message.Images is { Count: > 0 } || message.ToolCalls is { Count: > 0 })
                throw new ArgumentException("A Tool Bench preset's messages are user messages with text.");
        var setup = new BenchSetup
        {
            Provider = preset.Provider ?? "",
            Model = preset.Model ?? "",
            Account = preset.Account,
            Thinking = preset.Options?.Thinking,
            SystemPrompt = preset.SystemPrompt,
            Tools = preset.Tools ?? [],
            ToolChoice = preset.ToolChoice,
            Parallel = preset.Parallel ?? true,
            Rules = preset.Rules ?? new BenchRules(),
        }.Normalize();
        return preset with
        {
            Options = setup.Thinking is null ? null : new PresetOptions { Thinking = setup.Thinking },
            Tools = setup.Tools.Count == 0 ? null : setup.Tools,
            ToolChoice = setup.Tools.Count == 0 || setup.ToolChoice is null or "auto" ? null : setup.ToolChoice,
            Parallel = setup.Tools.Count == 0 ? null : preset.Parallel,
            Rules = preset.Rules is null ? null : setup.Rules,
        };
    }

    private static Preset CheckCompare(Preset preset)
    {
        if (preset.Provider is not null) throw new ArgumentException("A Compare preset names its models in its columns.");
        if (preset.Tools is not null || preset.ToolChoice is not null || preset.Parallel is not null) throw new ArgumentException("Compare sends no tools.");
        if (preset.Headers is not null) throw new ArgumentException("Compare sends no extra headers.");
        if (preset.Rules is not null) throw new ArgumentException("Only a Tool Bench preset has auto-respond rules.");
        if (preset.Options is { } options && options with { Thinking = null, MaxTokens = null, Temperature = null } is { Empty: false })
            throw new ArgumentException("A Compare preset keeps thinking, max tokens and temperature only of the request options.");
        if (preset.Messages is { } messages && (messages.Count != 1 || messages[0] is not { Role: "user" } prompt || string.IsNullOrEmpty(prompt.Text) || prompt.Images is { Count: > 0 }))
            throw new ArgumentException("A Compare preset's prompt is one user message with text.");
        IReadOnlyList<CompareColumnDraft>? columns = null;
        if (preset.Columns is { } list)
        {
            if (list.Count > CompareDraft.MaxColumns) throw new ArgumentException($"Compare has at most {CompareDraft.MaxColumns} columns.");
            columns = [.. list.Select((column, index) =>
            {
                var trimmed = (column ?? throw new ArgumentException($"Column {index + 1} is empty.")).Trimmed();
                if (trimmed.Provider.Length == 0 || trimmed.Model.Length == 0) throw new ArgumentException($"Column {index + 1} names a provider and a model.");
                if (trimmed.Thinking is { } level && !RunSpec.ThinkingLevels.Contains(level)) throw new ArgumentException($"Column {index + 1}: thinking is {string.Join(", ", RunSpec.ThinkingLevels)}, not \"{level}\".");
                if (trimmed.Temperature is { } temperature && (double.IsNaN(temperature) || temperature < 0 || temperature > 2)) throw new ArgumentException($"Column {index + 1}: temperature is 0 to 2.");
                if (trimmed.Account is { Length: > 200 }) throw new ArgumentException("An account id is at most 200 characters.");
                return trimmed;
            })];
        }
        var probe = Probe(preset with { Messages = null }, null);
        return preset with { Columns = columns, Options = preset.Options is null ? null : OptionsOf(probe) };
    }

    private static IReadOnlyList<BenchTool>? NormalizeTools(IReadOnlyList<BenchTool>? tools)
    {
        if (tools is null) return null;
        if (tools.Count > RunSpec.MaxTools) throw new ArgumentException($"A preset has at most {RunSpec.MaxTools} tools.");
        var list = tools.Select(tool => BenchSchema.NormalizeTool(tool ?? throw new ArgumentException("A tool is empty."))).ToList();
        var duplicate = list.GroupBy(tool => tool.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"There are two tools named {duplicate.Key}.");
        return list;
    }

    // RunSpec's own checks of the options, headers, messages and tools: a probe spec with a placeholder model (and a placeholder user
    // message when the preset has none to answer), normalized.
    private static RunSpec Probe(Preset preset, IReadOnlyList<BenchTool>? tools)
    {
        var options = preset.Options ?? new PresetOptions();
        var messages = (preset.Messages ?? []).ToList();
        if (!messages.Any(message => message?.Role is "user" or "toolResult")) messages.Add(new RunMessage { Role = "user", Text = "." });
        var choice = preset.ToolChoice is "any" ? "required" : preset.ToolChoice;
        var enabled = tools?.Where(tool => tool.Enabled).Select(tool => new RunTool(tool.Name, tool.Description, tool.Parameters, Strict: tool.Strict ? true : null)).ToList();
        if (choice is not (null or "auto" or "none" or "required") && tools is not null && tools.Any(tool => tool.Name == choice) && enabled?.Any(tool => tool.Name == choice) != true)
            throw new ArgumentException($"The tool choice \"{choice}\" names a tool that is switched off.");
        if (choice is "required" && enabled is not { Count: > 0 }) choice = tools is { Count: > 0 } ? null : choice;
        var spec = new RunSpec
        {
            Provider = preset.Provider ?? "preset",
            Model = preset.Model ?? "preset",
            Messages = messages,
            Tools = enabled is { Count: > 0 } ? enabled : null,
            ToolChoice = choice,
            Thinking = options.Thinking,
            MaxTokens = options.MaxTokens,
            Temperature = options.Temperature,
            TopP = options.TopP,
            TopK = options.TopK,
            Stop = options.Stop,
            Seed = options.Seed,
            PresencePenalty = options.PresencePenalty,
            FrequencyPenalty = options.FrequencyPenalty,
            ResponseFormat = options.ResponseFormat,
            ServiceTier = options.ServiceTier,
            Headers = preset.Headers,
            ProviderOptions = options.ProviderOptions,
            TextVerbosity = options.TextVerbosity,
            ReasoningSummary = options.ReasoningSummary,
            SessionId = options.SessionId,
            TimeoutMs = options.TimeoutMs,
            // RunSpec takes the switch only with a tool on; CheckPlayground keeps it either way (a preset saved without its tools, or with
            // them switched off, still has it: a page with tools uses it, and a run without one says it needs one, never a silent drop).
            ParallelToolCalls = enabled is { Count: > 0 } ? options.ParallelToolCalls : null,
            CacheRetention = options.CacheRetention,
            MaxRetries = options.MaxRetries,
            MaxRetryDelayMs = options.MaxRetryDelayMs,
            ThinkingBudgets = options.ThinkingBudgets,
            AzureApiVersion = options.AzureApiVersion,
        };
        return spec.Normalize();
    }

    private static PresetOptions OptionsOf(RunSpec spec) => new()
    {
        Thinking = spec.Thinking is null or "off" ? null : spec.Thinking,
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
        ThinkingBudgets = spec.ThinkingBudgets,
        AzureApiVersion = spec.AzureApiVersion,
    };

    private static void CheckSecrets(Preset preset)
    {
        foreach (var (name, value) in preset.Headers ?? new Dictionary<string, string>())
        {
            if (RunSpec.SecretName(name)) throw new ArgumentException($"The header {name} is not saved: a preset never holds a credential.");
            if (LooksSecret(value)) throw new ArgumentException($"The header {name} looks like it holds a credential, so the preset is not saved. Keep secrets out of header values.");
        }
        if (preset.Options?.ProviderOptions is { } extra) CheckNode(extra, "providerOptions");
    }

    private static void CheckNode(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject json:
                foreach (var (key, value) in json)
                {
                    if (RunSpec.SecretName(key)) throw new ArgumentException($"{path}.{key} is not saved: a preset never holds a credential.");
                    CheckNode(value, path + "." + key);
                }
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++) CheckNode(array[index], $"{path}[{index}]");
                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && LooksSecret(text):
                throw new ArgumentException($"{path} looks like it holds a credential, so the preset is not saved.");
        }
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex IdPattern();

    // W5 retest 1: also every key prefix SecretText masks (xai-, gsk_, nvapi-, csk-, hf_, fw_), so a case or preset value with one of them is
    // refused even when this app does not store that key (TestCase.CaseSpec's stored-credential match catches the keys it does store).
    [GeneratedRegex("^(?:sk-|sk_|pk_live|rk_live|xox[abprs]-|ghp_|gho_|ghu_|ghs_|github_pat_|glpat-|AIza|AKIA|ASIA|ya29\\.|xai-|gsk_|nvapi-|csk-|hf_|fw_)[A-Za-z0-9_\\-\\.]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
