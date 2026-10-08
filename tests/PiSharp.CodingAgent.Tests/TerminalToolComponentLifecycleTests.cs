using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Tui;

internal static class TerminalToolComponentLifecycleTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [ ("tool-row.actual-held-source-write-disposal-and-persistent-lease", Held),
      ("tool-row.actual-generation-stale-before-source-and-physical-write", Stale) ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Actual tool presentation lifecycle control failed."); }
    private static async Task Held() => await Run(false);
    private static async Task Stale() => await Run(true);
    private static async Task Run(bool stale)
    {
        var console = new ConsoleFixture(); var view = new TerminalSessionView(console, new Viewport()); long generation = 1;
        var input = new TerminalExtensionInputAdmission(1, () => generation);
        var registry = new ExtensionRegistry(null, new TerminalCustomComponentUiProvider(new UnavailableExtensionUiProvider(), view, input, () => generation));
        RegistrationScope? scope = null; IExtensionToolComponentPresentation? lease = null;
        IExtensionContext? createContext = null, paintContext = null;
        var renderEntered = Gate(); var rendered = new TaskCompletionSource<ExtensionCustomComponentRows>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeEntered = Gate(); var disposed = Gate(); bool unloaded = false; int calls = 0;
        var originals = new List<Task> { rendered.Task, disposed.Task }; var errors = new List<Exception>();
        var settled = new Dictionary<Task, Exception?>(ReferenceEqualityComparer.Instance);
        var expected = new HashSet<Task>(ReferenceEqualityComparer.Instance);
        var knownFaultNodes = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var cleanupTargets = new Dictionary<Task, string>(ReferenceEqualityComparer.Instance);
        CancellationTokenRegistration cancellationObserver = default; bool heldFaultsAsserted = false;
        try
        {
            var start = view.StartAsync(CancellationToken.None).AsTask(); originals.Add(start); await start;
            var activation = registry.ActivateAsync("tool-host", new Extension(entries => entries.RegisterCommand(new("attach", "attach", "",
                (_, context, _) =>
                {
                    createContext = context;
                    var identity = new ExtensionCustomComponentIdentity(context.OwnerId, context.OwnerGeneration, generation, "tool-component-1");
                    lease = ((IExtensionToolComponentUi)((IExtensionUiContext)context).Ui).AttachToolComponent(new(identity,
                        (_, fresh, _) => { calls++; paintContext = fresh; renderEntered.TrySetResult(); return rendered.Task; },
                        (_, _, _) => throw new InvalidOperationException("Tool row borrowed raw input authority."),
                        _ => { disposeEntered.TrySetResult(); return disposed.Task; }));
                    return ValueTask.CompletedTask;
                })), () => { unloaded = true; return ValueTask.CompletedTask; })); originals.Add(activation); scope = await activation;
            var attach = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "attach", JsonData.EmptyObject).AsTask(); originals.Add(attach); await attach;
            var show = lease!.ShowAsync(); originals.Add(show);
            await Task.WhenAny(renderEntered.Task, show); Check(renderEntered.Task.IsCompleted && !show.IsCompleted && calls == 1 && !ReferenceEquals(createContext, paintContext));
            if (!stale) console.HoldNext = true;
            rendered.TrySetResult(new(["held-original-row"], [17]));
            if (stale)
            {
                await show; var beforeCalls = calls; var beforeWrites = console.Started; generation++;
                bool refused = false; try { _ = lease.InvalidateAsync(); } catch (InvalidOperationException) { refused = true; }
                Check(refused && calls == beforeCalls && console.Started == beforeWrites);
                disposed.TrySetResult();
            }
            else
            {
                await Task.WhenAny(console.WriteEntered.Task, show); Check(console.WriteEntered.Task.IsCompleted && !show.IsCompleted);
                var ownedPaintToken = paintContext!.OperationCancellationToken;
                var canceled = Gate(); cancellationObserver = ownedPaintToken.Register(() => canceled.TrySetResult());
                var unload = scope.DisposeAsync().AsTask(); originals.Add(unload); Check(!unload.IsCompleted && console.Active == 1 && !unloaded);
                var cancellationWinner = await Task.WhenAny(canceled.Task, show, unload).WaitAsync(TimeSpan.FromSeconds(5));
                Check(ReferenceEquals(cancellationWinner, canceled.Task) && ownedPaintToken.IsCancellationRequested && !show.IsCompleted && console.Active == 1);
                console.Release.TrySetResult();
                var showError = await Observe(show);
                Check(show.IsFaulted && !show.IsCanceled && showError is not null);
                var showNodes = FaultGraph(showError!);
                Check(showNodes.Any(node => node is OperationCanceledException) && showNodes.All(node => node is AggregateException aggregate && aggregate.InnerExceptions.Count > 0 ||
                    node is OperationCanceledException cancel && cancel.CancellationToken == ownedPaintToken && ownedPaintToken.IsCancellationRequested));
                foreach (var node in showNodes.Where(node => node is not AggregateException)) knownFaultNodes.Add(node);
                expected.Add(show);
                // A new faulted OCE with the same token or an unexpected wrapper is never admitted.
                var alien = Task.FromException(new OperationCanceledException(ownedPaintToken)); originals.Add(alien);
                var alienError = await Observe(alien); Check(alien.IsFaulted && !IsKnownGraph(alienError!)); expected.Add(alien);
                var unexpectedWrapper = new IOException("Unexpected wrapper over the exact known cancellation.", knownFaultNodes.First());
                Check(!IsKnownGraph(unexpectedWrapper));
                var emptyEnvelope = (ExtensionRegistrationException)Activator.CreateInstance(typeof(ExtensionRegistrationException),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                    [ExtensionRegistrationFailure.CleanupFailed, "tool-host", "dispose", new AggregateException()], null)!;
                Check(!IsKnownGraph(new AggregateException()) && !AdmitClosingGraph(emptyEnvelope, "tool-host"));
                var disposalWinner = await Task.WhenAny(disposeEntered.Task, unload).WaitAsync(TimeSpan.FromSeconds(5));
                Check(ReferenceEquals(disposalWinner, disposeEntered.Task) && !unload.IsCompleted && !unloaded);
                disposed.TrySetResult(); var unloadError = await Observe(unload);
                Check(unload.IsFaulted && unloadError is not null && AdmitClosingGraph(unloadError, "tool-host") && unloaded);
                expected.Add(unload); heldFaultsAsserted = true;
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            rendered.TrySetResult(new(["held-original-row"], [17])); disposed.TrySetResult(); console.Release.TrySetResult();
            if (lease is not null) Acquire(() => lease.DisposeAsync().AsTask(), "tool-host");
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask(), "tool-host");
            Acquire(() => registry.DisposeAsync().AsTask(), "registry");
            foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
            {
                var error = await Observe(original);
                if (error is not null && !expected.Contains(original))
                {
                    try
                    {
                        if (heldFaultsAsserted && original.IsFaulted && cleanupTargets.TryGetValue(original, out var owner) && AdmitClosingGraph(error, owner)) expected.Add(original);
                        else errors.Add(error);
                    }
                    catch (Exception metadataError) { errors.Add(error); errors.Add(metadataError); }
                }
            }
            try { cancellationObserver.Dispose(); } catch (Exception error) { errors.Add(error); }
            // The view remains usable until every physical-retirement and registry original settles.
            Task? viewCleanup = null;
            try { viewCleanup = view.DisposeAsync().AsTask(); await viewCleanup; }
            catch (Exception error) { errors.Add(viewCleanup?.Exception ?? error); }
            if (console.Active != 0 || console.Started != console.Settled || console.Disposed) errors.Add(new InvalidOperationException("Borrowed physical originals not joined."));
        }
        if (errors.Count != 0) throw new AggregateException(errors);
        void Acquire(Func<Task> acquire, string owner)
        { try { var original = acquire(); originals.Add(original); cleanupTargets.TryAdd(original, owner); } catch (Exception error) { errors.Add(error); } }
        async Task<Exception?> Observe(Task original)
        {
            if (settled.TryGetValue(original, out var prior)) return prior;
            Exception? full = null; try { await original; } catch (Exception error) { full = original.Exception ?? error; }
            settled.Add(original, full); return full;
        }
        bool IsKnownGraph(Exception root)
        {
            var nodes = FaultGraph(root);
            return nodes.Any(knownFaultNodes.Contains) && nodes.All(node => node is AggregateException aggregate ? aggregate.InnerExceptions.Count > 0 : knownFaultNodes.Contains(node));
        }
        bool AdmitClosingGraph(Exception root, string owner)
        {
            var nodes = FaultGraph(root);
            if (nodes.Any(node => node is AggregateException aggregate && aggregate.InnerExceptions.Count == 0)) return false;
            var unknown = nodes.Where(node => node is not AggregateException && !knownFaultNodes.Contains(node)).ToArray();
            if (unknown.Length == 0) return nodes.Any(knownFaultNodes.Contains);
            if (unknown.Length != 1 || unknown[0] is not ExtensionRegistrationException { Failure: ExtensionRegistrationFailure.CleanupFailed, Operation: "dispose" } wrapper ||
                wrapper.OwnerId != owner || wrapper.InnerException is null || !IsKnownGraph(wrapper.InnerException)) return false;
            knownFaultNodes.Add(wrapper); return true;
        }
    }
    private static HashSet<Exception> FaultGraph(Exception root)
    {
        var found = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var pending = new Stack<Exception>(); pending.Push(root); int edges = 0;
        while (pending.TryPop(out var node))
        {
            if (!found.Add(node)) continue; Check(found.Count <= 1024);
            IEnumerable<Exception> children = node is AggregateException aggregate ? aggregate.InnerExceptions : node.InnerException is { } inner ? [inner] : [];
            foreach (var child in children) { Check(++edges <= 4096); pending.Push(child); }
        }
        return found;
    }
    private sealed class Extension(Action<IExtensionRegistry> initialize, Func<ValueTask> dispose) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => dispose();
    }
    private sealed class Viewport : ITerminalViewportSource { public TerminalViewport ReadViewport() => new(80, 8, 0, 0, 80, 8); }
    private sealed class ConsoleFixture : IConsoleTerminal
    {
        internal bool HoldNext, Disposed; internal int Active, Started, Settled;
        internal readonly TaskCompletionSource WriteEntered = Gate(), Release = Gate();
        public TerminalLeaseSnapshot Snapshot => new(new(0, 0, 65001, 65001, 25, true, 0, 0), new(0, 0, 65001, 65001, 25, true, 0, 0), null, false, false, 0, Active, 0, 0, Started, Settled);
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw new InvalidOperationException("Tool presentation owns no input.");
        public async ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        {
            Check(Interlocked.Increment(ref Active) == 1); Interlocked.Increment(ref Started);
            try { if (HoldNext) { HoldNext = false; WriteEntered.TrySetResult(); await Release.Task; } }
            finally { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Settled); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
