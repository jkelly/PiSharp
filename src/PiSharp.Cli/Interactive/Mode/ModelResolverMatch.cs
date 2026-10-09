// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/model-resolver.ts (findExactModelReferenceMatch,
// resolveModelScopeFromModels) over the interactive mode's model JSON, delegating to the CLI's port (src/PiSharp.Cli/Models/ModelResolver.cs).
using System.Text.Json.Nodes;
using PiSharp.Cli.Models;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed record ModelScopeResolution(IReadOnlyList<ScopedModel> ScopedModels, IReadOnlyList<string> UnmatchedPatterns);

internal static class ModelResolverMatch
{
    private static List<(RegistryModel Registry, JsonObject Json)> Convert(IReadOnlyList<JsonObject> models)
    {
        var converted = new List<(RegistryModel, JsonObject)>();
        foreach (var model in models)
        {
            try { converted.Add((RegistryModel.FromJson(model), model)); }
            catch (ArgumentException) { }
        }
        return converted;
    }

    public static JsonObject? FindExactModelReferenceMatch(string reference, IReadOnlyList<JsonObject> models)
    {
        var converted = Convert(models);
        var match = ModelResolver.FindExactModelReferenceMatch(reference, converted.Select(pair => pair.Registry).ToList());
        return match is null ? null : converted.First(pair => ReferenceEquals(pair.Registry, match)).Json;
    }

    public static ModelScopeResolution ResolveModelScopeFromModels(IReadOnlyList<string> patterns, IReadOnlyList<JsonObject> models)
    {
        var converted = Convert(models);
        var (scoped, diagnostics) = ModelResolver.ResolveModelScope(patterns, converted.Select(pair => pair.Registry).ToList());
        return new(scoped.Select(entry => new ScopedModel(converted.First(pair => ReferenceEquals(pair.Registry, entry.Model)).Json, entry.ThinkingLevel)).ToList(),
            diagnostics.Where(diagnostic => diagnostic.Code == "no-match").Select(diagnostic => diagnostic.Pattern).ToList());
    }
}
