using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

internal static class MistralToolStreamingTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mistral tool declarations replay and indexed fragments use the actual HTTP SSE consumer", ToolLoop),
        ("mistral invalid mixed chunk rolls back unpublished text and tool mutations", AtomicChunk),
        ("mistral Simple reasoning maps and cache options reach request and reasoning stream", SimpleOptions),
        ("mistral tool finalization waits original held body disposal and cancellation", HeldCleanup),
        ("mistral tool choice and unsupported replay refuse before HTTP effects", Admission)
    ];
    private static readonly ModelDescriptor Model = new("fixture", "mistral-conversations", "mistral");
    private static MistralTextOptions Options => new(new("https://fixture.invalid/"), true, new(1, 2, 1, 0), "fixture") { ApiKey = "fixture" };
    private static ChatRequest Request => new(Model, [Entry("user", "{\"content\":\"hello\"}")], 1);
    private static TranscriptEntry Entry(string role, string json) => new(role, JsonData.Parse(json));
    private static string Frame(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static string Delta(object delta, string? finish = null) => Frame(new { choices = new[] { new { delta, finish_reason = finish } } });
    private static object Call(int index, string? id, string? name, object arguments) => new { index, id, function = new { name, arguments } };
    private static async Task ToolLoop()
    {
        var request = Request with { Messages = [
            Entry("system", "{\"role\":\"system\",\"content\":\"Use tools\",\"toolsAdded\":[{\"name\":\"lookup\",\"description\":\"Lookup\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}]}"),
            Entry("assistant", "{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"foreign-id-long\",\"name\":\"lookup\",\"arguments\":{\"n\":1}}]}"),
            Entry("toolResult", "{\"role\":\"toolResult\",\"toolCallId\":\"foreign-id-long\",\"toolName\":\"lookup\",\"content\":[{\"type\":\"text\",\"text\":\" denied \"}],\"isError\":true}") ] };
        var body = new Body(Delta(new { content = "first" }) +
            Delta(new { tool_calls = new[] { Call(0, "first-id", "lookup", "{\"n\":"), Call(1, "second-id", "lookup", new { m = 2 }) } }) +
            Delta(new { content = "later", tool_calls = new[] { Call(0, "ignored-later-id", null, "1}") } }, "tool_calls") +
            Frame(new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 5, completion_tokens = 3, total_tokens = 8 } }));
        var hookCalled = false;
        using var handler = new Handler(async message =>
        {
            var wire = JsonData.Parse(await message.Content!.ReadAsStringAsync()).Value;
            Check(wire.GetProperty("tool_choice").GetString() == "any" && !wire.TryGetProperty("toolChoice", out _));
            var function = wire.GetProperty("tools")[0].GetProperty("function");
            Check(function.GetProperty("name").GetString() == "lookup" && !function.GetProperty("strict").GetBoolean());
            var messages = wire.GetProperty("messages"); var id = messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString()!;
            Check(id.Length == 9 && id.All(char.IsAsciiLetterOrDigit) && messages[2].GetProperty("tool_call_id").GetString() == id);
            Check(!messages[1].GetProperty("prefix").GetBoolean() && messages[2].GetProperty("content")[0].GetProperty("text").GetString() == "[tool error] denied");
            return Response(body);
        });
        using var client = new HttpClient(handler);
        var options = Options with { ToolChoice = JsonData.Parse("\"any\""), OnPayload = (payload, _, _) =>
        { hookCalled = payload.Value.GetProperty("messages")[1].TryGetProperty("toolCalls", out _); return ValueTask.FromResult<JsonData?>(null); } };
        var events = await Collect(new(client, Model, options), request);
        Check(events[^1] is StreamDone);
        var terminal = (StreamDone)events[^1];
        Check(hookCalled && body.Released && terminal.Reason == StopReason.ToolUse && terminal.Message.Usage.TotalTokens == 8);
        var cost = terminal.Message.Usage.Cost;
        Check(cost.Input == cost.SourceBinary64Cost!.Value.GetProperty("input").GetDecimal() &&
            PiWireJson.ReadMessage(PiWireJson.WriteMessage(terminal.Message).Value).Usage.TotalTokens == 8);
        var calls = terminal.Message.Content.OfType<ToolCallContent>().ToArray();
        Check(calls.Length == 2 && calls[0].Id == "first-id" && calls[0].Arguments.Value.GetProperty("n").GetInt32() == 1 && calls[1].Arguments.Value.GetProperty("m").GetInt32() == 2);
        Check(events.OfType<TextStarted>().Select(value => value.ContentIndex).SequenceEqual([0, 3]) &&
            events.OfType<ToolCallStarted>().Select(value => value.ContentIndex).SequenceEqual([1, 2]) &&
            events.OfType<ToolCallEnded>().Select(value => value.ContentIndex).SequenceEqual([1, 2]));
    }
    private static async Task AtomicChunk()
    {
        var valid = Delta(new { tool_calls = new[] { Call(0, "stable-id", "lookup", "{\"x\":1}") } });
        var malformed = "data: {\"choices\":[{\"delta\":{\"content\":\"unpublished\",\"tool_calls\":[{\"index\":\"bad\",\"function\":{\"name\":\"bad\",\"arguments\":\"{}\"}}]}}]}\n\n";
        var body = new Body(valid + malformed); using var handler = new Handler(_ => Task.FromResult(Response(body))); using var client = new HttpClient(handler);
        var events = await Collect(new(client, Model, Options), Request);
        Check(events[^1] is StreamError && body.Released && !events.OfType<TextStarted>().Any());
        var terminal = (StreamTerminalEvent)events[^1];
        Check(terminal.Message.Content.Length == 1 && terminal.Message.Content[0] is ToolCallContent { Id: "stable-id" });
    }
    private static async Task SimpleOptions()
    {
        foreach (var mapped in new[] { false, true })
        {
            var body = new Body(Delta(new { content = new object[] { new { type = "thinking", thinking = new[] { new { type = "text", text = "reason" } } }, "", "answer" } }, "stop"));
            using var handler = new Handler(async message =>
            {
                var wire = JsonData.Parse(await message.Content!.ReadAsStringAsync()).Value;
                Check(wire.GetProperty(mapped ? "reasoning_effort" : "prompt_mode").GetString() == (mapped ? "medium" : "reasoning"));
                Check(wire.GetProperty("prompt_cache_key").GetString() == "explicit-session"); return Response(body);
            }); using var client = new HttpClient(handler);
            var options = Options with { Reasoning = true, SessionId = "explicit-session", ThinkingLevelMap = mapped
                ? ImmutableDictionary<string, string?>.Empty.Add("high", "medium") : null };
            var events = await Collect(new(client, Model, options), Request with { ThinkingLevel = "high" });
            Check(events[^1] is StreamDone && events.OfType<ThinkingStarted>().Count() == 1 && events.OfType<ThinkingEnded>().Single().Content == "reason" &&
                events.OfType<TextStarted>().Count() == 1 && body.Released);
        }
        var clampedBody = new Body(Delta(new { content = "clamped" }, "stop"));
        using var clampedHandler = new Handler(async message =>
        {
            var wire = JsonData.Parse(await message.Content!.ReadAsStringAsync()).Value;
            Check(wire.GetProperty("reasoning_effort").GetString() == "high" && !wire.TryGetProperty("prompt_cache_key", out _));
            return Response(clampedBody);
        }); using var clampedClient = new HttpClient(clampedHandler);
        var clamped = new MistralTextHttpSseTransport(clampedClient, Model, Options with { Reasoning = true, CachePrompt = false,
            SessionId = "not-sent", ThinkingLevelMap = ImmutableDictionary<string, string?>.Empty.Add("high", null) });
        Check(clamped.GetSupportedThinkingLevels(Model).SequenceEqual(["off", "minimal", "low", "medium"]));
        Check((await Collect(clamped, Request with { ThinkingLevel = "max" }))[^1] is StreamDone && clampedBody.Released);
    }
    private static async Task HeldCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new Body(Delta(new { tool_calls = new[] { Call(0, null, "lookup", "{}") } }, "tool_calls"), entered, release);
        using var handler = new Handler(_ => Task.FromResult(Response(body))); using var client = new HttpClient(handler); using var cancel = new CancellationTokenSource();
        var observed = new List<StreamEvent>();
        var original = Collect(new(client, Model, Options), Request, cancel.Token, observed);
        Exception? assertion = null;
        try
        {
            Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted);
            Check(!observed.OfType<ToolCallEnded>().Any() && !observed.OfType<StreamTerminalEvent>().Any());
            cancel.Cancel(); Check(!original.IsCompleted);
        }
        catch (Exception error) { assertion = error; }
        finally
        {
            release.TrySetResult();
            try { await original; } catch (Exception error) { assertion = assertion is null ? error : new AggregateException(assertion, original.Exception ?? error); }
        }
        if (assertion is not null) throw assertion;
        Check(body.Released && observed[^1] is StreamError { Reason: StopReason.Aborted } && observed.OfType<ToolCallEnded>().Count() == 1);
    }
    private static async Task Admission()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected send")); using var client = new HttpClient(handler);
        foreach (var options in new[] { Options with { ToolChoice = JsonData.Parse("\"invalid\"") }, Options })
        {
            var request = options.ToolChoice is null ? Request with { Messages = [Entry("assistant", "{\"content\":\"unsupported scalar replay\"}")] } : Request;
            var events = await Collect(new(client, Model, options), request);
            Check(events[^1] is StreamError && !events.OfType<StreamStarted>().Any() && handler.Calls == 0);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request); }
    }
    private sealed class Body(string wire, TaskCompletionSource? entered = null, TaskCompletionSource? release = null) : MemoryStream(Encoding.UTF8.GetBytes(wire))
    {
        internal bool Released;
        public override async ValueTask DisposeAsync() { entered?.TrySetResult(); if (release is not null) await release.Task; Released = true; base.Dispose(); }
    }
    private static HttpResponseMessage Response(Body body) => new(HttpStatusCode.OK) { Content = new StreamContent(body) };
    private static async Task<List<StreamEvent>> Collect(MistralTextHttpSseTransport transport, ChatRequest request, CancellationToken token = default, List<StreamEvent>? observed = null)
    { var result = observed ?? []; await foreach (var item in transport.StreamAsync(request, token)) result.Add(item); return result; }
    private static void Check(bool value, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    { if (!value) throw new MistralFixtureAssertionException(nameof(MistralToolStreamingTests), line); }
}
