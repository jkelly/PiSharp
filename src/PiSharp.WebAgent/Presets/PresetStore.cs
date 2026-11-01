// Decision 0011 (run W4, R8): presets in webagent.db (migration 400 of the presets range 400-499). One row per preset: its id, the preset as
// JSON (SettingsStore.JsonOptions, exactly the shape an export holds plus the use counts) and when it was created and changed. Ids are made
// from the name when a preset is first saved (lower-case words joined by '-', "-2", "-3" on a clash) and kept when it is renamed, so a link
// such as /playground?preset={id} keeps working.
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Presets;

/// <summary>Presets in webagent.db. A singleton; safe to call from many threads.</summary>
public sealed class PresetStore
{
    /// <summary>The table.</summary>
    public static SqlMigration Migration { get; } = new(400, "presets: saved presets", """
        CREATE TABLE presets (
            id TEXT PRIMARY KEY,
            data TEXT NOT NULL,
            created INTEGER NOT NULL,
            updated INTEGER NOT NULL
        );
        CREATE INDEX presets_updated ON presets (updated);
        """);

    private readonly WebAgentDb db;
    private readonly ILogger? log;
    private readonly TimeProvider time;
    private readonly Lock gate = new();

    /// <summary>A store over <paramref name="db"/>.</summary>
    public PresetStore(WebAgentDb db, ILogger<PresetStore>? log = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
        this.log = log;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a preset is saved or deleted.</summary>
    public event EventHandler? Changed;

    /// <summary>Preset <paramref name="id"/>, or null.</summary>
    public Preset? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return db.Query("SELECT data FROM presets WHERE id = $id;", reader => Parse(reader.GetString(0)), ("$id", id)).FirstOrDefault();
    }

    /// <summary>Every preset, most recently changed first.</summary>
    public IReadOnlyList<Preset> List() =>
        [.. db.Query("SELECT data FROM presets ORDER BY updated DESC, id;", reader => Parse(reader.GetString(0))).OfType<Preset>()];

    /// <summary>How many presets there are.</summary>
    public int Count() => Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM presets;") ?? 0L, CultureInfo.InvariantCulture);

    /// <summary>Stores a new preset under a fresh id made from its name (or its own id, when free).</summary>
    public Preset Add(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        lock (gate)
        {
            var id = FreeId(string.IsNullOrEmpty(preset.Id) ? Slug(preset.Name) : preset.Id);
            var now = time.GetUtcNow();
            return Write(preset with { Id = id, Created = preset.Created == default ? now : preset.Created, Updated = now });
        }
    }

    /// <summary>Stores <paramref name="preset"/> under its id, replacing a preset of that id (an import keeping an id).</summary>
    public Preset Put(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrEmpty(preset.Id)) throw new ArgumentException("The preset has no id.");
        lock (gate)
        {
            var now = time.GetUtcNow();
            return Write(preset with { Created = preset.Created == default ? now : preset.Created, Updated = now });
        }
    }

    /// <summary>Stores several new presets in one transaction (an import): all or none.</summary>
    public IReadOnlyList<Preset> AddAll(IReadOnlyList<Preset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        IReadOnlyList<Preset> stored;
        lock (gate)
        {
            var now = time.GetUtcNow();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<Preset>();
            foreach (var preset in presets)
            {
                var id = FreeId(string.IsNullOrEmpty(preset.Id) ? Slug(preset.Name) : preset.Id, taken);
                taken.Add(id);
                rows.Add(preset with { Id = id, Created = preset.Created == default ? now : preset.Created, Updated = now });
            }
            stored = db.Transaction((connection, transaction) =>
            {
                foreach (var row in rows) WebAgentDb.Execute(connection, transaction, Insert, Parameters(row));
                return (IReadOnlyList<Preset>)rows;
            });
        }
        if (stored.Count > 0) Changed?.Invoke(this, EventArgs.Empty);
        return stored;
    }

    /// <summary>Changes preset <paramref name="id"/> with <paramref name="change"/> (read, change and write under one lock).</summary>
    /// <param name="id">The preset.</param>
    /// <param name="change">The change.</param>
    /// <param name="touch">Whether the change counts as an update (false for a use count).</param>
    /// <returns>The preset as stored, or null when there is no such preset.</returns>
    public Preset? Update(string id, Func<Preset, Preset> change, bool touch = true)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            if (Get(id) is not { } preset) return null;
            var next = change(preset) with { Id = preset.Id, Created = preset.Created };
            return Write(touch ? next with { Updated = time.GetUtcNow() } : next with { Updated = preset.Updated });
        }
    }

    /// <summary>Removes preset <paramref name="id"/>; true when it was there.</summary>
    public bool Delete(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        bool removed;
        lock (gate) removed = db.Execute("DELETE FROM presets WHERE id = $id;", ("$id", id)) > 0;
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>An id from <paramref name="name"/>: lower-case letters and digits, words joined by '-', at most 48 characters.</summary>
    public static string Slug(string? name)
    {
        var builder = new StringBuilder();
        foreach (var raw in (name ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(raw);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            if (builder.Length >= 48) break;
        }
        var slug = builder.ToString().Trim('-');
        return Usable(slug.Length == 0 ? "preset" : slug);
    }

    /// <summary><paramref name="id"/>, or <c>{id}-preset</c> when it is one of <see cref="Reserved"/>.</summary>
    public static string Usable(string id) => Reserved.Contains(id) ? id + "-preset" : id;

    /// <summary>Ids a preset never gets: the words of the Presets routes (/api/presets/export, /presets/grid...).</summary>
    public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(["export", "import", "preview", "grid", "picker", "new"], StringComparer.Ordinal);

    private const string Insert = """
        INSERT INTO presets (id, data, created, updated) VALUES ($id, $data, $created, $updated)
        ON CONFLICT (id) DO UPDATE SET data = excluded.data, updated = excluded.updated;
        """;

    private static (string, object?)[] Parameters(Preset preset) =>
    [
        ("$id", preset.Id), ("$data", JsonSerializer.Serialize(preset, SettingsStore.JsonOptions)),
        ("$created", preset.Created.ToUnixTimeMilliseconds()), ("$updated", preset.Updated.ToUnixTimeMilliseconds()),
    ];

    private Preset Write(Preset preset)
    {
        db.Execute(Insert, Parameters(preset));
        Changed?.Invoke(this, EventArgs.Empty);
        return preset;
    }

    private string FreeId(string wanted, IReadOnlySet<string>? taken = null)
    {
        var stem = wanted.Length > 56 ? wanted[..56].TrimEnd('-') : wanted;
        bool Used(string id) => Reserved.Contains(id) || taken?.Contains(id) == true || db.Scalar("SELECT 1 FROM presets WHERE id = $id;", ("$id", id)) is not null;
        if (!Used(stem)) return stem;
        for (var index = 2; ; index++)
        {
            var id = $"{stem}-{index.ToString(CultureInfo.InvariantCulture)}";
            if (!Used(id)) return id;
        }
    }

    private Preset? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Preset>(json, SettingsStore.JsonOptions); }
        catch (JsonException error)
        {
            log?.LogWarning(error, "A preset could not be read.");
            return null;
        }
    }
}
