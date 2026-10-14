// proper-lockfile 4.1.2 (MIT) lib/lockfile.js (lock, acquireLock, isLockStale, removeLock, updateLock, unlock) and lib/mtime-precision.js
// (probe, getMtime), as Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT) packages/coding-agent uses it: core/settings-manager.ts and
// core/project-trust.ts (lockSync: ten attempts 20 ms apart) and src/experimental/durable/sessions.ts (lock with retries and mtime
// updates). PiSettings' and InteractiveSettings' locks take their acquisition step from here.
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PiSharp.Cli.Pi;

/// <summary>proper-lockfile's <c>lock()</c> options.</summary>
internal sealed record PiLockOptions
{
    /// <summary>A lock not updated for this long is stale and taken over (proper-lockfile <c>stale</c>, default 10 s, at least 2 s).</summary>
    public TimeSpan Stale { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the holder renews the lock's mtime (proper-lockfile <c>update</c>); null is <see cref="Stale"/> / 2. Clamped to
    /// [1 s, stale / 2].</summary>
    public TimeSpan? Update { get; init; }

    /// <summary>Further attempts after the first (proper-lockfile <c>retries.retries</c>).</summary>
    public int Retries { get; init; }

    /// <summary>The pause before each further attempt (proper-lockfile <c>retries.minTimeout</c> = <c>maxTimeout</c>).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Called once when the held lock was lost: removed, or renewed by another process (proper-lockfile <c>onCompromised</c>, which
    /// throws by default). Must not throw.</summary>
    public Action<Exception>? OnCompromised { get; init; }
}

/// <summary>proper-lockfile's protocol: the lock of <c>&lt;file&gt;</c> is the directory <c>&lt;file&gt;.lock</c>, created exclusively with
/// <c>mkdir</c>; its holder renews its mtime, and a lock whose mtime is older than the stale time belongs to a dead process and is taken
/// over. Pi (Node) and PiSharp exclude each other with the same directory.</summary>
internal static class PiLockfile
{
    /// <summary>The lock directory of <paramref name="file"/> (proper-lockfile <c>getLockFile</c>).</summary>
    internal static string LockPath(string file) => file + ".lock";

    /// <summary>proper-lockfile <c>acquireLock</c>, one attempt: an exclusive <c>mkdir</c>; an existing lock older than
    /// <paramref name="stale"/> is removed and the <c>mkdir</c> tried once more. False when the lock is held.</summary>
    internal static bool TryAcquire(string lockPath, TimeSpan stale)
    {
        if (MakeDirectory(lockPath)) return true;
        if (stale <= TimeSpan.Zero) return false;
        DateTime modified;
        try
        {
            if (!Directory.Exists(lockPath)) return MakeDirectory(lockPath); // Removed meanwhile: retry without the stale check.
            modified = Directory.GetLastWriteTimeUtc(lockPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        // GetLastWriteTimeUtc reports 1601-01-01 instead of throwing when the path is gone: the holder released the lock in between, so
        // try the mkdir again rather than taking a vanished (or just re-created) lock for stale.
        if (modified == DateTime.FromFileTimeUtc(0)) return MakeDirectory(lockPath);
        if (!(modified < DateTime.UtcNow - stale)) return false;
        try { Directory.Delete(lockPath, recursive: false); }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; } // Being removed or re-created.
        return MakeDirectory(lockPath);
    }

    /// <summary>proper-lockfile <c>lockSync</c> as Pi's settings and trust stores retry it: ten attempts 20 ms apart, stale after 10 s;
    /// <paramref name="run"/> runs while the lock is held, and the lock is removed afterwards.</summary>
    /// <exception cref="IOException">"Lock file is already being held" when every attempt found it held.</exception>
    internal static void WithLock(string file, Action run, int attempts = 10, TimeSpan? delay = null, TimeSpan? stale = null)
    {
        var lockPath = LockPath(file);
        Exception? failure = null;
        for (var attempt = 1; ; attempt++)
        {
            // A transient failure (Windows refuses a mkdir over a directory pending deletion) is a failed attempt, as the lock being held;
            // when the last attempt failed with an error rather than finding the lock held, that error is what the caller sees.
            try { if (TryAcquire(lockPath, stale ?? TimeSpan.FromSeconds(10))) break; failure = null; }
            catch (Exception error) when (error is (IOException and not DirectoryNotFoundException) or UnauthorizedAccessException) { failure = error; }
            if (attempt >= attempts)
            {
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                throw new IOException("Lock file is already being held");
            }
            Thread.Sleep(delay ?? TimeSpan.FromMilliseconds(20));
        }
        try { run(); }
        finally { try { Directory.Delete(lockPath); } catch (IOException) { } }
    }

    /// <summary>proper-lockfile <c>lock(file, options)</c> with <c>realpath: false</c>: acquires the lock, retrying as configured, and
    /// keeps renewing it until the returned handle is disposed (<c>release()</c>).</summary>
    /// <exception cref="IOException">"Lock file is already being held" (ELOCKED) when every attempt found it held.</exception>
    internal static async Task<PiLockHandle> LockAsync(string file, PiLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        // proper-lockfile: stale = max(stale, 2000); update = update ?? stale / 2, clamped to [1000, stale / 2].
        var stale = TimeSpan.FromMilliseconds(Math.Max(options.Stale.TotalMilliseconds, 2000));
        var update = TimeSpan.FromMilliseconds(Math.Max(Math.Min((options.Update ?? stale / 2).TotalMilliseconds, stale.TotalMilliseconds / 2), 1000));
        var lockPath = LockPath(Path.GetFullPath(file));
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = null;
            try { if (TryAcquire(lockPath, stale)) break; }
            catch (Exception error) when (error is (IOException and not DirectoryNotFoundException) or UnauthorizedAccessException) { failure = error; }
            if (attempt >= options.Retries)
            {
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
                throw new IOException("Lock file is already being held");
            }
            await Task.Delay(options.RetryDelay, cancellationToken).ConfigureAwait(false);
        }
        DateTime mtime; bool seconds;
        try { (mtime, seconds) = Probe(lockPath); }
        catch
        {
            try { Directory.Delete(lockPath); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return new PiLockHandle(lockPath, mtime, seconds, stale, update, options.OnCompromised);
    }

    /// <summary>mtime-precision.js <c>probe</c>: sets the mtime to the next second plus 5 ms and reads it back; a file system that drops the
    /// milliseconds has second precision.</summary>
    private static (DateTime Mtime, bool Seconds) Probe(string lockPath)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var target = DateTime.UnixEpoch.AddMilliseconds((long)Math.Ceiling(now / 1000.0) * 1000 + 5);
        Directory.SetLastWriteTimeUtc(lockPath, target);
        Directory.SetLastAccessTimeUtc(lockPath, target);
        var read = Directory.GetLastWriteTimeUtc(lockPath);
        return (read, Milliseconds(read) % 1000 == 0);
    }

    internal static long Milliseconds(DateTime utc) => (long)Math.Floor((utc - DateTime.UnixEpoch).TotalMilliseconds);

    /// <summary>An exclusive <c>mkdir</c> (Node's <c>fs.mkdir</c>, mode 0o777): true when it created the directory, false when something
    /// exists at <paramref name="path"/> (EEXIST). Other failures throw.</summary>
    internal static bool MakeDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // The extended-length form: managed file APIs add it for long paths, but a direct CreateDirectoryW is limited to MAX_PATH
            // unless the process opts in, so a deep agent or project directory would otherwise never get its lock.
            if (CreateDirectoryW(ExtendedLengthPath(path), IntPtr.Zero)) return true;
            var error = Marshal.GetLastPInvokeError();
            // ERROR_ALREADY_EXISTS, and ERROR_ACCESS_DENIED for a name that exists but cannot be created over (Node maps both to EEXIST
            // when the path exists).
            if (error == 183 || error == 5 && Path.Exists(path)) return false;
            if (error is 2 or 3) throw new DirectoryNotFoundException($"ENOENT: no such file or directory, mkdir '{path}'");
            throw new IOException($"mkdir '{path}' failed", new Win32Exception(error));
        }
        if (mkdir(path, Convert.ToUInt32("777", 8)) == 0) return true;
        var errno = Marshal.GetLastPInvokeError();
        if (errno == 17) return false; // EEXIST on Linux and macOS.
        if (errno == 2) throw new DirectoryNotFoundException($"ENOENT: no such file or directory, mkdir '{path}'");
        throw new IOException($"mkdir '{path}' failed (errno {errno})");
    }

    /// <summary><paramref name="path"/> as a Win32 extended-length path (<c>\\?\C:\...</c>, or <c>\\?\UNC\server\share\...</c>).</summary>
    internal static string ExtendedLengthPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}

/// <summary>A held proper-lockfile lock: renewed every update interval until disposed (proper-lockfile's <c>release()</c>).</summary>
internal sealed class PiLockHandle : IAsyncDisposable
{
    private readonly string lockPath;
    private readonly bool seconds;
    private readonly TimeSpan stale, update;
    private readonly Action<Exception>? onCompromised;
    private readonly CancellationTokenSource renewal = new();
    private readonly Task renewing;
    private DateTime mtime;
    private DateTime lastUpdate = DateTime.UtcNow;
    private int released;

    internal PiLockHandle(string lockPath, DateTime mtime, bool seconds, TimeSpan stale, TimeSpan update, Action<Exception>? onCompromised)
    {
        this.lockPath = lockPath; this.mtime = mtime; this.seconds = seconds; this.stale = stale; this.update = update;
        this.onCompromised = onCompromised;
        renewing = RenewAsync();
    }

    /// <summary>The lock directory.</summary>
    internal string LockPath => lockPath;

    /// <summary>Whether the lock was lost (proper-lockfile: compromised).</summary>
    internal bool Compromised { get; private set; }

    /// <summary>proper-lockfile <c>updateLock</c>: while the mtime is still the one this holder wrote, writes a new one; a failed update is
    /// retried after 1 s until the stale time has passed since the last success.</summary>
    private async Task RenewAsync()
    {
        var delay = update;
        try
        {
            while (true)
            {
                await Task.Delay(delay, renewal.Token).ConfigureAwait(false);
                if (Volatile.Read(ref released) != 0) return;
                var overThreshold = lastUpdate + stale < DateTime.UtcNow;
                DateTime current;
                try { current = Directory.GetLastWriteTimeUtc(lockPath); if (!Directory.Exists(lockPath)) throw new DirectoryNotFoundException(lockPath); }
                catch (DirectoryNotFoundException error) { Compromise(error); return; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (overThreshold) { Compromise(error); return; }
                    delay = TimeSpan.FromSeconds(1);
                    continue;
                }
                if (current != mtime)
                {
                    Compromise(new IOException("Unable to update lock within the stale threshold"));
                    return;
                }
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var next = DateTime.UnixEpoch.AddMilliseconds(seconds ? (long)Math.Ceiling(now / 1000.0) * 1000 : now);
                try
                {
                    Directory.SetLastWriteTimeUtc(lockPath, next);
                    Directory.SetLastAccessTimeUtc(lockPath, next);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (Volatile.Read(ref released) != 0) return;
                    if (error is DirectoryNotFoundException || lastUpdate + stale < DateTime.UtcNow) { Compromise(error); return; }
                    delay = TimeSpan.FromSeconds(1);
                    continue;
                }
                // The value read back, which is the value the next stat compares (the file system keeps its own precision).
                mtime = Directory.GetLastWriteTimeUtc(lockPath);
                lastUpdate = DateTime.UtcNow;
                delay = update;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Compromise(Exception error)
    {
        if (Interlocked.Exchange(ref released, 1) != 0) return;
        Compromised = true;
        try { onCompromised?.Invoke(error); } catch { }
    }

    /// <summary>proper-lockfile <c>unlock</c>: stops renewing and removes the lock directory (a missing one is fine). A compromised lock is
    /// not removed: it may be another process's now.</summary>
    public async ValueTask DisposeAsync()
    {
        var first = Interlocked.Exchange(ref released, 1) == 0;
        await renewal.CancelAsync().ConfigureAwait(false);
        try { await renewing.ConfigureAwait(false); } catch (OperationCanceledException) { }
        renewal.Dispose();
        if (!first || Compromised) return;
        try { Directory.Delete(lockPath, recursive: false); }
        catch (DirectoryNotFoundException) { }
    }
}
