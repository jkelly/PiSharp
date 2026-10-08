using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AzureResponses;
using PiSharp.Contracts;

internal static class AzureSimpleTests
{
    private static readonly ModelDescriptor Model = new("synthetic-model", "azure-openai-responses", "azure-openai-responses");
    private const string Key = "SYNTHETIC_AZURE_KEY";
    public static readonly List<(string Name, Task Original, Exception? Direct)> HeldOriginals = [];
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Azure Simple assertion failed."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ChatRequest Request(params TranscriptEntry[] entries) => new(Model, entries.Length == 0 ? [User("ask", 1)] : [.. entries]);
    private static TranscriptEntry User(string text, long timestamp) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp })));
    private static AzureResponsesOptions Direct(double context = 8192, bool reasoning = false, object? map = null) => new(
        JsonData.Parse(JsonSerializer.Serialize(new
        {
            id = Model.Id, api = Model.Api, provider = Model.Provider, baseUrl = "https://synthetic.openai.azure.com/openai/v1",
            contextWindow = context, maxTokens = 2048, reasoning, thinkingLevelMap = map
        })), new(reasoning));
    private sealed class Handler(bool held = false) : HttpMessageHandler
    {
        public int Calls; public string? Body; public Uri? Endpoint; public string? ApiKey; public bool Disposed;
        public readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public Task<HttpResponseMessage>? Original;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Original = Send(); return Original;
            async Task<HttpResponseMessage> Send()
            {
                Calls++; Body = await request.Content!.ReadAsStringAsync(); Endpoint = request.RequestUri;
                ApiKey = request.Headers.GetValues("api-key").Single(); Entered.TrySetResult();
                if (held) await Release.Task;
                return new(HttpStatusCode.OK) { Content = new StringContent(
                    "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"synthetic-response\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":0,\"total_tokens\":1}}}\n\n", Encoding.UTF8, "text/event-stream") };
            }
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private static async Task<List<StreamEvent>> Drain(IChatTransport transport, ChatRequest request, CancellationToken token = default)
    { var frames = new List<StreamEvent>(); await foreach (var frame in transport.StreamAsync(request, token)) frames.Add(frame); return frames; }
    public static Task Budget()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false);
        AzureResponsesSimpleResolution Resolve(double context, double? cap = null) => new AzureResponsesSimpleTransport(client, Model,
            new(Direct(context) with { MaxTokens = cap }, Key)).Resolve(Request(User("abcd", 1)));
        // Pi 1.1.0 estimate.ts: "abcd" is ceil(4 / 3.5) = 2 tokens (1 at four characters per token).
        Check(Resolve(4100).MaxTokens == 2 && Resolve(4090).MaxTokens == 1);
        Check(Resolve(0, -3).MaxTokens == 1 && Resolve(8192, -3).MaxTokens == -3);
        Check(Resolve(8192, 0).MaxTokens == 0 && Resolve(8192, 12.5).MaxTokens == 12.5);
        Check(Resolve(4100, 100).ContextEstimate.Tokens == 2 && handler.Calls == 0);
        var unicode = new AzureResponsesSimpleTransport(client, Model, new(Direct(), Key)).Resolve(Request(User("😀abc", 1)));
        Check(unicode.ContextEstimate.Tokens == 2);
        var bounded = new AzureResponsesSimpleTransport(client, Model, new AzureResponsesSimpleOptions(Direct(), Key) { MaximumContextCharacters = 1 });
        try { _ = bounded.Resolve(Request()); throw new InvalidOperationException("Missing context bound."); }
        catch (AzureResponsesException error) { Check(error.Failure == AzureResponsesFailure.ResourceLimit); }
        return Task.CompletedTask;
    }
    public static Task Usage()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false);
        var transport = new AzureResponsesSimpleTransport(client, Model, new(Direct(), Key));
        TranscriptEntry Assistant(long time, string stop, double total, double input = 0) => new("assistant", JsonData.Parse(JsonSerializer.Serialize(new
        { role = "assistant", content = Array.Empty<object>(), timestamp = time, stopReason = stop,
            usage = new { totalTokens = total, input, output = 0, cacheRead = 0, cacheWrite = 0 } })));
        var request = Request(User("prefix", 100), Assistant(99, "stop", 100), Assistant(101, "error", 999),
            Assistant(102, "stop", 0, 20), User("abcde", 103));
        var resolved = transport.Resolve(request);
        Check(resolved.ContextEstimate.LastUsageIndex == 3 && resolved.ContextEstimate.UsageTokens == 20 &&
            resolved.ContextEstimate.TrailingTokens == 2 && resolved.ContextEstimate.Tokens == 22);
        Check(transport.Resolve(Request(User("prefix", 100), Assistant(99, "stop", 100))).ContextEstimate.LastUsageIndex is null);
        return Task.CompletedTask;
    }
    public static Task Thinking()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false);
        var map = new Dictionary<string, string?> { ["off"] = null, ["low"] = null, ["medium"] = null, ["high"] = "high" };
        var transport = new AzureResponsesSimpleTransport(client, Model, new(Direct(reasoning: true, map: map), Key, "low"));
        Check(transport.Resolve(Request()).ClampedReasoning == "high" && transport.Resolve(Request()).ReasoningEffort == "high");
        Check(transport.ClampThinkingLevel("max") == "high" && !transport.GetSupportedThinkingLevels().Contains("max"));
        var off = new AzureResponsesSimpleTransport(client, Model, new(Direct(reasoning: true), Key, "off")).Resolve(Request());
        Check(off.ClampedReasoning == "off" && off.ReasoningEffort is null);
        var nonreasoning = new AzureResponsesSimpleTransport(client, Model, new(Direct(), Key, "high")).Resolve(Request());
        Check(nonreasoning.ClampedReasoning == "off" && nonreasoning.ReasoningEffort is null); return Task.CompletedTask;
    }
    public static async Task Wire()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false);
        var payloads = 0; var responses = 0;
        var direct = Direct(4100, reasoning: true) with
        {
            ToolChoice = JsonData.Parse("\"none\""), SessionId = "synthetic-session", Temperature = 0.25,
            Hooks = new(OnPayload: (body, model, _) => { Check(model == Model); payloads++; return ValueTask.FromResult<JsonData?>(body); },
                OnResponse: (_, model, _) => { Check(model == Model); responses++; return ValueTask.CompletedTask; })
        };
        var transport = new AzureResponsesSimpleTransport(client, Model, new(direct, Key, "off"));
        var frames = await Drain(transport, Request());
        Check(frames.Last() is StreamTerminalEvent { Reason: StopReason.Stop } && handler.Calls == 1 && payloads == 1 && responses == 1);
        using var body = JsonDocument.Parse(handler.Body!);
        Check(body.RootElement.GetProperty("max_output_tokens").GetDouble() == 16 && body.RootElement.GetProperty("tool_choice").GetString() == "none");
        Check(body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() == "none" && handler.ApiKey == Key);
        Check(handler.Endpoint!.AbsoluteUri == "https://synthetic.openai.azure.com/openai/v1/responses?api-version=v1" && !handler.Disposed);
    }
    public static Task MissingKey()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false); var callbacks = 0;
        var direct = Direct() with { Hooks = new(OnPayload: (_, _, _) => { callbacks++; return ValueTask.FromResult<JsonData?>(null); }) };
        foreach (var key in new string?[] { null, "", "bad key" })
        {
            var transport = new AzureResponsesSimpleTransport(client, Model, new(direct, key));
            try { _ = transport.StreamAsync(new(Model, default)); throw new InvalidOperationException("Missing credential admission."); }
            catch (AzureResponsesException error) { Check(error.Failure == AzureResponsesFailure.Credential); }
        }
        Check(callbacks == 0 && handler.Calls == 0); return Task.CompletedTask;
    }
    public static Task PreCancel()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false); using var stop = new CancellationTokenSource(); stop.Cancel();
        var callbacks = 0; var direct = Direct() with { Hooks = new(OnPayload: (_, _, _) => { callbacks++; return ValueTask.FromResult<JsonData?>(null); }) };
        var transport = new AzureResponsesSimpleTransport(client, Model, new(direct, Key));
        try { _ = transport.StreamAsync(new(Model, default), stop.Token); throw new InvalidOperationException("Missing cancellation admission."); }
        catch (OperationCanceledException error) { Check(error.CancellationToken == stop.Token); }
        Check(callbacks == 0 && handler.Calls == 0); return Task.CompletedTask;
    }
    public static async Task HeldPayload()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false); using var stop = new CancellationTokenSource();
        var entered = Gate(); var original = new TaskCompletionSource<JsonData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0; var direct = Direct() with { Hooks = new(OnPayload: (_, model, _) =>
        { Check(model == Model); callbacks++; entered.TrySetResult(); return new(original.Task); }) };
        var transport = new AzureResponsesSimpleTransport(client, Model, new(direct, Key));
        var drain = Drain(transport, Request(), stop.Token); Exception? rejected = null; Exception? control = null;
        try
        {
            if (await Task.WhenAny(entered.Task, drain) == drain && !entered.Task.IsCompleted) { await drain; throw new InvalidOperationException("No callback entry."); }
            await entered.Task; stop.Cancel(); Check(!drain.IsCompleted && !original.Task.IsCompleted && handler.Calls == 0);
        }
        catch (Exception error) { control = error; }
        finally
        {
            original.TrySetResult(null); try { await drain; } catch (Exception error) { rejected = error; }
            await original.Task;
            HeldOriginals.Add(("payload-callback", original.Task, null)); HeldOriginals.Add(("payload-drain", drain, rejected));
        }
        if (control is not null) throw new AggregateException("Held payload control failed.", new[] { control, rejected }.OfType<Exception>());
        Check(rejected is null && drain.Result.Last() is StreamTerminalEvent { Reason: StopReason.Aborted } && callbacks == 1 && handler.Calls == 0);
    }
    public static async Task HeldSend()
    {
        using var handler = new Handler(held: true); using var client = new HttpClient(handler, false); using var stop = new CancellationTokenSource();
        var transport = new AzureResponsesSimpleTransport(client, Model, new(Direct(), Key));
        var drain = Drain(transport, Request(), stop.Token); Exception? rejected = null, sendFailure = null, control = null;
        try
        {
            if (await Task.WhenAny(handler.Entered.Task, drain) == drain && !handler.Entered.Task.IsCompleted) { await drain; throw new InvalidOperationException("No send entry."); }
            await handler.Entered.Task; var original = handler.Original ?? throw new InvalidOperationException("Missing original send.");
            stop.Cancel(); Check(!original.IsCompleted && !drain.IsCompleted);
        }
        catch (Exception error) { control = error; }
        finally
        {
            handler.Release.TrySetResult(); try { await drain; } catch (Exception error) { rejected = error; }
            if (handler.Original is { } original)
            { try { await original; } catch (Exception error) { sendFailure = error; } HeldOriginals.Add(("http-send", original, sendFailure)); }
            HeldOriginals.Add(("send-drain", drain, rejected));
        }
        if (control is not null || sendFailure is not null) throw new AggregateException("Held send control failed.", new[] { control, rejected, sendFailure }.OfType<Exception>());
        Check(rejected is null && drain.Result.Last() is StreamTerminalEvent { Reason: StopReason.Aborted } && handler.Calls == 1 && !handler.Disposed);
    }
}
