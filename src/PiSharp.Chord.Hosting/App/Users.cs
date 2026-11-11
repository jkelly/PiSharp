// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/users.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using static PiSharp.Chord.Contracts.Schemas;

namespace PiSharp.Chord.Hosting.App;

public sealed record UserDefinition(Contract Type, Facet Facet);

/// <summary>Optional persisted user profiles. The application chooses and publishes this facet explicitly.</summary>
public static class Users
{
    public static UserDefinition Define(JsonNode profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var type = Contract.Local("chord.app.users", new()
        {
            Version = 1, Params = [], Members = new()
            {
                ["list"] = Arr(Obj(("id", Str()), ("profile", profile))),
                ["get"] = Method(Str(), Union(profile, Nul())),
                ["update"] = Method(Obj(("id", Str()), ("profile", profile))), ["remove"] = Method(Str()),
            },
        });
        var document = StateDefinition.Define("chord.app.users", new()
        {
            Version = 1, Schema = Obj(("users", Map(profile))), Initial = () => new() { ["users"] = new JsonObject() },
            Persist = true, CheckpointWhen = (_, _, info) => info.DeltasSinceBase >= 50,
        });
        return new(type, Facet.Define((host, _) => host.Serve(type, (_, _, self) =>
        {
            var accounts = self.State(document);
            return new(new Dictionary<string, object?>
            {
                ["list"] = self.Derive([accounts], values => new JsonArray(((JsonObject)((JsonObject)values[0]!)["users"]!).Select(pair =>
                    (JsonNode?)new JsonObject { ["id"] = pair.Key, ["profile"] = pair.Value?.DeepClone() }).ToArray())),
                ["get"] = (HostMethod)((args, _) => Task.FromResult<object?>(accounts.Value!["users"]![((JsonValue)args!).GetValue<string>()]?.DeepClone())),
                ["update"] = (HostMethod)(async (args, context) =>
                {
                    var input = (JsonObject)args!; var id = input["id"]!.GetValue<string>();
                    if (id == "__proto__") throw new ArgumentException("users: __proto__ is no user id");
                    await accounts.ChangeAsync(draft => draft["users"]![id] = input["profile"]?.DeepClone(), context.Context).ConfigureAwait(false); return null;
                }),
                ["remove"] = (HostMethod)(async (args, context) =>
                {
                    var id = ((JsonValue)args!).GetValue<string>();
                    await accounts.ChangeAsync(draft => ((JsonObject)draft["users"]!).Remove(id), context.Context).ConfigureAwait(false); return null;
                }),
            });
        })));
    }
}
