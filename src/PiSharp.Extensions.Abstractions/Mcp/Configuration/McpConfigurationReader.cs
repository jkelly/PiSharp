// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/mcp-servers.ts and packages/coding-agent/src/extensions/mcp/config.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Configuration;

/// <summary>Pure projection of pinned v1.1.0 mcp-servers.ts and extensions/mcp/config.ts. No I/O or interpolation.</summary>
public static class McpConfigurationReader
{
    private const string Exposures = "\"codemode\", \"deferred\", \"direct\", \"hidden\"";
    private static readonly string[] OverrideKeys = ["enabled", "exposure", "toolExposure"];

    /// <summary>Validates one server entry and returns a copy with exposure aliases (`codemode-deferred`) resolved.</summary>
    public static McpConfigurationValidation Validate(string name, JsonElement raw)
    {
        ArgumentNullException.ThrowIfNull(name);
        McpConfigurationValidation Invalid(string message) => new(null, message);
        McpConfigurationValidation Error(string message) => Invalid($"server \"{name}\": {message}");
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant))
            return Invalid($"invalid server name \"{name}\" (use letters, digits, \"_\" and \"-\")");
        if (raw.ValueKind != JsonValueKind.Object) return Invalid($"server \"{name}\" must be an object");
        var resolved = McpJson.Clone(raw, resolveExposureAliases: true); var value = resolved.Value;
        var exposure = McpExposure.Codemode;
        if (value.TryGetProperty("exposure", out var suppliedExposure) && !TryExposure(suppliedExposure, out exposure))
            return Error($"exposure must be one of {Exposures}");
        var overrides = ImmutableArray.CreateBuilder<KeyValuePair<string, McpExposure>>();
        if (value.TryGetProperty("toolExposure", out var tools))
        {
            if (tools.ValueKind != JsonValueKind.Object) return Error("toolExposure must map tool names to exposures");
            foreach (var property in McpJson.Properties(tools))
            {
                if (!TryExposure(property.Value, out var selected)) return Error($"toolExposure \"{property.Name}\" must be one of {Exposures}");
                overrides.Add(new(property.Name, selected));
            }
        }
        var enabled = true;
        if (value.TryGetProperty("enabled", out var suppliedEnabled))
        {
            if (suppliedEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Error("enabled must be a boolean");
            enabled = suppliedEnabled.GetBoolean();
        }
        string? description = null;
        if (value.TryGetProperty("description", out var suppliedDescription))
        {
            if (suppliedDescription.ValueKind != JsonValueKind.String) return Error("description must be a string");
            description = suppliedDescription.GetString();
        }
        var timeout = 60d;
        if (value.TryGetProperty("timeout", out var suppliedTimeout))
        {
            if (suppliedTimeout.ValueKind != JsonValueKind.Number || !suppliedTimeout.TryGetDouble(out timeout) || !(timeout > 0))
                return Error("timeout must be a positive number of seconds");
        }
        var type = value.TryGetProperty("type", out var suppliedType) ? suppliedType : default;
        bool TypeIs(string candidate) => type.ValueKind == JsonValueKind.String && type.GetString() == candidate;
        if (TypeIs("sse")) return Error("legacy SSE transport is not supported; use the streamable HTTP URL");
        McpTransportKind transport; string? authProvider = null;
        if (value.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
            (type.ValueKind == JsonValueKind.Undefined || TypeIs("http") || TypeIs("streamable-http")))
        {
            if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return Error("url must be an http or https URL");
            if (value.TryGetProperty("headers", out var headers) && !StringRecord(headers)) return Error("headers must map names to strings");
            if (value.TryGetProperty("oauth", out var oauth) && ValidateOAuth(oauth) is { } oauthError) return Error(oauthError);
            if (value.TryGetProperty("auth", out var auth))
            {
                if (auth.ValueKind != JsonValueKind.Object || !auth.TryGetProperty("provider", out var provider) ||
                    provider.ValueKind != JsonValueKind.String || provider.GetString()!.Length == 0)
                    return Error("auth.provider must be a provider name");
                // The provider credential is sent to `url`, so it must not travel in clear text.
                if (uri.Scheme != "https" && !IsLoopbackHost(uri.Host))
                    return Error("auth requires an https URL, or http on localhost, 127.0.0.1, or [::1]");
                authProvider = provider.GetString();
            }
            transport = McpTransportKind.Http;
        }
        else if (value.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String &&
            (type.ValueKind == JsonValueKind.Undefined || TypeIs("stdio")))
        {
            if (value.TryGetProperty("args", out var arguments) && (arguments.ValueKind != JsonValueKind.Array || arguments.EnumerateArray().Any(arg => arg.ValueKind != JsonValueKind.String)))
                return Error("args must be an array of strings");
            if (value.TryGetProperty("env", out var environment) && !StringRecord(environment)) return Error("env must map names to strings");
            if (value.TryGetProperty("cwd", out var cwd) && cwd.ValueKind != JsonValueKind.String) return Error("cwd must be a string");
            transport = McpTransportKind.Stdio;
        }
        else return Invalid($"server \"{name}\" needs either \"command\" (stdio) or \"url\" (streamable HTTP)");
        return new(new(resolved, transport, exposure, enabled, timeout, overrides.ToImmutable(), description, authProvider), null);
    }

    /// <summary>Global then trusted project servers. A project entry without `command`, `url` or `type` overrides only
    /// `enabled`, `exposure` and `toolExposure` of the global server of the same name; the rest of the global entry,
    /// including credentials the project could not set itself, is kept. Project entries cannot use `auth`.</summary>
    public static McpLoadedConfiguration Load(McpConfigurationDocument? global, McpConfigurationDocument? project, bool projectTrusted)
    {
        var servers = new List<McpServerEntry>();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var errors = ImmutableArray.CreateBuilder<string>();
        bool? autoEnable = null;
        void Read(McpConfigurationDocument? input, McpConfigurationScope scope)
        {
            if (input is null) return;
            JsonDocument document;
            try { document = JsonDocument.Parse(input.Text); }
            catch (JsonException error) { errors.Add($"{input.Source}: {PiSharp.Contracts.Compatibility.JsJsonSyntax.Describe(input.Text, error.Message)}"); return; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || (root.TryGetProperty("mcpServers", out var map) && map.ValueKind != JsonValueKind.Object))
                { errors.Add($"{input.Source}: expected an object with an \"mcpServers\" object"); return; }
                if (root.TryGetProperty("autoEnableCodemode", out var preference))
                {
                    if (preference.ValueKind is JsonValueKind.True or JsonValueKind.False) autoEnable = preference.GetBoolean();
                    else errors.Add($"{input.Source}: autoEnableCodemode must be a boolean");
                }
                if (!root.TryGetProperty("mcpServers", out map)) return;
                foreach (var property in McpJson.Properties(map))
                {
                    var name = property.Name; var value = property.Value;
                    if (scope == McpConfigurationScope.Project && value.ValueKind == JsonValueKind.Object && IsOverride(value))
                    {
                        var extra = McpJson.Properties(value).Any(key => !OverrideKeys.Contains(key.Name, StringComparer.Ordinal));
                        if (!indices.TryGetValue(name, out var baseIndex))
                            errors.Add($"{input.Source}: server \"{name}\" needs \"command\" or \"url\", or a global server to override");
                        else if (extra)
                            errors.Add($"{input.Source}: server \"{name}\": an override can only set {string.Join(", ", OverrideKeys)}");
                        else
                        {
                            var merged = McpJson.Merge(servers[baseIndex].Config.Raw.Value, value);
                            var overridden = Validate(name, merged.Value);
                            if (overridden.Config is not { } overrideConfig) errors.Add($"{input.Source}: {overridden.Error}");
                            else servers[baseIndex] = servers[baseIndex] with { Config = overrideConfig, Override = input.Source };
                        }
                        continue;
                    }
                    var result = Validate(name, value);
                    if (result.Config is not { } config) { errors.Add($"{input.Source}: {result.Error}"); continue; }
                    // Names that differ only in `-` and `_` would share a namespace.
                    var clash = servers.Select(server => server.Name).FirstOrDefault(other =>
                        other != name && McpCatalogPlanner.Namespace(other) == McpCatalogPlanner.Namespace(name));
                    if (clash is not null) { errors.Add($"{input.Source}: server \"{name}\" conflicts with \"{clash}\""); continue; }
                    // A repository cannot pick where a provider credential goes.
                    if (scope == McpConfigurationScope.Project && config.Raw.Value.TryGetProperty("url", out _) &&
                        config.Raw.Value.TryGetProperty("auth", out var auth) && McpJson.Truthy(auth))
                    { errors.Add($"{input.Source}: server \"{name}\": auth is only allowed in the global mcp.json"); continue; }
                    var entry = new McpServerEntry(name, config, input.Source, scope);
                    if (indices.TryGetValue(entry.Name, out var index)) servers[index] = entry;
                    else { indices.Add(entry.Name, servers.Count); servers.Add(entry); }
                }
            }
        }
        Read(global, McpConfigurationScope.Global);
        if (projectTrusted) Read(project, McpConfigurationScope.Project);
        return new(servers.ToImmutableArray(), autoEnable, errors.ToImmutable());
    }

    public static McpExposure GetToolExposure(McpServerConfiguration config, string toolName)
    {
        ArgumentNullException.ThrowIfNull(config); ArgumentNullException.ThrowIfNull(toolName);
        foreach (var pair in config.ToolExposure) if (pair.Key == toolName) return pair.Value;
        foreach (var pair in config.ToolExposure)
        {
            if (!pair.Key.Contains('*')) continue;
            var pattern = "\\A" + string.Join("[^\\r\\n\\u2028\\u2029]*", pair.Key.Split('*').Select(Regex.Escape)) + "\\z";
            if (Regex.IsMatch(toolName, pattern, RegexOptions.CultureInvariant)) return pair.Value;
        }
        return config.Exposure;
    }

    /// <summary>Exposures a server's tools can have, known from its config before it connects.</summary>
    public static ImmutableHashSet<McpExposure> ConfiguredExposures(McpServerConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.ToolExposure.Select(pair => pair.Value).Append(config.Exposure).ToImmutableHashSet();
    }
    /// <summary>Whether some tools are declared to the model, so the first prompt waits for the server.</summary>
    public static bool HasDirectTools(McpServerConfiguration config) => ConfiguredExposures(config).Contains(McpExposure.Direct);
    /// <summary>Whether some tools are reached through codemode or tool_search.</summary>
    public static bool HasIndirectTools(McpServerConfiguration config)
    { var exposures = ConfiguredExposures(config); return exposures.Contains(McpExposure.Codemode) || exposures.Contains(McpExposure.Deferred); }

    /// <summary>Whether two entries name the same server with the same connection: the `/mcp` manager changes only `enabled`,
    /// `exposure`, `toolExposure` and `description` of a server (saveConfig), which keep its transport.</summary>
    public static bool SameConnection(McpServerEntry first, McpServerEntry second)
    {
        ArgumentNullException.ThrowIfNull(first); ArgumentNullException.ThrowIfNull(second);
        if (ReferenceEquals(first, second)) return true;
        static string Connection(McpServerEntry entry) => JsonSerializer.Serialize(McpJson.Properties(entry.Config.Raw.Value)
            .Where(property => property.Name is not ("enabled" or "exposure" or "toolExposure" or "description"))
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal));
        return first.Name == second.Name && first.Source == second.Source && first.Scope == second.Scope &&
            first.Config.Transport == second.Config.Transport && Connection(first) == Connection(second);
    }

    /// <summary>HTTP servers authenticate with OAuth unless the config supplies an `Authorization` header or `auth`.</summary>
    public static bool UsesOAuth(McpServerConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Transport != McpTransportKind.Http || config.Raw.Value.TryGetProperty("auth", out var auth) && McpJson.Truthy(auth)) return false;
        return !(config.Raw.Value.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object &&
            McpJson.Properties(headers).Any(header => header.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsLoopbackRedirectUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "http" && IsLoopbackHost(uri.Host) && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    /// <summary>`localhost`, `127.0.0.1` or `[::1]`, the hosts allowed plain http for credentials and metadata.</summary>
    public static bool IsLoopbackHost(string host) => host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]";

    private static bool IsOverride(JsonElement value) =>
        !value.TryGetProperty("command", out _) && !value.TryGetProperty("url", out _) && !value.TryGetProperty("type", out _);

    private static string? ValidateOAuth(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return "oauth must be an object";
        if (value.TryGetProperty("clientId", out var id) && id.ValueKind != JsonValueKind.String) return "oauth.clientId must be a string";
        if (value.TryGetProperty("clientSecret", out var secret) && secret.ValueKind != JsonValueKind.String) return "oauth.clientSecret must be a string";
        int? port = null;
        if (value.TryGetProperty("callbackPort", out var suppliedPort))
        {
            if (suppliedPort.ValueKind != JsonValueKind.Number || !suppliedPort.TryGetDouble(out var number) ||
                number < 1 || number > 65535 || number != Math.Truncate(number)) return "oauth.callbackPort must be a port number";
            port = (int)number;
        }
        Uri? callback = null;
        if (value.TryGetProperty("callbackUrl", out var suppliedUrl))
        {
            if (suppliedUrl.ValueKind != JsonValueKind.String || !IsLoopbackRedirectUri(suppliedUrl.GetString()!))
                return "oauth.callbackUrl must be an http URI on localhost, 127.0.0.1, or [::1] without query or fragment";
            callback = new Uri(suppliedUrl.GetString()!);
            if (!callback.IsDefaultPort && port is not null && callback.Port != port) return "oauth.callbackUrl and oauth.callbackPort name different ports";
        }
        if (value.TryGetProperty("scope", out var scope) && scope.ValueKind != JsonValueKind.String) return "oauth.scope must be a string";
        var named = value.TryGetProperty("clientName", out var clientName);
        if (named && (clientName.ValueKind != JsonValueKind.String || McpJson.JsTrim(clientName.GetString()!).Length == 0))
            return "oauth.clientName must be a non-empty string";
        if (value.TryGetProperty("clientRegistration", out var registration) &&
            !(registration.ValueKind == JsonValueKind.String && registration.GetString() == "dcr"))
        {
            if (!(registration.ValueKind == JsonValueKind.String && registration.GetString() == "cimd")) return "oauth.clientRegistration must be \"dcr\" or \"cimd\"";
            if (value.TryGetProperty("clientId", out _) || named)
                return "oauth.clientRegistration \"cimd\" cannot be combined with oauth.clientId or oauth.clientName";
            if (callback is not null && (callback.Host == "[::1]" || callback.AbsolutePath != "/callback"))
                return "oauth.clientRegistration \"cimd\" requires oauth.callbackUrl on localhost or 127.0.0.1 with path /callback";
        }
        if (value.TryGetProperty("authServerMetadataUrl", out var metadataUrl))
        {
            Uri? parsed = metadataUrl.ValueKind == JsonValueKind.String && Uri.TryCreate(metadataUrl.GetString(), UriKind.Absolute, out var candidate) ? candidate : null;
            if (parsed is null || !(parsed.Scheme == "https" || parsed.Scheme == "http" && IsLoopbackHost(parsed.Host)))
                return "oauth.authServerMetadataUrl must be an https URL, or http on localhost, 127.0.0.1, or [::1]";
        }
        return null;
    }

    private static bool StringRecord(JsonElement value) => value.ValueKind == JsonValueKind.Object && McpJson.Properties(value).All(p => p.Value.ValueKind == JsonValueKind.String);
    private static bool TryExposure(JsonElement value, out McpExposure exposure)
    {
        exposure = default;
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        exposure = text switch { "codemode" => McpExposure.Codemode, "deferred" => McpExposure.Deferred,
            "direct" => McpExposure.Direct, "hidden" => McpExposure.Hidden, _ => default };
        return text is "codemode" or "deferred" or "direct" or "hidden";
    }
}

internal static class McpJson
{
    /// <summary>Older exposure names, accepted in configs and replaced by their current name when validated.</summary>
    private static string? ExposureAlias(string? value) => value == "codemode-deferred" ? "codemode" : null;

    // JSON.parse uses last duplicate value and Object.entries enumerates array-index keys first.
    internal static IEnumerable<JsonProperty> Properties(JsonElement value)
    {
        var order = new List<string>(); var values = new Dictionary<string, JsonProperty>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) { if (!values.ContainsKey(property.Name)) order.Add(property.Name); values[property.Name] = property; }
        uint? Index(string name) => uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            index != uint.MaxValue && index.ToString(CultureInfo.InvariantCulture) == name ? index : null;
        return order.OrderBy(name => Index(name) is null ? 1 : 0).ThenBy(name => Index(name) ?? 0).Select(name => values[name]);
    }
    internal static JsonData Clone(JsonElement value, bool resolveExposureAliases = false)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            void Write(JsonElement item, int depth, string? parent)
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    foreach (var p in Properties(item))
                    {
                        writer.WritePropertyName(p.Name);
                        var alias = resolveExposureAliases && p.Value.ValueKind == JsonValueKind.String &&
                            (depth == 0 && p.Name == "exposure" || depth == 1 && parent == "toolExposure") ? ExposureAlias(p.Value.GetString()) : null;
                        if (alias is not null) writer.WriteStringValue(alias); else Write(p.Value, depth + 1, depth == 0 ? p.Name : null);
                    }
                    writer.WriteEndObject();
                }
                else if (item.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var entry in item.EnumerateArray()) Write(entry, depth + 1, null); writer.WriteEndArray(); }
                else item.WriteTo(writer);
            }
            Write(value, 0, null);
        }
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }
    /// <summary>`{ ...baseValue, ...overrides }`: base keys keep their position, new keys follow.</summary>
    internal static JsonData Merge(JsonElement baseValue, JsonElement overrides)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            var replacements = Properties(overrides).ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var names = Properties(baseValue).Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            writer.WriteStartObject();
            foreach (var property in Properties(baseValue))
            { writer.WritePropertyName(property.Name); (replacements.TryGetValue(property.Name, out var replaced) ? replaced : property.Value).WriteTo(writer); }
            foreach (var property in Properties(overrides).Where(property => !names.Contains(property.Name)))
            { writer.WritePropertyName(property.Name); property.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        return Clone(JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray())).Value);
    }
    /// <summary>JavaScript truthiness of a parsed JSON value.</summary>
    internal static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.String => value.GetString()!.Length != 0,
        JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0 && !double.IsNaN(number),
        _ => true
    };
    /// <summary>`String.prototype.trim`: ECMAScript WhiteSpace and LineTerminator code units.</summary>
    internal static string JsTrim(string value) => value.Trim(JsWhiteSpace);
    internal static readonly char[] JsWhiteSpace = [.. new[] { 0x20, 0x09, 0x0a, 0x0d, 0x0b, 0x0c, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002,
        0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000, 0xfeff }.Select(code => (char)code)];
}
