// Decision 0011 (run W4, unit R9): every settings section of the app (each ISettingsSection<T> in this assembly, so a section a later unit
// adds is exported, imported and reset without a change here), read and written through ISettingsStore only, so its cache, validation and
// Changed event behave as they do for the pages. A section is handled as a JSON object of its keys (camelCase, as stored): what an export
// holds is the keys that differ from the section's defaults (a null written out where the default is not null), and an import overlays the
// file's keys on the section as it is now, so keys the file does not name keep this machine's values. Nothing here is a secret: sections
// never hold one (keys live in credentials.json).
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>One settings section, without its type: read, compare, write and reset it through an <see cref="ISettingsStore"/>.</summary>
public interface IDataSettingsSection
{
    /// <summary>The section's name in webagent.db (<c>tools</c>).</summary>
    string Name { get; }

    /// <summary>What the pages call it ("Tools &amp; workspace").</summary>
    string Title { get; }

    /// <summary>Whether it moves to another machine in an export (false for this machine's own state, such as the setup wizard's).</summary>
    bool Exported { get; }

    /// <summary>The section as stored now (or its defaults), every key written, nulls included.</summary>
    JsonObject Current(ISettingsStore store);

    /// <summary>The section's defaults, every key written.</summary>
    JsonObject Defaults();

    /// <summary><paramref name="value"/> read as the section and normalized (it is not saved).</summary>
    /// <exception cref="ArgumentException">A value is not allowed, or the JSON is not this section.</exception>
    object Read(JsonObject value);

    /// <summary>Saves <paramref name="value"/> (from <see cref="Read"/>).</summary>
    Task SaveAsync(ISettingsStore store, object value, CancellationToken cancellationToken);

    /// <summary>Resets the section to its defaults.</summary>
    Task ResetAsync(ISettingsStore store, CancellationToken cancellationToken);
}

/// <summary>The app's settings sections.</summary>
public static class DataSettingsSections
{
    /// <summary>Sections that stay on this machine: the setup wizard's state (whether it was finished or skipped here).</summary>
    public static IReadOnlySet<string> MachineOnly { get; } = new HashSet<string>(StringComparer.Ordinal) { "setup" };

    /// <summary>The page titles of the sections the app has today; a section not listed is shown by its name.</summary>
    public static IReadOnlyDictionary<string, string> Titles { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["defaults"] = "Defaults",
        ["tools"] = "Tools & workspace",
        ["models"] = "Models shown",
        ["spend"] = "Spend options",
        ["spend-filters"] = "Saved spend filters",
        ["history"] = "Run history",
        ["agentCompare"] = "Agent compare defaults",
        ["setup"] = "Setup wizard",
    };

    /// <summary>How sections are written here: as stored (<see cref="SettingsStore.JsonOptions"/>), but with nulls written out.</summary>
    public static JsonSerializerOptions FullJson { get; } = new(SettingsStore.JsonOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>Every section in this assembly, by name.</summary>
    public static IReadOnlyList<IDataSettingsSection> All { get; } = Discover();

    /// <summary>The section named <paramref name="name"/>, or null.</summary>
    public static IDataSettingsSection? Find(string name) => All.FirstOrDefault(section => section.Name == name);

    /// <summary>The keys of <paramref name="current"/> that differ from <paramref name="baseline"/>, with <paramref name="current"/>'s values.</summary>
    public static JsonObject Changed(JsonObject current, JsonObject baseline)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);
        var changed = new JsonObject();
        foreach (var key in current.Select(pair => pair.Key).Union(baseline.Select(pair => pair.Key), StringComparer.Ordinal))
        {
            var now = current[key];
            if (!JsonNode.DeepEquals(now, baseline[key])) changed[key] = now?.DeepClone();
        }
        return changed;
    }

    /// <summary><paramref name="baseline"/> with <paramref name="overlay"/>'s keys written over it.</summary>
    public static JsonObject Overlay(JsonObject baseline, JsonObject overlay)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(overlay);
        var merged = (JsonObject)baseline.DeepClone();
        foreach (var (key, value) in overlay) merged[key] = value?.DeepClone();
        return merged;
    }

    private static IReadOnlyList<IDataSettingsSection> Discover()
    {
        Type[] types;
        try { types = typeof(DataSettingsSections).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { types = [.. error.Types.OfType<Type>()]; }
        return [.. types
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && type.GetInterfaces().Any(contract =>
                contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ISettingsSection<>) && contract.GenericTypeArguments[0] == type))
            .Select(type => (IDataSettingsSection)Activator.CreateInstance(typeof(Section<>).MakeGenericType(type))!)
            .OrderBy(section => section.Name, StringComparer.Ordinal)];
    }

    /// <summary>One section type.</summary>
    private sealed class Section<T> : IDataSettingsSection where T : class, ISettingsSection<T>
    {
        public string Name => T.SectionName;

        public string Title => Titles.GetValueOrDefault(T.SectionName) ?? T.SectionName;

        public bool Exported => !MachineOnly.Contains(T.SectionName);

        public JsonObject Current(ISettingsStore store) => Write(store.Get<T>());

        public JsonObject Defaults() => Write(T.CreateDefault().Normalize());

        public object Read(JsonObject value)
        {
            ArgumentNullException.ThrowIfNull(value);
            try
            {
                return (value.Deserialize<T>(SettingsStore.JsonOptions) ?? throw new ArgumentException($"The {Title} settings are empty.")).Normalize();
            }
            catch (JsonException error)
            {
                throw new ArgumentException($"The {Title} settings are not in the expected form ({error.Message}).", error);
            }
            catch (NotSupportedException error)
            {
                throw new ArgumentException($"The {Title} settings are not in the expected form ({error.Message}).", error);
            }
        }

        public Task SaveAsync(ISettingsStore store, object value, CancellationToken cancellationToken) => store.SaveAsync((T)value, cancellationToken);

        public Task ResetAsync(ISettingsStore store, CancellationToken cancellationToken) => store.ResetAsync<T>(cancellationToken);

        // The keys a section stores: a computed, get-only property (history's retention as a TimeSpan) is not a setting, so it is neither
        // exported nor reviewed, and a file that has one gets the "not something this app imports" note.
        private static readonly HashSet<string> Stored = [.. FullJson.GetTypeInfo(typeof(T)).Properties
            .Where(property => property.Set is not null || property.AssociatedParameter is not null).Select(property => property.Name)];

        private static JsonObject Write(T value)
        {
            var node = JsonSerializer.SerializeToNode(value, FullJson) as JsonObject ?? [];
            foreach (var key in node.Select(pair => pair.Key).Where(key => !Stored.Contains(key)).ToList()) node.Remove(key);
            return node;
        }
    }
}
