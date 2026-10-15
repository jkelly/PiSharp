// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/images/node-worker.ts, and the PhotonThread class of
// packages/durable/src/images/node.ts.
// The worker of ImageProcessors.CreateThreaded(): images are prepared here, off the caller's thread. Requests are answered in order; a
// failure becomes an InvalidOperationException with its message, as upstream's reply carries only the message. After idleMs without a
// request the thread exits; ThreadedImageProcessor then starts a new one for the next image.
using PiSharp.Tools.Images;

namespace PiSharp.Durable.Tools.Images;

/// <summary>One request (upstream <c>PrepareRequest</c>): the image to prepare, and where its reply goes (upstream <c>PrepareReply</c>).</summary>
internal sealed record ImageRequest(byte[] Bytes, string MimeType, ImageLimits Limits)
{
    public TaskCompletionSource<PreparedImage?> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>One worker thread and the requests it has not answered; it exits after <c>idleMs</c> without requests.</summary>
internal sealed class ImageWorkerThread
{
    private readonly object gate = new();
    private readonly Queue<ImageRequest> pending = new();
    private readonly CodecImageProcessor images;
    private readonly TimeSpan idle;
    private bool exited;

    /// <summary>Starts the thread with its first request.</summary>
    public ImageWorkerThread(IImageCodec codec, double idleMs, ImageRequest first)
    {
        images = new CodecImageProcessor(codec);
        idle = TimeSpan.FromMilliseconds(Math.Max(0, idleMs));
        pending.Enqueue(first);
        new Thread(Run) { IsBackground = true, Name = "pi-durable images" }.Start();
    }

    /// <summary>Whether the thread has exited, or is about to (it takes no more requests).</summary>
    public bool Exited
    {
        get { lock (gate) return exited; }
    }

    /// <summary>Queues <paramref name="request"/>; false once the thread exited.</summary>
    public bool TryPost(ImageRequest request)
    {
        lock (gate)
        {
            if (exited) return false;
            pending.Enqueue(request);
            Monitor.Pulse(gate);
            return true;
        }
    }

    private void Run()
    {
        for (;;)
        {
            ImageRequest request;
            lock (gate)
            {
                while (pending.Count == 0)
                {
                    if (!Monitor.Wait(gate, idle) && pending.Count == 0)
                    {
                        exited = true;
                        return;
                    }
                }
                request = pending.Dequeue();
            }
            try
            {
                request.Reply.TrySetResult(images.Prepare(request.Bytes, request.MimeType, request.Limits));
            }
            catch (Exception error)
            {
                request.Reply.TrySetException(new InvalidOperationException(error.Message));
            }
        }
    }
}
