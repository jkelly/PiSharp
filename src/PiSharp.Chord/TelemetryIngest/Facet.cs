// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/telemetry-ingest/facet.ts.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Host;
using PiSharp.Telemetry;

namespace PiSharp.Chord.TelemetryIngest;

public sealed record TelemetryIngestOptions
{
    public required ITelemetryIngestTarget Target { get; init; }
    public required Func<JsonObject?, string?> WorkerHost { get; init; }
    public required Func<JsonObject?, SpanAttributes> PrincipalAttributes { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public static partial class TelemetryIngest
{
    /// <summary>Serves page and authorized-worker ingestion without creating telemetry about ingestion.</summary>
    public static Facet CreateFacet(TelemetryIngestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Target);
        ArgumentNullException.ThrowIfNull(options.WorkerHost);
        ArgumentNullException.ThrowIfNull(options.PrincipalAttributes);
        return Facet.Define((host, _) =>
        {
            host.Serve(PageTelemetry, (_, principal, _) =>
            {
                var budgets = new PageBudgets();
                return new(new Dictionary<string, object?>
                {
                    ["ingest"] = (HostMethod)((value, ctx) =>
                    {
                        var batch = ReadBatch(value, false);
                        var accepted = new List<StoredSpan>();
                        var prefix = $"browser:{ctx.Incarnation}:";
                        var budget = budgets.Of(ctx.Incarnation, options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                        foreach (var span in batch.Spans)
                        {
                            if (budget.Spans >= PageSpansPerMinute) break;
                            if (!span.Id.StartsWith(prefix, StringComparison.Ordinal) || !span.TraceId.StartsWith(prefix, StringComparison.Ordinal) ||
                                (span.ParentId is not null && !span.ParentId.StartsWith(prefix, StringComparison.Ordinal)) || span.EndedMs < span.StartedMs) continue;
                            budget.Spans++;
                            var attributes = new SpanAttributes { ["chord.host"] = "browser" };
                            foreach (var pair in span.Attributes) if (!pair.Key.StartsWith("chord.", StringComparison.Ordinal)) attributes[pair.Key] = pair.Value;
                            foreach (var pair in options.PrincipalAttributes(principal)) attributes[pair.Key] = pair.Value;
                            accepted.Add(span with { Attributes = attributes });
                        }
                        var written = options.Target.Ingest(new(accepted, []));
                        return Task.FromResult<object?>(ReceiptNode(written, batch.Spans.Count));
                    })
                }, "main");
            }, new(Untraced: true));
            host.Serve(WorkerTelemetry, (_, principal, _) =>
            {
                var worker = options.WorkerHost(principal);
                if (worker is null) return new(new Dictionary<string, object?>(), "other");
                return new(new Dictionary<string, object?>
                {
                    ["ingest"] = (HostMethod)((value, _) =>
                    {
                        var batch = ReadBatch(value, true);
                        var stamped = batch.Spans.Select(span =>
                        {
                            var attrs = new SpanAttributes(span.Attributes) { ["chord.host"] = worker };
                            return span with { Attributes = attrs };
                        }).ToArray();
                        var written = options.Target.Ingest(new(stamped, batch.Metrics));
                        return Task.FromResult<object?>(ReceiptNode(written, batch.Spans.Count));
                    })
                }, "worker");
            }, new(Untraced: true));
        });
    }
    private static JsonObject ReceiptNode(int written, int total) => new() { ["written"] = written, ["dropped"] = total - written };
    private static TelemetryBatch ReadBatch(object? value, bool worker)
    {
        var node = value as JsonObject ?? throw new ArgumentException("Expected telemetry batch", nameof(value));
        return new(node["spans"]!.Deserialize<StoredSpan[]>()!, worker ? node["metrics"]!.Deserialize<StoredMetric[]>()! : []);
    }
    internal sealed class PageBudgets
    {
        private readonly OrderedDictionary<string, PageBudget> values = new(StringComparer.Ordinal);
        public PageBudget Of(string incarnation, long now)
        {
            if (!values.TryGetValue(incarnation, out var budget) || now - budget.Start >= 60_000)
            {
                values.Remove(incarnation);
                if (values.Count >= 1000) values.RemoveAt(0);
                budget = new(now); values[incarnation] = budget;
            }
            return budget;
        }
    }
    internal sealed class PageBudget(long start) { public long Start { get; } = start; public int Spans; }
}
