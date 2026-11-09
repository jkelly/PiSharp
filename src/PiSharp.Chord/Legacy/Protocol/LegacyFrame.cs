// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/wire.ts, link.ts.
#pragma warning disable PISHARP_SCHEMA001 // The legacy protocol deliberately uses the existing experimental schema foundation.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Contracts.Schema;
using static PiSharp.Chord.Contracts.Schemas;

namespace PiSharp.Chord.Legacy;

/// <summary>A protocol 4 frame. Packed payloads stay opaque while a relay forwards them.</summary>
public sealed class LegacyFrame
{
    private readonly JsonObject envelope;
    public LegacyFrame(JsonObject envelope, LegacyPacked? packed = null)
    {
        this.envelope = (JsonObject)envelope.DeepClone();
        Packed = packed;
        if (packed is not null) this.envelope.Remove(packed.Field);
    }
    public string Type => ChordJson.AsString(envelope["t"]) ?? "";
    public JsonObject Json => (JsonObject)envelope.DeepClone();
    public LegacyPacked? Packed { get; }
    public JsonNode? this[string key] => envelope[key]?.DeepClone();
    public bool Has(string key) => envelope.ContainsKey(key) || Packed?.Field == key;
    public string? String(string key) => ChordJson.AsString(envelope[key]);
    public long Number(string key) => checked((long)(ChordJson.AsNumber(envelope[key]) ?? 0));
    public bool Is(string key, bool value) => ChordJson.IsBoolean(envelope[key], value);
    public LegacyFrame With(string key, JsonNode? value)
    {
        var copy = Json;
        copy[key] = value?.DeepClone();
        return new(copy, Packed?.Field == key ? null : Packed);
    }
    public LegacyFrame Without(string key)
    {
        var copy = Json; copy.Remove(key);
        return new(copy, Packed?.Field == key ? null : Packed);
    }
    public JsonNode? UnpackPayload(int? maximumBytes = null) => Packed is null
        ? envelope[LegacyCodec.PayloadField(this) ?? "value"]?.DeepClone()
        : LegacyCodec.Unpack(Packed, maximumBytes);
    internal JsonObject ValidationJson()
    {
        var value = Json;
        if (Packed is not null) value[Packed.Field] = null;
        return value;
    }
}

/// <summary>Raw DEFLATE of one JSON args/value field; no inflation is required to relay it.</summary>
public sealed class LegacyPacked
{
    private readonly byte[] bytes;
    public LegacyPacked(string field, ReadOnlySpan<byte> bytes)
    {
        if (field is not ("args" or "value")) throw new ArgumentException("bad packed payload", nameof(field));
        Field = field;
        this.bytes = bytes.ToArray();
    }
    public string Field { get; }
    public ReadOnlyMemory<byte> Bytes => bytes;
}

/// <summary>The independent v1 wire schema. It deliberately does not accept v2/next frames.</summary>
public static class LegacyProtocol
{
    public const int Version = 4;
    public const string SourceCommit = "f9a26365568127461c47a07a37dcb42c140a3ac5";
    public static IReadOnlyList<string> ErrorCodes { get; } = Array.AsReadOnly(new[]
    {
        "aborted", "contract_mismatch", "forbidden", "gone", "invalid", "not_in_variant", "not_ready", "released",
        "superseded", "unavailable", "upgrade_conflict", "upgrade_required",
    });
    private static readonly JsonObject description = MakeDescription();
    private static readonly JsonObject schema = MakeSchema();
    private static readonly JsonSchemaValidator validator = JsonSchemaValidator.Compile(schema);
    public static JsonObject FrameSchema => (JsonObject)schema.DeepClone();
    public static JsonObject DescriptionSchema => (JsonObject)description.DeepClone();
    public static bool IsFrame(LegacyFrame frame) => validator.Check(frame.ValidationJson());
    public static bool IsFrame(JsonNode? frame) => validator.Check(frame);
    public static JsonObject ToWireError(Exception error) => LegacyPeerSession.Error(error);
    public static ChordException FromWireError(JsonObject error) => LegacyPeerSession.FromError(error);
    public static LegacyFrame Frame(string type, params (string Name, JsonNode? Value)[] values)
    {
        var json = new JsonObject { ["t"] = type };
        foreach (var (name, value) in values) json[name] = value?.DeepClone();
        return new(json);
    }

    private static JsonObject Open(params (string Name, Property Field)[] fields)
    {
        var value = Obj(fields);
        value["additionalProperties"] = true;
        return value;
    }
    private static JsonObject MakeDescription()
    {
        var obj = Open();
        var member = Union(Open(("kind", Lit("method")), ("args", Opt(obj)), ("result", Opt(obj))),
            Open(("kind", Lit("state")), ("schema", obj)), Open(("kind", Lit("opaque")), ("id", Str())));
        var placement = Union(Obj(("name", Str(min: 1))), Obj(("prefix", Str(min: 1)), ("key", Str(min: 1))));
        return Open(("name", Str(min: 1)), ("scope", OneOf("remote", "local")), ("version", Int()),
            ("input", obj), ("host", Opt(placement)), ("variants", Map(Map(member))));
    }
    private static JsonObject MakeSchema()
    {
        var id = Int(min: 1); var name = Str(min: 1);
        var error = Open(("code", OneOf(ErrorCodes.Select(s => (Literal)s).ToArray())), ("message", Str()), ("reason", Opt(Str())));
        var root = Open(("rootId", name), ("codeVersion", id));
        var options = Open(("remove", Opt(Arr(Str()))), ("gateDeadline", Opt(Num())), ("retireDeadline", Opt(Num())));
        var report = Open(("codeVersion", Int()), ("previous", Int()), ("changed", Arr(Str())), ("removed", Arr(Str())),
            ("prepared", Arr(Str())), ("gated", Arr(Str())));
        JsonObject F(string t, params (string Name, Property Field)[] p) => Open(new[] { ("t", (Property)Lit(t)) }.Concat(p).ToArray());
        return Union(
            F("hello", ("protocol", Opt(id)), ("name", name), ("kind", OneOf("host", "client")), ("codeVersion", Int(min: 0)), ("acks", Opt(Bool()))),
            F("ack", ("n", id)),
            F("hold", ("id", id), ("at", name), ("token", description), ("input", Unknown()), ("principal", Opt(Unknown())),
                ("origin", Opt(name)), ("state", Opt(Lit(false))), ("again", Opt(Lit(true)))),
            F("release", ("id", id)),
            F("shape", ("id", id), ("variant", Opt(Str())), ("members", Arr(Str())), ("contract", Str()), ("state", Arr(name))),
            F("gone", ("id", id), ("error", error)),
            F("call", ("id", id), ("hold", id), ("member", name), ("args", Opt(Unknown())), ("root", Opt(root))),
            F("result", ("id", id), ("value", Opt(Unknown())), ("error", Opt(error))),
            F("cancel", ("id", id)), F("sub", ("hold", id)), F("unsub", ("hold", id)), F("resync", ("hold", id), ("m", name)),
            F("state", ("hold", id), ("m", name), ("as", OneOf("snap", "reset", "ops", "error")), ("value", Opt(Unknown())),
                ("ops", Opt(Arr(Arr(Unknown())))), ("error", Opt(error))),
            F("ping", ("n", Int())), F("pong", ("n", Int())), F("bye", ("reason", Opt(Str()))),
            F("root_end", ("rootId", name)), F("invalidate", ("pattern", Map(Unknown()))),
            F("prepare", ("reload", id), ("codeVersion", id), ("plugins", Arr(Str())), ("options", options)),
            F("vote", ("reload", id), ("error", Opt(error)), ("report", Opt(report))),
            F("commit", ("reload", id)), F("retire", ("codeVersion", id)), F("abort", ("reload", id)));
    }
}
