// Decision 0011 (run W3, fix 2, spend-core): one app per data folder. The app keeps <data>\webagent.lock open with FileShare.None for as long
// as it runs, the way PiSharp.Durable.Hosting's FileLock (internal there) keeps device\catalog.lock: the operating system releases the lock
// when the process ends, even on a crash, so there is nothing stale to detect, and on Unix .NET takes an advisory flock that also refuses a
// second open in the same process. What assumes this process is the only one on the folder relies on this lock, not on the order in which
// the hosted services happen to start: the Spend Guard's startup recovery marks every row left reserved as uncertain, which is right only
// when the process that reserved it is gone. WebAgentHost.CreateBuilder checks the lock first, so a second start on a folder in use is
// refused with one line and exit code 2 before it opens anything else (the device's catalog.lock was the only thing refusing it before, with
// a stack trace, and only because the chat area's hosted service happened to start before the spend area's). UseWebAgentCore then takes it
// for the app's life, before it prepares the folder, and the spend ledger is not made without it.
namespace PiSharp.WebAgent.Hosting;

/// <summary>The exclusive lock of a data folder (<see cref="FileName"/> held open with <see cref="FileShare.None"/>). Disposing it lets the
/// folder go; so does the end of the process.</summary>
public sealed class WebAgentDataLock : IDisposable
{
    /// <summary>The lock file's name in the data folder.</summary>
    public const string FileName = "webagent.lock";

    private FileStream? stream;

    private WebAgentDataLock(string dataDirectory, FileStream stream)
    {
        DataDirectory = dataDirectory;
        this.stream = stream;
    }

    /// <summary>The data folder this lock holds, an absolute path.</summary>
    public string DataDirectory { get; }

    /// <summary>Whether the lock is still held (until <see cref="Dispose"/>).</summary>
    public bool Held => Volatile.Read(ref stream) is not null;

    /// <summary>The lock file of <paramref name="dataDirectory"/>.</summary>
    public static string PathIn(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Join(Path.GetFullPath(dataDirectory), FileName);
    }

    /// <summary>Takes the lock of <paramref name="dataDirectory"/>, creating the folder if it is missing, or returns null while another
    /// holder has it (another process, or this one).</summary>
    /// <exception cref="InvalidOperationException">The folder cannot be created, or the lock file cannot be opened for another reason
    /// (no access, the path is a file).</exception>
    public static WebAgentDataLock? TryAcquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var folder = Path.GetFullPath(dataDirectory);
        try
        {
            Directory.CreateDirectory(folder);
            return new WebAgentDataLock(folder, new FileStream(Path.Join(folder, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1));
        }
        catch (IOException error) when (Directory.Exists(folder) && InUse(error))
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException($"The data folder {folder} cannot be used: {error.Message}", error);
        }
    }

    /// <summary>Takes the lock of <paramref name="dataDirectory"/> (see <see cref="TryAcquire"/>).</summary>
    /// <exception cref="DataFolderLockedException">Another holder has it: another Web Agent runs on the folder.</exception>
    /// <exception cref="InvalidOperationException">The folder cannot be created or the lock file opened for another reason.</exception>
    public static WebAgentDataLock Acquire(string dataDirectory) =>
        TryAcquire(dataDirectory) ?? throw new DataFolderLockedException(Path.GetFullPath(dataDirectory));

    /// <summary>Refuses a folder another holder has locked now: takes the lock and lets it go at once (the app takes it for good in
    /// <see cref="WebAgentHost.UseWebAgentCore"/>).</summary>
    /// <exception cref="DataFolderLockedException">Another holder has it: another Web Agent runs on the folder.</exception>
    /// <exception cref="InvalidOperationException">The folder cannot be created or the lock file opened for another reason.</exception>
    public static void ThrowIfLocked(string dataDirectory)
    {
        using var probe = Acquire(dataDirectory);
    }

    /// <summary>Lets the folder go; idempotent.</summary>
    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();

    /// <summary>Whether opening the lock file failed because another handle holds it: on Windows a sharing or lock violation (other I/O
    /// errors are reported as they are); on Unix .NET reports the held flock as an IOException.</summary>
    private static bool InUse(IOException error) =>
        !OperatingSystem.IsWindows() || (error.HResult & 0xFFFF) is 32 or 33;
}

/// <summary>Another holder has the data folder's <see cref="WebAgentDataLock"/>: another Web Agent runs on it. An
/// <see cref="InvalidOperationException"/>, so Program.cs refuses the start with its one-line message.</summary>
/// <param name="dataDirectory">The data folder, an absolute path.</param>
public sealed class DataFolderLockedException(string dataDirectory) : InvalidOperationException(
    $"The data folder {dataDirectory} is in use by another running Web Agent (it holds {WebAgentDataLock.FileName}). Stop that one first, or start this one with another --data folder.")
{
    /// <summary>The data folder, an absolute path.</summary>
    public string DataDirectory { get; } = dataDirectory;
}
