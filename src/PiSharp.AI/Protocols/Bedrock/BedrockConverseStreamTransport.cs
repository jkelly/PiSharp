// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/bedrock-converse-stream.ts (stream: client configuration,
// region and endpoint selection, bearer token or SigV4 credentials, custom headers, event handling, stop reasons, usage, errors and
// diagnostics) and providers/amazon-bedrock.ts. The AWS SDK's ConverseStream command, standard retry strategy and event stream
// codec are ported natively.
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.Bedrock;

/// <summary>A modeled Bedrock service error (the SDK's BedrockRuntimeServiceException): from the HTTP response or a stream event.</summary>
public sealed class BedrockServiceException(string name, string message, int? status = null, string? requestId = null, string? body = null)
    : Exception(message)
{
    public string Name { get; } = name;
    public int? Status { get; } = status;
    public string? RequestId { get; } = requestId;
    /// <summary>The raw body when the SDK could not fold it into the message.</summary>
    public string? Body { get; } = body;
    /// <summary>Modeled service exception (prefixed in the display message).</summary>
    public bool Modeled { get; init; } = true;
}

/// <summary>
/// Bedrock ConverseStream over HTTP with the AWS event stream. Each request resolves its thinking level as streamSimple does;
/// the credentials (bearer token, explicit keys or the default chain) are resolved per request and cached by the chain.
/// Borrows the client.
/// </summary>
public sealed partial class BedrockConverseStreamTransport : IChatTransport, IThinkingLevelTransport
{
    private readonly HttpClient _client;
    private readonly BedrockModel _model;
    private readonly BedrockConverseOptions _options;
    private readonly AwsEnvironment _environment;
    private readonly ImmutableArray<string> _levels;
    private readonly Dictionary<string, AwsCredentialChain> _chains = new(StringComparer.Ordinal);
    /// <summary>The level used when a request carries none (the session's selected level at binding).</summary>
    private readonly string? _defaultLevel;

    private static readonly ImmutableDictionary<string, string> ErrorPrefixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["InternalServerException"] = "Internal server error", ["ModelStreamErrorException"] = "Model stream error",
        ["ValidationException"] = "Validation error", ["ThrottlingException"] = "Throttling error", ["ServiceUnavailableException"] = "Service unavailable"
    }.ToImmutableDictionary();
    private const string DataRetentionDocs = "https://docs.aws.amazon.com/bedrock/latest/userguide/data-retention.html";
    private static readonly HashSet<string> ThrottlingCodes = new(StringComparer.Ordinal)
    {
        "BandwidthLimitExceeded", "EC2ThrottledException", "LimitExceededException", "PriorRequestNotComplete",
        "ProvisionedThroughputExceededException", "RequestLimitExceeded", "RequestThrottled", "RequestThrottledException",
        "SlowDown", "ThrottledException", "Throttling", "ThrottlingException", "TooManyRequestsException", "TransactionInProgressException"
    };
    private static readonly HashSet<string> TransientCodes = new(StringComparer.Ordinal) { "TimeoutError", "RequestTimeout", "RequestTimeoutException" };

    public BedrockConverseStreamTransport(HttpClient client, ModelDescriptor model, JsonData modelMetadata, BedrockConverseOptions options,
        AwsEnvironment environment, string? defaultThinkingLevel = null)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(modelMetadata);
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(environment);
        if (model.Api != "bedrock-converse-stream" || string.IsNullOrWhiteSpace(model.Id) || modelMetadata.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Unsupported Bedrock model selection.");
        _client = client; _model = new(model, modelMetadata); _options = options; _environment = environment;
        _levels = ProviderTranscript.SupportedThinkingLevels(modelMetadata.Value);
        if (defaultThinkingLevel is not null && !_levels.Contains(defaultThinkingLevel)) throw new ArgumentException("Unsupported Bedrock thinking level.");
        _defaultLevel = defaultThinkingLevel;
    }

    /// <summary>One cached default chain per configured profile (the chain caches expiring credentials).</summary>
    private AwsCredentialChain Chain(string? profile)
    {
        lock (_chains)
        {
            if (!_chains.TryGetValue(profile ?? "", out var chain)) _chains[profile ?? ""] = chain = new(_environment, profile);
            return chain;
        }
    }

    public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) =>
        model == _model.Descriptor ? _levels : throw new ArgumentException("Unknown Bedrock model.");

    /// <summary>getProviderEnvValue: the provider-scoped env, then the process environment.</summary>
    private string? Env(BedrockConverseOptions o, string name) => o.Environment is { } scoped && scoped.TryGetValue(name, out var value) && value.Length > 0 ? value : _environment.Env(name);
    private static string? OptionsProfile(BedrockConverseOptions o) => o.Profile is { Length: > 0 } profile ? profile :
        o.Environment is { } scoped && scoped.TryGetValue("AWS_PROFILE", out var value) && value.Length > 0 ? value : null;
    private string? Profile(BedrockConverseOptions o) => OptionsProfile(o) ?? Env(o, "AWS_PROFILE");
    private string? ConfiguredRegion(BedrockConverseOptions o) => o.Region is { Length: > 0 } region ? region : Env(o, "AWS_REGION") ?? Env(o, "AWS_DEFAULT_REGION");

    /// <summary>The resolved client configuration: region, endpoint and authentication, with the request's effective options.</summary>
    internal sealed record Resolved(string Region, Uri Endpoint, string? BearerToken, AwsCredentials? Credentials, BedrockConverseOptions Options);

    internal async Task<Resolved> ResolveAsync(CancellationToken token)
    {
        var o = _options;
        if (_options.Auth is { } auth)
        {
            var (apiKey, environment) = await auth(token).ConfigureAwait(false);
            o = _options with { ApiKey = apiKey, Environment = environment };
        }
        string? Env(string name) => this.Env(o, name);
        var configuredRegion = ConfiguredRegion(o);
        var ambientProfile = _environment.Env("AWS_PROFILE") is not null;
        var endpointRegion = StandardEndpointRegion(_model.BaseUrl);
        var explicitEndpoint = endpointRegion is null || configuredRegion is null && !ambientProfile;
        string? region;
        if (ArnRegion().Match(_model.Id) is { Success: true } arn) region = arn.Groups[1].Value;
        else if (configuredRegion is not null) region = configuredRegion;
        else if (endpointRegion is not null && explicitEndpoint) region = endpointRegion;
        else if (!ambientProfile) region = "us-east-1";
        else
        {
            // The SDK's region chain: the configured profile's region in the shared config file.
            var files = AwsSharedFiles.Load(_environment);
            region = files.Profiles.TryGetValue(Profile(o) ?? "default", out var data) ? data.GetValueOrDefault("region") : null;
            if (region is null) throw new BedrockServiceException("Error", "Region is missing") { Modeled = false };
        }
        Uri endpoint;
        if (explicitEndpoint && _model.BaseUrl.Length != 0) endpoint = new(_model.BaseUrl);
        else if ((Env("AWS_ENDPOINT_URL_BEDROCK_RUNTIME") ?? Env("AWS_ENDPOINT_URL")) is { } configured) endpoint = new(configured);
        else
        {
            var fips = string.Equals(Env("AWS_USE_FIPS_ENDPOINT"), "true", StringComparison.OrdinalIgnoreCase);
            var suffix = region.StartsWith("cn-", StringComparison.Ordinal) ? "amazonaws.com.cn" : "amazonaws.com";
            endpoint = new($"https://bedrock-runtime{(fips ? "-fips" : "")}.{region}.{suffix}");
        }
        var skipAuth = Env("AWS_BEDROCK_SKIP_AUTH") == "1";
        var bearer = o.BearerToken is { Length: > 0 } explicitBearer ? explicitBearer : o.ApiKey is { Length: > 0 } key ? key : Env("AWS_BEARER_TOKEN_BEDROCK");
        if (bearer is not null && !skipAuth) return new(region, endpoint, bearer, null, o);
        if (skipAuth) return new(region, endpoint, null, new("dummy-access-key", "dummy-secret-key") { Source = "AWS_BEDROCK_SKIP_AUTH" }, o);
        // A profile configured through pi (option or scoped AWS_PROFILE) wins over ambient keys (#6957).
        if (OptionsProfile(o) is null && Env("AWS_ACCESS_KEY_ID") is { } id && Env("AWS_SECRET_ACCESS_KEY") is { } secret)
            return new(region, endpoint, null, new(id, secret, Env("AWS_SESSION_TOKEN")) { Source = "AWS access keys" }, o);
        return new(region, endpoint, null, await Chain(Profile(o)).ResolveAsync(token).ConfigureAwait(false), o);
    }

    [GeneratedRegex("^arn:aws(?:-[a-z0-9-]+)?:bedrock:([a-z0-9-]+):")] private static partial Regex ArnRegion();
    [GeneratedRegex("^bedrock-runtime(?:-fips)?\\.([a-z0-9-]+)\\.amazonaws\\.com(?:\\.cn)?$")] private static partial Regex StandardEndpoint();

    internal static string? StandardEndpointRegion(string? baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        var match = StandardEndpoint().Match(uri.Host.ToLowerInvariant());
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The serialized request (headers and body) for a request, without sending: the wire the transport would send.</summary>
    public async Task<HttpRequestMessage> CreateRequestAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var body = Encoding.UTF8.GetBytes(BuildBody(request, resolved).ToJsonString(BodyJson));
        return CreateHttpRequest(resolved, body);
    }

    private static readonly JsonSerializerOptions BodyJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private JsonObject BuildBody(ChatRequest request, Resolved resolved)
    {
        var level = request.ThinkingLevel ?? _defaultLevel;
        if (level is not null && !_levels.Contains(level)) throw new ArgumentException("Unsupported Bedrock thinking level.");
        var transcript = ProviderTranscript.Parse(request.Messages);
        var o = resolved.Options;
        var stream = BedrockConverseRequest.Simple(_model, transcript, o, level);
        return BedrockConverseRequest.Build(_model, request, o, stream, name => Env(o, name), ConfiguredRegion(o) ?? resolved.Region);
    }

    private HttpRequestMessage CreateHttpRequest(Resolved resolved, byte[] body)
    {
        var basePath = resolved.Endpoint.AbsolutePath.TrimEnd('/');
        // __extendedEncodeURIComponent(modelId): every reserved character, including ":" and "/", is escaped.
        var path = basePath + "/model/" + AwsSigV4.EscapeUri(_model.Id) + "/converse-stream";
        var uri = new Uri(resolved.Endpoint.GetLeftPart(UriPartial.Authority) + path);
        var host = resolved.Endpoint.IsDefaultPort ? resolved.Endpoint.Host : resolved.Endpoint.Host + ":" + resolved.Endpoint.Port.ToString(CultureInfo.InvariantCulture);
        var headers = new List<KeyValuePair<string, string>> { new("content-type", "application/json") };
        // Caller headers are applied before signing; SigV4 and auth headers are reserved.
        foreach (var (name, value) in resolved.Options.Headers ?? ImmutableDictionary<string, string?>.Empty)
        {
            if (value is null) continue;
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("x-amz-", StringComparison.Ordinal) || lower is "authorization" or "host") continue;
            headers.RemoveAll(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            headers.Add(new(name, value));
        }
        var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body), Version = HttpVersion.Version11 };
        message.Headers.TryAddWithoutValidation("User-Agent", "PiSharp");
        foreach (var (name, value) in headers)
            if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) message.Content.Headers.TryAddWithoutValidation("Content-Type", value);
            else if (!message.Headers.TryAddWithoutValidation(name, value)) message.Content.Headers.TryAddWithoutValidation(name, value);
        if (resolved.BearerToken is { } bearer) message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        else
        {
            var signature = AwsSigV4.Sign(new("POST", host, path, [], headers, body), resolved.Credentials!, resolved.Region, "bedrock",
                _environment.Time.GetUtcNow());
            foreach (var (name, value) in signature.AddedHeaders) message.Headers.TryAddWithoutValidation(name, value);
            message.Headers.TryAddWithoutValidation("Authorization", signature.Authorization);
        }
        return message;
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model != _model.Descriptor) throw new ArgumentException("Unknown Bedrock model.");
        if (request.ThinkingLevel is { } level && !_levels.Contains(level)) throw new ArgumentException("Unsupported Bedrock thinking level.");
        return StreamCore(request, cancellationToken);
    }

    private async IAsyncEnumerable<StreamEvent> StreamCore(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
    {
        var state = new BedrockEventState(_model, request, _options);
        var queue = new List<StreamEvent>();
        string? requestId = null; Exception? failure = null;
        HttpResponseMessage? response = null; Stream? body = null; IAsyncEnumerator<AwsEventStreamMessage>? events = null;
        try
        {
            try
            {
                var resolved = await ResolveAsync(token).ConfigureAwait(false);
                var payload = BuildBody(request, resolved);
                if (_options.OnPayload is { } hook && await hook(JsonData.Parse(payload.ToJsonString(BodyJson)), request.Model, token).ConfigureAwait(false) is { } replaced)
                    payload = JsonNode.Parse(replaced.ToString()) as JsonObject ?? throw new InvalidOperationException("Bedrock payload must be an object.");
                var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString(BodyJson));
                if (bytes.Length > _options.MaximumPayloadBytes) throw new BedrockServiceException("Error", "Bedrock request exceeds the configured payload limit.") { Modeled = false };
                response = await SendWithRetriesAsync(resolved, bytes, token).ConfigureAwait(false);
                requestId = Normalize(Header(response, "x-amzn-requestid"));
                if (_options.OnResponse is { } responseHook) await responseHook(Observation(response), request.Model, token).ConfigureAwait(false);
                body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                events = AwsEventStream.DecodeAsync(body, cancellationToken: token).GetAsyncEnumerator(token);
            }
            catch (Exception error) { failure = error; }
            while (failure is null)
            {
                queue.Clear();
                try
                {
                    if (!await events!.MoveNextAsync().ConfigureAwait(false)) break;
                    state.Handle(events.Current, queue);
                }
                catch (Exception error) { failure = error; }
                foreach (var frame in queue) yield return frame;
            }
            if (failure is null)
            {
                StreamTerminalEvent? done = null;
                try { if (token.IsCancellationRequested) throw new OperationCanceledException("Request was aborted"); queue.Clear(); done = state.Complete(queue); }
                catch (Exception error) { failure = error; }
                if (done is not null) { foreach (var frame in queue) yield return frame; yield return done; yield break; }
            }
        }
        finally
        {
            if (events is not null) try { await events.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            if (body is not null) try { await body.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            response?.Dispose();
        }
        var aborted = token.IsCancellationRequested;
        yield return state.Fail(failure!, aborted, FormatError(failure!, aborted), aborted ? null : Diagnostic(failure!, requestId));
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(Resolved resolved, byte[] body, CancellationToken token)
    {
        var maxAttempts = _options.MaxAttempts ?? (int.TryParse(Env(resolved.Options, "AWS_MAX_ATTEMPTS"), NumberStyles.None, CultureInfo.InvariantCulture, out var configured) && configured > 0 ? configured : 3);
        var random = _options.Random ?? System.Random.Shared.NextDouble;
        var delay = _options.Delay ?? ((wait, cancellation) => Task.Delay(wait, _environment.Time, cancellation));
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            HttpResponseMessage? response = null; Exception? error;
            using (var request = CreateHttpRequest(resolved, body))
            {
                try
                {
                    response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) return response;
                    error = await ServiceErrorAsync(response, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { response?.Dispose(); throw; }
                catch (Exception sendError) when (sendError is HttpRequestException or IOException or TaskCanceledException) { error = sendError; }
            }
            var retryAfter = response is null ? null : Header(response, "x-amz-retry-after");
            response?.Dispose();
            var kind = RetryKind(error);
            if (kind is null || attempt >= maxAttempts) throw error;
            var baseDelay = kind == "throttling" ? 500d : 100d;
            var wait = Math.Floor(Math.Min(20_000, random() * Math.Pow(2, attempt - 1) * baseDelay));
            if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var serverDelay)) wait = Math.Max(wait, serverDelay);
            await delay(TimeSpan.FromMilliseconds(wait), token).ConfigureAwait(false);
            // Credentials may have rotated meanwhile; the chain's cache keeps this cheap.
            resolved = resolved.BearerToken is null && resolved.Credentials?.Source is not ("AWS_BEDROCK_SKIP_AUTH" or "AWS access keys")
                ? resolved with { Credentials = await Chain(Profile(resolved.Options)).ResolveAsync(token).ConfigureAwait(false) } : resolved;
        }
    }

    /// <summary>The SDK's standard retry classification: throttling codes or 429, transient codes, 500/502/503/504 or a network error.</summary>
    private static string? RetryKind(Exception error) => error switch
    {
        BedrockServiceException service when service.Status == 429 || ThrottlingCodes.Contains(service.Name) => "throttling",
        BedrockServiceException service when TransientCodes.Contains(service.Name) || service.Status is 500 or 502 or 503 or 504 => "transient",
        HttpRequestException or IOException or TaskCanceledException => "transient",
        _ => null
    };

    private async Task<BedrockServiceException> ServiceErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var status = (int)response.StatusCode; var requestId = Normalize(Header(response, "x-amzn-requestid"));
        string? code = Header(response, "x-amzn-errortype")?.Split(':')[0];
        string? message = null;
        try
        {
            if (JsonNode.Parse(text) is JsonObject json)
            {
                message = (json["message"] ?? json["Message"]) is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : null;
                if (code is null && (json["__type"] ?? json["code"] ?? json["Code"]) is JsonValue type && type.TryGetValue<string>(out var typeName))
                    code = typeName.Split('#')[^1].Split(':')[0];
            }
        }
        catch (JsonException) { }
        var trimmed = text.Trim();
        // A body the SDK cannot model (a gateway's HTML page) surfaces as "status: body" instead of "Unknown: UnknownError".
        if (message is null)
            return new(code ?? "Unknown", "UnknownError", status, requestId,
                trimmed.Length == 0 ? null : trimmed.Length <= 4000 ? trimmed : trimmed[..4000] + $"... [truncated {trimmed.Length - 4000} chars]")
            { Modeled = code is not null };
        return new(code ?? "Unknown", message, status, requestId);
    }

    /// <summary>formatBedrockError: the modeled prefix, the message (or "status: body" when the body is not in it) and the
    /// data-retention hint.</summary>
    internal static string FormatError(Exception error, bool aborted)
    {
        if (aborted && error is OperationCanceledException) return "Request was aborted";
        string core;
        if (error is BedrockServiceException { Body: { } raw, Status: { } status } service && !service.Message.Contains(raw, StringComparison.Ordinal))
            core = status.ToString(CultureInfo.InvariantCulture) + ": " + raw;
        else core = error.Message;
        var hint = core.Contains("data retention mode", StringComparison.OrdinalIgnoreCase) ? $" See {DataRetentionDocs} for supported data retention modes." : "";
        if (error is BedrockServiceException { Modeled: true } modeled)
            return (ErrorPrefixes.TryGetValue(modeled.Name, out var prefix) ? prefix : modeled.Name) + ": " + core + hint;
        return core + hint;
    }

    /// <summary>appendBedrockFailureDiagnostic: status, errorCode (names ending in Exception) and the request id, when known.</summary>
    internal static JsonObject? Diagnostic(Exception error, string? fallbackRequestId)
    {
        var details = new JsonObject();
        var service = error as BedrockServiceException;
        if (service?.Status is { } status) details["status"] = status;
        var name = service?.Name ?? (error as AwsEventStreamProtocolError)?.Name;
        if (name is not null && name.EndsWith("Exception", StringComparison.Ordinal) && Normalize(name) is { } code) details["errorCode"] = code;
        if ((Normalize(service?.RequestId) ?? fallbackRequestId) is { } requestId) details["requestId"] = requestId;
        return details.Count == 0 ? null : details;
    }

    private static string? Normalize(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length is 0 or > 200 ? null : trimmed;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values) ? string.Join(", ", values) : null;

    private static JsonData Observation(HttpResponseMessage response)
    {
        var headers = new JsonObject();
        foreach (var field in response.Headers.Concat(response.Content.Headers)) headers[field.Key.ToLowerInvariant()] = string.Join(", ", field.Value);
        return JsonData.Parse(new JsonObject { ["status"] = (int)response.StatusCode, ["headers"] = headers }.ToJsonString());
    }
}

/// <summary>A protocol-level event stream error (":message-type" error): not a modeled service exception.</summary>
public sealed class AwsEventStreamProtocolError(string name, string message) : Exception(message)
{
    public string Name { get; } = name;
}
