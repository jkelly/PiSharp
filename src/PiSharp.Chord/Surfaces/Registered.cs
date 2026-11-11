// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/registered.ts.
using System.Text.Json;
using PiSharp.Chord.Host;

namespace PiSharp.Chord.Surfaces;

/// <summary>Native registration counterpart. This callback is trusted managed code, not a JavaScript evaluator or sandbox.</summary>
public delegate void FacetFunction(Func<string, object?> require, RegisteredModule module);
public sealed class RegisteredModule { public object? Exports { get; set; } = new Dictionary<string, object?>(); }
public sealed record RegisteredScript(string Plugin, string Digest, FacetFunction Run);

public static class Registered
{
    public const string Register = "chordFacet";

    /// <summary>Emits the upstream JavaScript registration wrapper for a separate browser/JS build adapter.</summary>
    public static string Source(string plugin, string digest, string source) =>
        $"{Register}({JsonSerializer.Serialize(plugin)},{JsonSerializer.Serialize(digest)},{source}\n);\n";

    public static Facet Evaluate(RegisteredScript script, IReadOnlyDictionary<string, object?> modules)
    {
        var module = new RegisteredModule();
        script.Run(specifier => modules.TryGetValue(specifier, out var value) ? value :
            throw new InvalidOperationException($"plugin \"{script.Plugin}\": \"{specifier}\" is not one of this client's host packages"), module);
        var facet = module.Exports switch
        {
            Facet direct => direct,
            IReadOnlyDictionary<string, object?> exports when exports.TryGetValue("default", out var value) => value as Facet,
            _ => null,
        };
        return facet ?? throw new InvalidOperationException($"plugin \"{script.Plugin}\": its default export is not a facet (Facet.Define)");
    }

    public static SetEntry Entry(RegisteredScript script, IReadOnlyDictionary<string, object?> modules) =>
        new(script.Plugin, script.Digest, () =>
        {
            try { return new Prepared(Evaluate(script, modules), script.Digest); }
            catch (Exception error) { return new PreparationFailed(error); }
        });
}
