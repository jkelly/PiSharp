using PiSharp.Telemetry;
using PiSharp.Telemetry.Testing;

// packages/telemetry/test/conformance.test.ts at Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): the adapter conformance kit
// (PiSharp.Telemetry.Testing) against the in-memory adapter, and the snapshot case. PiSharp also runs the kit's capability group
// against the no-op adapter, which records nothing for the other groups to read.
internal static partial class Program
{
    private static Task<TelemetryAdapterFixture> MemoryFixture()
    {
        var context = new InMemoryTelemetryContext();
        return Task.FromResult(new TelemetryAdapterFixture
        {
            Context = context,
            Propagation = context,
            Metrics = context,
            GetMetrics = () => Task.FromResult(context.GetMetrics()),
            GetSpans = () => Task.FromResult(context.GetSpans())
        });
    }

    private static Task<TelemetryAdapterFixture> NoopFixture() => Task.FromResult(new TelemetryAdapterFixture
    {
        Context = NoopTelemetry.Context,
        GetSpans = () => Task.FromResult<IReadOnlyList<RecordedTelemetrySpan>>([])
    });

    private static IEnumerable<(string Id, Func<Task> Run)> ConformanceCases =>
    [
        .. TelemetryAdapterConformance.Create(MemoryFixture)
            .Select(testCase => ($"telemetry.conformance.memory: {testCase.Group}: {testCase.Name}", (Func<Task>)testCase.RunAsync)),
        .. TelemetryAdapterConformance.Create(NoopFixture).Where(testCase => testCase.Group == "capabilities")
            .Select(testCase => ($"telemetry.conformance.noop: {testCase.Group}: {testCase.Name}", (Func<Task>)testCase.RunAsync)),
        ("telemetry.conformance.memory: returns detached snapshots without exposing mutable recording state", async () =>
        {
            var context = new InMemoryTelemetryContext();
            bool? openSettled = null;
            int? openEndSequence = -1;
            await context.StartSpanAsync(new SpanOptions("snapshot") { Attributes = new() { ["tags"] = new[] { "initial" } } }, span =>
            {
                span.AddEvent("event", new SpanAttributes { ["value"] = 1 });
                var open = context.GetSpans()[0];
                openSettled = open.Settled;
                openEndSequence = open.EndSequence;
            });
            Equal(false, openSettled, "open span is unsettled");
            Check(openEndSequence is null, "open span has no end sequence");
            var first = context.GetSpans()[0];
            Check(first.Settled, "settled");
            Equal<int?>(1, first.EndSequence, "end sequence");
            first.Attributes["tags"] = new[] { "mutated" };
            first.Events[0].Attributes["value"] = 2;
            var second = context.GetSpans()[0];
            Json(new { tags = new[] { "initial" } }, second.Attributes, "fresh attributes");
            Json(new[] { new { name = "event", attributes = new { value = 1 } } }, second.Events, "fresh events");
        }),
    ];
}
