using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

internal static class LoadoutDiagnosticDeliveryTests
{
    internal const string Prefix = "prepare-loadout-diagnostic.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "binding-failure-reaches-captured-sink-once", BindingFailure),
        (Prefix + "held-reporter-shares-original-task-and-retains-capacity", HeldReporter),
        (Prefix + "reporter-failure-retains-original-cause-and-skips-later-effects", ReporterFailure),
        (Prefix + "stale-and-retired-generations-cannot-report-as-current", GenerationRefusal),
        (Prefix + "admission-cancellation-and-captures-during-delivery-preserve-batches", AdmissionAndBatches),
        (Prefix + "foreign-cancellation-and-reporter-self-wait-preserve-causes", ForeignAndSelfWait),
        (Prefix + "unknown-tools-bounds-and-repeated-occurrences-remain-explicit", BoundsAndOccurrences)
    ];

    private static async Task BindingFailure()
    {
        await using var f = await Fixture.Create(); using var lifetime = new CancellationTokenSource();
        var bootstrap = new List<(string Name, Exception Error)>(); var seen = new List<ExtensionEventDiagnostic>();
        // Binding construction itself is synchronous and prepares the loadout before exposing Snapshot.
        // The activation owner retains these bounded initial captures, then binds delivery to the actual returned snapshot.
        var binding = new ExtensionAgentBinding(f.Registry, new NoPolicy(), (_, _, _) => ValueTask.FromResult(true),
            options: new() { ReportLoadoutDiagnostic = (name, error) => bootstrap.Add((name, error)) },
            sessionCancellationToken: lifetime.Token);
        Check(bootstrap.Count == 1 && ReferenceEquals(bootstrap[0].Error, f.PrepareFailure) &&
            binding.LoadoutDiagnostics.Single().Code == "CallbackFailure", "Genuine leased preparation failure or existing fallback diagnostics changed.");
        var bridge = new LoadoutDiagnosticDelivery(binding.Snapshot, (diagnostic, token) =>
        {
            Check(token == lifetime.Token, "Reporter lost its borrowed generation lifetime.");
            seen.Add(diagnostic); return ValueTask.CompletedTask;
        }, lifetime.Token);
        var capture = bridge.Capture(bootstrap[0].Name, bootstrap[0].Error);
        Check(capture.Status == LoadoutDiagnosticCaptureStatus.Captured && seen.Count == 0, "Capture called the async sink inside pure preparation.");
        var result = await bridge.DeliverAsync(binding.Snapshot);
        var registered = binding.Snapshot.Registrations.Single(row => row.Name == "probe" && row.Kind == "Tool");
        Check(seen.Single() == new ExtensionEventDiagnostic("prepare_loadout", registered.OwnerId, registered.OwnerGeneration,
            registered.RegistrationId, ExtensionEventFailure.HandlerFailed), "Wrong event, extension generation or tool registration reached sink.");
        Check(result.Results.Single().Status == LoadoutDiagnosticDeliveryStatus.Delivered &&
            ReferenceEquals(result.Results[0].Capture.PrepareFailure, f.PrepareFailure), "Original prepare cause was discarded.");
        Check(!JsonSerializer.Serialize(seen[0]).Contains("private prepare cause", StringComparison.Ordinal), "Native diagnostic exposed plugin text/stack.");
        Check((await bridge.DeliverAsync(binding.Snapshot)).Results.IsEmpty && seen.Count == 1, "Original capture reported twice.");
    }

    private static async Task HeldReporter()
    {
        await using var f = await Fixture.Create(); using var lifetime = new CancellationTokenSource();
        var entered = Gate(); var release = Gate(); var calls = 0; var joined = false;
        Task? reporterTask = null;
        async Task ReportAsync()
        {
            calls++; entered.TrySetResult();
            try { await release.Task; } finally { joined = true; }
        }
        var bridge = new LoadoutDiagnosticDelivery(f.Snapshot, (_, _) => new ValueTask(reporterTask = ReportAsync()),
            lifetime.Token, maximumPendingDiagnostics: 1);
        bridge.Capture("probe", f.PrepareFailure); var original = bridge.DeliverAsync(f.Snapshot);
        try
        {
            Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted, "Async reporter escaped original delivery task.");
            var shared = bridge.DeliverAsync(f.Snapshot);
            Check(ReferenceEquals(original, shared), "Concurrent caller started duplicate delivery instead of joining original.");
            var overflowCause = new IOException("another prepare failure"); var declined = bridge.Capture("probe", overflowCause);
            Check(declined.Status == LoadoutDiagnosticCaptureStatus.CapacityReached && ReferenceEquals(declined.PrepareFailure, overflowCause) && calls == 1,
                "Held callback released capacity or capture refusal lost original cause.");
            lifetime.Cancel(); Check(!original.IsCompleted && !joined, "Retirement detached original reporter cleanup.");
        }
        finally { release.TrySetResult(); try { await original; } finally { if (reporterTask is not null) await reporterTask; } }
        Check(joined && calls == 1 && (await bridge.DeliverAsync(f.Snapshot)).Results.IsEmpty, "Original join or at-most-once effect failed.");
    }

    private static async Task ReporterFailure()
    {
        await using var f = await Fixture.Create(); using var lifetime = new CancellationTokenSource();
        var cause = new IOException("original reporter failure"); var secondPrepare = new IOException("second original prepare failure"); var calls = 0;
        var bridge = new LoadoutDiagnosticDelivery(f.Snapshot, (_, _) => { calls++; throw cause; }, lifetime.Token);
        bridge.Capture("probe", f.PrepareFailure); bridge.Capture("probe", secondPrepare);
        var receipt = await bridge.DeliverAsync(f.Snapshot);
        Check(calls == 1 && receipt.Results.Length == 2 && receipt.Results[0].Status == LoadoutDiagnosticDeliveryStatus.ReporterFailed &&
            ReferenceEquals(receipt.Results[0].ReporterFailure, cause) && ReferenceEquals(receipt.Results[0].Capture.PrepareFailure, f.PrepareFailure) &&
            receipt.Results[1].Status == LoadoutDiagnosticDeliveryStatus.SkippedAfterReporterFailure &&
            ReferenceEquals(receipt.Results[1].Capture.PrepareFailure, secondPrepare), "Reporter failure replaced prepare cause or abandoned skipped diagnostics.");
        var failure = Throws<AggregateException>(receipt.ThrowIfReporterFailed);
        Check(failure.InnerExceptions.Count == 2 && ReferenceEquals(failure.InnerExceptions[0], f.PrepareFailure) &&
            ReferenceEquals(failure.InnerExceptions[1], cause), "Host failure policy did not retain both exact original causes.");
        Check((await bridge.DeliverAsync(f.Snapshot)).Results.IsEmpty && calls == 1, "Uncertain reporter effect was retried automatically.");
    }

    private static async Task GenerationRefusal()
    {
        await using var f = await Fixture.Create(); using var lifetime = new CancellationTokenSource(); var calls = 0;
        var bridge = new LoadoutDiagnosticDelivery(f.Snapshot, (_, _) => { calls++; return ValueTask.CompletedTask; }, lifetime.Token);
        bridge.Capture("probe", f.PrepareFailure);
        await f.Scope.DisposeAsync(); await f.Registry.ActivateAsync("owner", new Plugin(api => api.RegisterTool(Tool("probe", "prepare-id", _ => null))));
        var replacement = f.Registry.CaptureSnapshot();
        Check(replacement.Tools.Single().OwnerGeneration != f.Snapshot.Tools.First(tool => tool.Name == "probe").OwnerGeneration, "Fixture did not replace actual registry owner.");
        var stale = await bridge.DeliverAsync(replacement);
        Check(calls == 0 && stale.Results.Single().Status == LoadoutDiagnosticDeliveryStatus.StaleGeneration &&
            ReferenceEquals(stale.Results[0].Capture.PrepareFailure, f.PrepareFailure), "Old generation reported through the current binding.");
        Check(bridge.Capture("probe", f.PrepareFailure).Status == LoadoutDiagnosticCaptureStatus.RetiredGeneration &&
            (await bridge.DeliverAsync(f.Snapshot)).Results.IsEmpty, "Mismatched generation was revived by passing old metadata.");
        using var retired = new CancellationTokenSource();
        var stopped = new LoadoutDiagnosticDelivery(replacement, (_, _) => { calls++; return ValueTask.CompletedTask; }, retired.Token);
        stopped.Capture("probe", f.PrepareFailure); retired.Cancel();
        Check((await stopped.DeliverAsync(replacement)).Results.Single().Status == LoadoutDiagnosticDeliveryStatus.RetiredGeneration && calls == 0,
            "Retired generation started a reporter or dropped captured failure evidence.");
    }

    private static async Task AdmissionAndBatches()
    {
        await using var f = await Fixture.Create(); using var lifetime = new CancellationTokenSource(); using var canceled = new CancellationTokenSource();
        var entered = Gate(); var release = Gate(); var calls = 0;
        Task? reporterTask = null;
        async Task ReportAsync() { calls++; if (calls == 1) { entered.TrySetResult(); await release.Task; } }
        var bridge = new LoadoutDiagnosticDelivery(f.Snapshot, (_, _) => new ValueTask(reporterTask = ReportAsync()),
            lifetime.Token, maximumPendingDiagnostics: 2);
        bridge.Capture("probe", f.PrepareFailure); canceled.Cancel();
        Task<LoadoutDiagnosticDeliveryReceipt>? unexpectedAdmission = null;
        OperationCanceledException refusal;
        try { refusal = Throws<OperationCanceledException>(() => unexpectedAdmission = bridge.DeliverAsync(f.Snapshot, canceled.Token)); }
        finally
        {
            if (unexpectedAdmission is not null)
            {
                release.TrySetResult();
                try { await unexpectedAdmission; } finally { if (reporterTask is not null) await reporterTask; }
            }
        }
        Check(refusal.CancellationToken == canceled.Token && calls == 0, "Canceled admission consumed pending work or borrowed generation cancellation.");
        var original = bridge.DeliverAsync(f.Snapshot); var later = new IOException("captured during original delivery");
        try
        {
            Check(await Task.WhenAny(entered.Task, original) == entered.Task, "Original reporter did not enter.");
            Check(bridge.Capture("probe", later).Status == LoadoutDiagnosticCaptureStatus.Captured &&
                bridge.Capture("probe", new IOException()).Status == LoadoutDiagnosticCaptureStatus.CapacityReached, "Combined pending/in-flight charge exceeded bound.");
        }
        finally { release.TrySetResult(); try { await original; } finally { if (reporterTask is not null) await reporterTask; } }
        var next = await bridge.DeliverAsync(f.Snapshot);
        Check(next.Results.Length == 1 && ReferenceEquals(next.Results[0].Capture.PrepareFailure, later) && calls == 2 &&
            (await bridge.DeliverAsync(f.Snapshot)).Results.IsEmpty, "Capture during delivery was lost, appended to current batch or reported twice.");
    }

    private static async Task ForeignAndSelfWait()
    {
        await using var f = await Fixture.Create(); using var lifetime = new CancellationTokenSource(); using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var error = new OperationCanceledException(foreign.Token);
        var bridge = new LoadoutDiagnosticDelivery(f.Snapshot, (_, _) => throw error, lifetime.Token);
        bridge.Capture("probe", f.PrepareFailure); var receipt = await bridge.DeliverAsync(f.Snapshot);
        Check(ReferenceEquals(receipt.Results[0].ReporterFailure, error) && !lifetime.IsCancellationRequested,
            "Foreign reporter cancellation was replaced or retired the owning generation.");
        LoadoutDiagnosticDelivery? self = null; Task<LoadoutDiagnosticDeliveryReceipt>? unexpectedSelfWait = null; var refused = false;
        self = new(f.Snapshot, (_, _) =>
        {
            try { unexpectedSelfWait = self!.DeliverAsync(f.Snapshot); }
            catch (InvalidOperationException) { refused = true; throw; }
            throw new IOException("Reporter self-wait was unexpectedly admitted.");
        }, lifetime.Token);
        self.Capture("probe", f.PrepareFailure); var guardedTask = self.DeliverAsync(f.Snapshot);
        LoadoutDiagnosticDeliveryReceipt guarded;
        try { guarded = await guardedTask; }
        finally { if (unexpectedSelfWait is not null && !ReferenceEquals(unexpectedSelfWait, guardedTask)) await unexpectedSelfWait; }
        Check(refused && guarded.Results[0].ReporterFailure is InvalidOperationException &&
            ReferenceEquals(guarded.Results[0].Capture.PrepareFailure, f.PrepareFailure), "Reporter self-wait deadlocked or lost original prepare cause.");
    }

    private static async Task BoundsAndOccurrences()
    {
        await using var f = await Fixture.Create(); using var lifetime = new CancellationTokenSource(); var calls = 0;
        Throws<ArgumentException>(() => new LoadoutDiagnosticDelivery(f.Snapshot, (_, _) => ValueTask.CompletedTask, CancellationToken.None));
        Throws<ArgumentOutOfRangeException>(() => new LoadoutDiagnosticDelivery(f.Snapshot, (_, _) => ValueTask.CompletedTask, lifetime.Token, 0));
        var bridge = new LoadoutDiagnosticDelivery(f.Snapshot, (_, _) => { calls++; return ValueTask.CompletedTask; }, lifetime.Token);
        foreach (var name in new[] { "builtin", "plain" })
        {
            var declined = bridge.Capture(name, f.PrepareFailure);
            Check(declined.Status == LoadoutDiagnosticCaptureStatus.UnknownTool && declined.Diagnostic is null &&
                ReferenceEquals(declined.PrepareFailure, f.PrepareFailure), "Unknown/non-prepare tool borrowed extension identity or lost refusal evidence.");
        }
        Check((await bridge.DeliverAsync(f.Snapshot)).Results.IsEmpty && calls == 0, "Declined capture reached extension sink.");
        for (var i = 0; i < 2; i++)
        {
            bridge.Capture("probe", f.PrepareFailure);
            Check((await bridge.DeliverAsync(f.Snapshot)).Results.Single().Status == LoadoutDiagnosticDeliveryStatus.Delivered, "Repeated preparation occurrence was suppressed.");
        }
        Check(calls == 2, "Distinct occurrences sharing one exception were deduplicated or redelivered.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ExtensionRegistry Registry = new();
        internal readonly Exception PrepareFailure = new IOException("private prepare cause and stack");
        internal ExtensionRegistrySnapshot Snapshot = null!;
        internal IAsyncDisposable Scope = null!;
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            try
            {
                f.Scope = await f.Registry.ActivateAsync("owner", new Plugin(api =>
                {
                    api.RegisterTool(Tool("probe", "prepare-id", _ => throw f.PrepareFailure));
                    api.RegisterTool(Tool("plain", "plain-id"));
                }));
                f.Snapshot = f.Registry.CaptureSnapshot(); return f;
            }
            catch { await f.Registry.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => Registry.DisposeAsync();
    }
    private static ExtensionToolDescriptor Tool(string name, string id, Func<ToolLoadout, ToolLoadoutChanges?>? prepare = null) =>
        new(id, name, "fixture", JsonData.EmptyObject, (value, _, _) => ValueTask.FromResult(value)) { PrepareLoadout = prepare };
    private sealed class Plugin(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { token.ThrowIfCancellationRequested(); initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Prepare diagnostic delivery acquired execution authority."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
