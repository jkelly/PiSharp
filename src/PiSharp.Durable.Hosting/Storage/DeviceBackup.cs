// PiSharp-only (decision 0010, M6): backup, verification and restore of a device's data directory. A backup is a quiesced copy: it takes
// the device lock (so the device must be stopped), copies every SQLite database with SQLite's backup API (which includes the WAL) and turns
// each copy into a rollback-journal database so it can be checked read-only, copies blobs, prompts, the archive and device.json as they
// are, and writes a manifest of every file's size and SHA-256 last. Secrets (the device token) stay out unless asked for; trigger
// credentials are stored as hashes and travel with the catalog. Verification rechecks every listed file and every database's integrity;
// restore verifies first and only writes into an empty directory, so a restore never mixes with a live device.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace PiSharp.Durable.Hosting;

/// <summary>One file of a backup.</summary>
/// <param name="Path">Relative to the backup's root, with <c>/</c> separators.</param>
/// <param name="Size">Bytes.</param>
/// <param name="Sha256">Lowercase hex.</param>
public sealed record BackupFile(string Path, long Size, string Sha256);

/// <summary>What a backup holds (<c>pisharp-backup.json</c>).</summary>
/// <param name="CreatedAt">Unix milliseconds.</param>
/// <param name="DeviceUid">The device identity in the catalog.</param>
/// <param name="CatalogVersion">The catalog's schema version.</param>
/// <param name="Sessions">The catalog's session IDs.</param>
/// <param name="IncludesSecrets">Whether the device token is in the backup.</param>
/// <param name="Files">Every file but the manifest.</param>
public sealed record BackupManifest(long CreatedAt, string DeviceUid, int CatalogVersion, IReadOnlyList<string> Sessions, bool IncludesSecrets,
    IReadOnlyList<BackupFile> Files)
{
    /// <summary>The manifest's format and version.</summary>
    public const string Format = "pisharp.durable-backup";

    /// <summary>The manifest's version.</summary>
    public const int Version = 1;

    /// <summary>The manifest as JSON.</summary>
    public JsonObject ToJson() => new()
    {
        ["format"] = Format, ["version"] = Version, ["createdAt"] = CreatedAt, ["deviceUid"] = DeviceUid, ["catalogVersion"] = CatalogVersion,
        ["sessions"] = new JsonArray([.. Sessions.Select(id => (JsonNode?)id)]), ["includesSecrets"] = IncludesSecrets,
        ["files"] = new JsonArray([.. Files.Select(file => (JsonNode?)new JsonObject { ["path"] = file.Path, ["size"] = file.Size, ["sha256"] = file.Sha256 })]),
    };

    /// <summary>Reads a manifest.</summary>
    /// <exception cref="InvalidDataException">Not a version 1 backup manifest.</exception>
    public static BackupManifest FromJson(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json["format"]?.GetValue<string>() != Format || json["version"]?.GetValue<int>() != Version)
            throw new InvalidDataException($"Not a {Format} version {Version} manifest.");
        try
        {
            return new BackupManifest(json["createdAt"]!.GetValue<long>(), json["deviceUid"]!.GetValue<string>(), json["catalogVersion"]!.GetValue<int>(),
                [.. (json["sessions"] as JsonArray ?? []).Select(id => id!.GetValue<string>())], json["includesSecrets"]?.GetValue<bool>() == true,
                [.. (json["files"] as JsonArray ?? []).Select(file => new BackupFile(file!["path"]!.GetValue<string>(), file["size"]!.GetValue<long>(),
                    file["sha256"]!.GetValue<string>()))]);
        }
        catch (Exception error) when (error is NullReferenceException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The backup manifest is incomplete: " + error.Message, error);
        }
    }
}

/// <summary>The result of checking a backup.</summary>
/// <param name="Manifest">The manifest, when it could be read.</param>
/// <param name="Problems">What is wrong; empty when the backup is intact.</param>
public sealed record BackupCheck(BackupManifest? Manifest, IReadOnlyList<string> Problems)
{
    /// <summary>The backup is intact.</summary>
    public bool Ok => Manifest is not null && Problems.Count == 0;
}

/// <summary>Backs up, verifies and restores a device's data directory.</summary>
public static class DeviceBackup
{
    /// <summary>The manifest's file name.</summary>
    public const string ManifestFile = "pisharp-backup.json";

    /// <summary>Files a backup leaves out unless secrets are asked for.</summary>
    public static IReadOnlyList<string> SecretFiles { get; } = ["token"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Copies the stopped device at <paramref name="dataDirectory"/> into <paramref name="target"/>, which must be missing or empty, and
    /// returns the manifest written there.
    /// </summary>
    /// <exception cref="DeviceLockedException">The device is running.</exception>
    /// <exception cref="IOException">The target is not empty, or a file could not be copied.</exception>
    public static BackupManifest Create(string dataDirectory, string target, bool includeSecrets = false, TimeProvider? time = null)
    {
        var data = Path.GetFullPath(dataDirectory);
        var root = Path.GetFullPath(target);
        if (!File.Exists(Path.Join(data, "catalog.sqlite"))) throw new IOException($"{data} has no device catalog.");
        if (Inside(root, data) || Inside(data, root)) throw new IOException("The backup and the data directory must not contain each other.");
        RequireEmpty(root);
        using var deviceLock = FileLock.TryAcquire(Path.Join(data, "catalog.lock"), $"backup, pid {Environment.ProcessId}")
            ?? throw new DeviceLockedException($"The device at {data} is running. Stop it first: a backup copies a quiesced data directory.");
        Directory.CreateDirectory(root);
        var files = new List<BackupFile>();
        foreach (var source in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(data, source).Replace('\\', '/');
            if (Skipped(relative) || !includeSecrets && SecretFiles.Contains(relative, StringComparer.Ordinal)) continue;
            var copy = Path.Join(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            if (relative.EndsWith(".sqlite", StringComparison.Ordinal)) CopyDatabase(source, copy);
            else File.Copy(source, copy);
            files.Add(Describe(root, relative));
        }
        var (uid, version, sessions) = ReadCatalog(Path.Join(root, "catalog.sqlite"));
        var manifest = new BackupManifest((time ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds(), uid, version, sessions, includeSecrets, files);
        var temporary = Path.Join(root, ManifestFile + ".tmp");
        File.WriteAllText(temporary, manifest.ToJson().ToJsonString(Indented));
        File.Move(temporary, Path.Join(root, ManifestFile));
        return manifest;
    }

    /// <summary>Checks a backup: its manifest, every listed file's size and hash, every database's integrity, and files it does not list.</summary>
    public static BackupCheck Verify(string backup)
    {
        var root = Path.GetFullPath(backup);
        BackupManifest manifest;
        try
        {
            manifest = BackupManifest.FromJson(JsonNode.Parse(File.ReadAllText(Path.Join(root, ManifestFile))) as JsonObject
                ?? throw new InvalidDataException("The backup manifest is not a JSON object."));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return new BackupCheck(null, [$"{ManifestFile}: {error.Message}"]);
        }
        var problems = new List<string>();
        if (manifest.CatalogVersion > SessionCatalog.SchemaVersion)
            problems.Add($"The catalog has schema version {manifest.CatalogVersion}; this host reads version {SessionCatalog.SchemaVersion}.");
        if (!manifest.Files.Any(file => file.Path == "catalog.sqlite")) problems.Add("The backup has no catalog.sqlite.");
        var listed = new HashSet<string>(StringComparer.Ordinal) { ManifestFile };
        foreach (var file in manifest.Files)
        {
            if (!listed.Add(file.Path) || file.Path.Split('/').Any(part => part is "" or "." or ".."))
            {
                problems.Add($"{file.Path}: not a valid, unique path.");
                continue;
            }
            var path = Path.Join(root, file.Path);
            if (!File.Exists(path)) problems.Add($"{file.Path}: missing.");
            else if (new FileInfo(path).Length != file.Size) problems.Add($"{file.Path}: {new FileInfo(path).Length} bytes, the manifest says {file.Size}.");
            else if (Hash(path) != file.Sha256) problems.Add($"{file.Path}: its SHA-256 does not match the manifest.");
            else if (file.Path.EndsWith(".sqlite", StringComparison.Ordinal) && Integrity(path) is { } damage) problems.Add($"{file.Path}: {damage}");
        }
        foreach (var extra in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !listed.Contains(path)).Order(StringComparer.Ordinal))
            problems.Add($"{extra}: not listed in the manifest.");
        return new BackupCheck(manifest, problems);
    }

    /// <summary>Verifies <paramref name="backup"/>, then copies it into <paramref name="dataDirectory"/>, which must be missing or empty.</summary>
    /// <exception cref="InvalidDataException">The backup is damaged; the message lists the problems.</exception>
    /// <exception cref="IOException">The data directory is not empty.</exception>
    public static BackupManifest Restore(string backup, string dataDirectory)
    {
        var root = Path.GetFullPath(backup);
        var data = Path.GetFullPath(dataDirectory);
        if (Inside(root, data) || Inside(data, root)) throw new IOException("The backup and the data directory must not contain each other.");
        RequireEmpty(data);
        var check = Verify(root);
        if (!check.Ok) throw new InvalidDataException("The backup is damaged: " + string.Join(" ", check.Problems.Take(5)) +
            (check.Problems.Count > 5 ? $" ({check.Problems.Count - 5} more)" : ""));
        Directory.CreateDirectory(data);
        foreach (var file in check.Manifest!.Files)
        {
            var target = Path.Join(data, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Join(root, file.Path), target);
            if (SecretFiles.Contains(file.Path, StringComparer.Ordinal) && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return check.Manifest;
    }

    /// <summary>
    /// Opens a SQLite file without writing anything beside it. A WAL database opened read-only makes its <c>-wal</c> and <c>-shm</c> files
    /// and, unable to checkpoint, leaves them; so a database without them (closed cleanly, nothing writing it) opens immutable, and one with
    /// them (a running device, or a crash) opens read-only over the files that are already there.
    /// </summary>
    internal static SqliteConnection OpenReadOnly(string path)
    {
        var full = Path.GetFullPath(path);
        var quiet = !File.Exists(full + "-wal") && !File.Exists(full + "-shm");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = quiet ? new Uri(full).AbsoluteUri + "?immutable=1" : full,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>The integrity problem of a SQLite file opened read-only, or null when <c>PRAGMA quick_check</c> passes.</summary>
    internal static string? Integrity(string path)
    {
        try
        {
            using var connection = OpenReadOnly(path);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = command.ExecuteScalar() as string;
            return result == "ok" ? null : "integrity check failed: " + result;
        }
        catch (SqliteException error)
        {
            return "not a readable SQLite database: " + error.Message;
        }
    }

    private static bool Skipped(string relative) =>
        relative is "catalog.lock" || relative.EndsWith("-wal", StringComparison.Ordinal) || relative.EndsWith("-shm", StringComparison.Ordinal) ||
        relative.EndsWith("-journal", StringComparison.Ordinal) || relative.StartsWith("blobs/tmp/", StringComparison.Ordinal) ||
        relative.StartsWith("catalog.v", StringComparison.Ordinal) && relative.Contains(".bak", StringComparison.Ordinal) ||
        relative.EndsWith(".tmp", StringComparison.Ordinal);

    private static void CopyDatabase(string source, string target)
    {
        using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);
            using var command = to.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            command.ExecuteNonQuery();
        }
    }

    private static (string Uid, int Version, IReadOnlyList<string> Sessions) ReadCatalog(string path)
    {
        using var connection = OpenReadOnly(path);
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var schema = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var uid = connection.CreateCommand();
        uid.CommandText = "SELECT value FROM meta WHERE key = 'device_uid';";
        var device = uid.ExecuteScalar() as string ?? "";
        using var list = connection.CreateCommand();
        list.CommandText = "SELECT id FROM sessions ORDER BY created_at, id;";
        using var reader = list.ExecuteReader();
        var sessions = new List<string>();
        while (reader.Read()) sessions.Add(reader.GetString(0));
        return (device, schema, sessions);
    }

    private static BackupFile Describe(string root, string relative)
    {
        var path = Path.Join(root, relative);
        return new BackupFile(relative, new FileInfo(path).Length, Hash(path));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void RequireEmpty(string directory)
    {
        if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException($"{directory} is not empty.");
    }

    private static bool Inside(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
