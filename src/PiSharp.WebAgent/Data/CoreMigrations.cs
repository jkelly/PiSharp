// Decision 0011 (run W2, WU4): the core area's migrations (ids 1-99). app_info keeps facts about the data folder itself, for diagnostics and a
// later About page: when it was created and the layout version (1: device\, keys\, webagent.db, credentials.json).
namespace PiSharp.WebAgent.Data;

/// <summary>The core area's webagent.db migrations.</summary>
public static class CoreMigrations
{
    /// <summary>1: <c>app_info (key, value)</c>, seeded with <c>created_at</c> (UTC, ISO 8601) and <c>data_layout</c> = 1.</summary>
    public static SqlMigration AppInfo { get; } = new(1, "core: app info", """
        CREATE TABLE IF NOT EXISTS app_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT OR IGNORE INTO app_info (key, value) VALUES ('created_at', strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));
        INSERT OR IGNORE INTO app_info (key, value) VALUES ('data_layout', '1');
        """);

    /// <summary>Every core migration.</summary>
    public static IReadOnlyList<IWebAgentMigration> All { get; } = [AppInfo];
}
