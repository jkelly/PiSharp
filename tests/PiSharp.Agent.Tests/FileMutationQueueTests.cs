using System.Collections.Concurrent;
using PiSharp.Agent.Tools;

internal static class FileMutationQueueTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("file mutation queue same-key FIFO survives operation failure", SameKeyFifo),
        ("file mutation queue unrelated ordinal keys overlap", DifferentKeys),
        ("file mutation queue serialized async resolver preserves authored alias order", ResolverOrderAndAliases),
        ("file mutation queue awaits failing resolver cleanup without poisoning registration", ResolverFailure),
        ("file mutation queue pre-admission and registration-wait cancellation retain order", RegistrationCancellation),
        ("file mutation queue canceled key waiter cannot release active predecessor", CanceledWaiter),
        ("file mutation queue execution cancellation awaits cleanup and preserves returned result", ExecutionCancellation),
        ("file mutation queue operation/key/character bounds reject atomically and recover", LogicalBounds),
        ("file mutation queue concurrent admission obeys exact operation cap", ConcurrentAdmission),
        ("file mutation queue validates native options and exact UTF16 key inputs", Validation)
    ];

    private static async Task SameKeyFifo()
    {
        var queue = Identity();
        var order = new ConcurrentQueue<string>();
        var firstStarted = Gate(); var firstRelease = Gate(); var secondStarted = Gate(); var secondRelease = Gate();
        var first = queue.RunAsync<int>("same", async _ =>
        {
            order.Enqueue("first.start"); firstStarted.TrySetResult();
            try { await firstRelease.Task; return 11; }
            finally { order.Enqueue("first.cleanup"); }
        });
        await firstStarted.Task;
        var second = queue.RunAsync<int>("same", async _ =>
        {
            order.Enqueue("second.start"); secondStarted.TrySetResult();
            try { await secondRelease.Task; throw new MarkerException(); }
            finally { order.Enqueue("second.cleanup"); }
        });
        var third = queue.RunAsync<int>("same", _ => { order.Enqueue("third.start"); return ValueTask.FromResult(33); });
        try
        {
            Equal(new FileMutationQueueSnapshot(3, 1), queue.Snapshot);
            Check(!secondStarted.Task.IsCompleted && !third.IsCompleted, "Same-key successor overtook active work.");
            firstRelease.TrySetResult(); Equal(11, await first); await secondStarted.Task;
            Check(!third.IsCompleted, "Third operation overtook second operation.");
            secondRelease.TrySetResult(); await ThrowsAsync<MarkerException>(() => second); Equal(33, await third);
            Sequence(["first.start", "first.cleanup", "second.start", "second.cleanup", "third.start"], order);
            Empty(queue);
        }
        finally { firstRelease.TrySetResult(); secondRelease.TrySetResult(); await Settle(first, second, third); }
    }

    private static async Task DifferentKeys()
    {
        var queue = Identity();
        var upperStarted = Gate(); var lowerStarted = Gate(); var release = Gate();
        var upper = queue.RunAsync<int>("FILE", async _ => { upperStarted.TrySetResult(); await release.Task; return 1; });
        var lower = queue.RunAsync<int>("file", async _ => { lowerStarted.TrySetResult(); await release.Task; return 2; });
        try
        {
            await upperStarted.Task; await lowerStarted.Task;
            Equal(new FileMutationQueueSnapshot(2, 2), queue.Snapshot);
            Check(!upper.IsCompleted && !lower.IsCompleted, "Independent operations did not overlap.");
            release.TrySetResult(); Equal(1, await upper); Equal(2, await lower); Empty(queue);
        }
        finally { release.TrySetResult(); await Settle(upper, lower); }
    }

    private static async Task ResolverOrderAndAliases()
    {
        var order = new ConcurrentQueue<string>();
        var firstResolver = Gate(); var releaseFirstResolver = Gate(); var secondResolver = Gate(); var releaseSecondResolver = Gate();
        var firstStarted = Gate(); var releaseFirst = Gate(); var thirdStarted = Gate();
        var resolving = 0; var calls = 0;
        var queue = new FileMutationQueue(async (path, _) =>
        {
            Check(Interlocked.Increment(ref resolving) == 1, "Host key resolvers overlapped.");
            Interlocked.Increment(ref calls); order.Enqueue("resolve." + path);
            try
            {
                if (path == "authored-target") { firstResolver.TrySetResult(); await releaseFirstResolver.Task; }
                if (path == "authored-alias") { secondResolver.TrySetResult(); await releaseSecondResolver.Task; }
                return path == "other" ? "other-key" : "same-key";
            }
            finally { Interlocked.Decrement(ref resolving); }
        });
        var first = queue.RunAsync<int>("authored-target", async _ =>
        {
            firstStarted.TrySetResult();
            // State reads remain available from operation callbacks.
            Check(queue.Snapshot.RegisteredOperations > 0, "Operation did not retain its reservation.");
            await releaseFirst.Task; return 1;
        });
        await firstResolver.Task;
        var second = queue.RunAsync<int>("authored-alias", _ => ValueTask.FromResult(2));
        var third = queue.RunAsync<int>("other", _ => { thirdStarted.TrySetResult(); return ValueTask.FromResult(3); });
        try
        {
            Equal(1, calls); Equal(new FileMutationQueueSnapshot(3, 0), queue.Snapshot);
            Check(!secondResolver.Task.IsCompleted, "Later resolver overtook an awaited resolver.");
            releaseFirstResolver.TrySetResult(); await secondResolver.Task; await firstStarted.Task;
            Check(!thirdStarted.Task.IsCompleted, "Third resolver bypassed serialized registration.");
            releaseSecondResolver.TrySetResult(); await thirdStarted.Task; Equal(3, await third);
            Check(!second.IsCompleted && !first.IsCompleted, "Authored alias did not share the first reservation.");
            releaseFirst.TrySetResult(); Equal(1, await first); Equal(2, await second);
            Sequence(["resolve.authored-target", "resolve.authored-alias", "resolve.other"], order);
            Equal(0, resolving); Empty(queue);
        }
        finally
        { releaseFirstResolver.TrySetResult(); releaseSecondResolver.TrySetResult(); releaseFirst.TrySetResult(); await Settle(first, second, third); }
    }

    private static async Task ResolverFailure()
    {
        var started = Gate(); var release = Gate(); var cleanup = Gate(); var releaseCleanup = Gate(); var nextResolver = Gate();
        var effects = 0;
        var queue = new FileMutationQueue(async (path, _) =>
        {
            if (path == "bad")
            {
                started.TrySetResult();
                try { await release.Task; throw new MarkerException(); }
                finally { cleanup.TrySetResult(); await releaseCleanup.Task; }
            }
            nextResolver.TrySetResult(); return path;
        });
        var first = queue.RunAsync<int>("bad", _ => { effects++; return ValueTask.FromResult(1); });
        await started.Task;
        var second = queue.RunAsync<int>("good", _ => { effects++; return ValueTask.FromResult(2); });
        try
        {
            release.TrySetResult(); await cleanup.Task;
            Check(!first.IsCompleted && !nextResolver.Task.IsCompleted, "Registration released before resolver cleanup settled.");
            releaseCleanup.TrySetResult(); await ThrowsAsync<MarkerException>(() => first); Equal(2, await second);
            Equal(1, effects); Empty(queue);
        }
        finally { release.TrySetResult(); releaseCleanup.TrySetResult(); await Settle(first, second); }
    }

    private static async Task RegistrationCancellation()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        var unusedResolvers = 0; var unusedEffects = 0;
        var unused = new FileMutationQueue((path, _) => { unusedResolvers++; return ValueTask.FromResult(path); });
        var canceled = Throws<OperationCanceledException>(() => unused.RunAsync<int>("key", _ =>
            { unusedEffects++; return ValueTask.FromResult(0); }, before.Token));
        Equal(before.Token, canceled.CancellationToken); Equal(0, unusedResolvers); Equal(0, unusedEffects); Empty(unused);

        using var waiting = new CancellationTokenSource();
        var firstResolver = Gate(); var releaseFirstResolver = Gate(); var lastResolver = Gate();
        var order = new ConcurrentQueue<string>(); var skippedEffects = 0;
        var queue = new FileMutationQueue(async (path, _) =>
        {
            order.Enqueue(path);
            if (path == "first") { firstResolver.TrySetResult(); await releaseFirstResolver.Task; }
            if (path == "last") lastResolver.TrySetResult();
            return path;
        });
        var first = queue.RunAsync<int>("first", _ => ValueTask.FromResult(1));
        await firstResolver.Task;
        var second = queue.RunAsync<int>("skipped", _ => { skippedEffects++; return ValueTask.FromResult(2); }, waiting.Token);
        var last = queue.RunAsync<int>("last", _ => ValueTask.FromResult(3));
        try
        {
            waiting.Cancel();
            Check(!second.IsCompleted && !lastResolver.Task.IsCompleted, "Canceled registration broke resolver order.");
            Equal(new FileMutationQueueSnapshot(3, 0), queue.Snapshot);
            releaseFirstResolver.TrySetResult(); Equal(1, await first);
            Equal(waiting.Token, (await ThrowsAsync<OperationCanceledException>(() => second)).CancellationToken);
            Equal(3, await last); Equal(0, skippedEffects); Sequence(["first", "last"], order); Empty(queue);
        }
        finally { releaseFirstResolver.TrySetResult(); await Settle(first, second, last); }
        await ResolverCancellationCleanup();
    }

    private static async Task ResolverCancellationCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = Gate(); var never = Gate(); var cleanup = Gate(); var releaseCleanup = Gate(); var nextResolver = Gate();
        var effects = 0;
        var queue = new FileMutationQueue(async (path, token) =>
        {
            if (path == "first")
            {
                Equal(cancellation.Token, token); entered.TrySetResult();
                try { await never.Task.WaitAsync(token); return path; }
                finally { cleanup.TrySetResult(); await releaseCleanup.Task; }
            }
            nextResolver.TrySetResult(); return path;
        });
        var first = queue.RunAsync<int>("first", _ => { effects++; return ValueTask.FromResult(1); }, cancellation.Token);
        await entered.Task;
        var next = queue.RunAsync<int>("next", _ => { effects++; return ValueTask.FromResult(2); });
        try
        {
            cancellation.Cancel(); await cleanup.Task;
            Check(!first.IsCompleted && !nextResolver.Task.IsCompleted, "Canceled resolver cleanup was detached.");
            Equal(new FileMutationQueueSnapshot(2, 0), queue.Snapshot);
            releaseCleanup.TrySetResult(); await ThrowsAsync<OperationCanceledException>(() => first);
            Equal(2, await next); Equal(1, effects); Empty(queue);
        }
        finally { cancellation.Cancel(); never.TrySetResult(); releaseCleanup.TrySetResult(); await Settle(first, next); }
    }

    private static async Task CanceledWaiter()
    {
        using var waiting = new CancellationTokenSource();
        var queue = Identity(); var firstStarted = Gate(); var firstRelease = Gate(); var cleanup = Gate(); var cleanupRelease = Gate();
        var thirdStarted = Gate(); var skippedEffects = 0;
        var first = queue.RunAsync<int>("same", async _ =>
        {
            firstStarted.TrySetResult();
            try { await firstRelease.Task; return 1; }
            finally { cleanup.TrySetResult(); await cleanupRelease.Task; }
        });
        await firstStarted.Task;
        var second = queue.RunAsync<int>("same", _ => { skippedEffects++; return ValueTask.FromResult(2); }, waiting.Token);
        var third = queue.RunAsync<int>("same", _ => { thirdStarted.TrySetResult(); return ValueTask.FromResult(3); });
        try
        {
            waiting.Cancel(); Check(!second.IsCompleted && !thirdStarted.Task.IsCompleted, "Canceled waiter released its predecessor chain.");
            Equal(new FileMutationQueueSnapshot(3, 1), queue.Snapshot);
            firstRelease.TrySetResult(); await cleanup.Task;
            Check(!second.IsCompleted && !thirdStarted.Task.IsCompleted, "Successor ran during predecessor cleanup.");
            cleanupRelease.TrySetResult(); Equal(1, await first);
            await ThrowsAsync<OperationCanceledException>(() => second); Equal(3, await third); Equal(0, skippedEffects); Empty(queue);
        }
        finally { firstRelease.TrySetResult(); cleanupRelease.TrySetResult(); await Settle(first, second, third); }
    }

    private static async Task ExecutionCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var queue = Identity(); var entered = Gate(); var never = Gate(); var cleanup = Gate(); var releaseCleanup = Gate(); var successorStarted = Gate();
        var first = queue.RunAsync<int>("same", async token =>
        {
            Equal(cancellation.Token, token); entered.TrySetResult();
            try { await never.Task.WaitAsync(token); return 1; }
            finally { cleanup.TrySetResult(); await releaseCleanup.Task; }
        }, cancellation.Token);
        await entered.Task;
        var second = queue.RunAsync<int>("same", _ => { successorStarted.TrySetResult(); return ValueTask.FromResult(2); });
        try
        {
            cancellation.Cancel(); await cleanup.Task;
            Check(!first.IsCompleted && !successorStarted.Task.IsCompleted, "Execution cancellation detached admitted cleanup.");
            Equal(3, await queue.RunAsync<int>("other", _ => ValueTask.FromResult(3)));
            releaseCleanup.TrySetResult(); await ThrowsAsync<OperationCanceledException>(() => first); Equal(2, await second); Empty(queue);
        }
        finally { cancellation.Cancel(); never.TrySetResult(); releaseCleanup.TrySetResult(); await Settle(first, second); }

        using var ignored = new CancellationTokenSource();
        var releaseReturn = Gate(); var knownResult = new object(); var ignoredStarted = Gate(); var afterStarted = Gate();
        var returnsKnown = queue.RunAsync<object>("same", async _ =>
            { ignoredStarted.TrySetResult(); await releaseReturn.Task; return knownResult; }, ignored.Token);
        await ignoredStarted.Task;
        var after = queue.RunAsync<int>("same", _ => { afterStarted.TrySetResult(); return ValueTask.FromResult(4); });
        try
        {
            ignored.Cancel(); Check(!returnsKnown.IsCompleted && !afterStarted.Task.IsCompleted, "Ignoring cancellation caused early reservation release.");
            releaseReturn.TrySetResult(); Check(ReferenceEquals(knownResult, await returnsKnown), "Late cancellation concealed a returned completed result.");
            Equal(4, await after); Empty(queue);
        }
        finally { releaseReturn.TrySetResult(); await Settle(returnsKnown, after); }
    }

    private static async Task LogicalBounds()
    {
        var resolverEntered = Gate(); var releaseResolver = Gate(); var releaseOperation = Gate(); var resolverCalls = 0;
        var queue = new FileMutationQueue(async (_, _) =>
        {
            Interlocked.Increment(ref resolverCalls); resolverEntered.TrySetResult(); await releaseResolver.Task; return "same";
        }, new(MaximumRegisteredOperations: 2));
        var first = queue.RunAsync<int>("one", async _ => { await releaseOperation.Task; return 1; });
        await resolverEntered.Task;
        var second = queue.RunAsync<int>("two", _ => ValueTask.FromResult(2));
        try
        {
            Equal(FileMutationQueueLimit.RegisteredOperations, Throws<FileMutationQueueLimitException>(() =>
                queue.RunAsync<int>("three", _ => ValueTask.FromResult(3))).Limit);
            Equal(1, resolverCalls); Equal(new FileMutationQueueSnapshot(2, 0), queue.Snapshot);
            releaseResolver.TrySetResult(); releaseOperation.TrySetResult(); Equal(1, await first); Equal(2, await second);
            Equal(3, await queue.RunAsync<int>("three", _ => ValueTask.FromResult(3))); Empty(queue);
        }
        finally { releaseResolver.TrySetResult(); releaseOperation.TrySetResult(); await Settle(first, second); }

        var holdKey = Gate(); var activeStarted = Gate(); var deniedEffects = 0;
        var keys = Identity(new(MaximumKeys: 1));
        var active = keys.RunAsync<int>("a", async _ => { activeStarted.TrySetResult(); await holdKey.Task; return 1; });
        await activeStarted.Task;
        var denied = keys.RunAsync<int>("b", _ => { deniedEffects++; return ValueTask.FromResult(2); });
        var same = keys.RunAsync<int>("a", _ => ValueTask.FromResult(3));
        try
        {
            Equal(FileMutationQueueLimit.Keys, (await ThrowsAsync<FileMutationQueueLimitException>(() => denied)).Limit);
            Equal(0, deniedEffects); Equal(new FileMutationQueueSnapshot(2, 1), keys.Snapshot);
            holdKey.TrySetResult(); Equal(1, await active); Equal(3, await same);
            Equal(4, await keys.RunAsync<int>("b", _ => ValueTask.FromResult(4))); Empty(keys);
        }
        finally { holdKey.TrySetResult(); await Settle(active, denied, same); }

        var keyCalls = 0; var characterEffects = 0;
        var characters = new FileMutationQueue((path, _) => { keyCalls++; return ValueTask.FromResult(path == "bad" ? "long" : "key"); },
            new(MaximumKeyCharacters: 3));
        Equal(FileMutationQueueLimit.KeyCharacters, Throws<FileMutationQueueLimitException>(() =>
            characters.RunAsync<int>("long", _ => { characterEffects++; return ValueTask.FromResult(1); })).Limit);
        Equal(0, keyCalls); Empty(characters);
        Equal(FileMutationQueueLimit.KeyCharacters, (await ThrowsAsync<FileMutationQueueLimitException>(() =>
            characters.RunAsync<int>("bad", _ => { characterEffects++; return ValueTask.FromResult(1); }))).Limit);
        Equal(0, characterEffects); Empty(characters);
        Equal(2, await characters.RunAsync<int>("ok", _ => ValueTask.FromResult(2))); Empty(characters);
    }

    private static async Task ConcurrentAdmission()
    {
        const int attempts = 32;
        var queue = Identity(new(MaximumRegisteredOperations: 4));
        var launch = Gate(); var allAttempted = Gate(); var release = Gate(); var attempted = 0; var effects = 0;
        var workers = Enumerable.Range(0, attempts).Select(_ => Attempt()).ToArray();
        try
        {
            launch.TrySetResult(); await allAttempted.Task;
            Equal(new FileMutationQueueSnapshot(4, 1), queue.Snapshot);
            release.TrySetResult(); var outcomes = await Task.WhenAll(workers);
            Equal(4, outcomes.Count(value => value)); Equal(attempts - 4, outcomes.Count(value => !value)); Equal(4, effects); Empty(queue);
        }
        finally { launch.TrySetResult(); release.TrySetResult(); await Settle(workers); }

        async Task<bool> Attempt()
        {
            await launch.Task;
            Task<int>? admitted = null;
            try { admitted = queue.RunAsync<int>("same", async _ => { Interlocked.Increment(ref effects); await release.Task; return 1; }); }
            catch (FileMutationQueueLimitException error) { Equal(FileMutationQueueLimit.RegisteredOperations, error.Limit); }
            finally { if (Interlocked.Increment(ref attempted) == attempts) allAttempted.TrySetResult(); }
            if (admitted is null) return false;
            Equal(1, await admitted); return true;
        }
    }

    private static async Task Validation()
    {
        Throws<ArgumentNullException>(() => new FileMutationQueue(null!));
        foreach (var options in new FileMutationQueueOptions[]
            { new(MaximumRegisteredOperations: 0), new(MaximumKeys: -1), new(MaximumKeyCharacters: 0) })
            Throws<ArgumentOutOfRangeException>(() => Identity(options));
        var queue = Identity(); var effects = 0;
        Throws<ArgumentNullException>(() => queue.RunAsync<int>("key", null!));
        foreach (var key in new string[] { null!, "", "a\0b", "\ud800", "\udc00" })
            Throws<ArgumentException>(() => queue.RunAsync<int>(key, _ => { effects++; return ValueTask.FromResult(0); }));
        foreach (var key in new string[] { null!, "", "a\0b", "\ud800", "\udc00" })
        {
            var invalid = new FileMutationQueue((_, _) => ValueTask.FromResult(key));
            await ThrowsAsync<ArgumentException>(() => invalid.RunAsync<int>("input", _ => { effects++; return ValueTask.FromResult(0); }));
            Empty(invalid);
        }
        Equal(0, effects); Empty(queue);
        Equal(1, await Identity(new(MaximumKeyCharacters: 2)).RunAsync<int>("\U0001f642", _ => ValueTask.FromResult(1)));
        Equal(2, await queue.RunAsync<int>(" ", _ => ValueTask.FromResult(2)));
        Empty(queue);
    }

    private static FileMutationQueue Identity(FileMutationQueueOptions? options = null) =>
        new((path, _) => ValueTask.FromResult(path), options);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Empty(FileMutationQueue queue) => Equal(new FileMutationQueueSnapshot(0, 0), queue.Snapshot);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Registration or operation order changed.");
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static async Task Settle(params Task[] tasks) { foreach (var task in tasks) { try { await task; } catch { } } }
    private sealed class MarkerException : Exception { }
}
