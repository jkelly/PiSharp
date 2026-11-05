using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.AI;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Telemetry;

// PiSharp.AI request tracing: packages/ai/test/telemetry.test.ts and telemetry-options.test.ts at Pi
// f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT). Upstream drives models.stream/complete through a faux provider; here a faux
// IChatTransport drives a ChatRun, the PiSharp request path, and AiTelemetry.ObserveModelRequest is driven directly where upstream calls
// observeModelRequest or a deferred poll (PiSharp.AI has no deferred-poll path yet). The credential redaction, the faulty-adapter and the
// typed-option cases are PiSharp's own. Every fake credential is assembled at run time; nothing leaves the process.
internal static partial class Program
{
    private static readonly ModelDescriptor AiModel = new("model", "telemetry-test", "test");
    private const string Secrets = "text-secret|thinking-secret|argument-secret|call-secret|setup-secret|deferred-secret";

    private static JsonData JsonText(string value) => JsonData.Parse(JsonSerializer.Serialize(value));

    private static AssistantMessage AiMessage(StopReason stop = StopReason.Stop, string? errorMessage = null)
    {
        var extra = JsonFields.Empty.Set("responseId", JsonText("response"));
        if (errorMessage is not null) extra = extra.Set("errorMessage", JsonText(errorMessage));
        return new(AiModel.Api, AiModel.Provider, AiModel.Id, 0,
            [new TextContent("text-secret"), new ThinkingContent("thinking-secret"),
             new ToolCallContent("call-secret", "lookup", JsonData.Parse("{\"value\":\"argument-secret\"}"))],
            new TokenUsage(11, 22, 33, 44, 110, new UsageCost(0.1m, 0.2m, 0.3m, 0.4m, 1m)), stop, extra);
    }

    private static AssistantMessage Partial(AssistantMessage result) =>
        result with { Content = [], StopReason = StopReason.Pending, ExtraProperties = null };

    /// <summary>telemetry.test.ts <c>events</c>, in the block shape the PiSharp stream reducer admits.</summary>
    private static StreamEvent[] AiEvents(AssistantMessage result)
    {
        var toolCall = (ToolCallContent)result.Content[2];
        return
        [
            new StreamStarted(Partial(result)),
            new TextStarted(0, new TextContent("")),
            new TextDelta(0, "text-secret"),
            new TextEnded(0, "text-secret"),
            new ThinkingStarted(1, new ThinkingContent("")),
            new ThinkingEnded(1, "thinking-secret"),
            new ToolCallStarted(2, toolCall with { Arguments = JsonData.EmptyObject }),
            new ToolCallDelta(2, "{\"value\":"),
            new ToolCallDelta(2, "\"argument-secret\"}"),
            new ToolCallEnded(2, toolCall),
            result.StopReason is StopReason.Error or StopReason.Aborted ? new StreamError(result.StopReason, result)
                : new StreamDone(result.StopReason == StopReason.Pending ? StopReason.Deferred : result.StopReason, result)
        ];
    }

    private sealed class FauxTransport(Func<ChatRequest, IReadOnlyList<StreamEvent>> respond) : IChatTransport
    {
        public ChatRequest? Seen { get; private set; }

        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Seen = request;
            await Task.Yield();
            foreach (var value in respond(request)) yield return value;
        }
    }

    private static ChatRequest AiRequest(ITelemetrySpan? telemetry, string? reasoning = null) =>
        new(AiModel, []) { Telemetry = telemetry, ThinkingLevel = reasoning };

    /// <summary>A request under a <c>caller</c> span; returns the recorded caller and request spans.</summary>
    private static async Task<(RecordedTelemetrySpan Caller, RecordedTelemetrySpan Request, ChatResult Result)> Traced(
        InMemoryTelemetryContext telemetry, AssistantMessage result, string? reasoning = null, Func<ITelemetrySpan, ITelemetrySpan>? wrap = null)
    {
        var client = new ChatClient(new FauxTransport(_ => AiEvents(result)));
        ChatResult? completion = null;
        await telemetry.StartSpanAsync("caller", async parent =>
        {
            completion = await client.CompleteAsync(AiRequest(wrap is null ? parent : wrap(parent), reasoning));
        });
        await Settled(telemetry);
        var spans = telemetry.GetSpans();
        return (spans[0], spans[1], completion!);
    }

    private static object ExpectedEndAttributes(bool reasoning, string stop = "stop") => reasoning
        ? new Dictionary<string, object>
        {
            ["provider"] = "test", ["api"] = "telemetry-test", ["model"] = "model", ["reasoning"] = "high", ["stop"] = stop,
            ["usage.input"] = 11, ["usage.output"] = 22, ["usage.cache_read"] = 33, ["usage.cache_write"] = 44, ["cost.total"] = 1,
            ["response.id"] = "response"
        }
        : new Dictionary<string, object>
        {
            ["provider"] = "test", ["api"] = "telemetry-test", ["model"] = "model", ["stop"] = stop,
            ["usage.input"] = 11, ["usage.output"] = 22, ["usage.cache_read"] = 33, ["usage.cache_write"] = 44, ["cost.total"] = 1,
            ["response.id"] = "response"
        };

    private static readonly object[] ExpectedEvents =
    [
        new { name = "pi.ai.first_chunk", attributes = new { } },
        new { name = "pi.ai.block", attributes = new { type = "text", index = 0, chars = 11 } },
        new { name = "pi.ai.block", attributes = new { type = "thinking", index = 1, chars = 15 } },
        new { name = "pi.ai.block", attributes = new { type = "tool_call", index = 2, chars = 27, tool = "lookup" } }
    ];

    private static void CheckMeasurements(InMemoryTelemetryContext telemetry, RecordedTelemetrySpan span)
    {
        var measurements = telemetry.GetMetrics();
        Equal(7, measurements.Count, "measurements");
        Json(new[] { 11, 22, 33, 44, 1 }, measurements.Where(item => item.Kind == TelemetryMetricKind.Counter).Select(item => item.Value), "counters");
        foreach (var measurement in measurements)
        {
            Json(measurement.Name switch
            {
                "pi.ai.tokens" => new[] { "kind", "model", "provider" },
                "pi.ai.request.duration" => new[] { "model", "provider", "stop" },
                _ => new[] { "model", "provider" }
            }, measurement.Attributes.Keys.Order(StringComparer.Ordinal), measurement.Name + " attribute names");
            if (measurement.Kind == TelemetryMetricKind.Histogram) Json(span.Context, measurement.Context, measurement.Name + " exemplar");
        }
    }

    /// <summary>A span without the metrics capability (telemetry.test.ts <c>{ ...span, metrics: undefined }</c>).</summary>
    private sealed class TracingOnlySpan(ITelemetrySpan inner) : ITelemetrySpan
    {
        public ITelemetryMetrics? Metrics => null;
        public ITelemetryPropagation? Propagation => inner.Propagation;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => inner.StartSpanAsync(options, callback);
        public void AddLink(SpanLink link) => inner.AddLink(link);
        public void AddEvent(string name, SpanAttributes? attributes = null) => inner.AddEvent(name, attributes);
        public void SetAttributes(SpanAttributes attributes) => inner.SetAttributes(attributes);
        public void SetStatus(SpanStatus status) => inner.SetStatus(status);
    }

    /// <summary>A span whose metrics capability is <paramref name="metrics"/> (vi.spyOn(telemetry, "histogram" | "counter")).</summary>
    private sealed class MetricsSpan(ITelemetrySpan inner, ITelemetryMetrics metrics) : ITelemetrySpan
    {
        public ITelemetryMetrics? Metrics => metrics;
        public ITelemetryPropagation? Propagation => inner.Propagation;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => inner.StartSpanAsync(options, callback);
        public void AddLink(SpanLink link) => inner.AddLink(link);
        public void AddEvent(string name, SpanAttributes? attributes = null) => inner.AddEvent(name, attributes);
        public void SetAttributes(SpanAttributes attributes) => inner.SetAttributes(attributes);
        public void SetStatus(SpanStatus status) => inner.SetStatus(status);
    }

    private sealed class CountingMetrics(ITelemetryMetrics inner) : ITelemetryMetrics
    {
        public List<string> Counters { get; } = [];
        public List<string> Histograms { get; } = [];
        public List<string> UpDownCounters { get; } = [];
        public ITelemetryCounter Counter(string name, InstrumentOptions? options = null) { Counters.Add(name); return inner.Counter(name, options); }
        public ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null) { UpDownCounters.Add(name); return inner.UpDownCounter(name, options); }
        public ITelemetryHistogram Histogram(string name, HistogramOptions? options = null) { Histograms.Add(name); return inner.Histogram(name, options); }
    }

    /// <summary>An adapter that fails: when a span starts, or when the request span records an event.</summary>
    private sealed class FaultySpan(ITelemetrySpan inner, bool failStart, bool failEvents) : ITelemetrySpan
    {
        public ITelemetryMetrics? Metrics => inner.Metrics;
        public ITelemetryPropagation? Propagation => inner.Propagation;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => failStart
            ? throw new InvalidOperationException("adapter start failure")
            : inner.StartSpanAsync(options, child => callback(failEvents ? new FaultySpan(child, false, true) : child));
        public void AddLink(SpanLink link) => inner.AddLink(link);
        public void AddEvent(string name, SpanAttributes? attributes = null)
        {
            if (failEvents) throw new InvalidOperationException("adapter event failure");
            inner.AddEvent(name, attributes);
        }
        public void SetAttributes(SpanAttributes attributes) => inner.SetAttributes(attributes);
        public void SetStatus(SpanStatus status) => inner.SetStatus(status);
    }

    private sealed class CapturingImages : IImagesApi
    {
        public ImagesOptions? Seen { get; private set; }
        public string Api => "telemetry-test-images";
        public Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Seen = options;
            return Task.FromResult(new AssistantImages(model.Api, model.Provider, model.Id, [], ModelOperationStopReason.Stop, 0));
        }
    }

    /// <summary>A poll observed directly, as fetchDeferred does: every event, then the result.</summary>
    private static async Task<RecordedTelemetrySpan> Polled(InMemoryTelemetryContext telemetry, AssistantMessage result)
    {
        await telemetry.StartSpanAsync("caller", span =>
        {
            var observer = AiTelemetry.ObserveModelRequest(AiModel, span, poll: true);
            foreach (var value in AiEvents(result)) observer.OnEvent(value);
            observer.OnResult(result);
        });
        await Settled(telemetry);
        return telemetry.GetSpans()[1];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ObserveLargeError(ITelemetrySpan span)
    {
        var result = AiMessage(StopReason.Error, new string('x', 4_000_000) + "tail");
        var observer = AiTelemetry.ObserveModelRequest(AiModel, span, poll: true);
        observer.OnEvent(new StreamStarted(Partial(result)));
        observer.OnResult(result);
        return new WeakReference(result);
    }

    private static IEnumerable<(string Id, Func<Task> Run)> AiCases =>
    [
        ("telemetry.ai.request-observes-a-chat-run-without-changing-events-or-results", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var result = AiMessage();
            var events = AiEvents(result);
            var client = new ChatClient(new FauxTransport(_ => events));
            var received = new List<StreamEvent>();
            ChatResult? completion = null;
            await telemetry.StartSpanAsync("caller", async parent =>
            {
                await using var run = await client.StartAsync(AiRequest(parent, "high"));
                await foreach (var value in run.ReadEventsAsync()) received.Add(value);
                completion = await run.Completion;
            });
            await Settled(telemetry);
            Equal(events.Length, received.Count, "delivered events");
            for (var index = 0; index < events.Length - 1; index++) Same(events[index], received[index], $"event {index}");
            Same(result, completion!.Message, "result message");
            Check(completion.Failure is null, "no failure");
            var spans = telemetry.GetSpans();
            var (parent, span) = (spans[0], spans[1]);
            Equal(2, spans.Count, "spans");
            Equal(AiTelemetry.RequestSpanName, span.Name, "span name");
            Equal<int?>(parent.Id, span.ParentId, "parent");
            Json(ExpectedEndAttributes(reasoning: true), span.Attributes, "attributes");
            Json(ExpectedEvents, span.Events, "events");
            Json(new { status = "ok" }, span.Status, "status");
            Check(!Regex.IsMatch(Text(span), Secrets), "no content in the span: " + Text(span));
            CheckMeasurements(telemetry, span);
            Check(!Regex.IsMatch(Text(telemetry.GetMetrics()), Secrets), "no content in measurements");
        }),
        ("telemetry.ai.poll-span-and-metrics", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var span = await Polled(telemetry, AiMessage());
            Equal(AiTelemetry.PollSpanName, span.Name, "span name");
            Json(ExpectedEndAttributes(reasoning: false), span.Attributes, "attributes");
            Json(ExpectedEvents, span.Events, "events");
            Check(!Regex.IsMatch(Text(span), Secrets), "no content in the span");
            CheckMeasurements(telemetry, span);
        }),
        ("telemetry.ai.error-status-without-error-attributes-or-events", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var result = AiMessage(StopReason.Error, "error-secret");
            var (_, span, completion) = await Traced(telemetry, result);
            Same(result, completion.Message, "result");
            Json(new { status = "error", error = new { name = "provider_error", message = "error-secret" } }, span.Status, "status");
            Equal("error", span.Attributes["stop"]?.GetString(), "stop");
            Check(!Text(span.Attributes).Contains("error-secret", StringComparison.Ordinal), "no message in attributes");
            Check(!Text(span.Events).Contains("error-secret", StringComparison.Ordinal), "no message in events");
        }),
        ("telemetry.ai.aborted-status-stays-ok", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var result = AiMessage(StopReason.Aborted, "error-secret");
            var (_, span, completion) = await Traced(telemetry, result);
            Same(result, completion.Message, "result");
            Json(new { status = "ok" }, span.Status, "status");
            Equal("aborted", span.Attributes["stop"]?.GetString(), "stop");
            Check(!Text(span).Contains("error-secret", StringComparison.Ordinal), "no message anywhere in the span");
        }),
        ("telemetry.ai.bounds-error-messages-to-500-characters", async () =>
        {
            foreach (var missing in new[] { false, true })
            {
                var result = AiMessage(StopReason.Error, missing ? null : new string('x', 500) + "discarded-tail");
                var (_, requestSpan, _) = await Traced(new InMemoryTelemetryContext(), result);
                var pollSpan = await Polled(new InMemoryTelemetryContext(), result);
                foreach (var span in new[] { requestSpan, pollSpan })
                {
                    Json(new { status = "error", error = new { name = "provider_error", message = missing ? "" : new string('x', 500) } },
                        span.Status, $"status (missing={missing}, {span.Name})");
                    Check(!Text(span).Contains("discarded-tail", StringComparison.Ordinal), "tail discarded");
                    Check(!Text(span.Attributes).Contains("xxx", StringComparison.Ordinal), "no message in attributes");
                    Check(!Text(span.Events).Contains("xxx", StringComparison.Ordinal), "no message in events");
                }
            }
        }),
        ("telemetry.ai.bounded-error-does-not-keep-a-large-message-alive", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            WeakReference? message = null;
            await telemetry.StartSpanAsync("caller", span => { message = ObserveLargeError(span); });
            await Settled(telemetry);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Json(new { status = "error", error = new { name = "provider_error", message = new string('x', 500) } }, telemetry.GetSpans()[1].Status, "status");
            Check(!message!.IsAlive, "the 4 MB message is collectable while the span is retained");
        }),
        ("telemetry.ai.redacts-credentials-in-error-messages", async () =>
        {
            // Fake credential shapes, assembled here so no key-shaped literal is committed.
            var password = "hunter" + "-" + "2fake";
            var queryKey = "AIza" + new string('Q', 35);
            var bearer = "faketok" + "en" + "123456";
            var basic = "dXNlcjpw" + "YXNzZmFrZQ==";
            var named = "zz" + "fakevalue" + "99";
            var openAi = "sk-" + "proj-" + new string('F', 24);
            var github = "ghp" + "_" + new string('G', 36);
            var message = $"401 from https://user:{password}@example.test/v1/models?alt=sse&key={queryKey} (sent Bearer {bearer}, then Authorization: Basic {basic}); " +
                $"api_key={named}, retry with {openAi} or {github}; max_tokens=100";
            var telemetry = new InMemoryTelemetryContext();
            var (_, span, completion) = await Traced(telemetry, AiMessage(StopReason.Error, message));
            Equal(StopReason.Error, completion.Message.StopReason, "error result");
            var recorded = span.Status.Error?.Message ?? throw new InvalidOperationException("Expected an error status.");
            foreach (var secret in new[] { password, queryKey, bearer, basic, named, openAi, github })
                Check(!recorded.Contains(secret, StringComparison.Ordinal) && !Text(telemetry.GetSpans()).Contains(secret, StringComparison.Ordinal),
                    "credential recorded: " + recorded);
            foreach (var kept in new[] { "401 from https://", "@example.test/v1/models?alt=sse&key=[redacted]", "Bearer [redacted]", "api_key=[redacted]",
                         "max_tokens=100" })
                Check(recorded.Contains(kept, StringComparison.Ordinal), $"'{kept}' missing from: {recorded}");
            Check(recorded.Length <= 500, "bounded");
        }),
        ("telemetry.ai.records-setup-failures-without-their-details", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var client = new ChatClient(new FauxTransport(_ => throw new InvalidOperationException("setup-secret")));
            ChatResult? completion = null;
            await telemetry.StartSpanAsync("caller", async span => { completion = await client.CompleteAsync(AiRequest(span)); });
            Equal(StopReason.Error, completion!.Message.StopReason, "error result");
            await Settled(telemetry);
            var recorded = telemetry.GetSpans()[1];
            Check(recorded.Attributes["reasoning"] is null && !recorded.Attributes.ContainsKey("reasoning"), "no reasoning attribute");
            Json(new[] { new { name = "pi.ai.first_chunk", attributes = new { } } }, recorded.Events, "events");
            Json(new { status = "error", error = new { name = "provider_error", message = "Provider transport failed." } }, recorded.Status, "status");
            Check(!Text(recorded.Attributes).Contains("Provider transport failed", StringComparison.Ordinal), "no message in attributes");
            Check(!Text(recorded).Contains("setup-secret", StringComparison.Ordinal), "the transport's exception is not recorded");
        }),
        ("telemetry.ai.counts-interleaved-tool-deltas-without-reading-arguments", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var result = AiMessage();
            await telemetry.StartSpanAsync("caller", span =>
            {
                var observer = AiTelemetry.ObserveModelRequest(AiModel, span);
                var toolCall = new ToolCallContent("call", "lookup", JsonData.Parse("{\"value\":\"argument-secret\"}"));
                foreach (var (index, delta) in new[] { (2, "ab"), (3, "123"), (2, "c") }) observer.OnEvent(new ToolCallDelta(index, delta));
                observer.OnEvent(new ToolCallEnded(3, toolCall));
                observer.OnEvent(new ToolCallEnded(2, toolCall));
                observer.OnEvent(new ToolCallEnded(4, toolCall));
                observer.OnResult(result);
            });
            await Settled(telemetry);
            var span = telemetry.GetSpans()[1];
            Json(new[]
            {
                new { name = "pi.ai.block", attributes = new { type = "tool_call", index = 3, chars = 3, tool = "lookup" } },
                new { name = "pi.ai.block", attributes = new { type = "tool_call", index = 2, chars = 3, tool = "lookup" } },
                new { name = "pi.ai.block", attributes = new { type = "tool_call", index = 4, chars = 0, tool = "lookup" } }
            }, span.Events.Skip(1), "tool blocks");
            Check(!Text(span).Contains("argument-secret", StringComparison.Ordinal), "arguments are not recorded");
        }),
        ("telemetry.ai.tracing-only-span-without-metrics", async () =>
        {
            var adapter = new InMemoryTelemetryContext();
            var (_, _, completion) = await Traced(adapter, AiMessage(), wrap: span => new TracingOnlySpan(span));
            Equal(StopReason.Stop, completion.Message.StopReason, "result");
            Equal(2, adapter.GetSpans().Count, "spans");
            Equal(0, adapter.GetMetrics().Count, "no measurements");
        }),
        ("telemetry.ai.defaults-to-no-telemetry", async () =>
        {
            var transport = new FauxTransport(_ => AiEvents(AiMessage()));
            var completion = await new ChatClient(transport).CompleteAsync(AiRequest(null));
            Equal(StopReason.Stop, completion.Message.StopReason, "result");
            Check(transport.Seen?.Telemetry is null, "no span");
            Equal(true, AiTelemetry.Schema.Spans[AiTelemetry.RequestSpanName].StartAttributes["provider"].Required, "schema provider required");
            // The observer itself defaults to the no-op context.
            var observer = AiTelemetry.ObserveModelRequest(AiModel, null, "high");
            observer.OnEvent(new StreamStarted(Partial(AiMessage())));
            observer.OnResult(AiMessage());
        }),
        ("telemetry.ai.low-cardinality-metrics-with-monotonic-durations", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var result = AiMessage();
            var time = new ManualTime(clockMs: 100);
            await telemetry.StartSpanAsync("caller", span =>
            {
                var observer = AiTelemetry.ObserveModelRequest(AiModel, span, timeProvider: time);
                time.ClockMs = 125;
                observer.OnEvent(new StreamStarted(Partial(result)));
                time.ClockMs = 175;
                observer.OnResult(result);
            });
            await Settled(telemetry);
            var metrics = telemetry.GetMetrics();
            Json(new object[]
            {
                new { name = "pi.ai.first_chunk.duration", value = 25, attributes = new { provider = "test", model = "model" }, unit = "ms" },
                new { name = "pi.ai.request.duration", value = 75, attributes = new { provider = "test", model = "model", stop = "stop" }, unit = "ms" },
                new { name = "pi.ai.tokens", value = 11, attributes = new { provider = "test", model = "model", kind = "input" }, unit = "token" },
                new { name = "pi.ai.tokens", value = 22, attributes = new { provider = "test", model = "model", kind = "output" }, unit = "token" },
                new { name = "pi.ai.tokens", value = 33, attributes = new { provider = "test", model = "model", kind = "cache_read" }, unit = "token" },
                new { name = "pi.ai.tokens", value = 44, attributes = new { provider = "test", model = "model", kind = "cache_write" }, unit = "token" },
                new { name = "pi.ai.cost", value = 1, attributes = new { provider = "test", model = "model" }, unit = "USD" }
            }, metrics.Select(item => new { name = item.Name, value = item.Value, attributes = item.Attributes, unit = item.Unit }), "measurements");
            Check(metrics.Take(2).All(item => item.Context is not null), "duration exemplars");
        }),
        ("telemetry.ai.records-poll-usage-only-when-final", async () =>
        {
            foreach (var reason in new[] { StopReason.Pending, StopReason.Deferred, StopReason.Stop, StopReason.Error })
            {
                var telemetry = new InMemoryTelemetryContext();
                var span = await Polled(telemetry, AiMessage(reason));
                Equal(AiTelemetry.PollSpanName, span.Name, "poll span");
                var counters = telemetry.GetMetrics().Count(item => item.Kind == TelemetryMetricKind.Counter);
                Equal(reason is StopReason.Pending or StopReason.Deferred ? 0 : 5, counters, $"{reason} poll counters");
            }
            // A request (not a poll) that ends deferred reports its usage.
            var request = new InMemoryTelemetryContext();
            await request.StartSpanAsync("caller", span => AiTelemetry.ObserveModelRequest(AiModel, span).OnResult(AiMessage(StopReason.Deferred)));
            await Settled(request);
            Equal(5, request.GetMetrics().Count(item => item.Kind == TelemetryMetricKind.Counter), "deferred request counters");
        }),
        ("telemetry.ai.creates-instruments-once-per-metrics-object", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var counting = new CountingMetrics(telemetry);
            await Traced(telemetry, AiMessage(), wrap: span => new MetricsSpan(span, counting));
            await telemetry.StartSpanAsync("caller", span =>
            {
                var observer = AiTelemetry.ObserveModelRequest(AiModel, new MetricsSpan(span, counting), poll: true);
                foreach (var value in AiEvents(AiMessage())) observer.OnEvent(value);
                observer.OnResult(AiMessage());
            });
            var client = new ChatClient(new FauxTransport(_ => AiEvents(AiMessage())));
            await telemetry.StartSpanAsync("caller", async span => { await client.CompleteAsync(AiRequest(new MetricsSpan(span, counting))); });
            await Settled(telemetry);
            Json(new[] { "pi.ai.request.duration", "pi.ai.first_chunk.duration" }, counting.Histograms, "histogram creations");
            Json(new[] { "pi.ai.tokens", "pi.ai.cost" }, counting.Counters, "counter creations");
            Equal(0, counting.UpDownCounters.Count, "no up-down counters");
            Equal(21, telemetry.GetMetrics().Count, "measurements");
        }),
        ("telemetry.ai.a-failing-adapter-never-changes-the-chat", async () =>
        {
            foreach (var (failStart, failEvents) in new[] { (true, false), (false, true) })
            {
                var telemetry = new InMemoryTelemetryContext();
                var result = AiMessage();
                var events = AiEvents(result);
                var client = new ChatClient(new FauxTransport(_ => events));
                var received = new List<StreamEvent>();
                ChatResult? completion = null;
                await telemetry.StartSpanAsync("caller", async parent =>
                {
                    await using var run = await client.StartAsync(AiRequest(new FaultySpan(parent, failStart, failEvents)));
                    await foreach (var value in run.ReadEventsAsync()) received.Add(value);
                    completion = await run.Completion;
                });
                await Settled(telemetry);
                Equal(events.Length, received.Count, $"delivered events (start={failStart})");
                Same(result, completion!.Message, "result");
                Check(completion.Failure is null, "no failure");
                Equal(failStart ? 1 : 2, telemetry.GetSpans().Count, "spans");
            }
        }),
        ("telemetry.ai.request-options-carry-the-span-through-registry-dispatch", async () =>
        {
            ITelemetrySpan telemetry = NoopTelemetry.Context;
            var options = new ImagesOptions { Telemetry = telemetry };
            Same(telemetry, options.Telemetry, "images option");
            Same(telemetry, new ClassifierOptions { Telemetry = telemetry }.Telemetry, "classifier option");
            Same(telemetry, (options with { MaxRetries = 1 }).Telemetry, "copied options");
            var images = new CapturingImages();
            var model = new ImageModel("image-model", "Image Model", images.Api, "telemetry-image-provider", "https://example.test", ["text"],
                new ModelCost(0, 0, 0, 0), ["image"]);
            var registry = new ModelOperationsRegistry((_, _) => ValueTask.FromResult<ProviderAuthResult?>(new(null)));
            registry.SetProvider(new ModelOperationsProvider(model.Provider, "Telemetry")
            {
                Models = [model],
                Images = ImmutableDictionary<string, IImagesApi>.Empty.Add(images.Api, images)
            });
            var output = await registry.GenerateImagesAsync(model, new ImagesContext([new ImagesTextBlock("circle")]), options);
            Equal(ModelOperationStopReason.Stop, output.StopReason, "image result");
            Same(telemetry, images.Seen?.Telemetry, "dispatched options keep the span");
            var transport = new FauxTransport(_ => AiEvents(AiMessage()));
            await new ChatClient(transport).CompleteAsync(AiRequest(telemetry));
            Same(telemetry, transport.Seen?.Telemetry, "chat request keeps the span");
        }),
    ];
}
