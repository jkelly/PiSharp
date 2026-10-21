// PiSharp-only (decision 0010, M6): `pisharp durable doctor`'s store checks. Checking is read-only: every database opens with
// SQLITE_OPEN_READONLY, nothing is created (the lock is only tried when its file exists, and released at once), and a damaged or old store
// becomes a failed or skipped check rather than an exception. Messages carry counts, IDs and paths, never secrets or record contents.
// Repair is separate and explicit: with the device stopped it migrates an old catalog (after the catalog's own backup copy) and collects
// unreferenced blobs and stale temporary files; it never deletes a session, a record or a referenced blob.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace PiSharp.Durable.Hosting;

/// <summary>One doctor check.</summary>
/// <param name="Id">The check's ID.</param>
/// <param name="Status"><c>ok</c>, <c>info</c>, <c>warn</c>, <c>fail</c> or <c>skip</c>.</param>
/// <param name="Message">What was found.</param>
/// <param name="Fix">What to do about it, when something can be done.</param>
public sealed record DoctorCheck(string Id, string Status, string Message, string? Fix = null)
{
    /// <summary>The check as JSON.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["id"] = Id, ["status"] = Status, ["message"] = Message };
        if (Fix is not null) json["fix"] = Fix;
        return json;
    }
}

/// <summary>What the doctor checks beyond the defaults.</summary>
public sealed record DoctorOptions
{
    /// <summary>Hash every blob against its name (reads every byte), not only its size.</summary>
    public bool VerifyBlobs { get; init; }

    /// <summary>Less free disk than this is a warning.</summary>
    public long MinimumFreeBytes { get; init; } = 1L << 30;

    /// <summary>The clock of age checks.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>Checks and repairs a device's data directory.</summary>
public static class DeviceDoctor
{
    /// <summary>Checks the data directory without changing it.</summary>
    public static IReadOnlyList<DoctorCheck> Check(string dataDirectory, DoctorOptions? options = null)
    {
        options ??= new DoctorOptions();
        var data = Path.GetFullPath(dataDirectory);
        var checks = new List<DoctorCheck>();
        if (!Directory.Exists(data))
        {
            checks.Add(new DoctorCheck("data-directory", "warn", $"{data} does not exist: no device has run here.", "pisharp durable serve creates it."));
            return checks;
        }
        checks.Add(new DoctorCheck("data-directory", "ok", data));
        checks.Add(Running(data));
        checks.Add(Disk(data, options.MinimumFreeBytes));
        var catalogPath = Path.Join(data, "catalog.sqlite");
        if (!File.Exists(catalogPath))
        {
            checks.Add(new DoctorCheck("catalog", "warn", "No catalog.sqlite: the device has not started here yet.", "pisharp durable serve creates it."));
            return checks;
        }
        SqliteConnection catalog;
        try
        {
            catalog = DeviceBackup.OpenReadOnly(catalogPath);
        }
        catch (SqliteException error)
        {
            checks.Add(new DoctorCheck("catalog", "fail", "catalog.sqlite cannot be opened read-only: " + error.Message, RestoreFix));
            return checks;
        }
        using (catalog)
        {
            long version;
            try
            {
                version = Convert.ToInt64(Scalar(catalog, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
                if (Scalar(catalog, "PRAGMA quick_check;") is string result && result != "ok")
                {
                    checks.Add(new DoctorCheck("catalog", "fail", "catalog.sqlite fails its integrity check: " + Line(result), RestoreFix));
                    return checks;
                }
            }
            catch (SqliteException error)
            {
                checks.Add(new DoctorCheck("catalog", "fail", "catalog.sqlite is not a readable SQLite database: " + error.Message, RestoreFix));
                return checks;
            }
            checks.Add(version switch
            {
                0 => new DoctorCheck("catalog", "warn", "catalog.sqlite has no schema yet.", "Start the device to create it."),
                > SessionCatalog.SchemaVersion => new DoctorCheck("catalog", "fail",
                    $"catalog.sqlite has schema version {version}; this PiSharp reads version {SessionCatalog.SchemaVersion}.", "Update PiSharp, or restore an older backup."),
                < SessionCatalog.SchemaVersion => new DoctorCheck("catalog", "warn",
                    $"catalog.sqlite has schema version {version}; the device migrates it to {SessionCatalog.SchemaVersion} when it starts, after copying it to catalog.v{version}.bak.sqlite.",
                    "pisharp durable doctor --repair migrates it now (the device must be stopped)."),
                _ => new DoctorCheck("catalog", "ok", $"Schema version {version}; integrity ok."),
            });
            if (version is 0 or > SessionCatalog.SchemaVersion) return checks;
            try
            {
                checks.Add(Sessions(catalog, data, version));
                checks.AddRange(Application(catalog, data, version, options));
            }
            catch (SqliteException error)
            {
                checks.Add(new DoctorCheck("catalog-records", "fail", "Reading the catalog failed: " + error.Message, RestoreFix));
            }
        }
        if (Directory.EnumerateFiles(data, "catalog.v*.bak*.sqlite").Count() is var copies and > 0)
            checks.Add(new DoctorCheck("migration-copies", "info", $"{copies} catalog copies from earlier migrations (catalog.v*.bak.sqlite) are kept beside the catalog.",
                "Delete them yourself once the device runs well on the new version."));
        return checks;
    }

    /// <summary>Repairs what can be repaired without a decision: migrates an old catalog (after its backup copy) and collects unreferenced
    /// blobs past <paramref name="blobGrace"/> and stale temporary files. Returns what it did.</summary>
    /// <exception cref="DeviceLockedException">The device is running.</exception>
    public static IReadOnlyList<string> Repair(string dataDirectory, TimeSpan? blobGrace = null, TimeProvider? time = null)
    {
        var data = Path.GetFullPath(dataDirectory);
        var catalogPath = Path.Join(data, "catalog.sqlite");
        if (!File.Exists(catalogPath)) return ["Nothing to repair: there is no catalog."];
        using var deviceLock = FileLock.TryAcquire(Path.Join(data, "catalog.lock"), $"doctor --repair, pid {Environment.ProcessId}")
            ?? throw new DeviceLockedException($"The device at {data} is running. Stop it before repairing.");
        var actions = new List<string>();
        long before;
        using (var probe = DeviceBackup.OpenReadOnly(catalogPath))
        {
            before = Convert.ToInt64(Scalar(probe, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        }
        using var catalog = SessionCatalog.Open(catalogPath);
        if (before is > 0 and < SessionCatalog.SchemaVersion)
            actions.Add($"Migrated the catalog from schema version {before} to {SessionCatalog.SchemaVersion}; the old one is in catalog.v{before}.bak.sqlite.");
        var collected = new BlobStore(Path.Join(data, "blobs"), catalog, time ?? TimeProvider.System).Collect(blobGrace ?? TimeSpan.FromDays(1));
        actions.Add($"Collected {collected.Blobs} unreferenced blobs ({collected.Bytes} bytes), {collected.OrphanFiles} files the catalog does not know and " +
            $"{collected.TemporaryFiles} stale temporary files.");
        return actions;
    }

    private const string RestoreFix = "Stop the device and restore the newest backup into a new data directory (pisharp durable restore).";

    private static DoctorCheck Running(string data)
    {
        var path = Path.Join(data, "catalog.lock");
        if (!File.Exists(path)) return new DoctorCheck("device", "ok", "Not running (no device has held the lock).");
        using var held = FileLock.TryAcquire(path);
        return held is null
            ? new DoctorCheck("device", "info", "Running: a process holds catalog.lock. Checks read a live store; repair and backup need it stopped.")
            : new DoctorCheck("device", "ok", "Not running.");
    }

    private static DoctorCheck Disk(string data, long minimum)
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(data)!).AvailableFreeSpace;
            return free < minimum
                ? new DoctorCheck("disk", "warn", $"{free / (1 << 20)} MiB free on {Path.GetPathRoot(data)}.", "Free disk space, or run pisharp durable retention purge.")
                : new DoctorCheck("disk", "ok", $"{free / (1 << 20)} MiB free.");
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new DoctorCheck("disk", "skip", "Free space unknown: " + error.Message);
        }
    }

    private static DoctorCheck Sessions(SqliteConnection catalog, string data, long version)
    {
        var rows = Rows(catalog, version >= 3 ? "SELECT id, archive_path FROM sessions ORDER BY created_at, id;" : "SELECT id, NULL FROM sessions ORDER BY created_at, id;",
            reader => (Id: reader.GetString(0), Archived: !reader.IsDBNull(1)));
        var problems = new List<string>();
        foreach (var (id, archived) in rows.Where(row => !row.Archived))
        {
            var path = Path.Join(data, "sessions", id, "agent.sqlite");
            if (!File.Exists(path)) problems.Add($"{id}: agent.sqlite is missing");
            else if (DeviceBackup.Integrity(path) is { } damage) problems.Add($"{id}: {damage}");
        }
        var known = rows.Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sessions = Path.Join(data, "sessions");
        var orphans = Directory.Exists(sessions) ? Directory.EnumerateDirectories(sessions).Select(Path.GetFileName).Where(name => !known.Contains(name!)).ToList() : [];
        if (problems.Count > 0)
            return new DoctorCheck("sessions", "fail", $"{problems.Count} of {rows.Count} sessions are damaged: " + string.Join("; ", problems.Take(5)) +
                (orphans.Count > 0 ? $"; {orphans.Count} session directories are not in the catalog" : ""),
                "The device lists a damaged session as unavailable and keeps the others; restore a backup to recover it.");
        if (orphans.Count > 0)
            return new DoctorCheck("sessions", "warn", $"{rows.Count} sessions ok; {orphans.Count} directories under sessions/ are not in the catalog: " +
                string.Join(", ", orphans.Take(5)), "They are ignored; move them out of the data directory to clear this warning.");
        return new DoctorCheck("sessions", "ok", $"{rows.Count} sessions ({rows.Count(row => row.Archived)} archiving); every database passes its integrity check.");
    }

    private static IEnumerable<DoctorCheck> Application(SqliteConnection catalog, string data, long version, DoctorOptions options)
    {
        var now = options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (version >= 4)
        {
            var schedules = Rows(catalog, "SELECT value FROM job_schedules ORDER BY id;", reader => JsonNode.Parse(reader.GetString(0)) as JsonObject);
            var failing = schedules.Where(schedule => schedule?["lastError"] is JsonValue).Select(schedule => schedule!["id"]?.GetValue<string>()).ToList();
            var enabled = schedules.Count(schedule => schedule?["enabled"]?.GetValue<bool>() == true);
            yield return failing.Count > 0
                ? new DoctorCheck("schedules", "warn", $"{schedules.Count} schedules ({enabled} enabled); the last tick of {string.Join(", ", failing.Take(5))} failed.",
                    "pisharp durable schedules list shows the errors.")
                : new DoctorCheck("schedules", "ok", $"{schedules.Count} schedules ({enabled} enabled).");
            var credentials = Rows(catalog, "SELECT expires_at FROM trigger_credentials;", reader => reader.GetInt64(0));
            yield return new DoctorCheck("trigger-credentials", "ok",
                $"{credentials.Count(at => at > now)} active and {credentials.Count(at => at <= now)} expired trigger credentials (stored as hashes).");
        }
        if (version < 5)
        {
            yield return new DoctorCheck("application-records", "skip", $"Catalog version {version} predates application records (version 5).");
            yield break;
        }
        var uncertain = Count(catalog, "SELECT COUNT(*) FROM budget_reservations WHERE status = 'uncertain';");
        var stuck = Count(catalog, "SELECT COUNT(*) FROM budget_reservations WHERE status = 'reserved' AND updated_at < $at;", ("$at", now - 86_400_000));
        yield return uncertain + stuck > 0
            ? new DoctorCheck("reservations", "warn", $"{uncertain} uncertain reservations (a paid call whose outcome is unknown) and {stuck} reservations open for over a day.",
                "pisharp durable budgets show lists them; pisharp durable budgets resolve settles or releases one.")
            : new DoctorCheck("reservations", "ok", "No uncertain or long-open reservations.");
        yield return Blobs(catalog, data, options);
        var pastRetention = Count(catalog, "SELECT COUNT(*) FROM budget_reservations WHERE status IN ('settled', 'released') AND updated_at < $at;", ("$at", now - 90L * 86_400_000)) +
            Count(catalog, "SELECT COUNT(*) FROM judgment_cache WHERE expires_at < $now;", ("$now", now)) +
            (version >= 6 ? Count(catalog, "SELECT COUNT(*) FROM retrieval_cache WHERE fetched_at < $at;", ("$at", now - 30L * 86_400_000)) : 0);
        yield return pastRetention > 0
            ? new DoctorCheck("retention", "info", $"About {pastRetention} catalog records are past their retention.", "pisharp durable retention show, then retention purge.")
            : new DoctorCheck("retention", "ok", "No catalog records past retention.");
        if (version < 6) yield break;
        var failingSources = Rows(catalog, """
            SELECT s.id, st.failures FROM sources s JOIN source_state st ON st.source_id = s.id WHERE s.deleted_at IS NULL AND st.failures > 0 ORDER BY st.failures DESC, s.id;
            """, reader => $"{reader.GetString(0)} ({reader.GetInt64(1)} failures)");
        var sources = Count(catalog, "SELECT COUNT(*) FROM sources WHERE deleted_at IS NULL;");
        yield return failingSources.Count > 0
            ? new DoctorCheck("sources", "warn", $"{failingSources.Count} of {sources} sources are failing: {string.Join(", ", failingSources.Take(5))}.",
                "pisharp durable sources health <id> shows the last error.")
            : new DoctorCheck("sources", "ok", $"{sources} sources; none failing.");
    }

    private static DoctorCheck Blobs(SqliteConnection catalog, string data, DoctorOptions options)
    {
        var rows = Rows(catalog, "SELECT hash, size FROM blobs;", reader => (Hash: reader.GetString(0), Size: reader.GetInt64(1)));
        var missing = new List<string>();
        var damaged = new List<string>();
        var root = Path.Join(data, "blobs", "sha256");
        foreach (var (hash, size) in rows)
        {
            var hex = hash.StartsWith("sha256:", StringComparison.Ordinal) && hash.Length == 71 ? hash[7..] : null;
            var path = hex is null ? null : Path.Join(root, hex[..2], hex);
            if (path is null || !File.Exists(path)) missing.Add(hash);
            else if (new FileInfo(path).Length != size) damaged.Add(hash);
            else if (options.VerifyBlobs)
            {
                using var stream = File.OpenRead(path);
                if (Convert.ToHexStringLower(SHA256.HashData(stream)) != hex) damaged.Add(hash);
            }
        }
        var known = rows.Select(row => row.Hash.Length == 71 ? row.Hash[7..] : row.Hash).ToHashSet(StringComparer.Ordinal);
        var orphans = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count(file => !known.Contains(Path.GetFileName(file))) : 0;
        var unreferenced = Count(catalog, "SELECT COUNT(*) FROM blobs b WHERE NOT EXISTS (SELECT 1 FROM blob_refs r WHERE r.hash = b.hash);");
        var bytes = rows.Sum(row => row.Size);
        if (missing.Count + damaged.Count > 0)
            return new DoctorCheck("blobs", "fail", $"{missing.Count} blobs are missing and {damaged.Count} do not match their {(options.VerifyBlobs ? "hash" : "size")} " +
                $"(of {rows.Count}): {string.Join(", ", missing.Concat(damaged).Take(3))}.", "Records that use them cannot show their text; restore a backup to recover them.");
        if (orphans + unreferenced > 0)
            return new DoctorCheck("blobs", "info", $"{rows.Count} blobs ({bytes} bytes); {unreferenced} unreferenced and {orphans} files the catalog does not know.",
                "They are collected after a day's grace by retention purge or doctor --repair.");
        return new DoctorCheck("blobs", "ok", $"{rows.Count} blobs ({bytes} bytes){(options.VerifyBlobs ? ", every hash verified" : "")}.");
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar();
    }

    private static long Count(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        Convert.ToInt64(Scalar(connection, sql, parameters), CultureInfo.InvariantCulture);

    private static List<T> Rows<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    private static string Line(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length > 200 ? line[..199] + "…" : line;
    }
}
