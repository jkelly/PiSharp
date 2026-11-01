// PiSharp-only (decision 0008). The hub's registry of devices, after durable-pi's NetworkRegistry (apps/dpi-sdk/src/node/registry.ts, MIT):
// each device's id, address, source and token, the identity it first answered with, and its last status, description and tree, so an offline
// device still shows the sessions it last reported. Tokens are encrypted with ASP.NET Data Protection and never leave this process. The
// same database keeps UI state (the selection, panel widths) by key. Devices from a config file or this computer's `pisharp durable serve`
// are read-only in the UI; devices added in the UI can be edited and removed.
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using PiSharp.Durable.Client;

namespace PiSharp.Durable.Web.Hub;

/// <summary>A registry row; <see cref="HasToken"/> says whether a token is stored, never the token.</summary>
public sealed record DeviceRecord(string Id, string Name, string Kind, string Url, string Source, bool HasToken, string? DeviceUid, string? Status,
    string? Error, long? SeenAt, DeviceInfo? Info, DeviceTree? Tree);

/// <summary>The device registry and UI state store.</summary>
public sealed partial class DeviceRegistry : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly IDataProtector protector;
    private readonly Lock gate = new();

    public DeviceRegistry(HubOptions options, IDataProtectionProvider protection)
    {
        Directory.CreateDirectory(options.DataDirectory);
        Path = System.IO.Path.Join(options.DataDirectory, "network.sqlite");
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, url TEXT NOT NULL, source TEXT NOT NULL,
              token TEXT, device_uid TEXT, status TEXT, error TEXT, seen_at INTEGER, info TEXT, tree TEXT, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ui_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
        protector = protection.CreateProtector("PiSharp.Durable.Web.DeviceTokens.v1");
    }

    /// <summary>The database file.</summary>
    public string Path { get; }

    /// <summary>Raised after a device is added, edited or removed.</summary>
    public event Action<string>? Changed;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex IdPattern();

    /// <summary>Whether <paramref name="id"/> is a valid device id: lower-case letters, digits and dashes, at most 32.</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>An absolute http or https address without a path, or an error.</summary>
    public static string NormalizeUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host))
            throw new ArgumentException("The address must be an http:// or https:// URL, such as http://build-box.lan:4780.");
        return parsed.GetLeftPart(UriPartial.Authority);
    }

    public IReadOnlyList<DeviceRecord> List()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, kind, url, source, token, device_uid, status, error, seen_at, info, tree FROM devices ORDER BY created_at, id;";
            using var reader = command.ExecuteReader();
            var rows = new List<DeviceRecord>();
            while (reader.Read()) rows.Add(Read(reader));
            return rows;
        }
    }

    public DeviceRecord? Get(string id) => List().FirstOrDefault(device => device.Id == id);

    /// <summary>The device's token, decrypted; null when none is stored or it cannot be decrypted (new keys).</summary>
    public string? Token(string id)
    {
        string? stored;
        lock (gate) stored = Scalar("SELECT token FROM devices WHERE id = $id;", ("$id", id)) as string;
        if (string.IsNullOrEmpty(stored)) return null;
        try { return protector.Unprotect(stored); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    /// <summary>Adds a device, or replaces one from the same source.</summary>
    /// <exception cref="ArgumentException">An invalid id or address, or an id another source uses.</exception>
    public void Upsert(string id, string name, string kind, string url, string source, string? token)
    {
        if (!IsValidId(id)) throw new ArgumentException("The name must be lower-case letters, digits and dashes (at most 32), starting with a letter or digit.");
        var address = NormalizeUrl(url);
        lock (gate)
        {
            var existing = Scalar("SELECT source FROM devices WHERE id = $id;", ("$id", id)) as string;
            if (existing is not null && existing != source) throw new ArgumentException($"A device called {id} already exists.");
            Execute("""
                INSERT INTO devices (id, name, kind, url, source, token, created_at) VALUES ($id, $name, $kind, $url, $source, $token, $now)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, kind = excluded.kind, url = excluded.url,
                  token = COALESCE(excluded.token, devices.token),
                  device_uid = CASE WHEN devices.url = excluded.url THEN devices.device_uid ELSE NULL END;
                """,
                ("$id", id), ("$name", string.IsNullOrWhiteSpace(name) ? id : name.Trim()), ("$kind", kind), ("$url", address), ("$source", source),
                ("$token", token is null ? DBNull.Value : protector.Protect(token)), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        Changed?.Invoke(id);
    }

    /// <summary>Edits a UI device's address or token; null keeps a value. A new address forgets the device's identity.</summary>
    public void Update(string id, string? url, string? token)
    {
        var device = Get(id) ?? throw new KeyNotFoundException($"No device {id}.");
        if (device.Source != "ui" && url is not null) throw new InvalidOperationException($"{id} comes from {SourceText(device.Source)}; edit it there.");
        lock (gate)
        {
            if (url is not null)
                Execute("UPDATE devices SET url = $url, device_uid = NULL WHERE id = $id;", ("$id", id), ("$url", NormalizeUrl(url)));
            if (token is not null) Execute("UPDATE devices SET token = $token WHERE id = $id;", ("$id", id), ("$token", protector.Protect(token)));
        }
        Changed?.Invoke(id);
    }

    public void Remove(string id)
    {
        var device = Get(id) ?? throw new KeyNotFoundException($"No device {id}.");
        if (device.Source != "ui") throw new InvalidOperationException($"{id} comes from {SourceText(device.Source)}; remove it there.");
        lock (gate) Execute("DELETE FROM devices WHERE id = $id;", ("$id", id));
        Changed?.Invoke(id);
    }

    /// <summary>Removes a config device the config no longer declares.</summary>
    public void RemoveConfig(string id)
    {
        lock (gate) Execute("DELETE FROM devices WHERE id = $id AND source = 'config';", ("$id", id));
        Changed?.Invoke(id);
    }

    public void LearnUid(string id, string uid)
    {
        lock (gate) Execute("UPDATE devices SET device_uid = $uid WHERE id = $id AND device_uid IS NULL;", ("$id", id), ("$uid", uid));
    }

    public void ForgetUid(string id)
    {
        lock (gate) Execute("UPDATE devices SET device_uid = NULL WHERE id = $id;", ("$id", id));
        Changed?.Invoke(id);
    }

    /// <summary>Records the device's last status, and what it last reported.</summary>
    public void RecordStatus(string id, string status, string? error, long? seenAt, DeviceInfo? info, DeviceTree? tree)
    {
        lock (gate)
            Execute("""
                UPDATE devices SET status = $status, error = $error, seen_at = COALESCE($seen, seen_at),
                  info = COALESCE($info, info), tree = COALESCE($tree, tree) WHERE id = $id;
                """,
                ("$id", id), ("$status", status), ("$error", (object?)error ?? DBNull.Value), ("$seen", (object?)seenAt ?? DBNull.Value),
                ("$info", info is null ? DBNull.Value : DurableWire.Serialize(info)), ("$tree", tree is null ? DBNull.Value : DurableWire.Serialize(tree)));
    }

    public string? UiGet(string key)
    {
        lock (gate) return Scalar("SELECT value FROM ui_state WHERE key = $key;", ("$key", key)) as string;
    }

    public void UiSet(string key, string? value)
    {
        lock (gate)
        {
            if (value is null) Execute("DELETE FROM ui_state WHERE key = $key;", ("$key", key));
            else Execute("INSERT INTO ui_state (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", ("$key", key), ("$value", value));
        }
    }

    public static string SourceText(string source) => source switch
    {
        "config" => "dpi-network.yaml",
        "local" => "this computer's pisharp durable serve",
        _ => "the UI",
    };

    private static DeviceRecord Read(SqliteDataReader reader)
    {
        static T? Json<T>(SqliteDataReader reader, int column) where T : class
        {
            if (reader.IsDBNull(column)) return null;
            try { return JsonSerializer.Deserialize<T>(reader.GetString(column), DurableWire.Options); }
            catch (JsonException) { return null; }
        }
        return new DeviceRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            !reader.IsDBNull(5) && reader.GetString(5).Length > 0, reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), Json<DeviceInfo>(reader, 10), Json<DeviceTree>(reader, 11));
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar() is var result && result is DBNull ? null : result;
    }

    public void Dispose() => connection.Dispose();
}
