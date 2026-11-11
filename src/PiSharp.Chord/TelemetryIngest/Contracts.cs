// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/telemetry-ingest/batch.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using static PiSharp.Chord.Contracts.Schemas;

namespace PiSharp.Chord.TelemetryIngest;

/// <summary>The bounded wire schemas and server-hosted telemetry contracts.</summary>
public static partial class TelemetryIngest
{
    public const int PageBatchSpans = 100;
    public const int WorkerBatchRows = 1000;
    public const int PageSpansPerMinute = 1200;
    private static readonly HostKind Server = new("server");
    private static readonly JsonObject PageSchema = Obj(("spans", Arr(SpanSchema(128, 128, 32,
        Union(Text(256), Num(), Bool()), 16, 8), max: PageBatchSpans)));
    private static readonly JsonObject WorkerSchema = Obj(("spans", Arr(SpanSchema(256, 1024, 256,
        Union(Str(max: 65_536), Num(), Bool(), Arr(Union(Str(), Num()))), 1024, 256), max: WorkerBatchRows)),
        ("metrics", Arr(Obj(("intervalMs", Int(min: 0)), ("name", Text(256, 1)),
            ("kind", Union(Lit("counter"), Lit("upDownCounter"), Lit("histogram"))), ("unit", Opt(Text(64))),
            ("attributes", Attributes(64, Union(Str(max: 4096), Num(), Bool()))),
            ("count", Int(min: 0)), ("sum", Num()), ("min", Num()), ("max", Num())), max: WorkerBatchRows)));
    private static readonly JsonObject Receipt = Obj(("written", Int()), ("dropped", Int()));
    public static JsonObject PageBatch => (JsonObject)PageSchema.DeepClone();
    public static JsonObject WorkerBatch => (JsonObject)WorkerSchema.DeepClone();
    public static Contract PageTelemetry { get; } = Contract.Remote("chord.telemetry.page", new()
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(),
        Variants = new() { ["main"] = new() { Methods = new() { ["ingest"] = Method(PageSchema, Receipt) } } }
    });
    public static Contract WorkerTelemetry { get; } = Contract.Remote("chord.telemetry.worker", new()
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(),
        Variants = new() { ["worker"] = new() { Methods = new() { ["ingest"] = Method(WorkerSchema, Receipt) } }, ["other"] = new() }
    });
    private static JsonObject Id(int max) => Str(min: 1, max: max, pattern: "^[\\x21-\\x7e]+$");
    private static JsonObject Text(int max, int min = 0) => Str(min: min, max: max, pattern: "^[^\\u0000-\\u001f\\u007f-\\u009f]*$");
    private static JsonObject Attributes(int count, JsonNode value) => new()
    {
        ["type"] = "object", ["maxProperties"] = count,
        ["patternProperties"] = new JsonObject { ["^[^\\u0000-\\u001f\\u007f-\\u009f]{1,64}$"] = value.DeepClone() },
        ["additionalProperties"] = false,
    };
    private static JsonObject SpanSchema(int id, int name, int attributes, JsonNode value, int events, int links)
    {
        var record = Attributes(attributes, value);
        return Obj(("id", Id(id)), ("traceId", Id(id)), ("parentId", Opt(Id(id))), ("name", Text(name, 1)),
            ("startedMs", Num(min: 0)), ("endedMs", Num(min: 0)), ("attributes", record),
            ("events", Arr(Obj(("name", Text(name, 1)), ("timeMs", Num(min: 0)), ("attributes", record)), max: events)),
            ("links", Arr(Obj(("traceId", Id(id)), ("spanId", Id(id)), ("attributes", Opt(record))), max: links)),
            ("status", Union(Obj(("status", Lit("ok"))), Obj(("status", Lit("error")),
                ("error", Opt(Obj(("name", Text(64)), ("message", Text(500)))))))));
    }
}
