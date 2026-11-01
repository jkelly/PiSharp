// Decision 0011 (run W4, unit R9): the "settings" part of an export: every settings section that moves between machines (all but the setup
// wizard's state), with only the keys that differ from the section's defaults, so an import onto another machine changes what the user
// changed and nothing else. On import each section is one item: its changed keys are listed with this app's value and the file's, and it is
// checked as the pages check it (a default model must be in this app's catalog). A working folder this machine doesn't have is not imported
// (the machine's own folder stays, and the review says so) rather than holding up the section's other keys.
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>The settings sections, as an export part.</summary>
public sealed class SettingsExportPart(ISettingsStore store, IModelCatalog? catalog = null) : IDataExportPart, IDataResetPart
{
    /// <summary>The part's name.</summary>
    public const string PartName = "settings";

    /// <inheritdoc/>
    public string Name => PartName;

    /// <inheritdoc/>
    public string Title => "settings";

    /// <inheritdoc/>
    public JsonNode? Export()
    {
        var sections = new JsonObject();
        foreach (var section in DataSettingsSections.All.Where(section => section.Exported))
        {
            var changed = DataSettingsSections.Changed(section.Current(store), section.Defaults());
            if (changed.Count > 0) sections[section.Name] = changed;
        }
        return sections;
    }

    /// <inheritdoc/>
    public DataImportPartPlan Plan(JsonNode? value) => Build(value).Plan;

    /// <inheritdoc/>
    public async Task<DataImportPartResult> ImportAsync(JsonNode? value, IReadOnlySet<string> accepted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        var (plan, merged) = Build(value);
        var applied = new List<string>();
        var failed = new List<DataImportFailure>();
        foreach (var item in plan.Items.Where(item => accepted.Contains(item.Id)))
        {
            if (item.Problem is not null)
            {
                failed.Add(new DataImportFailure(item.Id, item.Title, item.Problem));
                continue;
            }
            var (section, read) = merged[item.Id];
            try
            {
                await section.SaveAsync(store, read, cancellationToken).ConfigureAwait(false);
                applied.Add(item.Id);
            }
            catch (ArgumentException error)
            {
                failed.Add(new DataImportFailure(item.Id, item.Title, error.Message));
            }
        }
        return new DataImportPartResult(applied, failed, []);
    }

    private (DataImportPartPlan Plan, Dictionary<string, (IDataSettingsSection Section, object Value)> Merged) Build(JsonNode? value)
    {
        if (value is not JsonObject file) throw new ArgumentException("The file's settings are not a JSON object of sections.");
        var items = new List<DataImportItem>();
        var notes = new List<string>();
        var merged = new Dictionary<string, (IDataSettingsSection, object)>(StringComparer.Ordinal);
        var unchanged = 0;
        foreach (var (name, node) in file)
        {
            var section = DataSettingsSections.Find(name);
            if (section is null)
            {
                notes.Add($"The settings section “{Cut(name)}” isn't one this app has, so it is left out.");
                continue;
            }
            if (!section.Exported)
            {
                notes.Add($"{section.Title} stays as it is on this machine, so it is left out.");
                continue;
            }
            if (node is not JsonObject keys)
            {
                notes.Add($"{section.Title} in the file is not an object of settings, so it is left out.");
                continue;
            }
            var current = section.Current(store);
            var overlay = new JsonObject();
            foreach (var (key, incoming) in keys)
            {
                if (!current.ContainsKey(key)) notes.Add($"{section.Title} has no setting “{Cut(key)}”, so it is left out.");
                else overlay[key] = incoming?.DeepClone();
            }
            if (section.Name == "tools" && overlay["workingDirectory"] is JsonValue folder && folder.TryGetValue<string>(out var path) &&
                !string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
            {
                overlay.Remove("workingDirectory");
                notes.Add($"There is no folder {path} on this machine, so the working folder stays {current["workingDirectory"]?.GetValue<string>() ?? "as it is"}.");
            }
            var changed = DataSettingsSections.Changed(DataSettingsSections.Overlay(current, overlay), current);
            if (changed.Count == 0)
            {
                unchanged++;
                continue;
            }
            var id = PartName + "/" + section.Name;
            var changes = changed.Select(pair => new DataImportChange(pair.Key, Text(current[pair.Key]), Text(pair.Value))).ToList();
            string? problem = null;
            try
            {
                var read = section.Read(DataSettingsSections.Overlay(current, overlay));
                problem = Check(read);
                if (problem is null) merged[id] = (section, read);
            }
            catch (ArgumentException error)
            {
                problem = error.Message;
            }
            items.Add(new DataImportItem(id, PartName, section.Title, changes, problem));
        }
        return (new DataImportPartPlan(items, unchanged, notes), merged);
    }

    /// <summary>The sections a reset sets back: every one but this machine's own state (the setup wizard's).</summary>
    public static IEnumerable<IDataSettingsSection> Resettable => DataSettingsSections.All.Where(section => section.Exported);

    /// <summary>The titles of the sections that differ from their defaults now.</summary>
    public IReadOnlyList<string> ChangedSections() =>
        [.. Resettable.Where(section => DataSettingsSections.Changed(section.Current(store), section.Defaults()).Count > 0).Select(section => section.Title)];

    /// <inheritdoc/>
    public string ResetText()
    {
        var changed = ChangedSections();
        return changed.Count == 0 ? "" : "the settings you changed (" + string.Join(", ", changed) + ")";
    }

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        foreach (var section in Resettable) await section.ResetAsync(store, cancellationToken).ConfigureAwait(false);
    }

    // The checks the settings pages make beyond a section's own: a default model is one this app's catalog has.
    private string? Check(object value) => value switch
    {
        DefaultsSettings { Provider: { } provider, Model: { } model } when catalog is not null && catalog.FindModel(provider, model) is null =>
            $"This app's catalog has no model {model} of {provider}.",
        _ => null,
    };

    /// <summary>A setting's value as the review shows it: compact JSON, or null for none.</summary>
    public static string? Text(JsonNode? value) => value?.ToJsonString(Compact);

    private static readonly System.Text.Json.JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Cut(string text) => text.Length > 60 ? text[..59] + "…" : text;
}
