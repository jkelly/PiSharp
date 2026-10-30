// Decision 0011 (run W4, R0): what a Playground, Compare or Bench run asks for, as the pages post it (JSON, camelCase): the model and the
// account (by id), the system prompt, the messages (system, user, assistant, tool result; text and images; an assistant's tool calls, and
// its signed thinking blocks and each call's thought signature, so a tool loop replays them as the provider sent them), the tools (a tool
// may be strict), and every request option the Playground offers (thinking, max tokens, temperature, top P, top K, stop sequences, seed,
// presence and frequency penalties, response format, tool choice, parallel tool calls, service tier, text verbosity, reasoning summary,
// session id, timeout, extra headers and provider options). A spec is checked here for what
// is never allowed whatever the model (sizes, ranges, names, credential headers, provider options that would replace the request's own
// structure); whether the chosen model and its API take each option is RunModelOptions' check. The account is an id: a spec never carries
// a key, and a header that could carry one is refused.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Runs;

/// <summary>An image of a message: its media type and base64 data.</summary>
public sealed record RunImage(string MimeType, string Data);

/// <summary>A tool call of an assistant message.</summary>
/// <param name="Id">The call id.</param>
/// <param name="Name">The tool's name.</param>
/// <param name="Arguments">The arguments.</param>
/// <param name="ThoughtSignature">The provider's signature of the call (Google's thought signature), sent back as the provider gave it so
/// a thinking model's tool loop continues; null when it had none.</param>
public sealed record RunToolCall(string Id, string Name, JsonObject? Arguments, string? ThoughtSignature = null);

/// <summary>A thinking block of an assistant message, as the provider streamed it: the text and its signature (Anthropic signs every block
/// and refuses a tool loop whose thinking comes back changed or missing; OpenAI Responses keeps its encrypted reasoning item here).</summary>
/// <param name="Text">The thinking text (empty for a redacted block).</param>
/// <param name="Signature">The provider's signature, or null.</param>
/// <param name="Redacted">Whether the provider redacted it (the signature then holds the opaque data).</param>
public sealed record RunThinking(string Text, string? Signature = null, bool Redacted = false);

/// <summary>One message of a run.</summary>
public sealed record RunMessage
{
    /// <summary><c>system</c>, <c>user</c>, <c>assistant</c> or <c>toolResult</c>.</summary>
    public string Role { get; init; } = "user";

    /// <summary>The text (a tool result's output).</summary>
    public string? Text { get; init; }

    /// <summary>Images (user and tool result messages).</summary>
    public IReadOnlyList<RunImage>? Images { get; init; }

    /// <summary>An assistant message's thinking blocks, before its text, as the provider sent them (signed); null for none.</summary>
    public IReadOnlyList<RunThinking>? Thinking { get; init; }

    /// <summary>An assistant message's tool calls.</summary>
    public IReadOnlyList<RunToolCall>? ToolCalls { get; init; }

    /// <summary>A tool result's call id.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>A tool result's tool name.</summary>
    public string? ToolName { get; init; }

    /// <summary>Whether a tool result is an error.</summary>
    public bool? IsError { get; init; }
}

/// <summary>A tool the model may call: a name, a description and a JSON Schema for its arguments.</summary>
/// <param name="Name">The name.</param>
/// <param name="Description">The description.</param>
/// <param name="Parameters">The JSON Schema of its arguments.</param>
/// <param name="Strict">Whether the provider must keep the arguments to the schema (OpenAI's strict function calling); null or false for
/// the provider's default.</param>
public sealed record RunTool(string Name, string? Description = null, JsonObject? Parameters = null, bool? Strict = null);

/// <summary>A run request. Unset options are not sent (the provider's defaults apply).</summary>
public sealed partial record RunSpec
{
    /// <summary>The most messages a run sends.</summary>
    public const int MaxMessages = 500;

    /// <summary>The most tools a run offers.</summary>
    public const int MaxTools = 128;

    /// <summary>The most images a run sends.</summary>
    public const int MaxImages = 20;

    /// <summary>The most stop sequences.</summary>
    public const int MaxStop = 16;

    /// <summary>The most extra headers.</summary>
    public const int MaxHeaders = 32;

    /// <summary>The largest request a run sends (messages, tools, images), in UTF-8 bytes.</summary>
    public const long MaxRequestBytes = 32L * 1024 * 1024;

    /// <summary>The thinking levels a spec may name (the model's own levels decide which it takes).</summary>
    public static IReadOnlyList<string> ThinkingLevels { get; } = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>The service tiers a spec may name (PLAN.md "Request speed": the seven values PiSharp's Responses adapter accepts; the model's
    /// API decides which it takes).</summary>
    public static IReadOnlyList<string> ServiceTiers { get; } = ["auto", "default", "flex", "scale", "priority", "fast", "ultrafast"];

    /// <summary>The text verbosities a spec may name.</summary>
    public static IReadOnlyList<string> TextVerbosities { get; } = ["low", "medium", "high"];

    /// <summary>The reasoning summaries a spec may name.</summary>
    public static IReadOnlyList<string> ReasoningSummaries { get; } = ["auto", "concise", "detailed"];

    /// <summary>The shortest per-run timeout, in milliseconds.</summary>
    public const int MinTimeoutMs = 1_000;

    /// <summary>The longest per-run timeout, in milliseconds (one hour).</summary>
    public const int MaxTimeoutMs = 3_600_000;

    /// <summary>The longest session id (OpenAI's prompt cache key).</summary>
    public const int MaxSessionId = 64;

    /// <summary>Provider option keys that would replace the request's own structure, never accepted.</summary>
    public static IReadOnlyList<string> StructuralKeys { get; } =
        ["model", "messages", "input", "contents", "system", "systemInstruction", "system_instruction", "instructions", "tools", "stream", "stream_options"];

    /// <summary>The provider id.</summary>
    public string Provider { get; init; } = "";

    /// <summary>The model id.</summary>
    public string Model { get; init; } = "";

    /// <summary>The account id, or null for the provider's active account.</summary>
    public string? Account { get; init; }

    /// <summary>The source's wire name (<c>playground</c> when null): <c>playground</c>, <c>compare</c>, <c>bench</c>, <c>agent-compare</c>,
    /// <c>arena</c>.</summary>
    public string? Source { get; init; }

    /// <summary>The group (a Compare or Bench run), or null.</summary>
    public string? GroupId { get; init; }

    /// <summary>The parent run, or null.</summary>
    public string? ParentId { get; init; }

    /// <summary>A title for History, or null (the first user text).</summary>
    public string? Title { get; init; }

    /// <summary>The system prompt, or null.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The messages, oldest first.</summary>
    public IReadOnlyList<RunMessage> Messages { get; init; } = [];

    /// <summary>The tools.</summary>
    public IReadOnlyList<RunTool>? Tools { get; init; }

    /// <summary><c>auto</c>, <c>none</c>, <c>required</c> or a tool's name; null for the provider's default.</summary>
    public string? ToolChoice { get; init; }

    /// <summary>The thinking level (<see cref="ThinkingLevels"/>); null for off.</summary>
    public string? Thinking { get; init; }

    /// <summary>The output cap, or null for the model's own.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>0 to 2.</summary>
    public double? Temperature { get; init; }

    /// <summary>0 to 1.</summary>
    public double? TopP { get; init; }

    /// <summary>1 to 1000.</summary>
    public int? TopK { get; init; }

    /// <summary>Stop sequences.</summary>
    public IReadOnlyList<string>? Stop { get; init; }

    /// <summary>The sampling seed.</summary>
    public long? Seed { get; init; }

    /// <summary>-2 to 2.</summary>
    public double? PresencePenalty { get; init; }

    /// <summary>-2 to 2.</summary>
    public double? FrequencyPenalty { get; init; }

    /// <summary><c>{type: "text"}</c>, <c>{type: "json_object"}</c> or <c>{type: "json_schema", name, schema, strict?}</c>.</summary>
    public JsonObject? ResponseFormat { get; init; }

    /// <summary><see cref="ServiceTiers"/>; only the OpenAI Responses API of provider openai, and the Codex backend of provider openai-codex
    /// (auto, default, flex and priority), take one.</summary>
    public string? ServiceTier { get; init; }

    /// <summary>Whether the model may call several tools in one turn (null for the provider's default).</summary>
    public bool? ParallelToolCalls { get; init; }

    /// <summary><see cref="TextVerbosities"/>: how long the answer is (the OpenAI Responses and Codex APIs).</summary>
    public string? TextVerbosity { get; init; }

    /// <summary><see cref="ReasoningSummaries"/>: the summary of the model's reasoning (the OpenAI Responses and Codex APIs, while thinking).</summary>
    public string? ReasoningSummary { get; init; }

    /// <summary>The session id the provider caches the prompt under (OpenAI's <c>prompt_cache_key</c>), or null for PiSharp's own.</summary>
    public string? SessionId { get; init; }

    /// <summary>How long to wait for the provider to start answering (its response headers), in milliseconds; null for no limit.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Extra request headers (never one that carries a credential).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Fields merged into the provider's request body (objects merge, anything else replaces); never the request's structure.</summary>
    public JsonObject? ProviderOptions { get; init; }

    /// <summary>The spend source (<see cref="Source"/>, <c>playground</c> when null).</summary>
    public SpendSource SpendSource => Source is null ? SpendSource.Playground : SpendSources.TryParse(Source, out var parsed) ? parsed : SpendSource.Playground;

    /// <summary>The thinking level, or null when off.</summary>
    public string? ThinkingLevel => Thinking is null or "off" ? null : Thinking;

    /// <summary>The spec from a page's JSON.</summary>
    /// <exception cref="ArgumentException">It is not a run spec.</exception>
    public static RunSpec Parse(JsonNode? json)
    {
        if (json is not JsonObject) throw new ArgumentException("Send the run as a JSON object.");
        try
        {
            return json.Deserialize<RunSpec>(SettingsStore.JsonOptions) ?? throw new ArgumentException("Send the run as a JSON object.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The run is not valid: " + (error.Path is { Length: > 0 } path ? path + ": " : "") + FirstSentence(error.Message));
        }
        catch (InvalidOperationException error)
        {
            throw new ArgumentException("The run is not valid: " + FirstSentence(error.Message));
        }
    }

    /// <summary>The same spec, trimmed and checked for what no model allows.</summary>
    /// <exception cref="ArgumentException">A value is not allowed; the message says which.</exception>
    public RunSpec Normalize()
    {
        var provider = Provider?.Trim() ?? "";
        var model = Model?.Trim() ?? "";
        if (provider.Length == 0) throw new ArgumentException("Choose a provider.");
        if (model.Length == 0) throw new ArgumentException("Choose a model.");
        SpendSource? source = null;
        if (Source is not null)
        {
            if (!SpendSources.TryParse(Source.Trim(), out var parsed)) throw new ArgumentException($"There is no source \"{Source}\".");
            if (parsed == Spend.SpendSource.Chat)
                throw new ArgumentException("Chats record their own calls; a run's source is playground, compare, bench, agent-compare or arena.");
            source = parsed;
        }
        if (Messages is null || Messages.Count == 0) throw new ArgumentException("Add at least one message.");
        if (Messages.Count > MaxMessages) throw new ArgumentException($"A run sends at most {MaxMessages} messages.");
        var images = 0;
        for (var index = 0; index < Messages.Count; index++)
        {
            var message = Messages[index] ?? throw new ArgumentException($"Message {index + 1} is empty.");
            var where = $"Message {index + 1}";
            switch (message.Role)
            {
                case "system" or "user":
                    if (message.ToolCalls is { Count: > 0 }) throw new ArgumentException($"{where}: only an assistant message has tool calls.");
                    break;
                case "assistant":
                    if (message.Images is { Count: > 0 }) throw new ArgumentException($"{where}: an assistant message has no images.");
                    foreach (var block in message.Thinking ?? [])
                        if (block is null || block.Text is null || (block.Redacted && string.IsNullOrEmpty(block.Signature)))
                            throw new ArgumentException($"{where}: a thinking block needs its text (a redacted one, its data).");
                    foreach (var call in message.ToolCalls ?? [])
                    {
                        if (call is null || string.IsNullOrWhiteSpace(call.Id)) throw new ArgumentException($"{where}: every tool call needs an id.");
                        CheckToolName(call.Name, where);
                    }
                    break;
                case "toolResult":
                    if (string.IsNullOrWhiteSpace(message.ToolCallId)) throw new ArgumentException($"{where}: a tool result names the call it answers (toolCallId).");
                    CheckToolName(message.ToolName, where);
                    if (message.ToolCalls is { Count: > 0 }) throw new ArgumentException($"{where}: only an assistant message has tool calls.");
                    break;
                default:
                    throw new ArgumentException($"{where}: the role is system, user, assistant or toolResult, not \"{message.Role}\".");
            }
            if (message.Role == "system" && message.Images is { Count: > 0 }) throw new ArgumentException($"{where}: a system message has no images.");
            if (message.Role != "assistant" && message.Thinking is { Count: > 0 }) throw new ArgumentException($"{where}: only an assistant message has thinking blocks.");
            foreach (var image in message.Images ?? [])
            {
                if (image is null || string.IsNullOrWhiteSpace(image.MimeType) || !image.MimeType.StartsWith("image/", StringComparison.Ordinal))
                    throw new ArgumentException($"{where}: an image needs an image/* media type.");
                if (string.IsNullOrEmpty(image.Data)) throw new ArgumentException($"{where}: an image has no data.");
                images++;
            }
        }
        if (images > MaxImages) throw new ArgumentException($"A run sends at most {MaxImages} images.");
        if (!Messages.Any(message => message.Role is "user" or "toolResult"))
            throw new ArgumentException("Add a user message (or a tool result) for the model to answer.");
        var tools = Tools ?? [];
        if (tools.Count > MaxTools) throw new ArgumentException($"A run offers at most {MaxTools} tools.");
        foreach (var tool in tools)
        {
            if (tool is null) throw new ArgumentException("A tool is empty.");
            CheckToolName(tool.Name, "A tool");
            if (tool.Parameters is { } schema && schema["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind != "object")
                throw new ArgumentException($"Tool {tool.Name}: its parameters are a JSON Schema object (type \"object\").");
        }
        if (tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count) throw new ArgumentException("Two tools share a name.");
        var choice = string.IsNullOrWhiteSpace(ToolChoice) ? null : ToolChoice.Trim();
        if (choice is not (null or "auto" or "none" or "required") && !tools.Any(tool => tool.Name == choice))
            throw new ArgumentException($"The tool choice \"{choice}\" is auto, none, required or one of the run's tools.");
        if (choice is "required" && tools.Count == 0) throw new ArgumentException("A required tool call needs at least one tool.");
        var thinking = string.IsNullOrWhiteSpace(Thinking) ? null : Thinking.Trim();
        if (thinking is not null && !ThinkingLevels.Contains(thinking)) throw new ArgumentException($"Thinking is {string.Join(", ", ThinkingLevels)}, not \"{thinking}\".");
        if (MaxTokens is { } max && (max < 1 || max > 10_000_000)) throw new ArgumentException("Max tokens is 1 or more.");
        Range(Temperature, 0, 2, "Temperature");
        Range(TopP, 0, 1, "Top P");
        if (TopK is { } topK && (topK < 1 || topK > 1000)) throw new ArgumentException("Top K is 1 to 1000.");
        Range(PresencePenalty, -2, 2, "Presence penalty");
        Range(FrequencyPenalty, -2, 2, "Frequency penalty");
        var stop = Stop?.Where(text => !string.IsNullOrEmpty(text)).ToList();
        if (stop is { Count: > MaxStop }) throw new ArgumentException($"At most {MaxStop} stop sequences.");
        if (stop is not null && stop.Any(text => text.Length > 200)) throw new ArgumentException("A stop sequence is at most 200 characters.");
        var tier = string.IsNullOrWhiteSpace(ServiceTier) ? null : ServiceTier.Trim();
        if (tier is not null && !ServiceTiers.Contains(tier)) throw new ArgumentException($"The service tier is {string.Join(", ", ServiceTiers)}, not \"{tier}\".");
        if (ResponseFormat is { } format) CheckFormat(format);
        var verbosity = string.IsNullOrWhiteSpace(TextVerbosity) ? null : TextVerbosity.Trim();
        if (verbosity is not null && !TextVerbosities.Contains(verbosity))
            throw new ArgumentException($"Text verbosity is {string.Join(", ", TextVerbosities)}, not \"{verbosity}\".");
        var summary = string.IsNullOrWhiteSpace(ReasoningSummary) ? null : ReasoningSummary.Trim();
        if (summary is not null && !ReasoningSummaries.Contains(summary))
            throw new ArgumentException($"The reasoning summary is {string.Join(", ", ReasoningSummaries)}, not \"{summary}\".");
        var session = string.IsNullOrWhiteSpace(SessionId) ? null : SessionId.Trim();
        if (session is not null && (session.Length > MaxSessionId || !SessionPattern().IsMatch(session)))
            throw new ArgumentException($"A session id is letters, digits, '-', '_', '.' and ':' (at most {MaxSessionId}).");
        if (TimeoutMs is { } timeout && (timeout < MinTimeoutMs || timeout > MaxTimeoutMs))
            throw new ArgumentException($"The timeout is {MinTimeoutMs:N0} to {MaxTimeoutMs:N0} ms.");
        if (ParallelToolCalls is not null && tools.Count == 0) throw new ArgumentException("Parallel tool calls need at least one tool.");
        var headers = Headers is null ? null : CheckHeaders(Headers);
        if (ProviderOptions is { } extra)
            foreach (var key in extra.Select(pair => pair.Key))
                if (StructuralKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"Provider options cannot set \"{key}\": the run sets the request's model, messages and tools itself.");
                else if (SecretName(key) && !RunModelOptions.OutputOptionName(key))
                    throw new ArgumentException($"Provider options cannot set \"{key}\": the account supplies its own credentials.");
        var normalized = this with
        {
            Provider = provider,
            Model = model,
            Account = string.IsNullOrWhiteSpace(Account) ? null : Account.Trim(),
            Source = source?.Wire(),
            GroupId = string.IsNullOrWhiteSpace(GroupId) ? null : GroupId.Trim(),
            ParentId = string.IsNullOrWhiteSpace(ParentId) ? null : ParentId.Trim(),
            Title = RunText.Title(Title),
            SystemPrompt = string.IsNullOrEmpty(SystemPrompt) ? null : SystemPrompt,
            Tools = tools.Count == 0 ? null : tools,
            ToolChoice = choice,
            Thinking = thinking,
            Stop = stop is { Count: > 0 } ? stop : null,
            ServiceTier = tier,
            TextVerbosity = verbosity,
            ReasoningSummary = summary,
            SessionId = session,
            Headers = headers is { Count: > 0 } ? headers : null,
            ProviderOptions = ProviderOptions is { Count: > 0 } ? ProviderOptions : null,
        };
        foreach (var id in new[] { normalized.GroupId, normalized.ParentId })
            if (id is not null && (id.Length > 100 || !IdPattern().IsMatch(id))) throw new ArgumentException("A group or parent id is letters, digits, '-' and '_' (at most 100).");
        return normalized;
    }

    /// <summary>The request's size in UTF-8 bytes (system prompt, messages, tools), as JSON.</summary>
    public long RequestBytes() => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { SystemPrompt, Messages, Tools }, SettingsStore.JsonOptions));

    /// <summary>The spec without its messages, system prompt and tools: the options History shows.</summary>
    public JsonObject OptionsJson()
    {
        var json = JsonSerializer.SerializeToNode(this with { Messages = [], SystemPrompt = null, Tools = null }, SettingsStore.JsonOptions)!.AsObject();
        json.Remove("messages");
        json.Remove("systemPrompt");
        json.Remove("tools");
        json.Remove("spendSource");
        json.Remove("thinkingLevel");
        foreach (var key in json.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList()) json.Remove(key);
        if (Tools is { Count: > 0 } tools) json["toolNames"] = new JsonArray([.. tools.Select(tool => (JsonNode)JsonValue.Create(tool.Name)!)]);
        return json;
    }

    /// <summary>The system prompt, messages and tools as History stores them.</summary>
    public JsonObject MessagesJson()
    {
        var json = new JsonObject();
        if (SystemPrompt is not null) json["systemPrompt"] = SystemPrompt;
        json["messages"] = JsonSerializer.SerializeToNode(Messages, SettingsStore.JsonOptions);
        if (Tools is { Count: > 0 }) json["tools"] = JsonSerializer.SerializeToNode(Tools, SettingsStore.JsonOptions);
        return json;
    }

    /// <summary>The first user text, for a title.</summary>
    public string? FirstUserText() => Messages.FirstOrDefault(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Text))?.Text;

    /// <summary>Whether a header or option name looks like it carries a credential: <c>authorization</c>, <c>cookie</c>, or any name with
    /// <c>key</c>, <c>token</c>, <c>secret</c>, <c>auth</c>, <c>password</c>, <c>credential</c> or <c>signature</c> in it.</summary>
    public static bool SecretName(string name)
    {
        var lower = name.ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal);
        return lower is "cookie" or "set-cookie" or "proxy-authorization" ||
            new[] { "key", "token", "secret", "auth", "password", "credential", "signature", "x-amz-" }.Any(part => lower.Contains(part, StringComparison.Ordinal));
    }

    private static readonly string[] ReservedHeaders = ["host", "content-length", "content-type", "content-encoding", "transfer-encoding", "connection", "upgrade", "te", "expect"];

    private static Dictionary<string, string> CheckHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count > MaxHeaders) throw new ArgumentException($"At most {MaxHeaders} extra headers.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, rawValue) in headers)
        {
            var name = rawName?.Trim() ?? "";
            if (!HeaderName().IsMatch(name)) throw new ArgumentException($"\"{rawName}\" is not a header name.");
            if (SecretName(name)) throw new ArgumentException($"The header {name} is not allowed: the account supplies its own credentials, and a run never sends one.");
            if (ReservedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"The header {name} is set by the request itself.");
            var value = rawValue ?? "";
            if (value.Length > 8192 || value.Any(character => character is '\r' or '\n' or '\0')) throw new ArgumentException($"The value of {name} is not allowed.");
            if (!result.TryAdd(name, value)) throw new ArgumentException($"The header {name} is set twice.");
        }
        return result;
    }

    private static void CheckFormat(JsonObject format)
    {
        var type = format["type"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        switch (type)
        {
            case "text" or "json_object":
                return;
            case "json_schema":
                if (format["schema"] is not JsonObject) throw new ArgumentException("A JSON schema response format needs its schema (an object).");
                if (format["name"] is JsonValue name && (!name.TryGetValue<string>(out var named) || !ToolPattern().IsMatch(named)))
                    throw new ArgumentException("A JSON schema's name is letters, digits, '_' and '-' (at most 64).");
                return;
            default:
                throw new ArgumentException("The response format is text, json_object or json_schema.");
        }
    }

    private static void CheckToolName(string? name, string where)
    {
        if (string.IsNullOrEmpty(name) || !ToolPattern().IsMatch(name)) throw new ArgumentException($"{where}: a tool name is letters, digits, '_' and '-' (at most 64).");
    }

    private static void Range(double? value, double low, double high, string what)
    {
        if (value is { } number && (!double.IsFinite(number) || number < low || number > high))
            throw new ArgumentException($"{what} is {low.ToString(System.Globalization.CultureInfo.InvariantCulture)} to {high.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
    }

    private static string FirstSentence(string message)
    {
        var end = message.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? message[..(end + 1)] : message;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ToolPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9_.:-]+$")]
    private static partial Regex SessionPattern();

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,100}$")]
    private static partial Regex HeaderName();
}
