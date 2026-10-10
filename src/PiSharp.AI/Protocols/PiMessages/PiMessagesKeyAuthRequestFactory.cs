using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.PiMessages;

/// <summary>Builds one independently owned request. Does not send, mutate history or own the client.</summary>
public sealed class PiMessagesKeyAuthRequestFactory
{
    private readonly ModelDescriptor _model;
    private readonly PiMessagesOptions _options;
    private readonly Uri _endpoint;
    public PiMessagesKeyAuthRequestFactory(ModelDescriptor model, PiMessagesOptions options)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(options); options.Validate();
        var metadata = PiMessagesData.Admit(options.ModelMetadata, options).Value;
        if (model.Api != "pi-messages" || PiMessagesData.String(metadata, "id") != model.Id || PiMessagesData.String(metadata, "api") != model.Api ||
            PiMessagesData.String(metadata, "provider") != model.Provider) throw PiMessagesData.Fail(PiMessagesFailure.InvalidConfiguration);
        var url = PiMessagesData.String(metadata, "baseUrl").TrimEnd('/') + "/messages";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0)
            throw PiMessagesData.Fail(PiMessagesFailure.InvalidConfiguration);
        if (options.Debug) endpoint = SetDebugQuery(endpoint, options.MaximumPayloadBytes);
        _model = model; _options = options; _endpoint = endpoint;
    }
    public Uri Endpoint => _endpoint;
    public async ValueTask<HttpRequestMessage> CreateAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.ApiKey)) throw new PiMessagesException(PiMessagesFailure.MissingKey, $"No API key provided for provider \"{_model.Provider}\"");
        if (request is null || request.Model != _model || request.Messages.IsDefault) throw PiMessagesData.Fail(PiMessagesFailure.InvalidRequest);
        cancellationToken.ThrowIfCancellationRequested();
        var messages = new JsonArray();
        foreach (var entry in request.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || entry.WireBody is null || entry.WireBody.Value.ValueKind != JsonValueKind.Object ||
                PiMessagesData.String(entry.WireBody.Value, "role") != entry.Role) throw PiMessagesData.Fail(PiMessagesFailure.InvalidRequest);
            messages.Add(JsonNode.Parse(PiMessagesData.Admit(entry.WireBody, _options).ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions));
        }
        var values = new JsonObject(); var undefined = ImmutableArray.CreateBuilder<string>();
        void Field(string name, JsonNode? value) { if (value is null) undefined.Add("/options/" + name); else values[name] = value; }
        Field("temperature", _options.Temperature is { } temperature ? JsonValue.Create(temperature) : null);
        Field("maxTokens", _options.MaxTokens is { } maximum ? JsonValue.Create(maximum) : null);
        Field("reasoning", _options.Reasoning is { } reasoning ? JsonValue.Create(reasoning) : null);
        Field("cacheRetention", ResolveCache() is { } cache ? JsonValue.Create(cache) : null);
        Field("sessionId", (_options.SessionId ?? request.SessionId) is { } session ? JsonValue.Create(session) : null);
        if (_options.ToolChoice is { } toolChoice) values["toolChoice"] = JsonNode.Parse(PiMessagesData.Admit(toolChoice, _options).ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions);
        else undefined.Add("/options/toolChoice");
        var payload = PiMessagesData.Admit(JsonData.Parse(new JsonObject { ["model"] = _model.Id, ["context"] = new JsonObject { ["messages"] = messages }, ["options"] = values }.ToJsonString()), _options);
        if (_options.Hooks.OnPayload is { } callback)
        {
            var replacement = await callback(new(payload, undefined.Order(StringComparer.Ordinal).ToImmutableArray()), _model, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); if (replacement is not null) payload = PiMessagesData.Admit(replacement, _options);
        }
        var bytes = Encoding.UTF8.GetBytes(EcmaScriptJsonProjection.Project(payload, new(MaximumInputCharacters: _options.MaximumPayloadBytes,
            MaximumInputBytes: _options.MaximumPayloadBytes, MaximumOutputCharacters: _options.MaximumPayloadBytes, MaximumOutputBytes: _options.MaximumPayloadBytes,
            MaximumDepth: _options.MaximumJsonDepth, MaximumStringCharacters: _options.MaximumPayloadBytes,
            // Node and number counts grow with the message count; the payload budget bounds them.
            MaximumNodes: _options.MaximumPayloadBytes, MaximumNumbers: _options.MaximumPayloadBytes, MaximumTotalNumberCharacters: _options.MaximumPayloadBytes)));
        HttpRequestMessage? owned = null;
        try
        {
            owned = new(HttpMethod.Post, _endpoint) { Content = new ByteArrayContent(bytes) };
            var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["authorization"] = "Bearer " + _options.ApiKey, ["accept"] = "text/event-stream", ["content-type"] = "application/json" };
            if (_options.Headers is { } configured)
            {
                var source = PiMessagesData.Admit(configured, _options).Value;
                if (source.ValueKind != JsonValueKind.Object) throw PiMessagesData.Fail(PiMessagesFailure.UnsupportedContent);
                var normalized = new Dictionary<string, (string Name, string Value)>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in source.EnumerateObject())
                {
                    normalized.Remove(field.Name);
                    if (field.Value.ValueKind == JsonValueKind.Null) continue;
                    if (field.Value.ValueKind != JsonValueKind.String) throw PiMessagesData.Fail(PiMessagesFailure.UnsupportedContent);
                    normalized.Add(field.Name, (field.Name, field.Value.GetString()!));
                }
                foreach (var field in normalized.Values) headers[field.Name] = field.Value;
            }
            if (headers.Count > _options.MaximumHeaders) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
            foreach (var (name, value) in headers)
            {
                if (name.Length + value.Length > _options.MaximumHeaderCharacters) throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
                if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) { if (!owned.Content.Headers.TryAddWithoutValidation(name, value)) throw PiMessagesData.Fail(PiMessagesFailure.UnsupportedContent); }
                else if (!owned.Headers.TryAddWithoutValidation(name, value)) throw PiMessagesData.Fail(PiMessagesFailure.UnsupportedContent);
            }
            cancellationToken.ThrowIfCancellationRequested(); return owned;
        }
        catch { owned?.Dispose(); throw; }
    }
    private string? ResolveCache()
    {
        if (!string.IsNullOrEmpty(_options.CacheRetention)) return _options.CacheRetention;
        string? scoped = null;
        if (_options.Environment is { } environment && environment.Value.ValueKind == JsonValueKind.Object &&
            environment.Value.TryGetProperty("PI_CACHE_RETENTION", out var field) && field.ValueKind == JsonValueKind.String) scoped = field.GetString();
        var lookup = _options.EnvironmentLookup ?? (name => System.Environment.GetEnvironmentVariable(name));
        var value = !string.IsNullOrEmpty(scoped) ? scoped : lookup("PI_CACHE_RETENTION");
        return value == "long" ? "long" : null;
    }
    private static Uri SetDebugQuery(Uri endpoint, int maximumUrlBytes)
    {
        // Source constructs URL after raw baseUrl + "/messages", then calls
        // url.searchParams.set("debug", "1"). Preserve that order even for query/fragment bases.
        var query = endpoint.Query.Length == 0 ? "" : endpoint.Query[1..];
        var fields = new List<KeyValuePair<string, string>>();
        var replaced = false;
        foreach (var segment in query.Split('&'))
        {
            if (segment.Length == 0) continue;
            var equals = segment.IndexOf('=');
            var name = DecodeForm(equals < 0 ? segment : segment[..equals]);
            var value = DecodeForm(equals < 0 ? "" : segment[(equals + 1)..]);
            if (name == "debug")
            {
                if (replaced) continue;
                value = "1"; replaced = true;
            }
            fields.Add(new(name, value));
        }
        if (!replaced) fields.Add(new("debug", "1"));
        var encodedQuery = string.Join("&", fields.Select(field => EncodeForm(field.Key) + "=" + EncodeForm(field.Value)));
        if (endpoint.Fragment.Length == 0)
        {
            // Ordinary Uri validation/normalization has already admitted the authority
            // and path. Only our ASCII form encoder supplies the exact query. A raw
            // fragment is never admitted to the noncanonicalizing representation.
            var exactUrl = endpoint.GetLeftPart(UriPartial.Path) + "?" + encodedQuery;
            if (exactUrl.Length > maximumUrlBytes || Encoding.UTF8.GetByteCount(exactUrl) > maximumUrlBytes)
                throw PiMessagesData.Fail(PiMessagesFailure.ResourceLimit);
            if (!Uri.TryCreate(exactUrl, UriKind.Absolute, out var validated) || validated.Scheme != endpoint.Scheme ||
                validated.Authority != endpoint.Authority || validated.UserInfo.Length != 0 || validated.Fragment.Length != 0)
                throw PiMessagesData.Fail(PiMessagesFailure.InvalidConfiguration);
            return new Uri(exactUrl, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        }
        // Fragment-bearing URL representation still uses the ordinary validated Uri;
        // disabling canonicalization there would treat the fragment as request query.
        var builder = new UriBuilder(endpoint) { Query = encodedQuery };
        return builder.Uri;
    }
    private static string DecodeForm(string text)
    {
        // WHATWG form decoding replaces '+', percent-decodes bytes, then uses
        // UTF-8 replacement decoding. Uri.UnescapeDataString does not do this combination.
        var input = Encoding.UTF8.GetBytes(text.Replace('+', ' '));
        var output = new byte[input.Length]; var length = 0;
        static int Hex(byte value) => value switch
        {
            >= 48 and <= 57 => value - 48,
            >= 65 and <= 70 => value - 65 + 10,
            >= 97 and <= 102 => value - 97 + 10,
            _ => -1
        };
        for (var index = 0; index < input.Length; index++)
        {
            if (input[index] == 37 && index + 2 < input.Length && Hex(input[index + 1]) is var high && high >= 0 &&
                Hex(input[index + 2]) is var low && low >= 0)
            { output[length++] = (byte)(high * 16 + low); index += 2; }
            else output[length++] = input[index];
        }
        return new UTF8Encoding(false, false).GetString(output, 0, length);
    }
    private static string EncodeForm(string text)
    {
        var output = new StringBuilder();
        const string hex = "0123456789ABCDEF";
        foreach (var value in Encoding.UTF8.GetBytes(text))
        {
            if (value is >= 65 and <= 90 or >= 97 and <= 122 or >= 48 and <= 57 or 42 or 45 or 46 or 95)
                output.Append((char)value);
            else if (value == 32) output.Append('+');
            else { output.Append('%'); output.Append(hex[value >> 4]); output.Append(hex[value & 15]); }
        }
        return output.ToString();
    }
}
