using System.Reflection;
using System.Runtime.ExceptionServices;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

// Reflection reaches the actual internal production owner; no duplicate helper, synthetic
// supervisor, process launch or public SDK surface is introduced for qualification.
internal static class NativeComponentOpenLifetimeTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [ ("native-open.actual-owner-frame-free-stop-reentry-and-held-retirement", () => Run(false, false)),
      ("native-open.actual-owner-nested-frame-free-ancestor-and-unrelated-retirement", () => Run(true, false)),
      ("native-open.actual-owner-retirement-retains-cancel-sibling-originals", () => Run(false, true)) ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Actual native open owner control failed."); }
    private static readonly Type OwnerType = typeof(NodeCommandInputExtension).Assembly.GetType("PiSharp.Compatibility.Node.NativeComponentOpenLifetime", true)!;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Create(ExtensionRegistry.RegisteredExtensionComponent participant) =>
        Activator.CreateInstance(OwnerType, Members, null, [participant], null)!;
    private static CancellationToken Token(object owner) => (CancellationToken)OwnerType.GetProperty("Token", Members)!.GetValue(owner)!;
    private static Task Invoke(object owner, string method)
    {
        try { return (Task)OwnerType.GetMethod(method, Members)!.Invoke(owner, null)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static Task Stop(object owner) => Invoke(owner, "RequestStop");
    private static Task Retire(object owner) => Invoke(owner, "RetireAndJoinStopAsync");

    private static async Task Run(bool nested, bool fault)
    {
        var registry = new ExtensionRegistry(); RegistrationScope? scope = null;
        ExtensionRegistry.RegisteredExtensionComponent? marker = null;
        object? first = null, second = null, unrelated = null;
        var originals = new List<Task>(); var observed = new Dictionary<Task, Exception?>(ReferenceEqualityComparer.Instance);
        var acknowledged = new HashSet<Task>(ReferenceEqualityComparer.Instance); var collected = new HashSet<Task>(ReferenceEqualityComparer.Instance); var errors = new List<Exception>();
        var registrations = new List<CancellationTokenRegistration>(); var entered = Gate(); var release = Gate();
        var left = new IOException("Actual native Cancel left original."); var right = new IOException("Actual native Cancel sibling original.");
        bool ownRefused = false, ancestorRefused = false, registryRefused = false, unrelatedClosed = false;
        try
        {
            var activation = registry.ActivateAsync("native-open-owner", new Extension(entries => entries.RegisterCommand(new("bind", "bind", "",
                (_, context, _) =>
                {
                    marker = ExtensionRegistry.BindCustomComponentContext(context, "native-owner-marker", 1, () => 1,
                        () => Task.CompletedTask, _ => Task.CompletedTask);
                    first = Create(marker); second = Create(marker); unrelated = Create(marker);
                    return ValueTask.CompletedTask;
                }))));
            Track(activation); scope = await activation;
            var bind = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "bind", JsonData.EmptyObject).AsTask(); Track(bind); await bind;
            var revision = registry.CaptureSnapshot().Revision;
            // These are normal Register callbacks with a frame-free captured ExecutionContext.
            var frameFree = new AsyncLocal<object?>(); var capturedContextSentinel = new object();
            frameFree.Value = capturedContextSentinel;
            try
            {
                registrations.Add(Token(first!).Register(() =>
                {
                    Check(ReferenceEquals(frameFree.Value, capturedContextSentinel));
                    try { _ = Retire(first!); } catch (InvalidOperationException) { ownRefused = true; }
                    try { _ = scope!.DisposeAsync(); }
                    catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { registryRefused = true; }
                    if (nested)
                    {
                        var original = Stop(second!); Track(original); original.GetAwaiter().GetResult();
                    }
                    entered.TrySetResult(); release.Task.GetAwaiter().GetResult();
                }));
                if (nested) registrations.Add(Token(second!).Register(() =>
                {
                    Check(ReferenceEquals(frameFree.Value, capturedContextSentinel));
                    try { _ = Retire(first!); } catch (InvalidOperationException) { ancestorRefused = true; }
                    var original = Retire(unrelated!); Track(original); original.GetAwaiter().GetResult(); unrelatedClosed = true;
                }));
                if (fault)
                {
                    registrations.Add(Token(first!).Register(() => throw left));
                    registrations.Add(Token(first!).Register(() => throw right));
                }
            }
            finally { frameFree.Value = null; }
            var stopping = Stop(first!); Track(stopping);
            var winner = await Task.WhenAny(entered.Task, stopping).WaitAsync(TimeSpan.FromSeconds(5)); Check(ReferenceEquals(winner, entered.Task));
            Check(ownRefused && registryRefused && !marker!.IsRetired && registry.CaptureSnapshot().Revision == revision);
            if (nested) Check(ancestorRefused && unrelatedClosed);
            var retiring = Retire(first!); Track(retiring); var same = Retire(first!); Track(same);
            Check(ReferenceEquals(retiring, same) && !retiring.IsCompleted && !stopping.IsCompleted);
            release.TrySetResult();
            if (fault)
            {
                var stopError = await Observe(stopping); var retireError = await Observe(retiring);
                Check(stopError is not null && retireError is not null && Contains(stopError, left) && Contains(stopError, right) &&
                    Contains(retireError, left) && Contains(retireError, right) && OnlyKnown(stopError) && OnlyKnown(retireError));
                acknowledged.Add(stopping); acknowledged.Add(retiring);
            }
            else { await stopping; await retiring; }
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            release.TrySetResult();
            foreach (var owner in new[] { first, second, unrelated }) if (owner is not null) Acquire(() => Retire(owner));
            // Join every real Cancel/retirement before joining cancellation registrations or marker cleanup.
            await Join();
            foreach (var registration in registrations) try { registration.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (marker is not null) Acquire(() => marker.DisposeAsync().AsTask());
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask());
            Acquire(() => registry.DisposeAsync().AsTask()); await Join();
        }
        if (errors.Count != 0) throw new AggregateException("Actual native open ownership originals failed.", errors);
        void Track(Task original) { lock (originals) originals.Add(original); }
        void Acquire(Func<Task> acquire) { try { Track(acquire()); } catch (Exception error) { errors.Add(error); } }
        async Task<Exception?> Observe(Task original)
        {
            if (observed.TryGetValue(original, out var prior)) return prior;
            Exception? captured = null; try { await original; } catch (Exception error) { captured = original.Exception ?? error; }
            observed.Add(original, captured); return captured;
        }
        async Task Join()
        {
            while (true)
            {
                Task[] pending; lock (originals) pending = new HashSet<Task>(originals.Where(original => !collected.Contains(original)), ReferenceEqualityComparer.Instance).ToArray();
                if (pending.Length == 0) return;
                foreach (var original in pending)
                {
                    var error = await Observe(original); if (error is not null && !acknowledged.Contains(original)) errors.Add(error);
                    collected.Add(original);
                }
            }
        }
        bool OnlyKnown(Exception root) => Graph(root).All(node => node is AggregateException || ReferenceEquals(node, left) || ReferenceEquals(node, right));
    }
    private static bool Contains(Exception root, Exception target) => Graph(root).Contains(target);
    private static HashSet<Exception> Graph(Exception root)
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
    private sealed class Extension(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
