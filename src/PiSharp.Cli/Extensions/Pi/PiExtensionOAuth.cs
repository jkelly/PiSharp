// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/provider-composer.ts (ExtensionOAuthConfig,
// adaptOAuth: login callbacks onAuth/onDeviceCode/onPrompt/onProgress/onManualCodeInput/onSelect over the auth interaction, refresh,
// toAuth through getApiKey; composeOAuthAuth; the provider name falls back to oauth.name; getAllModels applies modifyModels with the
// OAuth credential) and packages/coding-agent/src/core/extensions/types.ts (ProviderConfig.oauth).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;
using PiSharp.Contracts;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>The OAuth method of a provider an extension registered with <c>oauth</c>: login, refresh and the API key run in Node.</summary>
internal sealed class PiExtensionOAuth(PiExtensionHost host, string provider, string name, bool isSubscription) : IProviderOAuth
{
    public string Name => name;
    public bool IsSubscription => isSubscription;
    public string? LoginLabel => null;

    public async Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken)
    {
        var loginId = Guid.NewGuid().ToString("N");
        host.BeginOAuthLogin(loginId, interaction);
        try
        {
            var result = await host.CallAsync("provider.oauth", new JsonObject { ["provider"] = provider, ["op"] = "login", ["loginId"] = loginId }, cancellationToken)
                .ConfigureAwait(false);
            return FromJson(result) ?? throw new InvalidOperationException($"{name} login returned no credentials");
        }
        catch (PiSharp.Compatibility.Node.Pi.PiNodeHostException) when (cancellationToken.IsCancellationRequested) { throw OAuthFlows.Cancelled(cancellationToken); }
        finally { host.EndOAuthLogin(loginId); }
    }

    public async Task<OAuthCredentialSnapshot> RefreshAsync(string providerId, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
    {
        var result = await host.CallAsync("provider.oauth", new JsonObject { ["provider"] = provider, ["op"] = "refresh", ["credentials"] = ToJson(current) }, cancellationToken)
            .ConfigureAwait(false);
        return FromJson(result) ?? throw new InvalidOperationException($"{name} refresh returned no credentials");
    }

    /// <summary>getApiKey(credentials) (synchronous upstream; answered by the Node host's event loop).</summary>
    public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = host.CallAsync("provider.oauth", new JsonObject { ["provider"] = provider, ["op"] = "apiKey", ["credentials"] = ToJson(credential) }, timeout.Token)
            .GetAwaiter().GetResult();
        return new(result is { ValueKind: JsonValueKind.String } key ? key.GetString() : null);
    }

    /// <summary>modifyModels(models, credentials): the chat models projected for the signed-in credential.</summary>
    internal JsonArray? ModifyModels(JsonArray models, OAuthCredentialSnapshot credential)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = host.CallAsync("provider.oauth", new JsonObject
        { ["provider"] = provider, ["op"] = "modifyModels", ["models"] = models.DeepClone(), ["credentials"] = ToJson(credential) }, timeout.Token).GetAwaiter().GetResult();
        return result is { ValueKind: JsonValueKind.Array } array ? JsonNode.Parse(array.GetRawText())!.AsArray() : null;
    }

    /// <summary>OAuthCredentials as the extension sees them: access, refresh, expires and the flow's other fields.</summary>
    internal static JsonObject ToJson(OAuthCredentialSnapshot credential)
    {
        var json = new JsonObject { ["type"] = "oauth" };
        foreach (var (key, value) in credential.ProviderData) json[key] = value;
        foreach (var (key, value) in credential.ProviderJson) json[key] = JsonNode.Parse(value.ToString());
        json["access"] = credential.Access; json["refresh"] = credential.Refresh; json["expires"] = credential.ExpiresUnixMilliseconds;
        return json;
    }

    internal static OAuthCredentialSnapshot? FromJson(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } credential) return null;
        string Text(string field) => credential.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
        var expires = credential.TryGetProperty("expires", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetDouble(out var number) ? (long)number : 0;
        var strings = new Dictionary<string, string>(StringComparer.Ordinal); var others = new Dictionary<string, JsonData>(StringComparer.Ordinal);
        foreach (var property in credential.EnumerateObject())
        {
            if (property.Name is "access" or "refresh" or "expires" or "type") continue;
            if (property.Value.ValueKind == JsonValueKind.String) strings[property.Name] = property.Value.GetString()!;
            else others[property.Name] = JsonData.Parse(property.Value.GetRawText());
        }
        return new(Text("access"), Text("refresh"), expires, strings, others);
    }
}

/// <summary>The model registry's view of the extensions' OAuth methods, over the run's <c>auth.json</c>.</summary>
internal sealed class PiExtensionOAuthLayer(PiExtensionHost host, string? authPath, TimeProvider? time) : PiSharp.Cli.Models.IExtensionOAuthLayer
{
    public bool Has(string provider) => host.OAuthFlow(provider) is not null;

    public string? ApiKey(string provider)
    {
        if (host.OAuthFlow(provider) is not { } flow || authPath is null) return null;
        var credential = new StoredOAuthLifecycle(new AuthJsonCredentialStore(authPath, time), flow, time).ResolveAsync(provider).GetAwaiter().GetResult();
        return credential is null ? null : flow.ToAuth(credential).ApiKey;
    }

    public IReadOnlyList<PiSharp.Cli.Models.RegistryModel>? ModifyModels(string provider, IReadOnlyList<PiSharp.Cli.Models.RegistryModel> models)
    {
        if (!host.HasModifyModels(provider) || host.OAuthFlow(provider) is not { } flow || authPath is null) return null;
        OAuthCredentialSnapshot? credential;
        try { credential = new AuthJsonCredentialStore(authPath, time).ReadAsync(provider, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OAuthLifecycleException) { return null; }
        if (credential is null) return null;
        // The extension hook is chat-only; other model types pass through untouched.
        var chat = models.Where(model => model.Type == PiSharp.AI.Catalogs.CatalogModelType.Chat).ToList();
        try
        {
            if (flow.ModifyModels(new JsonArray([.. chat.Select(model => (JsonNode)model.CloneJson())]), credential) is not { } modified) return null;
            return [.. modified.OfType<JsonObject>().Select(PiSharp.Cli.Models.RegistryModel.FromJson),
                .. models.Where(model => model.Type != PiSharp.AI.Catalogs.CatalogModelType.Chat)];
        }
        catch (Exception error) when (error is PiSharp.Compatibility.Node.Pi.PiNodeHostException or OperationCanceledException or ArgumentException or InvalidOperationException)
        { return null; }
    }
}

internal sealed partial class PiExtensionHost
{
    private readonly ConcurrentDictionary<string, IProviderAuthInteraction> _oauthLogins = new(StringComparer.Ordinal);
    internal void BeginOAuthLogin(string loginId, IProviderAuthInteraction interaction) => _oauthLogins[loginId] = interaction;
    internal void EndOAuthLogin(string loginId) => _oauthLogins.TryRemove(loginId, out _);

    /// <summary>adaptOAuth onPrompt/onManualCodeInput/onSelect: the login's interaction answers (select prompts with the option id).</summary>
    private async Task<JsonNode?> OAuthPromptAsync(JsonElement parameters, CancellationToken token)
    {
        if (!_oauthLogins.TryGetValue(parameters.GetProperty("loginId").GetString()!, out var interaction))
            throw new InvalidOperationException("The login is no longer running");
        var prompt = parameters.GetProperty("prompt");
        string? Text(string name) => prompt.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var kind = Text("type") switch { "manual_code" => AuthPromptKind.ManualCode, "select" => AuthPromptKind.Select, _ => AuthPromptKind.Text };
        IReadOnlyList<AuthPromptOption>? options = prompt.TryGetProperty("options", out var items) && items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? new AuthPromptOption(item.GetString()!, item.GetString()!)
                : new AuthPromptOption(item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : item.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "",
                    item.TryGetProperty("label", out var label) ? label.GetString() ?? "" : item.TryGetProperty("id", out var fallback) ? fallback.GetString() ?? "" : "",
                    item.TryGetProperty("description", out var description) ? description.GetString() : null))] : null;
        var answer = await interaction.PromptAsync(new(kind, Text("message") ?? "", Text("placeholder"), options), token).ConfigureAwait(false);
        return answer;
    }

    /// <summary>adaptOAuth onAuth/onDeviceCode/onProgress: the login's interaction is notified.</summary>
    private void OAuthEvent(JsonElement parameters)
    {
        if (!_oauthLogins.TryGetValue(parameters.GetProperty("loginId").GetString()!, out var interaction)) return;
        var value = parameters.GetProperty("event");
        string? Text(string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        double? Number(string name) => value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetDouble() : null;
        var kind = Text("type") switch { "auth_url" => AuthEventKind.AuthUrl, "device_code" => AuthEventKind.DeviceCode, "progress" => AuthEventKind.Progress, _ => AuthEventKind.Info };
        try
        {
            interaction.Notify(new(kind)
            {
                Url = Text("url"), Instructions = Text("instructions"), Message = Text("message"), UserCode = Text("userCode"),
                VerificationUri = Text("verificationUri") ?? Text("verificationUrl"), IntervalSeconds = Number("intervalSeconds"), ExpiresInSeconds = Number("expiresInSeconds")
            });
        }
        catch (Exception) { /* A UI failure must not end the login. */ }
    }

    private readonly ConcurrentDictionary<string, PiExtensionOAuth> _oauthFlows = new(StringComparer.Ordinal);

    /// <summary>The login entries of the providers the extensions registered with <c>oauth</c> (an extension's oauth on a built-in
    /// provider replaces its OAuth method; the built-in API-key method stays). A provider without an apiKey has no API-key login.</summary>
    internal ImmutableArray<ProviderAuthEntry> OAuthEntries()
    {
        var entries = ImmutableArray.CreateBuilder<ProviderAuthEntry>();
        foreach (var registration in ProviderRegistrations)
        {
            if (registration["name"]?.GetValue<string>() is not { } provider || registration["config"]?["oauth"] is not JsonObject oauth) continue;
            var oauthName = oauth["name"]?.GetValue<string>() ?? provider;
            var flow = _oauthFlows.AddOrUpdate(provider, _ => new(this, provider, oauthName, oauth["isSubscription"]?.GetValue<bool>() == true),
                (_, _) => new(this, provider, oauthName, oauth["isSubscription"]?.GetValue<bool>() == true));
            var builtin = ProviderAuthCatalog.Builtin(provider);
            var displayName = registration["config"]?["name"]?.GetValue<string>() ?? builtin?.Name ?? oauthName;
            var hasKey = registration["config"]?["apiKey"] is not null;
            entries.Add(new(provider, displayName, builtin?.ApiKeyName is { Length: > 0 } inherited ? inherited : hasKey ? displayName + " API key" : "",
                builtin?.EnvironmentVariables ?? [], builtin?.ApiKeyLogin ?? ApiKeyLoginKind.Secret,
                new(oauthName, flow.IsSubscription, null, _ => flow)));
        }
        return entries.ToImmutable();
    }

    /// <summary>The extension OAuth flow of a provider (null when no extension registered one).</summary>
    internal PiExtensionOAuth? OAuthFlow(string provider)
    {
        _ = OAuthEntries();
        return _oauthFlows.TryGetValue(provider, out var flow) && ProviderRegistrations.Any(registration =>
            registration["name"]?.GetValue<string>() == provider && registration["config"]?["oauth"] is JsonObject) ? flow : null;
    }

    /// <summary>Whether the provider's oauth has modifyModels.</summary>
    internal bool HasModifyModels(string provider) => ProviderRegistrations.Any(registration =>
        registration["name"]?.GetValue<string>() == provider && registration["config"]?["oauth"]?["hasModifyModels"]?.GetValue<bool>() == true);
}
