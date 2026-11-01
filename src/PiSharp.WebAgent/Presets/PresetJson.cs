// Decision 0011 (run W4, R8): presets as JSON files. Export one: the preset itself ({id}.preset.json); export all: an envelope
// {format: "pisharp-presets", version: 1, exported, presets: [...]} (pisharp-presets-YYYY-MM-DD.json). Either holds what the preset holds,
// without this machine's use counts, and never a secret (a preset holds none: PresetRules). Import takes either file, or a plain list of
// presets; the whole file is checked first (invalid JSON, an unknown format or any invalid preset refuses the import and nothing is saved),
// and an unknown field (an apiKey a hand-written file carries) is dropped by the reader.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Presets;

/// <summary>Presets as JSON files.</summary>
public static class PresetJson
{
    /// <summary>The envelope's format name.</summary>
    public const string Format = "pisharp-presets";

    /// <summary>The envelope's version.</summary>
    public const int Version = 1;

    /// <summary>The most presets one file imports.</summary>
    public const int MaxPresets = 500;

    /// <summary>The largest file, in characters.</summary>
    public const int MaxText = 32 * 1024 * 1024;

    /// <summary>The keys only this machine knows, left out of a file.</summary>
    public static IReadOnlyList<string> LocalKeys { get; } = ["uses", "lastUsed", "usedIn"];

    private static readonly JsonSerializerOptions Indented = new(SettingsStore.JsonOptions) { WriteIndented = true };

    /// <summary>A preset as a file holds it.</summary>
    public static JsonObject Node(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var json = JsonSerializer.SerializeToNode(preset, SettingsStore.JsonOptions)!.AsObject();
        foreach (var key in LocalKeys) json.Remove(key);
        return json;
    }

    /// <summary>One preset's file.</summary>
    public static string Export(Preset preset) => Node(preset).ToJsonString(Indented);

    /// <summary>The file of every preset in <paramref name="presets"/>.</summary>
    public static string ExportAll(IEnumerable<Preset> presets, DateTimeOffset at) => Envelope(presets, at).ToJsonString(Indented);

    /// <summary>The envelope of <paramref name="presets"/>.</summary>
    public static JsonObject Envelope(IEnumerable<Preset> presets, DateTimeOffset at) => new()
    {
        ["format"] = Format,
        ["version"] = Version,
        ["exported"] = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        ["presets"] = new JsonArray([.. presets.Select(preset => (JsonNode)Node(preset))]),
    };

    /// <summary>The file name of one preset's file.</summary>
    public static string FileName(Preset preset) => preset.Id + ".preset.json";

    /// <summary>The file name of the file of every preset.</summary>
    public static string AllFileName(DateTimeOffset at) => "pisharp-presets-" + at.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".json";

    /// <summary>The presets of a file's text, each checked.</summary>
    /// <exception cref="ArgumentException">The text is not valid JSON, not a presets file, or a preset in it is not valid (nothing is imported).</exception>
    public static IReadOnlyList<Preset> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("The file is empty.");
        if (text.Length > MaxText) throw new ArgumentException($"The file is over {MaxText / (1024 * 1024)} MB.");
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The file is not valid JSON: " + FirstSentence(error.Message));
        }
        return Items(root);
    }

    /// <summary>The presets of a parsed file (an envelope, a list of presets or one preset), each checked.</summary>
    /// <exception cref="ArgumentException">It is not a presets file, or a preset in it is not valid.</exception>
    public static IReadOnlyList<Preset> Items(JsonNode? root)
    {
        IReadOnlyList<JsonNode?> items = root switch
        {
            JsonArray array => [.. array],
            JsonObject envelope when envelope.ContainsKey("presets") => Envelope(envelope),
            JsonObject single when single.ContainsKey("name") => [single],
            _ => throw new ArgumentException("This is not a presets file: it has no presets (a PiSharp presets export, a list of presets or one preset)."),
        };
        if (items.Count > MaxPresets) throw new ArgumentException($"A file imports at most {MaxPresets} presets.");
        var presets = new List<Preset>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var label = item is JsonObject named && named["name"] is JsonValue name && name.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? $"Preset {index + 1} (“{Cut(text.Trim())}”)" : $"Preset {index + 1}";
            try
            {
                presets.Add(Read(item));
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException($"{label}: {error.Message} Nothing was imported.", error);
            }
        }
        return presets;
    }

    /// <summary>One preset of a file, checked (its use counts are this machine's, so a file's are dropped).</summary>
    /// <exception cref="ArgumentException">It is not a valid preset.</exception>
    public static Preset Read(JsonNode? node)
    {
        if (node is not JsonObject json) throw new ArgumentException("A preset is a JSON object.");
        Preset preset;
        try
        {
            preset = json.Deserialize<Preset>(SettingsStore.JsonOptions) ?? throw new ArgumentException("A preset is a JSON object.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("It is not valid: " + (error.Path is { Length: > 0 } path ? path + ": " : "") + FirstSentence(error.Message));
        }
        catch (InvalidOperationException error)
        {
            throw new ArgumentException("It is not valid: " + FirstSentence(error.Message));
        }
        return PresetRules.Normalize(preset with { Uses = 0, LastUsed = null, UsedIn = [] });
    }

    /// <summary>Whether two presets hold the same bundle (ignoring when they were saved and how often they were used).</summary>
    public static bool Same(Preset a, Preset b) => JsonNode.DeepEquals(Comparable(a), Comparable(b));

    /// <summary>The keys whose values differ between <paramref name="before"/> (null: none) and <paramref name="after"/>, with short texts.</summary>
    public static IReadOnlyList<(string Key, string? Before, string? After)> Changes(Preset? before, Preset after)
    {
        var now = before is null ? new JsonObject() : Comparable(before);
        var next = Comparable(after);
        var changes = new List<(string, string?, string?)>();
        foreach (var key in next.Select(pair => pair.Key).Union(now.Select(pair => pair.Key), StringComparer.Ordinal))
        {
            var a = now[key];
            var b = next[key];
            if (JsonNode.DeepEquals(a, b)) continue;
            changes.Add((key, Short(a), Short(b)));
        }
        return changes;
    }

    private static JsonObject Comparable(Preset preset)
    {
        var json = Node(preset);
        json.Remove("created");
        json.Remove("updated");
        return json;
    }

    private static IReadOnlyList<JsonNode?> Envelope(JsonObject envelope)
    {
        if (envelope["format"] is JsonValue format && format.TryGetValue<string>(out var name) && name != Format)
            throw new ArgumentException($"This is a \"{Cut(name)}\" file, not a PiSharp presets file.");
        if (envelope["version"] is JsonValue version && version.TryGetValue<int>(out var number) && number > Version)
            throw new ArgumentException($"This presets file is version {number}; this app reads version {Version}.");
        return envelope["presets"] is JsonArray list ? [.. list] : throw new ArgumentException("The file's presets are not a list.");
    }

    private static string? Short(JsonNode? node)
    {
        if (node is null) return null;
        var text = node is JsonValue value && value.TryGetValue<string>(out var plain) ? plain : node.ToJsonString(SettingsStore.JsonOptions);
        return text.Length > 160 ? text[..159] + "…" : text;
    }

    private static string Cut(string text) => text.Length > 60 ? text[..59] + "…" : text;

    private static string FirstSentence(string message)
    {
        var end = message.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? message[..(end + 1)] : message;
    }
}
