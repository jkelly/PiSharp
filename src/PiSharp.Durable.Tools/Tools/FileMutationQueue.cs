// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/tools/file-mutation-queue.ts
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Tools;

/// <summary>Upstream <c>withFileMutationQueue</c>.</summary>
internal static class FileMutationQueue
{
    /// <summary>Tail of the mutation chain of each file, keyed by file system id and canonical path.</summary>
    private static readonly Dictionary<string, Task> Queues = new(StringComparer.Ordinal);

    private static async Task<string> MutationKeyAsync(IExecutionEnv env, string path, DurableContext context)
    {
        var absolutePath = (await env.AbsolutePathAsync(path, context).ConfigureAwait(false)).GetOrThrow();
        return $"{env.Id}\0{await CanonicalAsync(env, absolutePath, context).ConfigureAwait(false)}";
    }

    /// <summary>
    /// The canonical path; for a file that does not exist yet, its canonical parent joined with its name, so a <c>write</c> that creates a
    /// file and a later mutation of it share one key even under a symlinked directory.
    /// </summary>
    private static async Task<string> CanonicalAsync(IExecutionEnv env, string absolutePath, DurableContext context)
    {
        var result = await env.CanonicalPathAsync(absolutePath, context).ConfigureAwait(false);
        if (result.IsOk) return result.Value;
        if (result.Error.Code == FileErrorCode.NotSupported) return absolutePath;
        if (result.Error.Code != FileErrorCode.NotFound) throw result.Error;
        // The file system splits the path, so a name may contain characters that are separators elsewhere.
        var parent = (await env.JoinPathAsync([absolutePath, ".."], context).ConfigureAwait(false)).GetOrThrow();
        if (parent == absolutePath || !absolutePath.StartsWith(parent, StringComparison.Ordinal)) return absolutePath;
        var name = absolutePath[(parent.Length + (parent.EndsWith('/') || parent.EndsWith('\\') ? 0 : 1))..];
        var canonicalParent = await CanonicalAsync(env, parent, context).ConfigureAwait(false);
        return (await env.JoinPathAsync([canonicalParent, name], context).ConfigureAwait(false)).GetOrThrow();
    }

    /// <summary>
    /// Serializes <c>edit</c> and <c>write</c> mutations of one file within this process: same file system id and canonical path, whichever
    /// environment object the call got. Other files, and other file systems, never wait. Concurrent calls on one file run in the order their
    /// keys resolve. Not a lock against <c>bash</c> or other processes.
    /// </summary>
    public static async Task<T> RunAsync<T>(IExecutionEnv env, string path, Func<Task<T>> fn, DurableContext context)
    {
        var key = await MutationKeyAsync(env, path, context).ConfigureAwait(false);
        // Take the slot without awaiting, so no other call can take it in between.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous, tail;
        lock (Queues)
        {
            previous = Queues.GetValueOrDefault(key) ?? Task.CompletedTask;
            tail = Chain(previous, done.Task);
            Queues[key] = tail;
        }
        await previous.ConfigureAwait(false);
        try
        {
            return await fn().ConfigureAwait(false);
        }
        finally
        {
            done.SetResult();
            lock (Queues)
                if (Queues.TryGetValue(key, out var current) && current == tail) Queues.Remove(key);
        }
    }

    private static async Task Chain(Task previous, Task done)
    {
        await previous.ConfigureAwait(false);
        await done.ConfigureAwait(false);
    }
}
