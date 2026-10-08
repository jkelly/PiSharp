// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/auth-storage.ts FileAuthStorageBackend.withLock,
// packages/coding-agent/src/extensions/mcp/oauth.ts McpOAuthCredentialStore (`mcp-auth.json` in the agent directory).
using System.Text;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>The durable credential document: `&lt;agent dir&gt;/mcp-auth.json`, like the original. Each update runs while an
/// exclusive `mcp-auth.json.lock` is held, so pi and PiSharp processes sharing the agent directory never interleave
/// read-modify-write cycles. Like the original, the parent directory is created owner-only (0700) and the document is
/// created as `{}` owner-only (0600); the mode applies only on creation, so administrator-managed modes and ACLs stay
/// intact, and for the same reason the document is rewritten in place rather than replaced by a renamed copy. The
/// path is caller-admitted; nothing is read from the environment.</summary>
public sealed class McpOAuthFileCredentialBackend : IMcpOAuthCredentialBackend
{
    public const string FileName = "mcp-auth.json";
    /// <summary>proper-lockfile's defaults as the original uses them: stale after 10 s, 10 attempts 20 ms apart.</summary>
    public static readonly TimeSpan StaleLockAge = TimeSpan.FromSeconds(10);
    private const int LockAttempts = 10;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(20);
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object gate = new();

    public McpOAuthFileCredentialBackend(string explicitlyAdmittedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(explicitlyAdmittedPath);
        if (!Path.IsPathFullyQualified(explicitlyAdmittedPath)) throw new ArgumentException("An absolute credential path is required.", nameof(explicitlyAdmittedPath));
        FilePath = Path.GetFullPath(explicitlyAdmittedPath);
    }

    /// <summary>`mcp-auth.json` in the agent directory, where the original keeps MCP credentials.</summary>
    public static McpOAuthFileCredentialBackend InAgentDirectory(string agentDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentDirectory);
        return new(Path.Combine(agentDirectory, FileName));
    }

    public string FilePath { get; }
    public string LockPath => FilePath + ".lock";

    public T WithLock<T>(Func<string?, (T Result, string? Next)> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        // One writer per process at a time; the lock file excludes other processes.
        lock (gate)
        {
            EnsureParentDirectory(); EnsureFileExists();
            using var held = AcquireLock();
            var current = File.Exists(FilePath) ? File.ReadAllText(FilePath, Utf8) : null;
            var (result, next) = update(current);
            if (next is not null) Write(next);
            return result;
        }
    }

    private void EnsureParentDirectory()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        if (Directory.Exists(directory)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void EnsureFileExists()
    {
        if (File.Exists(FilePath)) return;
        try
        {
            using var created = new FileStream(FilePath, Options(FileMode.CreateNew));
            created.Write("{}"u8);
        }
        catch (IOException) when (File.Exists(FilePath)) { /* Another process created it first. */ }
    }

    private void Write(string next)
    {
        using var stream = new FileStream(FilePath, Options(FileMode.Create));
        stream.Write(Utf8.GetBytes(next));
        stream.Flush(flushToDisk: true);
    }

    private static FileStreamOptions Options(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    /// <summary>The lock is `mcp-auth.json.lock`, the path proper-lockfile uses: created exclusively, so it fails while
    /// the original holds its lock directory there and the original's `mkdir` fails while this lock file exists. A lock
    /// older than <see cref="StaleLockAge"/> belongs to a process that died holding it and is taken over.</summary>
    private Lock AcquireLock()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return new(LockPath, new FileStream(LockPath, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None })); }
            // Windows reports an existing directory of that name (the original's lock) as access denied.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt >= LockAttempts) throw new IOException($"The MCP credential store is locked by another process: {LockPath}", error);
                if (!RemoveStale()) Thread.Sleep(LockRetryDelay);
            }
        }
    }

    /// <summary>Removes a stale lock; false when the lock is held (or could not be removed).</summary>
    private bool RemoveStale()
    {
        try
        {
            if (Directory.Exists(LockPath))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(LockPath) <= StaleLockAge) return false;
                Directory.Delete(LockPath, recursive: false); return true;
            }
            if (!File.Exists(LockPath)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(LockPath) <= StaleLockAge) return false;
            File.Delete(LockPath); return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed class Lock(string path, FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
