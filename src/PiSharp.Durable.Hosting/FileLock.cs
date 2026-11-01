// PiSharp-only (decision 0008). durable-pi creates <db>.lock with O_EXCL and detects a dead owner with kill(pid, 0); here the lock is the open
// file itself, so the operating system releases it when the process dies and there is nothing stale to detect. On Unix, .NET implements
// FileShare.None with an advisory flock(LOCK_EX), which also refuses a second open in the same process.
using System.Text;

namespace PiSharp.Durable.Hosting;

/// <summary>An exclusive lock held by keeping one file open with <see cref="FileShare.None"/>.</summary>
internal sealed class FileLock : IDisposable
{
    private FileStream? stream;

    private FileLock(FileStream stream) => this.stream = stream;

    /// <summary>Takes the lock, or returns null while another holder has it. <paramref name="owner"/> is written into the file for people
    /// who look; the lock does not depend on it.</summary>
    public static FileLock? TryAcquire(string path, string? owner = null)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
        }
        catch (IOException)
        {
            return null;
        }
        if (owner is not null)
        {
            try
            {
                stream.SetLength(0);
                stream.Write(Encoding.UTF8.GetBytes(owner));
                stream.Flush();
            }
            catch (IOException)
            {
                // The note is informational only.
            }
        }
        return new FileLock(stream);
    }

    /// <summary>Releases the lock; idempotent.</summary>
    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();
}
