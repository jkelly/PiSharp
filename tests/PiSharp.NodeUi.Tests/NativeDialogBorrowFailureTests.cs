using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.ExceptionServices;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.ExtensionHost.Protocol;

// Caller must invoke this under an actual registry command, with its already-initialized
// same-owner Node extension. This fixture acquires no process, source launch or replacement scope.
internal static class NativeDialogBorrowFailureTests
{
    internal sealed record CapturedOriginal(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly List<CapturedOriginal> retainedOriginals = [];
    internal static CapturedOriginal[] RawCapturedOriginals
    { get { lock (retainedOriginals) return retainedOriginals.ToArray(); } }
    private static void Retain(IEnumerable<CapturedOriginal> originals)
    { lock (retainedOriginals) retainedOriginals.AddRange(originals); }
    private static object Call(object target, string name, params object?[] args)
    {
        try { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!; }
        catch (TargetInvocationException error) when (error.InnerException is { } direct)
        { ExceptionDispatchInfo.Capture(direct).Throw(); throw; }
    }
    private static void Check(bool value) { if (!value) throw new IOException("Actual pre-request dialog retirement failed."); }
    internal static async Task RunAsync(NodeCommandInputExtension node, IExtensionCommandContext actualContext)
    {
        Check(node.WorkerSnapshot is { Ready: true, Stopped: false, PendingCalls: 0 } && node.ActiveContexts == 0);
        string? operation = null; object? child = null;
        Task<JsonData>? invocation = null; Task? retirement = null;
        AggregateException? invocationAggregate = null, retirementAggregate = null;
        Exception? invocationDirect = null, retirementDirect = null;
        bool invocationObserved = false, retirementObserved = false, exactFaultAsserted = false;
        var failures = new List<Exception>();
        try
        {
            operation = (string)Call(node, "Borrow", actualContext, null, null);
            var contexts = (IDictionary)typeof(NodeCommandInputExtension).GetField("contexts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
            var borrowed = contexts[operation]!;
            child = borrowed.GetType().GetProperty("Dialogs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(borrowed)!;
            Check(child is not null && node.ActiveContexts == 1);
            invocation = (Task<JsonData>)Call(node, "InvokeAsync", "command-input.command", operation,
                new Dictionary<string, object?> { ["overBudget"] = new string('x', 300_000) }, actualContext, CancellationToken.None, true);
            try { await invocation; }
            catch (Exception error) { invocationDirect = error; if (invocation.IsFaulted) invocationAggregate = invocation.Exception; }
            finally { invocationObserved = true; }
            Check(invocation.IsFaulted && invocationDirect is IOException { Message: "Commands/Input request byte budget." } &&
                invocationAggregate is not null && invocationAggregate.InnerExceptions.Contains(invocationDirect) &&
                node.WorkerSnapshot is { PendingCalls: 0 });
            exactFaultAsserted = true;
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            // The production finalizer is reached even though the actual InvokeAsync original
            // failed before entering its request/settlement try. Keep the first raw graph once.
            if (operation is not null)
                try
                {
                    retirement = (Task)Call(node, "RetireContextAsync", operation, invocationDirect);
                    try { await retirement; }
                    catch (Exception error) { retirementDirect = error; if (retirement.IsFaulted) retirementAggregate = retirement.Exception; }
                    finally { retirementObserved = true; }
                }
                catch (Exception error) { failures.Add(error); }
            if (invocation is not null && !invocationObserved)
                try { await invocation; }
                catch (Exception error) { invocationDirect = error; if (invocation.IsFaulted) invocationAggregate = invocation.Exception; }
            if (retirement is not null && !retirementObserved)
                try { await retirement; }
                catch (Exception error) { retirementDirect = error; if (retirement.IsFaulted) retirementAggregate = retirement.Exception; }
            Retain(new[]
            {
                invocation is null ? null : new CapturedOriginal("actual-overbudget-invoke-before-request", invocation, invocationAggregate, invocationDirect),
                retirement is null ? null : new CapturedOriginal("actual-borrowed-finalizer", retirement, retirementAggregate, retirementDirect)
            }.OfType<CapturedOriginal>());
        }
        try
        {
            Check(child is not null);
            var participant = child!.GetType().GetField("participant", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(child)!;
            Check((bool)participant.GetType().GetProperty("IsRetired")!.GetValue(participant)! && node.ActiveContexts == 0 &&
                retirement is { IsCompletedSuccessfully: true } && node.WorkerSnapshot is { PendingCalls: 0 });
        }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0 || !exactFaultAsserted || retirementDirect is not null)
            throw new AggregateException("Pre-request criteria and all actual original failures.", failures.Concat(
                RawCapturedOriginals.SelectMany(original => new Exception?[] { original.Aggregate, original.Direct }).OfType<Exception>()));
    }

    // The caller installs this concrete borrowed provider in its real registry before Node
    // activation, then invokes this method in that same owner's actual command context.
    internal static async Task RunRetirementFaultAsync(NodeCommandInputExtension node,
        IExtensionCommandContext actualContext, RetirementFaultUiProvider provider)
    {
        Check(node.WorkerSnapshot is { Ready: true, Stopped: false, PendingCalls: 0 } && node.ActiveContexts == 0);
        using var parent = new CancellationTokenSource();
        string? operation = null; object? child = null;
        Task<WorkerValue>? open = null; Task<JsonData>? invocation = null; Task? finalization = null;
        var records = new Dictionary<Task, CapturedOriginal>(ReferenceEqualityComparer.Instance);
        var failures = new List<Exception>(); bool exactFaultAsserted = false;
        async Task Observe(string phase, Task original)
        {
            if (records.ContainsKey(original)) return;
            AggregateException? aggregate = null; Exception? direct = null;
            try { await original; }
            catch (Exception error) { direct = error; if (original.IsFaulted) aggregate = original.Exception; }
            records.Add(original, new(phase, original, aggregate, direct));
        }
        try
        {
            operation = (string)Call(node, "Borrow", actualContext, null, null);
            var contexts = (IDictionary)typeof(NodeCommandInputExtension).GetField("contexts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
            var borrowed = contexts[operation]!;
            child = borrowed.GetType().GetProperty("Dialogs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(borrowed)!;
            var reserve = (WorkerValue)Call(child, "Reserve", "dialog-1", "ui.input");
            var scope = reserve.Json!.Value.GetProperty("scopeId").GetString()!;
            open = (Task<WorkerValue>)Call(child, "Open", "dialog-1", scope, 9L, "ui.input", JsonData.Parse("[\"held\"]"), false, CancellationToken.None);
            var winner = await Task.WhenAny(provider.Entered.Task, open).WaitAsync(TimeSpan.FromSeconds(10));
            Check(ReferenceEquals(winner, provider.Entered.Task) && provider.InputOriginal is { IsCompleted: false });
            parent.Cancel();
            invocation = (Task<JsonData>)Call(node, "InvokeAsync", "command-input.command", operation,
                new Dictionary<string, object?>(), actualContext, parent.Token, true);
            winner = await Task.WhenAny(provider.CancellationEntered.Task, invocation).WaitAsync(TimeSpan.FromSeconds(10));
            Check(ReferenceEquals(winner, provider.CancellationEntered.Task));
            provider.Release.TrySetResult();
            await Observe("parent-canceled-source-invoke-with-retirement-fault", invocation);
            Check(invocation.IsFaulted && !invocation.IsCanceled && records[invocation].Aggregate is { } graph &&
                Contains(graph, provider.Leaf) && ContainsCancellation(graph, parent.Token));
            exactFaultAsserted = true;
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            provider.Release.TrySetResult();
            if (invocation is not null) await Observe("source-invoke", invocation);
            if (operation is not null)
                try
                {
                    finalization = (Task)Call(node, "RetireContextAsync", operation,
                        invocation is not null && records.TryGetValue(invocation, out var record) ? record.Direct : null);
                    await Observe("finalization-keeps-primary-and-retirement", finalization);
                }
                catch (Exception error) { failures.Add(error); }
            if (open is not null) await Observe("native-registry-open", open);
            if (provider.InputOriginal is { } actual) await Observe("actual-native-input", actual);
            Retain(records.Values);
        }
        try
        {
            Check(finalization is { IsFaulted: true } && records[finalization].Aggregate is { } graph &&
                Contains(graph, provider.Leaf) && ContainsCancellation(graph, parent.Token) && node.ActiveContexts == 0);
        }
        catch (Exception error) { failures.Add(error); }
        // Any failed criterion carries every raw original, even ones expected to fail on success.
        if (failures.Count != 0 || !exactFaultAsserted)
            throw new AggregateException("Cancellation/retirement criteria and every actual original.", failures.Concat(
                records.Values.SelectMany(record => new Exception?[] { record.Aggregate, record.Direct }).OfType<Exception>()));
    }
    private static IEnumerable<Exception> Graph(Exception root)
    {
        var pending = new Stack<Exception>(); pending.Push(root);
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var edges = 0;
        while (pending.TryPop(out var error))
        {
            if (!seen.Add(error)) continue;
            if (seen.Count > 1024) throw new IOException("Control exception graph node limit.");
            yield return error;
            IEnumerable<Exception> children = error is AggregateException aggregate ? aggregate.InnerExceptions :
                error.InnerException is { } inner ? new[] { inner } : Array.Empty<Exception>();
            foreach (var child in children) { if (++edges > 4096) throw new IOException("Control exception graph edge limit."); pending.Push(child); }
        }
    }
    private static bool Contains(Exception root, Exception expected) => Graph(root).Any(error => ReferenceEquals(error, expected));
    private static bool ContainsCancellation(Exception root, CancellationToken token) => Graph(root).Any(error =>
        error is OperationCanceledException cancellation && cancellation.CancellationToken == token && token.IsCancellationRequested);

    internal sealed class RetirementFaultUiProvider : IExtensionUiProvider
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously), CancellationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly IOException Leaf = new("actual registered dialog cancellation failure");
        internal Task? InputOriginal;
        public IExtensionUiScope OpenScope(IExtensionContext context) => new Scope(this);
        private async Task<ExtensionUiOutcome<string>> Input(CancellationToken token)
        {
            using var registration = token.Register(() => { CancellationEntered.TrySetResult(); throw Leaf; });
            await Release.Task;
            // Real callback returns an ordinary native canceled outcome. The independent real
            // CTS.Cancel original faults from the registered handler and must not be lost.
            return ExtensionUiOutcome<string>.Cancelled();
        }
        private sealed class Scope(RetirementFaultUiProvider owner) : IExtensionUiScope
        {
            public ExtensionUiCapabilities Capabilities => new(ExtensionUiMode.Tui, 1, 9, [ExtensionUiFeature.Input]);
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default)
            { var original = owner.Input(cancellationToken); owner.InputOriginal = original; owner.Entered.TrySetResult(); return new(original); }
            public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
