using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Mcp.Configuration;

/// <summary>Pure projection of pinned v0.99.1 mcp-servers.ts and extensions/mcp/config.ts. No I/O or interpolation.</summary>
public static class McpConfigurationReader
{
    private const string Exposures = "\"codemode\", \"codemode-deferred\", \"deferred\", \"direct\", \"hidden\"";

    public static McpConfigurationValidation Validate(string name, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(name);
        McpConfigurationValidation Invalid(string message) => new(null, message);
        McpConfigurationValidation Error(string message) => Invalid($"server \"{name}\": {message}");
        if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant))
            return Invalid($"invalid server name \"{name}\" (use letters, digits, \"_\" and \"-\")");
        if (value.ValueKind != JsonValueKind.Object) return Invalid($"server \"{name}\" must be an object");
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
        var timeout = 60d;
        if (value.TryGetProperty("timeout", out var suppliedTimeout))
        {
            if (suppliedTimeout.ValueKind != JsonValueKind.Number || !suppliedTimeout.TryGetDouble(out timeout) || !(timeout > 0))
                return Error("timeout must be a positive number of seconds");
        }
        var type = value.TryGetProperty("type", out var suppliedType) ? suppliedType : default;
        bool TypeIs(string candidate) => type.ValueKind == JsonValueKind.String && type.GetString() == candidate;
        if (TypeIs("sse")) return Error("legacy SSE transport is not supported; use the streamable HTTP URL");
        McpTransportKind transport;
        if (value.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
            (type.ValueKind == JsonValueKind.Undefined || TypeIs("http") || TypeIs("streamable-http")))
        {
            if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return Error("url must be an http or https URL");
            if (value.TryGetProperty("headers", out var headers) && !StringRecord(headers)) return Error("headers must map names to strings");
            if (value.TryGetProperty("oauth", out var oauth) && ValidateOAuth(oauth) is { } oauthError) return Error(oauthError);
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
        return new(new(McpJson.Clone(value), transport, exposure, enabled, timeout, overrides.ToImmutable()), null);
    }

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
            catch (JsonException error) { errors.Add($"{input.Source}: {error.Message}"); return; }
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
                    var result = Validate(property.Name, property.Value);
                    if (result.Config is not { } config) { errors.Add($"{input.Source}: {result.Error}"); continue; }
                    var entry = new McpServerEntry(property.Name, config, input.Source, scope);
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

    public static bool IsLoopbackRedirectUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "http" && (uri.Host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]") &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0;

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
        if (value.TryGetProperty("callbackUrl", out var suppliedUrl))
        {
            if (suppliedUrl.ValueKind != JsonValueKind.String || !IsLoopbackRedirectUri(suppliedUrl.GetString()!))
                return "oauth.callbackUrl must be an http URI on localhost, 127.0.0.1, or [::1] without query or fragment";
            var uri = new Uri(suppliedUrl.GetString()!);
            if (!uri.IsDefaultPort && port is not null && uri.Port != port) return "oauth.callbackUrl and oauth.callbackPort name different ports";
        }
        if (value.TryGetProperty("scope", out var scope) && scope.ValueKind != JsonValueKind.String) return "oauth.scope must be a string";
        return null;
    }

    private static bool StringRecord(JsonElement value) => value.ValueKind == JsonValueKind.Object && McpJson.Properties(value).All(p => p.Value.ValueKind == JsonValueKind.String);
    private static bool TryExposure(JsonElement value, out McpExposure exposure)
    {
        exposure = default;
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        exposure = text switch { "codemode" => McpExposure.Codemode, "codemode-deferred" => McpExposure.CodemodeDeferred,
            "deferred" => McpExposure.Deferred, "direct" => McpExposure.Direct, "hidden" => McpExposure.Hidden, _ => default };
        return text is "codemode" or "codemode-deferred" or "deferred" or "direct" or "hidden";
    }
}

internal static class McpJson
{
    // JSON.parse uses last duplicate value and Object.entries enumerates array-index keys first.
    internal static IEnumerable<JsonProperty> Properties(JsonElement value)
    {
        var order = new List<string>(); var values = new Dictionary<string, JsonProperty>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) { if (!values.ContainsKey(property.Name)) order.Add(property.Name); values[property.Name] = property; }
        uint? Index(string name) => uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            index != uint.MaxValue && index.ToString(CultureInfo.InvariantCulture) == name ? index : null;
        return order.OrderBy(name => Index(name) is null ? 1 : 0).ThenBy(name => Index(name) ?? 0).Select(name => values[name]);
    }
    internal static JsonData Clone(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            void Write(JsonElement item)
            {
                if (item.ValueKind == JsonValueKind.Object) { writer.WriteStartObject(); foreach (var p in Properties(item)) { writer.WritePropertyName(p.Name); Write(p.Value); } writer.WriteEndObject(); }
                else if (item.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var entry in item.EnumerateArray()) Write(entry); writer.WriteEndArray(); }
                else item.WriteTo(writer);
            }
            Write(value);
        }
        return JsonData.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
