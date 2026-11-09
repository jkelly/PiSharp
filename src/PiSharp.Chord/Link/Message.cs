// Port of Pi f9a26365568127461c47a07a37dcb42c140a3ac5 next/link/message.ts.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Link;

internal abstract record Message(string Kind) { public long? Origin { get; init; } }
internal sealed record HelloMessage(long Protocol, string Incarnation, long Calls) : Message("hello");
internal sealed record HoldMessage(long Hold, string Type, long Version, string Fingerprint, Part Params, WireAddress Address,
    JsonNode? Principal = null, string? Incarnation = null, IReadOnlyList<string>? Chain = null, long? Limit = null) : Message("hold");
internal sealed record ReleaseMessage(long Hold) : Message("release");
internal sealed record CallMessage(long Call, long Hold, long Method, Part? Args = null, IReadOnlyList<ReadOnlyMemory<byte>>? Segments = null) : Message("call");
internal sealed record AbortMessage(long Call) : Message("abort");
internal sealed record RetryMessage(long Hold, long Hops) : Message("retry");
internal sealed record BoundMessage(IReadOnlyList<BoundEntry> Holds) : Message("bound");
internal sealed record StateMessage(IReadOnlyList<StateEntry> Holds) : Message("state");
internal sealed record AckMessage() : Message("ack");
internal sealed record ClosedMessage() : Message("closed");
internal sealed record RevokeMessage(JsonNode? Revocation) : Message("revoke");
internal sealed record PingMessage() : Message("ping");
internal sealed record PongMessage() : Message("pong");
internal sealed record FailedMessage(long Hold, FailureCause Cause, long? RetryIn = null) : Message("failed");
internal sealed record LoadingMessage(long Hold) : Message("loading");
internal sealed record ResultMessage(long Call, Part? Value = null, IReadOnlyList<ReadOnlyMemory<byte>>? Segments = null) : Message("result");
internal sealed record ErrorMessage(long Call, WireError Error) : Message("error");
internal sealed record GoneMessage(long Hold, WireError Error) : Message("gone");
internal sealed record WireAddress(string Kind, JsonNode? Params);
internal sealed record BoundMember(long Member, Part Value);
internal sealed record StateMember(long Member, Part Ops);
internal sealed record FailedMember(long Member, WireError Error);
internal sealed record BoundEntry(long Hold, long Variant, IReadOnlyList<BoundMember> Members, IReadOnlyList<FailedMember> Failed);
internal sealed record StateEntry(long Hold, IReadOnlyList<StateMember> Members, IReadOnlyList<FailedMember> Failed, IReadOnlyList<long> Retired);
internal sealed record FailureCause(string Kind, WireError Error);

internal sealed record WireError(JsonObject Json)
{
    public static WireError FromException(Exception error) => new(error switch
    {
        ChordError chord => new JsonObject { ["kind"] = "chord", ["error"] = chord.ToJson() },
        PluginError plugin => Plugin(plugin.Code, plugin.Message, plugin.Details),
        _ => Plugin("internal", error.Message, null),
    });
    private static JsonObject Plugin(string code, string message, JsonNode? details)
    {
        var json = new JsonObject { ["kind"] = "plugin", ["code"] = code, ["message"] = message };
        if (details is not null) json["details"] = details.DeepClone();
        return json;
    }
    public Exception ToException()
    {
        Check();
        return ChordJson.AsString(Json["kind"]) == "chord" ? DataInternal.ChordErrorFromJson(Json["error"]) :
            new PluginError(ChordJson.AsString(Json["code"])!, ChordJson.AsString(Json["message"])!, Json["details"]);
    }
    internal void Check()
    {
        var kind = ChordJson.AsString(Json["kind"]);
        if (kind == "chord")
        {
            if (Json.Count != 2 || !Json.ContainsKey("error")) throw MessageCheck.Invalid("wire error");
            // The protocol envelope permits arbitrary Chord error data; its owner validates the error table.
            return;
        }
        if (kind != "plugin" || ChordJson.AsString(Json["code"]) is null || ChordJson.AsString(Json["message"]) is null ||
            Json.Any(p => p.Key is not ("kind" or "code" or "message" or "details"))) throw MessageCheck.Invalid("wire error");
    }
}

internal interface ICodec
{
    string Name { get; }
    byte[] Encode(Message message);
    Message Decode(ReadOnlyMemory<byte> bytes);
}

internal static class MessageCheck
{
    internal const long MaxSafeInteger = 9007199254740991;
    internal const int Chain = 32;
    internal const int RetryHops = 16;
    private static readonly Regex Uuid = new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant);
    private static readonly Regex Fingerprint = new("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);
    internal static ArgumentException Invalid(string detail) => new("message: fails the message schema (" + detail + ")");
    internal static void Number(long value, long min = 0, long max = MaxSafeInteger) { if (value < min || value > max) throw Invalid("integer"); }
    private static void Ids(IEnumerable<long> values) { foreach (var n in values) Number(n); }
    internal static Message Check(Message m)
    {
        if (m.Origin is { } origin)
        {
            Number(origin, 1);
            if (m is HelloMessage or RevokeMessage or PingMessage or PongMessage) throw Invalid("origin");
        }
        switch (m)
        {
            case HelloMessage x: Number(x.Protocol); Number(x.Calls, 1); if (!Uuid.IsMatch(x.Incarnation)) throw Invalid("incarnation"); break;
            case HoldMessage x:
                Number(x.Hold); Number(x.Version, 1); if (!Fingerprint.IsMatch(x.Fingerprint)) throw Invalid("fingerprint");
                if (x.Principal is not null && (x.Principal is not JsonObject p || ChordJson.AsString(p["kind"]) is null)) throw Invalid("principal");
                if (x.Incarnation is not null && !Uuid.IsMatch(x.Incarnation)) throw Invalid("incarnation");
                if (x.Chain is not null && (x.Chain.Count > Chain || x.Chain.Any(c => !Uuid.IsMatch(c)))) throw Invalid("chain");
                if (x.Limit is { } limit) Number(limit); break;
            case ReleaseMessage x: Number(x.Hold); break;
            case CallMessage x: Ids([x.Call, x.Hold, x.Method]); break;
            case AbortMessage x: Number(x.Call); break;
            case RetryMessage x: Number(x.Hold); Number(x.Hops, 1, RetryHops); break;
            case BoundMessage x:
                foreach (var e in x.Holds) { Ids([e.Hold,e.Variant]); foreach(var a in e.Members) Number(a.Member); Failures(e.Failed); } break;
            case StateMessage x:
                foreach (var e in x.Holds) { Number(e.Hold); foreach(var a in e.Members) Number(a.Member); Failures(e.Failed); Ids(e.Retired); } break;
            case ClosedMessage: if (m.Origin is null) throw Invalid("closed origin"); break;
            case FailedMessage x:
                Number(x.Hold); if (x.Cause.Kind is not ("transient" or "broken" or "waiting")) throw Invalid("cause");
                x.Cause.Error.Check(); if (x.RetryIn is { } retry) Number(retry); break;
            case LoadingMessage x: Number(x.Hold); break;
            case ResultMessage x: Number(x.Call); break;
            case ErrorMessage x: Number(x.Call); x.Error.Check(); break;
            case GoneMessage x: Number(x.Hold); x.Error.Check(); break;
        }
        return m;
    }
    private static void Failures(IEnumerable<FailedMember> failures) { foreach(var f in failures) { Number(f.Member); f.Error.Check(); } }
}
