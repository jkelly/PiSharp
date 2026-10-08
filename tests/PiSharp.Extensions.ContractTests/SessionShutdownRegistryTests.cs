using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

internal static class SessionShutdownRegistryTests
{
    internal const string Prefix = "session-shutdown-registry.";
    private static JsonData Event => JsonData.Parse("{\"type\":\"session_shutdown\",\"reason\":\"quit\"}");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + " absent handlers return false and unrelated topics remain untouched", AbsentHandlers),
        (Prefix + " reason and destination are preserved and malformed events are rejected", EventAdmission),
        (Prefix + " failures report sanitized identities and continue in owner order", FailureContinuation),
        (Prefix + " admitted removal retains callbacks and later captures see replacement", CapturedRemoval),
        (Prefix + " cleanup joins original callback and repeat notifications remain host owned", DisposalJoin),
        (Prefix + " reporter failure propagates and releases all participant leases", ReporterFailure),
        (Prefix + " stale foreign and retired snapshots reject before callbacks", SnapshotAdmission)
    ];

    private static async Task EventAdmission()
    {
        await using var registry = new ExtensionRegistry(); var observed = new List<JsonData>();
        await registry.ActivateAsync("owner", new Plugin(entries => entries.Observe(new("shutdown", "session_shutdown",
            (value, _, _) => { observed.Add(value); return ValueTask.CompletedTask; }))));
        foreach (var reason in new[] { "quit", "reload", "new", "resume", "fork" })
        {
            var value = JsonData.Parse("{\"type\":\"session_shutdown\",\"reason\":\"" + reason +
                "\",\"targetSessionFile\":\"destination.jsonl\",\"opaque\":1.0}");
            Check(await registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), value) && ReferenceEquals(observed[^1], value),
                "Reason, destination or immutable opaque event fields were rewritten.");
        }
        foreach (var raw in new[] { "null", "{}", "{\"type\":\"session_shutdown\"}",
            "{\"type\":\"session_start\",\"reason\":\"quit\"}",
            "{\"type\":\"session_shutdown\",\"reason\":\"unknown\"}",
            "{\"type\":\"session_shutdown\",\"reason\":null}",
            "{\"type\":\"session_shutdown\",\"reason\":\"quit\",\"targetSessionFile\":null}" })
        {
            var error = await Throws<ExtensionRegistrationException>(() =>
                registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), JsonData.Parse(raw)).AsTask());
            Check(error.Failure == ExtensionRegistrationFailure.InvalidDescriptor && observed.Count == 5,
                "Invalid lifecycle payload entered a handler or lost its admission failure.");
        }
        Check(await registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), Event) && observed.Count == 6,
            "Optional absent destination was rejected or failed admission leaked its dispatch charge.");
    }

    private static async Task AbsentHandlers()
    {
        await using var registry = new ExtensionRegistry(new() { MaximumConcurrentDispatches = 1 });
        var unrelated = 0; var reports = 0;
        await registry.ActivateAsync("owner", new Plugin(entries => entries.Observe(
            new("other", "agent_start", (_, _, _) => { unrelated++; return ValueTask.CompletedTask; }))));
        for (var index = 0; index < 2; index++)
            Check(!await registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), Event, (_, _) =>
                { reports++; return ValueTask.CompletedTask; }), "Empty shutdown dispatch reported handlers or leaked its dispatch charge.");
        Check(unrelated == 0 && reports == 0, "Shutdown invoked unrelated registrations or emitted a false failure.");
    }

    private static async Task FailureContinuation()
    {
        await using var registry = new ExtensionRegistry();
        using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var order = new List<string>(); var diagnostics = new List<ExtensionEventDiagnostic>();
        var first = await registry.ActivateAsync("first", new Plugin(entries =>
        {
            entries.Observe(new("fail", "session_shutdown", (_, _, _) =>
                { order.Add("fail"); throw new IOException("private plugin payload"); }));
            entries.Observe(new("oce", "session_shutdown", (_, _, _) =>
                { order.Add("oce"); throw new OperationCanceledException(foreign.Token); }));
        }));
        await registry.ActivateAsync("second", new Plugin(entries => entries.Observe(
            new("success", "session_shutdown", (value, context, token) =>
            {
                Check(value.ToString() == Event.ToString() && context.OwnerId == "second",
                    "Shutdown event shape or callback owner changed.");
                Check(token.CanBeCanceled && token == context.OperationCancellationToken &&
                    !context.SessionCancellationToken.CanBeCanceled && context.ExtensionLifetimeCancellationToken.CanBeCanceled,
                    "Shutdown borrowed a canceled operation/session or lost generation lifetime ownership.");
                order.Add("success"); return ValueTask.CompletedTask;
            }))));
        Check(await registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), Event, (diagnostic, token) =>
        {
            Check(!token.CanBeCanceled, "Failure reporting borrowed caller cancellation.");
            diagnostics.Add(diagnostic); return ValueTask.CompletedTask;
        }), "Existing shutdown handlers were reported absent.");
        Check(order.SequenceEqual(new[] { "fail", "oce", "success" }), "Failure or OCE stopped the notification chain.");
        Check(diagnostics.SequenceEqual(new[]
        {
            new ExtensionEventDiagnostic("session_shutdown", "first", first.OwnerGeneration, "fail", ExtensionEventFailure.HandlerFailed),
            new ExtensionEventDiagnostic("session_shutdown", "first", first.OwnerGeneration, "oce", ExtensionEventFailure.HandlerFailed)
        }), "Diagnostics leaked plugin data or lost exact owner/registration identity.");
    }

    private static async Task CapturedRemoval()
    {
        await using var registry = new ExtensionRegistry();
        var order = new List<string>(); IDisposable? later = null; RegistrationScope? second = null;
        var mutate = true;
        await registry.ActivateAsync("first", new Plugin(entries => entries.Observe(
            new("first", "session_shutdown", async (_, _, _) =>
            {
                order.Add("first");
                if (!mutate) return;
                mutate = false; later!.Dispose();
                second!.Observe(new("later", "session_shutdown", (_, _, _) =>
                    { order.Add("replacement"); return ValueTask.CompletedTask; }));
                var error = await Throws<ExtensionRegistrationException>(() => second!.DisposeAsync().AsTask());
                Check(error.Failure == ExtensionRegistrationFailure.ReentrantDisposal, "Participant disposal could await an admitted later handler.");
            }))));
        second = await registry.ActivateAsync("second", new Plugin(entries => later = entries.Observe(
            new("later", "session_shutdown", (_, _, _) => { order.Add("original"); return ValueTask.CompletedTask; }))));
        var captured = registry.CaptureSnapshot();
        Check(await registry.DispatchSessionShutdownAsync(captured, Event), "Captured shutdown list disappeared.");
        Check(order.SequenceEqual(new[] { "first", "original" }), "Removal replaced an already admitted callback.");
        order.Clear();
        var stale = await Throws<ExtensionRegistrationException>(() => registry.DispatchSessionShutdownAsync(captured, Event).AsTask());
        Check(stale.Failure == ExtensionRegistrationFailure.StaleSnapshot && order.Count == 0, "Retired snapshot started partial callbacks.");
        later!.Dispose(); // An old handle must not remove the replacement with the same ID.
        Check(await registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), Event), "Replacement shutdown handler was removed by an old handle.");
        Check(order.SequenceEqual(new[] { "first", "replacement" }), "Fresh capture did not see the replacement in owner order.");
    }

    private static async Task DisposalJoin()
    {
        await using var registry = new ExtensionRegistry(new() { MaximumConcurrentDispatches = 1 });
        var entered = Gate(); var release = Gate(); var calls = 0; var disposed = 0;
        var scope = await registry.ActivateAsync("held", new Plugin(entries => entries.Observe(
            new("held", "session_shutdown", async (_, _, _) =>
            { calls++; entered.TrySetResult(); await release.Task; })), () => { disposed++; return ValueTask.CompletedTask; }));
        var captured = registry.CaptureSnapshot();
        var dispatch = registry.DispatchSessionShutdownAsync(captured, Event).AsTask();
        Task? cleanup = null;
        try
        {
            await entered.Task;
            var limit = await Throws<ExtensionRegistrationException>(() => registry.DispatchSessionShutdownAsync(captured, Event).AsTask());
            Check(limit.Failure == ExtensionRegistrationFailure.LimitExceeded && calls == 1,
                "Shutdown bypassed the registry's shared concurrent dispatch bound.");
            cleanup = scope.DisposeAsync().AsTask();
            var repeated = scope.DisposeAsync().AsTask();
            Check(ReferenceEquals(cleanup, repeated) && !cleanup.IsCompleted && !dispatch.IsCompleted && disposed == 0,
                "Scope cleanup detached the original notification or failed to share settlement.");
            Check(scope.ExtensionLifetimeCancellationToken.IsCancellationRequested, "Disposal did not cancel its owned lifetime.");
        }
        finally
        {
            release.TrySetResult();
            try { Check(await dispatch, "Admitted notification was reported absent after disposal."); }
            finally { if (cleanup is not null) await cleanup; }
        }
        Check(calls == 1 && disposed == 1, "Disposal repeated callbacks or plugin cleanup.");
        var replacementCalls = 0;
        await registry.ActivateAsync("held", new Plugin(entries => entries.Observe(
            new("replacement", "session_shutdown", (_, _, _) => { replacementCalls++; return ValueTask.CompletedTask; }))));
        var fresh = registry.CaptureSnapshot();
        Check(await registry.DispatchSessionShutdownAsync(fresh, Event) && await registry.DispatchSessionShutdownAsync(fresh, Event) && replacementCalls == 2,
            "The registry imposed a hidden once-only shutdown policy or leaked an original lease.");
    }

    private static async Task ReporterFailure()
    {
        await using var registry = new ExtensionRegistry(new() { MaximumConcurrentDispatches = 1 });
        var laterCalls = 0; var cleanupCalls = 0;
        var first = await registry.ActivateAsync("first", new Plugin(entries => entries.Observe(
            new("fail", "session_shutdown", (_, _, _) => throw new IOException("plugin failure"))),
            () => { cleanupCalls++; return ValueTask.CompletedTask; }));
        var second = await registry.ActivateAsync("second", new Plugin(entries => entries.Observe(
            new("later", "session_shutdown", (_, _, _) => { laterCalls++; return ValueTask.CompletedTask; })),
            () => { cleanupCalls++; return ValueTask.CompletedTask; }));
        var original = new IOException("authored reporter failure");
        var error = await Throws<IOException>(() => registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), Event,
            (_, _) => throw original).AsTask());
        Check(ReferenceEquals(error, original) && laterCalls == 0, "Throwing source-style error listener lost failure identity or allowed later handlers.");
        await first.DisposeAsync(); await second.DisposeAsync();
        Check(cleanupCalls == 2 && !await registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), Event),
            "Reporter failure retained an owner lease or shared dispatch charge.");
    }

    private static async Task SnapshotAdmission()
    {
        await using var registry = new ExtensionRegistry(); await using var foreign = new ExtensionRegistry();
        var calls = 0;
        var scope = await registry.ActivateAsync("owner", new Plugin(entries => entries.Observe(
            new("shutdown", "session_shutdown", (_, _, _) => { calls++; return ValueTask.CompletedTask; }))));
        var captured = registry.CaptureSnapshot();
        var error = await Throws<ExtensionRegistrationException>(() => registry.DispatchSessionShutdownAsync(foreign.CaptureSnapshot(), Event).AsTask());
        Check(error.Failure == ExtensionRegistrationFailure.StaleSnapshot && calls == 0, "A foreign empty snapshot bypassed registry identity admission.");
        await scope.DisposeAsync();
        await registry.ActivateAsync("owner", new Plugin(entries => entries.Observe(
            new("shutdown", "session_shutdown", (_, _, _) => { calls++; return ValueTask.CompletedTask; }))));
        error = await Throws<ExtensionRegistrationException>(() => registry.DispatchSessionShutdownAsync(captured, Event).AsTask());
        Check(error.Failure == ExtensionRegistrationFailure.StaleSnapshot && calls == 0, "Old owner generation entered a replacement callback.");
        await registry.DisposeAsync();
        error = await Throws<ExtensionRegistrationException>(() => registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), Event).AsTask());
        Check(error.Failure == ExtensionRegistrationFailure.InactiveScope, "Shutdown notification implicitly reopened a disposed registry.");
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private sealed class Plugin(Action<IExtensionRegistry> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        { token.ThrowIfCancellationRequested(); initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
}
