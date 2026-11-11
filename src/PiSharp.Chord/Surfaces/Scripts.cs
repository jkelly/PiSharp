// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/scripts.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;

namespace PiSharp.Chord.Surfaces;

public sealed record ScriptEntry(string Name, string Digest, string Url, string? Css = null)
{
    public JsonObject ToJson()
    {
        var value = new JsonObject { ["name"] = Name, ["digest"] = Digest, ["url"] = Url };
        if (Css is not null) value["css"] = Css;
        return value;
    }
}

/// <summary>The base hash, combined URL and name-ordered plugin scripts of a client kind.</summary>
public sealed record ScriptSet(string Base, string Combined, IReadOnlyList<ScriptEntry> Plugins)
{
    public JsonObject ToJson() => new() { ["base"] = Base, ["combined"] = Combined,
        ["plugins"] = new JsonArray(Plugins.Select(entry => (JsonNode)entry.ToJson()).ToArray()) };

    public static ScriptSet FromJson(JsonNode? value)
    {
        if (value is not JsonObject obj || obj["base"] is not JsonValue b || !b.TryGetValue<string>(out var baseHash) ||
            obj["combined"] is not JsonValue c || !c.TryGetValue<string>(out var combined) || obj["plugins"] is not JsonArray plugins)
            throw new ArgumentException("Invalid script set", nameof(value));
        var entries = new List<ScriptEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in plugins)
        {
            if (node is not JsonObject entry || entry["name"] is not JsonValue n || !n.TryGetValue<string>(out var name) ||
                entry["digest"] is not JsonValue d || !d.TryGetValue<string>(out var digest) ||
                entry["url"] is not JsonValue u || !u.TryGetValue<string>(out var url) ||
                entry["css"] is not null && (entry["css"] is not JsonValue css || !css.TryGetValue<string>(out _)) || !names.Add(name))
                throw new ArgumentException("Invalid or duplicate script entry", nameof(value));
            entries.Add(new(name, digest, url, entry["css"]?.GetValue<string>()));
        }
        return new(baseHash, combined, entries);
    }
}

public static class Scripts
{
    public static HostKind Server { get; } = new("server");
    public static JsonObject Schema { get; } = Schemas.Obj(("base", Schemas.Str()), ("combined", Schemas.Str()),
        ("plugins", Schemas.Arr(Schemas.Obj(("name", Schemas.Str()), ("digest", Schemas.Str()), ("url", Schemas.Str()), ("css", Schemas.Opt(Schemas.Str()))))));
    public static Contract WebScripts { get; } = Define("chord.web.scripts");
    public static Contract TuiScripts { get; } = Define("chord.tui.scripts");

    private static Contract Define(string name) => Contract.Remote(name, new RemoteDeclaration
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(),
        Variants = new() { ["main"] = new() { State = new() { ["set"] = Schema } } },
    });
}
