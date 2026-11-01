// Decision 0011 (run W3, CP): "Test connection" and "Discover models" of a custom provider, the credentials area's connection tester
// pattern (ConnectionTester): only when the user clicks, offline with --faux, through CredentialsOptions.CreateHttp (tests fake it), within
// CredentialsOptions.TestTimeout, and with every secret scrubbed from the answer. The request is the free model list, never a chat request:
// GET {baseUrl}/models with a Bearer key for the OpenAI, Mistral and Pi APIs (what LM Studio, Ollama, vLLM and llama.cpp serve),
// GET {baseUrl}/v1/models with x-api-key and anthropic-version for Anthropic Messages, GET {baseUrl}/models with x-goog-api-key for Google.
// The key is the one the editor would save: a key typed but not saved yet (used once, never kept), the stored vault key (only to a server
// the saved entry already uses), or the referenced variable's value on the web host. The provider's headers are sent as configured, their
// $VAR references resolved, never a !command.
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli.Models;
using PiSharp.WebAgent.Credentials;

namespace PiSharp.WebAgent.CustomProviders;

/// <summary>Tests a custom provider's connection by listing its models.</summary>
public sealed class CustomProviderTester(CustomProviderService service, ICredentialVault vault, CredentialsOptions options)
{
    /// <summary>The Anthropic API version sent with an Anthropic Messages test.</summary>
    public const string AnthropicVersion = ConnectionTester.AnthropicVersion;

    /// <summary>Tests the saved provider <paramref name="id"/> and records the result as its last test.</summary>
    /// <exception cref="KeyNotFoundException">There is no such provider.</exception>
    public async Task<CustomProviderTest> TestSavedAsync(string id, CancellationToken cancellationToken = default)
    {
        var draft = service.Draft(id) ?? throw new KeyNotFoundException($"There is no custom provider {id}.");
        var result = await TestAsync(draft, cancellationToken).ConfigureAwait(false);
        if (result.Supported) service.RecordTest(id, result);
        return result;
    }

    /// <summary>Tests <paramref name="draft"/> as the editor holds it (not saved): its base URL, API, key and headers.</summary>
    public async Task<CustomProviderTest> TestAsync(CustomProviderDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (options.Offline) return Unsupported(CredentialsOptions.OfflineMessage);
        var baseUrl = draft.BaseUrl?.Trim().TrimEnd('/') ?? "";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0)
            throw new ArgumentException("Enter the base URL first: an http or https address, such as http://localhost:1234/v1.");
        var api = draft.Api?.Trim() ?? "";
        if (!CustomProviderJson.Apis.Contains(api)) throw new ArgumentException("Choose an API type first.");
        var (key, keyError) = await KeyAsync(draft, cancellationToken).ConfigureAwait(false);
        if (keyError is not null) return Failed(keyError, null);
        var secrets = new List<string?> { key };
        using var request = Request(api, baseUrl, key, draft.AuthHeader);
        var resolver = new ConfigValueResolver(options.Environment, _ => null);
        var fileHeaders = service.Current.Document["providers"]?[draft.OriginalId ?? ""]?["headers"] as System.Text.Json.Nodes.JsonObject;
        foreach (var header in draft.Headers ?? [])
        {
            var name = header.Name?.Trim();
            if (!CustomProviderJson.IsHeaderName(name)) continue;
            var raw = header.Value ?? (header.Hidden && fileHeaders?[name!]?.GetValueKind() == JsonValueKind.String ? fileHeaders[name!]!.GetValue<string>() : null);
            if (raw is null || ConfigValueResolver.IsCommand(raw) || resolver.Resolve(raw) is not { } value || value.Any(character => character is '\r' or '\n')) continue;
            if (CustomProviderJson.IsSecretHeader(name!) || raw != value) secrets.Add(value);
            request.Headers.Remove(name!);
            request.Headers.TryAddWithoutValidation(name!, value);
        }
        using var http = options.CreateHttp();
        return await SendAsync(http, request, url, [.. secrets], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The key a test sends (or null for none), or why there is none.</summary>
    private async Task<(string? Key, string? Error)> KeyAsync(CustomProviderDraft draft, CancellationToken token)
    {
        switch (draft.Key?.Source?.Trim() ?? "none")
        {
            case "env":
                var variable = draft.Key!.Variable?.Trim().TrimStart('$').Trim('{', '}');
                if (!CustomProviderJson.IsVariable(variable)) return (null, "Enter the environment variable's name first.");
                return options.Environment(variable!) is { Length: > 0 } value ? (value, null) : (null, $"The {variable} environment variable is not set on this machine, so there is no key to test.");
            case "stored":
                if (draft.Key!.ApiKey?.Trim() is { Length: > 0 } typed) return (typed, null);
                var id = draft.OriginalId is { Length: > 0 } original ? original : draft.Id?.Trim() ?? "";
                if (service.StoredKey(id) is not { } account) return (null, "Paste the API key to test it.");
                // The stored key is tested only against a server the saved entry already uses (as a save keeps it only then).
                if (!service.StoredKeyMayGo(draft.OriginalId, draft.BaseUrl?.Trim().TrimEnd('/') ?? "")) return (null, CustomProviderService.StoredKeyElsewhere);
                try { return ((await vault.ReadAsync(account.Id, token).ConfigureAwait(false))?.ApiKey, null); }
                catch (CredentialUnavailableException error) { return (null, error.Message); }
            default:
                return (null, null);
        }
    }

    /// <summary>The model list request of <paramref name="api"/> at <paramref name="baseUrl"/>.</summary>
    public static HttpRequestMessage Request(string api, string baseUrl, string? key, bool authHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, api switch
        {
            "anthropic-messages" => baseUrl + (baseUrl.EndsWith("/v1", StringComparison.Ordinal) ? "" : "/v1") + "/models",
            _ => baseUrl + "/models",
        });
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (key is null) return request;
        switch (api)
        {
            case "anthropic-messages":
                if (!authHeader) request.Headers.TryAddWithoutValidation("x-api-key", key);
                request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                break;
            case "google-generative-ai":
                if (!authHeader) request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                break;
            default:
                authHeader = true;
                break;
        }
        if (authHeader) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        return request;
    }

    private async Task<CustomProviderTest> SendAsync(HttpMessageInvoker http, HttpRequestMessage request, Uri baseUrl, string?[] secrets, CancellationToken token)
    {
        var endpoint = $"GET {request.RequestUri!.GetLeftPart(UriPartial.Path)}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.TestTimeout);
        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            var elapsed = clock.ElapsedMilliseconds;
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var models = ModelIds(body);
                return new CustomProviderTest(true, models.Count == 0 ? "Connected, but the provider listed no models." : "Connected.")
                {
                    StatusCode = status, LatencyMs = elapsed, Endpoint = endpoint, Models = models, TestedAt = options.Time.GetUtcNow(),
                };
            }
            var message = SecretText.Scrub($"{status} {ErrorText(body) ?? response.ReasonPhrase ?? ""}".Trim(), secrets);
            if (status is 401 or 403) message += " The provider rejected the key.";
            else if (status == 404) message += " Check the base URL: the model list was not found there.";
            return new CustomProviderTest(false, message) { StatusCode = status, LatencyMs = elapsed, Endpoint = endpoint, TestedAt = options.Time.GetUtcNow() };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Failed($"No answer within {options.TestTimeout.TotalSeconds:0} seconds.", endpoint);
        }
        catch (HttpRequestException error)
        {
            var refused = error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused } || error.HttpRequestError == HttpRequestError.ConnectionError;
            var text = refused
                ? $"Connection refused. Nothing is listening at {baseUrl.Host}:{baseUrl.Port}{(CustomProviderJson.IsLoopback(baseUrl.ToString()) ? "; start the server, then test again." : ".")}"
                : "Could not reach the provider: " + error.Message;
            return Failed(SecretText.Scrub(text, secrets), endpoint);
        }
    }

    /// <summary>The model ids of a model list: OpenAI and Anthropic <c>data[].id</c>, Google <c>models[].name</c> (without <c>models/</c>),
    /// Ollama <c>models[].name</c> or <c>models[].model</c>.</summary>
    internal static IReadOnlyList<string> ModelIds(string body)
    {
        var ids = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            JsonElement list = default;
            if (root.ValueKind == JsonValueKind.Array) list = root;
            else if (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("data", out list) || root.TryGetProperty("models", out list)) && list.ValueKind == JsonValueKind.Array) { }
            else return ids;
            foreach (var item in list.EnumerateArray())
            {
                string? id = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (item.ValueKind == JsonValueKind.Object)
                    foreach (var name in new[] { "id", "name", "model" })
                        if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) { id = value.GetString(); break; }
                if (id is { Length: > 0 and <= 200 } && !ids.Contains(id)) ids.Add(id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id);
                if (ids.Count >= 500) break;
            }
        }
        catch (JsonException) { /* Not a model list: no ids. */ }
        return ids;
    }

    private static string? ErrorText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error)) return null;
            if (error.ValueKind == JsonValueKind.String) return error.GetString();
            return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private CustomProviderTest Failed(string message, string? endpoint) => new(false, message) { Endpoint = endpoint, TestedAt = options.Time.GetUtcNow() };

    private CustomProviderTest Unsupported(string message) => new(false, message) { Supported = false, TestedAt = options.Time.GetUtcNow() };
}
