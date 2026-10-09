// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/model-runtime.ts (registerProvider with streamSimple:
// the extension's stream implementation serves its API, with request-time authentication) and packages/ai/src/api-registry.ts
// (registerApiProvider: an API's stream function is looked up by model.api).
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.AI;
using PiSharp.Cli.Models;
using PiSharp.Contracts;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>A chat route whose stream is an extension's <c>streamSimple</c> in the Node host: the session's request (its transcript, system
/// messages included) and the model's resolved API key and headers go to Node; the pi-ai events come back as the native stream.</summary>
internal sealed class PiProviderTransport(PiExtensionHost host, RegistryModel entry, ModelRegistry registry) : IChatTransport, IThinkingLevelTransport
{
    public System.Collections.Immutable.ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) =>
        entry.Reasoning ? ["off", "minimal", "low", "medium", "high"] : ["off"];

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var auth = registry.ResolveRequestAuth(entry, out _);
        var options = new JsonObject { ["maxTokens"] = entry.MaxTokens > 0 ? entry.MaxTokens : null };
        if (auth?.ApiKey is { } apiKey) options["apiKey"] = apiKey;
        if (auth?.Headers is { Count: > 0 } headers) options["headers"] = new JsonObject([.. headers.Select(header => KeyValuePair.Create(header.Key, (JsonNode?)header.Value))]);
        if (request.ThinkingLevel is { } level && level != "off") options["reasoning"] = level;
        var messages = new JsonArray([.. request.Messages.Select(message => JsonNode.Parse(message.WireBody.ToString()))]);
        var events = Channel.CreateUnbounded<JsonElement>(new() { SingleReader = true });
        var call = host.CallAsync("provider.stream", new JsonObject
        {
            ["provider"] = entry.Provider, ["api"] = entry.Api, ["model"] = entry.CloneJson(), ["context"] = new JsonObject { ["messages"] = messages }, ["options"] = options
        }, cancellationToken, value => events.Writer.TryWrite(value.Clone()));
        _ = call.ContinueWith(_ => events.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var terminal = false;
        await foreach (var value in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var mapped = Map(value, request);
            if (mapped is null) continue;
            terminal = mapped is StreamTerminalEvent;
            yield return mapped;
            if (terminal) break;
        }
        if (terminal) { await Settle(call).ConfigureAwait(false); yield break; }
        string? failure = null; JsonElement? final = null;
        try { final = await call.ConfigureAwait(false); }
        catch (Exception error) when (error is PiSharp.Compatibility.Node.Pi.PiNodeHostException or InvalidOperationException or IOException) { failure = error.Message; }
        if (failure is null && final is { ValueKind: JsonValueKind.Object } message)
        {
            var assistant = PiWireJson.ReadMessage(message);
            yield return assistant.StopReason is StopReason.Error or StopReason.Aborted ? new StreamError(assistant.StopReason, assistant) : new StreamDone(assistant.StopReason, assistant);
            yield break;
        }
        var properties = JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(failure ?? "The extension provider ended without a message")));
        yield return new StreamError(cancellationToken.IsCancellationRequested ? StopReason.Aborted : StopReason.Error,
            new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, [], TokenUsage.Zero,
                cancellationToken.IsCancellationRequested ? StopReason.Aborted : StopReason.Error, properties));
    }

    /// <summary>A pi-ai AssistantMessageEvent as the native stream event. pi-ai streams share one mutable partial message across
    /// events, so the event's own fields are read; the partial supplies only a tool call's id and name.</summary>
    private static StreamEvent? Map(JsonElement value, ChatRequest request)
    {
        try
        {
            var type = value.GetProperty("type").GetString();
            int Index() => value.GetProperty("contentIndex").GetInt32();
            string Text(string name) => value.GetProperty(name).GetString() ?? "";
            switch (type)
            {
                case "start":
                    return new StreamStarted(new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending));
                case "text_start": return new TextStarted(Index(), new TextContent(""));
                case "text_delta": return new TextDelta(Index(), Text("delta"));
                case "text_end": return new TextEnded(Index(), Text("content"));
                case "thinking_start": return new ThinkingStarted(Index(), new ThinkingContent(""));
                case "thinking_delta": return new ThinkingDelta(Index(), Text("delta"));
                case "thinking_end": return new ThinkingEnded(Index(), Text("content"));
                case "toolcall_start":
                {
                    var call = value.TryGetProperty("partial", out var partial) && partial.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > Index() ? content[Index()] : default;
                    var id = call.ValueKind == JsonValueKind.Object && call.TryGetProperty("id", out var callId) ? callId.GetString() ?? "" : "";
                    var name = call.ValueKind == JsonValueKind.Object && call.TryGetProperty("name", out var callName) ? callName.GetString() ?? "" : "";
                    return new ToolCallStarted(Index(), new ToolCallContent(id, name, JsonData.EmptyObject));
                }
                case "toolcall_delta": return new ToolCallDelta(Index(), Text("delta"));
                case "toolcall_end": return new ToolCallEnded(Index(), (ToolCallContent)PiWireJson.ReadContent(value.GetProperty("toolCall")));
                case "done":
                {
                    var message = PiWireJson.ReadMessage(value.GetProperty("message"));
                    return new StreamDone(message.StopReason, message);
                }
                case "error":
                {
                    var message = PiWireJson.ReadMessage(value.GetProperty("error"));
                    return new StreamError(message.StopReason is StopReason.Aborted ? StopReason.Aborted : StopReason.Error, message);
                }
                default: return null;
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or InvalidCastException) { return null; }
    }

    private static async Task Settle(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (error is PiSharp.Compatibility.Node.Pi.PiNodeHostException or InvalidOperationException or IOException or OperationCanceledException) { }
    }
}
