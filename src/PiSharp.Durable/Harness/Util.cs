// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/util.ts. Waiters are guarded by a short lock, since .NET
// callers may add and resolve from any thread; a waiter's task completes asynchronously (a promise's continuation is a later microtask),
// so resolving one never runs its caller's code under the lock or inline in a Session listener.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>Pending waits by key. Each settles once: through <see cref="Resolve"/>, <see cref="RejectAll"/>, or cancellation of its
/// context (upstream <c>Waiters</c>).</summary>
internal sealed class Waiters<TKey, T>
{
    private readonly Lock gate = new();
    private readonly Dictionary<Key, HashSet<TaskCompletionSource<T>>> sets = [];

    /// <summary>A pending wait for <paramref name="key"/>; rejects at once when <paramref name="context"/> is already cancelled.</summary>
    public Task<T> Add(TKey key, DurableContext context)
    {
        var signal = context.Signal;
        if (signal is { Token.IsCancellationRequested: true }) return Task.FromException<T>(signal.Error());
        var waiter = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slot = new Key(key);
        HashSet<TaskCompletionSource<T>> set;
        lock (gate)
        {
            if (!sets.TryGetValue(slot, out var existing)) sets[slot] = existing = [];
            set = existing;
            set.Add(waiter);
        }
        if (signal is not null)
        {
            var registration = signal.Token.UnsafeRegister(_ =>
            {
                lock (gate)
                {
                    set.Remove(waiter);
                    if (set.Count == 0 && sets.TryGetValue(slot, out var current) && ReferenceEquals(current, set)) sets.Remove(slot);
                }
                waiter.TrySetException(signal.Error());
            }, null);
            waiter.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Unregister(), registration,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
        }
        return waiter.Task;
    }

    /// <summary>Keys with pending waits.</summary>
    public List<TKey> Keys()
    {
        lock (gate) return [.. sets.Keys.Select(key => key.Value)];
    }

    /// <summary>Resolves every wait for <paramref name="key"/>.</summary>
    public void Resolve(TKey key, T value)
    {
        HashSet<TaskCompletionSource<T>>? set;
        lock (gate)
        {
            if (sets.Remove(new Key(key), out set)) set = [.. set];
        }
        foreach (var waiter in set ?? []) waiter.TrySetResult(value);
    }

    /// <summary>Rejects every pending wait with <paramref name="error"/>.</summary>
    public void RejectAll(Exception error)
    {
        List<TaskCompletionSource<T>> waiters;
        lock (gate)
        {
            waiters = [.. sets.Values.SelectMany(set => set)];
            sets.Clear();
        }
        foreach (var waiter in waiters) waiter.TrySetException(error);
    }

    // A nullable key slot: upstream keys idle waits by conversation, with `undefined` for the whole Harness.
    private readonly record struct Key(TKey Value);
}

/// <summary>Harness utilities (upstream <c>util.ts</c>).</summary>
internal static class HarnessUtil
{
    /// <summary>Every item of a paginated scan, in page order (upstream <c>scanAll</c>).</summary>
    public static async Task<List<T>> ScanAll<T>(Func<JsonObjectValue?, Task<Page<T>>> scan)
    {
        var items = new List<T>();
        JsonObjectValue? cursor = null;
        do
        {
            var page = await scan(cursor).ConfigureAwait(false);
            items.AddRange(page.Items);
            cursor = page.Next;
        } while (cursor is not null);
        return items;
    }

    /// <summary>Upstream <c>closedError()</c>.</summary>
    public static InvalidOperationException ClosedError() => new("Harness is closed");
}
