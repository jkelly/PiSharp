// proper-lockfile 4.1.2 (MIT) lib/lockfile.js (lockSync's acquireLock, isLockStale and removeLock), as Pi
// 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT) packages/coding-agent uses it in core/settings-manager.ts and core/project-trust.ts:
// lockSync with ten attempts 20 ms apart, stale after 10 s. PiSettings', InteractiveSettings' and PiProjectTrust's locks take their
// acquisition step from here.
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PiSharp.Cli.Pi;

/// <summary>proper-lockfile's protocol: the lock of <c>&lt;file&gt;</c> is the directory <c>&lt;file&gt;.lock</c>, created exclusively with
/// <c>mkdir</c>, and a lock whose mtime is older than the stale time belongs to a dead process and is taken over. Pi (Node) and PiSharp
/// exclude each other with the same directory.</summary>
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
