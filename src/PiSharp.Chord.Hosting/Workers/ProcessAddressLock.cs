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
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory) + "|" + name)));
        var mutexName = (OperatingSystem.IsWindows() ? "Local\\" : "") + "PiSharpChord-" + key;
        var released = new ManualResetEvent(false);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Mutex ownership is thread-affine. One dedicated worker takes and releases it; the caller may dispose on any thread.
        var finished = Task.Factory.StartNew(() =>
        {
            Mutex? mutex = null; var held = false;
            try
            {
                mutex = new Mutex(false, mutexName);
                // Unix cannot combine a named mutex with another wait handle. Keep every
                // bounded wait and the eventual release on this same ownership thread.
                while (!held)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { held = mutex.WaitOne(50); }
                    catch (AbandonedMutexException) { held = true; }
                }
                cancellationToken.ThrowIfCancellationRequested();
                acquired.TrySetResult(); released.WaitOne();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { acquired.TrySetCanceled(cancellationToken); }
            catch (Exception error) { acquired.TrySetException(error); }
            finally { if (held) mutex!.ReleaseMutex(); mutex?.Dispose(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { await acquired.Task.ConfigureAwait(false); return new(released, finished); }
        catch { released.Set(); await finished.ConfigureAwait(false); released.Dispose(); throw; }
    }
    private int disposed;
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; released.Set(); finished.GetAwaiter().GetResult(); released.Dispose(); }
}
