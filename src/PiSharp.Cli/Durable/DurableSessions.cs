// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/sessions.ts
// The session layout and lock are upstream's, so Pi's experimental durable agent and `pisharp durable` continue each other's sessions:
// <agentDir>/experimental/durable-sessions/<sha256(realpath cwd) hex[..24]>/<13-digit ms>-<uuid>/session.sqlite, locked with
// proper-lockfile on the session directory (<dir>.lock, realpath false, 12 retries 1 s apart, stale after 10 s). realpath is Node's
// fs.promises.realpath (libuv uv_fs_realpath): GetFinalPathNameByHandleW on Windows, realpath(3) elsewhere.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Durable;

/// <summary>One session directory holding <c>session.sqlite</c>, locked by this process (upstream <c>SessionLocation</c>).</summary>
internal sealed class DurableSessionLocation(string id, string directory, string cwd, bool created, PiLockHandle handle, DurableLockLoss loss)
    : IAsyncDisposable
{
    private int released;

    /// <summary>The directory name, <c>&lt;13-digit ms&gt;-&lt;uuid&gt;</c>.</summary>
    public string Id { get; } = id;
    /// <summary>The session directory.</summary>
    public string Directory { get; } = directory;
    /// <summary><c>&lt;directory&gt;/session.sqlite</c>.</summary>
    public string Database => Path.Join(Directory, "session.sqlite");
    /// <summary>The real path of the working directory.</summary>
    public string Cwd { get; } = cwd;
    /// <summary>Whether this call created the session.</summary>
    public bool Created { get; } = created;
    /// <summary>The held lock.</summary>
    internal PiLockHandle Lock { get; } = handle;

    /// <summary>Why the lock was lost (removed, or taken over by another process); null while it is held.</summary>
    internal DurableSessionLockLostException? LockLost => loss.Error;

    /// <summary>Calls <paramref name="handler"/> once when the lock is lost, at once when it already was. One handler.</summary>
    internal void OnLockLost(Action<DurableSessionLockLostException> handler) => loss.Attach(handler);

    /// <summary>Throws <see cref="LockLost"/> when the lock was lost.</summary>
    internal void ThrowIfLockLost()
    {
        if (loss.Error is { } lost) throw lost;
    }

    /// <summary>Releases the lock (upstream <c>release()</c>); idempotent.</summary>
    public async Task ReleaseAsync()
    {
        if (Interlocked.Exchange(ref released, 1) != 0) return;
        await Lock.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await ReleaseAsync().ConfigureAwait(false);
}

/// <summary>Session directories and their lock (upstream <c>sessions.ts</c>).</summary>
internal static partial class DurableSessions
{
    /// <summary>Upstream's lock options: retries 12 × 1 s, proper-lockfile's default 10 s stale time.</summary>
    internal static PiLockOptions DefaultLock { get; } = new() { Retries = 12, RetryDelay = TimeSpan.FromSeconds(1), Stale = TimeSpan.FromSeconds(10) };

    [GeneratedRegex("^[0-9]{13}-[0-9a-f-]{36}$")]
    private static partial Regex SessionName();

    /// <summary><c>&lt;agentDir&gt;/experimental/durable-sessions/&lt;sha256(cwd) hex[..24]&gt;</c> for a real cwd.</summary>
    internal static string RootFor(string agentDir, string realCwd) => Path.Join(agentDir, "experimental", "durable-sessions",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(realCwd)))[..24]);

    /// <summary>A new session for <paramref name="cwdInput"/>, or its newest one with <paramref name="continueSession"/> (upstream
    /// <c>selectSession</c>).</summary>
    /// <param name="agentDir">pi's agent directory (upstream <c>getAgentDir()</c>).</param>
    /// <param name="lockOptions">The lock's timing; null is upstream's (<see cref="DefaultLock"/>).</param>
    /// <param name="now">The clock of a new session's name; null is the wall clock.</param>
    internal static async Task<DurableSessionLocation> SelectAsync(string cwdInput, bool continueSession, string agentDir,
        PiLockOptions? lockOptions = null, Func<DateTimeOffset>? now = null, CancellationToken cancellationToken = default)
    {
        var cwd = RealPath(Path.GetFullPath(cwdInput));
        var root = RootFor(agentDir, cwd);
        System.IO.Directory.CreateDirectory(root);

        string directory;
        var created = false;
        if (continueSession)
        {
            // Directory names sort as JavaScript's default sort does (UTF-16 code units).
            var newest = new DirectoryInfo(root).EnumerateDirectories().Select(entry => entry.Name).Where(name => SessionName().IsMatch(name))
                .Order(StringComparer.Ordinal).LastOrDefault();
            if (newest is null) throw new InvalidOperationException($"No durable session exists for {cwd}");
            directory = Path.Join(root, newest);
        }
        else
        {
            var milliseconds = (now?.Invoke() ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            directory = Path.Join(root, $"{milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(13, '0')}-{Guid.NewGuid():D}");
            if (!PiLockfile.MakeDirectory(directory)) throw new IOException($"EEXIST: file already exists, mkdir '{directory}'");
            created = true;
        }

        PiLockHandle handle;
        // proper-lockfile's default onCompromised throws, which ends upstream's process: it never runs on with a lost lock. Here the loss is
        // recorded for the session to fail on (DurableRuntime.OpenAsync); a caller's own callback still runs.
        var loss = new DurableLockLoss(directory);
        var options = lockOptions ?? DefaultLock;
        var callerCallback = options.OnCompromised;
        options = options with { OnCompromised = error => { loss.Lose(error); callerCallback?.Invoke(error); } };
        try
        {
            // A lock left by a crashed process goes stale after 10 s; wait that long before giving up.
            handle = await PiLockfile.LockAsync(directory, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DurableSessionLockedException($"Session is already open in another process: {directory}", error);
        }
        return new DurableSessionLocation(Path.GetFileName(directory), directory, cwd, created, handle, loss);
    }

    /// <summary>Node's <c>fs.promises.realpath</c> (libuv <c>uv_fs_realpath</c>): every link resolved and, on Windows, every name in its
    /// on-disk spelling with the volume's drive letter.</summary>
    internal static string RealPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // FILE_FLAG_BACKUP_SEMANTICS opens directories; no access rights are needed to read the final path.
            using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) throw RealPathError(path, Marshal.GetLastPInvokeError());
            var buffer = new char[1024];
            while (true)
            {
                var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
                if (length == 0) throw RealPathError(path, Marshal.GetLastPInvokeError());
                if (length < buffer.Length) { var final = new string(buffer, 0, (int)length); return StripLongPrefix(final); }
                buffer = new char[length + 1];
            }
        }
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero) throw RealPathError(path, Marshal.GetLastPInvokeError());
        try { return Marshal.PtrToStringUTF8(resolved)!; }
        finally { free(resolved); }
    }

    /// <summary>libuv fs__realpath_handle: <c>\\?\UNC\server\share</c> becomes <c>\\server\share</c> and <c>\\?\C:\</c> becomes <c>C:\</c>.</summary>
    private static string StripLongPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\" + path[7..] :
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    private static IOException RealPathError(string path, int error) => error is 2 or 3
        ? new DirectoryNotFoundException($"ENOENT: no such file or directory, realpath '{path}'")
        : new IOException($"realpath '{path}' failed", OperatingSystem.IsWindows() ? new Win32Exception(error) : null);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);
}

/// <summary>The session's lock is held by another process.</summary>
internal sealed class DurableSessionLockedException(string message, Exception inner) : IOException(message, inner);

/// <summary>The session's lock was lost while this process held it: its directory was removed, or another process took it over (proper-lockfile:
/// compromised). Another process may be writing the session now, so this one must not commit to it again.</summary>
internal sealed class DurableSessionLockLostException(string directory, Exception cause)
    : IOException($"the session lock was lost (removed, or taken over by another process): {PiLockfile.LockPath(directory)}", cause);

/// <summary>The loss of a session's lock, reported by the lock's renewal (any thread) and handled by whoever attached.</summary>
internal sealed class DurableLockLoss(string directory)
{
    private readonly Lock gate = new();
    private Action<DurableSessionLockLostException>? handler;
    private volatile DurableSessionLockLostException? error;

    /// <summary>The loss; null while the lock is held.</summary>
    public DurableSessionLockLostException? Error => error;

    /// <summary>proper-lockfile <c>onCompromised</c>: records the loss and tells the handler. Never throws.</summary>
    public void Lose(Exception cause)
    {
        Action<DurableSessionLockLostException>? target;
        DurableSessionLockLostException lost;
        lock (gate)
        {
            if (error is not null) return;
            error = lost = new DurableSessionLockLostException(directory, cause);
            target = handler;
        }
        try { target?.Invoke(lost); } catch { /* The lock's callback must not throw. */ }
    }

    /// <summary>Sets the handler; it runs at once when the lock is already lost.</summary>
    public void Attach(Action<DurableSessionLockLostException> target)
    {
        DurableSessionLockLostException? lost;
        lock (gate)
        {
            handler = target;
            lost = error;
        }
        if (lost is not null) target(lost);
    }
}
