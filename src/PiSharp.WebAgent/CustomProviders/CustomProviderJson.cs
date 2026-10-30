// Decision 0011 (run W3, CP): reading and writing Pi's models.json format for the custom providers area. The file is the user's: it keeps
// Pi's schema (PiSharp.ModelRuntime's ModelsJsonConfig validates it), so it can be copied between the Pi CLI and this app. What the web app
// adds is a line of rules about keys: an apiKey in the file is only ever an environment reference ($VAR or ${VAR}); a key the user types is
// a vault account, never written into the file; a literal or !command key written by hand is left in the file but not used (running a
// command from a web form is out of scope), and the routes read a sanitized copy (RouteCopy) without it. Header values that look like
// secrets (Authorization, *key*, *token*, *secret*, *password*, cookies) must be references too, and a header value never runs a command.
// A built-in provider's entry contributes only its modelOverrides to that copy: its requests go where its account says (Settings ›
// Providers) with the account's key, so a baseUrl, headers, compat, models, api, authHeader or oauth written for it is listed on the page
// as not used (BuiltinIgnoredFields) and never changes where that key is sent. A stored key follows a custom provider only to the servers
// its saved entry already uses (SameServers): an edit or an import that points it elsewhere needs the key entered again.
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;

namespace PiSharp.WebAgent.CustomProviders;

/// <summary>models.json helpers of the custom providers area.</summary>
public static partial class CustomProviderJson
{
    /// <summary>The API types a custom provider can use: the ones a live route serves for a provider that is not built in.</summary>
    public static IReadOnlyList<string> Apis { get; } =
        ["openai-completions", "openai-responses", "anthropic-messages", "google-generative-ai", "mistral-conversations", "pi-messages"];

    /// <summary>The compat fields the editor manages (the OpenAI Completions group of the mockup), with their allowed string values (null:
    /// a boolean). Other compat fields are kept from the file as they are.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>?> EditableCompat { get; } = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal)
    {
        ["supportsDeveloperRole"] = null,
        ["supportsReasoningEffort"] = null,
        ["supportsUsageInStreaming"] = null,
        ["requiresToolResultName"] = null,
        ["supportsStrictMode"] = null,
        ["maxTokensField"] = ["max_tokens", "max_completion_tokens"],
        ["thinkingFormat"] = ["openai", "openrouter", "together", "baseten", "deepseek", "zai", "qwen", "chat-template", "qwen-chat-template", "string-thinking", "ant-ling"],
        ["cacheControlFormat"] = ["anthropic"],
    };

    /// <summary>The thinking levels of a <c>thinkingLevelMap</c>, in order.</summary>
    public static IReadOnlyList<string> ThinkingLevels { get; } = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>The fields a model override may set (model-config.ts ModelOverrideSchema).</summary>
    public static IReadOnlyList<string> OverrideFields { get; } =
        ["name", "reasoning", "thinkingLevelMap", "input", "inputLimits", "cost", "promptCache", "contextWindow", "maxTokens", "samplingParams", "headers", "compat"];

    /// <summary>The key models.json requests carry when a provider has none (the live routes need one; servers without auth ignore it).</summary>
    public const string PlaceholderKey = "no-key";

    /// <summary>The header comment written at the top of the file.</summary>
    internal static string Header(DateTimeOffset saved) =>
        $"// PiSharp Web Agent · models.json · saved {saved.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC\n" +
        "// Same schema as Pi's models.json: // comments and trailing commas are allowed.\n";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The file text of <paramref name="root"/>: the header comment, then indented JSON.</summary>
    public static string Format(JsonObject root, DateTimeOffset saved)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Header(saved) + root.ToJsonString(Indented) + "\n";
    }

    /// <summary>The JSON text of <paramref name="node"/>, indented.</summary>
    public static string Pretty(JsonNode? node) => node?.ToJsonString(Indented) ?? "null";

    /// <summary><paramref name="text"/> (models.json, comments and trailing commas allowed) as a JSON object, or the reason it is not one.</summary>
    internal static JsonObject? ParseObject(string text, out string? error)
    {
        error = null;
        try
        {
            if (JsonNode.Parse(ModelsJsonConfig.StripJsonComments(ModelsJsonConfig.StripBom(text))) is JsonObject root) return root;
            error = "models.json must be a JSON object.";
        }
        catch (JsonException parse) { error = "models.json is not valid JSON: " + parse.Message; }
        return null;
    }

    /// <summary>The <c>providers</c> object of <paramref name="root"/>, created when missing.</summary>
    internal static JsonObject Providers(JsonObject root)
    {
        if (root["providers"] is JsonObject providers) return providers;
        providers = new JsonObject();
        root["providers"] = providers;
        return providers;
    }

    /// <summary>Whether <paramref name="id"/> is a built-in provider (its entry can only override, not be a custom provider).</summary>
    public static bool IsBuiltin(string id) => BuiltinProviders.TryGet(id, out _) || id is "faux";

    /// <summary>Whether <paramref name="id"/> is a valid new custom provider id: a lower-case slug of at most 64 characters.</summary>
    public static bool IsSlug(string? id) => id is not null && SlugPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^\$(?:([A-Za-z_][A-Za-z0-9_]*)|\{([A-Za-z_][A-Za-z0-9_]*)\})$")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariablePattern();

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,128}$")]
    private static partial Regex HeaderNamePattern();

    /// <summary>Whether <paramref name="name"/> is an environment variable name.</summary>
    public static bool IsVariable(string? name) => name is not null && VariablePattern().IsMatch(name);

    /// <summary>Whether <paramref name="name"/> is an HTTP header name.</summary>
    public static bool IsHeaderName(string? name) => name is not null && HeaderNamePattern().IsMatch(name);

    /// <summary>The variable <paramref name="value"/> is exactly a reference to (<c>$VAR</c> or <c>${VAR}</c>), or null.</summary>
    public static string? ReferenceOf(string? value) =>
        value is not null && ReferencePattern().Match(value) is { Success: true } match ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;

    /// <summary>Whether a header named <paramref name="name"/> carries a credential, so its value must be a reference.</summary>
    public static bool IsSecretHeader(string name) =>
        name.Equals("authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cookie", StringComparison.OrdinalIgnoreCase) || name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("signature", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a header value is safe to show and to route: not a command, and a reference when the name carries a credential.</summary>
    public static bool IsShownHeader(string name, string value) =>
        !ConfigValueResolver.IsCommand(value) && (!IsSecretHeader(name) ||
            ConfigValueResolver.GetEnvVarNames(value).Count > 0 && AnyReference().Replace(value, "").Trim().Length <= 16);

    [GeneratedRegex(@"\$(?:\{[A-Za-z_][A-Za-z0-9_]*\}|[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex AnyReference();

    /// <summary>Whether <paramref name="url"/> is on this machine: <c>localhost</c>, <c>*.localhost</c>, <c>127.0.0.0/8</c> or <c>::1</c>.</summary>
    public static bool IsLoopback(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>Whether a listed model id looks like an embedding model (not a chat model).</summary>
    public static bool LooksLikeEmbedding(string id) => id.Contains("embed", StringComparison.OrdinalIgnoreCase);

    /// <summary>How the provider entry <paramref name="provider"/> configures its key: none, an environment reference, or something the web
    /// app does not use (with the reason).</summary>
    public static (CustomKeySource Source, string? Variable, string? Problem) KeyOf(JsonObject provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider["apiKey"] is not JsonValue value || !value.TryGetValue<string>(out var key)) return (CustomKeySource.None, null, null);
        if (ReferenceOf(key) is { } variable) return (CustomKeySource.Environment, variable, null);
        if (ConfigValueResolver.IsCommand(key))
            return (CustomKeySource.Unsupported, null, "Its apiKey is a !command, which the web app does not run. Edit it and choose another key source.");
        return (CustomKeySource.Unsupported, null, ConfigValueResolver.GetEnvVarNames(key).Count > 0
            ? "Its apiKey mixes text and variables; the web app only reads a single $VAR reference. Edit it and choose another key source."
            : "Its apiKey is a literal key written into models.json. Edit it and save the key in the vault, or use a $VAR reference.");
    }

    /// <summary>The string map <paramref name="name"/> of <paramref name="value"/>, in order (non-strings skipped).</summary>
    internal static IEnumerable<KeyValuePair<string, string>> StringMap(JsonObject value, string name)
    {
        if (value[name] is not JsonObject map) yield break;
        foreach (var (key, node) in map)
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text)) yield return KeyValuePair.Create(key, text);
    }

    private static string? Text(JsonObject value, string name) => value[name] is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonObject? value, string name) =>
        value?[name] is JsonValue scalar && scalar.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;

    private static bool Flag(JsonObject value, string name) => value[name] is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) && flag;

    /// <summary>The custom provider <paramref name="id"/> of the entry <paramref name="provider"/>, as metadata.</summary>
    public static CustomProviderInfo Info(string id, JsonObject provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var (source, variable, problem) = KeyOf(provider);
        var headers = StringMap(provider, "headers").ToList();
        if (problem is null && headers.FirstOrDefault(pair => ConfigValueResolver.IsCommand(pair.Value)) is { Key: { } commandHeader })
            problem = $"Its header {commandHeader} runs a !command, which the web app does not run; it is not sent.";
        var models = new List<CustomModelInfo>();
        if (provider["models"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject model || Text(model, "id") is not { Length: > 0 } modelId) continue;
                var cost = model["cost"] as JsonObject;
                var input = Number(cost, "input") ?? 0;
                var output = Number(cost, "output") ?? 0;
                models.Add(new CustomModelInfo(modelId, Text(model, "name") is { Length: > 0 } name ? name : modelId)
                {
                    Reasoning = Flag(model, "reasoning"),
                    Images = model["input"] is JsonArray kinds && kinds.Any(kind => kind is JsonValue entry && entry.TryGetValue<string>(out var text) && text == "image"),
                    ContextWindow = Number(model, "contextWindow"),
                    MaxTokens = Number(model, "maxTokens"),
                    CostInput = input,
                    CostOutput = output,
                    Priced = input > 0 || output > 0 || (Number(cost, "cacheRead") ?? 0) > 0 || (Number(cost, "cacheWrite") ?? 0) > 0,
                });
            }
        }
        var baseUrl = Text(provider, "baseUrl") ?? "";
        var api = Text(provider, "api") ?? "";
        var variables = new List<string>();
        if (variable is not null) variables.Add(variable);
        foreach (var (_, value) in headers) variables.AddRange(ConfigValueResolver.GetEnvVarNames(value));
        return new CustomProviderInfo(id, Text(provider, "name") is { Length: > 0 } display ? display : id, baseUrl, api)
        {
            FileKey = source,
            KeyVariable = variable,
            Problem = problem,
            AuthHeader = Flag(provider, "authHeader"),
            HeaderNames = [.. headers.Select(pair => pair.Key)],
            Variables = [.. variables.Distinct(StringComparer.Ordinal)],
            Models = models,
            Loopback = IsLoopback(baseUrl),
            Routed = source != CustomKeySource.Unsupported && models.Count > 0 && LiveSessionSelection.SupportedApi(id, api),
        };
    }

    /// <summary>The one field of a built-in provider's entry the routes read: <c>modelOverrides</c>.</summary>
    public const string BuiltinRoutedField = "modelOverrides";

    /// <summary>The fields of the built-in provider entry <paramref name="entry"/> that are not used (every field but
    /// <see cref="BuiltinRoutedField"/>), in file order: a built-in provider's requests go where its account says, with its key.</summary>
    public static IReadOnlyList<string> BuiltinIgnoredFields(JsonObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return [.. entry.Select(pair => pair.Key).Where(field => field != BuiltinRoutedField)];
    }

    /// <summary>The servers <paramref name="entry"/> sends requests to: the scheme and authority (<c>https://host:port</c>) of its
    /// <c>baseUrl</c> and of every model's <c>baseUrl</c>.</summary>
    internal static HashSet<string> Servers(JsonObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var servers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? url)
        {
            if (url?.Trim() is not { Length: > 0 } text) return;
            servers.Add(Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : text);
        }
        Add(Text(entry, "baseUrl"));
        if (entry["models"] is JsonArray models) foreach (var model in models.OfType<JsonObject>()) Add(Text(model, "baseUrl"));
        return servers;
    }

    /// <summary>Whether every server <paramref name="next"/> sends requests to is one the saved entry <paramref name="saved"/> already sends
    /// to (false when nothing is saved): a stored key may follow an edit or an import of a custom provider only then.</summary>
    internal static bool SameServers(JsonObject? saved, JsonObject next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return saved is not null && Servers(next).IsSubsetOf(Servers(saved));
    }

    /// <summary>The sanitized copy of <paramref name="root"/> the catalog and the routes read, or null when it routes nothing: custom
    /// providers whose key is not usable here are left out, a built-in provider's entry keeps only its <c>modelOverrides</c> (left out
    /// when it has none: its key is an account's, sent only where that account says), and header values that run a command are dropped
    /// everywhere.</summary>
    internal static JsonObject? RouteCopy(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root["providers"] is not JsonObject providers) return null;
        var copy = new JsonObject();
        foreach (var (id, node) in providers)
        {
            if (node is not JsonObject entry) continue;
            JsonObject clone;
            if (IsBuiltin(id))
            {
                if (entry[BuiltinRoutedField] is not JsonObject { Count: > 0 } builtinOverrides) continue;
                clone = new JsonObject { [BuiltinRoutedField] = builtinOverrides.DeepClone() };
            }
            else
            {
                clone = (JsonObject)entry.DeepClone();
                if (KeyOf(clone).Source == CustomKeySource.Unsupported) continue;
            }
            DropCommandHeaders(clone);
            if (clone["models"] is JsonArray models) foreach (var model in models.OfType<JsonObject>()) DropCommandHeaders(model);
            if (clone["modelOverrides"] is JsonObject overrides) foreach (var (_, value) in overrides) if (value is JsonObject entryOverride) DropCommandHeaders(entryOverride);
            copy[id] = clone;
        }
        return copy.Count == 0 ? null : new JsonObject { ["providers"] = copy };
    }

    private static void DropCommandHeaders(JsonObject value)
    {
        if (value["headers"] is not JsonObject headers) return;
        foreach (var name in headers.Where(pair => pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && ConfigValueResolver.IsCommand(text))
            .Select(pair => pair.Key).ToList()) headers.Remove(name);
    }

    /// <summary><paramref name="root"/> as it may be shown or exported: an <c>apiKey</c> that is not a reference is removed, and so is a
    /// header value that looks like a secret or runs a command. The app writes neither, so this only changes a file edited by hand.</summary>
    public static JsonObject Redacted(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var clone = (JsonObject)root.DeepClone();
        if (clone["providers"] is not JsonObject providers) return clone;
        foreach (var (_, node) in providers)
        {
            if (node is not JsonObject entry) continue;
            if (entry["apiKey"] is JsonValue key && (!key.TryGetValue<string>(out var text) || ReferenceOf(text) is null)) entry.Remove("apiKey");
            RedactHeaders(entry);
            if (entry["models"] is JsonArray models) foreach (var model in models.OfType<JsonObject>()) RedactHeaders(model);
            if (entry["modelOverrides"] is JsonObject overrides) foreach (var (_, value) in overrides) if (value is JsonObject entryOverride) RedactHeaders(entryOverride);
        }
        return clone;
    }

    /// <summary>Removes from <paramref name="value"/>'s <c>headers</c> every value that may not be shown (<see cref="IsShownHeader"/>).</summary>
    internal static void RedactHeaders(JsonObject value)
    {
        if (value["headers"] is not JsonObject headers) return;
        foreach (var name in headers.Where(pair => !(pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && IsShownHeader(pair.Key, text)))
            .Select(pair => pair.Key).ToList()) headers.Remove(name);
    }

    /// <summary>The <c>apiKey</c> strings of a models.json text, found leniently (for scrubbing an error about a text that does not parse).</summary>
    internal static IEnumerable<string> ApiKeysIn(string text) =>
        ApiKeyPattern().Matches(text).Select(match => match.Groups[1].Value).Where(value => value.Length >= 4);

    [GeneratedRegex("\"apiKey\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"")]
    private static partial Regex ApiKeyPattern();

    /// <summary>A short JSON text of a scalar override value, as a chip shows it (<c>200,000</c>, <c>$1.80</c>, <c>true</c>).</summary>
    public static string Short(JsonNode? value, bool money = false)
    {
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<double>(out var number))
                return money ? "$" + number.ToString("0.####", CultureInfo.InvariantCulture) : number.ToString("#,0.####", CultureInfo.InvariantCulture);
            if (scalar.TryGetValue<string>(out var text)) return text;
            if (scalar.TryGetValue<bool>(out var flag)) return flag ? "true" : "false";
        }
        if (value is null) return "null";
        var json = value.ToJsonString();
        return json.Length <= 40 ? json : json[..39] + "…";
    }

    /// <summary>Bytes as the page shows them (<c>2.1 KB</c>).</summary>
    public static string Size(long bytes) => bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + " B"
        : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";

    /// <summary>The text of <paramref name="value"/> as a JSON override value: JSON when it parses, else a string.</summary>
    internal static JsonNode? ParseValue(string value)
    {
        var trimmed = value.Trim();
        try { return JsonNode.Parse(trimmed); }
        catch (JsonException) { return JsonValue.Create(trimmed); }
    }
}
