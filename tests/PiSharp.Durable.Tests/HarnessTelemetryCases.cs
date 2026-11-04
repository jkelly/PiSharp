// Pi 2ec55d808 (MIT): packages/durable/test/harness-telemetry.test.ts, ported in the Pi v1.5.0 sync (area telemetry) from the text of
// that commit's cases as f9a26365 has them. The file is also changed by areas tools-schema (pi-schema parameters, here JSON Schema),
// observers (observeCommits, here SubscribeCommits, the v1.2.0 form this port keeps until that area lands) and vocabulary, so its citation
// names the telemetry commit. Every case records into a fresh InMemoryTelemetryContext, or a throwing in-process adapter: nothing leaves
// the process. Adaptations, beyond those of ChatSupport.cs and HarnessSupport.cs:
// - toMatchObject/toEqual of recorded spans compare the fields upstream names; carriers compare by their entries; JSON.stringify of a span
//   is Dump, its name, attributes, events, links and status. A thrown Error's `name` is the exception's type name here, so the faulted
//   case throws a PrivateExceptionName exception.
// - vi.spyOn(storage, "submission") is a counting storage; vi.spyOn(telemetry.propagation, "inject" | "extract") is SpyTelemetry, which
//   wraps InMemoryTelemetryContext so every span, extracted parent and the root expose a counting propagation.
// - trace: undefined and an omitted trace are both TaskOptions.Trace null; "root" is TaskTrace.Root.
// - runtime.report's { kind: "host", callback: "models" } is ReportFrom.Host(HostCallback.Models).
// - The codemode case runs DurableCodemode's tool on PiSharp.Codemode's sandbox, as the codemode.* groups do.
// - The throwing adapters are small ITelemetryContext and ITelemetrySpan classes over the no-op ones (upstream's object spreads).
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using PiSharp.Telemetry;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessTelemetryCases() => TelemetryTests.Cases();
}

/// <summary>The harness.telemetry.* cases (upstream harness-telemetry.test.ts).</summary>
internal static class TelemetryTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly TaskOptions ConversationOwned = new(TaskOwnership.Conversation);

    /// <summary>Upstream <c>openTracked</c>.</summary>
    private static Task<OpenedChat> OpenTracked(IStorage storage, ChatSetup setup, ITelemetryContext telemetry) =>
        OpenChat(storage, setup, telemetry: telemetry);

    /// <summary>Upstream <c>toolReply</c>.</summary>
    private static JsonObjectValue ToolReply(string name, JsonObjectValue? args = null) =>
        FauxAssistantMessage([FauxToolCall(name, args ?? JsonObjectValue.Empty, "call-1")], stopReason: "toolUse");

    private static readonly JsonObjectValue NoParameters = O("""{"type":"object","properties":{},"additionalProperties":false}""");

    private static ToolRegistration Tool(string name, ToolExecute execute, JsonObjectValue? parameters = null, ToolReplay replay = ToolReplay.Unsafe) =>
        Define.Tool(new ToolRegistration { Name = name, Description = name, Parameters = parameters ?? NoParameters, Execute = execute, Replay = replay });

    private static ToolExecutionResult Text(string text) => new() { Output = JsonValue.Array(FauxText(text)) };

    private static ToolExecutionResult Empty() => new() { Output = JsonArrayValue.Empty };

    private static DurableTask Task1(string name, IReadOnlyDictionary<string, TaskPhaseHandler> phases, string initial) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = 1,
        Initial = _ => O(initial),
        Phases = phases,
        Abort = (_, runtime, context) => runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Aborted(), context),
    });

    private static Task Complete(ITaskRuntime runtime, DurableContext context) =>
        runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Completed(JsonValue.Null), context);

    // ─── Recorded telemetry ─────────────────────────────────────────────────

    private static Task AllSettled(InMemoryTelemetryContext telemetry) => Eventually(() => telemetry.GetSpans().All(span => span.Settled));

    private static IEnumerable<RecordedTelemetrySpan> Named(InMemoryTelemetryContext telemetry, string name) =>
        telemetry.GetSpans().Where(span => span.Name == name);

    private static string? Str(RecordedTelemetrySpan span, string name) =>
        span.Attributes[name] is { Type: TelemetryAttributeType.String } value ? value.GetString() : null;

    private static double? Num(RecordedTelemetrySpan span, string name) =>
        span.Attributes[name] is { Type: TelemetryAttributeType.Number } value ? value.GetNumber() : null;

    private static bool? Bool(RecordedTelemetrySpan span, string name) =>
        span.Attributes[name] is { Type: TelemetryAttributeType.Boolean } value ? value.GetBoolean() : null;

    private static bool SameCarrier(IReadOnlyDictionary<string, string>? expected, IReadOnlyDictionary<string, string>? actual) =>
        expected is null ? actual is null : actual is not null && expected.Count == actual.Count &&
            expected.All(item => actual.TryGetValue(item.Key, out var value) && value == item.Value);

    private static string Carrier(IReadOnlyDictionary<string, string>? carrier) =>
        carrier is null ? "null" : "{" + string.Join(",", carrier.Select(item => $"{item.Key}:{item.Value}")) + "}";

    private static void SameCarrier(IReadOnlyDictionary<string, string>? expected, IReadOnlyDictionary<string, string>? actual, string what) =>
        Check(SameCarrier(expected, actual), $"{what}: expected {Carrier(expected)}, actual {Carrier(actual)}");

    private static RecordedTelemetryEvent Event(string name, SpanAttributes attributes) => new(name, attributes);

    private static bool Contains(IEnumerable<RecordedTelemetryEvent> events, RecordedTelemetryEvent expected) => events.Any(expected.Equals);

    private static string Events(IEnumerable<RecordedTelemetryEvent> events) => "[" + string.Join(", ", events.Select(item => item.Name + item.Attributes)) + "]";

    private static void SameEvents(IReadOnlyList<RecordedTelemetryEvent> expected, IReadOnlyList<RecordedTelemetryEvent> actual, string what) =>
        Check(expected.SequenceEqual(actual), $"{what}: expected {Events(expected)}, actual {Events(actual)}");

    /// <summary>JSON.stringify of a recorded span: its name, attributes, events, links and status.</summary>
    private static string Dump(RecordedTelemetrySpan span) =>
        $"{span.Name}|{span.Attributes}|{Events(span.Events)}|{string.Join(",", span.Links.Select(link => Carrier(link.Context) + link.Attributes))}|" +
        $"{span.Status.Status}:{span.Status.Error?.Name}:{span.Status.Error?.Message}";

    private static string Dump(IEnumerable<RecordedTelemetrySpan> spans) => string.Join("\n", spans.Select(Dump));

    /// <summary>JSON.stringify of the spans' attributes and events.</summary>
    private static string Recorded(IEnumerable<RecordedTelemetrySpan> spans) =>
        string.Join("\n", spans.Select(span => $"{span.Attributes}|{Events(span.Events)}"));

    private static List<SpanAttributes> MetricAttributes(InMemoryTelemetryContext telemetry, string name) =>
        [.. telemetry.GetMetrics().Where(metric => metric.Name == name).Select(metric => metric.Attributes)];

    private static void SameAttributes(IReadOnlyList<SpanAttributes> expected, IReadOnlyList<SpanAttributes> actual, string what) =>
        Check(expected.SequenceEqual(actual), $"{what}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");

    // ─── Adapters ───────────────────────────────────────────────────────────

    /// <summary>A thrown error whose type name must never be recorded (upstream's <c>error.name = "private-exception-name"</c>).</summary>
    private sealed class PrivateExceptionName(string message) : Exception(message);

    /// <summary>A counting storage (upstream <c>vi.spyOn(storage, "submission")</c>).</summary>
    private sealed class SubmissionReads(IStorage inner) : DelegatingStorage(inner)
    {
        private int reads;
        public int Reads => Volatile.Read(ref reads);
        public volatile bool Counting;

        public override Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id, DurableContext context)
        {
            if (Counting) Interlocked.Increment(ref reads);
            return base.GetSubmissionAsync(id, context);
        }
    }

    /// <summary>
    /// InMemoryTelemetryContext with counting propagation (upstream <c>vi.spyOn(telemetry.propagation, ...)</c>): the root, every span and
    /// every extracted parent expose this propagation, and calls reach the recorder with its own contexts.
    /// </summary>
    private sealed class SpyTelemetry(InMemoryTelemetryContext inner) : ITelemetryContext, ITelemetryPropagation
    {
        public InMemoryTelemetryContext Inner { get; } = inner;
        public SyncList<ITelemetryContext> Injected { get; } = new();
        public SyncList<IReadOnlyDictionary<string, string>?> Extracted { get; } = new();
        public volatile bool Spying;

        public ITelemetryMetrics? Metrics => Inner.Metrics;
        public ITelemetryPropagation? Propagation => this;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
            Inner.StartSpanAsync(Unwrap(options), span => callback(new Span(this, span)));

        public IReadOnlyDictionary<string, string>? Inject(ITelemetryContext context)
        {
            if (Spying) Injected.Add(context);
            return Inner.Inject(Unwrap(context));
        }

        public ITelemetryContext Extract(IReadOnlyDictionary<string, string>? carrier)
        {
            if (Spying) Extracted.Add(carrier);
            return new Parent(this, Inner.Extract(carrier));
        }

        private ITelemetryContext Unwrap(ITelemetryContext context) => context switch
        {
            Span span => span.Inner,
            Parent parent => parent.Inner,
            SpyTelemetry spy when ReferenceEquals(spy, this) => Inner,
            _ => context,
        };

        private SpanOptions Unwrap(SpanOptions options) =>
            options.Links is null ? options : options with { Links = [.. options.Links.Select(link => link with { Context = Unwrap(link.Context) })] };

        private sealed class Parent(SpyTelemetry spy, ITelemetryContext inner) : ITelemetryContext
        {
            public ITelemetryContext Inner { get; } = inner;
            public ITelemetryMetrics? Metrics => Inner.Metrics;
            public ITelemetryPropagation? Propagation => spy;
            public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
                Inner.StartSpanAsync(spy.Unwrap(options), span => callback(new Span(spy, span)));
        }

        private sealed class Span(SpyTelemetry spy, ITelemetrySpan inner) : ITelemetrySpan
        {
            public ITelemetrySpan Inner { get; } = inner;
            public ITelemetryMetrics? Metrics => Inner.Metrics;
            public ITelemetryPropagation? Propagation => spy;
            public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) =>
                Inner.StartSpanAsync(spy.Unwrap(options), span => callback(new Span(spy, span)));
            public void AddLink(SpanLink link) => Inner.AddLink(link with { Context = spy.Unwrap(link.Context) });
            public void AddEvent(string name, SpanAttributes? attributes = null) => Inner.AddEvent(name, attributes);
            public void SetAttributes(SpanAttributes attributes) => Inner.SetAttributes(attributes);
            public void SetStatus(SpanStatus status) => Inner.SetStatus(status);
        }
    }

    /// <summary>Instruments whose <c>add</c> throws (upstream's <c>throwingMetric</c>).</summary>
    private sealed class ThrowingInstrument : ITelemetryCounter, ITelemetryUpDownCounter
    {
        public void Add(double value, SpanAttributes? attributes = null) => throw new InvalidOperationException("metrics down");
    }

    /// <summary><c>{ ...NOOP_TELEMETRY_METRICS, counter | upDownCounter: () => throwing }</c>.</summary>
    private sealed class ThrowingMetrics(bool counter, bool upDownCounter) : ITelemetryMetrics
    {
        public ITelemetryCounter Counter(string name, InstrumentOptions? options = null) =>
            counter ? new ThrowingInstrument() : NoopTelemetry.Metrics.Counter(name, options);
        public ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null) =>
            upDownCounter ? new ThrowingInstrument() : NoopTelemetry.Metrics.UpDownCounter(name, options);
        public ITelemetryHistogram Histogram(string name, HistogramOptions? options = null) => NoopTelemetry.Metrics.Histogram(name, options);
    }

    /// <summary><c>{ ...NOOP_TELEMETRY_CONTEXT, metrics }</c>.</summary>
    private sealed class MetricsOnly(ITelemetryMetrics metrics) : ITelemetryContext
    {
        public ITelemetryMetrics? Metrics => metrics;
        public ITelemetryPropagation? Propagation => NoopTelemetry.Propagation;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => NoopTelemetry.Context.StartSpanAsync(options, callback);
    }

    /// <summary><c>{ ...NOOP_TELEMETRY_CONTEXT, addEvent: () => throw }</c>.</summary>
    private sealed class ThrowingEvents : ITelemetrySpan
    {
        public ITelemetryMetrics? Metrics => NoopTelemetry.Metrics;
        public ITelemetryPropagation? Propagation => NoopTelemetry.Propagation;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => NoopTelemetry.Context.StartSpanAsync(options, callback);
        public void AddLink(SpanLink link) { }
        public void AddEvent(string name, SpanAttributes? attributes = null) => throw new InvalidOperationException("telemetry down");
        public void SetAttributes(SpanAttributes attributes) { }
        public void SetStatus(SpanStatus status) { }
    }

    // ─── Cases ──────────────────────────────────────────────────────────────

    internal static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return Case("harness.telemetry.records-content-free-submit-and-phase-spans-nested-parents-checkpoints-entry-events-and-metrics", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            AddTool(setup.Registry, Tool("inner", (_, _, _) => Task.FromResult(Text("private-result")),
                O("""{"type":"object","properties":{"secret":{"type":"string"}},"required":["secret"],"additionalProperties":false}""")));
            AddTool(setup.Registry, Tool("outer", async (_, api, callContext) =>
            {
                await api.ExecuteToolAsync("inner", Obj(("secret", "private-argument")), callContext);
                return Text("private-output");
            }));
            setup.Faux.SetResponses(ToolReply("outer"), FauxAssistantMessage("private-answer"));
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            var submitted = await root.SubmitAsync(SubmissionDraft.Input("private-input", requestId: "dedupe"), Context);
            await submitted.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await AllSettled(telemetry);
            var record = await submitted.StatusAsync(Context);
            var again = await root.SubmitAsync(SubmissionDraft.Input("another-private-input", requestId: "dedupe"), Context);
            Equal(record.ToJson().ToJson(), (await again.StatusAsync(Context)).ToJson().ToJson(), "deduplicated status");
            var spans = telemetry.GetSpans();
            var submit = spans.First(span => span.Name == "pi.durable.submit");
            Check(submit.ParentId is null && Num(submit, "submission.id") == submitted.Id.Value && Str(submit, "result") == "placed", $"submit: {Dump(submit)}");
            SameCarrier(submit.Context, record.Trace, "record trace");
            var phases = spans.Where(span => span.Name == "pi.durable.phase").ToList();
            Check(phases.Count > 4, $"phases: {phases.Count}");
            var requests = spans.Where(span => span.Name == "pi.ai.request").ToList();
            Equal(2, requests.Count, "requests");
            foreach (var request in requests)
                Equal("request", phases.FirstOrDefault(phase => phase.Id == request.ParentId) is { } parent ? Str(parent, "phase") : null, "request parent phase");
            SameCarrier(submit.Context, phases[0].ParentContext, "first phase parent");
            Check(Str(phases[0], "phase") == "prepare" && Bool(phases[0], "resumed") == false && Str(phases[0], "outcome") == "next=request",
                $"first phase: {Dump(phases[0])}");
            var outer = phases.First(span => Str(span, "tool.name") == "outer");
            var inner = phases.First(span => Str(span, "tool.name") == "inner");
            SameCarrier(outer.Context, inner.ParentContext, "inner parent");
            Check(Str(outer, "phase") == "call" && Str(outer, "outcome") == "done" && Str(outer, "tool.call_id") == "call-1", $"outer: {Dump(outer)}");
            Check(Contains(outer.Events, Event("pi.durable.checkpoint", new SpanAttributes { ["phase"] = "execute" })), $"outer checkpoint: {Dump(outer)}");
            Check(phases.Any(span => Str(span, "outcome") == "waiting"), "a waiting phase");
            Check(Contains(submit.Events, Event("pi.durable.entry", new SpanAttributes { ["entry.id"] = record.Entry!.Value.Value, ["entry.kind"] = "pi.user" })),
                $"submit entry event: {Dump(submit)}");
            Check(Contains(phases.SelectMany(span => span.Events), Event("pi.durable.submission", new SpanAttributes
            {
                ["submission.id"] = submitted.Id.Value, ["status"] = "done", ["answer.entry_id"] = record.Answer!.Value.Value,
            })), "settlement event");
            var recorded = Recorded(spans);
            foreach (var content in new[] { "private-input", "private-answer", "private-output", "private-result", "private-argument", "secret", "gen_ai" })
                Check(!recorded.Contains(content, StringComparison.Ordinal), $"recorded {content}");
            await harness.CloseAsync(Context);
            var metrics = telemetry.GetMetrics();
            Equal(phases.Count, metrics.Count(metric => metric.Name == "pi.durable.phase.duration"), "phase durations");
            Equal(0.0, metrics.Where(metric => metric.Name == "pi.durable.tasks.active").Sum(metric => metric.Value), "active tasks");
            foreach (var metric in metrics.Where(metric => metric.Name.StartsWith("pi.durable.", StringComparison.Ordinal)))
                Check(!metric.Attributes.Keys.Any(key => key.Contains("id", StringComparison.Ordinal)), $"{metric.Name} attributes {metric.Attributes}");
            Equal(0, setup.Reports.Count, "reports");
        });

        yield return Case("harness.telemetry.returns-admission-results-without-re-reading-records-or-probing-root-link-targets", async () =>
        {
            var setup = Setup();
            var spy = new SpyTelemetry(new InMemoryTelemetryContext());
            var storage = new SubmissionReads(new MemoryStorage());
            var gate = new Deferred();
            var reached = new Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async _ =>
            {
                reached.Resolve();
                await gate.Task;
                return FauxAssistantMessage("first");
            }), FauxAssistantMessage("second"));
            var (harness, root) = await OpenTracked(storage, setup, spy);
            storage.Counting = true;
            spy.Spying = true;
            var first = await root.SubmitAsync(SubmissionDraft.Input("first"), Context);
            await reached.Task;
            var second = await root.SubmitAsync(SubmissionDraft.Input("second", requestId: "queued"), Context);
            var duplicate = await root.SubmitAsync(SubmissionDraft.Input("ignored", requestId: "queued"), Context);
            Equal(second.Id, duplicate.Id, "duplicate id");
            Equal(0, storage.Reads, "submission reads");
            Check(!spy.Injected.Snapshot().Any(target => ReferenceEquals(target, spy)), "never injects the root");
            Check(!spy.Extracted.Snapshot().Any(carrier => carrier is null), "never extracts an absent carrier");
            Check(Named(spy.Inner, "pi.durable.submit").Select(span => Str(span, "result")).SequenceEqual(["placed", "queued", "queued"]),
                $"results: {Dump(Named(spy.Inner, "pi.durable.submit"))}");
            storage.Counting = false;
            spy.Spying = false;
            gate.Resolve();
            await first.WaitAsync(Context);
            await second.WaitAsync(Context);
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.inherits-omitted-or-undefined-task-traces-and-stores-no-carrier-for-an-explicit-root", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var task = Task1("test.trace", new Dictionary<string, TaskPhaseHandler> { ["finish"] = (_, runtime, context) => Complete(runtime, context) },
                """{"phase":"finish"}""");
            AddTask(setup.Registry, task);
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            var ids = await telemetry.StartSpanAsync("creator", span => root.CommitAsync(async tx => new[]
            {
                await tx.CreateTaskAsync(task, JsonValue.Null, ConversationOwned),
                await tx.CreateTaskAsync(task, JsonValue.Null, ConversationOwned with { Trace = null }),
                await tx.CreateTaskAsync(task, JsonValue.Null, ConversationOwned with { Trace = TaskTrace.Root }),
            }, Context.WithTelemetry(span)));
            foreach (var id in ids) await harness.WaitForTaskAsync(id, Context);
            await AllSettled(telemetry);
            var creator = telemetry.GetSpans()[0];
            var records = new List<TaskRecord>();
            foreach (var id in ids) records.Add((await harness.GetTaskAsync(id, Context))!);
            SameCarrier(creator.Context, records[0].Trace, "omitted trace");
            SameCarrier(creator.Context, records[1].Trace, "undefined trace");
            SameCarrier(null, records[2].Trace, "root trace");
            var phases = Named(telemetry, "pi.durable.phase").ToList();
            Equal(3, phases.Count, "phases");
            SameCarrier(creator.Context, phases[0].ParentContext, "first parent");
            SameCarrier(creator.Context, phases[1].ParentContext, "second parent");
            SameCarrier(null, phases[2].ParentContext, "root parent");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.records-checkpoints-only-when-the-running-phase-changes", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var task = Task1("test.checkpoint", new Dictionary<string, TaskPhaseHandler>
            {
                ["call"] = async (_, runtime, context) =>
                {
                    await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Running(O("""{"phase":"call","count":1}""")), context);
                    await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Running(O("""{"phase":"call","count":2}""")), context);
                    await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Running(O("""{"phase":"finish","count":2}""")), context);
                },
                ["finish"] = (_, runtime, context) => Complete(runtime, context),
            }, """{"phase":"call","count":0}""");
            AddTask(setup.Registry, task);
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null, ConversationOwned), Context);
            await harness.WaitForTaskAsync(id, Context);
            await AllSettled(telemetry);
            SameEvents([Event("pi.durable.checkpoint", new SpanAttributes { ["phase"] = "finish" })],
                [.. telemetry.GetSpans().SelectMany(span => span.Events).Where(item => item.Name == "pi.durable.checkpoint")], "checkpoints");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.starts-each-queued-input-s-run-in-its-admission-trace-and-links-input-roots-to-their-caller", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var gate = new Deferred();
            var reached = new Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async _ =>
            {
                reached.Resolve();
                await gate.Task;
                return FauxAssistantMessage("first");
            }), FauxAssistantMessage("second"));
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            await telemetry.StartSpanAsync("caller", async caller =>
            {
                var context = Context.WithTelemetry(caller);
                var first = await root.SubmitAsync(SubmissionDraft.Input("first"), context);
                await reached.Task;
                var second = await root.SubmitAsync(SubmissionDraft.Input("second"), context);
                Equal(SubmissionStatus.Queued, (await second.StatusAsync(context)).Status, "second queued");
                gate.Resolve();
                await first.WaitAsync(context);
                await second.WaitAsync(context);
            });
            await harness.WaitForIdleAsync(Context);
            await AllSettled(telemetry);
            var spans = telemetry.GetSpans();
            var callerSpan = spans.First(span => span.Name == "caller");
            var submits = spans.Where(span => span.Name == "pi.durable.submit").ToList();
            Equal(2, submits.Count, "submits");
            foreach (var submit in submits)
            {
                Check(submit.ParentId is null, "submit is a root");
                Check(submit.Links is [var link] && SameCarrier(callerSpan.Context, link.Context) && link.Attributes.Count == 0, $"submit links: {Dump(submit)}");
                Check(spans.Any(span => span.Name == "pi.durable.phase" && Str(span, "phase") == "prepare" && SameCarrier(submit.Context, span.ParentContext)),
                    "a prepare phase in the submit's trace");
            }
            Check(!SameCarrier(submits[0].Context, submits[1].Context), "distinct submit traces");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.links-the-other-queued-inputs-to-the-first-invocation-of-their-shared-run", async () =>
        {
            var setup = Setup();
            setup.FollowUpMode = QueueMode.All;
            var telemetry = new InMemoryTelemetryContext();
            var gate = new Deferred();
            var reached = new Deferred();
            setup.Faux.SetResponses(new FauxResponseFactory(async _ =>
            {
                reached.Resolve();
                await gate.Task;
                return FauxAssistantMessage("first");
            }), FauxAssistantMessage("shared"));
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            var first = await root.SubmitAsync(SubmissionDraft.Input("first"), Context);
            await reached.Task;
            var second = await root.SubmitAsync(SubmissionDraft.Input("second"), Context);
            var third = await root.SubmitAsync(SubmissionDraft.Input("third"), Context);
            var secondTrace = (await second.StatusAsync(Context)).Trace;
            var thirdTrace = (await third.StatusAsync(Context)).Trace;
            gate.Resolve();
            await first.WaitAsync(Context);
            await second.WaitAsync(Context);
            await third.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await AllSettled(telemetry);
            var tasks = await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = "pi.generation" }, 10), Context);
            var shared = tasks.Items[1];
            SameCarrier(secondTrace, shared.Trace, "shared run trace");
            Check(shared.Links is [var link] && SameCarrier(thirdTrace, link), "shared run links");
            var phases = Named(telemetry, "pi.durable.phase").Where(span => Num(span, "task.id") == shared.Id.Value).ToList();
            Check(phases.Select(span => Str(span, "phase")).SequenceEqual(["prepare", "request"]), $"shared run phases: {Dump(phases)}");
            foreach (var phase in phases)
                Check(phase.Links is [var phaseLink] && SameCarrier(thirdTrace, phaseLink.Context) && phaseLink.Attributes.Count == 0, $"phase links: {Dump(phase)}");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.links-a-steer-admission-to-the-current-run-without-moving-its-work-into-the-steer-s-trace", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var gate = new Deferred();
            var reached = new Deferred();
            AddTool(setup.Registry, Tool("hold", async (_, _, _) =>
            {
                reached.Resolve();
                await gate.Task;
                return Empty();
            }));
            setup.Faux.SetResponses(ToolReply("hold"), FauxAssistantMessage("answer"));
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            var first = await root.SubmitAsync(SubmissionDraft.Input("first"), Context);
            await reached.Task;
            var run = (await harness.InspectAsync(Context)).Tasks.First(task => task.Record.Kind == "pi.generation").Record;
            var steer = await root.SubmitAsync(SubmissionDraft.Input("steer", WhenBusy.Steer), Context);
            var steerTrace = (await steer.StatusAsync(Context)).Trace;
            var steerSpan = telemetry.GetSpans().First(span => span.Name == "pi.durable.submit" && Num(span, "submission.id") == steer.Id.Value);
            Check(steerSpan.ParentId is null && steerSpan.Links is [var link] && SameCarrier(run.Trace, link.Context) && link.Attributes.Count == 0,
                $"steer span: {Dump(steerSpan)}");
            gate.Resolve();
            await first.WaitAsync(Context);
            await steer.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await AllSettled(telemetry);
            Check(!Named(telemetry, "pi.durable.phase").Any(span => SameCarrier(steerTrace, span.ParentContext)), "no phase in the steer's trace");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.keeps-a-public-write-admission-open-through-commit-as-a-child-of-its-caller", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root) = await OpenTracked(storage, setup, telemetry);
            await telemetry.StartSpanAsync("caller", async caller =>
            {
                var held = storage.HoldCommits();
                var writing = root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "app.note", Data = "private-note" }), Context.WithTelemetry(caller));
                await held.Entered;
                var inFlight = telemetry.GetSpans().First(span => span.Name == "pi.durable.submit");
                Check(!inFlight.Settled && inFlight.Events.Count == 0, $"in flight: {Dump(inFlight)}");
                held.Release();
                var submission = await writing;
                var record = await submission.StatusAsync(Context);
                var span = telemetry.GetSpans().First(span => span.Name == "pi.durable.submit");
                SameCarrier(telemetry.GetSpans()[0].Context, span.ParentContext, "write parent");
                Check(span.Settled && Str(span, "result") == "done", $"write span: {Dump(span)}");
                SameCarrier(span.Context, record.Trace, "write trace");
                Check(span.Events.Select(item => item.Name).SequenceEqual(["pi.durable.entry", "pi.durable.submission"]), $"write events: {Dump(span)}");
            });
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.links-an-input-submitted-inside-a-turn-to-the-calling-phase-without-parenting-it", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            AddTool(setup.Registry, Tool("delegate", async (_, api, callContext) =>
            {
                var child = await api.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Task(api.TaskId)), callContext);
                var conversation = (await api.ConversationAsync(child.Id, callContext))!;
                await (await conversation.SubmitAsync(SubmissionDraft.Input("child"), callContext)).WaitAsync(callContext);
                return Empty();
            }));
            setup.Faux.SetResponses(ToolReply("delegate"), FauxAssistantMessage("child answer"), FauxAssistantMessage("parent answer"));
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            await (await root.SubmitAsync(SubmissionDraft.Input("parent"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await AllSettled(telemetry);
            var spans = telemetry.GetSpans();
            var tool = spans.First(span => Str(span, "tool.name") == "delegate");
            var child = spans.Where(span => span.Name == "pi.durable.submit").ElementAt(1);
            Check(child.ParentId is null, "child submit is a root");
            Check(child.Links is [var link] && SameCarrier(tool.Context, link.Context) && link.Attributes.Count == 0, $"child links: {Dump(child)}");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.parents-background-compaction-to-the-prepare-phase-that-creates-it", async () =>
        {
            var setup = Setup(new FauxOptions { Models = [new FauxModelOptions("faux-1", 1000, 100)] });
            setup.Compaction = new CompactionSettings { Enabled = true, ReserveTokens = 300, KeepRecentTokens = 150, BackgroundTokens = 1000 };
            var telemetry = new InMemoryTelemetryContext();
            AddHooks(setup.Registry, HarnessTasks.CompactionTask, new CompactionHooks { BeforeCompact = (_, _, _) => Task.FromResult<CompactionDecision?>(CompactionDecision.Declined) });
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            await root.CommitAsync(async tx =>
            {
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "pi.user", Model = JsonValue.Array(Obj(("role", "user"), ("content", new string('x', 400)), ("timestamp", 1))) });
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "pi.assistant", Model = JsonValue.Array(FauxAssistantMessage(new string('y', 400))) });
            }, Context);
            await (await root.SubmitAsync(SubmissionDraft.Input(new string('z', 400)), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await Eventually(() => telemetry.GetSpans().Any(span => Str(span, "task.name") == "pi.compaction" && span.Settled));
            var spans = telemetry.GetSpans();
            var prepare = spans.First(span => Str(span, "task.name") == "pi.generation" && Str(span, "phase") == "prepare");
            var compaction = spans.First(span => Str(span, "task.name") == "pi.compaction");
            SameCarrier(prepare.Context, compaction.ParentContext, "compaction parent");
            var tasks = await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = "pi.compaction" }, 10), Context);
            Check(tasks.Items[0].Background, "background compaction");
            SameCarrier(prepare.Context, tasks.Items[0].Trace, "compaction trace");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.marks-only-the-first-invocation-after-reopen-resumed-retaining-its-stored-parent", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var storage = new ControlledStorage(new MemoryStorage()) { KeepOpenForReopen = true };
            var recover = false;
            var reached = new Deferred();
            var task = Task1("test.resume", new Dictionary<string, TaskPhaseHandler>
            {
                ["hold"] = async (_, runtime, context) =>
                {
                    if (!Volatile.Read(ref recover))
                    {
                        reached.Resolve();
                        await Aborted(context.CancellationToken);
                    }
                    await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Running(O("""{"phase":"finish"}""")), context);
                },
                ["finish"] = (_, runtime, context) => Complete(runtime, context),
            }, """{"phase":"hold"}""");
            AddTask(setup.Registry, task);
            var (harness, root) = await OpenTracked(storage, setup, telemetry);
            TaskId id = default;
            await telemetry.StartSpanAsync("creator", async span =>
            {
                id = await root.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null, ConversationOwned), Context.WithTelemetry(span));
            });
            harness.Resume();
            await reached.Task;
            var before = (await harness.GetTaskAsync(id, Context))!;
            await harness.CloseAsync(Context);
            Volatile.Write(ref recover, true);
            var reopenedTelemetry = new InMemoryTelemetryContext();
            (harness, root) = await OpenTracked(storage.Reopen(), setup, reopenedTelemetry);
            await harness.WaitForTaskAsync(id, Context);
            await AllSettled(reopenedTelemetry);
            var phases = telemetry.GetSpans().Concat(reopenedTelemetry.GetSpans()).Where(span => span.Name == "pi.durable.phase").ToList();
            Check(phases.Select(span => Bool(span, "resumed")).SequenceEqual([false, true, false]), $"resumed: {Dump(phases)}");
            Equal("aborted", Str(phases[0], "outcome"), "first outcome");
            Equal(SpanStatus.Ok, phases[0].Status, "first status");
            foreach (var phase in phases) SameCarrier(before.Trace, phase.ParentContext, "phase parent");
            await harness.CloseAsync(Context);
            Check(telemetry.GetMetrics().Concat(reopenedTelemetry.GetMetrics()).Where(metric => metric.Name == "pi.durable.tasks.active")
                .Select(metric => metric.Value).SequenceEqual([1.0, -1, 1, -1]), "active task counts");
        });

        yield return Case("harness.telemetry.emits-entry-and-settlement-events-only-after-successful-storage-commits", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root) = await OpenTracked(storage, setup, telemetry);
            await telemetry.StartSpanAsync("caller", async span =>
            {
                var context = Context.WithTelemetry(span);
                var rollback = await ThrowsAsync<InvalidOperationException>(() => root.CommitAsync(async tx =>
                {
                    await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.rolled-back", Data = "private-rolled-back" });
                    throw new InvalidOperationException("rollback");
                }, context), "rolled back");
                Equal("rollback", rollback.Message, "rollback message");
                Equal(0, telemetry.GetSpans()[0].Events.Count, "no events after rollback");
                var held = storage.HoldCommits();
                var writing = root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.note", Data = "private-content" }), context);
                await held.Entered;
                Equal(0, telemetry.GetSpans()[0].Events.Count, "no events while held");
                held.Release();
                var entry = await writing;
                var submission = await root.CommitAsync(tx => tx.CreateSubmissionAsync(new SubmissionCreate
                {
                    ConversationId = root.Id, Type = SubmissionType.Write, Status = SubmissionStatus.Done, Entry = entry.Id,
                }), context);
                SameEvents([
                    Event("pi.durable.entry", new SpanAttributes { ["entry.id"] = entry.Id.Value, ["entry.kind"] = "app.note" }),
                    Event("pi.durable.submission", new SpanAttributes { ["submission.id"] = submission.Id.Value, ["status"] = "done" }),
                ], telemetry.GetSpans()[0].Events, "caller events");
            });
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.records-internal-compaction-admissions-as-phase-events-without-a-submit-span", async () =>
        {
            var setup = Setup();
            setup.Compaction = new CompactionSettings { KeepRecentTokens = 1 };
            var telemetry = new InMemoryTelemetryContext();
            var storage = new ControlledStorage(new MemoryStorage());
            Gate? held = null;
            AddHooks(setup.Registry, HarnessTasks.CompactionTask, new CompactionHooks
            {
                BeforeCompact = (_, _, _) =>
                {
                    Volatile.Write(ref held, storage.HoldCommits());
                    return Task.FromResult<CompactionDecision?>(CompactionDecision.FromSummary("private-summary"));
                },
            });
            var (harness, root) = await OpenTracked(storage, setup, telemetry);
            await root.CommitAsync(async tx =>
            {
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "pi.user", Model = JsonValue.Array(Obj(("role", "user"), ("content", "old"), ("timestamp", 1))) });
                await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "pi.assistant", Model = JsonValue.Array(FauxAssistantMessage("answer")) });
            }, Context);
            var id = await root.CompactAsync(null, Context);
            await Eventually(() => Volatile.Read(ref held) is not null);
            await held!.Entered;
            Check(!telemetry.GetSpans().Any(span => span.Name == "pi.durable.submit"), "no submit span while held");
            var holding = telemetry.GetSpans().First(span => Str(span, "task.name") == "pi.compaction");
            Check(!holding.Settled && holding.Events.Count == 0, $"held phase: {Dump(holding)}");
            held.Release();
            await harness.WaitForTaskAsync(id, Context);
            await AllSettled(telemetry);
            var spans = telemetry.GetSpans();
            var phase = spans.First(span => Str(span, "task.name") == "pi.compaction");
            Check(!spans.Any(span => span.Name == "pi.durable.submit"), "no submit span");
            Check(phase.Events.Any(item => item.Name == "pi.durable.entry"), $"entry event: {Dump(phase)}");
            Check(phase.Events.Any(item => item.Name == "pi.durable.submission" && item.Attributes["status"] is { } status && status.Equals((AttributeValue)"done")),
                $"submission event: {Dump(phase)}");
            Check(!Dump(spans).Contains("private-summary", StringComparison.Ordinal), "no summary recorded");
            await harness.CloseAsync(Context);
        });

        foreach (var (status, id) in new[]
        {
            ("faulted", "harness.telemetry.records-a-fixed-faulted-category-and-a-bounded-diagnostic"),
            ("failed", "harness.telemetry.records-a-fixed-failed-category-and-a-bounded-diagnostic"),
            ("orphaned", "harness.telemetry.records-a-fixed-orphaned-category-and-a-bounded-diagnostic"),
        })
        {
            yield return Case(id, async () =>
            {
                var setup = Setup();
                var telemetry = new InMemoryTelemetryContext();
                var message = string.Concat(Enumerable.Repeat("diagnostic ", 80));
                var task = Task1("test.fault", new Dictionary<string, TaskPhaseHandler>
                {
                    ["fail"] = async (_, runtime, context) =>
                    {
                        if (status == "faulted") throw new PrivateExceptionName(message);
                        TaskOutcome outcome = status == "orphaned"
                            ? new OrphanedTaskOutcome(message)
                            : new FailedTaskOutcome(new TaskOutcomeError(message, "private-error-detail"));
                        await runtime.CommitAsync((_, _) => (TaskState?)new TerminalTaskState(outcome), context);
                    },
                }, """{"phase":"fail"}""");
                AddTask(setup.Registry, task);
                var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
                var id = await root.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null,
                    ConversationOwned with { Trace = TaskTrace.Of(new Dictionary<string, string> { ["unknown"] = "carrier" }) }), Context);
                var record = await harness.WaitForTaskAsync(id, Context);
                var settled = ((TerminalTaskState)record.State).Outcome;
                Equal(status, WireStatus(settled.Status), "outcome status");
                Equal(message, settled switch { OrphanedTaskOutcome orphaned => orphaned.Reason, FailedTaskOutcome failed => failed.Error.Message,
                    FaultedTaskOutcome faulted => faulted.Error.Message, _ => null }, "outcome message");
                await AllSettled(telemetry);
                var span = Named(telemetry, "pi.durable.phase").First();
                Check(span.ParentId is null && Str(span, "outcome") == "faulted", $"phase: {Dump(span)}");
                Equal(SpanStatus.Failed($"task_{status}", message[..500]), span.Status, "status");
                if (status == "faulted")
                    Check(Contains(span.Events, Event("pi.durable.report", new SpanAttributes
                    {
                        ["type"] = "task_faulted", ["reason"] = "threw", ["extension"] = "task:test.fault", ["task.id"] = id.Value,
                    })), $"report event: {Dump(span)}");
                Check(!Dump(span).Contains(nameof(PrivateExceptionName), StringComparison.Ordinal), "no exception name");
                Check(!Dump(span).Contains("private-error-detail", StringComparison.Ordinal), "no error detail");
                Check(!Recorded([span]).Contains("diagnostic", StringComparison.Ordinal), "no diagnostic in attributes or events");
                await harness.CloseAsync(Context);
            });
        }

        yield return Case("harness.telemetry.keeps-both-interrupted-runs-and-explicit-abort-handlers-at-status-ok", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var reached = new Deferred();
            var task = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.abort",
                Version = 1,
                Initial = _ => O("""{"phase":"hold"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["hold"] = async (_, _, context) =>
                    {
                        reached.Resolve();
                        await Aborted(context.CancellationToken);
                    },
                },
                Abort = (_, runtime, context) => runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Aborted("private-abort-reason"), context),
            });
            AddTask(setup.Registry, task);
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null, ConversationOwned), Context);
            harness.Resume();
            await reached.Task;
            await harness.AbortTaskAsync(id, Context);
            await harness.WaitForTaskAsync(id, Context);
            await AllSettled(telemetry);
            var phases = Named(telemetry, "pi.durable.phase").ToList();
            Check(phases.Select(span => Str(span, "kind")).SequenceEqual(["run", "abort"]), $"kinds: {Dump(phases)}");
            foreach (var span in phases)
            {
                Equal("aborted", Str(span, "outcome"), "outcome");
                Equal(SpanStatus.Ok, span.Status, "status");
            }
            Check(!Dump(phases).Contains("private-abort-reason", StringComparison.Ordinal), "no abort reason");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.mirrors-runtime-reports-as-fixed-field-phase-events-and-type-only-counters", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var error = new InvalidOperationException("private-report-payload");
            var task = Task1("test.report", new Dictionary<string, TaskPhaseHandler>
            {
                ["report"] = async (_, runtime, context) =>
                {
                    runtime.Report(error);
                    runtime.Report(error, ReportFrom.FromExtension("reporting-extension"));
                    runtime.Report(error, ReportFrom.Host(HostCallback.Models));
                    await Complete(runtime, context);
                },
            }, """{"phase":"report"}""");
            AddTask(setup.Registry, task);
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null, ConversationOwned), Context);
            await harness.WaitForTaskAsync(id, Context);
            await AllSettled(telemetry);
            var span = Named(telemetry, "pi.durable.phase").First();
            SameEvents([
                Event("pi.durable.report", new SpanAttributes { ["type"] = "task_error", ["extension"] = "task:test.report", ["task.id"] = id.Value }),
                Event("pi.durable.report", new SpanAttributes { ["type"] = "extension_error", ["extension"] = "reporting-extension", ["task.id"] = id.Value }),
                Event("pi.durable.report", new SpanAttributes { ["type"] = "host_error", ["task.id"] = id.Value }),
            ], span.Events, "report events");
            Check(setup.Reports.Snapshot().SequenceEqual([error, error, error]), "reports");
            Check(!telemetry.GetSpans().Any(item => item.Name == "pi.durable.report"), "no report span");
            Check(telemetry.GetMetrics().Where(metric => metric.Name == "pi.durable.reports").Select(metric => (metric.Attributes, metric.Value))
                .SequenceEqual([
                    (new SpanAttributes { ["type"] = "task_error" }, 1.0),
                    (new SpanAttributes { ["type"] = "extension_error" }, 1.0),
                    (new SpanAttributes { ["type"] = "host_error" }, 1.0),
                ]), "report counters");
            Check(!Dump(telemetry.GetSpans()).Contains("private-report-payload", StringComparison.Ordinal), "no payload");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.reports-hook-failures-on-the-phase-and-clock-failures-without-a-phase-context", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var hookError = new InvalidOperationException("private-hook-error");
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks { BeforeRequest = (_, _, _) => throw hookError }, "reporting-hooks");
            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            await (await root.SubmitAsync(SubmissionDraft.Input("input"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await AllSettled(telemetry);
            var request = Named(telemetry, "pi.durable.phase").First(span => Str(span, "phase") == "request");
            Check(Contains(request.Events, Event("pi.durable.report", new SpanAttributes
            {
                ["type"] = "extension_error", ["extension"] = "reporting-hooks", ["task.id"] = Num(request, "task.id"),
            })), $"hook report: {Dump(request)}");
            var clockError = new InvalidOperationException("private-clock-error");
            var task = Task1("test.clock", new Dictionary<string, TaskPhaseHandler>
            {
                ["clock"] = async (_, runtime, context) =>
                {
                    setup.Now = () => throw clockError;
                    runtime.Now();
                    setup.Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await Complete(runtime, context);
                },
            }, """{"phase":"clock"}""");
            AddTask(setup.Registry, task);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(task, JsonValue.Null, ConversationOwned), Context);
            await harness.WaitForTaskAsync(id, Context);
            await AllSettled(telemetry);
            var clock = telemetry.GetSpans().First(span => Str(span, "task.name") == "test.clock");
            Equal(0, clock.Events.Count, "clock events");
            Check(!telemetry.GetSpans().Any(span => span.Name == "pi.durable.report"), "no report span");
            SameAttributes([new SpanAttributes { ["type"] = "extension_error" }, new SpanAttributes { ["type"] = "host_error" }],
                MetricAttributes(telemetry, "pi.durable.reports"), "report counters");
            Check(setup.Reports.Snapshot().SequenceEqual<Exception>([hookError, clockError]), "reports");
            var dump = Dump(telemetry.GetSpans());
            Check(!dump.Contains("private-hook-error", StringComparison.Ordinal) && !dump.Contains("private-clock-error", StringComparison.Ordinal), "no error text");
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.counts-an-outside-span-session-failure-without-starting-a-span", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var storage = new ControlledStorage(new MemoryStorage());
            var (harness, root) = await OpenTracked(storage, setup, telemetry);
            var error = new InvalidOperationException("private-storage-error");
            storage.FailNextCommit(error);
            var thrown = await ThrowsAsync<InvalidOperationException>(() => root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.failed" }), Context),
                "refused commit");
            Check(ReferenceEquals(error, thrown), "the commit rejects with the storage error");
            await harness.Closed;
            await AllSettled(telemetry);
            Check(setup.Reports.Snapshot().SequenceEqual<Exception>([error]), "reports");
            Equal(0, telemetry.GetSpans().Count, "spans");
            Check(!string.Join(",", telemetry.GetMetrics().Select(metric => metric.Attributes.ToString())).Contains("private-storage-error", StringComparison.Ordinal),
                "no error text in metrics");
            SameAttributes([new SpanAttributes { ["type"] = "storage_failed" }], MetricAttributes(telemetry, "pi.durable.reports"), "report counters");
        });

        yield return Case("harness.telemetry.counts-reports-raised-with-a-noop-span-without-creating-a-root", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            var error = new InvalidOperationException("private-noop-report");
            var observation = harness.SubscribeCommits((_, _) => throw error);
            await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.note" }), Context);
            await Eventually(() => setup.Reports.Count > 0);
            Check(setup.Reports.Snapshot().SequenceEqual<Exception>([error]), "reports");
            Equal(0, telemetry.GetSpans().Count, "spans");
            SameAttributes([new SpanAttributes { ["type"] = "listener_error" }], MetricAttributes(telemetry, "pi.durable.reports"), "report counters");
            observation.Dispose();
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.attaches-synchronous-commit-listener-reports-to-the-active-submission-span", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            var error = new InvalidOperationException("private-listener-error");
            var observation = harness.SubscribeCommits((publication, _) =>
            {
                if (publication.Changes.Any(change => change is EntryWrite { Value.Kind: "pi.user" })) throw error;
            });
            await (await root.SubmitAsync(SubmissionDraft.Input("input"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await AllSettled(telemetry);
            Check(setup.Reports.Snapshot().SequenceEqual<Exception>([error]), "reports");
            var span = telemetry.GetSpans().First(span => span.Name == "pi.durable.submit");
            Check(Contains(span.Events, Event("pi.durable.report", new SpanAttributes { ["type"] = "listener_error" })), $"submit events: {Dump(span)}");
            Check(!Dump(telemetry.GetSpans()).Contains("private-listener-error", StringComparison.Ordinal), "no error text");
            observation.Dispose();
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.preserves-async-listener-error-containment-and-report-counting", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            setup.Faux.SetResponses(FauxAssistantMessage("answer"));
            var (harness, root) = await OpenTracked(new MemoryStorage(), setup, telemetry);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observation = harness.SubscribeCommits((publication, _) =>
                publication.Changes.Any(change => change is EntryWrite { Value.Kind: "pi.user" }) ? failed.Task : Task.CompletedTask);
            await (await root.SubmitAsync(SubmissionDraft.Input("input"), Context)).WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await AllSettled(telemetry);
            var error = new InvalidOperationException("private-async-listener-error");
            failed.SetException(error);
            await Eventually(() => setup.Reports.Count == 1 && telemetry.GetSpans().All(span => span.Settled));
            Check(setup.Reports.Snapshot().SequenceEqual<Exception>([error]), "reports");
            SameAttributes([new SpanAttributes { ["type"] = "listener_error" }], MetricAttributes(telemetry, "pi.durable.reports"), "report counters");
            Check(!Dump(telemetry.GetSpans()).Contains("private-async-listener-error", StringComparison.Ordinal), "no error text");
            observation.Dispose();
            await harness.CloseAsync(Context);
        });

        yield return Case("harness.telemetry.emits-replay-for-a-codemode-journal-result-without-rerunning-its-nested-tool", async () =>
        {
            var setup = Setup();
            var telemetry = new InMemoryTelemetryContext();
            var storage = new ControlledStorage(new MemoryStorage()) { KeepOpenForReopen = true };
            var sandbox = CodemodeTests.NewSandbox();
            var runs = 0;
            var recover = false;
            var reached = new Deferred();
            AddTool(setup.Registry, DurableCodemode.CreateTool(new CodemodeToolOptions { Sandbox = sandbox, Models = false }));
            AddTool(setup.Registry, Tool("once", (_, _, _) =>
            {
                Interlocked.Increment(ref runs);
                return Task.FromResult(Empty());
            }));
            AddTool(setup.Registry, Tool("hold", async (_, _, callContext) =>
            {
                if (!Volatile.Read(ref recover))
                {
                    reached.Resolve();
                    await Aborted(callContext.CancellationToken);
                }
                return Empty();
            }, replay: ToolReplay.Safe));
            setup.Faux.SetResponses(ToolReply("codemode", Obj(("code", "await tools.once({}); await tools.hold({});"))), FauxAssistantMessage("done"));
            var (harness, _) = await OpenTracked(storage, setup, telemetry);
            var root = await harness.RootAsync(Context);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("run"), Context);
            await reached.Task;
            await harness.CloseAsync(Context);
            Volatile.Write(ref recover, true);
            (harness, _) = await OpenTracked(storage.Reopen(), setup, telemetry);
            await (await harness.SubmissionAsync(submission.Id, Context))!.WaitAsync(Context);
            await harness.WaitForIdleAsync(Context);
            await AllSettled(telemetry);
            Equal(1, Volatile.Read(ref runs), "once runs");
            Check(Contains(telemetry.GetSpans().SelectMany(span => span.Events).Where(item => item.Name == "pi.durable.replay"),
                Event("pi.durable.replay", new SpanAttributes { ["tool.name"] = "once" })), "replay event");
            await harness.CloseAsync(Context);
            await sandbox.CloseAsync();
        });

        yield return Case("harness.telemetry.reports-telemetry-adapters-that-throw-while-a-commit-is-published-and-the-commit-stands", async () =>
        {
            var idle = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.telemetry-idle",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, _, _) => Task.CompletedTask },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var telemetry = new MetricsOnly(new ThrowingMetrics(counter: false, upDownCounter: true));
            var opened = await OpenTasks(new MemoryStorage(), [idle], telemetry: telemetry);
            var harness = opened.Harness;
            var infos = opened.Infos;
            var root = await harness.RootAsync(Context);
            var view = await root.WatchAsync(Context);
            var shown = new SyncList<EntryId?>();
            view.Start((call, _) =>
            {
                var value = Change(call).Value;
                shown.Add(value.Entries.Length > 0 ? value.Entries[^1].Id : null);
                return Task.CompletedTask;
            });
            var hosts = new SyncList<Seq>();
            harness.SubscribeCommits((publication, _) => hosts.Add(publication.Seq));
            var failing = Context.WithTelemetry(new ThrowingEvents());
            var (entry, created) = await root.CommitAsync(async tx =>
            {
                var appended = await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = JsonObjectValue.Empty });
                var task = await tx.CreateTaskAsync(idle, JsonValue.Null, ConversationOwned);
                return (appended.Id, task);
            }, failing);
            // The entry event, then the active-task metric; the report's own event throws too and is dropped.
            Check(infos.Snapshot().SequenceEqual([ReportInfo.HostError(HostCallback.Telemetry), ReportInfo.HostError(HostCallback.Telemetry)]),
                $"infos: {string.Join(", ", infos.Snapshot())}");
            Equal(1, hosts.Count, "host listener deliveries");
            await Eventually(() => shown.Count > 0);
            Check(shown.Snapshot().SequenceEqual<EntryId?>([entry]), "view shows the entry");
            Equal(DurableTaskStatus.Pending, (await harness.GetTaskAsync(created, Context))?.State.Status, "task status");
            await view.StopAsync();
            await harness.CloseAsync(Context);
            Equal(SessionEnd.Closed, await harness.Closed, "closed");
        });

        yield return Case("harness.telemetry.drops-a-report-s-own-metric-that-throws-after-onreport-got-the-report", async () =>
        {
            var telemetry = new MetricsOnly(new ThrowingMetrics(counter: true, upDownCounter: false));
            var opened = await OpenTasks(new MemoryStorage(), [], telemetry: telemetry);
            var (harness, _, reports) = opened;
            var root = await harness.RootAsync(Context);
            var bug = new InvalidOperationException("listener bug");
            harness.SubscribeCommits((_, _) => throw bug);
            await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = JsonObjectValue.Empty }), Context);
            // The listener runs in the commit's publication; a throw escaping its report would fail the commit there.
            await Flush();
            Check(reports.Snapshot().SequenceEqual<Exception>([bug]), "reports");
            Check(opened.Infos.Snapshot().SequenceEqual([ReportInfo.ListenerError]), "infos");
            await harness.CloseAsync(Context);
            Equal(SessionEnd.Closed, await harness.Closed, "closed");
        });
    }

    private static string WireStatus(TaskOutcomeStatus status) => status switch
    {
        TaskOutcomeStatus.Completed => "completed",
        TaskOutcomeStatus.Failed => "failed",
        TaskOutcomeStatus.Aborted => "aborted",
        TaskOutcomeStatus.Orphaned => "orphaned",
        TaskOutcomeStatus.Faulted => "faulted",
        _ => status.ToString(),
    };
}
