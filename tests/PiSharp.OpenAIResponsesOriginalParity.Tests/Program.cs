// Source-derived controls for Pi d86654a; author execution remains forbidden.
using System.Net;
using PiSharp.AI;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Transports;
using PiSharp.Contracts;

var groups = new (string Name, Func<Task> Run)[] {
    ("held-http-acquisition-before-start", Controls.Held),
    ("http-acquisition-failure-no-start", Controls.AcquisitionFailure),
    ("failed-terminal-provider-error-and-retained-text", () => Controls.Failure(false)),
    ("error-event-provider-error-and-retained-text", () => Controls.Failure(true)),
    ("incomplete-unknown-reason-retained-text-usage", () => Controls.Incomplete("fixture_future_reason")),
    ("incomplete-content-filter-retained-text-usage", () => Controls.Incomplete("content_filter")),
    ("incomplete-empty-reason-retained-text-usage", () => Controls.Incomplete("")),
    ("length-partial-text-isolated-reducer", LengthControls.Reducer),
    ("length-partial-text-public-run", LengthControls.PartialText),
    ("length-unfinished-tools-thinking-and-completed-distinction", LengthControls.UnfinishedKinds),
    ("length-genuine-provider-source-errors-unchanged", LengthControls.Errors),
    ("length-cleanup-fault-overrides-success-and-retains-original", LengthControls.CleanupFailure),
    ("length-genuine-abort-unchanged", LengthControls.Abort),
    ("factory-model-map-fallback-missing-null-off", FactoryBindingControls.FallbackMap),
    ("factory-model-map-authoritative-explicit-effort", FactoryBindingControls.ModelMap),
    ("factory-no-model-native-map-retained", FactoryBindingControls.NativeMap),
    ("factory-model-cost-all-thinking-branches", FactoryBindingControls.CostBranches),
    ("factory-model-cost-response-tier-precedence", FactoryBindingControls.CostTiers),
    ("factory-missing-null-zero-cost-native-compatibility", FactoryBindingControls.MissingCosts),
    ("factory-malformed-and-threshold-tier-cost-preflight", FactoryBindingControls.InvalidCosts)
};
var originals = new List<OriginalTaskRecord>();
try
{
    foreach (var group in groups) {
        var record = new OriginalTaskRecord(group.Name); originals.Add(record);
        try { var original = group.Run(); record.Original = original; await original; } catch (Exception error) { record.Direct = error; } finally { record.Capture(); }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { sourceDerived = true, groups = groups.Length, records = originals.Select(QualificationReporter.Project).ToArray(),
        held = Controls.Originals.Select(QualificationReporter.Project).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
    return originals.Any(record => record.Direct is not null || record.Original?.IsCompletedSuccessfully != true) ? 1 : 0;
}
catch (Exception failure) { throw new QualificationReportingFailure(failure, originals.ToArray(), Controls.Originals.ToArray()); }

static class Controls
{
    internal static readonly List<OriginalTaskRecord> Originals = [];
    private static readonly ChatRequest Request = new(new("fixture", "openai-responses", "openai"), []);
    private static HttpRequestMessage RequestFactory(ChatRequest _) => new(HttpMethod.Post, "https://fixture.invalid/responses") { Content = new StringContent("{}") };
    private static void Check(bool condition, [CallerLineNumber] int line = 0) { if (!condition) throw new InvalidOperationException($"Responses draft fixture assertion at line {line}."); }
    private static async Task<List<StreamEvent>> Drain(IChatTransport transport) { var result = new List<StreamEvent>(); await foreach (var item in transport.StreamAsync(Request)) result.Add(item); return result; }
    internal static async Task Held()
    {
        using var handler = new HeldHandler(); using var client = new HttpClient(handler);
        var transport = new ResponsesHttpSseTransport(client, RequestFactory);
        var enumerator = transport.StreamAsync(Request).GetAsyncEnumerator();
        var original = enumerator.MoveNextAsync().AsTask(); Exception? assertion = null;
        var pullRecord = new OriginalTaskRecord("held-first-pull-original", original);
        var responseRecord = new OriginalTaskRecord("held-response-original", handler.Release.Task);
        Originals.Add(pullRecord); Originals.Add(responseRecord);
        var content = new ObservedContent();
        try {
            // Before the fix, first MoveNext completes with Start without calling SendAsync.
            await Task.WhenAny(original, handler.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Check(handler.Entered.Task.IsCompletedSuccessfully && !original.IsCompleted);
        } catch (Exception error) { assertion = error; }
        finally { handler.Release.TrySetResult(new(HttpStatusCode.OK) { Content = content }); }
        var faults = new List<Exception>(); if (assertion is not null) faults.Add(assertion);
        try { Check(await original); Check(enumerator.Current is StreamStarted && content.Reads == 0); } catch (Exception error) { pullRecord.Direct = error; } finally { pullRecord.Capture(); }
        try { await handler.Release.Task; } catch (Exception error) { responseRecord.Direct = error; } finally { responseRecord.Capture(); }
        faults.AddRange(pullRecord.Faults()); faults.AddRange(responseRecord.Faults());
        // Dispose only after the actual MoveNext settles; never hide it behind a timed wrapper.
        var disposalRecord = new OriginalTaskRecord("held-dispose-original"); Originals.Add(disposalRecord);
        try { var disposal = enumerator.DisposeAsync().AsTask(); disposalRecord.Original = disposal; await disposal; }
        catch (Exception error) { disposalRecord.Direct = error; } finally { disposalRecord.Capture(); }
        faults.AddRange(disposalRecord.Faults());
        if (handler.Release.Task.IsCompletedSuccessfully) handler.Release.Task.Result.Dispose();
        if (faults.Count != 0) throw new AggregateException("Held originals settled with faults.", faults);
    }
    internal static async Task AcquisitionFailure()
    {
        using var handler = new RejectionHandler(); using var client = new HttpClient(handler);
        var events = await Drain(new ResponsesHttpSseTransport(client, RequestFactory));
        Check(handler.Calls == 1 && events.Count == 1 && events[0] is StreamError { NativeSourceException: HttpSseRejectedException { StatusCode: HttpStatusCode.ServiceUnavailable } });
        var supplied = new InvalidOperationException("fixture source failure");
        using var failingHandler = new FaultHandler(supplied); using var failingClient = new HttpClient(failingHandler);
        events = await Drain(new ResponsesHttpSseTransport(failingClient, RequestFactory));
        Check(events.Count == 1 && events[0] is StreamError failure && ReferenceEquals(failure.NativeSourceException, supplied));
    }
    internal static async Task Failure(bool errorEvent)
    {
        var terminal = errorEvent
            ? "{\"type\":\"error\",\"code\":\"fixture_error\",\"message\":\"synthetic failure\"}"
            : "{\"type\":\"response.failed\",\"response\":{\"id\":\"r\",\"status\":\"failed\",\"error\":{\"code\":\"backend_error\",\"message\":\"synthetic failure\"}}}";
        var events = await Drain(new ResponsesTextToolTransport((_, token) => Source(terminal, token)));
        Check(events[0] is StreamStarted && events[^1] is StreamError { Reason: StopReason.Error });
        var failure = (StreamError)events[^1];
        Check(failure.Message.Content.OfType<TextContent>().Single().Text == "partial");
        Check(Field(failure.Message, "errorMessage") == (errorEvent ? "Error Code fixture_error: synthetic failure" : "backend_error: synthetic failure"));
        if (!errorEvent) Check(Field(failure.Message, "rawStopReason") == "failed");
        if (!errorEvent)
            foreach (var (details, expected) in new[] {
                ("\"error\":{\"code\":\"\",\"message\":\"\"}", "unknown: no message"),
                ("\"incomplete_details\":{\"reason\":\"fixture_reason\"}", "incomplete: fixture_reason"),
                ("\"error\":null", "Unknown error (no error details in response)") })
            {
                var json = "{\"type\":\"response.failed\",\"response\":{\"status\":\"failed\"," + details + "}}";
                var frames = await Drain(new ResponsesTextToolTransport((_, token) => Source(json, token, unfinished: true)));
                Check(frames[^1] is StreamError);
                var message = ((StreamError)frames[^1]).Message;
                Check(message.Content.OfType<TextContent>().Single().Text == "partial" && Field(message, "errorMessage") == expected);
            }
    }
    internal static async Task Incomplete(string reason)
    {
        var terminal = JsonSerializer.Serialize(new { type = "response.incomplete", response = new {
            id = "r", status = "incomplete", incomplete_details = new { reason }, output = Array.Empty<object>(),
            usage = new { input_tokens = 12, output_tokens = 3, total_tokens = 15,
                input_tokens_details = new { cached_tokens = 2 }, output_tokens_details = new { reasoning_tokens = 1 } } } });
        var events = await Drain(new ResponsesTextToolTransport((_, token) => Source(terminal, token, unfinished: true, poison: false)));
        Check(events[0] is StreamStarted && events[^1] is StreamError { Reason: StopReason.Error });
        var failure = (StreamError)events[^1];
        Check(failure.NativeSourceException is null && failure.NativeCleanupExceptions is null);
        Check(!events.OfType<TextEnded>().Any() && failure.Message.Content.OfType<TextContent>().Single().Text == "partial");
        Check(Field(failure.Message, "rawStopReason") == "incomplete" + (reason.Length == 0 ? "" : "." + reason));
        Check(Field(failure.Message, "errorMessage") == (reason.Length == 0 ? "Response incomplete without a provider reason" : "Response incomplete: " + reason));
        Check(failure.Message.Usage.Input == 10 && failure.Message.Usage.Output == 3 && failure.Message.Usage.CacheRead == 2 && failure.Message.Usage.TotalTokens == 15);
    }
    private static string? Field(AssistantMessage message, string name)
    {
        if (message.ExtraProperties is { } fields && fields.TryGet(name, out var value)) return value?.Value.GetString();
        return null;
    }
    private static async IAsyncEnumerable<JsonData> Source(string terminal, [EnumeratorCancellation] CancellationToken token, bool unfinished = false, bool poison = true)
    {
        await Task.CompletedTask;
        foreach (var json in new[] {
            "{\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"m\"}}",
            "{\"type\":\"response.output_text.delta\",\"output_index\":0,\"item_id\":\"m\",\"delta\":\"partial\"}",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"m\",\"content\":[{\"type\":\"output_text\",\"text\":\"partial\"}]}}", terminal })
        {
            token.ThrowIfCancellationRequested();
            if (unfinished && json.Contains("response.output_item.done", StringComparison.Ordinal)) continue;
            yield return JsonData.Parse(json);
        }
        // Original provider failures exit before another DTO can overwrite or obscure them.
        if (poison) yield return JsonData.Parse("{\"type\":\"must-not-consume-after-provider-failure\"}");
    }
    private sealed class ObservedContent : HttpContent
    {
        internal int Reads;
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++;
            await stream.WriteAsync(Encoding.UTF8.GetBytes("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"output\":[]}}\n\n"));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class FaultHandler(Exception supplied) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromException<HttpResponseMessage>(supplied);
    }
    private sealed class HeldHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<HttpResponseMessage> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Entered.TrySetResult(); return Release.Task; }
    }
    private sealed class RejectionHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("fixture rejection") }); }
    }
}
