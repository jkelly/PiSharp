// Decision 0011 (run W4, R8): the "presets" part of Settings › Data & storage's export (R9's IDataExportPart). It exports every preset as the
// Presets page's Export all does (PresetJson: no use counts, never a secret), and on import each preset is one item, "presets/{id}": a new
// preset, or one this app has with the keys that differ (name, tags, model, options...). Unlike the Presets page's Import JSON, which never
// replaces a preset, a reviewed data import replaces the preset of the same id with the file's (keeping this machine's use counts and when it
// was created), because the user saw every changed key first. A preset that is not valid is listed with its problem and never imported.
// Presets are records, not settings, so "Reset all settings" leaves them alone (no IDataResetPart).
using System.Text.Json.Nodes;
using PiSharp.WebAgent.DataStorage;

namespace PiSharp.WebAgent.Presets;

/// <summary>The presets, as an export part.</summary>
public sealed class PresetsExportPart(PresetStore store) : IDataExportPart
{
    /// <summary>The part's name.</summary>
    public const string PartName = "presets";

    private readonly PresetStore store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc/>
    public string Name => PartName;

    /// <inheritdoc/>
    public string Title => "presets";

    /// <inheritdoc/>
    public JsonNode? Export()
    {
        var all = store.List();
        return all.Count == 0 ? null : new JsonArray([.. all.OrderBy(preset => preset.Id, StringComparer.Ordinal).Select(preset => (JsonNode)PresetJson.Node(preset))]);
    }

    /// <inheritdoc/>
    public DataImportPartPlan Plan(JsonNode? value)
    {
        var items = new List<DataImportItem>();
        var notes = new List<string>();
        var unchanged = 0;
        foreach (var (node, index) in Entries(value).Select((node, index) => (node, index)))
        {
            Preset preset;
            try
            {
                preset = PresetJson.Read(node);
            }
            catch (ArgumentException error)
            {
                var label = node is JsonObject json && json["name"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : $"Preset {index + 1}";
                var id = node is JsonObject withId && withId["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var raw) && raw.Length > 0 ? raw : $"#{index + 1}";
                items.Add(new DataImportItem($"{PartName}/{id}", PartName, label, [], error.Message));
                continue;
            }
            if (preset.Id.Length == 0) preset = preset with { Id = PresetStore.Slug(preset.Name) };
            var existing = store.Get(preset.Id);
            if (existing is not null && PresetJson.Same(existing, preset))
            {
                unchanged++;
                continue;
            }
            var changes = PresetJson.Changes(existing, preset).Select(change => new DataImportChange(change.Key, change.Before, change.After)).ToList();
            items.Add(new DataImportItem($"{PartName}/{preset.Id}", PartName, preset.Name, changes));
        }
        if (items.Count > 0 && items.Any(item => store.Get(item.Id[(PartName.Length + 1)..]) is not null))
            notes.Add("A preset of the same id is replaced by the file's; its use counts here are kept.");
        return new DataImportPartPlan(items, unchanged, notes);
    }

    /// <inheritdoc/>
    public Task<DataImportPartResult> ImportAsync(JsonNode? value, IReadOnlySet<string> accepted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        var applied = new List<string>();
        var failed = new List<DataImportFailure>();
        foreach (var node in Entries(value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Preset preset;
            try
            {
                preset = PresetJson.Read(node);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (preset.Id.Length == 0) preset = preset with { Id = PresetStore.Slug(preset.Name) };
            var id = $"{PartName}/{preset.Id}";
            if (!accepted.Contains(id)) continue;
            try
            {
                var existing = store.Get(preset.Id);
                store.Put(preset with
                {
                    Uses = existing?.Uses ?? 0,
                    LastUsed = existing?.LastUsed,
                    UsedIn = existing?.UsedIn ?? [],
                    Created = existing?.Created ?? preset.Created,
                });
                applied.Add(id);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                failed.Add(new DataImportFailure(id, preset.Name, error.Message));
            }
        }
        return Task.FromResult(new DataImportPartResult(applied, failed, []));
    }

    private static IReadOnlyList<JsonNode?> Entries(JsonNode? value) => value switch
    {
        null => [],
        JsonArray list => [.. list],
        JsonObject envelope when envelope["presets"] is JsonArray list => [.. list],
        _ => throw new ArgumentException("The presets in the file are not a list."),
    };
}
