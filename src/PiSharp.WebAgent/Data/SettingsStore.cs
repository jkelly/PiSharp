// Decision 0011 (run W2, WU4): ISettingsStore over webagent.db. One row per section (settings: section, value as camelCase JSON, updated_at),
// read once and cached; writes go through one gate, so UpdateAsync's read-change-write cannot lose another save. A row that no longer
// deserializes or validates (an older or hand-edited value) reads as the section's defaults and is logged, never thrown at a page.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Data;

/// <summary>Typed settings sections stored as JSON in webagent.db.</summary>
public sealed class SettingsStore(WebAgentDb db, ILogger<SettingsStore>? logger = null) : ISettingsStore
{
    /// <summary>300: <c>settings (section, value, updated_at)</c>, the first migration of the settings area.</summary>
    public static SqlMigration Migration { get; } = new(300, "settings: sections",
        "CREATE TABLE IF NOT EXISTS settings (section TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at INTEGER NOT NULL);");

    /// <summary>How sections are written: camelCase names, enums as camelCase strings (<c>"allowList"</c>), nulls left out.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, object> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ILogger log = logger ?? NullLogger<SettingsStore>.Instance;

    /// <inheritdoc/>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <inheritdoc/>
    public T Get<T>() where T : class, ISettingsSection<T> => (T)cache.GetOrAdd(T.SectionName, _ => Load<T>());

    /// <inheritdoc/>
    public async Task<T> SaveAsync<T>(T value, CancellationToken cancellationToken = default) where T : class, ISettingsSection<T>
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Normalize();
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Write(normalized); }
        finally { writes.Release(); }
        Changed?.Invoke(this, new SettingsChangedEventArgs(T.SectionName, normalized));
        return normalized;
    }

    /// <inheritdoc/>
    public async Task<T> UpdateAsync<T>(Func<T, T> change, CancellationToken cancellationToken = default) where T : class, ISettingsSection<T>
    {
        ArgumentNullException.ThrowIfNull(change);
        T normalized;
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            normalized = (change(Get<T>()) ?? throw new ArgumentException("The change returned no settings.")).Normalize();
            Write(normalized);
        }
        finally { writes.Release(); }
        Changed?.Invoke(this, new SettingsChangedEventArgs(T.SectionName, normalized));
        return normalized;
    }

    /// <inheritdoc/>
    public async Task<T> ResetAsync<T>(CancellationToken cancellationToken = default) where T : class, ISettingsSection<T>
    {
        var defaults = T.CreateDefault().Normalize();
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            db.Execute("DELETE FROM settings WHERE section = $section;", ("$section", T.SectionName));
            cache[T.SectionName] = defaults;
        }
        finally { writes.Release(); }
        Changed?.Invoke(this, new SettingsChangedEventArgs(T.SectionName, defaults));
        return defaults;
    }

    private void Write<T>(T normalized) where T : class, ISettingsSection<T>
    {
        db.Execute("""
            INSERT INTO settings (section, value, updated_at) VALUES ($section, $value, $at)
            ON CONFLICT(section) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """,
            ("$section", T.SectionName), ("$value", JsonSerializer.Serialize(normalized, JsonOptions)), ("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        cache[T.SectionName] = normalized;
    }

    private T Load<T>() where T : class, ISettingsSection<T>
    {
        if (db.Scalar("SELECT value FROM settings WHERE section = $section;", ("$section", T.SectionName)) is not string json)
            return T.CreateDefault().Normalize();
        try
        {
            return (JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new JsonException("The stored value is null.")).Normalize();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        {
            log.LogWarning(error, "Settings section {Section} could not be read; using its defaults.", T.SectionName);
            return T.CreateDefault().Normalize();
        }
    }
}
