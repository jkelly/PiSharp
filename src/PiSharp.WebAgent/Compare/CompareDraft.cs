// Decision 0011 (run W4, R2): what the Compare page (GET /compare, request mode) posts: one shared prompt (and an optional system prompt),
// the shared options (thinking, max tokens, temperature) and 2 to 4 columns, each a model (Provider → Model) with its own optional
// overrides (thinking, temperature). JSON, camelCase, as the page sends it; an unset value is not sent. The draft never carries a key: a
// column's account is an id, as a RunSpec's.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Compare;

/// <summary>One column of a Compare draft: a model and its overrides of the shared options.</summary>
public sealed record CompareColumnDraft
{
    /// <summary>The provider id.</summary>
    public string Provider { get; init; } = "";

    /// <summary>The model id.</summary>
    public string Model { get; init; } = "";

    /// <summary>The account id, or null for the provider's active account.</summary>
    public string? Account { get; init; }

    /// <summary>The thinking level this column sends instead of the shared one, or null to inherit it.</summary>
    public string? Thinking { get; init; }

    /// <summary>The temperature this column sends instead of the shared one, or null to inherit it.</summary>
    public double? Temperature { get; init; }

    /// <summary>Whether this column overrides a shared option.</summary>
    [JsonIgnore]
    public bool Overrides => Thinking is not null || Temperature is not null;

    /// <summary>The same column, trimmed.</summary>
    public CompareColumnDraft Trimmed() => this with
    {
        Provider = Provider?.Trim() ?? "",
        Model = Model?.Trim() ?? "",
        Account = string.IsNullOrWhiteSpace(Account) ? null : Account.Trim(),
        Thinking = string.IsNullOrWhiteSpace(Thinking) || Thinking.Trim() == "inherit" ? null : Thinking.Trim(),
    };
}

/// <summary>A Compare request: the shared prompt and options, and the columns.</summary>
public sealed record CompareDraft
{
    /// <summary>The fewest columns Run all sends.</summary>
    public const int MinColumns = 2;

    /// <summary>The most columns a Compare page has.</summary>
    public const int MaxColumns = 4;

    /// <summary>The shared system prompt, or null.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The shared user prompt.</summary>
    public string Prompt { get; init; } = "";

    /// <summary>The shared thinking level (<see cref="Runs.RunSpec.ThinkingLevels"/>), or null for off.</summary>
    public string? Thinking { get; init; }

    /// <summary>The shared output cap, or null for each model's own.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>The shared temperature, or null for each provider's default.</summary>
    public double? Temperature { get; init; }

    /// <summary>The columns, left to right.</summary>
    public IReadOnlyList<CompareColumnDraft> Columns { get; init; } = [];

    /// <summary>The same draft, trimmed (a blank system prompt is none).</summary>
    public CompareDraft Trimmed() => this with
    {
        SystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt,
        Prompt = Prompt ?? "",
        Thinking = string.IsNullOrWhiteSpace(Thinking) || Thinking.Trim() is "off" or "inherit" ? null : Thinking.Trim(),
        Columns = [.. (Columns ?? []).Select(column => (column ?? new CompareColumnDraft()).Trimmed())],
    };

    /// <summary>The draft without its columns (what a stored group keeps as its shared request).</summary>
    [JsonIgnore]
    public CompareDraft Shared => this with { Columns = [] };

    /// <summary>The draft from the page's JSON.</summary>
    /// <exception cref="ArgumentException">It is not a Compare draft.</exception>
    public static CompareDraft Parse(JsonNode? json)
    {
        if (json is not JsonObject) throw new ArgumentException("Send the comparison as a JSON object.");
        try
        {
            return (json.Deserialize<CompareDraft>(SettingsStore.JsonOptions) ?? throw new ArgumentException("Send the comparison as a JSON object.")).Trimmed();
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The comparison is not valid: " + (error.Path is { Length: > 0 } path ? path + ": " : "") + FirstSentence(error.Message));
        }
        catch (InvalidOperationException error)
        {
            throw new ArgumentException("The comparison is not valid: " + FirstSentence(error.Message));
        }
    }

    /// <summary>A column from the page's JSON.</summary>
    /// <exception cref="ArgumentException">It is not a column.</exception>
    public static CompareColumnDraft ParseColumn(JsonNode? json)
    {
        if (json is not JsonObject) throw new ArgumentException("Send the column as a JSON object.");
        try
        {
            return (json.Deserialize<CompareColumnDraft>(SettingsStore.JsonOptions) ?? throw new ArgumentException("Send the column as a JSON object.")).Trimmed();
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The column is not valid: " + (error.Path is { Length: > 0 } path ? path + ": " : "") + FirstSentence(error.Message));
        }
    }

    private static string FirstSentence(string message)
    {
        var end = message.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? message[..(end + 1)] : message;
    }
}
