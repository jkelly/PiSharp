// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/google-vertex.ts (createClient: Vertex API key or
// Application Default Credentials with project and location; resolveCustomBaseUrl, baseUrlIncludesApiVersion, buildGoogleAuthOptions)
// over @google/genai's endpoint construction and google-auth-library's ADC (authorized_user refresh, service_account JWT bearer,
// GCE metadata server). Native port; no Google SDK.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.AI.Protocols.GoogleVertex;

public sealed class GoogleCredentialsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The streamGenerateContent endpoints @google/genai builds for Vertex.</summary>
public static partial class GoogleVertexEndpoints
{
    private const string ApiVersion = "v1beta1";

    /// <summary>resolveCustomBaseUrl: a base URL without the {location} placeholder is a custom endpoint.</summary>
    public static string? CustomBaseUrl(string? baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim();
        return trimmed.Length == 0 || trimmed.Contains("{location}", StringComparison.Ordinal) ? null : trimmed;
    }

    [GeneratedRegex(@"(?:^|/)v\d+(?:beta\d*)?(?:/|$)")] private static partial Regex VersionSegment();

    /// <summary>baseUrlIncludesApiVersion: the URL path names a version segment.</summary>
    public static bool IncludesApiVersion(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? VersionSegment().IsMatch(uri.AbsolutePath) : VersionSegment().IsMatch(baseUrl);

    private static string Model(string id) => "publishers/google/models/" + Uri.EscapeDataString(id) + ":streamGenerateContent?alt=sse";

    /// <summary>Express mode (API key): no project or location in the path.</summary>
    public static Uri ApiKey(string modelId, string? baseUrl)
    {
        var custom = CustomBaseUrl(baseUrl);
        var root = (custom ?? "https://aiplatform.googleapis.com").TrimEnd('/');
        return new(root + "/" + (custom is not null && IncludesApiVersion(custom) ? "" : ApiVersion + "/") + Model(modelId));
    }

    /// <summary>ADC: the regional (or global) host and the projects/{project}/locations/{location} collection; a custom base URL is the
    /// collection itself (baseUrlResourceScope COLLECTION).</summary>
    public static Uri Adc(string modelId, string project, string location, string? baseUrl)
    {
        if (CustomBaseUrl(baseUrl) is { } custom)
            return new(custom.TrimEnd('/') + "/" + (IncludesApiVersion(custom) ? "" : ApiVersion + "/") + Model(modelId));
        var host = location == "global" ? "https://aiplatform.googleapis.com" : $"https://{location}-aiplatform.googleapis.com";
        return new($"{host}/{ApiVersion}/projects/{Uri.EscapeDataString(project)}/locations/{Uri.EscapeDataString(location)}/{Model(modelId)}");
    }
}

/// <summary>
/// Application Default Credentials: GOOGLE_APPLICATION_CREDENTIALS (or a stored credential's path), else the gcloud well-known file,
/// else the GCE metadata server. Access tokens are cached until five minutes before they expire.
/// </summary>
public sealed class GoogleApplicationDefaultCredentials(HttpMessageInvoker http, Func<string, string?> environment, string homeDirectory,
    TimeProvider? time = null, Func<string, string?>? readFile = null)
{
    public const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, string?> _read = readFile ?? (path => File.Exists(path) ? File.ReadAllText(path) : null);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Token, DateTimeOffset Expires)? _cached;

    /// <summary>The ADC file the library would read: the explicit path, else the gcloud well-known location.</summary>
    public string WellKnownFile(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath)) return explicitPath.StartsWith('~') ? homeDirectory + explicitPath[1..] : explicitPath;
        if (OperatingSystem.IsWindows() && environment("APPDATA") is { Length: > 0 } appData) return Path.Combine(appData, "gcloud", "application_default_credentials.json");
        return Path.Combine(environment("CLOUDSDK_CONFIG") ?? Path.Combine(homeDirectory, ".config", "gcloud"), "application_default_credentials.json");
    }

    public async Task<string> AccessTokenAsync(string? credentialsPath, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cached is { } cached && cached.Expires - _time.GetUtcNow() > TimeSpan.FromMinutes(5)) return cached.Token;
            var file = WellKnownFile(credentialsPath ?? environment("GOOGLE_APPLICATION_CREDENTIALS"));
            var text = _read(file);
            var (access, expiresIn) = text is null ? await MetadataAsync(token).ConfigureAwait(false) : await FromFileAsync(text, token).ConfigureAwait(false);
            _cached = (access, _time.GetUtcNow().AddSeconds(expiresIn));
            return access;
        }
        finally { _gate.Release(); }
    }

    private async Task<(string, double)> FromFileAsync(string text, CancellationToken token)
    {
        JsonObject json;
        try { json = JsonNode.Parse(text) as JsonObject ?? throw new GoogleCredentialsException("Invalid Google credentials file."); }
        catch (JsonException error) { throw new GoogleCredentialsException("Invalid Google credentials file.", error); }
        string Field(string name) => json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text
            : throw new GoogleCredentialsException($"Google credentials file is missing {name}.");
        switch (Field("type"))
        {
            case "authorized_user":
                return await TokenAsync("https://oauth2.googleapis.com/token", Form(("client_id", Field("client_id")), ("client_secret", Field("client_secret")),
                    ("refresh_token", Field("refresh_token")), ("grant_type", "refresh_token")), token).ConfigureAwait(false);
            case "service_account":
            {
                var tokenUri = json["token_uri"] is JsonValue uri && uri.TryGetValue<string>(out var configured) ? configured : "https://oauth2.googleapis.com/token";
                var now = _time.GetUtcNow().ToUnixTimeSeconds();
                var header = new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT" };
                if (json["private_key_id"] is JsonValue kid && kid.TryGetValue<string>(out var keyId)) header["kid"] = keyId;
                var claims = new JsonObject { ["iss"] = Field("client_email"), ["scope"] = Scope, ["aud"] = tokenUri, ["exp"] = now + 3600, ["iat"] = now };
                var unsigned = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
                using var rsa = RSA.Create();
                rsa.ImportFromPem(Field("private_key"));
                var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return await TokenAsync(tokenUri, Form(("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"), ("assertion", unsigned + "." + Base64Url(signature))), token).ConfigureAwait(false);
            }
            case var other: throw new GoogleCredentialsException($"Unsupported Google credentials type: {other}");
        }
    }

    private async Task<(string, double)> MetadataAsync(CancellationToken token)
    {
        var host = environment("GCE_METADATA_HOST") ?? "metadata.google.internal";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}/computeMetadata/v1/instance/service-accounts/default/token");
        request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");
        try { return Parse(await SendAsync(request, TimeSpan.FromSeconds(3), token).ConfigureAwait(false)); }
        catch (GoogleCredentialsException error) { throw new GoogleCredentialsException("Could not load the default credentials. Browse to https://cloud.google.com/docs/authentication/getting-started for more information.", error); }
    }

    private async Task<(string, double)> TokenAsync(string url, string form, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded") };
        return Parse(await SendAsync(request, TimeSpan.FromSeconds(30), token).ConfigureAwait(false));
    }

    private static (string, double) Parse(string text)
    {
        try
        {
            var json = JsonNode.Parse(text) as JsonObject;
            var access = json?["access_token"]?.GetValue<string>() ?? throw new GoogleCredentialsException("Google token response has no access_token.");
            var expires = json["expires_in"] is JsonValue value && value.TryGetValue<double>(out var seconds) ? seconds : 3600;
            return (access, expires);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new GoogleCredentialsException("Invalid Google token response.", error); }
    }

    private async Task<string> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var response = await http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new GoogleCredentialsException($"Google token request failed with status {(int)response.StatusCode}.");
            return body;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        { throw new GoogleCredentialsException("Google token request failed: " + error.Message, error); }
    }

    private static string Form(params (string Key, string Value)[] fields) =>
        string.Join('&', fields.Select(field => Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value)));
    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
