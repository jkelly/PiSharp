// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/auth-storage.ts
// (FileAuthStorageBackend, AuthStorage.read/modify) and packages/coding-agent/src/config.ts (getAuthPath).
using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Interactive;

namespace PiSharp.Cli.Authentication;

/// <summary>One <c>auth.json</c> entry: <c>oauth</c>, <c>api_key</c> (with its configured, unresolved key and <c>env</c>) or another type.</summary>
internal sealed record StoredCredentialEntry(string Type, string? Key, IReadOnlyDictionary<string, string>? Environment)
{
    public override string ToString() => $"StoredCredentialEntry ({Type}) [redacted]";
}

/// <summary>
/// The source <c>auth.json</c> credential store (<c>&lt;agent dir&gt;/auth.json</c>, shared with Pi) as the stored OAuth source of
/// <see cref="StoredOAuthLifecycle"/>. Entries are <c>{"type":"oauth","refresh","access","expires",...}</c>; other providers and
/// credential types are preserved. A modification holds a process lock and an exclusive <c>auth.json.lock</c>, re-reads the file
/// inside it and writes the merged document as <c>JSON.stringify(data, null, 2)</c>. A new file is created owner-only (0600 on Unix).
/// </summary>
internal sealed class AuthJsonCredentialStore : IAdmittedOAuthCredentialSource
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(StringComparer.Ordinal);
    private static readonly TimeSpan StaleLock = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _processLock;
    private readonly TimeProvider _time;

    public AuthJsonCredentialStore(string authPath, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrEmpty(authPath) || !Path.IsPathFullyQualified(authPath)) throw new ArgumentException("auth.json requires an absolute path.", nameof(authPath));
        AuthPath = Path.GetFullPath(authPath); _time = timeProvider ?? TimeProvider.System;
        _processLock = ProcessLocks.GetOrAdd(OperatingSystem.IsWindows() ? AuthPath.ToUpperInvariant() : AuthPath, _ => new(1, 1));
    }

    public string AuthPath { get; }

    /// <summary>Source getAuthPath: <c>PI_CODING_AGENT_DIR</c> (expanded as the source does) or <c>~/.pi/agent</c>, then <c>auth.json</c>.</summary>
    public static string DefaultPath(IReadOnlyDictionary<string, string?> environment, string home) => Path.Combine(
        TerminalKeybindingConfigurationLoader.ResolveAgentDirectory(home, OperatingSystem.IsWindows() ? "win32" : "linux", environment), "auth.json");

    public static AuthJsonCredentialStore CreateDefault() => new(DefaultPath(
        new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") },
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    public async Task<OAuthCredentialSnapshot?> ReadAsync(string provider, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        return await WithLockAsync(document => Task.FromResult(((OAuthCredentialSnapshot?)Credential(document, provider), (JsonObject?)null)),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<OAuthCredentialSnapshot?> ModifyAsync(string provider,
        Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider); ArgumentNullException.ThrowIfNull(mutation);
        return WithLockAsync(async document =>
        {
            var current = Credential(document, provider);
            var next = await mutation(current, cancellationToken).ConfigureAwait(false);
            if (next is null) return (current, (JsonObject?)null);
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new JsonObject { ["type"] = "oauth", ["refresh"] = next.Refresh, ["access"] = next.Access, ["expires"] = next.ExpiresUnixMilliseconds };
            foreach (var (key, value) in next.ProviderData) if (key is not ("type" or "refresh" or "access" or "expires")) entry[key] = value;
            document[provider] = entry;
            return (next, document);
        }, cancellationToken);
    }

    /// <summary>Source AuthStorage.read: the entry's type tag, and for <c>api_key</c> its configured key value (unresolved) and
    /// <c>env</c>. Null for a missing entry. A missing file is read without creating the directory or the lock.</summary>
    public async Task<StoredCredentialEntry?> ReadEntryAsync(string provider, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        if (!File.Exists(AuthPath)) return null;
        return await WithLockAsync(document => Task.FromResult(((StoredCredentialEntry?)Entry(document, provider), (JsonObject?)null)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Source AuthStorage.list: provider ids and credential types, without resolving key values.</summary>
    public async Task<IReadOnlyList<(string Provider, string Type)>> ListAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AuthPath)) return [];
        return await WithLockAsync(document => Task.FromResult(((IReadOnlyList<(string, string)>)document
            .Select(entry => (entry.Key, entry.Value is JsonObject value && value["type"] is JsonValue type && type.TryGetValue<string>(out var text) ? text : ""))
            .ToList(), (JsonObject?)null)), cancellationToken).ConfigureAwait(false);
    }

    private static StoredCredentialEntry? Entry(JsonObject document, string provider)
    {
        if (!document.TryGetPropertyValue(provider, out var node) || node is null) return null;
        if (node is not JsonObject entry || entry["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type))
            throw new InvalidDataException($"Invalid auth.json credential for provider \"{provider}\"");
        if (type == "oauth") { _ = Credential(document, provider); return new(type, null, null); }
        if (type != "api_key") return new(type, null, null);
        string? key = null; Dictionary<string, string>? environment = null;
        if (entry["key"] is { } keyNode)
            key = keyNode is JsonValue keyValue && keyValue.TryGetValue<string>(out var text) ? text
                : throw new InvalidDataException($"Invalid auth.json credential for provider \"{provider}\"");
        if (entry["env"] is { } envNode)
        {
            if (envNode is not JsonObject values) throw new InvalidDataException($"Invalid auth.json credential for provider \"{provider}\"");
            environment = new(StringComparer.Ordinal);
            foreach (var (name, value) in values)
                environment[name] = value is JsonValue text && text.TryGetValue<string>(out var stringValue) ? stringValue
                    : throw new InvalidDataException($"Invalid auth.json credential for provider \"{provider}\"");
        }
        return new(type, key, environment);
    }

    /// <summary>Source AuthStorage.delete (used by logout); a missing entry is not an error.</summary>
    public Task DeleteAsync(string provider, CancellationToken cancellationToken) => WithLockAsync(document =>
        Task.FromResult(((OAuthCredentialSnapshot?)null, document.Remove(provider) ? document : null)), cancellationToken);

    private static OAuthCredentialSnapshot? Credential(JsonObject document, string provider)
    {
        if (document[provider] is not JsonObject entry || entry["type"]?.GetValueKind() != JsonValueKind.String ||
            entry["type"]!.GetValue<string>() != "oauth") return null;
        if (entry["access"] is not JsonValue access || !access.TryGetValue<string>(out var accessToken) ||
            entry["refresh"] is not JsonValue refresh || !refresh.TryGetValue<string>(out var refreshToken) ||
            entry["expires"] is not JsonValue expires || !expires.TryGetValue<double>(out var expiresAt) || !double.IsFinite(expiresAt))
            throw new InvalidDataException($"Invalid auth.json credential for provider \"{provider}\"");
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entry)
            if (key is not ("type" or "refresh" or "access" or "expires") && value is JsonValue text && text.TryGetValue<string>(out var stringValue))
                data[key] = stringValue;
        return new(accessToken, refreshToken, (long)expiresAt, data);
    }

    private async Task<T> WithLockAsync<T>(Func<JsonObject, Task<(T Result, JsonObject? Next)>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _processLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(AuthPath)!;
            if (!Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
                else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            await using var fileLock = await AcquireFileLockAsync(token).ConfigureAwait(false);
            var document = await ReadDocumentAsync(token).ConfigureAwait(false);
            var (result, next) = await operation(document).ConfigureAwait(false);
            if (next is not null) await WriteDocumentAsync(next).ConfigureAwait(false);
            return result;
        }
        finally { _processLock.Release(); }
    }

    private async Task<JsonObject> ReadDocumentAsync(CancellationToken token)
    {
        string text;
        try { text = await File.ReadAllTextAsync(AuthPath, Encoding.UTF8, token).ConfigureAwait(false); }
        catch (FileNotFoundException) { return []; }
        if (text.StartsWith((char)0xFEFF)) text = text[1..];
        if (text.Length == 0) return [];
        try { return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Invalid auth.json: expected an object"); }
        catch (JsonException error) { throw new InvalidDataException("Failed to read auth.json: " + error.Message, error); }
    }

    private async Task WriteDocumentAsync(JsonObject document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions
            { WriteIndented = true, IndentSize = 2, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        // The mode applies only on creation so administrator-managed modes and ACLs remain intact.
        if (!OperatingSystem.IsWindows() && !File.Exists(AuthPath)) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(AuthPath, options);
        await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Exclusive <c>auth.json.lock</c> (the path proper-lockfile uses), stale after 30 seconds, retried with backoff until then.</summary>
    private async Task<IAsyncDisposable> AcquireFileLockAsync(CancellationToken token)
    {
        var path = AuthPath + ".lock"; var deadline = _time.GetUtcNow() + StaleLock; var retry = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(path) && !Directory.Exists(path)) throw;
                var age = _time.GetUtcNow() - new DateTimeOffset(Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path));
                if (age > StaleLock)
                {
                    try { if (Directory.Exists(path)) Directory.Delete(path); else File.Delete(path); } catch (Exception) { }
                    continue;
                }
                if (_time.GetUtcNow() >= deadline) throw new IOException("Failed to acquire auth storage lock", error);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10 * Math.Pow(2, retry++), 1000)), _time, token).ConfigureAwait(false);
            }
        }
    }
}
