using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Lifecycle;

public sealed record SessionCatalogStore(string Id, string Directory);
public sealed record SessionCatalogOptions(int MaximumDirectoryEntries = 2048, int MaximumStores = 32,
    int MaximumHeaderBytes = 65_536, int MaximumHeaderLines = 32, int MaximumPathCharacters = 4096,
    int MaximumPageSize = 128, SessionEntryCodecOptions? HeaderCodecOptions = null);
public sealed record SessionCatalogQuery(int PageSize = 32, string? Cursor = null, string? WorkingDirectory = null);
public sealed record SessionCatalogItem(string Key, string StoreId, string FileName, string Path, string SessionId,
    string CreatedTimestamp, string WorkingDirectory, string CwdGroup, string? ParentSessionPath,
    long FileBytes, long ModifiedUtcTicks, string HeaderFingerprint);
public sealed record SessionCatalogPage(ImmutableArray<SessionCatalogItem> Items, string? NextCursor,
    int SkippedFiles, int UnavailableStores);
public enum SessionCatalogFailure { InvalidQuery, ResourceLimit, SessionNotFound, SessionChanged, CleanupFailed }
public sealed class SessionCatalogException(SessionCatalogFailure failure) : Exception(failure switch
{
    SessionCatalogFailure.InvalidQuery => "Session catalog query is invalid.",
    SessionCatalogFailure.ResourceLimit => "Session catalog exceeds its configured limits.",
    SessionCatalogFailure.SessionNotFound => "Session is absent from the configured catalog; refresh the listing.",
    SessionCatalogFailure.SessionChanged => "The selected session identity changed; refresh the listing.",
    _ => "Session catalog reader cleanup failed."
}) { public SessionCatalogFailure Failure { get; } = failure; }

/// <summary>Trusted host seam. Readers own their returned stream and await its close.</summary>
public interface ISessionCatalogFileSystem
{
    bool IsDirectoryLink(string directory);
    IEnumerable<string> EnumerateFileNames(string directory);
    SessionCatalogFileMetadata GetMetadata(string path);
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);
}
public sealed record SessionCatalogFileMetadata(long Bytes, long ModifiedUtcTicks, bool IsLink);

/// <summary>Bounded, read-only current-v3 header discovery in explicitly configured stores. Image/message
/// bodies are not parsed. Pages rescan the live directory; they are not a filesystem snapshot or writer lease.</summary>
public sealed class SessionCatalog
{
    private readonly ImmutableArray<SessionCatalogStore> stores;
    private readonly SessionCatalogOptions options;
    private readonly SessionEntryCodec codec;
    private readonly ISessionCatalogFileSystem files;
    private readonly string configuration;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public SessionCatalog(IEnumerable<SessionCatalogStore> stores, SessionCatalogOptions? options = null,
        ISessionCatalogFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(stores); this.options = options ?? new();
        if (this.options.MaximumDirectoryEntries is < 1 or > 100_000 || this.options.MaximumStores is < 1 or > 128 ||
            this.options.MaximumHeaderBytes is < 1 or > 1_048_576 || this.options.MaximumHeaderLines is < 1 or > 128 ||
            this.options.MaximumPathCharacters is < 1 or > 32_768 || this.options.MaximumPageSize is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        var configured = ImmutableArray.CreateBuilder<SessionCatalogStore>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var store in stores)
        {
            if (configured.Count >= this.options.MaximumStores) throw new ArgumentException("Too many session stores.", nameof(stores));
            if (store is null || string.IsNullOrEmpty(store.Id) || store.Id.Length > 64 ||
                store.Id.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_')) ||
                string.IsNullOrWhiteSpace(store.Directory) || !Path.IsPathFullyQualified(store.Directory) ||
                store.Directory.Length > this.options.MaximumPathCharacters || !Scalar(store.Directory))
                throw new ArgumentException("Session store requires a bounded ID and absolute directory.", nameof(stores));
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(store.Directory));
            if (!ids.Add(store.Id) || !directories.Add(directory)) throw new ArgumentException("Session store identities and directories must be distinct.", nameof(stores));
            configured.Add(store with { Directory = directory });
        }
        if (configured.Count == 0) throw new ArgumentException("At least one explicit session store is required.", nameof(stores));
        this.stores = configured.ToImmutable(); files = fileSystem ?? new LocalFiles();
        codec = new(this.options.HeaderCodecOptions ?? new(MaximumRecordCharacters: this.options.MaximumHeaderBytes,
            MaximumUtf8Bytes: this.options.MaximumHeaderBytes));
        configuration = Digest(JsonSerializer.Serialize(this.stores));
    }

    public async Task<SessionCatalogPage> ListAsync(SessionCatalogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); cancellationToken.ThrowIfCancellationRequested();
        if (query.PageSize < 1 || query.PageSize > options.MaximumPageSize ||
            query.WorkingDirectory is { } cwd && (cwd.Length > options.MaximumPathCharacters || !Scalar(cwd)))
            throw new SessionCatalogException(SessionCatalogFailure.InvalidQuery);
        var group = query.WorkingDirectory is null ? null : CwdGroup(query.WorkingDirectory);
        var after = query.Cursor is null ? null : DecodeCursor(query.Cursor, group);
        var scanned = await ScanAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = scanned.Items.Where(item => (group is null || item.CwdGroup == group) &&
            (after is null || Compare(item, after) > 0)).Take(query.PageSize + 1).ToArray();
        var page = selected.Take(query.PageSize).ToImmutableArray();
        return new(page, selected.Length > query.PageSize ? EncodeCursor(page[^1], group) : null,
            scanned.SkippedFiles, scanned.UnavailableStores);
    }

    /// <summary>A catalog key binds the configured store, filename and complete compact header identity.
    /// Resume does not turn a header ID or caller-supplied path into authority.</summary>
    public async Task<SessionCatalogItem> FindAsync(string key, CancellationToken cancellationToken = default)
    {
        if (key is null || key.Length != 64 || key.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new SessionCatalogException(SessionCatalogFailure.InvalidQuery);
        var scanned = await ScanAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return scanned.Items.FirstOrDefault(item => item.Key == key) ?? throw new SessionCatalogException(SessionCatalogFailure.SessionNotFound);
    }
    public void ValidateOpenedHeader(SessionCatalogItem selected, SessionEntry actualHeader)
    {
        ArgumentNullException.ThrowIfNull(selected); ArgumentNullException.ThrowIfNull(actualHeader);
        if (!actualHeader.IsHeader || actualHeader.Id != selected.SessionId || HeaderFingerprint(actualHeader) != selected.HeaderFingerprint)
            throw new SessionCatalogException(SessionCatalogFailure.SessionChanged);
    }

    /// <summary>Lexical grouping preserves the original cwd. Recognizable Windows paths fold case and
    /// separators independently of the host OS; Unix paths preserve case. It does not resolve foreign paths.</summary>
    public static string CwdGroup(string cwd)
    {
        ArgumentNullException.ThrowIfNull(cwd);
        if (!Scalar(cwd)) throw new ArgumentException("Working directory contains invalid Unicode.", nameof(cwd));
        if (cwd.Length >= 3 && char.IsAsciiLetter(cwd[0]) && cwd[1] == ':' && cwd[2] is '\\' or '/' || cwd.StartsWith("\\\\", StringComparison.Ordinal))
            return "windows:" + cwd.Replace('\\', '/').TrimEnd('/').ToUpperInvariant();
        return cwd.StartsWith('/') ? "unix:" + (cwd.TrimEnd('/') is { Length: > 0 } path ? path : "/") : "literal:" + cwd;
    }

    private async Task<SessionCatalogPage> ScanAsync(CancellationToken token)
    {
        var result = new List<SessionCatalogItem>(); var skipped = 0; var unavailable = 0; var entries = 0;
        foreach (var store in stores)
        {
            token.ThrowIfCancellationRequested(); IEnumerator<string>? enumeration = null;
            try
            {
                if (files.IsDirectoryLink(store.Directory)) { unavailable++; continue; }
                enumeration = files.EnumerateFileNames(store.Directory).GetEnumerator();
                while (true)
                {
                    token.ThrowIfCancellationRequested(); bool more;
                    try { more = enumeration.MoveNext(); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception) { unavailable++; break; }
                    if (!more) break;
                    if (++entries > options.MaximumDirectoryEntries) throw new SessionCatalogException(SessionCatalogFailure.ResourceLimit);
                    var name = enumeration.Current;
                    if (!SafeFileName(name)) { skipped++; continue; }
                    if (!name.EndsWith(".jsonl", StringComparison.Ordinal)) continue;
                    var path = Path.Combine(store.Directory, name);
                    if (path.Length > options.MaximumPathCharacters) { skipped++; continue; }
                    try
                    {
                        var metadata = files.GetMetadata(path);
                        if (metadata.IsLink || metadata.Bytes < 0 || metadata.ModifiedUtcTicks is < 0 or > 3155378975999999999) { skipped++; continue; }
                        var header = await ReadHeaderAsync(path, token).ConfigureAwait(false);
                        if (header is null) { skipped++; continue; }
                        var body = header.WireBody.Value;
                        if (header.Id.Length == 0 || header.Id.Length > options.MaximumPathCharacters || header.Timestamp.Length > 128 ||
                            !body.TryGetProperty("cwd", out var directory) || directory.ValueKind != JsonValueKind.String ||
                            directory.GetString()!.Length > options.MaximumPathCharacters) { skipped++; continue; }
                        string? parent = null;
                        if (body.TryGetProperty("parentSession", out var parentValue))
                        {
                            if (parentValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                                parentValue.ValueKind == JsonValueKind.String && parentValue.GetString()!.Length > options.MaximumPathCharacters) { skipped++; continue; }
                            parent = parentValue.ValueKind == JsonValueKind.String ? parentValue.GetString() : null;
                        }
                        var fingerprint = HeaderFingerprint(header); var cwd = directory.GetString()!;
                        result.Add(new(Digest(store.Id + "\0" + name + "\0" + fingerprint), store.Id, name, path,
                            header.Id, header.Timestamp, cwd, CwdGroup(cwd), parent, metadata.Bytes, metadata.ModifiedUtcTicks, fingerprint));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (SessionCatalogException error) when (error.Failure == SessionCatalogFailure.CleanupFailed) { throw; }
                    catch (Exception) { skipped++; }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (SessionCatalogException) { throw; }
            catch (Exception) { unavailable++; }
            finally
            {
                try { enumeration?.Dispose(); }
                catch (Exception) { throw new SessionCatalogException(SessionCatalogFailure.CleanupFailed); }
            }
        }
        token.ThrowIfCancellationRequested(); result.Sort(Compare);
        return new(result.ToImmutableArray(), null, skipped, unavailable);
    }
    private async Task<SessionEntry?> ReadHeaderAsync(string path, CancellationToken token)
    {
        var source = await files.OpenReadAsync(path, token).ConfigureAwait(false);
        try
        {
            using var line = new MemoryStream(); var buffer = new byte[512]; var observed = 0; var lines = 0;
            while (observed <= options.MaximumHeaderBytes)
            {
                token.ThrowIfCancellationRequested();
                var requested = Math.Min(buffer.Length, options.MaximumHeaderBytes + 1 - observed);
                var count = await source.ReadAsync(buffer.AsMemory(0, requested), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (count < 0 || count > requested) throw new IOException("Invalid catalog stream read.");
                if (count == 0) return ParseHeader(line.ToArray());
                for (var index = 0; index < count; index++)
                {
                    if (++observed > options.MaximumHeaderBytes) return null;
                    if (buffer[index] != (byte)'\n') { line.WriteByte(buffer[index]); continue; }
                    if (++lines > options.MaximumHeaderLines) return null;
                    var bytes = line.ToArray(); line.SetLength(0);
                    if (bytes.All(value => value is (byte)' ' or (byte)'\t' or (byte)'\r')) continue;
                    return ParseHeader(bytes);
                }
            }
            return null;
        }
        finally
        {
            try { await source.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { throw new SessionCatalogException(SessionCatalogFailure.CleanupFailed); }
        }
    }
    private SessionEntry? ParseHeader(byte[] bytes)
    {
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') Array.Resize(ref bytes, bytes.Length - 1);
        if (bytes.Length == 0) return null;
        var header = codec.ParseUtf8(bytes); return header.IsHeader ? header : null;
    }
    private string HeaderFingerprint(SessionEntry header) => Digest(codec.Serialize(header));
    private bool SafeFileName(string name) => !string.IsNullOrEmpty(name) && name.Length <= options.MaximumPathCharacters &&
        name is not ("." or "..") && name.IndexOfAny(['/', '\\']) < 0 && !Path.IsPathRooted(name) &&
        name.All(value => !char.IsControl(value)) && Scalar(name);
    private static int Compare(SessionCatalogItem left, SessionCatalogItem right)
    {
        var modified = right.ModifiedUtcTicks.CompareTo(left.ModifiedUtcTicks); if (modified != 0) return modified;
        var store = string.CompareOrdinal(left.StoreId, right.StoreId); return store != 0 ? store : string.CompareOrdinal(left.FileName, right.FileName);
    }
    private string EncodeCursor(SessionCatalogItem item, string? group) => "v1." + Convert.ToBase64String(Utf8.GetBytes(
        JsonSerializer.Serialize(new object?[] { configuration, group, item.ModifiedUtcTicks, item.StoreId, item.FileName }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private SessionCatalogItem DecodeCursor(string cursor, string? group)
    {
        try
        {
            if (!cursor.StartsWith("v1.", StringComparison.Ordinal) || cursor.Length > options.MaximumPathCharacters * 6 + 1024 ||
                cursor.AsSpan(3).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") >= 0)
                throw new FormatException();
            var encoded = cursor[3..].Replace('-', '+').Replace('_', '/'); encoded += new string('=', (4 - encoded.Length % 4) % 4);
            using var document = JsonDocument.Parse(Utf8.GetString(Convert.FromBase64String(encoded)), new JsonDocumentOptions { MaxDepth = 2 });
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Array || body.GetArrayLength() != 5 || body[0].GetString() != configuration ||
                body[1].ValueKind is not (JsonValueKind.Null or JsonValueKind.String) || body[1].GetString() != group ||
                !body[2].TryGetInt64(out var ticks) || ticks is < 0 or > 3155378975999999999 ||
                body[3].ValueKind != JsonValueKind.String || body[4].ValueKind != JsonValueKind.String) throw new FormatException();
            var store = body[3].GetString()!; var name = body[4].GetString()!;
            if (!stores.Any(item => item.Id == store) || !SafeFileName(name)) throw new FormatException();
            return new("", store, name, "", "", "", "", "", null, 0, ticks, "");
        }
        catch (Exception) { throw new SessionCatalogException(SessionCatalogFailure.InvalidQuery); }
    }
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(value)));
    private static bool Scalar(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index])) { if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        return true;
    }
    private sealed class LocalFiles : ISessionCatalogFileSystem
    {
        public bool IsDirectoryLink(string directory) => (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        public IEnumerable<string> EnumerateFileNames(string directory) => Directory.EnumerateFileSystemEntries(directory).Select(item => Path.GetFileName(item));
        public SessionCatalogFileMetadata GetMetadata(string path)
        {
            var info = new FileInfo(path); return new(info.Length, info.LastWriteTimeUtc.Ticks, (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0);
        }
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 512, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
    }
}
