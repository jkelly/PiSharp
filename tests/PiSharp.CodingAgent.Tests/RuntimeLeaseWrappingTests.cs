using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;

internal static class RuntimeLeaseWrappingTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("runtime lease rejects invalid resource wrappers before ownership transfer", InvalidWrappers),
        ("runtime lease resource wrapper reserves disposal and retains original cleanup faults", Reservation)
    ];
    private static async Task InvalidWrappers()
    {
        await using var profile = await OfflineSessionProfile.CreateAsync(Path.GetTempPath(),
            Path.Combine(Path.GetTempPath(), "lease-wrap-" + Guid.NewGuid().ToString("N") + ".jsonl"), null, [], [], [], default);
        var resource = new Resource(); var lease = new SessionRuntimeLease(profile.Registry, resource);
        var called = 0;
        Func<IAsyncDisposable?, IAsyncDisposable> callback = _ => { called++; return new Resource(); };
        Reject(() => lease.WrapResourcesBeforeClaim(callback + callback)); Check(called == 0);
        Reject(() => lease.WrapResourcesBeforeClaim(_ => null!));
        Reject(() => lease.WrapResourcesBeforeClaim(_ => lease));
        Reject(() => lease.WrapResourcesBeforeClaim(original => original!));
        var faultedCancellation = new OperationCanceledException("wrapper construction failed");
        try { lease.WrapResourcesBeforeClaim(_ => throw faultedCancellation); throw new IOException("Expected rejection."); }
        catch (OperationCanceledException error) { Check(ReferenceEquals(error, faultedCancellation)); }
        Check(resource.Count == 0);
        var close = lease.DisposeAsync().AsTask();
        Check(ReferenceEquals(close, lease.DisposeAsync().AsTask())); await close;
        Check(resource.Count == 1);
        Reject(() => lease.WrapResourcesBeforeClaim(_ => new Resource()));
    }
    private static async Task Reservation()
    {
        await using var profile = await OfflineSessionProfile.CreateAsync(Path.GetTempPath(),
            Path.Combine(Path.GetTempPath(), "lease-wrap-" + Guid.NewGuid().ToString("N") + ".jsonl"), null, [], [], [], default);
        var faults = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new IOException("native first"); var second = new OperationCanceledException("native faulted cancellation");
        var original = new Resource(() => new(faults.Task)); var lease = new SessionRuntimeLease(profile.Registry, original);
        Resource? wrapper = null;
        Task? close = null, repeatedClose = null, reentrantClose = null;
        Exception? assertion = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            lease.WrapResourcesBeforeClaim(resource =>
            {
                Check(ReferenceEquals(resource, original));
                Reject(() => { reentrantClose = lease.DisposeAsync().AsTask(); });
                Reject(() => lease.WrapResourcesBeforeClaim(_ => new Resource()));
                return wrapper = new Resource(() => original.DisposeAsync());
            });
            close = lease.DisposeAsync().AsTask();
            repeatedClose = lease.DisposeAsync().AsTask();
            Check(ReferenceEquals(close, repeatedClose));
            faults.SetException([first, second]);
            try { await close; throw new IOException("Expected native failure."); }
            catch (Exception error)
            {
                Check(Contains(close.Exception ?? error, first) && Contains(close.Exception ?? error, second));
            }
            Check(original.Count == 1 && wrapper!.Count == 1 && faults.Task.IsFaulted);
        }
        catch (Exception error) { assertion = error; }
        finally
        {
            // Release before joining even when construction, reentry, or identity checks
            // fail. Preserve every admitted original, including a regressed second task.
            faults.TrySetException([first, second]);
            try { close ??= lease.DisposeAsync().AsTask(); }
            catch (Exception error) { cleanupFailures.Add(error); }
            var joined = new HashSet<Task>(ReferenceEqualityComparer.Instance);
            foreach (var task in new[] { close, repeatedClose, reentrantClose, faults.Task })
            {
                if (task is null || !joined.Add(task)) continue;
                try { await task; }
                catch (Exception error) { cleanupFailures.Add(task.Exception ?? error); }
            }
        }
        if (assertion is not null)
            throw new AggregateException("Lease fixture assertion failed; admitted cleanup originals were joined.",
                new[] { assertion }.Concat(cleanupFailures));
        // Successful assertions already validated the expected close failure. A distinct
        // admitted task or synchronous cleanup error must not silently escape teardown.
        if (cleanupFailures.Count == 0 || cleanupFailures.Any(error => !Contains(error, first) || !Contains(error, second)))
            throw new AggregateException("Lease fixture cleanup differed from its expected original.", cleanupFailures);
    }
    private sealed class Resource(Func<ValueTask>? callback = null) : IAsyncDisposable
    {
        internal int Count;
        public ValueTask DisposeAsync() { Count++; return callback?.Invoke() ?? ValueTask.CompletedTask; }
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
        throw new IOException("Expected resource wrapper rejection.");
    }
    private static bool Contains(Exception error, Exception original) => ReferenceEquals(error, original) ||
        (error is AggregateException aggregate ? aggregate.InnerExceptions.Any(child => Contains(child, original)) :
            error.InnerException is { } inner && Contains(inner, original));
    private static void Check(bool value) { if (!value) throw new IOException("Lease wrapper contract failed."); }
}
