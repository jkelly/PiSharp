using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime.Facade.Context;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;

// Source controls only. Root owns runner registration and permission to execute.
internal static class NativeSessionBehaviorAdapterTests
{
    private static readonly AsyncLocal<ControlCustody?> Control = new();
    private static readonly Dictionary<string, ImmutableArray<ExtensionBehaviorOriginalEvidence>> Evidence = new(StringComparer.Ordinal);
    internal static ImmutableArray<ExtensionBehaviorOriginalEvidence> CaptureEvidence(string control)
    { lock (Evidence) return Evidence[control]; }
    private static ExtensionSessionBehaviorOperationOwner NewOwner(IExtensionContext context, Action validate)
    { var owner = new ExtensionSessionBehaviorOperationOwner(context, validate); Control.Value!.Owners.Add(owner); return owner; }
    private static Task<T> Track<T>(Task<T> original) { Control.Value!.Track(original); return original; }
    private static Task Track(Task original) { Control.Value!.Track(original); return original; }
    private static async Task Controlled(Func<Task> body)
    {
        var prior = Control.Value; var custody = new ControlCustody(); Control.Value = custody;
        Exception? primary = null; var failures = new List<Exception>();
        try { await Track(body()); } catch (Exception error) { primary = error; }
        finally
        {
            try { await custody.Finish(failures); }
            finally { lock (Evidence) Evidence[body.Method.Name] = custody.Capture(); Control.Value = prior; }
        }
        if (primary is not null) failures.Insert(0, primary);
        if (failures.Count == 1 && failures[0] is not OperationCanceledException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count != 0) throw new AggregateException("Control assertion and unexpected original failures.", failures);
    }
    private sealed class ControlCustody
    {
        internal readonly List<Action> Releases = [];
        internal readonly List<ExtensionSessionBehaviorOperationOwner> Owners = [];
        internal readonly List<Func<Task>> Cleanup = [];
        internal readonly List<Action> AfterCleanup = [];
        private readonly HashSet<Task> originals = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Task> acknowledged = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Task, ExtensionBehaviorOriginalEvidence> rows = new(ReferenceEqualityComparer.Instance);
        private readonly List<ExtensionBehaviorOriginalEvidence> nestedJoinedRows = [];
        internal void Track(Task task) { lock (originals) originals.Add(task); }
        internal void Observe(Task task, Exception error) => Record(task, error);
        internal bool IsAcknowledged(Task task) => acknowledged.Contains(task);
        internal void AppendJoinedEvidence(ImmutableArray<ExtensionBehaviorOriginalEvidence> captured)
        { Check(captured.All(row => row.Original.IsCompleted)); nestedJoinedRows.AddRange(captured); }
        internal void AcknowledgeFault(Task task, Exception[] allowed, bool exact = true)
        {
            Check(task.IsFaulted && rows.TryGetValue(task, out var row) && row.Aggregate is not null);
            var full = GraphNodes(rows[task].Aggregate!); var direct = rows[task].Observed is { } observed ? GraphNodes(observed) : new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            var expected = new HashSet<Exception>(allowed, ReferenceEqualityComparer.Instance);
            Check(full.Count != 0 && (exact ? full.SetEquals(expected) : full.IsSubsetOf(expected)) && direct.IsSubsetOf(full));
            acknowledged.Add(task);
        }
        internal void AcknowledgeCanceled(Task task, CancellationToken token)
        {
            Check(task.IsCanceled && rows.TryGetValue(task, out var row) && row.Observed is OperationCanceledException error && error.CancellationToken == token);
            acknowledged.Add(task);
        }
        private void Record(Task task, Exception? observed)
        {
            Track(task);
            if (!rows.ContainsKey(task)) rows.Add(task, new("control-original", task, task.IsFaulted ? task.Exception : null, observed));
        }
        private static HashSet<Exception> GraphNodes(Exception error)
        {
            var result = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<Exception>(); pending.Push(error);
            var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            while (pending.TryPop(out var actual))
            {
                if (!seen.Add(actual)) continue;
                if (actual is AggregateException { InnerExceptions.Count: > 0 } aggregate)
                    foreach (var child in aggregate.InnerExceptions) pending.Push(child);
                else
                {
                    result.Add(actual); // All nonaggregate nodes and unknown empty aggregates retain exact identity.
                    if (actual.InnerException is { } inner) pending.Push(inner);
                }
            }
            return result;
        }
        private static void Unexpected(Exception error, List<Exception> failures)
        {
            if (!failures.Any(actual => ReferenceEquals(actual, error))) failures.Add(error);
        }
        internal void AcknowledgeOwner(ExtensionSessionBehaviorOperationOwner owner, Exception[] allowed)
        {
            foreach (var row in owner.CaptureOriginals())
            {
                Track(row.Original); rows.TryAdd(row.Original, row);
                if (row.Original.IsFaulted) AcknowledgeFault(row.Original, allowed, exact: false);
                else Check(row.Original.IsCompletedSuccessfully || acknowledged.Contains(row.Original));
            }
            var close = owner.CloseAsync();
            if (close.IsFaulted) AcknowledgeFault(close, allowed);
            if (owner.SettlementOriginal is { IsFaulted: true } settlement)
            {
                Record(settlement, rows[close].Observed); AcknowledgeFault(settlement, allowed);
            }
        }
        internal async Task Finish(List<Exception> failures)
        {
            foreach (var release in Releases) try { release(); } catch (Exception error) { Unexpected(error, failures); }
            foreach (var owner in Owners)
            {
                Task? close = null;
                try { close = owner.CloseAsync(); Track(close); await close; } catch (Exception error) { if (close is not null) Record(close, error); else Unexpected(error, failures); }
                if (close is not null && !rows.ContainsKey(close)) Record(close, null);
                if (owner.SettlementOriginal is { } settlement) Track(settlement);
                foreach (var row in owner.CaptureOriginals()) { Track(row.Original); rows.TryAdd(row.Original, row); }
            }
            for (var index = Cleanup.Count - 1; index >= 0; index--)
            {
                Task? original = null;
                try { original = Cleanup[index](); Track(original); await original; Record(original, null); }
                catch (Exception error) { if (original is not null) Record(original, error); else Unexpected(error, failures); }
            }
            Task[] captured; lock (originals) captured = originals.ToArray();
            foreach (var task in captured)
            {
                Exception? observed = null;
                try { await task; } catch (Exception error) { observed = error; }
                Record(task, observed);
                var row = rows[task];
                if (acknowledged.Contains(task)) continue;
                if (row.Aggregate is not null) Unexpected(row.Aggregate, failures);
                else if (row.Observed is not null) Unexpected(row.Observed, failures);
            }
            foreach (var cleanup in AfterCleanup) try { cleanup(); } catch (Exception error) { Unexpected(error, failures); }
        }
        internal ImmutableArray<ExtensionBehaviorOriginalEvidence> Capture() => rows.Values.Concat(nestedJoinedRows).ToImmutableArray();
    }
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("session-behavior.held-returned-context", () => Controlled(ReturnedContext)),
        ("session-behavior.held-compaction-callback", () => Controlled(CompactionCallback)),
        ("session-behavior.original-and-callback-faults", () => Controlled(CompactionFaults)),
        ("session-behavior.external-cancellation-self-wait", () => Controlled(CancellationSelfWait)),
        ("session-behavior.faulted-and-synchronous-oce", () => Controlled(FaultedCancellation)),
        ("session-behavior.configuration-original-and-admission", () => Controlled(ConfigurationOriginal)),
        ("session-behavior.actual-registered-native-host", () => Controlled(ActualRegisteredHost)),
        ("session-behavior.nested-owner-callback-ancestry", () => Controlled(NestedCallbackAncestry)),
        ("session-behavior.nested-owner-cancellation-ancestry", () => Controlled(NestedCancellationAncestry)),
        ("session-behavior.logical-to-physical-ancestry", () => Controlled(LogicalToPhysical)),
        ("session-behavior.physical-to-logical-after-await", () => Controlled(PhysicalToLogical))
    ];
    private static TaskCompletionSource<T> Held<T>() { var held = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); Control.Value!.Releases.Add(() => held.TrySetResult(default!)); Control.Value!.Track(held.Task); return held; }
    private static void Check(bool value) { if (!value) throw new IOException("Session behavior custody control failed."); }
    private static async Task<Exception> Error(Task task)
    { try { await task; } catch (Exception error) { Control.Value!.Observe(task, error); return error; } throw new IOException("Expected failure."); }
    private static async Task Gate(Task gate, params Task[] terminals)
    {
        using var deadline = new CancellationTokenSource();
        var diagnostic = Track(Task.Delay(TimeSpan.FromSeconds(10), deadline.Token));
        _ = Track(gate); foreach (var terminal in terminals) _ = Track(terminal);
        try
        {
            var race = Track(Task.WhenAny(new[] { gate, diagnostic }.Concat(terminals)));
            var winner = await race;
            if (gate.IsCompleted) { await gate; return; }
            if (ReferenceEquals(winner, diagnostic)) throw new TimeoutException("Held control gate did not arrive within diagnostic cap.");
            await winner; throw new IOException("Actual operation ended before its expected control gate.");
        }
        finally
        {
            deadline.Cancel();
            try { await diagnostic; }
            catch (OperationCanceledException error)
            {
                Control.Value!.Observe(diagnostic, error);
                Control.Value!.AcknowledgeCanceled(diagnostic, deadline.Token);
            }
        }
    }
    private static bool Contains(Exception graph, Exception original) => ReferenceEquals(graph, original) ||
        (graph is AggregateException aggregate && aggregate.InnerExceptions.Any(child => Contains(child, original))) ||
        (graph.InnerException is { } inner && Contains(inner, original));
    private static async Task ReturnedContext()
    {
        var context = new Context(); var fresh = new Context();
        var acquired = Held<Context?>(); var entered = Held<bool>(); var callback = Held<bool>();
        var owner = NewOwner(context, () => { });
        var completion = owner.WithSessionAsync(() => acquired.Task, result => result, (actual, token) =>
        {
            Check(ReferenceEquals(actual, fresh) && token == fresh.OperationCancellationToken);
            try { _ = owner.CloseAsync(); throw new IOException("Callback self-wait admitted."); } catch (InvalidOperationException) { }
            entered.SetResult(true); return new(callback.Task);
        });
        var close = owner.CloseAsync(); Check(!close.IsCompleted && ReferenceEquals(close, owner.CloseAsync()));
        Check(ReferenceEquals(owner.CaptureOriginals().Single().Original, acquired.Task));
        acquired.SetResult(fresh); await Gate(entered.Task, completion);
        Check(!close.IsCompleted && !completion.IsCompleted);
        Check(owner.CaptureOriginals().Any(row => ReferenceEquals(row.Original, callback.Task)));
        callback.SetResult(true); Check(ReferenceEquals(await completion, fresh)); await close;
        Check(owner.SettlementOriginal!.IsCompletedSuccessfully);
    }
    private static async Task CompactionCallback()
    {
        var engine = Held<bool>(); var entered = Held<bool>(); var callback = Held<bool>();
        var owner = NewOwner(new Context(), () => { });
        var result = new ExtensionBehaviorCompactionResult(JsonData.EmptyObject);
        var completion = owner.CompactAsync(() => new(engine.Task, () => result), new(OnComplete: (actual, token) =>
        { Check(ReferenceEquals(actual, result)); entered.SetResult(true); return new(callback.Task); }));
        var close = owner.CloseAsync(); Check(!close.IsCompleted); engine.SetResult(true); await Gate(entered.Task, completion);
        Check(!close.IsCompleted); callback.SetResult(true); await close;
        Check(owner.CaptureOriginals().Length == 2);
    }
    private static async Task CompactionFaults()
    {
        var primary = new IOException("engine-original"); var secondary = new IOException("callback-original");
        var engine = Held<bool>(); var callback = Held<bool>(); var entered = Held<bool>();
        var owner = NewOwner(new Context(), () => { });
        var completion = owner.CompactAsync(() => new(engine.Task, () => null), new(OnError: (actual, token) =>
        { Check(ReferenceEquals(actual, primary)); entered.SetResult(true); return new(callback.Task); }));
        var close = owner.CloseAsync(); engine.SetException(primary); await Gate(entered.Task, completion); Check(!close.IsCompleted);
        callback.SetException(secondary); _ = await Error(completion); var failure = await Error(close);
        Check(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 2);
        Check(Contains(failure, primary) && Contains(failure, secondary));
        var rows = owner.CaptureOriginals(); Check(ReferenceEquals(rows[0].Observed, primary) && ReferenceEquals(rows[1].Observed, secondary));
        Check(ReferenceEquals(close, owner.CloseAsync()));
        Control.Value!.AcknowledgeFault(completion, [primary, secondary]);
        Control.Value!.AcknowledgeOwner(owner, [primary, secondary]);
    }
    private static async Task CancellationSelfWait()
    {
        using var canceled = new CancellationTokenSource();
        var owner = NewOwner(new Context(), () => { });
        var refusals = 0;
        // Registered outside RunMarked: normal Register restores this external context; UnsafeRegister does not flow it.
        Action refuse = () => { try { _ = owner.CloseAsync(); } catch (InvalidOperationException) { refusals++; return; } throw new IOException("Self-wait admitted."); };
        using var normal = canceled.Token.Register(refuse);
        using var unsafeRegistration = canceled.Token.UnsafeRegister(state => ((Action)state!).Invoke(), refuse);
        var host = new Host { Signal = canceled.Cancel };
        owner.Abort(host); Check(refusals == 2 && host.Aborts == 1); await owner.CloseAsync();
    }
    private static async Task FaultedCancellation()
    {
        var fault = new OperationCanceledException("faulted-original"); var actual = Task.FromException<Context?>(fault);
        var owner = NewOwner(new Context(), () => { });
        var mapped = owner.WithSessionAsync(() => actual, value => value);
        var observed = await Error(mapped);
        Check(mapped.IsFaulted && observed is ExtensionFacadeOriginalFaultException originalFault && ReferenceEquals(originalFault.OriginalTask, actual));
        var close = owner.CloseAsync(); Check(Contains(await Error(close), fault) && close.IsFaulted);
        var row = owner.CaptureOriginals().Single(); Check(ReferenceEquals(row.Original, actual) && ReferenceEquals(row.Observed, fault));
        Check(ReferenceEquals(((ExtensionFacadeOriginalFaultException)observed).OriginalException, row.Aggregate));
        Control.Value!.AcknowledgeFault(mapped, [observed, fault]); Control.Value!.AcknowledgeOwner(owner, [fault]);
        var synchronous = new OperationCanceledException("supplier-threw");
        var second = NewOwner(new Context(), () => { });
        var completion = second.WithSessionAsync<Context>(() => throw synchronous, value => value);
        var synchronousCarrier = await Error(completion);
        Check(completion.IsFaulted && synchronousCarrier.GetType() == typeof(InvalidOperationException) &&
            synchronousCarrier.Message == "An adapter supplier threw cancellation without an actual canceled original." && ReferenceEquals(synchronousCarrier.InnerException, synchronous));
        Check(Contains(await Error(second.CloseAsync()), synchronous));
        Control.Value!.AcknowledgeFault(completion, [synchronousCarrier, synchronous]); Control.Value!.AcknowledgeOwner(second, [synchronous]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var canceledOriginal = Task.FromCanceled<Context?>(cancellation.Token);
        var third = NewOwner(new Context(), () => { });
        var canceledMap = third.WithSessionAsync(() => canceledOriginal, value => value);
        _ = await Error(canceledMap); Check(canceledMap.IsCanceled);
        _ = await Error(third.CloseAsync());
        Check(ReferenceEquals(third.CaptureOriginals().Single().Original, canceledOriginal));
        var canceledRow = third.CaptureOriginals().Single();
        Check(canceledRow.Observed is OperationCanceledException && canceledRow.Aggregate is null);
        Control.Value!.Observe(canceledOriginal, canceledRow.Observed!);
        Control.Value!.AcknowledgeCanceled(canceledOriginal, cancellation.Token);
        Control.Value!.AcknowledgeCanceled(canceledMap, cancellation.Token);
        Control.Value!.AcknowledgeOwner(third, [canceledRow.Observed!]);
        var empty = new AggregateException("actual-empty-aggregate-work-original");
        var emptyOriginal = Task.FromException<Context?>(empty);
        var emptyOwner = NewOwner(new Context(), () => { });
        var emptyMapped = emptyOwner.WithSessionAsync(() => emptyOriginal, result => result);
        Check(ReferenceEquals(await Error(emptyMapped), empty) && emptyMapped.IsFaulted);
        var emptyClose = emptyOwner.CloseAsync();
        Check(ReferenceEquals(await Error(emptyClose), empty) && emptyClose.IsFaulted && ReferenceEquals(emptyClose, emptyOwner.CloseAsync()));
        var emptyEvidence = emptyOwner.CaptureOriginals().Single();
        Check(ReferenceEquals(emptyEvidence.Original, emptyOriginal) && ReferenceEquals(emptyEvidence.Observed, empty));
        Check(emptyEvidence.Aggregate!.InnerExceptions.Count == 1 && ReferenceEquals(emptyEvidence.Aggregate.InnerExceptions[0], empty));
        Check(emptyOwner.SettlementOriginal!.IsFaulted);
        Control.Value!.AcknowledgeFault(emptyMapped, [empty]); Control.Value!.AcknowledgeOwner(emptyOwner, [empty]);
    }
    private static async Task ConfigurationOriginal()
    {
        var actual = Held<bool>(); var context = new Context(); var active = true; var host = new Host { Configuration = actual.Task };
        var owner = NewOwner(context, () => { if (!active) throw new InvalidOperationException("Origin retired."); });
        var mapped = owner.SetThinkingLevelAsync(host, "high"); Check(host.Level == "high" && ReferenceEquals(host.Context, context));
        var close = owner.CloseAsync(); Check(!close.IsCompleted); actual.SetResult(true); await mapped; await close;
        Check(ReferenceEquals(owner.CaptureOriginals().Single().Original, actual.Task));
        active = false;
        try { owner.Abort(host); throw new IOException("Retired origin accepted."); } catch (InvalidOperationException) { }
        Check(host.Aborts == 0);
        await CollectorNegativeControls();
    }
    private static async Task CollectorNegativeControls()
    {
        using var expectedCancel = new CancellationTokenSource(); expectedCancel.Cancel();
        using var unexpectedCancel = new CancellationTokenSource(); unexpectedCancel.Cancel();
        var expectedOriginal = Task.FromCanceled(expectedCancel.Token);
        var unexpectedOriginal = Task.FromCanceled(unexpectedCancel.Token);
        async Task CanceledSiblingProbe()
        {
            _ = Track(expectedOriginal); _ = Track(unexpectedOriginal);
            var observed = await Error(expectedOriginal);
            Check(observed is OperationCanceledException error && error.CancellationToken == expectedCancel.Token && expectedOriginal.IsCanceled);
            Control.Value!.AcknowledgeCanceled(expectedOriginal, expectedCancel.Token);
        }
        Func<Task> canceledBody = CanceledSiblingProbe;
        var canceledProbe = Track(Controlled(canceledBody)); var canceledCarrier = await Error(canceledProbe);
        var canceledRows = CaptureEvidence(canceledBody.Method.Name);
        var unexpectedRow = canceledRows.Single(row => ReferenceEquals(row.Original, unexpectedOriginal));
        var expectedRow = canceledRows.Single(row => ReferenceEquals(row.Original, expectedOriginal));
        Check(canceledProbe.IsFaulted && unexpectedRow.Observed is OperationCanceledException actual && actual.CancellationToken == unexpectedCancel.Token);
        Check(Contains(canceledCarrier, unexpectedRow.Observed!) && !Contains(canceledCarrier, expectedRow.Observed!));
        Control.Value!.AcknowledgeFault(canceledProbe, [unexpectedRow.Observed!]);
        Control.Value!.AppendJoinedEvidence(canceledRows);

        var empty = new AggregateException("unexpected-empty-original"); var emptyOriginal = Task.FromException(empty);
        Task EmptyAggregateProbe() { Track(emptyOriginal); return Task.CompletedTask; }
        Func<Task> emptyBody = EmptyAggregateProbe;
        var emptyProbe = Track(Controlled(emptyBody)); var emptyCarrier = await Error(emptyProbe);
        var emptyRows = CaptureEvidence(emptyBody.Method.Name);
        var emptyRow = emptyRows.Single(row => ReferenceEquals(row.Original, emptyOriginal));
        Check(emptyOriginal.IsFaulted && emptyRow.Aggregate!.InnerExceptions.Count == 1 && ReferenceEquals(emptyRow.Aggregate.InnerExceptions[0], empty));
        Check(Contains(emptyCarrier, empty)); Control.Value!.AcknowledgeFault(emptyProbe, [empty]);
        Control.Value!.AppendJoinedEvidence(emptyRows);

        var late = new IOException("late-callback-before-gate"); ExtensionSessionBehaviorOperationOwner? lateOwner = null;
        async Task LateCallbackProbe()
        {
            lateOwner = NewOwner(new Context(), () => { }); var entered = Held<bool>(); var callback = Held<bool>();
            var completion = lateOwner.WithSessionAsync(() => Task.FromResult<Context?>(new Context()), result => result,
                (result, token) => { _ = callback; throw late; });
            await Gate(entered.Task, completion);
        }
        Func<Task> lateBody = LateCallbackProbe;
        var lateProbe = Track(Controlled(lateBody)); var lateCarrier = await Error(lateProbe);
        var lateRows = CaptureEvidence(lateBody.Method.Name);
        Check(lateOwner is not null && lateOwner.SettlementOriginal!.IsCompleted && lateRows.All(row => row.Original.IsCompleted));
        Check(Contains(lateCarrier, late)); Control.Value!.AcknowledgeFault(lateProbe, [late]);
        Control.Value!.AppendJoinedEvidence(lateRows);

        var known = new OperationCanceledException("known-inner-original");
        var wrapper = new InvalidOperationException("unexpected-wrapper-over-known-original", known);
        var wrappedOriginal = Task.FromException(wrapper);
        async Task UnexpectedWrapperProbe()
        {
            _ = Track(wrappedOriginal); Check(ReferenceEquals(await Error(wrappedOriginal), wrapper));
            var refused = false;
            try { Control.Value!.AcknowledgeFault(wrappedOriginal, [known]); }
            catch (IOException error) when (error.Message == "Session behavior custody control failed.") { refused = true; }
            Check(refused && !Control.Value!.IsAcknowledged(wrappedOriginal));
        }
        Func<Task> wrapperBody = UnexpectedWrapperProbe;
        var wrapperProbe = Track(Controlled(wrapperBody)); var wrapperCarrier = await Error(wrapperProbe);
        var wrapperRows = CaptureEvidence(wrapperBody.Method.Name);
        Check(Contains(wrapperCarrier, wrapper) && Contains(wrapperCarrier, known));
        Check(ReferenceEquals(wrapperRows.Single(evidence => ReferenceEquals(evidence.Original, wrappedOriginal)).Observed, wrapper));
        Control.Value!.AcknowledgeFault(wrapperProbe, [wrapper, known]); Control.Value!.AppendJoinedEvidence(wrapperRows);
    }
    private static void RefuseSettlement(ExtensionSessionBehaviorOperationOwner owner)
    {
        try { _ = owner.CloseAsync(); throw new IOException("Ancestor Close self-join admitted."); } catch (InvalidOperationException) { }
        try { _ = owner.DisposeAsync(); throw new IOException("Ancestor Dispose self-join admitted."); } catch (InvalidOperationException) { }
    }
    private static async Task NestedCallbackAncestry()
    {
        var context = new Context();
        var a = NewOwner(context, () => { });
        var b = NewOwner(context, () => { });
        var bEngine = Held<Context?>(); var bCallback = Held<bool>();
        var bStarted = Held<bool>(); var entered = Held<bool>();
        Task<Context?>? bMapped = null;
        var aEngine = Task.FromResult<Context?>(context);
        var aMapped = a.WithSessionAsync(() => aEngine, actual => actual, (actual, token) =>
        {
            bMapped = b.WithSessionAsync(() => bEngine.Task, returned => returned, (returned, callbackToken) =>
            { RefuseSettlement(a); RefuseSettlement(b); entered.SetResult(true); return new(bCallback.Task); });
            bStarted.SetResult(true); return new(bMapped);
        });
        await Gate(bStarted.Task, aMapped);
        var aClose = a.CloseAsync(); var bClose = b.CloseAsync();
        Check(ReferenceEquals(aClose, a.CloseAsync()) && ReferenceEquals(bClose, b.CloseAsync()));
        Check(!aClose.IsCompleted && !bClose.IsCompleted);
        bEngine.SetResult(context); await Gate(entered.Task, aMapped, bMapped!);
        Check(!aClose.IsCompleted && !bClose.IsCompleted);
        Check(a.CaptureOriginals().Any(row => ReferenceEquals(row.Original, bMapped)));
        Check(b.CaptureOriginals().Any(row => ReferenceEquals(row.Original, bCallback.Task)));
        var fault = new IOException("nested-callback-original"); bCallback.SetException(fault);
        Check(ReferenceEquals(await Error(bMapped!), fault) && ReferenceEquals(await Error(aMapped), fault));
        Check(ReferenceEquals(await Error(aClose), fault) && ReferenceEquals(await Error(bClose), fault));
        Check(a.SettlementOriginal!.IsCompleted && b.SettlementOriginal!.IsCompleted);
        Check(ReferenceEquals(aClose, a.CloseAsync()) && ReferenceEquals(bClose, b.CloseAsync()));
        Control.Value!.AcknowledgeFault(aMapped, [fault]); Control.Value!.AcknowledgeFault(bMapped!, [fault]);
        Control.Value!.AcknowledgeOwner(a, [fault]); Control.Value!.AcknowledgeOwner(b, [fault]);
    }
    private static async Task NestedCancellationAncestry()
    {
        var a = NewOwner(new Context(), () => { });
        var b = NewOwner(new Context(), () => { });
        using var canceled = new CancellationTokenSource(); var callbacks = 0;
        Action refuse = () => { RefuseSettlement(a); RefuseSettlement(b); callbacks++; };
        using var normal = canceled.Token.Register(refuse);
        using var unsafeRegistration = canceled.Token.UnsafeRegister(state => ((Action)state!).Invoke(), refuse);
        var bHost = new Host { Signal = canceled.Cancel };
        var aHost = new Host { Signal = () => b.Abort(bHost) };
        a.Abort(aHost); Check(callbacks == 2 && aHost.Aborts == 1 && bHost.Aborts == 1);
        var aClose = a.CloseAsync(); var bClose = b.CloseAsync(); await aClose; await bClose;
        Check(ReferenceEquals(aClose, a.CloseAsync()) && ReferenceEquals(bClose, b.CloseAsync()));
    }
    private static async Task LogicalToPhysical()
    {
        var a = NewOwner(new Context(), () => { }); var b = NewOwner(new Context(), () => { });
        using var source = new CancellationTokenSource(); var checkedCallbacks = 0;
        Action refuse = () => { RefuseSettlement(a); RefuseSettlement(b); checkedCallbacks++; };
        // Both handlers are registered before either logical callback is entered.
        using var normal = source.Token.Register(refuse);
        using var unsafeRegistration = source.Token.UnsafeRegister(state => ((Action)state!).Invoke(), refuse);
        var entered = Held<bool>(); var callback = Held<bool>(); var continuation = Held<bool>();
        var completion = a.WithSessionAsync(() => Task.FromResult<Context?>(new Context()), returned => returned, async (returned, token) =>
        {
            await continuation.Task;
            b.Abort(new Host { Signal = source.Cancel });
            entered.SetResult(true); await callback.Task;
        });
        continuation.SetResult(true); await Gate(entered.Task, completion);
        Check(checkedCallbacks == 2);
        var close = a.CloseAsync(); Check(!close.IsCompleted && ReferenceEquals(close, a.CloseAsync()));
        callback.SetResult(true); await completion; await close; await b.CloseAsync();
    }
    private static async Task PhysicalToLogical()
    {
        var a = NewOwner(new Context(), () => { }); var b = NewOwner(new Context(), () => { });
        using var source = new CancellationTokenSource(); var checkedCallbacks = 0;
        Action refuse = () => { RefuseSettlement(a); RefuseSettlement(b); checkedCallbacks++; };
        using var normal = source.Token.Register(refuse);
        using var unsafeRegistration = source.Token.UnsafeRegister(state => ((Action)state!).Invoke(), refuse);
        var engine = Held<Context?>(); var continuation = Held<bool>(); var entered = Held<bool>(); var callback = Held<bool>();
        Task<Context?>? completion = null;
        a.Abort(new Host { Signal = () =>
        {
            completion = b.WithSessionAsync(() => engine.Task, returned => returned, async (returned, token) =>
            {
                await continuation.Task; RefuseSettlement(a); RefuseSettlement(b);
                b.Abort(new Host { Signal = source.Cancel });
                entered.SetResult(true); await callback.Task;
            });
        } });
        engine.SetResult(new Context()); continuation.SetResult(true); await Gate(entered.Task, completion!);
        Check(checkedCallbacks == 2);
        var close = b.CloseAsync(); Check(!close.IsCompleted && ReferenceEquals(close, b.CloseAsync()));
        callback.SetResult(true); await completion!; await close; await a.CloseAsync();
    }
    private static async Task ActualRegisteredHost()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pisharp-behavior-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); PersistentAgentSession? session = null; ReplaceableAgentSession? owner = null;
        Control.Value!.AfterCleanup.Add(() => Directory.Delete(folder, recursive: true));
        Control.Value!.Cleanup.Add(() => owner is not null ? owner.DisposeAsync().AsTask() : session is not null ? ((IAsyncDisposable)session).DisposeAsync().AsTask() : Task.CompletedTask);
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = "behavior-session", timestamp = "2026-01-01T00:00:00Z", cwd = folder }));
        var transport = new NoTransport(); var ids = 0;
        session = await Track(PersistentAgentSession.CreateAsync(Path.Combine(folder, "session.jsonl"), header,
            new AgentConfiguration(new ModelDescriptor("behavior-model", "openai-responses", "synthetic-offline"), transport, []),
            () => 1, () => "behavior-entry-" + ++ids));
        owner = new ReplaceableAgentSession(session, (path, token) => Task.FromException<PersistentAgentSession>(new NotSupportedException()));
        var views = new NativeSessionSnapshotProvider(); views.Attach(owner);
        var reads = new NativeExtensionContextFacadeHost(); reads.Attach(owner);
        var host = new NativeExtensionSessionBehaviorHost(owner, reads);
        var actualSession = session; var calls = 0;
        var registry = new ExtensionRegistry(null, null, views, reads);
        Control.Value!.Cleanup.Add(() => registry.DisposeAsync().AsTask());
        var descriptor = new ExtensionCommandDescriptor("behavior", "behavior", "behavior", async (arguments, context, token) =>
        {
            calls++;
            // Registry supplies the real callback context; reads validate it on every operation.
            var kernel = NewOwner(context, () => { _ = reads.GetCwd(context); });
            await kernel.SetThinkingLevelAsync(host, "medium");
            Check(actualSession.Snapshot.Context.ThinkingLevel == "high");
            await kernel.SetThinkingLevelAsync(host, "max");
            Check(actualSession.Snapshot.Context.ThinkingLevel == "high");
            kernel.Abort(host); await kernel.CloseAsync();
            Check(kernel.CaptureOriginals().Length == 2);
        });
        var pluginOwner = await Track(registry.ActivateAsync("behavior-owner", new Plugin(descriptor)));
        Control.Value!.Cleanup.Add(() => pluginOwner.DisposeAsync().AsTask());
        await Track(registry.InvokeCommandAsync(registry.CaptureSnapshot(), "behavior", JsonData.EmptyObject).AsTask());
        Check(calls == 1 && transport.Calls == 0);
    }
    private sealed class Plugin(ExtensionCommandDescriptor command) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        { registry.RegisterCommand(command); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoTransport : IChatTransport, IThinkingLevelTransport
    {
        internal int Calls;
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) => ["off", "high"];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new IOException("No provider call admitted in behavior control.")); yield break; }
    }
    private sealed class Context : IExtensionCommandContext
    {
        public string OwnerId => "behavior-control"; public long OwnerGeneration => 1;
        public CancellationToken OperationCancellationToken => default;
        public CancellationToken SessionCancellationToken => default;
        public CancellationToken ExtensionLifetimeCancellationToken => default;
    }
    private sealed class Host : IExtensionDirectSessionBehaviorHost
    {
        internal Task Configuration = Task.CompletedTask; internal Action? Signal; internal int Aborts;
        internal string? Level; internal IExtensionCommandContext? Context;
        public Task SetThinkingLevel(IExtensionCommandContext context, string level, CancellationToken token)
        { Context = context; Level = level; return Configuration; }
        public ExtensionBehaviorCompactionWork Compact(IExtensionCommandContext context, string? instructions, CancellationToken token) => throw new NotSupportedException();
        public void Abort(IExtensionContext context) { Aborts++; Signal?.Invoke(); }
    }
}
