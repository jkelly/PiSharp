// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/bedrock-converse-stream.ts (profile, region and explicit
// key selection) over @aws-sdk/client-bedrock-runtime 3.1127.0's default credential chain (@aws-sdk/credential-provider-node):
// environment, SSO, shared ini profiles (static keys, assume role, web identity, credential_process, SSO), web identity token file,
// then the ECS container endpoint or EC2 instance metadata. Native port; no AWS SDK.
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PiSharp.AI.Protocols.Bedrock;

public sealed class AwsCredentialsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The ambient inputs the chain reads. Everything is injectable so tests run against fakes.</summary>
public sealed class AwsEnvironment
{
    public AwsEnvironment(Func<string, string?> readEnvironment, string homeDirectory, HttpMessageInvoker http,
        TimeProvider? time = null, Func<string, string?>? readFile = null)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment); ArgumentNullException.ThrowIfNull(http);
        Read = readEnvironment; Home = homeDirectory ?? ""; Http = http; Time = time ?? TimeProvider.System;
        ReadFile = readFile ?? (path => File.Exists(path) ? File.ReadAllText(path) : null);
    }
    /// <summary>Process environment (provider-scoped values are overlaid by the caller).</summary>
    public Func<string, string?> Read { get; }
    public string Home { get; }
    public HttpMessageInvoker Http { get; }
    public TimeProvider Time { get; }
    public Func<string, string?> ReadFile { get; }
    /// <summary>Runs a profile's credential_process and returns its stdout. Null (the default) refuses credential_process, as PiSharp
    /// refuses stored "!command" keys; an owner decision is required to enable it.</summary>
    public Func<string, CancellationToken, Task<string>>? RunCredentialProcess { get; init; }
    /// <summary>Writes a refreshed SSO token cache file. Null leaves the cache unchanged.</summary>
    public Action<string, string>? WriteFile { get; init; }

    internal string? Env(string name) => Read(name) is { Length: > 0 } value ? value : null;
    internal string Expand(string path) => path.StartsWith('~') ? Home + path[1..] : path;
    internal string CredentialsFile => Expand(Env("AWS_SHARED_CREDENTIALS_FILE") ?? Path.Combine(Home, ".aws", "credentials"));
    internal string ConfigFile => Expand(Env("AWS_CONFIG_FILE") ?? Path.Combine(Home, ".aws", "config"));
}

/// <summary>Shared config/credentials files (parseKnownFiles): profiles merged with credentials-file values winning, plus
/// <c>[sso-session name]</c> sections from the config file.</summary>
public sealed class AwsSharedFiles
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Profiles { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SsoSessions { get; }
    private AwsSharedFiles(Dictionary<string, Dictionary<string, string>> profiles, Dictionary<string, Dictionary<string, string>> sessions)
    {
        Profiles = profiles.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value, StringComparer.Ordinal);
        SsoSessions = sessions.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value, StringComparer.Ordinal);
    }

    public static AwsSharedFiles Load(AwsEnvironment environment)
    {
        var profiles = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var sessions = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (section, values) in ParseIni(environment.ReadFile(environment.ConfigFile)))
        {
            // Config file: [default], [profile name] and [sso-session name]; other prefixes are ignored.
            string? profile = section == "default" ? "default" : section.StartsWith("profile ", StringComparison.Ordinal) ? section[8..].Trim() : null;
            if (section.StartsWith("sso-session ", StringComparison.Ordinal)) Merge(sessions, section[12..].Trim(), values);
            else if (profile is not null) Merge(profiles, profile, values);
        }
        foreach (var (section, values) in ParseIni(environment.ReadFile(environment.CredentialsFile)))
            Merge(profiles, section, values); // credentials-file sections are bare profile names
        return new(profiles, sessions);

        static void Merge(Dictionary<string, Dictionary<string, string>> target, string name, Dictionary<string, string> values)
        {
            if (!target.TryGetValue(name, out var existing)) target[name] = existing = new(StringComparer.Ordinal);
            foreach (var (key, value) in values) existing[key] = value;
        }
    }

    /// <summary>@smithy/shared-ini-file-loader parseIni: [section] headers, key = value pairs, ";"/"#" comments, indented
    /// continuation lines for nested values (flattened as parent.child).</summary>
    public static List<(string Section, Dictionary<string, string> Values)> ParseIni(string? text)
    {
        var result = new List<(string, Dictionary<string, string>)>();
        if (string.IsNullOrEmpty(text)) return result;
        Dictionary<string, string>? current = null; string? nestedParent = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            // A comment starts a line or follows whitespace.
            for (var index = 0; index < line.Length; index++)
                if (line[index] is '#' or ';' && (index == 0 || char.IsWhiteSpace(line[index - 1]))) { line = line[..index]; break; }
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var name = trimmed[1..^1].Trim();
                current = new(StringComparer.Ordinal); nestedParent = null; result.Add((name, current));
                continue;
            }
            if (current is null) continue;
            var indented = line.Length > 0 && char.IsWhiteSpace(line[0]);
            var equals = trimmed.IndexOf('=');
            if (equals < 0) continue;
            var key = trimmed[..equals].Trim(); var value = trimmed[(equals + 1)..].Trim();
            if (indented && nestedParent is not null) { current[nestedParent + "." + key] = value; continue; }
            if (value.Length == 0) { nestedParent = key; continue; }
            nestedParent = null; current[key] = value;
        }
        return result;
    }
}

/// <summary>
/// The default credential chain Bedrock uses when no bearer token or explicit keys are configured. Credentials with an expiration are
/// cached until five minutes before it.
/// </summary>
public sealed class AwsCredentialChain
{
    private readonly AwsEnvironment _environment;
    private readonly string? _profile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AwsCredentials? _cached;
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(1);

    /// <param name="profile">The configured profile (client config <c>profile</c>); null uses AWS_PROFILE, then "default".</param>
    public AwsCredentialChain(AwsEnvironment environment, string? profile = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment; _profile = string.IsNullOrEmpty(profile) ? null : profile;
    }

    public async Task<AwsCredentials> ResolveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } cached && (cached.Expiration is null || cached.Expiration.Value - _environment.Time.GetUtcNow() > RefreshWindow))
                return cached;
            return _cached = await ResolveUncachedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<AwsCredentials> ResolveUncachedAsync(CancellationToken token)
    {
        var errors = new List<string>();
        var profileName = _profile ?? _environment.Env("AWS_PROFILE");
        // fromEnv is skipped once a profile is configured ("AWS_PROFILE is set, skipping fromEnv provider").
        if (profileName is null && FromEnvironment(_environment) is { } environmentCredentials) return environmentCredentials;
        var files = AwsSharedFiles.Load(_environment);
        var profile = profileName ?? "default";
        if (files.Profiles.TryGetValue(profile, out var data))
        {
            if (IsSso(data))
                return await ResolveSsoAsync(profile, data, files, token).ConfigureAwait(false);
            try { return await ResolveProfileAsync(profile, files, new HashSet<string>(StringComparer.Ordinal), false, token).ConfigureAwait(false); }
            catch (AwsCredentialsException error) when (error.Message.StartsWith("Could not resolve credentials using profile", StringComparison.Ordinal))
            { errors.Add(error.Message); }
        }
        else errors.Add($"Profile {profile} could not be found or parsed in shared credentials file.");
        if (_environment.Env("AWS_WEB_IDENTITY_TOKEN_FILE") is { } tokenFile && _environment.Env("AWS_ROLE_ARN") is { } roleArn)
            return await AssumeRoleWithWebIdentityAsync(roleArn, tokenFile, _environment.Env("AWS_ROLE_SESSION_NAME"), null, token).ConfigureAwait(false);
        if (_environment.Env("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI") is not null || _environment.Env("AWS_CONTAINER_CREDENTIALS_FULL_URI") is not null)
            return await FromContainerAsync(token).ConfigureAwait(false);
        if (!string.Equals(_environment.Env("AWS_EC2_METADATA_DISABLED"), "true", StringComparison.OrdinalIgnoreCase))
        {
            try { return await FromInstanceMetadataAsync(token).ConfigureAwait(false); }
            catch (AwsCredentialsException error) { errors.Add(error.Message); }
        }
        throw new AwsCredentialsException("Could not load credentials from any providers");
    }

    /// <summary>fromEnv: AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY, with AWS_SESSION_TOKEN and AWS_CREDENTIAL_EXPIRATION.</summary>
    public static AwsCredentials? FromEnvironment(AwsEnvironment environment)
    {
        if (environment.Env("AWS_ACCESS_KEY_ID") is not { } id || environment.Env("AWS_SECRET_ACCESS_KEY") is not { } secret) return null;
        DateTimeOffset? expiration = DateTimeOffset.TryParse(environment.Env("AWS_CREDENTIAL_EXPIRATION"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        return new(id, secret, environment.Env("AWS_SESSION_TOKEN"), expiration) { Source = "environment" };
    }

    private static bool IsStatic(IReadOnlyDictionary<string, string> data) =>
        data.ContainsKey("aws_access_key_id") && data.ContainsKey("aws_secret_access_key");
    private static bool IsAssumeRole(IReadOnlyDictionary<string, string> data) =>
        data.ContainsKey("role_arn") && (data.ContainsKey("source_profile") || data.ContainsKey("credential_source")) &&
        !(data.ContainsKey("source_profile") && data.ContainsKey("credential_source"));
    private static bool IsWebIdentity(IReadOnlyDictionary<string, string> data) =>
        data.ContainsKey("web_identity_token_file") && data.ContainsKey("role_arn");
    private static bool IsSso(IReadOnlyDictionary<string, string> data) =>
        data.ContainsKey("sso_start_url") || data.ContainsKey("sso_account_id") || data.ContainsKey("sso_session") ||
        data.ContainsKey("sso_region") || data.ContainsKey("sso_role_name");

    /// <summary>credential-provider-ini resolveProfileData.</summary>
    private async Task<AwsCredentials> ResolveProfileAsync(string name, AwsSharedFiles files, HashSet<string> visited,
        bool assumeRoleRecursiveCall, CancellationToken token)
    {
        if (!files.Profiles.TryGetValue(name, out var data))
            throw new AwsCredentialsException($"Could not resolve credentials using profile: [{name}] in configuration/credentials file(s).");
        if (visited.Count > 0 && IsStatic(data)) return Static(data);
        if (assumeRoleRecursiveCall || IsAssumeRole(data))
        {
            if (!visited.Add(name))
                throw new AwsCredentialsException("Detected a cycle attempting to resolve credentials for profile " + name + ". Profiles visited: " + string.Join(", ", visited));
            AwsCredentials source;
            if (data.TryGetValue("source_profile", out var sourceProfile))
                source = await ResolveProfileAsync(sourceProfile, files, visited, files.Profiles.TryGetValue(sourceProfile, out var sourceData) &&
                    IsAssumeRole(sourceData) && sourceProfile != name, token).ConfigureAwait(false);
            else if (data.TryGetValue("credential_source", out var credentialSource))
                source = credentialSource switch
                {
                    "Environment" => FromEnvironment(_environment) ?? throw new AwsCredentialsException("Unable to find environment variable credentials."),
                    "Ec2InstanceMetadata" => await FromInstanceMetadataAsync(token).ConfigureAwait(false),
                    "EcsContainer" => await FromContainerAsync(token).ConfigureAwait(false),
                    _ => throw new AwsCredentialsException($"Unsupported credential source in profile {name}. Got {credentialSource}, expected EcsContainer or Ec2InstanceMetadata or Environment.")
                };
            else if (IsStatic(data)) source = Static(data);
            else throw new AwsCredentialsException($"Could not resolve credentials using profile: [{name}] in configuration/credentials file(s).");
            if (data.ContainsKey("mfa_serial"))
                throw new AwsCredentialsException($"Profile {name} requires multi-factor authentication, but no MFA code callback was provided.");
            return await AssumeRoleAsync(source, data, name, token).ConfigureAwait(false);
        }
        if (IsStatic(data)) return Static(data);
        if (IsWebIdentity(data))
            return await AssumeRoleWithWebIdentityAsync(data["role_arn"], data["web_identity_token_file"],
                data.GetValueOrDefault("role_session_name"), data.GetValueOrDefault("region"), token).ConfigureAwait(false);
        if (data.TryGetValue("credential_process", out var command)) return await FromProcessAsync(name, command, token).ConfigureAwait(false);
        if (IsSso(data)) return await ResolveSsoAsync(name, data, files, token).ConfigureAwait(false);
        throw new AwsCredentialsException($"Could not resolve credentials using profile: [{name}] in configuration/credentials file(s).");
    }

    private static AwsCredentials Static(IReadOnlyDictionary<string, string> data) =>
        new(data["aws_access_key_id"], data["aws_secret_access_key"], data.GetValueOrDefault("aws_session_token")) { Source = "shared profile" };

    /// <summary>STS AssumeRole, SigV4-signed with the source credentials (region: profile region, else us-east-1).</summary>
    private async Task<AwsCredentials> AssumeRoleAsync(AwsCredentials source, IReadOnlyDictionary<string, string> data, string profile, CancellationToken token)
    {
        var region = data.GetValueOrDefault("region") ?? _environment.Env("AWS_REGION") ?? "us-east-1";
        var form = new List<KeyValuePair<string, string>>
        {
            new("Action", "AssumeRole"), new("Version", "2011-06-15"), new("RoleArn", data["role_arn"]),
            new("RoleSessionName", data.GetValueOrDefault("role_session_name") ?? "aws-sdk-js-" + _environment.Time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
        };
        if (data.TryGetValue("external_id", out var externalId)) form.Add(new("ExternalId", externalId));
        if (data.TryGetValue("duration_seconds", out var duration)) form.Add(new("DurationSeconds", duration));
        var body = Encoding.UTF8.GetBytes(string.Join('&', form.Select(pair => AwsSigV4.EscapeUri(pair.Key) + "=" + AwsSigV4.EscapeUri(pair.Value))));
        var host = $"sts.{region}.amazonaws.com";
        var headers = new List<KeyValuePair<string, string>> { new("content-type", "application/x-www-form-urlencoded") };
        var signature = AwsSigV4.Sign(new("POST", host, "/", [], headers, body), source, region, "sts", _environment.Time.GetUtcNow());
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/x-www-form-urlencoded");
        foreach (var (name, value) in signature.AddedHeaders) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.TryAddWithoutValidation("Authorization", signature.Authorization);
        var xml = await SendTextAsync(request, "STS AssumeRole for profile " + profile, token).ConfigureAwait(false);
        return StsCredentials(xml, "AssumeRoleResult", "assume role");
    }

    /// <summary>fromTokenFile / profile web_identity_token_file: unsigned STS AssumeRoleWithWebIdentity.</summary>
    private async Task<AwsCredentials> AssumeRoleWithWebIdentityAsync(string roleArn, string tokenFile, string? sessionName, string? region, CancellationToken token)
    {
        var webToken = _environment.ReadFile(_environment.Expand(tokenFile))
            ?? throw new AwsCredentialsException("Web identity token file could not be read: " + tokenFile);
        region ??= _environment.Env("AWS_REGION") ?? "us-east-1";
        var form = new[]
        {
            KeyValuePair.Create("Action", "AssumeRoleWithWebIdentity"), KeyValuePair.Create("Version", "2011-06-15"),
            KeyValuePair.Create("RoleArn", roleArn),
            KeyValuePair.Create("RoleSessionName", sessionName ?? "aws-sdk-js-session-" + _environment.Time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
            KeyValuePair.Create("WebIdentityToken", webToken.Trim())
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://sts.{region}.amazonaws.com/")
        { Content = new StringContent(string.Join('&', form.Select(pair => AwsSigV4.EscapeUri(pair.Key) + "=" + AwsSigV4.EscapeUri(pair.Value))), Encoding.UTF8) };
        request.Content.Headers.ContentType = new("application/x-www-form-urlencoded");
        var xml = await SendTextAsync(request, "STS AssumeRoleWithWebIdentity", token).ConfigureAwait(false);
        return StsCredentials(xml, "AssumeRoleWithWebIdentityResult", "web identity token");
    }

    private static AwsCredentials StsCredentials(string xml, string resultName, string source)
    {
        try
        {
            var document = XDocument.Parse(xml);
            var credentials = document.Descendants().FirstOrDefault(element => element.Name.LocalName == resultName)?
                .Elements().FirstOrDefault(element => element.Name.LocalName == "Credentials")
                ?? throw new AwsCredentialsException("Invalid response from STS: missing credentials.");
            string Value(string name) => credentials.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value
                ?? throw new AwsCredentialsException("Invalid response from STS: missing " + name + ".");
            return new(Value("AccessKeyId"), Value("SecretAccessKey"), Value("SessionToken"),
                DateTimeOffset.Parse(Value("Expiration"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)) { Source = source };
        }
        catch (System.Xml.XmlException error) { throw new AwsCredentialsException("Invalid response from STS.", error); }
    }

    /// <summary>credential_process: refused unless the host enables it (see <see cref="AwsEnvironment.RunCredentialProcess"/>).</summary>
    private async Task<AwsCredentials> FromProcessAsync(string profile, string command, CancellationToken token)
    {
        if (_environment.RunCredentialProcess is not { } run)
            throw new AwsCredentialsException($"Profile {profile} uses credential_process, which PiSharp does not run; configure static, SSO, role or container credentials instead.");
        var output = await run(command, token).ConfigureAwait(false);
        JsonObject? data;
        try { data = JsonNode.Parse(output) as JsonObject; }
        catch (JsonException error) { throw new AwsCredentialsException($"Profile {profile} credential_process returned invalid JSON.", error); }
        if (data?["Version"]?.GetValue<int>() != 1)
            throw new AwsCredentialsException($"Profile {profile} credential_process did not return Version 1.");
        DateTimeOffset? expiration = data["Expiration"] is JsonValue expires && DateTimeOffset.TryParse(expires.GetValue<string>(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        return new(data["AccessKeyId"]!.GetValue<string>(), data["SecretAccessKey"]!.GetValue<string>(), data["SessionToken"]?.GetValue<string>(), expiration)
        { Source = "credential_process" };
    }

    /// <summary>fromSSO: the cached SSO access token (refreshed through SSO OIDC for sso-session profiles) traded for role credentials.</summary>
    private async Task<AwsCredentials> ResolveSsoAsync(string profile, IReadOnlyDictionary<string, string> data, AwsSharedFiles files, CancellationToken token)
    {
        string? startUrl = data.GetValueOrDefault("sso_start_url"), region = data.GetValueOrDefault("sso_region");
        string? session = data.GetValueOrDefault("sso_session");
        if (session is not null)
        {
            if (!files.SsoSessions.TryGetValue(session, out var sessionData))
                throw new AwsCredentialsException($"Sso session '{session}' could not be found in shared credentials file.");
            startUrl = sessionData.GetValueOrDefault("sso_start_url") ?? startUrl; region = sessionData.GetValueOrDefault("sso_region") ?? region;
        }
        if (startUrl is null || region is null || !data.TryGetValue("sso_account_id", out var account) || !data.TryGetValue("sso_role_name", out var role))
            throw new AwsCredentialsException($"Profile is configured with invalid SSO credentials. Required parameters \"sso_account_id\", \"sso_region\", \"sso_role_name\", \"sso_start_url\". Got {string.Join(", ", data.Keys.Where(key => key.StartsWith("sso_", StringComparison.Ordinal)))}");
        var cacheKey = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(session ?? startUrl)));
        var cachePath = Path.Combine(_environment.Home, ".aws", "sso", "cache", cacheKey + ".json");
        var cacheText = _environment.ReadFile(cachePath)
            ?? throw new AwsCredentialsException($"The SSO session associated with this profile is invalid. To refresh this SSO session run aws sso login with the corresponding profile.");
        JsonObject cache;
        try { cache = JsonNode.Parse(cacheText) as JsonObject ?? throw new AwsCredentialsException("Invalid SSO token cache."); }
        catch (JsonException error) { throw new AwsCredentialsException("Invalid SSO token cache.", error); }
        var accessToken = cache["accessToken"]?.GetValue<string>();
        var expiresAt = DateTimeOffset.TryParse(cache["expiresAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry) ? expiry : DateTimeOffset.MinValue;
        var now = _environment.Time.GetUtcNow();
        if (accessToken is null || expiresAt - now <= RefreshWindow)
        {
            // sso-session tokens refresh through SSO OIDC CreateToken; legacy start-url tokens cannot.
            if (session is not null && cache["refreshToken"] is JsonValue refresh && cache["clientId"] is JsonValue clientId && cache["clientSecret"] is JsonValue clientSecret)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://oidc.{region}.amazonaws.com/token")
                {
                    Content = new StringContent(new JsonObject
                    {
                        ["clientId"] = clientId.GetValue<string>(), ["clientSecret"] = clientSecret.GetValue<string>(),
                        ["grantType"] = "refresh_token", ["refreshToken"] = refresh.GetValue<string>()
                    }.ToJsonString(), Encoding.UTF8, "application/json")
                };
                var refreshed = JsonNode.Parse(await SendTextAsync(request, "SSO OIDC token refresh", token).ConfigureAwait(false)) as JsonObject
                    ?? throw new AwsCredentialsException("Invalid SSO OIDC token response.");
                accessToken = refreshed["accessToken"]?.GetValue<string>() ?? throw new AwsCredentialsException("Invalid SSO OIDC token response.");
                cache["accessToken"] = accessToken;
                cache["expiresAt"] = now.AddSeconds(refreshed["expiresIn"]?.GetValue<double>() ?? 0).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                if (refreshed["refreshToken"] is JsonValue next) cache["refreshToken"] = next.GetValue<string>();
                try { _environment.WriteFile?.Invoke(cachePath, cache.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); } catch (Exception) { }
            }
            else if (accessToken is null || expiresAt <= now)
                throw new AwsCredentialsException("The SSO session associated with this profile has expired. To refresh this SSO session run aws sso login with the corresponding profile.");
        }
        using var credentialsRequest = new HttpRequestMessage(HttpMethod.Get,
            $"https://portal.sso.{region}.amazonaws.com/federation/credentials?account_id={AwsSigV4.EscapeUri(account)}&role_name={AwsSigV4.EscapeUri(role)}");
        credentialsRequest.Headers.TryAddWithoutValidation("x-amz-sso_bearer_token", accessToken);
        var roleData = JsonNode.Parse(await SendTextAsync(credentialsRequest, "SSO GetRoleCredentials", token).ConfigureAwait(false))?["roleCredentials"] as JsonObject
            ?? throw new AwsCredentialsException("SSO returns an invalid temporary credential.");
        return new(roleData["accessKeyId"]?.GetValue<string>() ?? throw new AwsCredentialsException("SSO returns an invalid temporary credential."),
            roleData["secretAccessKey"]?.GetValue<string>() ?? throw new AwsCredentialsException("SSO returns an invalid temporary credential."),
            roleData["sessionToken"]?.GetValue<string>(),
            roleData["expiration"] is JsonValue expiration ? DateTimeOffset.FromUnixTimeMilliseconds(expiration.GetValue<long>()) : null) { Source = "SSO" };
    }

    /// <summary>fromContainerMetadata: the ECS relative URI on 169.254.170.2, or an allowed full URI, with an optional token.</summary>
    private async Task<AwsCredentials> FromContainerAsync(CancellationToken token)
    {
        string url;
        if (_environment.Env("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI") is { } relative) url = "http://169.254.170.2" + relative;
        else
        {
            url = _environment.Env("AWS_CONTAINER_CREDENTIALS_FULL_URI")!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var full) || full.Scheme != Uri.UriSchemeHttps &&
                !(full.IsLoopback || full.Host is "169.254.170.2" or "169.254.170.23" or "[fd00:ec2::23]"))
                throw new AwsCredentialsException(url + " is not a valid container metadata service hostname");
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var authorization = _environment.Env("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE") is { } file ? _environment.ReadFile(file)?.Trim()
            : _environment.Env("AWS_CONTAINER_AUTHORIZATION_TOKEN");
        if (!string.IsNullOrEmpty(authorization)) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return MetadataCredentials(await SendTextAsync(request, "container metadata", token, MetadataTimeout * 5).ConfigureAwait(false), "ECS task role");
    }

    /// <summary>fromInstanceMetadata: IMDSv2 session token, role name, then the role's credentials.</summary>
    private async Task<AwsCredentials> FromInstanceMetadataAsync(CancellationToken token)
    {
        var endpoint = (_environment.Env("AWS_EC2_METADATA_SERVICE_ENDPOINT") ?? "http://169.254.169.254").TrimEnd('/');
        string? sessionToken = null;
        try
        {
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Put, endpoint + "/latest/api/token");
            tokenRequest.Headers.TryAddWithoutValidation("x-aws-ec2-metadata-token-ttl-seconds", "21600");
            sessionToken = await SendTextAsync(tokenRequest, "instance metadata token", token, MetadataTimeout).ConfigureAwait(false);
        }
        catch (AwsCredentialsException) { /* IMDSv1 fallback */ }
        HttpRequestMessage Get(string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint + path);
            if (sessionToken is not null) request.Headers.TryAddWithoutValidation("x-aws-ec2-metadata-token", sessionToken);
            return request;
        }
        using var roleRequest = Get("/latest/meta-data/iam/security-credentials/");
        var role = (await SendTextAsync(roleRequest, "instance metadata", token, MetadataTimeout).ConfigureAwait(false)).Trim().Split('\n')[0].Trim();
        using var credentialsRequest = Get("/latest/meta-data/iam/security-credentials/" + role);
        return MetadataCredentials(await SendTextAsync(credentialsRequest, "instance metadata", token, MetadataTimeout).ConfigureAwait(false), "EC2 instance role");
    }

    private static AwsCredentials MetadataCredentials(string text, string source)
    {
        try
        {
            var data = JsonNode.Parse(text) as JsonObject ?? throw new AwsCredentialsException("Invalid metadata credentials.");
            DateTimeOffset? expiration = data["Expiration"] is JsonValue expires && DateTimeOffset.TryParse(expires.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
            return new(data["AccessKeyId"]?.GetValue<string>() ?? throw new AwsCredentialsException("Invalid metadata credentials."),
                data["SecretAccessKey"]?.GetValue<string>() ?? throw new AwsCredentialsException("Invalid metadata credentials."),
                data["Token"]?.GetValue<string>(), expiration) { Source = source };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new AwsCredentialsException("Invalid metadata credentials.", error); }
    }

    private async Task<string> SendTextAsync(HttpRequestMessage request, string what, CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30), _environment.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var response = await _environment.Http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AwsCredentialsException($"{what} failed with status {(int)response.StatusCode}.");
            return text;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
        { throw new AwsCredentialsException($"{what} failed: {error.Message}", error); }
    }
}
