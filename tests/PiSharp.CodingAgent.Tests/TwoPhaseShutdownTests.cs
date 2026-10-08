using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.AI;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Rpc.Ui;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Tools.Processes;

internal static class TwoPhaseShutdownTests
{
    internal const string Prefix = "two-phase shutdown ";
    private static readonly ModelDescriptor Model = new("shutdown", "openai-responses", "fixture");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "input cancellation joins original cleanup before stable stop and retains runtime", StopInput),
        (Prefix + "tool callback rejects self-stop and repeated stop joins original held tool", StopTool),
        (Prefix + "provider cancellation joins original enumerator finally before first phase", StopProvider),
        (Prefix + "phase two waits for owned exit and both original redirected reads", ChildStreams),
        (Prefix + "child exit and stream disposal failures retain every cause after joins", ChildFailures),
        (Prefix + "phase-one cancellation callback failure survives resource disposal", CancellationFailure),
        (Prefix + "repeat disposal shares original settlement and releases runtime once", RepeatDisposal),
        (Prefix + "actual RPC EOF waits for terminal acknowledgment before runtime release", HostBoundary),
        (Prefix + "user intent succeeds only after held input and terminal joins", HostUserCancellation),
        (Prefix + "external cancellation stays failed after both phases", HostExternalCancellation),
        (Prefix + "foreign input cancellation remains failure during user shutdown", HostForeignCancellation),
        (Prefix + "input IO failure remains failure during user shutdown", HostInputFailure),
        (Prefix + "terminal failure acknowledgment cannot turn shutdown into success", HostTerminalFailure),
        (Prefix + "foreign acknowledgment fails while original runtime cleanup still joins", HostForeignReceipt),
        (Prefix + "startup failure still reaches terminal settlement before cleanup", HostStartupFailure),
        (Prefix + "child receipt observer joins actual runner exit and held redirected originals", ProcessReceiptJoin),
        (Prefix + "uncertain child receipt remains failure with exact owner facts", ProcessUncertainReceipt),
        (Prefix + "ordinary cancellation and nonzero child exit preserve clean cleanup facts", ProcessCleanOutcomes),
        (Prefix + "native lifecycle observation preserves actual owned cancellation origin and joins", ObservationCancellation),
        (Prefix + "native lifecycle observation retains foreign OCE during owned cancellation", ObservationForeignCancellation),
        (Prefix + "conflicting terminal acknowledgment retains newly reported failure", HostConflictingReceipt),
        (Prefix + "owned input cancellation callback failure joins cleanup and retains cause", InputCancellationFailure),
        (Prefix + "shutdown callbacks cannot recursively wait on their own stop or disposal", ShutdownCallbackSelfWait),
        (Prefix + "accepted nonempty acknowledgment survives a subsequent callback exception", AcceptedAcknowledgmentThenThrow),
        (Prefix + "accepted nonempty acknowledgment survives a subsequent foreign receipt", AcceptedAcknowledgmentThenForeignReceipt),
        (Prefix + "accepted nonempty acknowledgment and conflicting new errors both survive", AcceptedAcknowledgmentThenConflict),
        (Prefix + "multiple input cancellation causes retain original order through repeat abort and disposal", MultipleInputCancellationCauses),
        (Prefix + "cancellation causes from sequential admissions remain distinct through shutdown", SequentialInputCancellationCauses),
        (Prefix + "input cancellation and resource cleanup failures retain identity without masking", InputAndRuntimeCancellationCauses)
    ];

    private static async Task StopInput()
    {
        await using var fixture = await Fixture.Create(); var entered = Gate(); var cleanup = Gate(); var release = Gate();
        using var cancellation = new CancellationTokenSource();
        var original = fixture.Session.SubmitInputAsync(new("held input", PromptInputSource.Rpc), new Admission(async token =>
        { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { cleanup.TrySetResult(); await release.Task; } }), cancellationToken: cancellation.Token);
        Task? stop = null;
        try
        {
            await Reach(entered.Task, original); cancellation.Cancel(); await Reach(cleanup.Task, original);
            stop = fixture.Owner.StopAdmissionAndJoinAsync();
            Check(!stop.IsCompleted && !original.IsCompleted && fixture.Runtime.Disposals == 0, "Input cleanup detached or resource disposal began early.");
            await Refuses(() => fixture.Session.PromptAsync(Input()));
            await Refuses(() => Task.FromResult(fixture.Owner.CaptureTree(fixture.Owner.Current)));
        }
        finally { release.TrySetResult(); await CancelledOriginal(original); if (stop is not null) await stop; }
        Check(!fixture.Session.Snapshot.IsDisposed && fixture.Backend.ActiveWriterCount == 1, "Stop released the current writer/runtime.");
    }

    private static async Task StopTool()
    {
        await using var fixture = await Fixture.Create(tool: true); var rejected = false;
        fixture.Adapter.Reentrant = () =>
        { try { _ = fixture.Owner.StopAdmissionAndJoinAsync(); } catch (InvalidOperationException) { rejected = true; } };
        var original = fixture.Session.PromptAsync(Input()); Task? stop = null;
        try
        {
            await Reach(fixture.Adapter.Entered.Task, original);
            stop = fixture.Owner.StopAdmissionAndJoinAsync();
            Check(ReferenceEquals(stop, fixture.Owner.StopAdmissionAndJoinAsync()), "Repeated stop detached the original settlement.");
            await Reach(fixture.Adapter.Cleanup.Task, original);
            Check(rejected && !stop.IsCompleted && !original.IsCompleted && fixture.Runtime.Disposals == 0, "Self-stop or held tool settlement was incorrect.");
            await Refuses(() => Task.FromResult(fixture.Session.ScheduleToolActivation([])));
        }
        finally { fixture.Adapter.Release.TrySetResult(); await CancelledOriginal(original); if (stop is not null) await stop; }
        Check(fixture.Adapter.Executions == 1 && fixture.Policy.Authorizations == 1 && !fixture.Session.Snapshot.IsDisposed,
            "Mandatory execution path or phase separation changed.");
    }
    private static async Task InputCancellationFailure()
    {
        await using var fixture = await Fixture.Create(); fixture.AllowDisposalFailure = true;
        var marker = new IOException("authored input cancellation callback failure"); var entered = Gate(); var cleanup = Gate(); var release = Gate();
        var original = fixture.Session.SubmitInputAsync(new("held cancellation callback"), new Admission(async token =>
        { using var registration = token.Register(() => throw marker); entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } finally { cleanup.TrySetResult(); await release.Task; } }));
        Task? stop = null;
        try
        {
            await Reach(entered.Task, original); stop = fixture.Owner.StopAdmissionAndJoinAsync(); await Reach(cleanup.Task, original);
            Check(!stop.IsCompleted && fixture.Runtime.Disposals == 0, "Cancellation callback failure detached original input cleanup.");
        }
        finally { release.TrySetResult(); await CancelledOriginal(original); }
        var phaseOne = await Throws<AggregateException>(async () => { await stop!; });
        Check(Contains(phaseOne, marker), "Original cancellation callback cause was discarded.");
        fixture.Runtime.ReleaseAll(); var phaseTwo = await Throws<AggregateException>(() => fixture.Owner.DisposeAsync().AsTask());
        Check(Contains(phaseTwo, marker) && fixture.Runtime.Disposals == 1, "Failed input stop skipped phase two or lost its cause.");
    }

    private static Task MultipleInputCancellationCauses() => InputCancellationCauseControl(1, false);
    private static Task SequentialInputCancellationCauses() => InputCancellationCauseControl(2, false);
    private static Task InputAndRuntimeCancellationCauses() => InputCancellationCauseControl(1, true);
    private static async Task InputCancellationCauseControl(int admissions, bool runtimeFailure)
    {
        await using var fixture = await Fixture.Create(); fixture.AllowDisposalFailure = true;
        var expected = new List<Exception>(); var callbacks = 0; Task? stop = null;
        for (var index = 0; index < admissions; index++)
        {
            var first = new IOException("authored original first callback " + index);
            var second = new IOException("authored original second callback " + index);
            var entered = Gate(); var cleanup = Gate(); var release = Gate();
            void Fail(Exception error)
            {
                callbacks++;
                if (!expected.Any(existing => ReferenceEquals(existing, error))) expected.Add(error);
                throw error;
            }
            var original = fixture.Session.SubmitInputAsync(new("held callback causes " + index), new Admission(async token =>
            {
                using var one = token.Register(() => Fail(first));
                using var two = token.Register(() => Fail(second));
                using var duplicate = token.Register(() => Fail(first));
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cleanup.TrySetResult(); await release.Task; }
            }));
            try
            {
                await Reach(entered.Task, original);
                Check(fixture.Session.Abort(), "Owned Abort became throwing or lost its admission result.");
                var afterFirst = callbacks; _ = fixture.Session.Abort();
                Check(afterFirst == 3 * (index + 1) && callbacks == afterFirst, "Repeated Abort repeated original cancellation callbacks.");
                await Reach(cleanup.Task, original);
                if (index == admissions - 1)
                {
                    stop = fixture.Session.StopAdmissionAndJoinAsync();
                    Check(ReferenceEquals(stop, fixture.Session.StopAdmissionAndJoinAsync()) && !stop.IsCompleted &&
                        !original.IsCompleted && fixture.Runtime.Disposals == 0, "Failed callbacks detached held admission cleanup or started phase two.");
                }
            }
            finally { release.TrySetResult(); await CancelledOriginal(original); }
        }
        var phaseOne = await Throws<AggregateException>(async () => await stop!);
        Check(phaseOne.InnerExceptions.SequenceEqual(expected, ReferenceEqualityComparer.Instance),
            "Phase one replaced, duplicated or reordered original callback exception instances.");
        var resourceError = new IOException("authored original resource cleanup cause");
        if (runtimeFailure) fixture.Runtime.Stderr.CloseFailure = resourceError;
        var disposal = fixture.Session.DisposeAsync().AsTask();
        PersistentAgentSessionException phaseTwo;
        try
        {
            await Reach(fixture.Runtime.Entered.Task, disposal);
            Check(!disposal.IsCompleted && ReferenceEquals(disposal, fixture.Session.DisposeAsync().AsTask()),
                "Failed phase one skipped original runtime cleanup or repeat disposal replaced it.");
        }
        finally
        {
            fixture.Runtime.ReleaseAll();
            phaseTwo = await Throws<PersistentAgentSessionException>(async () => await disposal);
        }
        if (runtimeFailure) expected.Add(resourceError);
        Check(phaseTwo.Fault.Failure == PersistentAgentSessionFailure.CleanupFailed && phaseTwo.InnerException is AggregateException aggregate &&
            aggregate.InnerExceptions.SequenceEqual(expected, ReferenceEqualityComparer.Instance) && fixture.Runtime.Disposals == 1 &&
            fixture.Backend.ActiveWriterCount == 0, "Phase two masked/reordered/duplicated causes or failed original resource joins.");
        var repeated = await Throws<PersistentAgentSessionException>(() => fixture.Session.DisposeAsync().AsTask());
        Check(ReferenceEquals(phaseTwo, repeated), "Repeated failed disposal replaced its original exception.");
    }
    private static async Task StopProvider()
    {
        await using var fixture = await Fixture.Create(holdProvider: true);
        var original = fixture.Session.PromptAsync(Input()); Task? stop = null;
        try
        {
            await Reach(fixture.Source.Entered.Task, original); stop = fixture.Owner.StopAdmissionAndJoinAsync();
            await Reach(fixture.Source.Cleanup.Task, original);
            Check(!stop.IsCompleted && !original.IsCompleted && fixture.Runtime.Disposals == 0, "Provider enumerator cleanup detached.");
        }
        finally { fixture.Source.Release.TrySetResult(); await CancelledOriginal(original); if (stop is not null) await stop; }
    }

    private static async Task ChildStreams()
    {
        await using var fixture = await Fixture.Create(); await fixture.Owner.StopAdmissionAndJoinAsync();
        Check(fixture.Runtime.Disposals == 0 && !fixture.Session.Snapshot.IsDisposed, "Stop terminated the retained runtime.");
        var original = fixture.Owner.DisposeAsync().AsTask();
        try
        {
            await Reach(fixture.Runtime.Entered.Task, original);
            Check(!original.IsCompleted, "Resource disposal detached owned exit.");
            fixture.Runtime.Exit.TrySetResult(); fixture.Runtime.Stdout.Release.TrySetResult();
            await fixture.Runtime.StdoutRead;
            Check(!original.IsCompleted, "Owned stderr original was detached after exit/stdout.");
            fixture.Runtime.Stderr.Release.TrySetResult(); await original;
            Check(fixture.Runtime.Disposals == 1 && fixture.Runtime.Stdout.Closes == 1 && fixture.Runtime.Stderr.Closes == 1 &&
                fixture.Backend.ActiveWriterCount == 0, "Owned resources were not closed exactly once after their originals.");
        }
        finally { fixture.Runtime.ReleaseAll(); await original; }
    }

    private static async Task ChildFailures()
    {
        await using var fixture = await Fixture.Create(); fixture.AllowDisposalFailure = true;
        var exitFailure = new IOException("authored child exit uncertainty"); var closeFailure = new IOException("authored redirected close failure");
        fixture.Runtime.Stdout.CloseFailure = closeFailure;
        await fixture.Owner.StopAdmissionAndJoinAsync(); var original = fixture.Owner.DisposeAsync().AsTask();
        try
        {
            await Reach(fixture.Runtime.Entered.Task, original); fixture.Runtime.Exit.TrySetException(exitFailure);
            Check(!original.IsCompleted, "Exit failure detached held redirected tasks.");
        }
        finally { fixture.Runtime.ReleaseAll(); }
        var failure = await Throws<AggregateException>(async () => { await original; });
        Check(Contains(failure, exitFailure) && Contains(failure, closeFailure) && fixture.Runtime.Stderr.Closes == 1,
            "Actual cleanup failure causes were discarded or later cleanup skipped.");
    }

    private static async Task CancellationFailure()
    {
        await using var fixture = await Fixture.Create(); fixture.AllowDisposalFailure = true;
        var marker = new IOException("authored lifetime callback failure");
        using var registration = fixture.Owner.Current.LifetimeToken.Register(() => throw marker);
        var phaseOne = await Throws<AggregateException>(() => fixture.Owner.StopAdmissionAndJoinAsync());
        Check(Contains(phaseOne, marker) && fixture.Runtime.Disposals == 0, "Phase-one failure was lost or disposed runtime early.");
        fixture.Runtime.ReleaseAll(); var phaseTwo = await Throws<AggregateException>(() => fixture.Owner.DisposeAsync().AsTask());
        Check(Contains(phaseTwo, marker) && fixture.Runtime.Disposals == 1 && fixture.Backend.ActiveWriterCount == 0,
            "Failed stop prevented final resource cleanup or lost its failure facts.");
    }

    private static async Task RepeatDisposal()
    {
        await using var fixture = await Fixture.Create(); var original = fixture.Owner.DisposeAsync().AsTask();
        try
        {
            await Reach(fixture.Runtime.Entered.Task, original);
            Check(ReferenceEquals(original, fixture.Owner.DisposeAsync().AsTask()) && fixture.Runtime.Disposals == 1,
                "Repeat disposal created a second release or settlement.");
            await fixture.Owner.StopAdmissionAndJoinAsync(); Check(!original.IsCompleted, "Stop after disposal detached held runtime work.");
        }
        finally { fixture.Runtime.ReleaseAll(); await original; }
    }
    private static async Task ShutdownCallbackSelfWait()
    {
        await using var fixture = await Fixture.Create(); var stopRejected = false; var ownerRejected = false; var sessionRejected = false;
        using var registration = fixture.Owner.Current.LifetimeToken.Register(() =>
        { try { _ = fixture.Owner.StopAdmissionAndJoinAsync(); } catch (InvalidOperationException) { stopRejected = true; } });
        fixture.Runtime.OnDispose = () =>
        {
            try { _ = fixture.Owner.DisposeAsync(); } catch (InvalidOperationException) { ownerRejected = true; }
            try { _ = fixture.Session.DisposeAsync(); } catch (InvalidOperationException) { sessionRejected = true; }
        };
        await fixture.Owner.StopAdmissionAndJoinAsync(); Check(stopRejected, "Lifetime callback received its own stop task.");
        var original = fixture.Owner.DisposeAsync().AsTask();
        try { await Reach(fixture.Runtime.Entered.Task, original); Check(ownerRejected && sessionRejected, "Runtime callback received its own disposal task."); }
        finally { fixture.Runtime.ReleaseAll(); await original; }
    }

    private static async Task HostBoundary()
    {
        using var files = new Files(); await files.WriteScript(); using var output = new MemoryStream(); using var errors = new StringWriter();
        var entered = Gate(); var release = Gate(); RpcSessionShutdownSettlement? captured = null;
        var original = RpcSessionCommand.RunWithPresentationAsync(files.Args, Stream.Null, output, errors, new Observer(), default,
            stopTerminalAndJoin: async settlement =>
            { captured = settlement; Check(settlement.Failures.IsEmpty && settlement.Session is { IsDisposed: false }, "First phase released the actual session or lost failure facts.");
                Check(!settlement.RuntimeCleanup.IsCompleted, "Runtime cleanup preceded terminal acknowledgment.");
                entered.TrySetResult(); await release.Task;
                var acknowledgment = settlement.AcknowledgeTerminalStopped();
                Check(ReferenceEquals(acknowledgment, settlement.AcknowledgeTerminalStopped()), "Repeated acknowledgment was not stable."); return acknowledgment; });
        try { await Reach(entered.Task, original); Check(!original.IsCompleted, "Host returned before the original terminal join."); }
        finally { release.TrySetResult(); }
        Check(await original == 0 && captured is not null && (await captured.RuntimeCleanup).IsEmpty, "Normal EOF did not complete both phases cleanly.");
    }

    private static Task HostUserCancellation() => HostCancellation(userIntent: true);
    private static Task HostExternalCancellation() => HostCancellation(userIntent: false);
    private static Task HostForeignCancellation() => HostCancellation(userIntent: true, foreign: true);
    private static Task HostInputFailure() => HostCancellation(userIntent: true, ioFailure: true);
    private static async Task HostCancellation(bool userIntent, bool foreign = false, bool ioFailure = false)
    {
        using var files = new Files(); await files.WriteScript(); using var output = new MemoryStream(); using var errors = new StringWriter();
        using var cancellation = new CancellationTokenSource(); using var other = new CancellationTokenSource(); other.Cancel();
        Exception? marker = foreign ? new OperationCanceledException(other.Token) : ioFailure ? new IOException("authored actual input failure") : null;
        using var input = new HeldInput(marker); var terminalEntered = Gate(); var terminalRelease = Gate(); RpcSessionShutdownSettlement? captured = null;
        var original = RpcSessionCommand.RunWithPresentationAsync(files.Args, input, output, errors, new Observer(), cancellation.Token,
            userShutdown: () => userIntent, stopTerminalAndJoin: async settlement =>
            { captured = settlement; terminalEntered.TrySetResult(); await terminalRelease.Task; return settlement.AcknowledgeTerminalStopped(); });
        try
        {
            await Reach(input.Entered.Task, original); cancellation.Cancel(); await Reach(input.Cleanup.Task, original);
            Check(!original.IsCompleted && captured is null, "Original input cleanup detached before terminal settlement.");
            input.Release.TrySetResult(); await Reach(terminalEntered.Task, original);
            Check(!original.IsCompleted && captured is not null && !captured.RuntimeCleanup.IsCompleted, "Terminal acknowledgment was bypassed.");
        }
        finally { input.Release.TrySetResult(); terminalRelease.TrySetResult(); }
        var result = await original; Check(result == (userIntent && marker is null ? 0 : 1), "Cancellation intent suppressed an external/foreign/IO failure or reported premature success.");
        Check(captured is not null && (await captured.RuntimeCleanup).IsEmpty, "Original runtime close did not join.");
        if (marker is not null) Check(captured!.Failures.Any(error => Contains(error, marker)), "Original input failure facts were discarded.");
    }

    private static async Task HostTerminalFailure()
    {
        using var files = new Files(); await files.WriteScript(); using var output = new MemoryStream(); using var errors = new StringWriter();
        var marker = new IOException("authored terminal leave failure"); RpcSessionShutdownSettlement? captured = null;
        var result = await RpcSessionCommand.RunWithPresentationAsync(files.Args, Stream.Null, output, errors, new Observer(), default,
            userShutdown: () => true, stopTerminalAndJoin: settlement =>
            { captured = settlement; return ValueTask.FromResult(settlement.AcknowledgeTerminalStopped([marker])); });
        Check(result == 1 && captured is not null && (await captured.RuntimeCleanup).IsEmpty && errors.ToString().Contains("CleanupFailed", StringComparison.Ordinal),
            "Terminal failure became success or skipped runtime cleanup.");
        Check((await captured!.Completion).Any(error => Contains(error, marker)), "Final settlement discarded terminal cleanup failure facts.");
    }

    private static async Task HostForeignReceipt()
    {
        using var files = new Files(); await files.WriteScript(); using var output = new MemoryStream(); using var errors = new StringWriter();
        RpcSessionShutdownSettlement? captured = null;
        var result = await RpcSessionCommand.RunWithPresentationAsync(files.Args, Stream.Null, output, errors, new Observer(), default,
            stopTerminalAndJoin: settlement => { captured = settlement; return ValueTask.FromResult(new RpcSessionShutdownSettlement([]).AcknowledgeTerminalStopped()); });
        Check(result == 1 && captured is not null && (await captured.RuntimeCleanup).IsEmpty, "Foreign terminal acknowledgment gained authority or detached cleanup.");
    }

    private static async Task HostStartupFailure()
    {
        using var output = new MemoryStream(); using var errors = new StringWriter(); var calls = 0; RpcSessionShutdownSettlement? captured = null;
        var result = await RpcSessionCommand.RunWithPresentationAsync([], Stream.Null, output, errors, new Observer(), default,
            stopTerminalAndJoin: settlement => { calls++; captured = settlement; Check(settlement.Session is null && !settlement.Failures.IsEmpty, "Startup failure was hidden.");
                return ValueTask.FromResult(settlement.AcknowledgeTerminalStopped()); });
        Check(result == 2 && calls == 1 && captured is not null && (await captured.RuntimeCleanup).IsEmpty, "Startup failure skipped terminal settlement or detached cleanup.");
    }
    private static async Task HostConflictingReceipt()
    {
        using var files = new Files(); await files.WriteScript(); using var output = new MemoryStream(); using var errors = new StringWriter();
        var marker = new IOException("authored late terminal failure"); RpcSessionShutdownSettlement? captured = null;
        var result = await RpcSessionCommand.RunWithPresentationAsync(files.Args, Stream.Null, output, errors, new Observer(), default,
            stopTerminalAndJoin: settlement =>
            { captured = settlement; _ = settlement.AcknowledgeTerminalStopped(); return ValueTask.FromResult(settlement.AcknowledgeTerminalStopped([marker])); });
        Check(result == 1 && captured is not null && (await captured.RuntimeCleanup).IsEmpty && (await captured.Completion).Any(error => Contains(error, marker)),
            "Conflicting repeat acknowledgment discarded new failure facts or detached cleanup.");
    }
    private static Task AcceptedAcknowledgmentThenThrow() => AcceptedAcknowledgmentFailure(0);
    private static Task AcceptedAcknowledgmentThenForeignReceipt() => AcceptedAcknowledgmentFailure(1);
    private static Task AcceptedAcknowledgmentThenConflict() => AcceptedAcknowledgmentFailure(2);
    private static async Task AcceptedAcknowledgmentFailure(int path)
    {
        using var files = new Files(); await files.WriteScript(); using var output = new MemoryStream(); using var errors = new StringWriter();
        var first = new IOException("authored accepted terminal cleanup error"); var second = new IOException("authored subsequent terminal cleanup error");
        var entered = Gate(); var release = Gate(); RpcSessionShutdownSettlement? captured = null;
        var original = RpcSessionCommand.RunWithPresentationAsync(files.Args, Stream.Null, output, errors, new Observer(), default,
            stopTerminalAndJoin: async settlement =>
            {
                captured = settlement; _ = settlement.AcknowledgeTerminalStopped([first]); entered.TrySetResult(); await release.Task;
                if (path == 0) throw second;
                if (path == 1) return new RpcSessionShutdownSettlement([]).AcknowledgeTerminalStopped();
                return settlement.AcknowledgeTerminalStopped([second]);
            });
        int result;
        try
        {
            await Reach(entered.Task, original);
            Check(!original.IsCompleted && captured is not null && !captured.RuntimeCleanup.IsCompleted,
                "Accepted receipt detached the original callback or began runtime release before it joined.");
        }
        finally { release.TrySetResult(); result = await original; }
        Check(result == 1 && captured is not null && (await captured.RuntimeCleanup).IsEmpty,
            "Callback/receipt failure became success or skipped original runtime cleanup joins.");
        var failures = await captured!.Completion;
        Check(failures.Any(error => ReferenceEquals(error, first)), "Accepted terminal cleanup error instance was discarded.");
        if (path == 0) Check(failures.Any(error => ReferenceEquals(error, second)), "Subsequent callback error instance was discarded.");
        else if (path == 1) Check(failures.Any(error => error is InvalidOperationException), "Foreign receipt validation failure was discarded.");
        else Check(failures.Any(error => Contains(error, second)), "Conflicting acknowledgment error instance was discarded.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal Files Files { get; } = new(); internal ChildRuntime Runtime { get; } = new(); internal Source Source { get; } = new();
        internal HeldAdapter Adapter { get; } = new(); internal Policy Policy { get; } = new();
        internal SessionStorageBackend Backend { get; private set; } = null!;
        internal PersistentAgentSession Session { get; private set; } = null!; internal ReplaceableAgentSession Owner { get; private set; } = null!;
        internal bool AllowDisposalFailure;
        internal static async Task<Fixture> Create(bool tool = false, bool holdProvider = false)
        {
            var fixture = new Fixture(); fixture.Source.Tool = tool; fixture.Source.Hold = holdProvider;
            fixture.Backend = new(fixture.Files.Root, SessionStorageMode.InMemory); var sequence = 0;
            var declaration = JsonData.Parse("{\"name\":\"held\",\"description\":\"held tool\",\"parameters\":{\"type\":\"object\"}}");
            var registry = new SessionRuntimeRegistry([new(Model, fixture.Source)], tool ? [new(declaration, fixture.Adapter)] : [], fixture.Policy);
            var lifecycle = new PersistentSessionLifecycle(registry, () => 0, () => "entry-" + ++sequence, backend: fixture.Backend,
                runtimeForWorkingDirectory: (_, _) => ValueTask.FromResult(new SessionRuntimeLease(registry, fixture.Runtime)));
            try
            {
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "shutdown",
                    timestamp = "2026-10-03T00:00:00.000Z", cwd = fixture.Files.Root }));
                fixture.Session = await lifecycle.CreateAsync(fixture.Files.Session, header, Model);
                if (tool) await fixture.Session.ConfigureAsync(new(SystemMessage: new("system", JsonData.Parse(JsonSerializer.Serialize(new
                    { role = "system", content = "", timestamp = 0, toolsAdded = new[] { declaration.Value } })))));
                fixture.Owner = lifecycle.Attach(fixture.Session); return fixture;
            }
            catch { fixture.Runtime.ReleaseAll(); await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Runtime.ReleaseAll(); Source.Release.TrySetResult(); Adapter.Release.TrySetResult();
            try { if (Owner is not null) await Owner.DisposeAsync(); else if (Session is not null) await Session.DisposeAsync(); else await Runtime.DisposeAsync(); }
            catch (Exception) when (AllowDisposalFailure) { }
            finally { Files.Dispose(); }
        }
    }
    private static async Task ProcessReceiptJoin()
    {
        var child = new ChildRuntime(); var runner = new OwnedProcessCleanup(new ChildRunner(child, ProcessResult(ProcessRunStatus.Canceled)));
        using var cancellation = new CancellationTokenSource(); var original = runner.RunAsync(Request(), cancellationToken: cancellation.Token).AsTask();
        try
        {
            await Reach(child.Entered.Task, original); cancellation.Cancel();
            Check(runner.Capture().IsEmpty && !runner.CaptureFailures().IsEmpty && !original.IsCompleted, "Observer fabricated settlement for held child originals.");
            child.Exit.TrySetResult(); child.Stdout.Release.TrySetResult(); await child.StdoutRead;
            Check(!original.IsCompleted && runner.Capture().IsEmpty, "Child receipt omitted original stderr join.");
        }
        finally { child.ReleaseAll(); await original; }
        var receipt = runner.Capture().Single();
        Check(receipt.Ordinal == 1 && receipt.ProcessId == 42 && receipt.CleanupConfirmed && receipt.CapturedOutputComplete &&
            runner.CaptureFailures().IsEmpty, "Observer changed actual runner receipt facts.");
    }
    private static async Task ProcessUncertainReceipt()
    {
        var child = new ChildRuntime(); child.ReleaseAll();
        var result = ProcessResult(ProcessRunStatus.Failed) with { CleanupConfirmed = false, CapturedOutputComplete = false, Diagnostics = [ProcessDiagnostic.CleanupFailed] };
        var runner = new OwnedProcessCleanup(new ChildRunner(child, result)); var actual = await runner.RunAsync(Request());
        var failure = (OwnedProcessCleanupException)runner.CaptureFailures().Single();
        Check(ReferenceEquals(result, actual) && failure.Receipt == runner.Capture().Single() && failure.Receipt.ProcessId == 42 &&
            !failure.Receipt.CleanupConfirmed && failure.Receipt.Diagnostics.SequenceEqual([ProcessDiagnostic.CleanupFailed]), "Uncertain child settlement was hidden or guessed.");
    }
    private static async Task ProcessCleanOutcomes()
    {
        foreach (var status in new[] { ProcessRunStatus.Canceled, ProcessRunStatus.NonZeroExit })
        {
            var child = new ChildRuntime(); child.ReleaseAll(); var expected = ProcessResult(status);
            var runner = new OwnedProcessCleanup(new ChildRunner(child, expected)); var actual = await runner.RunAsync(Request());
            Check(ReferenceEquals(expected, actual) && runner.CaptureFailures().IsEmpty && runner.Capture().Single().CleanupConfirmed,
                "Ordinary process outcome was incorrectly classified as uncertain cleanup.");
        }
    }
    private sealed class ChildRunner(ChildRuntime child, ProcessRunResult result) : IProcessRunner
    { public async ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? update = null, CancellationToken token = default)
        { await child.DisposeAsync(); return result; } }
    private static ProcessRequest Request() => new("fixture executable", [], "fixture cwd", ImmutableDictionary<string, string>.Empty, "fixture spill");
    private static ProcessRunResult ProcessResult(ProcessRunStatus status) => new(status, status == ProcessRunStatus.NonZeroExit ? 1 : 0, 42, true, true, true,
        new("", ToolOutputTruncator.Tail("", new(2000, 50 * 1024)), 0, 0, null), new("", false), 0, []);
    private static Task ObservationCancellation() => ObservationCancellationFor(false);
    private static Task ObservationForeignCancellation() => ObservationCancellationFor(true);
    private static async Task ObservationCancellationFor(bool foreign)
    {
        await using var registry = new ExtensionRegistry(); using var cancellation = new CancellationTokenSource();
        using var other = new CancellationTokenSource(); other.Cancel(); var marker = new OperationCanceledException(other.Token);
        var entered = Gate(); var cleanup = Gate(); var release = Gate();
        await registry.ActivateAsync("shutdown-observer", new Extension(async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { if (foreign) throw marker; throw; }
            finally { cleanup.TrySetResult(); await release.Task; }
        }));
        var original = registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "session_start", JsonData.EmptyObject, cancellation.Token).AsTask();
        try
        { await Reach(entered.Task, original); cancellation.Cancel(); await Reach(cleanup.Task, original); Check(!original.IsCompleted, "Lifecycle callback original cleanup detached."); }
        finally { release.TrySetResult(); }
        var failure = await Throws<OperationCanceledException>(async () => { await original; });
        Check(foreign ? ReferenceEquals(failure, marker) : failure.CancellationToken == cancellation.Token,
            "Observation confused foreign cancellation with its actual owned admission origin.");
    }
    private sealed class Extension(Func<CancellationToken, Task> observe) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        { registry.Observe(new("shutdown-start", "session_start", async (_, _, cancellation) => await observe(cancellation))); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    // Simulated child owner: these are the original exit and redirected-read tasks, not timers or PID guesses.
    // Production NodeWorkerSupervisor already exposes the joined physical receipt; no new process lifetime wrapper is needed.
    private sealed class ChildRuntime : IAsyncDisposable
    {
        internal TaskCompletionSource Entered = Gate(), Exit = Gate();
        internal HeldRedirectedStream Stdout { get; } = new();
        internal HeldRedirectedStream Stderr { get; } = new();
        internal Task<int> StdoutRead { get; }
        internal Task<int> StderrRead { get; }
        internal int Disposals;
        internal Action? OnDispose;
        internal ChildRuntime() { StdoutRead = Stdout.ReadAsync(new byte[8]).AsTask(); StderrRead = Stderr.ReadAsync(new byte[8]).AsTask(); }
        internal void ReleaseAll() { Exit.TrySetResult(); Stdout.Release.TrySetResult(); Stderr.Release.TrySetResult(); }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals); OnDispose?.Invoke(); Entered.TrySetResult(); var failures = new List<Exception>();
            foreach (var original in new Task[] { Exit.Task, StdoutRead, StderrRead }) try { await original; } catch (Exception error) { failures.Add(error); }
            try { await Stdout.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            try { await Stderr.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
    }
    private sealed class HeldRedirectedStream : MinimalStream
    {
        internal TaskCompletionSource Release = Gate(); internal Exception? CloseFailure; internal int Closes;
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default) { await Release.Task; return 0; }
        public override ValueTask DisposeAsync() { Closes++; return CloseFailure is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask; }
    }
    private sealed class HeldInput(Exception? failure) : MinimalStream
    {
        internal TaskCompletionSource Entered = Gate(), Cleanup = Gate(), Release = Gate();
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return 0; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { if (failure is not null) throw failure; throw; }
            finally { Cleanup.TrySetResult(); await Release.Task; }
        }
    }
    private abstract class MinimalStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Admission(Func<CancellationToken, Task> callback) : IPromptInputAdmission
    { public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token) { await callback(token); return new(PromptInputAction.Handled); } }
    private sealed class HeldAdapter : IPreparedToolAdapter
    {
        internal TaskCompletionSource Entered = Gate(), Cleanup = Gate(), Release = Gate(); internal Action? Reentrant; internal int Executions;
        public string Name => "held";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(Name,
            "fixture", PreparedToolActionKind.Path, "held", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { Executions++; Reentrant?.Invoke(); Entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); }
            finally { Cleanup.TrySetResult(); await Release.Task; } return ToolResult.Success("held"); }
    }
    private sealed class Policy : IToolActionPolicy
    { internal int Authorizations; public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Authorizations++; return ValueTask.FromResult(new ToolActionAuthorization(true)); } }
    private sealed class Source : IChatTransport
    {
        internal bool Tool, Hold; internal TaskCompletionSource Entered = Gate(), Cleanup = Gate(), Release = Gate();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 0, Tool ? [new ToolCallContent("root", "held", JsonData.EmptyObject)] :
                [new TextContent("done")], TokenUsage.Zero, Tool ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (Hold) { Entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { Cleanup.TrySetResult(); await Release.Task; } }
            if (Tool) { var call = (ToolCallContent)final.Content[0]; yield return new ToolCallStarted(0, call); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            yield return new StreamDone(final.StopReason, final);
        }
    }
    private sealed class Observer : IRpcExtensionUiPresentationObserver
    { public ValueTask PublishedAsync(RpcExtensionUiPresentation value, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask RetiredAsync(RpcExtensionUiRetirement value, CancellationToken token) => ValueTask.CompletedTask; }
    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-two-phase-" + Guid.NewGuid().ToString("N"));
        internal string Session => Path.Combine(Root, "session.jsonl"); internal string Script => Path.Combine(Root, "script.json");
        internal string[] Args => ["session", "rpc", "--session", Session, "--workspace", Root, "--offline-script", Script,
            "--offline-api", "openai-completions", "--session-mode", "new-memory"];
        internal Files() => Directory.CreateDirectory(Root);
        internal Task WriteScript() => File.WriteAllTextAsync(Script, JsonSerializer.Serialize(new { schemaVersion = 1,
            turns = new[] { SessionCommandTests.CompletionsText("unused offline bytes") } }));
        public void Dispose()
        { var full = Path.GetFullPath(Root); Check(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
            Path.GetFileName(full).StartsWith("PiSharp-two-phase-", StringComparison.Ordinal), "Unowned cleanup path."); Directory.Delete(full, true); }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TranscriptEntry Input() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"shutdown\",\"timestamp\":0}"));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reach(Task gate, Task original) { if (await Task.WhenAny(gate, original) == original) { await original; throw new InvalidOperationException("Original completed before required held boundary."); } await gate; }
    private static async Task CancelledOriginal(Task original) { try { await original; } catch (OperationCanceledException) { /* Owned stop/caller cancellation was deliberately requested in these cases. */ } }
    private static async Task Refuses(Func<Task> action) { try { await action(); } catch (Exception error) when (error is InvalidOperationException or PersistentAgentSessionException) { return; } throw new InvalidOperationException("Stopped admission accepted effects."); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static bool Contains(Exception error, Exception marker) => ReferenceEquals(error, marker) || error is AggregateException aggregate &&
        aggregate.InnerExceptions.Any(inner => Contains(inner, marker)) || error.InnerException is { } cause && Contains(cause, marker);
}
