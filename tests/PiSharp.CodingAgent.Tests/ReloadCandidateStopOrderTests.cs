using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;

internal static class ReloadCandidateStopOrderTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("reload candidate binding failure starts all physical stops before cancellation and joins originals", () => Scenario(false)),
        ("reload successful binding racing close joins stop-dependent cancellation and cleanup", () => Scenario(true))
    ];

    private static async Task Scenario(bool successfulBind)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-candidate-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var model = new ModelDescriptor("candidate-stop", "openai-responses", "authored");
        var registry = new SessionRuntimeRegistry([new(model, new UnusedTransport())], [], new Policy());
        var ids = 0;
        var lifecycle = new PersistentSessionLifecycle(registry, () => 1, () => "entry-" + ++ids);
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
            { type = "session", version = 3, id = "candidate-stop", timestamp = "2026-10-05T00:00:00Z", cwd = root }));
        var session = await lifecycle.CreateAsync(Path.Combine(root, "session.jsonl"), header, model);
        var owner = lifecycle.Attach(session);
        var initial = owner.Current;
        var stopOne = Gate(); var stopTwo = Gate(); var cancelEntered = Gate(); var physical = Gate();
        var stopOriginal = Gate(); var bodyOriginal = Gate(); var releaseEntered = Gate(); var releaseOriginal = Gate();
        var binding = new IOException("binding original"); var cancellation = new IOException("cancellation original");
        var stopA = new IOException("stop A"); var stopB = new OperationCanceledException("faulted stop B");
        var bodyA = new IOException("body A"); var bodyB = new IOException("body B");
        var releaseA = new IOException("release A"); var releaseB = new OperationCanceledException("faulted release B");
        var stops = 0; var bodies = 0; var releases = 0;
        var bound = Gate(); var finishBinding = Gate();
        CancellationTokenRegistration registration = default;
        var lease = new SessionRuntimeLease(registry, new Release(() =>
        { Interlocked.Increment(ref releases); releaseEntered.TrySetResult(); return releaseOriginal.Task; }), (boundOwner, attachment) =>
        {
            boundOwner.RegisterOwnedResource(attachment, _ =>
            { Interlocked.Increment(ref bodies); return bodyOriginal.Task; }, () =>
            { Interlocked.Increment(ref stops); stopOne.TrySetResult(); return stopOriginal.Task; });
            boundOwner.RegisterOwnedResource(attachment, _ => Task.CompletedTask, () =>
            { Interlocked.Increment(ref stops); stopTwo.TrySetResult(); return Task.CompletedTask; });
            registration = attachment.LifetimeToken.Register(() =>
            {
                cancelEntered.TrySetResult(); physical.Task.GetAwaiter().GetResult(); throw cancellation;
            });
            if (!successfulBind) throw binding;
            bound.TrySetResult(); finishBinding.Task.GetAwaiter().GetResult();
        });
        var reload = Task.Run(() => owner.RunReloadAsync(initial, async (reservation, _) =>
        {
            reservation.AdmitRuntime(lease);
            await reservation.InvalidateAndDrainAsync();
            await reservation.PublishAsync(lease, [], () => { });
            return true;
        }));
        Task? close = null;
        try
        {
            if (successfulBind)
            {
                await Within(bound.Task);
                close = Task.Run(async () => await owner.DisposeAsync());
                await Within(stopOne.Task); await Within(stopTwo.Task);
                finishBinding.TrySetResult();
            }
            // Baseline must reach these without close starting stops on its behalf.
            await Within(stopOne.Task); await Within(stopTwo.Task); await Within(cancelEntered.Task);
            Check(!reload.IsCompleted && !physical.Task.IsCompleted);
            var closeReturned = Gate();
            if (close is null)
            {
                close = Task.Run(async () =>
                { var original = owner.DisposeAsync().AsTask(); closeReturned.TrySetResult(); await original; });
                await Within(closeReturned.Task);
            }
            Check(!close.IsCompleted);
            stopOriginal.SetException([stopA, stopB]); bodyOriginal.SetException([bodyA, bodyB]); physical.TrySetResult();
            await Within(releaseEntered.Task); Check(!reload.IsCompleted && !close.IsCompleted);
            releaseOriginal.SetException([releaseA, releaseB]);
            var reloadError = await Failure(reload); var closeError = await Failure(close);
            var originals = new Exception[] { cancellation, stopA, stopB, bodyA, bodyB, releaseA, releaseB };
            if (!successfulBind) originals = [binding, .. originals];
            foreach (var original in originals)
            { Check(Contains(reloadError, original)); Check(Contains(closeError, original)); }
            Check(stopOriginal.Task.IsFaulted && releaseOriginal.Task.IsFaulted && stops == 2 && bodies == 1 && releases == 1);
            Check(session.Snapshot.IsDisposed && !ReferenceEquals(initial, owner.Current));
        }
        finally
        {
            // Also breaks the unfixed baseline cycle so failure does not detach work.
            finishBinding.TrySetResult(); physical.TrySetResult(); stopOriginal.TrySetResult(); bodyOriginal.TrySetResult(); releaseOriginal.TrySetResult();
            await Settle(reload); if (close is not null) await Settle(close);
            await Settle(owner.DisposeAsync().AsTask()); registration.Dispose();
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(root).StartsWith("pisharp-candidate-stop-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }
    private sealed class Release(Func<Task> release) : IAsyncDisposable
    { public ValueTask DisposeAsync() => new(release()); }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class UnusedTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("No provider work admitted.")); yield break; }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Within(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Settle(Task task) { try { await task; } catch (Exception) { } }
    private static async Task<Exception> Failure(Task task)
    { try { await Within(task); } catch (Exception error) { return task.Exception ?? error; } throw new IOException("Expected failure."); }
    private static bool Contains(Exception error, Exception original) => ReferenceEquals(error, original) ||
        (error is AggregateException aggregate ? aggregate.InnerExceptions.Any(child => Contains(child, original)) :
            error.InnerException is { } inner && Contains(inner, original));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Candidate stop order contract failed."); }
}
