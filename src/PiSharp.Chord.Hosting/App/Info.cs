// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/info.ts.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.State;
using static PiSharp.Chord.Contracts.Schemas;

namespace PiSharp.Chord.Hosting.App;

public static class AppPrincipals
{
    public static bool Same(JsonObject? first, JsonObject? second) => second is not null && Principals.Same(first, second, ChordContext.Background);
    /// <summary>Only actual JSON primitives and a string kind may cross an authentication boundary.</summary>
    public static JsonObject Require(JsonObject principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var value) || string.IsNullOrEmpty(value) || !Strict(principal))
            throw new ArgumentException("principal must be strict JSON with a string kind", nameof(principal));
        return (JsonObject)principal.DeepClone();
    }
    private static bool Strict(JsonNode? node) => node switch
    {
        null => true,
        JsonObject obj => obj.All(pair => Strict(pair.Value)),
        JsonArray array => array.All(Strict),
        JsonValue value when value.TryGetValue<JsonElement>(out var element) => element.ValueKind switch
        {
            JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            JsonValueKind.Number => element.TryGetDouble(out var n) && double.IsFinite(n),
            _ => false,
        },
        JsonValue value => value.TryGetValue<string>(out _) || value.TryGetValue<bool>(out _) ||
            value.TryGetValue<int>(out _) || value.TryGetValue<long>(out _) || value.TryGetValue<uint>(out _) ||
            value.TryGetValue<ulong>(out _) || value.TryGetValue<short>(out _) || value.TryGetValue<ushort>(out _) ||
            value.TryGetValue<byte>(out _) || value.TryGetValue<sbyte>(out _) || value.TryGetValue<decimal>(out _) ||
            value.TryGetValue<double>(out var d) && double.IsFinite(d) || value.TryGetValue<float>(out var f) && float.IsFinite(f),
        _ => false,
    };
}

public sealed record AppInfo(string DataDir, string Name, string Version, JsonObject Owner);
public sealed record WorkerInfo(Address Address, JsonNode? Start);
public static class Info
{
    public static Contract AppInfo { get; } = Contract.Local("chord.app.info", new()
    { Version = 2, Params = [], Members = new() { ["dataDir"] = Str(), ["name"] = Str(), ["version"] = Str(), ["owner"] = Unknown() } });
    public static Contract WorkerInfo { get; } = Contract.Local("chord.app.worker-info", new()
    { Version = 1, Params = [], Members = new() { ["address"] = Obj(("kind", Str()), ("key", Str()), ("params", Unknown())), ["start"] = Unknown() } });
    public static Facet AppInfoFacet(AppInfo info) => Facet.Define((host, _) => host.Serve(AppInfo, (_, _, _) => new(new Dictionary<string, object?>
    {
        ["dataDir"] = Watchables.Constant(JsonValue.Create(info.DataDir)), ["name"] = Watchables.Constant(JsonValue.Create(info.Name)),
        ["version"] = Watchables.Constant(JsonValue.Create(info.Version)), ["owner"] = Watchables.Constant(info.Owner.DeepClone()),
    })));
    public static Facet WorkerInfoFacet(WorkerInfo info) => Facet.Define((host, _) => host.Serve(WorkerInfo, (_, _, _) => new(new Dictionary<string, object?>
    { ["address"] = Watchables.Constant(info.Address.ToJson()), ["start"] = Watchables.Constant(info.Start?.DeepClone()) })));
}
