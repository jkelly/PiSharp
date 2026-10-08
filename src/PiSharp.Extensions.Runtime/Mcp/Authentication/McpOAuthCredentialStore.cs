// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/oauth.ts McpOAuthCredentialStore,
// packages/mcp/src/oauth/provider.ts McpOAuthState.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>Caller-admitted storage of the whole credential document (the original `mcp-auth.json`). One call reads the
/// current text (null when absent) and returns the result plus the next text to write, or null to leave it unchanged,
/// while no other writer runs. No file, lock or path is acquired by this leaf.</summary>
public interface IMcpOAuthCredentialBackend
{
    T WithLock<T>(Func<string?, (T Result, string? Next)> update);
}

/// <summary>OAuth state of MCP servers in one document, keyed by server name and URL, so servers sharing a URL keep
/// separate accounts. State written by older versions under the URL alone is taken over once, deterministically, by
/// the first server that loads it; others with that URL sign in again.</summary>
public sealed class McpOAuthCredentialStore
{
    // `JSON.stringify(states, null, 2)`: two-space indentation and `\n` on every platform.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly IMcpOAuthCredentialBackend backend;
    public McpOAuthCredentialStore(IMcpOAuthCredentialBackend explicitlyAdmitted) =>
        backend = explicitlyAdmitted ?? throw new ArgumentNullException(nameof(explicitlyAdmitted));

    /// <summary>`mcp__&lt;server&gt;|&lt;url&gt;`, where names differing only in `-` and `_` are the same server, and the legacy URL key.</summary>
    public static (string Key, string LegacyKey) StoreKeys(string name, Uri serverUrl)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(serverUrl);
        if (!serverUrl.IsAbsoluteUri) throw new ArgumentException("Absolute server URL required.", nameof(serverUrl));
        var legacy = serverUrl.AbsoluteUri;
        return (McpCatalogPlanner.Namespace(name) + "|" + legacy, legacy);
    }

    public IMcpAdmittedOAuthStateStore ForServer(string name, Uri serverUrl) => new ServerStore(this, StoreKeys(name, serverUrl));

    /// <summary>The stored tokens of a server, for noticing sign-ins done elsewhere. Does not take over legacy state.</summary>
    public McpOAuthTokens? Tokens(string name, Uri serverUrl)
    {
        var (key, legacyKey) = StoreKeys(name, serverUrl);
        var states = backend.WithLock(current => (Parse(current), (string?)null));
        var stored = states[key] ?? states[legacyKey];
        return stored is JsonObject value ? TokensOf(value) : null;
    }

    /// <summary>Returns whether credentials were stored for the server. Removes legacy state the server would take over.</summary>
    public bool Remove(string name, Uri serverUrl)
    {
        var (key, legacyKey) = StoreKeys(name, serverUrl);
        return backend.WithLock(current =>
        {
            var states = Parse(current);
            var stored = states.ContainsKey(key) ? key : states.ContainsKey(legacyKey) ? legacyKey : null;
            if (stored is null) return (false, (string?)null);
            states.Remove(stored);
            return (true, Serialize(states));
        });
    }

    private sealed class ServerStore(McpOAuthCredentialStore owner, (string Key, string LegacyKey) keys) : IMcpAdmittedOAuthStateStore
    {
        public ValueTask<McpOAuthState?> LoadAsync()
        {
            // The first server to load legacy state takes it over.
            var loaded = owner.backend.WithLock(current =>
            {
                var states = Parse(current);
                if (states[keys.Key] is not null || states[keys.LegacyKey] is null) return (states[keys.Key]?.DeepClone(), (string?)null);
                var legacy = states[keys.LegacyKey]!.DeepClone();
                states.Remove(keys.LegacyKey); states[keys.Key] = legacy.DeepClone();
                return (legacy, Serialize(states));
            });
            return ValueTask.FromResult(loaded is JsonObject value ? Deserialize(value) : null);
        }

        public ValueTask SaveAsync(McpOAuthState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            var serialized = SerializeState(state);
            owner.backend.WithLock(current =>
            {
                var states = Parse(current);
                states[keys.Key] = serialized;
                return (true, Serialize(states));
            });
            return ValueTask.CompletedTask;
        }
    }

    private static JsonObject Parse(string? content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        try { return JsonNode.Parse(content) as JsonObject ?? []; }
        catch (JsonException) { return []; }
    }
    private static string Serialize(JsonObject states) => states.ToJsonString(Indented) + "\n";

    private static McpOAuthState? Deserialize(JsonObject value)
    {
        if (value["serverUrl"] is not JsonValue url || !url.TryGetValue<string>(out var serverUrl)) return null;
        return new(serverUrl, Data(value["clientInformation"]), TokensOf(value), Number(value, "tokensExpireAt"), Text(value, "codeVerifier"),
            Text(value, "oauthState"), Data(value["discovery"]));
    }
    private static McpOAuthTokens? TokensOf(JsonObject value) =>
        value["tokens"] is JsonObject stored && Text(stored, "access_token") is { } access && Text(stored, "token_type") is { } type
            ? new(access, type, Number(stored, "expires_in"), Text(stored, "scope"), Text(stored, "refresh_token"), Text(stored, "id_token")) : null;
    private static JsonObject SerializeState(McpOAuthState state)
    {
        var value = new JsonObject { ["serverUrl"] = state.ServerUrl };
        if (state.ClientInformation is { } client) value["clientInformation"] = JsonNode.Parse(client.Value.GetRawText());
        if (state.Tokens is { } tokens)
        {
            var stored = new JsonObject { ["access_token"] = tokens.AccessToken, ["token_type"] = tokens.TokenType };
            if (tokens.ExpiresIn is { } expires) stored["expires_in"] = expires;
            if (tokens.Scope is { } scope) stored["scope"] = scope;
            if (tokens.RefreshToken is { } refresh) stored["refresh_token"] = refresh;
            if (tokens.IdToken is { } id) stored["id_token"] = id;
            value["tokens"] = stored;
        }
        if (state.TokensExpireAt is { } expireAt) value["tokensExpireAt"] = expireAt;
        if (state.CodeVerifier is { } verifier) value["codeVerifier"] = verifier;
        if (state.OAuthState is { } oauthState) value["oauthState"] = oauthState;
        if (state.Discovery is { } discovery) value["discovery"] = JsonNode.Parse(discovery.Value.GetRawText());
        return value;
    }
    private static string? Text(JsonObject value, string name) => value[name] is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;
    private static double? Number(JsonObject value, string name) => value[name] is JsonValue item && item.TryGetValue<double>(out var number) ? number : null;
    private static JsonData? Data(JsonNode? value) => value is null ? null : JsonData.Parse(value.ToJsonString());
}
