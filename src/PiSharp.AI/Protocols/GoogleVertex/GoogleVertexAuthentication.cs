// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/google-vertex.ts (createClient: Vertex API key or
// Application Default Credentials with project and location; resolveCustomBaseUrl, baseUrlIncludesApiVersion, buildGoogleAuthOptions)
// over @google/genai's endpoint construction and google-auth-library 10.6.2 (Apache-2.0) ADC: googleauth (fromJSON,
// fromImpersonatedJSON), refreshclient (authorized_user refresh), jwtclient (service_account JWT bearer), computeclient (GCE metadata
// server), impersonated (impersonated_service_account), externalAccountAuthorizedUserClient (external_account_authorized_user),
// externalclient + baseexternalclient + stscredentials + oauth2common (external_account STS token exchange and
// service_account_impersonation_url), identitypoolclient + filesubjecttokensupplier + urlsubjecttokensupplier +
// certificatesubjecttokensupplier (identity pool sources), awsclient + awsrequestsigner (through PiSharp's AwsSigV4) +
// defaultawssecuritycredentialssupplier (AWS source), pluggable-auth-client + pluggable-auth-handler + executable-response (executable
// source). Native port; no Google SDK.
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.AI.Protocols.Bedrock;
using AccessToken = (string Token, System.DateTimeOffset Expires);
using SubjectSource = System.Func<System.Threading.CancellationToken,
    System.Threading.Tasks.Task<(string Subject, System.Security.Cryptography.X509Certificates.X509Certificate2? Mtls)>>;

namespace PiSharp.AI.Protocols.GoogleVertex;

public sealed class GoogleCredentialsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One pluggable-auth executable run: the parsed command, the GOOGLE_EXTERNAL_ACCOUNT_* variables added to the inherited
/// environment, and the configured timeout.</summary>
public sealed record GoogleExecutableRequest(string Command, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout);

/// <summary>The exit code (null when the run was killed at the timeout) and the combined stdout and stderr text.</summary>
public sealed record GoogleExecutableResult(int? ExitCode, string Output);

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
/// else the GCE metadata server. The file may be authorized_user, service_account, impersonated_service_account, external_account
/// (identity pool file/url/certificate, aws, executable) or external_account_authorized_user. Access tokens are cached until five
/// minutes before they expire (tokens the server returns without a lifetime never expire, as in the library).
/// </summary>
public sealed partial class GoogleApplicationDefaultCredentials(HttpMessageInvoker http, Func<string, string?> environment, string homeDirectory,
    TimeProvider? time = null, Func<string, string?>? readFile = null)
{
    public const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    /// <summary>The x-goog-api-client prefix of the STS exchange (the library sends gl-node/{node version} auth/{its version}).</summary>
    public static string MetricsPrefix { get; } = $"gl-dotnet/{System.Environment.Version} auth/10.6.2";
    private const string DefaultUniverse = "googleapis.com";
    private const string TokenExchangeGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    private const string SamlType = "urn:ietf:params:oauth:token-type:saml2", IdTokenType = "urn:ietf:params:oauth:token-type:id_token",
        JwtType = "urn:ietf:params:oauth:token-type:jwt";
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, string?> _read = readFile ?? (path => File.Exists(path) ? File.ReadAllText(path) : null);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _rotatedRefreshTokens = new(StringComparer.Ordinal);
    private AccessToken? _cached;

    /// <summary>Runs a pluggable-auth executable (credential_source.executable); replaceable for hosts and tests.</summary>
    public Func<GoogleExecutableRequest, CancellationToken, Task<GoogleExecutableResult>> RunExecutable { get; init; } = RunProcessAsync;

    /// <summary>The mutual-TLS client for a credential_source.certificate exchange (STS and impersonation); disposed after the refresh.</summary>
    public Func<X509Certificate2, HttpMessageInvoker> MtlsHttp { get; init; } = CreateMtlsInvoker;

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
            var fresh = text is null ? await MetadataAsync(token).ConfigureAwait(false) : await FromJsonAsync(ParseFile(text), token).ConfigureAwait(false);
            _cached = fresh;
            return fresh.Token;
        }
        finally { _gate.Release(); }
    }

    private static JsonObject ParseFile(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject ?? throw new GoogleCredentialsException("Invalid Google credentials file."); }
        catch (JsonException error) { throw new GoogleCredentialsException("Invalid Google credentials file.", error); }
    }

    /// <summary>GoogleAuth.fromJSON.</summary>
    private Task<AccessToken> FromJsonAsync(JsonObject json, CancellationToken token) => Required(json, "type") switch
    {
        "authorized_user" => TokenAsync("https://oauth2.googleapis.com/token", Form(("client_id", Required(json, "client_id")),
            ("client_secret", Required(json, "client_secret")), ("refresh_token", Required(json, "refresh_token")), ("grant_type", "refresh_token")), token),
        "service_account" => ServiceAccountAsync(json, token),
        "impersonated_service_account" => ImpersonatedAsync(json, token),
        "external_account" => ExternalAccountAsync(json, token),
        "external_account_authorized_user" => ExternalAuthorizedUserAsync(json, token),
        var other => throw new GoogleCredentialsException($"Unsupported Google credentials type: {other}")
    };

    private Task<AccessToken> ServiceAccountAsync(JsonObject json, CancellationToken token)
    {
        var tokenUri = json["token_uri"] is JsonValue uri && uri.TryGetValue<string>(out var configured) ? configured : "https://oauth2.googleapis.com/token";
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var header = new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT" };
        if (json["private_key_id"] is JsonValue kid && kid.TryGetValue<string>(out var keyId)) header["kid"] = keyId;
        var claims = new JsonObject { ["iss"] = Required(json, "client_email"), ["scope"] = Scope, ["aud"] = tokenUri, ["exp"] = now + 3600, ["iat"] = now };
        var unsigned = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(Required(json, "private_key"));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return TokenAsync(tokenUri, Form(("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"), ("assertion", unsigned + "." + Base64Url(signature))), token);
    }

    private async Task<AccessToken> MetadataAsync(CancellationToken token)
    {
        var host = environment("GCE_METADATA_HOST") ?? "metadata.google.internal";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}/computeMetadata/v1/instance/service-accounts/default/token");
        request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");
        try { return Lifetime(await SendAsync(request, TimeSpan.FromSeconds(3), token).ConfigureAwait(false)); }
        catch (GoogleCredentialsException error) { throw new GoogleCredentialsException("Could not load the default credentials. Browse to https://cloud.google.com/docs/authentication/getting-started for more information.", error); }
    }

    private async Task<AccessToken> TokenAsync(string url, string form, CancellationToken token)
    {
        using var request = FormRequest(url, form);
        return Lifetime(await SendAsync(request, TimeSpan.FromSeconds(30), token).ConfigureAwait(false));
    }

    private AccessToken Lifetime(string text)
    {
        try
        {
            var json = JsonNode.Parse(text) as JsonObject;
            var access = json?["access_token"]?.GetValue<string>() ?? throw NoAccessToken();
            return (access, Expiry(json["expires_in"] is JsonValue value && value.TryGetValue<double>(out var seconds) ? seconds : 3600));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new GoogleCredentialsException("Invalid Google token response.", error); }
    }

    // ---- impersonated_service_account (googleauth.fromImpersonatedJSON + impersonated.refreshToken) ----

    private async Task<AccessToken> ImpersonatedAsync(JsonObject json, CancellationToken token)
    {
        if (!Truthy(json["source_credentials"])) throw new GoogleCredentialsException("The incoming JSON object does not contain a source_credentials field");
        if (!Truthy(json["service_account_impersonation_url"]))
            throw new GoogleCredentialsException("The incoming JSON object does not contain a service_account_impersonation_url field");
        var source = json["source_credentials"] as JsonObject ?? throw new GoogleCredentialsException("Invalid Google credentials file.");
        var url = JsString(json["service_account_impersonation_url"])!;
        if (url.Length > 256) throw new GoogleCredentialsException($"Target principal is too long: {url}");
        var target = TargetPrincipal().Match(url) is { Success: true } match ? match.Groups["target"].Value
            : throw new GoogleCredentialsException($"Cannot extract target principal from {url}");
        var universe = Universe(json);
        if (Str(json, "universe_domain") is { Length: > 0 } explicitUniverse && Universe(source) != explicitUniverse)
            throw new GoogleCredentialsException($"Universe domain {Universe(source)} in source credentials does not match {explicitUniverse} universe domain set for impersonated credentials.");
        var endpoint = JsString(json["endpoint"]) ?? $"https://iamcredentials.{universe}";
        var body = $"{{\"delegates\":{(json["delegates"] ?? new JsonArray()).ToJsonString(Relaxed)},\"scope\":[{JsQuote(Scope)}],\"lifetime\":{JsQuote((JsString(json["lifetime"]) ?? "3600") + "s")}}}";
        string? mapped = null;
        try
        {
            var sourceToken = await FromJsonAsync(source, token).ConfigureAwait(false);
            using var request = JsonRequest($"{endpoint}/v1/projects/-/serviceAccounts/{target}:generateAccessToken", body);
            request.Headers.TryAddWithoutValidation("authorization", "Bearer " + sourceToken.Token);
            if (Str(source, "quota_project_id") is { Length: > 0 } quota) request.Headers.TryAddWithoutValidation("x-goog-user-project", quota);
            var reply = await RawAsync(http, request, token).ConfigureAwait(false);
            if (!reply.Ok)
            {
                var error = (reply.IsJson ? Object(reply.Body) : null)?["error"] as JsonObject;
                if (Truthy(error?["status"]) && Truthy(error?["message"])) mapped = $"{JsString(error!["status"])}: unable to impersonate: {JsString(error["message"])}";
                throw new GoogleCredentialsException(mapped ?? GaxiosMessage(reply, json: null));
            }
            var data = Object(reply.Body);
            return (JsString(data?["accessToken"]) ?? throw NoAccessToken(), ExpireTime(data?["expireTime"]));
        }
        catch (Exception error) when (mapped is null && error is not OperationCanceledException)
        { throw new GoogleCredentialsException("unable to impersonate: Error: " + error.Message, error); }
    }

    /// <summary>The universe a credential resolves (AuthClient universe_domain, impersonated falls back to its source).</summary>
    private static string Universe(JsonObject json) => Str(json, "universe_domain") is { Length: > 0 } universe ? universe
        : Str(json, "type") == "impersonated_service_account" && json["source_credentials"] is JsonObject source ? Universe(source) : DefaultUniverse;

    // ---- external_account_authorized_user ----

    private async Task<AccessToken> ExternalAuthorizedUserAsync(JsonObject json, CancellationToken token)
    {
        var universe = JsString(json["universe_domain"]) is { Length: > 0 } domain ? domain : DefaultUniverse;
        var tokenUrl = JsString(json["token_url"]) ?? $"https://sts.{universe}/v1/oauthtoken";
        var original = Required(json, "refresh_token");
        using var request = FormRequest(tokenUrl, Form(("grant_type", "refresh_token"), ("refresh_token", _rotatedRefreshTokens.GetValueOrDefault(original, original))));
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("authorization", Basic(JsString(json["client_id"]) ?? "undefined", JsString(json["client_secret"])));
        var data = Object(await OAuthAsync(http, request, token).ConfigureAwait(false));
        var access = JsString(data?["access_token"]) ?? throw NoAccessToken();
        if (data!.TryGetPropertyValue("refresh_token", out var rotated) && JsString(rotated) is { } next) _rotatedRefreshTokens[original] = next;
        return (access, data.TryGetPropertyValue("expires_in", out var expiresIn) ? Expiry(JsNumber(expiresIn)) : DateTimeOffset.MaxValue);
    }

    // ---- external_account (BaseExternalAccountClient + StsCredentials) ----

    private async Task<AccessToken> ExternalAccountAsync(JsonObject json, CancellationToken token)
    {
        var clientId = Str(json, "client_id"); var clientSecret = Str(json, "client_secret");
        var tokenUrl = Str(json, "token_url") ?? $"https://sts.{Str(json, "universe_domain") ?? DefaultUniverse}/v1/token";
        var subjectTokenType = Str(json, "subject_token_type");
        var workforceUserProject = Str(json, "workforce_pool_user_project");
        var impersonationUrl = Str(json, "service_account_impersonation_url");
        var lifetimeNode = Opt(Opt(json, "service_account_impersonation") as JsonObject, "token_lifetime_seconds");
        var configLifetime = Truthy(lifetimeNode);
        var audience = Str(json, "audience") ?? throw Missing("audience");
        if (!string.IsNullOrEmpty(workforceUserProject) && !WorkforceAudience().IsMatch(audience))
            throw new GoogleCredentialsException("workforcePoolUserProject should not be set for non-workforce pool credentials.");
        // ExternalAccountClient.fromJSON: environment_id selects AwsClient, executable PluggableAuthClient, anything else IdentityPoolClient.
        var raw = json["credential_source"] as JsonObject;
        var (sourceType, subject) = Truthy(raw?["environment_id"]) ? Aws(json, audience)
            : Truthy(raw?["executable"]) ? Executable(raw!, audience, subjectTokenType, impersonationUrl) : IdentityPool(json);
        var (subjectToken, certificate) = await subject(token).ConfigureAwait(false);
        using var mtls = certificate is null ? null : MtlsHttp(certificate);
        var client = mtls ?? http;
        List<(string, string)> form = [("grant_type", TokenExchangeGrant), ("audience", audience), ("scope", Scope), ("requested_token_type", AccessTokenType), ("subject_token", subjectToken)];
        if (subjectTokenType is not null) form.Add(("subject_token_type", subjectTokenType));
        // Client authentication takes priority over the workforce pool user project.
        if (string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(workforceUserProject)) form.Add(("options", "{\"userProject\":" + JsQuote(workforceUserProject) + "}"));
        using var exchange = FormRequest(tokenUrl, Form(form));
        exchange.Headers.TryAddWithoutValidation("x-goog-api-client",
            $"{MetricsPrefix} google-byoid-sdk source/{sourceType} sa-impersonation/{Bool(impersonationUrl is not null)} config-lifetime/{Bool(configLifetime)}");
        exchange.Headers.TryAddWithoutValidation("accept", "application/json");
        if (!string.IsNullOrEmpty(clientId)) exchange.Headers.TryAddWithoutValidation("authorization", Basic(clientId, clientSecret));
        var sts = Object(await OAuthAsync(client, exchange, token).ConfigureAwait(false));
        var access = JsString(sts?["access_token"]) ?? throw NoAccessToken();
        if (string.IsNullOrEmpty(impersonationUrl)) return (access, Truthy(sts!["expires_in"]) ? Expiry(JsNumber(sts["expires_in"])) : DateTimeOffset.MaxValue);
        // getImpersonatedAccessToken: iamcredentials generateAccessToken with the configured (or default 3600 s) lifetime.
        var lifetime = (configLifetime ? JsString(lifetimeNode) : "3600") + "s";
        using var request = JsonRequest(impersonationUrl, $"{{\"scope\":[{JsQuote(Scope)}],\"lifetime\":{JsQuote(lifetime)}}}");
        request.Headers.TryAddWithoutValidation("authorization", "Bearer " + access);
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        var reply = await RawAsync(client, request, token).ConfigureAwait(false);
        if (!reply.Ok) throw new GoogleCredentialsException(GaxiosMessage(reply, json: true));
        var data = Object(reply.Body);
        return (JsString(data?["accessToken"]) ?? throw NoAccessToken(), ExpireTime(data?["expireTime"]));
    }

    // ---- identity pool: file, url and certificate sources ----

    private (string, SubjectSource) IdentityPool(JsonObject json)
    {
        var source = Opt(json, "credential_source");
        if (!Truthy(source)) throw new GoogleCredentialsException("A credential source or subject token supplier must be specified.");
        var options = source as JsonObject;
        var format = Opt(options, "format") as JsonObject;
        var formatType = Str(format, "type") is { Length: > 0 } type ? type : "text";
        var field = Str(format, "subject_token_field_name");
        if (formatType is not ("json" or "text")) throw new GoogleCredentialsException($"Invalid credential_source format \"{formatType}\"");
        if (formatType == "json" && string.IsNullOrEmpty(field)) throw new GoogleCredentialsException("Missing subject_token_field_name for JSON credential_source format");
        var file = Str(options, "file"); var url = Str(options, "url"); var certificate = Opt(options, "certificate");
        bool hasFile = !string.IsNullOrEmpty(file), hasUrl = !string.IsNullOrEmpty(url), hasCertificate = Truthy(certificate);
        if (hasFile && hasUrl || hasUrl && hasCertificate || hasFile && hasCertificate || !(hasFile || hasUrl || hasCertificate))
            throw new GoogleCredentialsException("No valid Identity Pool \"credential_source\" provided, must be either file, url, or certificate.");
        if (hasFile) return ("file", _ => Task.FromResult<(string, X509Certificate2?)>((FileSubject(file!, formatType, field), null)));
        if (hasUrl) return ("url", async token => (await UrlSubjectAsync(url!, Opt(options, "headers") as JsonObject, formatType, field, token).ConfigureAwait(false), null));
        var settings = certificate as JsonObject;
        var useDefault = Truthy(settings?["use_default_certificate_config"]);
        var location = JsString(settings?["certificate_config_location"]);
        if (!useDefault && string.IsNullOrEmpty(location))
            throw new GoogleCredentialsException("Either `useDefaultCertificateConfig` must be true or a `certificateConfigLocation` must be provided.");
        if (useDefault && !string.IsNullOrEmpty(location))
            throw new GoogleCredentialsException("Both `useDefaultCertificateConfig` and `certificateConfigLocation` cannot be provided.");
        var trustChain = JsString(settings?["trust_chain_path"]);
        return ("certificate", _ => Task.FromResult<(string, X509Certificate2?)>(CertificateSubject(location, trustChain)));
    }

    private string FileSubject(string path, string formatType, string? field)
    {
        var text = _read(path) ?? throw new GoogleCredentialsException($"The file at {path} does not exist, or it is not a file.");
        JsonNode? subject;
        if (formatType == "text") subject = JsonValue.Create(text);
        else
            try { subject = (JsonNode.Parse(text) as JsonObject)?[field!]; }
            catch (JsonException error) { throw new GoogleCredentialsException(error.Message, error); }
        return Truthy(subject) ? JsString(subject)! : throw new GoogleCredentialsException("Unable to parse the subject_token from the credential_source file");
    }

    private async Task<string> UrlSubjectAsync(string url, JsonObject? headers, string formatType, string? field, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null) foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, JsString(value) ?? "null");
        var body = await GaxiosAsync(http, request, formatType == "json", token).ConfigureAwait(false);
        var subject = formatType == "text" ? JsonValue.Create(body) : Object(body)?[field!];
        return Truthy(subject) ? JsString(subject)! : throw new GoogleCredentialsException("Unable to parse the subject_token from the credential_source URL");
    }

    /// <summary>CertificateSubjectTokenSupplier: the leaf (plus trust chain) as a JSON array of base64 DER certificates; the leaf and its
    /// key become the mTLS client certificate.</summary>
    private (string, X509Certificate2?) CertificateSubject(string? location, string? trustChainPath)
    {
        const string Variable = "GOOGLE_API_CERTIFICATE_CONFIG";
        string configPath; string? text;
        if (!string.IsNullOrEmpty(location))
            text = _read(configPath = location) ?? throw new GoogleCredentialsException($"Provided certificate config path is invalid: {location}");
        else if (environment(Variable) is { Length: > 0 } fromEnvironment)
            text = _read(configPath = fromEnvironment) ?? throw new GoogleCredentialsException($"Path from environment variable \"{Variable}\" is invalid: {fromEnvironment}");
        else
        {
            configPath = Path.Combine(environment("CLOUDSDK_CONFIG") is { Length: > 0 } sdk ? sdk
                : OperatingSystem.IsWindows() ? Path.Combine(environment("APPDATA") ?? "", "gcloud") : Path.Combine(homeDirectory, ".config", "gcloud"), "certificate_config.json");
            text = _read(configPath) ?? throw new GoogleCredentialsException("Could not find certificate configuration file. Searched override path, " +
                $"the \"{Variable}\" env var, and the gcloud path ({configPath}).");
        }
        string certPath, keyPath;
        try
        {
            var workload = ((JsonNode.Parse(text) as JsonObject)?["cert_configs"] as JsonObject)?["workload"] as JsonObject;
            if (JsString(workload?["cert_path"]) is not { Length: > 0 } cert || JsString(workload?["key_path"]) is not { Length: > 0 } key)
                throw new GoogleCredentialsException($"Certificate config file ({configPath}) is missing required \"cert_path\" or \"key_path\" in the workload config.");
            (certPath, keyPath) = (cert, key);
        }
        catch (JsonException error) { throw new GoogleCredentialsException($"Failed to parse certificate config from {configPath}: {error.Message}", error); }
        var certText = _read(certPath) ?? throw new GoogleCredentialsException($"Failed to read certificate file at {certPath}: file not found");
        X509Certificate2 leaf, mtls;
        try { leaf = X509Certificate2.CreateFromPem(certText); }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        { throw new GoogleCredentialsException($"Failed to read certificate file at {certPath}: {error.Message}", error); }
        var keyText = _read(keyPath) ?? throw new GoogleCredentialsException($"Failed to read private key file at {keyPath}: file not found");
        try { mtls = X509Certificate2.CreateFromPem(certText, keyText); }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        { throw new GoogleCredentialsException($"Failed to read private key file at {keyPath}: {error.Message}", error); }
        if (string.IsNullOrEmpty(trustChainPath)) return ($"[{JsQuote(Convert.ToBase64String(leaf.RawData))}]", mtls);
        var chainText = _read(trustChainPath) ?? throw new GoogleCredentialsException($"Failed to process certificate chain from {trustChainPath}: file not found");
        var chain = new List<X509Certificate2>();
        foreach (Match block in PemCertificate().Matches(chainText))
            try { chain.Add(X509Certificate2.CreateFromPem(block.Value)); }
            catch (Exception error) when (error is CryptographicException or ArgumentException)
            { throw new GoogleCredentialsException($"Failed to parse certificate at index {chain.Count} in trust chain file {trustChainPath}: {error.Message}", error); }
        var leafIndex = chain.FindIndex(entry => entry.RawData.AsSpan().SequenceEqual(leaf.RawData));
        if (leafIndex > 0) throw new GoogleCredentialsException($"Leaf certificate exists in the trust chain but is not the first entry (found at index {leafIndex}).");
        List<X509Certificate2> final = leafIndex == 0 ? chain : [leaf, .. chain];
        return ("[" + string.Join(',', final.Select(entry => JsQuote(Convert.ToBase64String(entry.RawData)))) + "]", mtls);
    }

    // ---- aws source (AwsClient + DefaultAwsSecurityCredentialsSupplier + AwsRequestSigner) ----

    private (string, SubjectSource) Aws(JsonObject json, string audience)
    {
        var options = Opt(json, "credential_source") as JsonObject;
        var environmentId = Str(options, "environment_id"); var regionUrl = Str(options, "region_url"); var credentialsUrl = Str(options, "url");
        var sessionTokenUrl = Str(options, "imdsv2_session_token_url"); var verificationUrl = Str(options, "regional_cred_verification_url");
        var match = AwsEnvironmentId().Match(environmentId ?? "");
        if (!match.Success || string.IsNullOrEmpty(verificationUrl)) throw new GoogleCredentialsException("No valid AWS \"credential_source\" provided");
        if (double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) != 1)
            throw new GoogleCredentialsException($"aws version \"{match.Groups[2].Value}\" is not supported in the current build.");
        return ("aws", async token => (await AwsSubjectAsync(audience, regionUrl, credentialsUrl, sessionTokenUrl, verificationUrl, token).ConfigureAwait(false), null));
    }

    private async Task<string> AwsSubjectAsync(string audience, string? regionUrl, string? credentialsUrl, string? sessionTokenUrl, string verificationUrl, CancellationToken token)
    {
        // IMDSv2: every metadata lookup fetches its own session token when imdsv2_session_token_url is configured.
        async Task<List<(string, string)>> MetadataHeadersAsync()
        {
            if (string.IsNullOrEmpty(sessionTokenUrl)) return [];
            using var put = new HttpRequestMessage(HttpMethod.Put, sessionTokenUrl);
            put.Headers.TryAddWithoutValidation("x-aws-ec2-metadata-token-ttl-seconds", "300");
            return [("x-aws-ec2-metadata-token", await GaxiosAsync(http, put, false, token).ConfigureAwait(false))];
        }
        async Task<string> GetAsync(string url, List<(string, string)> headers, bool json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            return await GaxiosAsync(http, request, json, token).ConfigureAwait(false);
        }
        var region = NonEmpty(environment("AWS_REGION")) ?? NonEmpty(environment("AWS_DEFAULT_REGION"));
        if (region is null)
        {
            var headers = await MetadataHeadersAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(regionUrl)) throw new GoogleCredentialsException("Unable to determine AWS region due to missing \"options.credential_source.region_url\"");
            var zone = await GetAsync(regionUrl, headers, false).ConfigureAwait(false);
            region = zone.Length == 0 ? "" : zone[..^1]; // us-east-2b -> us-east-2
        }
        AwsCredentials credentials;
        if (NonEmpty(environment("AWS_ACCESS_KEY_ID")) is { } keyId && NonEmpty(environment("AWS_SECRET_ACCESS_KEY")) is { } secret)
            credentials = new(keyId, secret, environment("AWS_SESSION_TOKEN"));
        else
        {
            var headers = await MetadataHeadersAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(credentialsUrl)) throw new GoogleCredentialsException("Unable to determine AWS role name due to missing \"options.credential_source.url\"");
            var role = await GetAsync(credentialsUrl, headers, false).ConfigureAwait(false);
            var data = Object(await GetAsync($"{credentialsUrl}/{role}", headers, true).ConfigureAwait(false));
            credentials = new(JsString(data?["AccessKeyId"]) ?? "undefined", JsString(data?["SecretAccessKey"]) ?? "undefined", JsString(data?["Token"]));
        }
        // A signed (but unsent) STS GetCallerIdentity POST: host, x-amz-date and x-amz-security-token are signed, no payload checksum header.
        var url = ReplaceFirst(verificationUrl, "{region}", region);
        var uri = new Uri(url);
        var query = uri.Query.Length <= 1 ? [] : uri.Query[1..].Split('&').Select(pair => pair.Split('=', 2))
            .Select(pair => new KeyValuePair<string, string>(Uri.UnescapeDataString(pair[0]), pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "")).ToList();
        var signed = AwsSigV4.Sign(new AwsSigningRequest("POST", uri.Authority, uri.AbsolutePath, query, [], []), credentials, region,
            uri.Authority.Split('.')[0], _time.GetUtcNow(), applyChecksum: false);
        var headersOut = new SortedDictionary<string, string>(StringComparer.Ordinal)
        { ["authorization"] = signed.Authorization, ["host"] = uri.Authority, ["x-goog-cloud-target-resource"] = audience };
        foreach (var (name, value) in signed.AddedHeaders) headersOut[name] = value;
        return EncodeUriComponent($"{{\"url\":{JsQuote(url)},\"method\":\"POST\",\"headers\":[" +
            string.Join(',', headersOut.Select(header => $"{{\"key\":{JsQuote(header.Key)},\"value\":{JsQuote(header.Value)}}}")) + "]}");
    }

    // ---- executable source (PluggableAuthClient + PluggableAuthHandler + ExecutableResponse) ----

    private (string, SubjectSource) Executable(JsonObject source, string audience, string? subjectTokenType, string? impersonationUrl)
    {
        var executable = source["executable"] as JsonObject;
        var command = JsString(executable?["command"]);
        if (string.IsNullOrEmpty(command)) throw new GoogleCredentialsException("No valid Pluggable Auth \"credential_source\" provided.");
        var timeout = 30_000d;
        if (executable!.TryGetPropertyValue("timeout_millis", out var configured) && ((timeout = JsNumber(configured)) < 5_000 || timeout > 120_000))
            throw new GoogleCredentialsException("Timeout must be between 5000 and 120000 milliseconds.");
        var outputFile = JsString(executable["output_file"]);
        var components = CommandComponents().Matches(command).Select(match => match.Value is ['"', .., '"'] quoted ? quoted[1..^1] : match.Value).ToList();
        if (components.Count == 0) throw new GoogleCredentialsException($"Provided command: \"{command}\" could not be parsed.");
        return ("executable", async token => (await ExecutableSubjectAsync(components, double.IsNaN(timeout) ? 1 : timeout, outputFile, audience,
            subjectTokenType, impersonationUrl, token).ConfigureAwait(false), null));
    }

    private async Task<string> ExecutableSubjectAsync(List<string> components, double timeout, string? outputFile, string audience, string? subjectTokenType,
        string? impersonationUrl, CancellationToken token)
    {
        if (environment("GOOGLE_EXTERNAL_ACCOUNT_ALLOW_EXECUTABLES") != "1")
            throw new GoogleCredentialsException("Pluggable Auth executables need to be explicitly allowed to run by setting the " +
                "GOOGLE_EXTERNAL_ACCOUNT_ALLOW_EXECUTABLES environment Variable to 1.");
        var hasOutputFile = !string.IsNullOrEmpty(outputFile);
        var response = hasOutputFile ? CachedExecutableResponse(outputFile!) : null;
        if (response is null)
        {
            var variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["GOOGLE_EXTERNAL_ACCOUNT_AUDIENCE"] = audience };
            if (subjectTokenType is not null) variables["GOOGLE_EXTERNAL_ACCOUNT_TOKEN_TYPE"] = subjectTokenType;
            variables["GOOGLE_EXTERNAL_ACCOUNT_INTERACTIVE"] = "0"; // interactive mode is not supported
            if (hasOutputFile) variables["GOOGLE_EXTERNAL_ACCOUNT_OUTPUT_FILE"] = outputFile!;
            if (ServiceAccountEmail(impersonationUrl) is { } email) variables["GOOGLE_EXTERNAL_ACCOUNT_IMPERSONATED_EMAIL"] = email;
            var result = await RunExecutable(new(components[0], components[1..], variables, TimeSpan.FromMilliseconds(timeout)), token).ConfigureAwait(false);
            if (result.ExitCode is not { } code) throw new GoogleCredentialsException("The executable failed to finish within the timeout specified.");
            if (code != 0) throw ExecutableError(result.Output, code.ToString(CultureInfo.InvariantCulture));
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(result.Output); }
            catch (JsonException) { parsed = null; }
            response = parsed is null ? throw new GoogleCredentialsException($"The executable returned an invalid response: {result.Output}")
                : ExecutableResponse.From(parsed as JsonObject ?? new JsonObject());
        }
        if (JsNumber(response.Version) > 1) throw new GoogleCredentialsException("Version of executable is not currently supported, maximum supported version is 1.");
        if (!response.Success) throw ExecutableError(response.Message, response.Code);
        if (hasOutputFile && !Truthy(response.Expiration))
            throw new GoogleCredentialsException("The executable response must contain the `expiration_time` field for successful responses when an output_file has been specified in the configuration.");
        if (IsExpired(response)) throw new GoogleCredentialsException("Executable response is expired.");
        return response.SubjectToken!;
    }

    /// <summary>PluggableAuthHandler.retrieveCachedResponse: a successful, unexpired response in the output file, else null.</summary>
    private ExecutableResponse? CachedExecutableResponse(string outputFile)
    {
        var text = _read(outputFile);
        if (string.IsNullOrEmpty(text)) return null;
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(text); }
        catch (JsonException) { parsed = null; }
        var response = parsed is null ? throw new GoogleCredentialsException($"The output file contained an invalid response: {text}")
            : ExecutableResponse.From(parsed as JsonObject ?? new JsonObject());
        return response.Success && !IsExpired(response) ? response : null;
    }

    private bool IsExpired(ExecutableResponse response) =>
        response.HasExpiration && JsNumber(response.Expiration) < Math.Floor(_time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d + 0.5);

    private static GoogleCredentialsException ExecutableError(string? message, string? code) =>
        new($"The executable failed with exit code: {code} and error message: {message}.");

    private static string? ServiceAccountEmail(string? impersonationUrl)
    {
        if (string.IsNullOrEmpty(impersonationUrl)) return null;
        if (impersonationUrl.Length > 256) throw new GoogleCredentialsException($"URL is too long: {impersonationUrl}");
        return ServiceAccountEmailPattern().Match(impersonationUrl) is { Success: true } match && match.Groups["email"].Value is { Length: > 0 } email ? email : null;
    }

    private sealed record ExecutableResponse(JsonNode? Version, bool Success, bool HasExpiration, JsonNode? Expiration, string? SubjectToken, string? Code, string? Message)
    {
        public static ExecutableResponse From(JsonObject json)
        {
            if (!Truthy(json["version"])) throw new GoogleCredentialsException("Executable response must contain a 'version' field.");
            if (!json.ContainsKey("success")) throw new GoogleCredentialsException("Executable response must contain a 'success' field.");
            if (!Truthy(json["success"]))
            {
                if (!Truthy(json["code"])) throw new GoogleCredentialsException("Executable response must contain a 'code' field when unsuccessful.");
                if (!Truthy(json["message"])) throw new GoogleCredentialsException("Executable response must contain a 'message' field when unsuccessful.");
                return new(json["version"], false, false, null, null, JsString(json["code"]), JsString(json["message"]));
            }
            var tokenType = json["token_type"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
            if (tokenType is not (SamlType or IdTokenType or JwtType))
                throw new GoogleCredentialsException("Executable response must contain a 'token_type' field when successful " +
                    $"and it must be one of {IdTokenType}, {JwtType}, or {SamlType}.");
            var subject = tokenType == SamlType ? json["saml_response"] : json["id_token"];
            if (!Truthy(subject))
                throw new GoogleCredentialsException(tokenType == SamlType ? $"Executable response must contain a 'saml_response' field when token_type={SamlType}."
                    : $"Executable response must contain a 'id_token' field when token_type={IdTokenType} or {JwtType}.");
            return new(json["version"], true, json.ContainsKey("expiration_time"), json["expiration_time"], JsString(subject), null, null);
        }
    }

    /// <summary>child_process.spawn without a shell: stdout and stderr are appended to one output as they arrive; the child is killed at the timeout.</summary>
    private static async Task<GoogleExecutableResult> RunProcessAsync(GoogleExecutableRequest request, CancellationToken token)
    {
        var info = new ProcessStartInfo(request.Command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in request.Environment) info.Environment[name] = value;
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new GoogleCredentialsException($"The executable failed to start: {error.Message}", error); }
        var output = new StringBuilder();
        async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            for (int read; (read = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0;) lock (output) output.Append(buffer, 0, read);
        }
        var completion = Task.WhenAll(PumpAsync(process.StandardOutput), PumpAsync(process.StandardError), process.WaitForExitAsync(CancellationToken.None));
        try { await completion.WaitAsync(request.Timeout, token).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            try { process.Kill(); } catch (Exception kill) when (kill is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
            _ = completion.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (error is OperationCanceledException) throw;
            lock (output) return new(null, output.ToString());
        }
        return new(process.ExitCode, output.ToString());
    }

    private static HttpMessageInvoker CreateMtlsInvoker(X509Certificate2 certificate) => new(new SocketsHttpHandler
    {
        // A PFX round trip gives the PEM key a persisted handle that SChannel accepts as a client credential.
        SslOptions = { ClientCertificates = new X509CertificateCollection { X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null) } }
    }, disposeHandler: true);

    // ---- HTTP ----

    private readonly record struct Reply(int Status, string Body, string? MediaType)
    {
        public bool Ok => Status is >= 200 and < 300;
        public bool IsJson => MediaType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<Reply> RawAsync(HttpMessageInvoker client, HttpRequestMessage request, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
            return new((int)response.StatusCode, await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false), response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        { throw new GoogleCredentialsException("Google token request failed: " + error.Message, error); }
    }

    private async Task<string> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken token)
    {
        var reply = await RawAsync(http, request, token, timeout).ConfigureAwait(false);
        return reply.Ok ? reply.Body : throw new GoogleCredentialsException($"Google token request failed with status {reply.Status}.");
    }

    /// <summary>A gaxios request: responseType json adds accept: application/json; a failure carries gaxios' error message.</summary>
    private async Task<string> GaxiosAsync(HttpMessageInvoker client, HttpRequestMessage request, bool json, CancellationToken token)
    {
        if (json && request.Headers.Accept.Count == 0) request.Headers.TryAddWithoutValidation("accept", "application/json");
        var reply = await RawAsync(client, request, token).ConfigureAwait(false);
        return reply.Ok ? reply.Body : throw new GoogleCredentialsException(GaxiosMessage(reply, json));
    }

    /// <summary>An OAuth endpoint (STS, oauthtoken): a failure becomes getErrorFromOAuthErrorResponse's text.</summary>
    private async Task<string> OAuthAsync(HttpMessageInvoker client, HttpRequestMessage request, CancellationToken token)
    {
        var reply = await RawAsync(client, request, token).ConfigureAwait(false);
        if (reply.Ok) return reply.Body;
        var data = Object(reply.Body);
        string? Part(string name) => data is not null && data.TryGetPropertyValue(name, out var value) ? JsString(value) ?? "null" : null;
        var message = $"Error code {Part("error") ?? "undefined"}";
        if (Part("error_description") is { } description) message += $": {description}";
        if (Part("error_uri") is { } uri) message += $" - {uri}";
        throw new GoogleCredentialsException(message);
    }

    /// <summary>GaxiosError.extractAPIErrorFromResponse's message for responseType json (true), text (false) or unknown (null: by
    /// content type, where a body that is neither JSON nor text/* is a blob).</summary>
    private static string GaxiosMessage(Reply reply, bool? json)
    {
        var message = $"Request failed with status code {reply.Status}";
        if (json == false) return reply.Body;
        if (json is null && !reply.IsJson) return reply.MediaType is null || reply.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? reply.Body : message;
        JsonNode? data;
        try { data = JsonNode.Parse(reply.Body); }
        catch (JsonException) { return reply.Body; }
        if (data is JsonValue text && text.GetValueKind() == JsonValueKind.String) message = text.GetValue<string>();
        if (data is not JsonObject body || !Truthy(body["error"])) return message;
        if (body["error"] is JsonValue code && code.GetValueKind() == JsonValueKind.String) return code.GetValue<string>();
        if (body["error"] is not JsonObject error) return message;
        if (error["message"] is JsonValue detail && detail.GetValueKind() == JsonValueKind.String) message = detail.GetValue<string>();
        if (error["errors"] is JsonArray errors && string.Join('\n', errors.OfType<JsonObject>().Select(entry => entry["message"]).OfType<JsonValue>()
            .Where(entry => entry.GetValueKind() == JsonValueKind.String).Select(entry => entry.GetValue<string>())) is { Length: > 0 } joined) return joined;
        return message;
    }

    private static HttpRequestMessage FormRequest(string url, string form) =>
        new(HttpMethod.Post, url) { Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded") };

    private static HttpRequestMessage JsonRequest(string url, string body)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new(HttpMethod.Post, url) { Content = content };
    }

    // ---- helpers ----

    private DateTimeOffset Expiry(double seconds) =>
        double.IsFinite(seconds) && Math.Abs(seconds) < 1e11 ? _time.GetUtcNow().AddSeconds(seconds) : DateTimeOffset.MaxValue;

    /// <summary>new Date(expireTime).getTime(); an unparsable time is NaN, which never expires.</summary>
    private static DateTimeOffset ExpireTime(JsonNode? node) =>
        JsString(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MaxValue;

    private static string Required(JsonObject json, string name) => json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw Missing(name);
    private static GoogleCredentialsException Missing(string name) => new($"Google credentials file is missing {name}.");
    private static GoogleCredentialsException NoAccessToken() => new("Google token response has no access_token.");
    private static string Basic(string clientId, string? clientSecret) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
    private static string Bool(bool value) => value ? "true" : "false";
    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static string ReplaceFirst(string text, string find, string replacement) =>
        text.IndexOf(find, StringComparison.Ordinal) is var at and >= 0 ? string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + find.Length)) : text;
    private static JsonObject? Object(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>originalOrCamelOptions: the snake_case key, else its camelCase alias.</summary>
    private static JsonNode? Opt(JsonObject? json, string key) =>
        json is null ? null : json[key] ?? json[SnakeSegment().Replace(key, match => char.ToUpperInvariant(match.Value[1]).ToString())];
    private static string? Str(JsonObject? json, string key) => JsString(Opt(json, key));

    /// <summary>JavaScript truthiness of a JSON value (absent and null are falsy).</summary>
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => value.GetValue<double>() is var number && number != 0 && !double.IsNaN(number),
            JsonValueKind.True => true,
            _ => false
        },
        _ => true
    };

    /// <summary>String(value) for a JSON value.</summary>
    private static string? JsString(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.GetValue<double>().ToString("R", CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        },
        JsonArray array => string.Join(',', array.Select(item => JsString(item) ?? "")),
        _ => "[object Object]"
    };

    /// <summary>Number(value) for a present JSON value (null is 0).</summary>
    private static double JsNumber(JsonNode? node) => node switch
    {
        null => 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.Number => value.GetValue<double>(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String => value.GetValue<string>().Trim() is var text && text.Length == 0 ? 0
                : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN,
            _ => double.NaN
        },
        _ => double.NaN
    };

    /// <summary>JSON.stringify of a string.</summary>
    private static string JsQuote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case < ' ': builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)); break;
                case var high when char.IsHighSurrogate(high) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]):
                    builder.Append(high).Append(value[++index]); break;
                case var lone when char.IsSurrogate(lone): builder.Append("\\u").Append(((int)lone).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: builder.Append(character); break;
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>encodeURIComponent: everything but A-Z a-z 0-9 - _ . ! ~ * ' ( ) is percent-encoded UTF-8.</summary>
    private static string EncodeUriComponent(string value) => Escape(value, static unit => unit is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'!' or (byte)'~'
        or (byte)'*' or (byte)'\'' or (byte)'(' or (byte)')', spaceAsPlus: false);

    /// <summary>URLSearchParams (application/x-www-form-urlencoded): A-Z a-z 0-9 * - . _ kept, space as +, the rest percent-encoded UTF-8.</summary>
    private static string Form(params IEnumerable<(string Key, string Value)> fields) =>
        string.Join('&', fields.Select(field => FormEscape(field.Key) + "=" + FormEscape(field.Value)));
    private static string FormEscape(string value) => Escape(value, static unit => unit is (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_', spaceAsPlus: true);

    private static string Escape(string value, Func<byte, bool> keep, bool spaceAsPlus)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var unit in Encoding.UTF8.GetBytes(value))
            if (unit is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' || keep(unit)) builder.Append((char)unit);
            else if (unit == (byte)' ' && spaceAsPlus) builder.Append('+');
            else builder.Append('%').Append(unit.ToString("X2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"//iam\.googleapis\.com/locations/[^/]+/workforcePools/[^/]+/providers/.+")] private static partial Regex WorkforceAudience();
    [GeneratedRegex(@"^(aws)([0-9]+)\z")] private static partial Regex AwsEnvironmentId();
    [GeneratedRegex(@"(?<target>[^/]+):(generateAccessToken|generateIdToken)\z")] private static partial Regex TargetPrincipal();
    [GeneratedRegex(@"serviceAccounts/(?<email>[^:]+):generateAccessToken\z")] private static partial Regex ServiceAccountEmailPattern();
    [GeneratedRegex(@"(?:[^\s""]+|""[^""]*"")+")] private static partial Regex CommandComponents();
    [GeneratedRegex("-----BEGIN CERTIFICATE-----[^-]+-----END CERTIFICATE-----")] private static partial Regex PemCertificate();
    [GeneratedRegex("_[^_]")] private static partial Regex SnakeSegment();
}
