// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/data.ts.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Hosting.App;

/// <summary>The worker's private live-snapshot endpoint. This is distinct from host-keyed documents.</summary>
public static class HostData
{
    public static Contract Type { get; } = Contract.Remote("chord.app.data", new()
    {
        Version = 1,
        Params = new() { ["address"] = Schemas.Str() },
        HostAddress = parameters => ParseAddress(parameters["address"]!.GetValue<string>()),
        Variants = new()
        {
            ["main"] = new()
            {
                Methods = new()
                {
                    ["exportTo"] = Schemas.Method(Schemas.Obj(("path", Schemas.Str()), ("manifest", Schemas.Str()), ("server", Schemas.Str()))),
                },
            },
        },
    });

    public static string AddressParam(Address address) => address.ToJson().ToJsonString();

    public static Facet CreateFacet(IStore store, string files, JsonObject server) => Facet.Define((host, _) =>
        host.Serve(Type, (_, principal, _) =>
        {
            if (!Principals.Same(principal, server, ChordContext.Background)) throw Errors.Forbidden("chord.app.data: the server's only");
            return new HostImplementation(new Dictionary<string, object?>
            {
                ["exportTo"] = (HostMethod)(async (argument, context) =>
                {
                    var data = argument as JsonObject ?? throw Errors.Invalid("args", "Expected export arguments");
                    var manifest = JsonSerializer.Deserialize<ArchiveManifest>(data["manifest"]!.GetValue<string>())
                        ?? throw Errors.Invalid("args", "Expected archive manifest");
                    await Archive.PackStoreAsync(store, files, manifest, data["server"]!.GetValue<string>(),
                        data["path"]!.GetValue<string>(), context.Context).ConfigureAwait(false);
                    return null;
                }),
            }, "main");
        }));

    private static Address ParseAddress(string text)
    {
        var json = JsonNode.Parse(text) as JsonObject ?? throw Errors.Invalid("args", "Invalid worker address");
        var kind = json["kind"]?.GetValue<string>() ?? throw Errors.Invalid("args", "Missing worker kind");
        var parameters = json["params"] as JsonObject ?? throw Errors.Invalid("args", "Missing worker parameters");
        var properties = new OrderedDictionary<string, Property>(StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
        {
            properties[name] = value is JsonValue scalar && scalar.TryGetValue<string>(out _) ? Schemas.Str()
                : value is JsonValue boolean && boolean.TryGetValue<bool>(out _) ? Schemas.Bool()
                : value is JsonValue number && number.TryGetValue<long>(out _) ? Schemas.Int()
                : throw Errors.Invalid("args", "Invalid worker parameter");
        }
        var declaration = new HostKind(kind, properties);
        var address = declaration.Address(parameters);
        if (declaration.KeyOfAddress(address) is null || address.Key != json["key"]?.GetValue<string>())
            throw Errors.Invalid("args", "Invalid worker key");
        return address;
    }
}
