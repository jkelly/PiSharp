// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/oauth.ts (McpOAuthCredentialStore
// withRefreshLock: proper-lockfile on `<agent dir>/mcp-auth-refresh-<hash>`, stale after 20 s, waited for up to 25 s in 100 ms steps).
using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>The per-server refresh lock of the original: one lock per server (by its credential key), shared by every pi and
/// PiSharp process using the agent directory, held from reading a server's tokens to saving refreshed ones. The lock is
/// <c>mcp-auth-refresh-&lt;first 16 hex of sha256(key)&gt;.lock</c>, the path proper-lockfile uses, created exclusively: it fails while
/// the original holds its lock directory there, and the original's <c>mkdir</c> fails while this lock file exists. Its holder
/// renews it every 10 s; a lock not renewed for 20 s belongs to a process that was killed and is taken over.</summary>
public static class McpOAuthRefreshLock
{
    public static readonly TimeSpan StaleAge = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The lock file of a server's credentials in <paramref name="directory"/>.</summary>
    public static string PathFor(string directory, string name, Uri serverUrl)
    {
        var (key, _) = McpOAuthCredentialStore.StoreKeys(name, serverUrl);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(directory, $"mcp-auth-refresh-{hash}.lock");
    }

    /// <summary>An acquisition of the server's lock for <see cref="McpAdmittedOAuthRefreshAdapter.RefreshLock"/>.</summary>
    public static Func<CancellationToken, Task<IAsyncDisposable>> For(string directory, string name, Uri serverUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var path = PathFor(directory, name, serverUrl);
        return token => AcquireAsync(directory, path, token);
    }

    private static async Task<IAsyncDisposable> AcquireAsync(string directory, string path, CancellationToken token)
    {
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var deadline = DateTime.UtcNow + WaitLimit;
        for (; ; )
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None });
                return new Held(path, stream);
            }
            // Windows reports an existing directory of that name (the original's lock) as access denied.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (RemoveStale(path)) continue;
                if (DateTime.UtcNow >= deadline) throw new IOException($"The MCP OAuth refresh lock is held by another process: {path}", error);
                await Task.Delay(RetryDelay, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Removes a lock its holder stopped renewing; false when it is held (or could not be removed).</summary>
    private static bool RemoveStale(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(path) <= StaleAge) return false;
                Directory.Delete(path, recursive: false); return true;
            }
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= StaleAge) return false;
            File.Delete(path); return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private sealed class Held : IAsyncDisposable
    {
        private readonly string path;
        private readonly FileStream stream;
        private readonly CancellationTokenSource renewal = new();
        private readonly Task renewing;
        public Held(string path, FileStream stream)
        {
            this.path = path; this.stream = stream;
            renewing = RenewAsync();
        }
        /// <summary>proper-lockfile's update interval (stale / 2): the lock's time is renewed while it is held.</summary>
        private async Task RenewAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(StaleAge / 2, renewal.Token).ConfigureAwait(false);
                    try { File.SetLastWriteTimeUtc(stream.SafeFileHandle, DateTime.UtcNow); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync()
        {
            await renewal.CancelAsync().ConfigureAwait(false);
            await renewing.ConfigureAwait(false);
            renewal.Dispose();
            await stream.DisposeAsync().ConfigureAwait(false);
            try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
