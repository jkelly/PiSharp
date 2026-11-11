// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/{preference,theme-choice,locale-choice}.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;

namespace PiSharp.Chord.Surfaces;

public static class Preferences
{
    public const int MaximumPicks = 10000;
    public static Contract Define(string name, JsonNode schema) => Contract.Remote(name, new RemoteDeclaration
    {
        Version = 1, Params = new(), HostAddress = _ => Scripts.Server.Address(),
        Variants = new() { ["main"] = new() { State = new() { ["pick"] = schema }, Methods = new() { ["choose"] = Schemas.Method(schema) } } },
    });

    /// <summary>Persists the last 10,000 canonical principals' picks; every holder of a principal follows the same document.</summary>
    public static Facet Facet(Contract choice, string store, JsonNode schema, JsonNode? none)
    {
        var picks = StateDefinition.Define(store, new StateOptions
        {
            Version = 1, Schema = Schemas.Obj(("picks", Schemas.Map(schema))), Initial = () => new JsonObject { ["picks"] = new JsonObject() },
            Persist = true, CheckpointWhen = (_, _, info) => info.DeltasSinceBase >= 50,
        });
        var pickStore = Contract.Local(store, new LocalDeclaration
        {
            Version = 1, Params = new(), Members = new()
            { ["picks"] = Schemas.Map(schema), ["set"] = Schemas.Method(Schemas.Obj(("key", Schemas.Str()), ("pick", schema))) },
        });
        return Host.Facet.Define((host, _) =>
        {
            host.Serve(pickStore, (_, _, self) =>
            {
                var state = self.State(picks);
                return new(new Dictionary<string, object?>
                {
                    ["picks"] = self.Derive([state], values => ((JsonObject)values[0]!)["picks"]),
                    ["set"] = (HostMethod)(async (arguments, context) =>
                    {
                        var args = (JsonObject)arguments!; var key = args["key"]!.GetValue<string>();
                        await state.ChangeAsync(draft =>
                        {
                            var all = (JsonObject)draft["picks"]!;
                            all.Remove(key); all[key] = args["pick"]?.DeepClone();
                            foreach (var old in all.Select(entry => entry.Key).Take(Math.Max(0, all.Count - MaximumPicks)).ToArray()) all.Remove(old);
                        }, context.Context).ConfigureAwait(false);
                        return null;
                    }),
                });
            });
            host.Serve(choice, (_, principal, self) =>
            {
                var all = self.Get(pickStore, new());
                var key = DataInternal.PrincipalKey(principal!, ChordContext.Background);
                return new(new Dictionary<string, object?>
                {
                    ["pick"] = self.Derive([all.Member("picks")], values =>
                    {
                        var dictionary = (JsonObject)values[0]!;
                        return dictionary.TryGetPropertyValue(key, out var picked) ? picked : none;
                    }),
                    ["choose"] = (HostMethod)((next, context) => all.CallAsync("set", new JsonObject
                        { ["key"] = key, ["pick"] = ((JsonNode?)next)?.DeepClone() }, context.Context)),
                }, "main");
            });
        });
    }
}

public static class ThemeChoice
{
    public static JsonObject Schema { get; } = Schemas.Obj(("dark", Schemas.Union(Schemas.Str(max: 128), Schemas.Nul())),
        ("light", Schemas.Union(Schemas.Str(max: 128), Schemas.Nul())));
    public static Contract Contract { get; } = Preferences.Define("chord.themes.choice", Schema);
    public static Facet Facet() => Preferences.Facet(Contract, "chord.themes.picks", Schema, new JsonObject { ["dark"] = null, ["light"] = null });
}

public static class LocaleChoice
{
    public static JsonObject Schema { get; } = Schemas.Union(Schemas.Str(max: 35, pattern: "^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$"), Schemas.Nul());
    public static Contract Contract { get; } = Preferences.Define("chord.locale.choice", Schema);
    public static Facet Facet() => Preferences.Facet(Contract, "chord.locale.picks", Schema, null);
}
