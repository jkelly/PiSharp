// Decision 0011 (run W3, CP): the data folder's models.json, Pi's format, read and written by the custom providers area. Reads are cheap:
// the file's size and time are checked at most once a second, so an edit in another editor is picked up on the next request (a new
// snapshot, a new Version) without a watcher. Every write goes through Update: the file is read again, the edit is applied to a copy,
// the result is validated the way Pi validates it (ModelsJsonConfig's schema, then ModelRegistry's composition, over a temporary file and
// with commands turned off), and only then written: to a temporary file that replaces models.json, keeping the previous file as
// models.json.bak. An invalid file (an edit by hand) lists nothing and refuses writes until it is fixed. Next to it the store keeps the
// sanitized copy the catalog and the routes read (routing/models.json, CustomProviderJson.RouteCopy): they never read the user's file, so
// a literal or !command key written by hand is never used, not even between a check and a read.
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Models;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.CustomProviders;

/// <summary>The custom providers and model overrides the catalog and the routes read.</summary>
public interface ICustomProviderSource
{
    /// <summary>The current snapshot (the file is checked for changes at most once a second).</summary>
    CustomProviderSnapshot Current { get; }
}

/// <summary>Reads and writes the data folder's <c>models.json</c>. A singleton; safe from many threads.</summary>
public sealed class CustomProviderStore : ICustomProviderSource, IDisposable
{
    /// <summary>The file name, as in Pi's agent folder.</summary>
    public const string FileName = "models.json";

    /// <summary>How long a snapshot is trusted before the file's size and time are checked again.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly object gate = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ILogger? log;
    private CustomProviderSnapshot current;
    private (bool Exists, long Ticks, long Length) stamp;
    private long checkedAt = long.MinValue;
    private long version;

    /// <summary>The store of <paramref name="options"/>' data folder.</summary>
    public CustomProviderStore(WebAgentOptions options, ILogger<CustomProviderStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Path = System.IO.Path.Join(options.DataDirectory, FileName);
        RoutesFile = System.IO.Path.Join(options.DataDirectory, "routing", FileName);
        this.log = log;
        current = new CustomProviderSnapshot { Path = Path };
    }

    /// <summary>The user's file: <c>&lt;data&gt;/models.json</c>.</summary>
    public string Path { get; }

    /// <summary>The sanitized copy the catalog and the routes read: <c>&lt;data&gt;/routing/models.json</c>.</summary>
    public string RoutesFile { get; }

    /// <summary>The previous file, kept by every write: <c>models.json.bak</c>.</summary>
    public string BackupPath => Path + ".bak";

    /// <inheritdoc/>
    public CustomProviderSnapshot Current
    {
        get
        {
            var snapshot = Volatile.Read(ref current);
            if (snapshot.Version != 0 && Environment.TickCount64 - Interlocked.Read(ref checkedAt) < (long)CheckInterval.TotalMilliseconds) return snapshot;
            return Refresh(force: false);
        }
    }

    /// <summary>Checks the file now and reads it again when it changed (or always, with <paramref name="force"/>).</summary>
    public CustomProviderSnapshot Refresh(bool force = true)
    {
        lock (gate)
        {
            Interlocked.Exchange(ref checkedAt, Environment.TickCount64);
            var now = Stamp();
            if (!force && current.Version != 0 && now == stamp) return current;
            stamp = now;
            var next = Load(now.Exists);
            Volatile.Write(ref current, next);
            return next;
        }
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to a copy of the file's document and writes the result when it is valid. <paramref name="beforeWrite"/>
    /// runs after the validation and before the write (the vault's part of a save); nothing is written when it throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is invalid now (fix or replace it first).</exception>
    /// <exception cref="ArgumentException">The result is not a valid models.json; the message says why.</exception>
    public async Task<CustomProviderSnapshot> UpdateAsync(Action<JsonObject> edit, Func<Task>? beforeWrite = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = Refresh(force: true);
            if (!before.Valid) throw new InvalidOperationException("models.json has an error, so nothing was saved. Fix the file (or import a valid one) first.");
            var document = (JsonObject)before.Document.DeepClone();
            edit(document);
            CustomProviderJson.Providers(document);
            var text = CustomProviderJson.Format(document, DateTimeOffset.UtcNow);
            if (Validate(text) is { } error) throw new ArgumentException(error);
            if (beforeWrite is not null) await beforeWrite().ConfigureAwait(false);
            Write(Path, text, keepBackup: true);
            log?.LogInformation("models.json saved ({Bytes} bytes).", Utf8.GetByteCount(text));
            return Refresh(force: true);
        }
        finally { writes.Release(); }
    }

    /// <summary>Why <paramref name="text"/> is not a valid models.json (Pi's schema and composition, commands off), or null.</summary>
    public string? Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var config = ModelsJsonConfig.Parse(text, Path);
        if (config.Error is { } schemaError) return schemaError;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RoutesFile)!);
        var probe = System.IO.Path.Join(System.IO.Path.GetDirectoryName(RoutesFile)!, $"check-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(probe, text, Utf8);
            return ModelRegistry.Create(new ModelRegistryOptions { ModelsPath = probe, RunCommand = _ => null }).GetError()?.Replace(probe, Path, StringComparison.Ordinal);
        }
        finally { TryDelete(probe); }
    }

    private (bool Exists, long Ticks, long Length) Stamp()
    {
        try
        {
            var info = new FileInfo(Path);
            return info.Exists ? (true, info.LastWriteTimeUtc.Ticks, info.Length) : (false, 0, 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (false, -1, -1); }
    }

    private CustomProviderSnapshot Load(bool exists)
    {
        var next = Interlocked.Increment(ref version);
        if (!exists)
        {
            WriteRoutes(null);
            return new CustomProviderSnapshot { Version = next, Path = Path };
        }
        string text;
        DateTimeOffset saved;
        try
        {
            text = File.ReadAllText(Path, Utf8);
            saved = File.GetLastWriteTimeUtc(Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WriteRoutes(null);
            return new CustomProviderSnapshot { Version = next, Path = Path, Exists = true, Error = "models.json could not be read: " + error.Message };
        }
        var size = Utf8.GetByteCount(text);
        var problem = Validate(text);
        var root = problem is null ? CustomProviderJson.ParseObject(text, out problem) : null;
        if (root is null)
        {
            WriteRoutes(null);
            log?.LogWarning("models.json is not valid; no custom provider or override is used until it is fixed.");
            return new CustomProviderSnapshot { Version = next, Path = Path, Exists = true, Saved = saved, Size = size, Error = Scrub(problem!, text) };
        }
        var providers = new List<CustomProviderInfo>();
        var overrides = new List<ModelOverrideInfo>();
        foreach (var (id, node) in CustomProviderJson.Providers(root))
        {
            if (node is not JsonObject entry) continue;
            if (!CustomProviderJson.IsBuiltin(id)) { providers.Add(CustomProviderJson.Info(id, entry)); continue; }
            if (entry["modelOverrides"] is not JsonObject models) continue;
            foreach (var (model, value) in models)
            {
                if (value is not JsonObject fields) continue;
                var shown = (JsonObject)fields.DeepClone();
                CustomProviderJson.RedactHeaders(shown);
                overrides.Add(new ModelOverrideInfo(id, model, shown));
            }
        }
        var routes = WriteRoutes(CustomProviderJson.RouteCopy(root));
        return new CustomProviderSnapshot
        {
            Version = next, Path = Path, Exists = true, Saved = saved, Size = size, Providers = providers, Overrides = overrides, RoutesPath = routes, Document = root,
        };
    }

    /// <summary>Writes (or, for null, deletes) the routes copy; its path, or null when there is none.</summary>
    private string? WriteRoutes(JsonObject? copy)
    {
        try
        {
            if (copy is null)
            {
                TryDelete(RoutesFile);
                return null;
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RoutesFile)!);
            Write(RoutesFile, CustomProviderJson.Pretty(copy), keepBackup: false);
            return RoutesFile;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("The routes copy of models.json could not be written: {Message}", error.Message);
            return null;
        }
    }

    private static void Write(string path, string text, bool keepBackup)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, text, Utf8);
        try
        {
            if (keepBackup && File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Left for the next write. */ }
    }

    /// <summary>An error about <paramref name="text"/> with every <c>apiKey</c> value of it replaced (a parse error may quote the text).</summary>
    internal static string Scrub(string error, string text) =>
        Credentials.SecretText.Scrub(error.Replace("\n\n", " ").Replace('\n', ' '), [.. CustomProviderJson.ApiKeysIn(text)]);

    /// <inheritdoc/>
    public void Dispose() => writes.Dispose();
}
