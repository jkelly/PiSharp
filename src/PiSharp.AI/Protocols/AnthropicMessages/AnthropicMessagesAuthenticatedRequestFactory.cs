using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.AI.Authentication;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>Actual configured Anthropic key/header-bearer/OAuth request construction. Explicit injected auth only; no ambient acquisition, SDK, send or retry. Existing key-only factory remains separate.</summary>
public sealed class AnthropicMessagesAuthenticatedRequestFactory
{
    private readonly Uri _endpoint;
    private readonly ModelDescriptor _model;
    private readonly AnthropicMessagesKeyAuthRequestOptions _options;
    private readonly AnthropicMessagesRequestProjector _projector;
    private readonly ImmutableArray<KeyValuePair<string, string?>> _headers;
    private readonly AnthropicInjectedAuthenticationBinding _authentication;

    public AnthropicMessagesAuthenticatedRequestFactory(Uri baseUri, ModelDescriptor expectedModel,
        AnthropicMessagesRequestOptions projectionOptions, AnthropicInjectedAuthenticationBinding authentication,
        AnthropicMessagesKeyAuthRequestOptions? options = null)
    {
        _options = options ?? new();
        ArgumentNullException.ThrowIfNull(authentication); _authentication = authentication;
        if (authentication.Authentication.Secret.Length is < 1 or > 4096 ||
            authentication.Kind is not (AuthenticationKind.ApiKey or AuthenticationKind.BearerToken)) throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidKey);
        if (baseUri is null || expectedModel is null || projectionOptions is null ||
            !double.IsFinite(_options.MaximumTokenMagnitude) || _options.MaximumTokenMagnitude <= 0 ||
            _options.MaxTokens is { } tokens && !double.IsFinite(tokens) || _options.MaximumKeyCharacters <= 0 || _options.MaximumBaseUriCharacters <= 0 ||
            _options.MaximumPayloadBytes <= 0 || _options.MaximumPayloadDepth is < 1 or > 64 || _options.MaximumHeaders <= 0 ||
            _options.MaximumHeaderCharacters <= 0 || _options.MaximumTotalHeaderCharacters <= 0 ||
            !baseUri.IsAbsoluteUri || baseUri.Scheme is not ("http" or "https") || baseUri.UserInfo.Length != 0 ||
            baseUri.Fragment.Length != 0 || baseUri.Query.Length != 0 || expectedModel.Api != "anthropic-messages" ||
            string.IsNullOrWhiteSpace(expectedModel.Id) || string.IsNullOrWhiteSpace(expectedModel.Provider)) throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration);
        if (baseUri.AbsoluteUri.Length > _options.MaximumBaseUriCharacters ||
            authentication.Authentication.Secret.Length > _options.MaximumKeyCharacters ||
            Math.Abs(_options.MaxTokens ?? projectionOptions.MaximumTokens) > _options.MaximumTokenMagnitude)
            throw Fail(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
        HeaderValue(authentication.Authentication.Secret);
        if (expectedModel.Provider != "anthropic" || projectionOptions.OAuthProjection && !authentication.UseOAuthProjection)
            throw Fail(AnthropicMessagesKeyAuthRequestFailure.UnsupportedOptions);
        // SDK buildURL appends the resource path to a configured base path rather than replacing it.
        try { _endpoint = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/v1/messages?beta=true"); }
        catch (UriFormatException) { throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration); }
        _model = expectedModel;
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        long supplied = 0;
        ReadHeaders(_options.RuntimeHeaders, merged, ref supplied);
        var configured = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["User-Agent"] = "PiSharp", ["accept"] = "application/json",
            ["anthropic-dangerous-direct-browser-access"] = "true"
        };
        if (_options.SessionAffinityHeader is not ("x-session-affinity" or "x-session-id"))
            throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration);
        if (_options.SessionId is not null)
        {
            HeaderValue(_options.SessionId);
            if (_options.SessionId.Length > _options.MaximumHeaderCharacters) throw Fail(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
            if (!authentication.UseOAuthProjection && _options.SendSessionAffinityHeaders && projectionOptions.CacheRetention != AnthropicCacheRetention.None && _options.SessionId.Length != 0)
                configured[_options.SessionAffinityHeader] = _options.SessionId;
        }
        if (authentication.UseOAuthProjection) { configured["user-agent"] = "claude-cli/2.1.280"; configured["x-app"] = "cli"; }
        ReadHeaders(_options.ModelHeaders, configured, ref supplied);
        foreach (var header in authentication.Headers) configured[header.Key] = header.Value;
        ReadHeaders(_options.Headers, configured, ref supplied);
        var betaFeatures = projectionOptions.BetaFeatures;
        foreach (var header in configured)
            if (header.Key.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase))
                betaFeatures = header.Value is null ? [] : header.Value.Split(',').Select(value => value.Trim()).Where(value => value.Length != 0)
                    .Distinct(StringComparer.Ordinal).ToImmutableArray();
        foreach (var header in configured) merged[header.Key] = header.Value;
        _headers = merged.ToImmutableArray();
        try
        {
            _projector = new(projectionOptions with
            {
                BetaFeatures = betaFeatures, OAuthProjection = authentication.UseOAuthProjection,
                MaximumOutputBytes = Math.Min(projectionOptions.MaximumOutputBytes, _options.MaximumPayloadBytes),
                MaximumOutputCharacters = Math.Min(projectionOptions.MaximumOutputCharacters, _options.MaximumPayloadBytes),
                MaximumJsonDepth = Math.Min(projectionOptions.MaximumJsonDepth, _options.MaximumPayloadDepth)
            });
        }
        catch (ArgumentException) { throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration); }
    }

    public HttpRequestMessage Create(ChatRequest request, CancellationToken cancellationToken = default)
        => CreateCore(request, cancellationToken, null);

    public AnthropicMessagesPreparedRequest Prepare(ChatRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || request.Model != _model) throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidRequest);
        var projected = AnthropicMessagesPreparedRequest.Project(_projector.Project(request, cancellationToken), Raw, cancellationToken);
        _ = PreparedHeaders(projected);
        return new(projected, _options, (payload, token) => CreateCore(request, token, payload), cancellationToken);
    }

    private HttpRequestMessage CreateCore(ChatRequest request, CancellationToken cancellationToken, JsonData? prepared)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || request.Model != _model) throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidRequest);
        var projected = prepared ?? _projector.Project(request, cancellationToken);
        CheckDepth(projected.Value, 0, cancellationToken);
        var headers = PreparedHeaders(projected);
        // Count the exact compact raw representation before allocating UTF-8 or acquiring an HTTP request/content.
        long bytes = 2; var fields = 0;
        foreach (var property in projected.Value.EnumerateObject())
        {
            if (property.Name == "betas") continue;
            var raw = prepared is null ? Raw(property) : property.Value.GetRawText();
            bytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(property.Name)) + 1L + Encoding.UTF8.GetByteCount(raw) + (fields++ == 0 ? 0 : 1);
            if (bytes > _options.MaximumPayloadBytes) throw Fail(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream((int)bytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in projected.Value.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (property.Name == "betas") continue;
                writer.WritePropertyName(property.Name); writer.WriteRawValue(prepared is null ? Raw(property) : property.Value.GetRawText());
            }
            writer.WriteEndObject();
        }
        if (buffer.Length > _options.MaximumPayloadBytes) throw Fail(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
        cancellationToken.ThrowIfCancellationRequested();
        HttpRequestMessage? result = null; HttpContent? content = null;
        try
        {
            content = new ByteArrayContent(buffer.ToArray());
            result = new(HttpMethod.Post, _endpoint) { Content = content }; content = null;
            foreach (var pair in headers)
                if (pair.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) result.Content!.Headers.ContentType = new MediaTypeHeaderValue(pair.Value);
                else if (!result.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) throw Fail(AnthropicMessagesKeyAuthRequestFailure.UnsupportedOptions);
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }
        catch (AnthropicMessagesKeyAuthRequestException) { result?.Dispose(); content?.Dispose(); throw; }
        catch (OperationCanceledException) { result?.Dispose(); content?.Dispose(); throw; }
        catch (Exception) { result?.Dispose(); content?.Dispose(); throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration); }
    }
    private Dictionary<string,string> PreparedHeaders(JsonData projected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["anthropic-version"] = "2023-06-01" };
        var removedAuth = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_authentication.UseOAuthProjection) headers["Authorization"] = "Bearer " + _authentication.ApiKey;
        else if (_authentication.ApiKey is { } key) headers["x-api-key"] = key;
        foreach (var pair in _headers)
            if (pair.Value is null) { headers.Remove(pair.Key); if (pair.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)) removedAuth.Add(pair.Key); }
            else
            {
                var value = pair.Value.Trim(' ', '\t'); removedAuth.Remove(pair.Key);
                if (pair.Key.Equals("x-stainless-helper", StringComparison.OrdinalIgnoreCase) && headers.TryGetValue(pair.Key, out var prior))
                    value = string.Join(", ", (prior + "," + value).Split(',').Select(token => token.Trim()).Where(token => token.Length != 0).Distinct(StringComparer.Ordinal));
                headers[pair.Key] = value;
            }
        headers["content-type"] = "application/json"; // SDK's JSON encoder body headers override client defaults.
        if (projected.Value.TryGetProperty("betas", out var betas))
            headers["anthropic-beta"] = string.Join(',', betas.EnumerateArray().Select(value => value.GetString()));
        if (!(headers.TryGetValue("x-api-key", out var admittedKey) && admittedKey.Length != 0) &&
            !(headers.TryGetValue("authorization", out var admittedBearer) && admittedBearer.Length != 0) && removedAuth.Count == 0)
            throw Fail(AnthropicMessagesKeyAuthRequestFailure.UnsupportedOptions);
        CheckHeaders(headers);
        return headers;
    }
    private string Raw(JsonProperty property) => property.Name == "max_tokens" && _options.MaxTokens is { } count
        ? Number(count) : property.Value.GetRawText();
    // Same bounded finite-double representation as the accepted transcript projector. Full shortest-digit ECMAScript qualification remains open.
    private static string Number(double value)
    {
        if (value == 0) return "0";
        var negative = value < 0; var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOf('E'); var exponent = exponentAt >= 0 ? int.Parse(text.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        var significand = exponentAt >= 0 ? text[..exponentAt] : text; var dot = significand.IndexOf('.');
        var position = (dot >= 0 ? dot : significand.Length) + exponent; var digits = significand.Replace(".", "", StringComparison.Ordinal);
        while (digits.Length > 1 && digits[0] == '0') { digits = digits[1..]; position--; }
        while (digits.Length > 1 && digits[^1] == '0') digits = digits[..^1];
        var prefix = negative ? "-" : "";
        if (position > 0 && position <= 21) return prefix + (position >= digits.Length ? digits + new string('0', position - digits.Length) : digits.Insert(position, "."));
        if (position <= 0 && position > -6) return prefix + "0." + new string('0', -position) + digits;
        var power = position - 1;
        return prefix + digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (power >= 0 ? "+" : "") + power.ToString(CultureInfo.InvariantCulture);
    }
    private void ReadHeaders(JsonData? source, Dictionary<string, string?> destination, ref long supplied)
    {
        if (source is null) return;
        var raw = source.ToString(); supplied += raw.Length;
        if (supplied > _options.MaximumTotalHeaderCharacters) throw Fail(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
        JsonData strict;
        try { strict = JsonData.Parse(raw); }
        catch (Exception) { throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration); }
        if (strict.Value.ValueKind != JsonValueKind.Object) throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration);
        foreach (var property in strict.Value.EnumerateObject())
        {
            HeaderName(property.Name);
            if (property.Name.Equals("host", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("connection", StringComparison.OrdinalIgnoreCase))
                throw Fail(AnthropicMessagesKeyAuthRequestFailure.UnsupportedOptions);
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration);
            string? value;
            try { value = property.Value.GetString(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            { throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration); }
            if (value is not null) HeaderValue(value);
            if (property.Name.Length > _options.MaximumHeaderCharacters || value?.Length > _options.MaximumHeaderCharacters)
                throw Fail(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
            destination[property.Name] = value;
            if (destination.Count > _options.MaximumHeaders) throw Fail(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
        }
    }
    private void CheckHeaders(Dictionary<string, string> headers)
    {
        long total = 0;
        foreach (var header in headers)
        {
            HeaderName(header.Key); HeaderValue(header.Value); total += header.Key.Length + (long)header.Value.Length;
            if (header.Key.Length > _options.MaximumHeaderCharacters || header.Value.Length > _options.MaximumHeaderCharacters ||
                total > _options.MaximumTotalHeaderCharacters) throw Fail(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
        }
        if (headers.Count > _options.MaximumHeaders) throw Fail(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
    }
    private void CheckDepth(JsonElement value, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return;
        if (++depth > _options.MaximumPayloadDepth) throw Fail(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
        if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) CheckDepth(property.Value, depth, token);
        else foreach (var item in value.EnumerateArray()) CheckDepth(item, depth, token);
    }
    private static void HeaderName(string value)
    {
        if (value.Length == 0 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character))))
            throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration);
    }
    private static void HeaderValue(string value)
    {
        if (value.Any(character => character is not '\t' && (character is < ' ' or > '~')))
            throw Fail(AnthropicMessagesKeyAuthRequestFailure.InvalidConfiguration);
    }
    private static AnthropicMessagesKeyAuthRequestException Fail(AnthropicMessagesKeyAuthRequestFailure failure) => new(failure);
}
