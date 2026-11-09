using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Chord.Hosting.Workers;

/// <summary>One process holds an address at once. The OS releases this exclusive handle after a crash.</summary>
public sealed class ProcessAddressLock : IDisposable
{
    private readonly ManualResetEvent released;
    private readonly Task finished;
    private ProcessAddressLock(ManualResetEvent released, Task finished) { this.released = released; this.finished = finished; }
    public static async Task<ProcessAddressLock> AcquireAsync(string name, string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory) + "|" + name)));
        var mutexName = (OperatingSystem.IsWindows() ? "Local\\" : "") + "PiSharpChord-" + key;
        var released = new ManualResetEvent(false);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Mutex ownership is thread-affine. One dedicated worker takes and releases it; the caller may dispose on any thread.
        var finished = Task.Factory.StartNew(() =>
        {
            using var mutex = new Mutex(false, mutexName); var held = false;
            try
            {
                try { held = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]) == 0; }
                catch (AbandonedMutexException) { held = true; }
                if (!held) { acquired.TrySetCanceled(cancellationToken); return; }
                acquired.TrySetResult(); released.WaitOne();
            }
            catch (Exception error) { acquired.TrySetException(error); }
            finally { if (held) mutex.ReleaseMutex(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { await acquired.Task.ConfigureAwait(false); return new(released, finished); }
        catch { released.Set(); await finished.ConfigureAwait(false); released.Dispose(); throw; }
    }
    private int disposed;
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; released.Set(); finished.GetAwaiter().GetResult(); released.Dispose(); }
}
