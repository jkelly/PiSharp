// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/sdk.ts (transformHeaders ->
// before_provider_headers, onPayload -> before_provider_request, onResponse -> after_provider_response, onProviderStreamEvent ->
// provider_stream_event) and core/extensions/runner.ts (emitBeforeProviderHeaders, emitBeforeProviderRequest, emit).
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>The provider request hooks Pi gives extensions, applied at the HTTP boundary of every live provider route: the JSON
/// request body is the provider payload, the request headers are the provider headers, and each server-sent event's JSON
/// <c>data</c> is a provider stream event. Requests of non-JSON bodies and non-SSE responses pass the body hooks by.</summary>
internal sealed class ExtensionProviderHttpHandler : DelegatingHandler
{
    internal static readonly string[] Topics = ["before_provider_request", "before_provider_headers", "after_provider_response", "provider_stream_event"];
    private readonly ExtensionRegistry _registry;
    private readonly ExtensionRegistrySnapshot _captured;
    private readonly Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? _report;
    private readonly PiSharp.Contracts.ModelDescriptor _model;

    internal ExtensionProviderHttpHandler(HttpMessageHandler inner, ExtensionRegistry registry, ExtensionRegistrySnapshot captured,
        PiSharp.Contracts.ModelDescriptor model, Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report) : base(inner)
    { _registry = registry; _captured = captured; _model = model; _report = report; }

    internal static bool Applies(ExtensionRegistry registry, ExtensionRegistrySnapshot captured) =>
        registry.HasEventHandlers(captured, "before_provider_request") || registry.HasEventHandlers(captured, "before_provider_headers") ||
        registry.HasObservers(captured, "after_provider_response") || registry.HasObservers(captured, "provider_stream_event");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_registry.HasEventHandlers(_captured, "before_provider_request") && request.Content is { } content &&
            content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var text = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonData? payload = null;
            try { payload = JsonData.Parse(text); } catch (JsonException) { }
            if (payload is not null)
            {
                var reduced = await _registry.ReduceEventAsync(_captured, "before_provider_request", Event("before_provider_request", writer => Raw(writer, "payload", payload)),
                    (current, result) => Event("before_provider_request", writer => Raw(writer, "payload", result)), _report, cancellationToken).ConfigureAwait(false);
                var final = JsonData.FromElement(reduced.Value.GetProperty("payload"));
                if (final.ToString() != payload.ToString())
                {
                    var replacement = new StringContent(final.Value.GetRawText(), Encoding.UTF8);
                    replacement.Headers.ContentType = content.Headers.ContentType;
                    request.Content = replacement;
                }
            }
        }
        if (_registry.HasEventHandlers(_captured, "before_provider_headers")) await ApplyHeadersAsync(request, cancellationToken).ConfigureAwait(false);
        // A SigV4-signed request (Bedrock) is signed again over the payload and headers the hooks left, as the SDK signs after them.
        if ((_registry.HasEventHandlers(_captured, "before_provider_request") || _registry.HasEventHandlers(_captured, "before_provider_headers")) &&
            request.Options.TryGetValue(PiSharp.AI.Providers.ProviderRequestSigning.Resign, out var resign))
            await resign(request, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (_registry.HasObservers(_captured, "after_provider_response"))
            await Observe("after_provider_response", Event("after_provider_response", writer =>
            {
                writer.WriteNumber("status", (int)response.StatusCode); writer.WritePropertyName("headers"); writer.WriteStartObject();
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var header in response.Headers.Concat(response.Content.Headers)) headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
                foreach (var header in headers) writer.WriteString(header.Key, header.Value);
                writer.WriteEndObject();
            }), cancellationToken).ConfigureAwait(false);
        if (_registry.HasObservers(_captured, "provider_stream_event") &&
            response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            var original = response.Content;
            var stream = new StreamEventTee(await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), this);
            var tee = new StreamContent(stream);
            foreach (var header in original.Headers) tee.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = tee;
        }
        else if (_registry.HasObservers(_captured, "provider_stream_event") &&
            response.Content.Headers.ContentType?.MediaType?.Equals("application/vnd.amazon.eventstream", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Bedrock ConverseStream: each decoded event-stream message is the SDK's output union member, { eventType: payload }.
            var original = response.Content;
            var tee = new StreamContent(new EventStreamTee(await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), this));
            foreach (var header in original.Headers) tee.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = tee;
        }
        return response;
    }

    private async Task ApplyHeadersAsync(HttpRequestMessage request, CancellationToken token)
    {
        var original = new JsonObject();
        foreach (var header in request.Headers) original[header.Key] = string.Join(", ", header.Value);
        var initial = Event("before_provider_headers", writer => { writer.WritePropertyName("headers"); writer.WriteRawValue(original.ToJsonString()); });
        var reduced = await _registry.ReduceEventAsync(_captured, "before_provider_headers", initial,
            (current, result) => result.Value.ValueKind == JsonValueKind.Object ? Event("before_provider_headers", writer => Raw(writer, "headers", result)) : null, _report, token).ConfigureAwait(false);
        var headers = reduced.Value.GetProperty("headers");
        // Handlers edit the headers object: a removed or null header is deleted, a string value is set.
        foreach (var name in original.Select(pair => pair.Key).ToArray())
            if (!headers.TryGetProperty(name, out var kept) || kept.ValueKind == JsonValueKind.Null) request.Headers.Remove(name);
        foreach (var header in headers.EnumerateObject())
            if (header.Value.ValueKind == JsonValueKind.String && (!original.TryGetPropertyValue(header.Name, out var before) || before!.GetValue<string>() != header.Value.GetString()))
            { request.Headers.Remove(header.Name); request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString()); }
    }

    private async ValueTask Observe(string topic, JsonData value, CancellationToken token)
    {
        try { await _registry.DispatchObservationsReportingAsync(_captured, topic, value, _report, token).ConfigureAwait(false); }
        catch (Exception) when (!token.IsCancellationRequested) { }
    }

    /// <summary>Source ProviderStreamEvent: <c>{data, type, provider, api, model}</c> for each parsed provider event.</summary>
    internal ValueTask ObserveStreamEventAsync(JsonData data, CancellationToken token) => Observe("provider_stream_event", Json(writer =>
    {
        Raw(writer, "data", data); writer.WriteString("type", "provider_stream_event");
        writer.WriteString("provider", _model.Provider); writer.WriteString("api", _model.Api); writer.WriteString("model", _model.Id);
    }), token);

    private static JsonData Event(string type, Action<Utf8JsonWriter> fields) => Json(writer => { writer.WriteString("type", type); fields(writer); });
    private static JsonData Json(Action<Utf8JsonWriter> write) => NativeSessionEventBinding.Json(write);
    private static void Raw(Utf8JsonWriter writer, string name, JsonData value) { writer.WritePropertyName(name); writer.WriteRawValue(value.ToString()); }

    /// <summary>Passes Bedrock's AWS event-stream bytes through unchanged, decoding each complete message as it is read: an event is
    /// <c>{ [:event-type]: payload }</c> and a modeled exception <c>{ [:exception-type]: payload }</c>, as the SDK's ConverseStream
    /// output yields them (the deserializer drops the service's random padding member <c>p</c>).</summary>
    private sealed class EventStreamTee(Stream inner, ExtensionProviderHttpHandler owner) : Stream
    {
        private readonly MemoryStream _pending = new(); private bool _broken;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var count = await inner.ReadAsync(buffer, token).ConfigureAwait(false);
            if (_broken || count == 0) return count;
            _pending.Write(buffer.Span[..count]);
            while (!_broken && _pending.Length >= PiSharp.AI.Protocols.Bedrock.AwsEventStream.PreludeLength)
            {
                var bytes = _pending.GetBuffer();
                var total = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
                if (total < PiSharp.AI.Protocols.Bedrock.AwsEventStream.MinimumMessageLength || total > PiSharp.AI.Protocols.Bedrock.AwsEventStream.MaximumMessageLength)
                { _broken = true; break; }
                if (_pending.Length < total) break;
                JsonData? data = null;
                try
                {
                    var message = PiSharp.AI.Protocols.Bedrock.AwsEventStream.Decode(bytes.AsSpan(0, total));
                    var kind = message.HeaderString(":message-type") switch
                    {
                        "event" => message.HeaderString(":event-type"),
                        "exception" => message.HeaderString(":exception-type"),
                        _ => null
                    };
                    if (kind is not null && (message.Payload.Length == 0 ? new JsonObject() : JsonNode.Parse(message.Payload)) is JsonObject payload)
                    {
                        if (message.HeaderString(":message-type") == "event") payload.Remove("p");
                        data = JsonData.Parse(new JsonObject { [kind] = payload }.ToJsonString());
                    }
                }
                catch (Exception error) when (error is PiSharp.AI.Protocols.Bedrock.AwsEventStreamException or JsonException) { _broken = true; }
                var rest = _pending.Length - total;
                Buffer.BlockCopy(bytes, total, bytes, 0, (int)rest); _pending.SetLength(rest);
                if (data is not null) await owner.ObserveStreamEventAsync(data, token).ConfigureAwait(false);
            }
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); _pending.Dispose(); } base.Dispose(disposing); }
    }

    /// <summary>Passes the provider's SSE bytes through unchanged, parsing complete <c>data:</c> lines as they are read.</summary>
    private sealed class StreamEventTee(Stream inner, ExtensionProviderHttpHandler owner) : Stream
    {
        private readonly StringBuilder _line = new(); private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _data = new();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var count = await inner.ReadAsync(buffer, token).ConfigureAwait(false);
            var chars = new char[_decoder.GetCharCount(buffer.Span[..count], count == 0)];
            _decoder.GetChars(buffer.Span[..count], chars, count == 0);
            foreach (var character in chars)
            {
                if (character != '\n') { if (character != '\r') _line.Append(character); continue; }
                var line = _line.ToString(); _line.Clear();
                if (line.Length == 0) { await FlushEventAsync(token).ConfigureAwait(false); continue; }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                { if (_data.Length > 0) _data.Append('\n'); _data.Append(line.AsSpan(line.StartsWith("data: ", StringComparison.Ordinal) ? 6 : 5)); }
            }
            if (count == 0) await FlushEventAsync(token).ConfigureAwait(false);
            return count;
        }
        private async ValueTask FlushEventAsync(CancellationToken token)
        {
            if (_data.Length == 0) return;
            var text = _data.ToString(); _data.Clear();
            JsonData? data = null;
            try { data = JsonData.Parse(text); } catch (JsonException) { }
            if (data is not null) await owner.ObserveStreamEventAsync(data, token).ConfigureAwait(false);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
