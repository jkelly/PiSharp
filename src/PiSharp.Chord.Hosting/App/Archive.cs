// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/{archive,app}.ts.
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Workers;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Hosting.App;

public sealed record ArchiveAddress(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("key")] string Key);
public sealed record ArchiveManifest(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("api")] string Api,
    [property: JsonPropertyName("address")] ArchiveAddress Address);
public sealed record ArchiveImportLimits(long Bytes = 4L * 1024 * 1024 * 1024, int Entries = 100_000);

/// <summary>An imported directory owned by the caller until atomically installed or disposed.</summary>
public sealed class ArchiveStaging : IAsyncDisposable
{
    private string? owned;
    internal ArchiveStaging(string staging, ArchiveManifest manifest) { owned = staging; Staging = staging; Manifest = manifest; }
    public string Staging { get; }
    public string Server => Path.Combine(Staging, "server.archive");
    public ArchiveManifest Manifest { get; }
    /// <summary>Installs at an absent target. The application registers the imported address separately.</summary>
    public void CommitTo(string target)
    {
        if (owned is null) throw new InvalidOperationException("The archive staging directory is no longer owned");
        if (Path.Exists(target)) throw new IOException("importData: the target already exists");
        Directory.Move(Staging, target);
        owned = null;
    }
    public ValueTask DisposeAsync()
    {
        var path = Interlocked.Exchange(ref owned, null);
        if (path is not null && Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Address archives: consistent store snapshot followed by immutable finished files.</summary>
public static class Archive
{
    // Frozen 1.5.2 includes the server's keyed records and therefore uses format 2.
    public const int Format = 2;

    /// <summary>The caller must hold the worker's address lock while exporting an idle directory.</summary>
    public static async Task PackDataAsync(string directory, ArchiveManifest manifest, string serverArchive, string path, ChordContext context)
    {
        if (!File.Exists(Path.Combine(directory, "store.sqlite"))) throw new IOException($"exportData: {manifest.Address.Key} has no data");
        await using var store = await Layout.OpenStoreAsync(Path.Combine(directory, "store.sqlite"), context).ConfigureAwait(false);
        await PackStoreAsync(store, Path.Combine(directory, "files"), manifest, serverArchive, path, context).ConfigureAwait(false);
    }

    public static async Task PackStoreAsync(IStore store, string files, ArchiveManifest manifest, string serverArchive, string path, ChordContext context)
    {
        ValidateManifest(manifest);
        var token = context.AbortSignal ?? CancellationToken.None;
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var staging = Path.Combine(parent, ".chord-export-" + Guid.NewGuid().ToString("N"));
        Layout.PrivateDirectory(staging);
        try
        {
            var exported = Path.Combine(staging, "store.archive");
            await using (var output = CreatePrivate(exported))
                await foreach (var chunk in Tables.EncodeJsonl(store.Export(context), token).ConfigureAwait(false))
                    await output.WriteAsync(chunk, token).ConfigureAwait(false);
            var written = Path.Combine(staging, "archive.tar");
            await using (var output = CreatePrivate(written))
            {
                await Tar.WriteAsync(output, Entries(exported, serverArchive, files, manifest, token), token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(written, path, overwrite: true);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    /// <summary>Checks and imports entirely in adjacent staging; any failure leaves no imported directory.</summary>
    public static async Task<ArchiveStaging> UnpackStagingAsync(string archive, string parent, Action<ArchiveManifest> accept,
        ArchiveImportLimits? limits, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(accept);
        limits ??= new();
        if (limits.Bytes < 0 || limits.Entries < 1) throw new ArgumentOutOfRangeException(nameof(limits));
        var token = context.AbortSignal ?? CancellationToken.None;
        Layout.PrivateDirectory(parent);
        var staging = Path.Combine(Path.GetFullPath(parent), ".chord-import-" + Guid.NewGuid().ToString("N"));
        Layout.PrivateDirectory(staging);
        try
        {
            ArchiveManifest? manifest = null;
            var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            long bytes = 0; var count = 0;
            await using (var input = File.OpenRead(archive))
            {
                await foreach (var entry in Tar.ReadAsync(input, token).ConfigureAwait(false))
                {
                    if (++count > limits.Entries) throw new InvalidDataException("importData: entry limit exceeded");
                    if (entry.Size > limits.Bytes - bytes) throw new InvalidDataException("importData: unpacked byte limit exceeded");
                    bytes += entry.Size;
                    if (!seen.Add(entry.Path)) throw new InvalidDataException("importData: duplicate path");
                    if (manifest is null && (entry.Path != "manifest.json" || entry.IsDirectory))
                        throw new InvalidDataException("importData: manifest.json must come first");
                    if (entry.Path == "manifest.json")
                    {
                        if (entry.Size > 64 * 1024) throw new InvalidDataException("importData: manifest.json is too large");
                        using var text = new MemoryStream();
                        await entry.Data.CopyToAsync(text, token).ConfigureAwait(false);
                        manifest = JsonSerializer.Deserialize<ArchiveManifest>(text.ToArray()) ?? throw new InvalidDataException("importData: missing manifest");
                        ValidateManifest(manifest);
                        accept(manifest);
                        continue;
                    }
                    if (entry.Path != "store.archive" && entry.Path != "server.archive" && !entry.Path.StartsWith("files/", StringComparison.Ordinal) && entry.Path != "files")
                        throw new InvalidDataException($"importData: unexpected entry {entry.Path}");
                    if (entry.IsDirectory)
                    {
                        if (entry.Path is "store.archive" or "server.archive") throw new InvalidDataException("importData: archive data must be a file");
                        Layout.PrivateDirectory(Path.Combine(staging, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
                        continue;
                    }
                    if (entry.Path == "files") throw new InvalidDataException("importData: files must be a directory");
                    var destination = Path.Combine(staging, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    Layout.PrivateDirectory(Path.GetDirectoryName(destination)!);
                    await using var output = CreatePrivate(destination);
                    await entry.Data.CopyToAsync(output, token).ConfigureAwait(false);
                }
            }
            if (manifest is null || !seen.Contains("store.archive") || !seen.Contains("server.archive"))
                throw new InvalidDataException("importData: missing manifest.json, store.archive or server.archive");
            var storeFile = Path.Combine(staging, "store.sqlite");
            var storeArchive = Path.Combine(staging, "store.archive");
            await using (var input = File.OpenRead(storeArchive))
            {
                var backend = await Layout.OpenBackendAsync(storeFile).ConfigureAwait(false);
                await using var store = await Tables.ImportStoreAsync(backend, Tables.DecodeJsonl(Chunks(input, token), token), context).ConfigureAwait(false);
            }
            File.Delete(storeArchive);
            token.ThrowIfCancellationRequested();
            return new ArchiveStaging(staging, manifest);
        }
        catch { Directory.Delete(staging, recursive: true); throw; }
    }

    public static void ValidateManifest(ArchiveManifest manifest)
    {
        if (manifest.Format != Format || string.IsNullOrEmpty(manifest.App) || string.IsNullOrEmpty(manifest.Api)
            || manifest.Address is null || string.IsNullOrEmpty(manifest.Address.Kind) || string.IsNullOrEmpty(manifest.Address.Key))
            throw new InvalidDataException("importData: invalid or unsupported archive manifest");
    }

    private static FileStream CreatePrivate(string path)
    {
        // Create the mode/ACL before writing even one byte of sensitive content.
        Layout.PrivateFile(path);
        return new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
    }

    private static async IAsyncEnumerable<TarEntry> Entries(string exported, string server, string files, ArchiveManifest manifest,
        [EnumeratorCancellation] CancellationToken token)
    {
        using (var data = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest)))
            yield return new("manifest.json", data.Length, data);
        await using (var data = File.OpenRead(exported)) yield return new("store.archive", data.Length, data);
        await using (var data = File.OpenRead(server)) yield return new("server.archive", data.Length, data);
        foreach (var path in PathsUnder(files))
        {
            token.ThrowIfCancellationRequested();
            FileStream data;
            try { data = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            await using (data)
                yield return new("files/" + Path.GetRelativePath(files, path).Replace(Path.DirectorySeparatorChar, '/'), data.Length, data);
        }
    }

    private static IEnumerable<string> PathsUnder(string directory)
    {
        FileSystemInfo[] entries;
        try { entries = new DirectoryInfo(directory).GetFileSystemInfos(); }
        catch (DirectoryNotFoundException) { yield break; }
        foreach (var entry in entries.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (entry.Name.StartsWith('.') || (entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (entry is DirectoryInfo)
            {
                foreach (var path in PathsUnder(entry.FullName)) yield return path;
            }
            else if (entry is System.IO.FileInfo) yield return entry.FullName;
        }
    }

    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) yield return buffer.AsMemory(0, read);
    }
}

public sealed record DataImportOptions(HostExisting Existing = HostExisting.Refuse);

/// <summary>Coordinates worker process leases, live snapshots and the server-owned portion of address data.</summary>
public sealed class AppArchiveService(string appName, string apiVersion, AppLayout layout, ChordHost host,
    IReadOnlyDictionary<string, ProcessWorkers> workers, ArchiveImportLimits? limits = null)
{
    public async Task ExportAsync(Address address, string path, ChordContext context)
    {
        var manager = WorkersOf(address);
        var manifest = new ArchiveManifest(Archive.Format, appName, apiVersion, new(address.Kind, address.Key));
        var target = Path.GetFullPath(path);
        var staging = Path.Combine(Path.GetDirectoryName(target)!, ".chord-server-" + Guid.NewGuid().ToString("N"));
        Layout.PrivateDirectory(staging);
        var token = context.AbortSignal ?? CancellationToken.None;
        try
        {
            var server = Path.Combine(staging, "server.archive");
            Layout.PrivateFile(server);
            await using (var output = new FileStream(server, FileMode.Truncate, FileAccess.Write, FileShare.None))
                await foreach (var chunk in Tables.EncodeJsonl(host.ExportHostData(address, context), token).ConfigureAwait(false))
                    await output.WriteAsync(chunk, token).ConfigureAwait(false);
            using var idle = await manager.TryLockIdleAsync(address, context).ConfigureAwait(false);
            if (idle is not null)
            {
                await Archive.PackDataAsync(Layout.WorkerDirectory(layout, address), manifest, server, target, context).ConfigureAwait(false);
                return;
            }
            await host.RootAsync(async call =>
            {
                var reference = await call.GetAsync(HostData.Type, new JsonObject { ["address"] = HostData.AddressParam(address) }).ConfigureAwait(false);
                await reference.CallAsync("exportTo", new JsonObject
                {
                    ["path"] = target, ["manifest"] = JsonSerializer.Serialize(manifest), ["server"] = server,
                }, call.Context).ConfigureAwait(false);
                return true;
            }, context).ConfigureAwait(false);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    public async Task ImportAsync(string archive, Address address, ChordContext context, DataImportOptions? options = null)
    {
        var manager = WorkersOf(address);
        using var idle = await manager.TryLockIdleAsync(address, context).ConfigureAwait(false)
            ?? throw new IOException($"importData: {address.Key} has a running worker or another data operation");
        var existing = options?.Existing ?? HostExisting.Refuse;
        var directory = Layout.WorkerDirectory(layout, address);
        if (existing == HostExisting.Refuse && Path.Exists(directory)) throw new IOException("importData: target already exists");
        await using var staged = await Archive.UnpackStagingAsync(Path.GetFullPath(archive), Path.GetDirectoryName(directory)!, manifest =>
        {
            if (manifest.App != appName) throw new InvalidDataException($"importData: archive belongs to {manifest.App}, not {appName}");
            if (manifest.Address.Kind != address.Kind) throw new InvalidDataException("importData: archive belongs to another host kind");
        }, limits, context).ConfigureAwait(false);
        // Validate the server part and its identity before publishing even the staged directory.
        await using (var input = File.OpenRead(staged.Server))
        {
            var checkedArchive = await Tables.ReadHostArchiveAsync(Tables.DecodeJsonl(Archive.Chunks(input,
                context.AbortSignal ?? CancellationToken.None)), context).ConfigureAwait(false);
            if (checkedArchive.Host != staged.Manifest.Address.Key) throw new InvalidDataException("importData: server archive address differs from manifest");
        }
        var aside = existing == HostExisting.Replace && Directory.Exists(directory) ? directory + ".aside-" + Guid.NewGuid().ToString("N") : null;
        var placed = false;
        try
        {
            context.AbortSignal?.ThrowIfCancellationRequested();
            if (aside is not null) Directory.Move(directory, aside);
            staged.CommitTo(directory);
            placed = true;
            await using var input = File.OpenRead(Path.Combine(directory, "server.archive"));
            await host.ImportHostDataAsync(Tables.DecodeJsonl(Archive.Chunks(input, context.AbortSignal ?? CancellationToken.None)), context,
                new HostImportOptions { Address = address, Existing = existing }).ConfigureAwait(false);
        }
        catch (ChordError error) when (placed && error.Code == "unavailable" && error.Reason == "storage_failed")
        {
            // The commit may have succeeded. Retain both generations rather than pair old files with new records.
            throw new IOException($"importData: server commit outcome is unknown; imported data remains at {directory}" +
                (aside is null ? "" : $"; previous data remains at {aside}"), error);
        }
        catch
        {
            if (placed && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            if (aside is not null && Directory.Exists(aside)) Directory.Move(aside, directory);
            throw;
        }
        // Committed cleanup cannot change the operation's result.
        try { File.Delete(Path.Combine(directory, "server.archive")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (aside is not null)
            try { Directory.Delete(aside, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public async Task DeleteAsync(Address address, ChordContext context)
    {
        using var idle = await WorkersOf(address).TryLockIdleAsync(address, context).ConfigureAwait(false)
            ?? throw new IOException($"deleteData: {address.Key} has a running worker or another data operation");
        await host.DeleteHostDataAsync(address, context).ConfigureAwait(false);
        var directory = Layout.WorkerDirectory(layout, address);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private ProcessWorkers WorkersOf(Address address) => workers.TryGetValue(address.Kind, out var manager) ? manager
        : throw new ArgumentException($"app: no worker kind {address.Kind}", nameof(address));
}
