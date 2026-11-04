// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/line.ts
//
// An object's line (§9): its transactions, cold loads, key pages and eviction sweeps run on it one at a time, in the order they asked.
// Upstream's line is a promise chain; here each holder's release completes a task the next holder waits for, and the tail is swapped under
// a short lock, as callers may ask from several threads (a residency timer fires on the thread pool). A holder is never handed the line
// inline: it yields first when nothing is ahead and resumes after its predecessor's release asynchronously, as a promise's `then` runs on a
// later microtask (StoreInternal.Turn, as the store's commit queue).
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Documents;

/// <summary>An object's line (upstream <c>Line</c>): <see cref="AcquireAsync"/> completes with the release once every earlier holder
/// released; releasing twice is harmless.</summary>
internal sealed class Line
{
    private readonly Lock gate = new();
    private Task tail = Task.CompletedTask;

    /// <summary>Completes with this holder's release once every earlier holder released.</summary>
    public async Task<Action> AcquireAsync()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (gate)
        {
            previous = tail;
            tail = released.Task;
        }
        await StoreInternal.Turn(previous).ConfigureAwait(false);
        return () => released.TrySetResult();
    }
}
