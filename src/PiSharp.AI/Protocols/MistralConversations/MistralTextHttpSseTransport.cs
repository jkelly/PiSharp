using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.MistralConversations;

/// <summary>Bound Mistral text/function-tool fetch/SSE composition. Borrows client; no ambient auth, retries or tool execution authority.</summary>
public sealed partial class MistralTextHttpSseTransport : IChatTransport, IModelProvider, IThinkingLevelTransport
{
    private readonly HttpClient client;
    private readonly ModelDescriptor model;
    private readonly MistralTextOptions options;
    public string ProviderId => "mistral";
    public IReadOnlyList<ModelDescriptor> Models { get; }
    public IChatTransport Transport => this;
    public MistralTextHttpSseTransport(HttpClient client, ModelDescriptor model, MistralTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(options);
        if (model.Provider != "mistral" || model.Api != "mistral-conversations" || string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 1024) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Invalid bound Mistral identity.");
        options.Validate(); this.client = client; this.model = model; this.options = options; Models = ImmutableArray.Create(model);
    }
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model != model) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Mistral request identity differs from bound model.");
        return new Invocation(this, request, cancellationToken);
    }
    private sealed class Invocation(MistralTextHttpSseTransport owner, ChatRequest request, CancellationToken token) : IAsyncEnumerable<StreamEvent>
    {
        private int claimed;
        public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0) throw new StreamProtocolException("Mistral invocation has one reader.");
            return owner.Run(request, token).GetAsyncEnumerator(cancellationToken);
        }
    }
    private async IAsyncEnumerable<StreamEvent> Run(ChatRequest request, [EnumeratorCancellation] CancellationToken caller)
    {
        using var timeout = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, timeout.Token);
        // StreamOptions.sessionId: the request's session, unless the transport was configured with one.
        var options = request.SessionId is { } session && this.options.SessionId is null ? this.options with { SessionId = session } : this.options;
        var token = linked.Token; var state = new TextState(request, options);
        HttpRequestMessage? send = null; HttpResponseMessage? response = null; Stream? body = null; IAsyncEnumerator<JsonData>? reader = null;
        Exception? failure = null, cleanup = null; var completed = false;
        try
        {
            try
            {
                token.ThrowIfCancellationRequested(); var payload = Admit(() => BuildPayload(request));
                if (string.IsNullOrEmpty(options.ApiKey)) throw Fail(NativeChatFailureCode.UnsupportedFeature, "No API key for provider: mistral");
                if (options.OnPayload is { } hook)
                {
                    var replacement = await AwaitSource(() => hook(payload, model, token)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (replacement is not null) { Admit(() => { AdmitPayload(replacement.Value, camel: true); return true; }); payload = replacement; }
                }
                var wire = JsonNode.Parse(payload.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!.AsObject();
                MapRequestOptions(wire);
                foreach (var message in wire["messages"]!.AsArray())
                {
                    foreach (var (from, to) in new[] { ("toolCalls", "tool_calls"), ("toolCallId", "tool_call_id") })
                        if (message!.AsObject().ContainsKey(from)) { var value = message[from]; message.AsObject().Remove(from); message[to] = value; }
                    if (message!["content"] is JsonArray parts)
                        foreach (var part in parts)
                            if (part!.AsObject().ContainsKey("imageUrl"))
                            { var image = part["imageUrl"]; part.AsObject().Remove("imageUrl"); part["image_url"] = image; }
                }
                var bytes = Encoding.UTF8.GetBytes(wire.ToJsonString()); Limit(bytes.Length, options.MaximumPayloadBytes);
                var baseUri = new Uri(options.BaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
                send = new(HttpMethod.Post, new Uri(baseUri, "v1/chat/completions")) { Content = new ByteArrayContent(bytes) };
                var key = options.ApiKey!;
                if (key.Contains('\r') || key.Contains('\n') || key.Length + 7 > options.MaximumHeaderCharacters) throw Fail(NativeChatFailureCode.ResourceLimit, "Mistral credential/header limit.");
                send.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key); send.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
                send.Headers.TryAddWithoutValidation("Accept", "text/event-stream"); send.Content.Headers.ContentType = new("application/json");
                ApplyHeaders(send, options.ModelHeaders); ApplyHeaders(send, options.Headers);
                var explicitAffinity = new[] { options.ModelHeaders, options.Headers }.Any(headers =>
                    headers?.Keys.Any(name => name.Equals("x-affinity", StringComparison.OrdinalIgnoreCase)) == true);
                if (options.CachePrompt && !string.IsNullOrEmpty(options.SessionId) && !explicitAffinity)
                    send.Headers.TryAddWithoutValidation("x-affinity", options.SessionId);
                ObserveHeaders(send.Headers.Concat(send.Content.Headers));
                token.ThrowIfCancellationRequested();
                timeout.CancelAfter(options.TimeoutMilliseconds);
                response = await AwaitSource(() => new ValueTask<HttpResponseMessage>(client.SendAsync(send, HttpCompletionOption.ResponseHeadersRead, token))).ConfigureAwait(false);
                var observed = ObserveHeaders(response.Headers.Concat(response.Content.Headers));
                if (options.OnResponse is { } responseHook) await AwaitSource(() => responseHook(JsonData.Parse(new JsonObject { ["status"] = (int)response.StatusCode, ["headers"] = observed }.ToJsonString()), model, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); body = await AwaitSource(() => new ValueTask<Stream>(response.Content.ReadAsStreamAsync(token))).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw await HttpError(response, body, token).ConfigureAwait(false);
                if (body is null) throw Fail(NativeChatFailureCode.SourceFailed, "Mistral response has no body");
                reader = Decode(body, token).GetAsyncEnumerator(token);
            }
            catch (Exception error) { failure = error; }
            if (failure is null) yield return new StreamStarted(state.Message(StopReason.Pending));
            while (failure is null)
            {
                ImmutableArray<StreamEvent> frames = []; var moved = false;
                try
                {
                    token.ThrowIfCancellationRequested(); moved = await reader!.MoveNextAsync().ConfigureAwait(false);
                    if (moved)
                    {
                        var chunk = reader.Current;
                        if (options.OnProviderStreamEvent is { } hook) await AwaitSource(() => hook(chunk, model, token)).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested(); frames = state.Convert(chunk);
                    }
                }
                catch (Exception error) { failure = error; }
                if (failure is not null || !moved) break;
                foreach (var frame in frames) yield return frame;
            }
            completed = true;
        }
        finally
        {
            if (reader is not null) try { await reader.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanup ??= error; }
            if (body is not null) try { await body.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanup ??= error; }
            try { response?.Dispose(); } catch (Exception error) { cleanup ??= error; }
            try { send?.Dispose(); } catch (Exception error) { cleanup ??= error; }
            if (!completed && cleanup is not null) throw Fail(NativeChatFailureCode.CleanupFailed, "Mistral iterator cleanup failed.");
        }
        // End and terminal are emitted only after the original callback/send/pull/disposal operations settle.
        foreach (var frame in state.Finish()) yield return frame;
        var reason = state.Reason; var code = NativeChatFailureCode.SourceFailed; string? text = null;
        if (caller.IsCancellationRequested) { reason = StopReason.Aborted; code = NativeChatFailureCode.Cancelled; text = "Request was aborted"; }
        else if (failure is not null) { reason = StopReason.Error; code = MistralNativeDiagnostics.FromException(failure).Code; text = failure is MistralTextException ? failure.Message : timeout.IsCancellationRequested ? "Mistral request timed out." : "Mistral operation failed."; }
        else if (reason == StopReason.Pending) { reason = StopReason.Error; code = NativeChatFailureCode.UnexpectedEof; text = "Mistral stream ended without a finish reason"; }
        else if (reason == StopReason.Error) { code = NativeChatFailureCode.ProviderError; text = state.ProviderError; }
        if (cleanup is not null && text is null) { reason = StopReason.Error; code = NativeChatFailureCode.CleanupFailed; text = "Mistral invocation cleanup failed."; }
        StreamTerminalEvent terminal = text is null ? new StreamDone(reason, state.Message(reason)) : new StreamError(reason, state.Message(reason, text)) { NativeDiagnostic = MistralNativeDiagnostics.FromCode(code) };
        if (cleanup is not null) terminal = terminal with { NativeCleanupDiagnostic = MistralNativeDiagnostics.FromCode(NativeChatFailureCode.CleanupFailed) };
        yield return terminal;
    }
    private JsonData BuildPayload(ChatRequest request)
    {
        var messages = new JsonArray(); long total = 0; var ids = new MistralToolIds();
        var replay = ResolveReplay(request, ids);
        for (var i = 0; i < replay.Length; i++)
        {
            var entry = replay[i]; var value = entry.WireBody.Value;
            Limit(Encoding.UTF8.GetByteCount(value.GetRawText()), options.MaximumPayloadBytes); CheckDepth(value, 0);
            if (value.ValueKind != JsonValueKind.Object) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral transcript value.");
            if (entry.Role is "assistant" or "toolResult")
            {
                var history = ProjectToolHistory(entry);
                if (history is not null) { total += history.ToJsonString().Length; Limit(total, options.MaximumContentCharacters); messages.Add(history); }
                continue;
            }
            if (entry.Role is not ("system" or "user")) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral replay role.");
            // Pi v1.1.0 maps only role and content; other stored fields (such as a system message's "offlineApi") are ignored.
            if (value.TryGetProperty("role", out var role) && role.GetString() != entry.Role) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Mistral transcript role mismatch.");
            var content = value.GetProperty("content"); JsonNode? node;
            if (content.ValueKind == JsonValueKind.String) node = JsonValue.Create(Sanitize(content.GetString()!));
            else if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = new JsonArray(); foreach (var part in content.EnumerateArray())
                { Limit(parts.Count + 1, options.MaximumContentBlocks); parts.Add(ProjectInputPart(part)); }
                node = entry.Role == "system" ? JsonValue.Create(string.Join("\n", parts.Select(x => x!["text"]!.GetValue<string>()))) : parts;
            }
            else throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral text content.");
            if (entry.Role == "system") node = JsonValue.Create(Sanitize(SystemText(value, i != 0)));
            total += node!.ToJsonString().Length; Limit(total, options.MaximumContentCharacters);
            if (entry.Role == "system" && node is JsonValue s && s.GetValue<string>().Length == 0 || node is JsonArray a && a.Count == 0) continue;
            messages.Add(new JsonObject { ["role"] = entry.Role, ["content"] = node });
        }
        var payload = new JsonObject { ["model"] = model.Id, ["stream"] = true, ["messages"] = messages };
        var tools = ProjectTools(request); if (tools.Count > 0) payload["tools"] = tools;
        if (options.ToolChoice is { } choice) payload["toolChoice"] = Choice(choice.Value);
        AddSimpleOptions(payload, request);
        if (options.Temperature is { } temperature) payload["temperature"] = temperature;
        if (options.MaxTokens is { } max) payload["maxTokens"] = max;
        var data = JsonData.Parse(payload.ToJsonString()); AdmitPayload(data.Value, true); return data;
    }
    private void AdmitPayload(JsonElement value, bool camel)
    {
        Limit(Encoding.UTF8.GetByteCount(value.GetRawText()), options.MaximumPayloadBytes); CheckDepth(value, 0);
        if (value.ValueKind != JsonValueKind.Object || value.GetProperty("model").GetString() != model.Id || value.GetProperty("stream").ValueKind != JsonValueKind.True) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral payload replacement.");
        // Original MistralChatPayload has an unknown-valued index signature. The hook's
        // extra root fields survive the wire conversion; byte/depth and core admission
        // still apply to the complete replacement before any HTTP effect.
        foreach (var field in new[] { "promptMode", "reasoningEffort", "promptCacheKey" })
            if (value.TryGetProperty(field, out var text) && text.ValueKind != JsonValueKind.String)
                throw Fail(NativeChatFailureCode.UnsupportedFeature, "Invalid Mistral reasoning/cache option.");
        if (value.TryGetProperty("toolChoice", out var choice)) _ = Choice(choice);
        if (value.TryGetProperty("tools", out var tools))
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.GetProperty("type").GetString() != "function") throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral tool kind.");
                var function = tool.GetProperty("function");
                if (string.IsNullOrEmpty(function.GetProperty("name").GetString()) || function.GetProperty("parameters").ValueKind != JsonValueKind.Object)
                    throw Fail(NativeChatFailureCode.UnsupportedFeature, "Invalid Mistral function declaration.");
            }
        foreach (var name in new[] { "temperature", camel ? "maxTokens" : "max_tokens" }) if (value.TryGetProperty(name, out var number) && (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var n) || !double.IsFinite(n))) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral numeric option.");
        var index = 0; long size = 0;
        foreach (var message in value.GetProperty("messages").EnumerateArray())
        {
            foreach (var p in message.EnumerateObject()) if (p.Name is not ("role" or "content" or "toolCalls" or "toolCallId" or "name" or "prefix")) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral payload message field.");
            var role = message.GetProperty("role").GetString(); if (role is not ("system" or "user" or "assistant" or "tool")) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral payload role.");
            if (message.TryGetProperty("toolCalls", out var calls)) { if (role != "assistant") throw Fail(NativeChatFailureCode.UnsupportedFeature, "Invalid Mistral tool-call role."); ValidateToolCalls(calls); }
            // A nameless call's result replays with name "" (owner decision 13).
            if (role == "tool" && (string.IsNullOrEmpty(message.GetProperty("toolCallId").GetString()) || message.GetProperty("name").GetString() is null))
                throw Fail(NativeChatFailureCode.UnsupportedFeature, "Invalid Mistral tool result.");
            if (!message.TryGetProperty("content", out var content))
            { if (role != "assistant" || !message.TryGetProperty("toolCalls", out _)) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Missing Mistral content."); index++; continue; }
            if (content.ValueKind == JsonValueKind.Array && role is "user" or "assistant" or "tool") foreach (var part in content.EnumerateArray())
            {
                Limit(content.GetArrayLength(), options.MaximumContentBlocks);
                if (role == "assistant") ValidateReplayPart(part); else ValidateInputPart(part);
            }
            else if (content.ValueKind != JsonValueKind.String) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral payload content.");
            size += content.GetRawText().Length; Limit(size, options.MaximumContentCharacters); index++;
        }
    }
    private void CheckDepth(JsonElement value, int depth) { Limit(depth, options.MaximumJsonDepth); if (value.ValueKind == JsonValueKind.Object) foreach (var p in value.EnumerateObject()) CheckDepth(p.Value, depth + 1); else if (value.ValueKind == JsonValueKind.Array) foreach (var p in value.EnumerateArray()) CheckDepth(p, depth + 1); }
    private JsonObject ObserveHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> fields)
    {
        var result = new JsonObject(); long total = 0; var count = 0;
        foreach (var field in fields) { var text = string.Join(", ", field.Value); Limit(++count, options.MaximumHeaders); Limit(field.Key.Length + text.Length, options.MaximumHeaderCharacters); total += field.Key.Length + text.Length; Limit(total, options.MaximumTotalHeaderCharacters); result[field.Key.ToLowerInvariant()] = text; }
        return result;
    }
    private static void ApplyHeaders(HttpRequestMessage request, ImmutableDictionary<string, string?>? headers)
    {
        if (headers is null) return;
        var content = request.Content ?? throw Fail(NativeChatFailureCode.UnsupportedFeature, "Missing Mistral request content.");
        foreach (var pair in headers)
        {
            foreach (var name in request.Headers.Select(header => header.Key).Where(name => name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).ToArray()) request.Headers.Remove(name);
            foreach (var name in content.Headers.Select(header => header.Key).Where(name => name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).ToArray()) content.Headers.Remove(name);
            if (pair.Value is not null && !request.Headers.TryAddWithoutValidation(pair.Key, pair.Value) &&
                !content.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral header binding.");
        }
    }
    private async ValueTask<MistralTextException> HttpError(HttpResponseMessage response, Stream body, CancellationToken token)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await AwaitSource(() => body.ReadAsync(buffer.AsMemory(), token)).ConfigureAwait(false)) != 0) { Limit(bytes.Length + count, options.MaximumErrorBytes); bytes.Write(buffer, 0, count); }
        var text = Encoding.UTF8.GetString(bytes.ToArray()).Trim(EcmaWhitespace);
        var suffix = text.Length > 0 ? text.Length > 4000 ? text[..4000] + $"... [truncated {text.Length - 4000} chars]" : text : (string.IsNullOrEmpty(response.ReasonPhrase) ? $"Request failed with status {(int)response.StatusCode}" : response.ReasonPhrase);
        return Fail(NativeChatFailureCode.ProviderError, $"Mistral API error ({(int)response.StatusCode}): {suffix}");
    }
    private static readonly Regex Boundary = new("\\r\\n\\r\\n|\\r\\n\\r|\\r\\n\\n|\\r\\r\\n|\\n\\r\\n|\\r\\r|\\n\\r|\\n\\n", RegexOptions.CultureInvariant);
    private static readonly char[] EcmaWhitespace = "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();
    private async IAsyncEnumerable<JsonData> Decode(Stream body, [EnumeratorCancellation] CancellationToken token)
    {
        using var input = new StreamReader(body, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[4096]; var pending = ""; long total = 0; int count; var first = true;
        while ((count = await AwaitSource(() => input.ReadAsync(buffer.AsMemory(), token)).ConfigureAwait(false)) != 0)
        {
            total += count; Limit(total, options.MaximumTotalCharacters); var incoming = new string(buffer, 0, count);
            if (first) { first = false; if (incoming.StartsWith('\uFEFF')) incoming = incoming[1..]; }
            pending += incoming;
            while (true) { var match = Boundary.Match(pending); if (!match.Success) break; var raw = pending[..match.Index]; pending = pending[(match.Index + match.Length)..]; var data = ParseFrame(raw); if (data == "[DONE]") yield break; if (data.Length > 0) yield return ParseChunk(data); }
            Limit(pending.Length, options.MaximumFrameCharacters);
        }
        var tail = ParseFrame(pending); if (tail.Length > 0 && tail != "[DONE]") yield return ParseChunk(tail);
    }
    private string ParseFrame(string raw) { Limit(raw.Length, options.MaximumFrameCharacters); return string.Join("\n", Regex.Split(raw, "\\r\\n|\\r|\\n").Where(x => x.StartsWith("data:", StringComparison.Ordinal)).Select(x => x[5..].TrimStart(EcmaWhitespace))).Trim(EcmaWhitespace); }
    private JsonData ParseChunk(string text) => Admit(() => ParseChunkCore(text));
    private JsonData ParseChunkCore(string text) { using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = options.MaximumJsonDepth }); var value = document.RootElement; if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral streaming event"); return JsonData.FromElement(value); }
    internal static string ReadTextPart(JsonElement part) { if (part.ValueKind != JsonValueKind.Object || part.GetProperty("type").GetString() != "text" || part.GetProperty("text").ValueKind != JsonValueKind.String || part.EnumerateObject().Any(x => x.Name is not ("type" or "text"))) throw Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral text/media/reasoning chunk."); return part.GetProperty("text").GetString()!; }
    internal static string Sanitize(string text) { var builder = new StringBuilder(text.Length); for (var i = 0; i < text.Length; i++) { if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { builder.Append(text[i]); builder.Append(text[++i]); } else if (!char.IsSurrogate(text[i])) builder.Append(text[i]); } return builder.ToString(); }
    // Shape classification is scoped to owned admission, never inferred from foreign
    // callback/HTTP/read exception types. Foreign failures keep fixed SourceFailed text.
    internal static T Admit<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw Fail(NativeChatFailureCode.MalformedStream, "Invalid Mistral admitted data."); }
        catch (StreamingJsonPreviewException error)
        { throw Fail(error.Failure is StreamingJsonPreviewFailure.CharacterLimit or StreamingJsonPreviewFailure.DepthLimit
            ? NativeChatFailureCode.ResourceLimit : NativeChatFailureCode.MalformedStream, "Invalid Mistral tool argument fragment."); }
        catch (OverflowException) { throw Fail(NativeChatFailureCode.ResourceLimit, "Mistral numeric limit."); }
    }
    private static async ValueTask<T> AwaitSource<T>(Func<ValueTask<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw Fail(NativeChatFailureCode.SourceFailed, "Mistral source operation failed."); }
    }
    private static async ValueTask AwaitSource(Func<ValueTask> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw Fail(NativeChatFailureCode.SourceFailed, "Mistral source operation failed."); }
    }
    internal static void Limit(long value, long maximum) { if (value > maximum) throw Fail(NativeChatFailureCode.ResourceLimit, "Mistral data exceeds configured limits."); }
    internal static MistralTextException Fail(NativeChatFailureCode code, string message) => new(code, message);
}
