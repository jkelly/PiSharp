using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Telemetry;

internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> AotJsonCases =>
    [
        ("telemetry.json.explicit-metadata-and-converter-overrides", Sync(() =>
        {
            var context = TelemetryJsonContext.Default;
            var attributes = new SpanAttributes { ["text"] = "中\n", ["count"] = 3, ["items"] = new[] { true, false }, ["missing"] = null };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(attributes, context.SpanAttributes);
            var roundtrip = JsonSerializer.Deserialize(bytes, context.SpanAttributes)!;
            Check(attributes.Where(pair => pair.Value is not null).All(pair => pair.Value!.Equals(roundtrip[pair.Key])), "source metadata preserves attribute JSON forms");
            Check(!roundtrip.ContainsKey("missing"), "undefined attributes remain omitted");
            var status = SpanStatus.Failed("Expected", "failure");
            Equal(status, JsonSerializer.Deserialize(JsonSerializer.Serialize(status, context.SpanStatus), context.SpanStatus), "source metadata preserves status");
            foreach (var factory in new[] { false, true })
            {
                var options = new JsonSerializerOptions();
                var converterFactory = new AttributeOverrideFactory();
                options.Converters.Add(factory ? converterFactory : new AttributeOverride());
                var custom = new TelemetryJsonContext(options);
                var json = JsonSerializer.Serialize(new SpanAttributes { ["value"] = "input" }, custom.SpanAttributes);
                Equal("{\"value\":\"override:input\"}", json, "custom attribute converter write");
                var restored = JsonSerializer.Deserialize("{\"value\":\"read\"}", custom.SpanAttributes)!;
                Equal("read:read", restored["value"]!.GetString(), "custom attribute converter read");
                if (factory) Equal(1, converterFactory.Created, "converter factories are cached per read-only serializer options");
            }
        }))
    ];

    private sealed class AttributeOverride : JsonConverter<AttributeValue>
    {
        public override AttributeValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => "read:" + reader.GetString();
        public override void Write(Utf8JsonWriter writer, AttributeValue value, JsonSerializerOptions options) => writer.WriteStringValue("override:" + value.GetString());
    }
    private sealed class AttributeOverrideFactory : JsonConverterFactory
    {
        public int Created { get; private set; }
        public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(AttributeValue);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) { Created++; return new AttributeOverride(); }
    }
}
