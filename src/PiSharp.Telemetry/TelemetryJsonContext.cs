using System.Text.Json.Serialization;

namespace PiSharp.Telemetry;

/// <summary>Reflection-free JSON metadata for the telemetry package's public data forms.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AttributeValue))]
[JsonSerializable(typeof(SpanAttributes))]
[JsonSerializable(typeof(SpanStatus))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(RecordedTelemetrySpan[]))]
[JsonSerializable(typeof(RecordedTelemetryMeasurement[]))]
[JsonSerializable(typeof(TelemetrySchemaDefinition))]
public partial class TelemetryJsonContext : JsonSerializerContext { }
