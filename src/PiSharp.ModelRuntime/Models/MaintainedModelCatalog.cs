// PiSharp-only (no upstream Pi source): optional maintained model catalog support.
using PiSharp.AI.Catalogs;
using PiSharp.Contracts;

namespace PiSharp.Cli.Models;

/// <summary>Optional reviewed catalog layer, after remote catalogs and before user/extension overrides.</summary>
internal static class MaintainedModelCatalog
{
    internal static (IReadOnlyList<RegistryModel> Models, string? Error) Apply(string provider, IReadOnlyList<RegistryModel> baseline, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return (baseline, null);
        try
        {
            var file = new FileInfo(path);
            if (file.Length > 8 * 1024 * 1024) return (baseline, "Maintained model catalog exceeds 8 MiB.");
            var supported = BuiltinProviders.All.FirstOrDefault(p => p.Id == provider)?.Apis ?? [];
            if (supported.Length == 0) return (baseline, null);
            var result = MaintainedCatalogOverlay.Apply(provider, baseline.Select(m => JsonData.Parse(m.ToJsonString())), JsonData.Parse(File.ReadAllText(path)), supported);
            var originals = baseline.ToDictionary(m => m.Type + ":" + m.Id, StringComparer.Ordinal);
            return (result.Models.Select(m =>
            {
                var updated = RegistryModel.FromJson(System.Text.Json.Nodes.JsonNode.Parse(m.ToString())!.AsObject());
                return originals.TryGetValue(updated.Type + ":" + updated.Id, out var original) && original.ToJsonString() == updated.ToJsonString() ? original : updated;
            }).ToArray(),
                result.Warnings.Length > 0 ? string.Join("\n", result.Warnings) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
        { return (baseline, "Maintained model catalog could not be applied: " + ex.Message); }
    }
}
