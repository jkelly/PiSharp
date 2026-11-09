// Authored from Pi v1.5.0 chord/test/telemetry-sqlite{,-store}.test.ts. Native SQLite, no upstream execution capture.
using System.Globalization;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.TelemetrySqlite;
using PiSharp.Telemetry;
using PiSharp.Telemetry.Testing;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> TelemetrySqliteCases()
    {
        foreach (var test in TelemetryAdapterConformance.Create(TelemetrySqliteFixture).Where(test => test.Group != "metrics"))
            yield return Case("telemetry-sqlite.conformance." + test.Group + "." + test.Name, test.RunAsync);

        yield return Case("telemetry-sqlite.buffering-settlement-clock", async () =>
        {
            var db = new TestSqliteDatabase(":memory:");
            var clock = new TelemetryClock();
            await using var recorder = await SqliteTelemetry.CreateAsync(db, timeProvider: clock);
            var writes = 0;
            db.OnTransactionStatement = _ => writes++;
            var release = new TaskCompletionSource<int>();
            ITelemetrySpan? active = null;
            var pending = recorder.StartSpanAsync("active", span => { active = span; span.AddEvent("begin"); return release.Task; });
            Equal(0, writes, "recording performs no SQL");
            await recorder.FlushAsync();
            Equal(0, (await recorder.FindTracesAsync()).Traces.Count, "active spans omitted");
            clock.Jump(125);
            release.SetResult(42);
            Equal(42, await pending, "callback result unchanged");
            active!.AddEvent("late");
            await active.StartSpanAsync("late-child", _ => 1);
            await recorder.FlushAsync();
            var span = (await recorder.FindTracesAsync()).Traces.Single().Spans.Single();
            Equal(125d, span.EndedMs - span.StartedMs, "elapsed clock");
            Equal("begin", span.Events.Single().Name, "late event ignored");
            await recorder.CloseAsync();
            Check(await db.GetAsync("SELECT 1 AS value") is not null, "close leaves database open");
            await db.CloseAsync();
        });

        yield return Case("telemetry-sqlite.metric-intervals-series-and-reopen", async () =>
        {
            var db = new TestSqliteDatabase(":memory:"); var clock = new TelemetryClock(); clock.Jump(20_001);
            var recorder = await SqliteTelemetry.CreateAsync(db, timeProvider: clock);
            var values = new[] { "original" };
            var histogram = recorder.Histogram("duration", new() { Unit = "ms", Boundaries = [1, 10] });
            histogram.Record(2, new() { ["b"] = values, ["a"] = true }); values[0] = "changed";
            histogram.Record(8, new() { ["a"] = true, ["b"] = new[] { "original" } });
            recorder.UpDownCounter("active").Add(-1);
            await recorder.FlushAsync();
            Equal(0, (await recorder.FindMetricsAsync(new("duration"))).Count, "partial interval omitted");
            clock.Jump(10_000);
            histogram.Record(4, new() { ["b"] = new[] { "original" }, ["a"] = true });
            await recorder.FlushAsync();
            var metric = (await recorder.FindMetricsAsync(new("duration"))).Single();
            Equal(20_000d, metric.IntervalMs, "ten-second interval"); Equal(2d, metric.Count, "canonical series merged");
            Equal(10d, metric.Sum, "sum"); Equal(2d, metric.Min, "min"); Equal(8d, metric.Max, "max");
            Equal("{\"a\":true,\"b\":[\"original\"]}", metric.Attributes.ToString(), "attributes copied and sorted");
            Equal(-1d, (await recorder.FindMetricsAsync(new("active"))).Single().Sum, "up-down increments");
            await recorder.CloseAsync();
            var reopened = await SqliteTelemetry.CreateAsync(db, timeProvider: clock);
            reopened.Histogram("duration", new() { Unit = "ms", Boundaries = [999] }).Record(6, new() { ["a"] = true, ["b"] = new[] { "original" } });
            await reopened.CloseAsync();
            var rows = await reopened.FindMetricsAsync(new("duration"));
            Equal(2, rows.Count, "reopen retains intervals"); Equal(10d, rows[1].Sum, "partial aggregate merges across recorders");
            Equal(2d, rows[1].Count, "boundaries not part of series identity"); await db.CloseAsync();
        });

        yield return Case("telemetry-sqlite.capacity-in-flight-and-concurrent-close", async () =>
        {
            var db = new TestSqliteDatabase(":memory:"); var clock = new TelemetryClock();
            var recorder = await SqliteTelemetry.CreateAsync(db, new() { MaxQueuedSpans = 1 }, clock);
            await recorder.StartSpanAsync("first", _ => 1); recorder.Counter("first").Add(1);
            recorder.Counter("first").Add(2); recorder.Counter("overflow").Add(1);
            clock.Jump(10_000);
            var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
            db.AfterTransaction = () => { entered.TrySetResult(); return release.Task; };
            var flush = recorder.FlushAsync(); await entered.Task;
            await recorder.StartSpanAsync("overflow", _ => 2); recorder.Counter("first").Add(3);
            Equal(1L, recorder.DroppedSpans, "in-flight spans consume capacity");
            Equal(2L, recorder.DroppedMeasurements, "new interval and excess series consume capacity");
            var second = recorder.FlushAsync(); var close = recorder.CloseAsync();
            Check(ReferenceEquals(close, recorder.CloseAsync()), "close is idempotent");
            release.SetResult(); await Task.WhenAll(flush, second, close);
            Equal("first", (await recorder.FindTracesAsync()).Traces.Single().Spans.Single().Name, "overflow not persisted");
            Equal(3d, (await recorder.FindMetricsAsync(new("first"))).Single().Sum, "existing queued series accepts measurements");
            await db.CloseAsync();
        });

        yield return Case("telemetry-sqlite.failure-is-permanent-and-atomic", async () =>
        {
            var db = new TestSqliteDatabase(":memory:"); var clock = new TelemetryClock();
            var recorder = await SqliteTelemetry.CreateAsync(db, timeProvider: clock);
            await recorder.StartSpanAsync("rollback", _ => 1); recorder.Counter("rollback").Add(1);
            await db.ExecAsync("DROP TABLE telemetry_metrics");
            var failure = await TelemetryFailure(recorder.CloseAsync());
            Check(ReferenceEquals(failure, await TelemetryFailure(recorder.FlushAsync())), "flush reports identical failure");
            Check(ReferenceEquals(failure, await TelemetryFailure(recorder.CloseAsync())), "close reports identical failure");
            Equal(0, (await db.AllAsync("SELECT * FROM telemetry_spans")).Count, "transaction rolls back span with metric failure");
            Equal(7, await recorder.StartSpanAsync("ignored", _ => 7), "inert callback still runs");
            Equal(0, clock.TimerCount, "failed recorder timer stopped"); await db.CloseAsync();
        });

        yield return Case("telemetry-sqlite.timer-retention-close-active", async () =>
        {
            var db = new TestSqliteDatabase(":memory:"); var clock = new TelemetryClock(); clock.Jump(20_001);
            var recorder = await SqliteTelemetry.CreateAsync(db, new() { RetentionMs = 20_000 }, clock);
            Equal(TimeSpan.FromSeconds(1), clock.Period, "one-second automatic flush");
            await recorder.StartSpanAsync("old", _ => 1); recorder.Counter("old").Add(1);
            clock.Fire(); await recorder.FlushAsync();
            Equal(1, (await recorder.FindTracesAsync()).Traces.Count, "timer persisted settled span");
            clock.Jump(30_000); await recorder.StartSpanAsync("recent", _ => 2); recorder.Counter("recent").Add(1);
            clock.Fire(); await recorder.FlushAsync();
            Equal("recent", (await recorder.FindTracesAsync()).Traces.Single().Spans.Single().Name, "retention pruned old span");
            Equal(0, (await recorder.FindMetricsAsync(new("old"))).Count, "retention pruned metric");
            var release = new TaskCompletionSource<int>(); var active = recorder.StartSpanAsync("late", _ => release.Task);
            await recorder.CloseAsync(); release.SetResult(3); Equal(3, await active, "close does not wait for active callback");
            Equal(1, (await recorder.FindTracesAsync()).Traces.Count, "late completion discarded");
            Equal(1, (await recorder.FindMetricsAsync(new("recent"))).Count, "close flushes partial interval"); await db.CloseAsync();
        });

        yield return Case("telemetry-sqlite.query-types-pagination-bindings", async () =>
        {
            var db = new TestSqliteDatabase(":memory:"); var clock = new TelemetryClock();
            await using var recorder = await SqliteTelemetry.CreateAsync(db, timeProvider: clock);
            const string unusual = "quote'\".$[x]";
            await recorder.StartSpanAsync(new("head") { Attributes = new() { [unusual] = true } }, async span =>
            {
                await span.StartSpanAsync(new("child") { Attributes = new() { ["other"] = 1d } }, _ => 0);
                span.SetStatus(SpanStatus.Failed("Expected", "test"));
            });
            clock.Jump(1);
            await recorder.StartSpanAsync(new("second") { Attributes = new() { [unusual] = 1d } }, _ => 0);
            await recorder.FlushAsync();
            Equal(1, (await recorder.FindTracesAsync(new() { Attributes = new() { [unusual] = true } })).Traces.Count, "booleans distinct from numbers");
            Equal(0, (await recorder.FindTracesAsync(new() { Attributes = new() { [unusual] = true, ["other"] = 1d } })).Traces.Count, "all attributes on one span");
            Equal(1, (await recorder.FindTracesAsync(new() { Name = "head", Errors = true })).Traces.Count, "head name plus error anywhere");
            Equal(0, (await recorder.FindTracesAsync(new() { Name = "child" })).Traces.Count, "child name not head name");
            var first = await recorder.FindTracesAsync(new() { Limit = 1 });
            Equal("second", first.Traces.Single().Spans.Single().Name, "descending start order");
            var next = await recorder.FindTracesAsync(new() { Limit = 1, Cursor = first.Cursor });
            Equal("head", next.Traces.Single().Spans[0].Name, "cursor advances"); Check(next.Cursor is null, "last page has no cursor");
            Check(await recorder.GetTraceAsync("missing' OR 1=1 --") is null, "identifier bound, not interpolated");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Check(await TelemetryFailure(recorder.FindTracesAsync(cancellationToken: cancellation.Token)) is OperationCanceledException, "cancellation honored");
            await recorder.CloseAsync(); await db.CloseAsync();
        });

        yield return Case("telemetry-sqlite.tied-pages-two-queries-and-surviving-child", async () =>
        {
            var db = new TestSqliteDatabase(":memory:"); var clock = new TelemetryClock();
            await using var recorder = await SqliteTelemetry.CreateAsync(db, timeProvider: clock);
            for (var index = 0; index < 25; index++)
            {
                await recorder.StartSpanAsync("root", span =>
                {
                    // Tie starts across traces, not within a trace: upstream breaks intra-trace ties by string ID,
                    // which can place child :10 before parent :9. Its fixture also gives children a later wall time.
                    clock.Jump(1);
                    return span.StartSpanAsync("child", _ => 1);
                });
                clock.Jump(-1);
            }
            Equal(0, (await recorder.FindTracesAsync()).Traces.Count, "reader never implicitly flushes");
            await recorder.FlushAsync();
            var queries = 0;
            db.OnTransactionStatement = sql => { if (sql.StartsWith("WITH heads", StringComparison.Ordinal) || sql.StartsWith("SELECT * FROM telemetry_spans", StringComparison.Ordinal)) queries++; };
            var first = await recorder.FindTracesAsync(); Equal(2, queries, "page uses two span queries");
            Equal(20, first.Traces.Count, "default limit");
            var rest = await recorder.FindTracesAsync(new() { Cursor = first.Cursor }); Equal(5, rest.Traces.Count, "tied remainder");
            Check(rest.Cursor is null, "no further page");
            var expected = (await recorder.FindTracesAsync(new() { Limit = 100 })).Traces;
            Sequence(expected.Select(t => t.TraceId), first.Traces.Concat(rest.Traces).Select(t => t.TraceId), "tied pages no duplicate or omission");
            Check(expected.All(t => t.Spans.Count == 2), "every page contains whole traces");
            Equal(0, (await recorder.FindTracesAsync(new() { Name = "child" })).Traces.Count, "children are later than their trace heads");
            var original = expected[0]; var parent = original.Spans.Single(s => s.Name == "root");
            await db.RunAsync("DELETE FROM telemetry_spans WHERE id = ?", parent.Id);
            var survivor = (await recorder.FindTracesAsync(new() { Name = "child" })).Traces.Single();
            Equal(original.TraceId, survivor.TraceId, "removed root retains original trace identity");
            Equal(parent.Id, survivor.Spans.Single().ParentId, "surviving child retains parent identity");
            await recorder.CloseAsync(); await db.CloseAsync();
        });

        yield return Case("telemetry-sqlite.metric-query-scalar-types-and-detached-snapshots", async () =>
        {
            var db = new TestSqliteDatabase(":memory:"); var clock = new TelemetryClock(); clock.Jump(20_001);
            await using var recorder = await SqliteTelemetry.CreateAsync(db, timeProvider: clock);
            var counter = recorder.Counter("hits", new() { Unit = "By" });
            counter.Add(2, new() { ["method"] = "read", ["valid"] = true });
            counter.Add(3, new() { ["valid"] = true, ["method"] = "read" });
            counter.Add(11, new() { ["method"] = "write", ["valid"] = 1d });
            recorder.Counter("other").Add(100);
            clock.Jump(10_000); counter.Add(7, new() { ["method"] = "read", ["valid"] = true });
            clock.Jump(10_000); await recorder.FlushAsync();
            var query = new MetricQuery("hits") { StartMin = 20_000, StartMax = 20_000, Attributes = new() { ["valid"] = true, ["method"] = "read" } };
            var metric = (await recorder.FindMetricsAsync(query)).Single();
            Equal(2d, metric.Count, "typed bounded query count"); Equal(5d, metric.Sum, "typed bounded query sum");
            metric.Attributes["method"] = "changed";
            Equal("read", (await recorder.FindMetricsAsync(query)).Single().Attributes["method"]!.GetString(), "stored metric detached from result mutation");
            Equal(11d, (await recorder.FindMetricsAsync(new("hits") { Attributes = new() { ["valid"] = 1d } })).Single().Sum, "boolean not numeric");
            Equal(7d, (await recorder.FindMetricsAsync(new("hits") { StartMin = 30_000 })).Single().Sum, "inclusive interval bound");
            Equal(0, (await recorder.FindMetricsAsync(new("missing"))).Count, "missing metric");
            await recorder.CloseAsync(); await db.CloseAsync();
        });
    }

    private static async Task<Exception> TelemetryFailure(Task task)
    {
        try { await task; } catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected a failure");
    }

    private static async Task<TelemetryAdapterFixture> TelemetrySqliteFixture()
    {
        var db = new TestSqliteDatabase(":memory:");
        var recorder = await SqliteTelemetry.CreateAsync(db);
        return new()
        {
            Context = recorder, Propagation = recorder,
            GetSpans = async () =>
            {
                await recorder.FlushAsync();
                var order = await db.AllAsync("SELECT rowid, id FROM telemetry_spans");
                var sequence = order.ToDictionary(row => (string)row["id"]!, row => Convert.ToInt32(row["rowid"], CultureInfo.InvariantCulture));
                var traces = await recorder.FindTracesAsync(new() { Limit = 10_000 });
                return traces.Traces.SelectMany(trace => trace.Spans).Select(span =>
                {
                    static IReadOnlyDictionary<string, string> Carrier(string id, string trace) => new Dictionary<string, string>
                    { ["pi-sqlite-span"] = id, ["pi-sqlite-trace"] = trace };
                    static int Id(string id) => int.Parse(id[(id.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
                    return new RecordedTelemetrySpan(Id(span.Id), span.ParentId is { } parent && parent.Split(':')[0] == span.Id.Split(':')[0] ? Id(parent) : null,
                        Carrier(span.Id, span.TraceId), span.ParentId is null ? null : Carrier(span.ParentId, span.TraceId),
                        span.Links.Select(link => new RecordedTelemetryLink(Carrier(link.SpanId, link.TraceId), link.Attributes ?? new())).ToArray(),
                        span.Name, span.Attributes, span.Events.Select(e => new RecordedTelemetryEvent(e.Name, e.Attributes)).ToArray(), span.Status, true, sequence[span.Id]);
                }).OrderBy(span => span.Id).ToArray();
            },
            Dispose = async () => { await recorder.CloseAsync(); await db.CloseAsync(); }
        };
    }

    private sealed class TelemetryClock : TimeProvider
    {
        private long _milliseconds;
        private ClockTimer? _timer;
        public int TimerCount => _timer is { Disposed: false } ? 1 : 0;
        public TimeSpan Period { get; private set; }
        public void Jump(long milliseconds) => _milliseconds += milliseconds;
        public void Fire() => _timer?.Fire();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_milliseconds);
        public override long GetTimestamp() => _milliseconds;
        public override long TimestampFrequency => 1000;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Period = period; return _timer = new(callback, state); }
        private sealed class ClockTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
