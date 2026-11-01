using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Decision 0010, M6: the capability manifest's web bindings against the hub's pages. A bound page must be served with the automation view it
// names, and every automation page the hub links must be bound, so removing or inventing a binding is caught.
internal static partial class Program
{
    /// <summary>Automation pages no manifest feature binds: <c>/schedules</c> belongs to recurring jobs (decision 0009), which predate the
    /// manifest of decision 0010.</summary>
    private static readonly string[] UnboundPages = ["/schedules"];

    private static JsonObject Manifest()
    {
        var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        return JsonNode.Parse(File.ReadAllText(Path.GetFullPath(Path.Join(metadata["WebProject"]!, "..", "..", "contracts", "durable-capabilities.json"))))!.AsObject();
    }

    /// <summary>What is wrong with a manifest's web bindings, given the automation view each hub page shows (null for a page that is not
    /// one).</summary>
    private static List<string> WebBindingProblems(JsonObject manifest, IReadOnlyDictionary<string, string?> views)
    {
        var problems = new List<string>();
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in manifest["features"]!.AsArray().OfType<JsonObject>())
        {
            var id = feature["id"]!.GetValue<string>();
            var pages = feature["web"]!.AsArray().Select(page => page!.GetValue<string>()).ToList();
            var reason = feature["unsupported"]?["web"]?.GetValue<string>();
            if ((pages.Count > 0) == (reason is { Length: > 0 })) problems.Add($"{id} must either name pages or say why it has none");
            foreach (var page in pages)
            {
                bound.Add(page);
                if (!views.TryGetValue(page.ToLowerInvariant(), out var view) || view is null) problems.Add($"{id}: {page} is not a hub automation page");
                else if (!string.Equals(view, page.TrimStart('/'), StringComparison.OrdinalIgnoreCase)) problems.Add($"{id}: {page} shows the {view} view");
            }
        }
        foreach (var (page, view) in views)
            if (view is not null && !bound.Contains(page) && !UnboundPages.Contains(page, StringComparer.Ordinal)) problems.Add($"{page} is not bound by any feature");
        return problems;
    }

    internal static IEnumerable<(string, Func<Task>)> ManifestCases() =>
    [
        Case("hub.manifest-web-bindings-match-the-hub-pages", async () =>
        {
            await using var hub = await Hub.StartAsync("manifest");
            var manifest = Manifest();
            // The pages the hub links from its top bar, and every page a binding names, with the automation view each one shows.
            var paths = Regex.Matches((await hub.Get("/executions")).Text, "href=\"(/[a-z][a-z-]*)\"").Select(match => match.Groups[1].Value)
                .Concat(manifest["features"]!.AsArray().SelectMany(feature => feature!["web"]!.AsArray().Select(page => page!.GetValue<string>().ToLowerInvariant())))
                .Distinct(StringComparer.Ordinal).ToList();
            var views = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var page = await hub.Get(path);
                views[path] = page.Status == 200 && Regex.Match(page.Text, "data-automation=\"([a-z-]+)\"") is { Success: true } marker ? marker.Groups[1].Value : null;
            }
            Equal("/budgets,/executions,/policies,/preferences,/research,/schedules,/sources",
                string.Join(",", views.Where(view => view.Value is not null).Select(view => view.Key).Order(StringComparer.Ordinal)), "the hub's automation pages");
            Equal("", string.Join("; ", WebBindingProblems(manifest, views)), "the manifest's web bindings match the hub");

            // The check catches a page dropped from a binding, a page that does not exist, and a binding with neither pages nor a reason.
            var damaged = manifest.DeepClone().AsObject();
            JsonObject Feature(string id) => damaged["features"]!.AsArray().OfType<JsonObject>().Single(feature => feature["id"]!.GetValue<string>() == id);
            Feature("research")["web"] = new JsonArray();
            Feature("research")["unsupported"] = new JsonObject { ["web"] = "Removed for the test." };
            Feature("budgets")["web"] = new JsonArray("/Budgets", "/Spending");
            Feature("retention")["unsupported"] = new JsonObject();
            var problems = WebBindingProblems(damaged, views);
            Equal("/research is not bound by any feature; budgets: /Spending is not a hub automation page; retention must either name pages or say why it has none",
                string.Join("; ", problems.Order(StringComparer.Ordinal)), "each damage is reported");
        }),
    ];
}
