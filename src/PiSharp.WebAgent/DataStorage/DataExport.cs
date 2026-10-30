// Decision 0011 (run W4, unit R9): Settings › Data & storage's settings file. Export writes one JSON file: a header (format, version, when,
// which app, and a note that says no credential is in it) and one value per IDataExportPart that has something to export. Credentials are
// never exported, not even encrypted for this machine: a key or sign-in is entered again on the new machine (the page says so). Import reads
// such a file, asks every part what it would change (DataImportPlan, which the page shows key by key) and applies only the items the user
// kept; a part the file does not have is left alone, and anything in the file this app does not know (or that would hold a credential, such
// as a "credentials" key) is left out and listed.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>What an import would change, over every part in the file.</summary>
/// <param name="Items">The items that would change, in part order.</param>
/// <param name="Unchanged">How many of the file's items this app has already.</param>
/// <param name="Notes">What is left out, and why.</param>
/// <param name="ExportedAt">When the file was exported, when it says.</param>
/// <param name="App">Which app exported it, when it says.</param>
public sealed record DataImportPlan(IReadOnlyList<DataImportItem> Items, int Unchanged, IReadOnlyList<string> Notes, DateTimeOffset? ExportedAt, string? App);

/// <summary>What an import did.</summary>
/// <param name="Applied">The ids of the items imported.</param>
/// <param name="Failed">The items that could not be imported, with why.</param>
/// <param name="Notes">What the parts said (a key moved into the vault, a header left out).</param>
public sealed record DataImportResult(IReadOnlyList<string> Applied, IReadOnlyList<DataImportFailure> Failed, IReadOnlyList<string> Notes);

/// <summary>An export: the file's text and what is in it.</summary>
/// <param name="Text">The file (indented JSON).</param>
/// <param name="FileName"><c>pisharp-webagent-settings-YYYY-MM-DD.json</c>.</param>
/// <param name="Parts">The titles of the parts with something in the file.</param>
/// <param name="Notes">Parts left out, and why.</param>
public sealed record DataExportFile(string Text, string FileName, IReadOnlyList<string> Parts, IReadOnlyList<string> Notes);

/// <summary>Builds, reads and applies the settings file.</summary>
public sealed class DataExport(IEnumerable<IDataExportPart> parts, TimeProvider? time = null)
{
    /// <summary>The file's <c>format</c>.</summary>
    public const string Format = "pisharp-webagent-settings";

    /// <summary>The file's <c>version</c>.</summary>
    public const int Version = 1;

    /// <summary>What the file's <c>note</c> says.</summary>
    public const string Note = "Settings of PiSharp Web Agent. No credential is in this file: API keys, tokens and sign-ins are never exported. " +
        "Connect the providers again on the other machine.";

    /// <summary>The header keys of the file (everything else is a part).</summary>
    public static IReadOnlySet<string> HeaderKeys { get; } = new HashSet<string>(StringComparer.Ordinal) { "format", "version", "exportedAt", "app", "note" };

    private readonly IReadOnlyList<IDataExportPart> all = Distinct(parts);
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The parts, in registration order.</summary>
    public IReadOnlyList<IDataExportPart> Parts => all;

    /// <summary>The file name of an export made at <paramref name="at"/> (UTC date).</summary>
    public static string FileName(DateTimeOffset at) => $"pisharp-webagent-settings-{at.UtcDateTime:yyyy-MM-dd}.json";

    /// <summary>The app's name and version, as the file's <c>app</c> says.</summary>
    public static string App { get; } = "PiSharp Web Agent " + (typeof(DataExport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?.Split('+')[0] ?? typeof(DataExport).Assembly.GetName().Version?.ToString() ?? "");

    /// <summary>Exports every part.</summary>
    public DataExportFile Build()
    {
        var now = clock.GetUtcNow();
        var file = new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["exportedAt"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["app"] = App,
            ["note"] = Note,
        };
        var included = new List<string>();
        var notes = new List<string>();
        foreach (var part in all)
        {
            JsonNode? value;
            try { value = part.Export(); }
            catch (InvalidOperationException error)
            {
                notes.Add(error.Message);
                continue;
            }
            if (value is null) continue;
            file[part.Name] = value;
            included.Add(part.Title);
        }
        return new DataExportFile(file.ToJsonString(Indented), FileName(now), included, notes);
    }

    /// <summary>What importing <paramref name="file"/> would change.</summary>
    /// <exception cref="ArgumentException">It is not a settings file of this app (the message says why).</exception>
    public DataImportPlan Plan(JsonNode? file)
    {
        var document = Read(file);
        var items = new List<DataImportItem>();
        var notes = new List<string>();
        var unchanged = 0;
        foreach (var (key, value) in document)
        {
            if (HeaderKeys.Contains(key)) continue;
            var part = all.FirstOrDefault(candidate => candidate.Name == key);
            if (part is null)
            {
                notes.Add($"The file's “{Cut(key)}” isn't something this app imports, so it is left out.");
                continue;
            }
            try
            {
                var plan = part.Plan(value);
                items.AddRange(plan.Items);
                unchanged += plan.Unchanged;
                notes.AddRange(plan.Notes);
            }
            catch (ArgumentException error)
            {
                notes.Add($"The {part.Title} in the file are left out: {error.Message}");
            }
        }
        DateTimeOffset? exported = document["exportedAt"] is JsonValue at && at.TryGetValue<string>(out var text) &&
            DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
        var app = document["app"] is JsonValue name && name.TryGetValue<string>(out var appText) ? Cut(appText, 80) : null;
        return new DataImportPlan(items, unchanged, notes, exported, app);
    }

    /// <summary>Imports the items of <paramref name="file"/> whose ids are in <paramref name="accepted"/>.</summary>
    /// <exception cref="ArgumentException">It is not a settings file of this app, or an id is not one of its items.</exception>
    public async Task<DataImportResult> ApplyAsync(JsonNode? file, IReadOnlyCollection<string> accepted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        var plan = Plan(file);
        var known = plan.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (accepted.FirstOrDefault(id => !known.Contains(id)) is { } unknown)
            throw new ArgumentException($"“{Cut(unknown)}” is not a change this file makes. Review the file again.");
        var document = Read(file);
        var applied = new List<string>();
        var failed = new List<DataImportFailure>();
        var notes = new List<string>();
        var chosen = accepted.ToHashSet(StringComparer.Ordinal);
        foreach (var part in all)
        {
            var ids = plan.Items.Where(item => item.Part == part.Name && chosen.Contains(item.Id)).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (ids.Count == 0) continue;
            var result = await part.ImportAsync(document[part.Name], ids, cancellationToken).ConfigureAwait(false);
            applied.AddRange(result.Applied);
            failed.AddRange(result.Failed);
            notes.AddRange(result.Notes);
        }
        return new DataImportResult(applied, failed, notes);
    }

    /// <summary><paramref name="file"/> as a settings file of this app.</summary>
    /// <exception cref="ArgumentException">It is not one.</exception>
    public static JsonObject Read(JsonNode? file)
    {
        if (file is not JsonObject document) throw new ArgumentException("That file is not a settings export: it is not a JSON object.");
        if (document["format"] is not JsonValue format || !format.TryGetValue<string>(out var name) || name != Format)
            throw new ArgumentException("That file is not a settings export of PiSharp Web Agent (its format is not " + Format + ").");
        if (document["version"] is not JsonValue version || !version.TryGetValue<int>(out var number))
            throw new ArgumentException("That settings export has no version.");
        if (number > Version) throw new ArgumentException($"That settings export is version {number}, newer than this app reads ({Version}). Update the app first.");
        if (number < 1) throw new ArgumentException("That settings export has an invalid version.");
        return document;
    }

    private static IReadOnlyList<IDataExportPart> Distinct(IEnumerable<IDataExportPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var list = new List<IDataExportPart>();
        foreach (var part in parts)
        {
            if (HeaderKeys.Contains(part.Name)) throw new InvalidOperationException($"An export part may not be named {part.Name}.");
            if (list.All(other => other.Name != part.Name)) list.Add(part);
        }
        return list;
    }

    private static string Cut(string text, int length = 60) => text.Length > length ? text[..(length - 1)] + "…" : text;
}
