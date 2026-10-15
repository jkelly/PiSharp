// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/images/node.ts
// Upstream's Node loader reads Photon's WebAssembly from the installed package and prepares images in a worker thread, so a large photo does
// not block the event loop. Here the codec is a .NET object the host passes (Images.cs), and the worker is a dedicated background thread
// (NodeWorker.cs) that takes requests in order and exits after IdleMs without one; the next image starts a new one. A background thread does
// not keep the process alive, as upstream's unref()'d worker does not while it is idle. A thread that cannot start falls back to the calling
// thread from then on, as upstream falls back when its worker cannot start.
using PiSharp.Tools.Images;

namespace PiSharp.Durable.Tools.Images;

/// <summary>Options of <see cref="ImageProcessors.CreateThreaded"/> (upstream <c>NodePhotonImagesOptions</c>).</summary>
public sealed record ThreadedImagesOptions
{
    /// <summary>Upstream's default <c>idleMs</c>: 30 s.</summary>
    public const double DefaultIdleMs = 30_000;

    /// <summary>
    /// Prepare images on a worker thread, so a large photo does not hold the caller for the second or two it takes. Default true; false
    /// prepares them on the calling thread.
    /// </summary>
    public bool Worker { get; init; } = true;

    /// <summary>How long the worker stays after its last image before it exits. The next image starts a new one. Default 30 s.</summary>
    public double IdleMs { get; init; } = DefaultIdleMs;
}

/// <summary>Upstream <c>createNodePhotonImages</c>'s processor: one worker at a time, started on demand.</summary>
internal sealed class ThreadedImageProcessor(IImageCodec codec, double idleMs) : IImageProcessor
{
    private readonly Lock gate = new();
    private ImageWorkerThread? thread;
    private IImageProcessor? fallback;
    private int started;

    /// <summary>Worker threads started so far (tests).</summary>
    internal int ThreadsStarted => Volatile.Read(ref started);

    /// <summary>Whether a worker thread is running (tests).</summary>
    internal bool Running
    {
        get { lock (gate) return thread is { Exited: false }; }
    }

    public Task<PreparedImage?> PrepareAsync(ReadOnlyMemory<byte> bytes, string mimeType, ImageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(mimeType);
        ArgumentNullException.ThrowIfNull(limits);
        // A copy: the caller keeps its bytes.
        var request = new ImageRequest(bytes.ToArray(), mimeType, limits);
        lock (gate)
        {
            if (fallback is null)
            {
                if (thread is not null && thread.TryPost(request)) return request.Reply.Task;
                try
                {
                    thread = new ImageWorkerThread(codec, idleMs, request);
                    Interlocked.Increment(ref started);
                    return request.Reply.Task;
                }
                catch (Exception error) when (error is ThreadStartException or OutOfMemoryException or InvalidOperationException)
                {
                    // No worker on this platform or build: prepare here from now on.
                    thread = null;
                    fallback = ImageProcessors.Create(codec);
                }
            }
        }
        return fallback.PrepareAsync(request.Bytes, mimeType, limits);
    }
}
