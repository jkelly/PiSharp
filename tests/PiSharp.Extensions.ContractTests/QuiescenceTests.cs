using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class QuiescenceTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("quiescence. callback drain precedes lease and leaves other owners available", CallbackDrain),
        ("quiescence. cancelled wait restores admission without abandoning or cancelling its callback", CancelledWait),
        ("quiescence. pre-cancelled pause changes no descriptors or revision", PreCancelled),
        ("quiescence. one pause owns resume and repeated old disposal cannot release another pause", ExclusiveLease),
        ("quiescence. shutdown defeats a pending pause and joins its actual callback", ShutdownWins),
        ("quiescence. retained old lease cannot revive a disposed or replaced generation", RetainedLease),
        ("quiescence. removed descriptors retain their already admitted callback drain", RetiredCallback),
        ("quiescence. captured multi-owner dispatch drains callbacks not yet invoked", CapturedParticipants),
        ("quiescence. callback cannot wait for quiescence of any owner leased by its dispatch", ReentrantPause),
        ("quiescence. failed callback releases its lease and still establishes actual idle", FailedCallback)
    ];

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ExtensionToolDescriptor Tool(string name, ExtensionToolCallback? callback = null) =>
        new("tool", name, "", JsonData.EmptyObject, callback ?? ((value, _, _) => ValueTask.FromResult(value)));

    private static async Task CallbackDrain()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var release = Gate(); var calls = 0; CancellationToken callbackToken = default;
        var plugin = new TestExtension((entries, _) =>
        {
            entries.RegisterTool(Tool("paused-tool", async (value, _, token) =>
            {
                if (++calls == 1) { callbackToken = token; entered.TrySetResult(); await release.Task; }
                return value;
            }));
            return ValueTask.CompletedTask;
        });
        var scope = await registry.ActivateAsync("paused-owner", plugin);
        var other = await registry.ActivateAsync("other-owner", new TestExtension((entries, _) =>
        { entries.RegisterTool(Tool("other-tool")); return ValueTask.CompletedTask; }));
        var captured = registry.CaptureSnapshot();
        var running = registry.InvokeToolAsync(captured, "paused-tool", JsonData.EmptyObject).AsTask();
        Task<RegistrationQuiescenceLease>? pause = null;
        RegistrationQuiescenceLease? lease = null;
        try
        {
            await entered.Task;
            pause = scope.QuiesceAsync().AsTask();
            False(pause.IsCompleted);
            False(callbackToken.IsCancellationRequested); Equal(0, plugin.Disposals);
            Equal("other-owner", registry.CaptureSnapshot().Registrations.Single().OwnerId);
            await Failure(() => registry.InvokeToolAsync(captured, "paused-tool", JsonData.EmptyObject).AsTask(),
                ExtensionRegistrationFailure.StaleSnapshot);
            await Failure(() => registry.ActivateAsync("paused-owner", new TestExtension()), ExtensionRegistrationFailure.DuplicateOwner);
            await Failure(() => Task.FromResult(scope.RegisterTool(Tool("late-tool"))), ExtensionRegistrationFailure.InactiveScope);
            await Failure(() => Task.FromResult(other.RegisterTool(new("conflict", "paused-tool", "", JsonData.EmptyObject,
                (value, _, _) => ValueTask.FromResult(value)))), ExtensionRegistrationFailure.DuplicateName);
            var input = JsonData.Parse("{\"other\":true}");
            True(ReferenceEquals(input, await registry.InvokeToolAsync(registry.CaptureSnapshot(), "other-tool", input)));
            release.TrySetResult(); await running; lease = await pause;
            Equal(scope.OwnerGeneration, lease.OwnerGeneration); Equal(scope.OwnerId, lease.OwnerId);
            False(callbackToken.IsCancellationRequested); Equal(0, plugin.Disposals);
            lease.Dispose(); lease.Dispose();
            Equal(2, registry.CaptureSnapshot().Registrations.Length);
            await registry.InvokeToolAsync(registry.CaptureSnapshot(), "paused-tool", JsonData.EmptyObject);
            Equal(2, calls); Equal(scope.OwnerGeneration, registry.CaptureSnapshot().Tools.Single(row => row.OwnerId == scope.OwnerId).OwnerGeneration);
        }
        finally
        {
            release.TrySetResult(); await running;
            if (pause is not null) (await pause).Dispose();
            lease?.Dispose();
        }
    }

    private static async Task CancelledWait()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var release = Gate(); var calls = 0; CancellationToken callbackToken = default;
        var scope = await registry.ActivateAsync("owner", new TestExtension((entries, _) =>
        {
            entries.RegisterTool(Tool("held", async (value, _, token) =>
            {
                if (++calls == 1) { callbackToken = token; entered.TrySetResult(); await release.Task; }
                return value;
            }));
            return ValueTask.CompletedTask;
        }));
        var running = registry.InvokeToolAsync(registry.CaptureSnapshot(), "held", JsonData.EmptyObject).AsTask();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await entered.Task;
            var pause = scope.QuiesceAsync(cancellation.Token).AsTask();
            False(pause.IsCompleted); Equal(0, registry.CaptureSnapshot().Registrations.Length);
            cancellation.Cancel(); await Throws<OperationCanceledException>(() => pause);
            False(running.IsCompleted); False(callbackToken.IsCancellationRequested);
            Equal(1, registry.CaptureSnapshot().Registrations.Length);
            await registry.InvokeToolAsync(registry.CaptureSnapshot(), "held", JsonData.EmptyObject);
            Equal(2, calls);
        }
        finally { release.TrySetResult(); await running; }
    }

    private static async Task PreCancelled()
    {
        await using var registry = new ExtensionRegistry();
        var scope = await registry.ActivateAsync("owner", new TestExtension((entries, _) =>
        { entries.RegisterTool(Tool("echo")); return ValueTask.CompletedTask; }));
        var captured = registry.CaptureSnapshot();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Throws<OperationCanceledException>(() => scope.QuiesceAsync(cancellation.Token).AsTask());
        True(ReferenceEquals(captured, registry.CaptureSnapshot()));
        False(scope.ExtensionLifetimeCancellationToken.IsCancellationRequested);
        await registry.InvokeToolAsync(captured, "echo", JsonData.EmptyObject);
    }

    private static async Task ExclusiveLease()
    {
        await using var registry = new ExtensionRegistry();
        var scope = await registry.ActivateAsync("owner", new TestExtension((entries, _) =>
        { entries.RegisterTool(Tool("echo")); return ValueTask.CompletedTask; }));
        var first = await scope.QuiesceAsync();
        try
        {
            await Failure(() => scope.QuiesceAsync().AsTask(), ExtensionRegistrationFailure.InactiveScope);
            first.Dispose();
            await using var second = await scope.QuiesceAsync();
            first.Dispose(); await first.DisposeAsync();
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
            await Failure(() => scope.QuiesceAsync().AsTask(), ExtensionRegistrationFailure.InactiveScope);
        }
        finally { first.Dispose(); }
        Equal(1, registry.CaptureSnapshot().Registrations.Length);
    }

    private static async Task ShutdownWins()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var release = Gate();
        var plugin = new TestExtension((entries, _) =>
        {
            entries.RegisterTool(Tool("held", async (value, _, _) =>
            { entered.TrySetResult(); await release.Task; return value; }));
            return ValueTask.CompletedTask;
        });
        var scope = await registry.ActivateAsync("owner", plugin);
        var running = registry.InvokeToolAsync(registry.CaptureSnapshot(), "held", JsonData.EmptyObject).AsTask();
        Task? close = null;
        try
        {
            await entered.Task;
            var pause = scope.QuiesceAsync().AsTask();
            close = scope.DisposeAsync().AsTask();
            await Throws<OperationCanceledException>(() => pause);
            False(close.IsCompleted); Equal(0, plugin.Disposals);
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
            await Failure(() => registry.ActivateAsync("owner", new TestExtension()), ExtensionRegistrationFailure.DuplicateOwner);
            release.TrySetResult(); await running; await close;
            Equal(1, plugin.Disposals);
            var replacement = await registry.ActivateAsync("owner", new TestExtension());
            True(replacement.OwnerGeneration > scope.OwnerGeneration);
        }
        finally { release.TrySetResult(); await running; if (close is not null) await close; }
    }

    private static async Task RetainedLease()
    {
        await using var registry = new ExtensionRegistry();
        var scope = await registry.ActivateAsync("owner", new TestExtension((entries, _) =>
        { entries.RegisterTool(Tool("old-tool")); return ValueTask.CompletedTask; }));
        var lease = await scope.QuiesceAsync();
        try
        {
            await scope.DisposeAsync();
            var replacement = await registry.ActivateAsync("owner", new TestExtension((entries, _) =>
            { entries.RegisterTool(Tool("new-tool")); return ValueTask.CompletedTask; }));
            lease.Dispose(); await lease.DisposeAsync();
            Equal("new-tool", registry.CaptureSnapshot().Tools.Single().Name);
            Equal(replacement.OwnerGeneration, registry.CaptureSnapshot().Tools.Single().OwnerGeneration);
            await Failure(() => scope.QuiesceAsync().AsTask(), ExtensionRegistrationFailure.InactiveScope);
            await registry.DisposeAsync();
            lease.Dispose(); Equal(0, registry.CaptureSnapshot().Registrations.Length);
        }
        finally { lease.Dispose(); }
    }

    private static async Task RetiredCallback()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var release = Gate(); IExtensionRegistration? handle = null;
        var scope = await registry.ActivateAsync("owner", new TestExtension((entries, _) =>
        {
            handle = entries.RegisterTool(Tool("held", async (value, _, _) =>
            { entered.TrySetResult(); await release.Task; return value; }));
            return ValueTask.CompletedTask;
        }));
        var running = registry.InvokeToolAsync(registry.CaptureSnapshot(), "held", JsonData.EmptyObject).AsTask();
        Task<RegistrationQuiescenceLease>? pause = null;
        try
        {
            await entered.Task; handle!.Dispose();
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
            pause = scope.QuiesceAsync().AsTask(); False(pause.IsCompleted);
            release.TrySetResult(); await running;
            using var lease = await pause;
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
        }
        finally { release.TrySetResult(); await running; if (pause is not null) (await pause).Dispose(); }
    }

    private static async Task CapturedParticipants()
    {
        await using var registry = new ExtensionRegistry();
        var firstEntered = Gate(); var releaseFirst = Gate(); var secondCalls = 0;
        await registry.ActivateAsync("first", new TestExtension((entries, _) =>
        {
            entries.Observe(new("observer", "notice", async (_, _, _) =>
            { firstEntered.TrySetResult(); await releaseFirst.Task; }));
            return ValueTask.CompletedTask;
        }));
        var second = await registry.ActivateAsync("second", new TestExtension((entries, _) =>
        {
            entries.Observe(new("observer", "notice", (_, _, _) =>
            { secondCalls++; return ValueTask.CompletedTask; }));
            return ValueTask.CompletedTask;
        }));
        var dispatch = registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "notice", JsonData.EmptyObject).AsTask();
        Task<RegistrationQuiescenceLease>? pause = null;
        try
        {
            await firstEntered.Task;
            pause = second.QuiesceAsync().AsTask(); False(pause.IsCompleted); Equal(0, secondCalls);
            releaseFirst.TrySetResult(); await dispatch;
            using var lease = await pause; Equal(1, secondCalls);
            Equal("first", registry.CaptureSnapshot().Registrations.Single().OwnerId);
        }
        finally { releaseFirst.TrySetResult(); await dispatch; if (pause is not null) (await pause).Dispose(); }
    }

    private static async Task ReentrantPause()
    {
        await using var registry = new ExtensionRegistry();
        RegistrationScope? second = null; var checks = 0;
        var first = await registry.ActivateAsync("first", new TestExtension((entries, _) =>
        {
            entries.Observe(new("observer", "notice", async (_, _, _) =>
            {
                var error = await Throws<ExtensionRegistrationException>(() => second!.QuiesceAsync().AsTask());
                Equal(ExtensionRegistrationFailure.ReentrantDisposal, error.Failure); Equal("quiesce", error.Operation);
                checks++;
            }));
            return ValueTask.CompletedTask;
        }));
        second = await registry.ActivateAsync("second", new TestExtension((entries, _) =>
        { entries.Observe(new("observer", "notice", (_, _, _) => ValueTask.CompletedTask)); return ValueTask.CompletedTask; }));
        await registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "notice", JsonData.EmptyObject);
        Equal(1, checks); Equal(2, registry.CaptureSnapshot().Registrations.Length);
        using var lease = await first.QuiesceAsync();
        Equal("second", registry.CaptureSnapshot().Registrations.Single().OwnerId);
    }

    private static async Task FailedCallback()
    {
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var release = Gate();
        var scope = await registry.ActivateAsync("owner", new TestExtension((entries, _) =>
        {
            entries.RegisterTool(Tool("held", async (_, _, _) =>
            { entered.TrySetResult(); await release.Task; throw new InvalidOperationException("authored callback failure"); }));
            return ValueTask.CompletedTask;
        }));
        var running = registry.InvokeToolAsync(registry.CaptureSnapshot(), "held", JsonData.EmptyObject).AsTask();
        Task<RegistrationQuiescenceLease>? pause = null;
        try
        {
            await entered.Task; pause = scope.QuiesceAsync().AsTask(); False(pause.IsCompleted);
            release.TrySetResult(); await Throws<InvalidOperationException>(() => running);
            using var lease = await pause; False(scope.ExtensionLifetimeCancellationToken.IsCancellationRequested);
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
        }
        finally
        {
            release.TrySetResult(); await Throws<InvalidOperationException>(() => running);
            if (pause is not null) (await pause).Dispose();
        }
    }

    private sealed class TestExtension(Func<IExtensionRegistry, CancellationToken, ValueTask>? initialize = null) : IPiSharpExtension
    {
        internal int Disposals;
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) =>
            initialize?.Invoke(registry, token) ?? ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private static async Task Failure(Func<Task> run, ExtensionRegistrationFailure expected)
    { Equal(expected, (await Throws<ExtensionRegistrationException>(run)).Failure); }
    private static async Task<T> Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) => True(!value);
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
}
