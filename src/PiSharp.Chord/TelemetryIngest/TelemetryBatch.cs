// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/telemetry-ingest/recorder.ts.
using System.Text.Json.Serialization;
using PiSharp.Telemetry;

namespace PiSharp.Chord.TelemetryIngest;

/// <summary>A detached batch of settled spans and ten-second metric aggregates.</summary>
public sealed record TelemetryBatch(
    [property: JsonPropertyName("spans")] IReadOnlyList<StoredSpan> Spans,
    [property: JsonPropertyName("metrics")] IReadOnlyList<StoredMetric> Metrics);

/// <summary>Queues telemetry without performing storage I/O; returns the number of spans accepted.</summary>
public interface ITelemetryIngestTarget
{
    int Ingest(TelemetryBatch batch);
}

public sealed record TelemetryReceipt(
    [property: JsonPropertyName("written")] int Written,
    [property: JsonPropertyName("dropped")] int Dropped);
