// Decision 0011 (run W4, unit R9): the "customProviders" part of an export: models.json's providers as the custom providers page exports them
// (CustomProviderJson.Redacted: a key that is not a $VAR reference and a secret-like header value are left out, so the file never holds a
// secret). On import, each provider entry is one item (a custom provider, or a built-in provider's model overrides), compared with this
// machine's entry key by key and checked against the whole file as Pi would read it; the accepted entries are imported through
// CustomProviderService.ImportAsync, so a literal key written into the file by hand goes into the encrypted vault (never models.json), a
// !command is dropped, and a built-in provider keeps only its model overrides, exactly as an import on the custom providers page.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.CustomProviders;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>The custom providers and built-in model overrides of models.json, as an export part.</summary>
public sealed class CustomProvidersExportPart(CustomProviderService service) : IDataExportPart, IDataResetPart
{
    /// <summary>The part's name.</summary>
    public const string PartName = "customProviders";

    private const int LongestText = 160;

    /// <inheritdoc/>
    public string Name => PartName;

    /// <inheritdoc/>
    public string Title => "custom providers";

    /// <inheritdoc/>
    public JsonNode? Export()
    {
        var snapshot = service.Current;
        if (!snapshot.Valid) throw new InvalidOperationException("models.json has an error, so the custom providers were left out. Fix it in Settings › Custom providers.");
        var providers = CustomProviderJson.Redacted(snapshot.Document)["providers"] as JsonObject;
        return providers is { Count: > 0 } ? providers.DeepClone() : null;
    }

    /// <inheritdoc/>
    public DataImportPartPlan Plan(JsonNode? value)
    {
        var incoming = Incoming(value);
        var snapshot = service.Current;
        var items = new List<DataImportItem>();
        var notes = new List<string>();
        var unchanged = 0;
        var current = CustomProviderJson.Redacted(snapshot.Document)["providers"] as JsonObject ?? [];
        foreach (var (id, node) in incoming)
        {
            if (node is not JsonObject entry)
            {
                notes.Add($"The provider “{Cut(id)}” in the file is not an object, so it is left out.");
                continue;
            }
            var builtin = CustomProviderJson.IsBuiltin(id);
            var shown = Shown(id, entry);
            var before = current[id] as JsonObject;
            var changes = new List<DataImportChange>();
            foreach (var key in shown.Select(pair => pair.Key).Union(builtin ? [] : before?.Select(pair => pair.Key) ?? [], StringComparer.Ordinal))
            {
                if (builtin && key != CustomProviderJson.BuiltinRoutedField) continue;
                var now = before?[key];
                var next = shown[key];
                if (builtin && next is JsonObject overrides && now is JsonObject existing)
                {
                    // Overrides are merged by model, so only the models the file names change.
                    foreach (var (model, fields) in overrides)
                        if (!JsonNode.DeepEquals(fields, existing[model])) changes.Add(new DataImportChange(key + "." + model, Text(existing[model]), Text(fields)));
                    continue;
                }
                if (!JsonNode.DeepEquals(now, next)) changes.Add(new DataImportChange(key, Text(now), Text(next)));
            }
            if (entry["apiKey"] is JsonValue apiKey && apiKey.TryGetValue<string>(out var keyText) && CustomProviderJson.ReferenceOf(keyText) is null && !builtin)
                changes.Add(new DataImportChange("apiKey", null, "a key written in the file: it goes into the encrypted vault, never into models.json"));
            if (builtin && CustomProviderJson.BuiltinIgnoredFields(entry).Count > 0)
                notes.Add($"{id} is a built-in provider: only its model overrides are imported; its requests go where its account says.");
            if (changes.Count == 0)
            {
                unchanged++;
                continue;
            }
            var title = builtin ? $"{id} model overrides" : (Text(entry, "name") ?? id) + (before is null ? " (new)" : "");
            items.Add(new DataImportItem(PartName + "/" + id, PartName, title, changes, Problem(snapshot, id, entry)));
        }
        return new DataImportPartPlan(items, unchanged, notes);
    }

    /// <inheritdoc/>
    public async Task<DataImportPartResult> ImportAsync(JsonNode? value, IReadOnlySet<string> accepted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        var plan = Plan(value);
        var incoming = Incoming(value);
        var chosen = plan.Items.Where(item => accepted.Contains(item.Id)).ToList();
        var failed = chosen.Where(item => item.Problem is not null).Select(item => new DataImportFailure(item.Id, item.Title, item.Problem!)).ToList();
        var go = chosen.Where(item => item.Problem is null).ToList();
        if (go.Count == 0) return new DataImportPartResult([], failed, []);
        var providers = new JsonObject();
        foreach (var item in go) providers[item.Id[(PartName.Length + 1)..]] = incoming[item.Id[(PartName.Length + 1)..]]?.DeepClone();
        var text = new JsonObject { ["providers"] = providers }.ToJsonString();
        try
        {
            var result = await service.ImportAsync(text, cancellationToken).ConfigureAwait(false);
            return new DataImportPartResult([.. go.Select(item => item.Id)], failed, result.Warnings);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            failed.AddRange(go.Select(item => new DataImportFailure(item.Id, item.Title, error.Message)));
            return new DataImportPartResult([], failed, []);
        }
    }

    /// <inheritdoc/>
    public string ResetText()
    {
        var snapshot = service.Current;
        if (!snapshot.Exists) return "";
        var parts = new List<string>();
        if (snapshot.Providers.Count > 0) parts.Add(snapshot.Providers.Count == 1 ? "the custom provider" : $"the {snapshot.Providers.Count} custom providers");
        if (snapshot.Overrides.Count > 0) parts.Add(snapshot.Overrides.Count == 1 ? "the built-in model override" : $"the {snapshot.Overrides.Count} built-in model overrides");
        return parts.Count == 0 ? "" : string.Join(" and ", parts) + " of models.json (kept as models.json.bak)";
    }

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        var snapshot = service.Current;
        if (!snapshot.Exists) return;
        if (!snapshot.Valid) throw new InvalidOperationException("models.json has an error, so it was not reset. Fix or replace it in Settings › Custom providers.");
        if (snapshot.Providers.Count == 0 && snapshot.Overrides.Count == 0) return;
        // Through the store, so the file is validated, written atomically and the old one kept as models.json.bak. Stored keys stay (credentials
        // are kept); an import of the same provider later uses them again.
        await service.Store.UpdateAsync(document => CustomProviderJson.Providers(document).Clear(), null, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject Incoming(JsonNode? value) =>
        value as JsonObject ?? throw new ArgumentException("The file's custom providers are not a JSON object of providers.");

    // An entry as the review shows it: redacted as an export would be (a literal key or secret-like header never reaches the page).
    private static JsonObject Shown(string id, JsonObject entry)
    {
        var redacted = CustomProviderJson.Redacted(new JsonObject { ["providers"] = new JsonObject { [id] = entry.DeepClone() } });
        return redacted["providers"]?[id]?.DeepClone() as JsonObject ?? [];
    }

    // Why the entry can't be imported: models.json is broken here, or the file with this entry would not be valid as Pi reads it.
    private string? Problem(CustomProviderSnapshot snapshot, string id, JsonObject entry)
    {
        if (!snapshot.Valid) return "models.json on this machine has an error. Fix it in Settings › Custom providers first.";
        if (!CustomProviderJson.IsBuiltin(id) && !CustomProviderJson.IsSlug(id) && id.Length > 64) return "The provider id is too long.";
        var document = (JsonObject)snapshot.Document.DeepClone();
        var providers = CustomProviderJson.Providers(document);
        var clone = Shown(id, entry);
        if (CustomProviderJson.IsBuiltin(id))
        {
            if (clone[CustomProviderJson.BuiltinRoutedField] is not JsonObject overrides) return null;
            if (providers[id] is not JsonObject existing) providers[id] = new JsonObject { [CustomProviderJson.BuiltinRoutedField] = overrides.DeepClone() };
            else if (existing[CustomProviderJson.BuiltinRoutedField] is not JsonObject current) existing[CustomProviderJson.BuiltinRoutedField] = overrides.DeepClone();
            else foreach (var (model, fields) in overrides) current[model] = fields?.DeepClone();
        }
        else providers[id] = clone;
        try
        {
            return service.Store.Validate(CustomProviderJson.Format(document, DateTimeOffset.UtcNow)) is { } error ? Cut(error, LongestText * 2) : null;
        }
        catch (IOException error)
        {
            return "It could not be checked: " + error.Message;
        }
    }

    private static string? Text(JsonObject value, string key) =>
        value[key] is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string? Text(JsonNode? value)
    {
        if (value is null) return null;
        if (value is JsonArray array && array.Count > 0 && array.All(item => item is JsonObject))
        {
            // A model list: its ids ("qwen3-coder, llama-3.3 (2 models)"), not its JSON.
            var ids = array.Select(item => item?["id"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : "?").ToList();
            return Cut(string.Join(", ", ids), LongestText - 16) + (ids.Count == 1 ? " (1 model)" : $" ({ids.Count} models)");
        }
        return Cut(value.ToJsonString(Compact), LongestText);
    }

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Cut(string text, int length = 60) => text.Length > length ? text[..(length - 1)] + "…" : text;
}
