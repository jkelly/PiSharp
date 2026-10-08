using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class TerminalInputAcquisitionFailureTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("terminal-acquisition.held-ui-error-and-original-cleanup-siblings", () => Run(new IOException("original UI acquisition"))),
        ("terminal-acquisition.faulted-oce-is-not-cancelled-and-retains-cleanup-siblings", () => Run(new OperationCanceledException("original faulted UI OCE")))
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Terminal acquisition control failed."); }
    private static async Task Run(Exception acquisitionError)
    {
        var cleanupOce = new OperationCanceledException("actual cleanup faulted OCE");
        var cleanupSibling = new IOException("actual cleanup sibling");
        var actions = new Actions(); var ui = new Ui(acquisitionError);
        RegisteredTerminalInputSession? input = null; RegistrationScope? owner = null;
        ExtensionTerminalInputHub.Scope? raw = null; var called = false;
        var registry = new ExtensionRegistry(null, new ExtensionTerminalInputUiProvider(ui,
            context => input!.SelectOwnerScope(context), context => input!.SelectRegistrationSink(context)), actions);
        input = new(registry, 1, 1);
        var originals = new List<Task> { actions.Cleanup.Task }; var errors = new List<Exception>(); var acknowledged = false;
        var expectedTasks = new HashSet<Task>(ReferenceEqualityComparer.Instance);
        var expectedNodes = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { acquisitionError, cleanupOce, cleanupSibling };
        void Acquire(Func<Task> acquire, bool expected = false)
        {
            try { var original = acquire(); originals.Add(original); if (expected) expectedTasks.Add(original); }
            catch (Exception error) { errors.Add(error); }
        }
        try
        {
            owner = await registry.ActivateAsync("acquisition", new Extension(entries => entries.RegisterCommand(new("register", "raw", "",
                (_, context, _) =>
                {
                    raw = input.SelectOwnerScope(context);
                    ((IExtensionTerminalInput)((IExtensionUiContext)context).Ui).OnTerminalInputAsync((_, _, _) =>
                    { called = true; return Task.FromResult<ExtensionTerminalInputResult?>(null); });
                    return ValueTask.CompletedTask;
                }))));
            var registration = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "raw", JsonData.EmptyObject).AsTask();
            originals.Add(registration); await registration;
            actions.Fail = true; ui.Fail = true;
            var dispatch = input.DispatchAsync("x", 1, 1); originals.Add(dispatch);
            await Task.WhenAny(actions.Entered.Task, dispatch); Check(actions.Entered.Task.IsCompleted && !dispatch.IsCompleted);
            Check(!called && !actions.Cleanup.Task.IsCompleted);
            actions.Cleanup.TrySetException([cleanupOce, cleanupSibling]);
            Check((await dispatch).Disposition == ExtensionTerminalInputDisposition.Forward);
            Check(actions.Cleanup.Task.IsFaulted && !actions.Cleanup.Task.IsCanceled && !called);
            Check(raw!.FaultedCallbacks.Length == 1);
            var callback = raw.FaultedCallbacks[0]; originals.Add(callback);
            Check(callback.IsFaulted && !callback.IsCanceled);
            var retained = callback.Exception!; var nodes = Nodes(retained);
            Check(nodes.Contains(acquisitionError) && nodes.Contains(cleanupOce) && nodes.Contains(cleanupSibling));
            Check(OnlyExpected(retained, expectedNodes));
            expectedTasks.Add(callback); expectedTasks.Add(actions.Cleanup.Task); acknowledged = true;
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            // Release the actual failed cleanup even when an earlier assertion fails; no held original leaks.
            if (actions.Fail) actions.Cleanup.TrySetException([cleanupOce, cleanupSibling]);
            else actions.Cleanup.TrySetResult();
            // This close joins the explicitly asserted same raw callback fault graph; unrelated closes
            // remain unexpected. Every non-Aggregate node must retain one of the exact original identities.
            Acquire(() => input.DisposeAsync().AsTask(), acknowledged);
            if (owner is not null) Acquire(() => owner.DisposeAsync().AsTask());
            Acquire(() => registry.DisposeAsync().AsTask());
            foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
                try { await original; }
                catch (Exception error)
                {
                    var full = original.Exception ?? error;
                    if (!expectedTasks.Contains(original) || !OnlyExpected(full, expectedNodes)) errors.Add(full);
                }
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private static HashSet<Exception> Nodes(Exception error)
    {
        var nodes = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var pending = new Stack<Exception>(); pending.Push(error);
        while (pending.TryPop(out var current))
        {
            if (!nodes.Add(current)) continue;
            if (nodes.Count > 1024) throw new InvalidOperationException("Control fault graph exceeded its bound.");
            if (current is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions) pending.Push(child);
            else if (current.InnerException is { } child) pending.Push(child);
        }
        return nodes;
    }
    private static bool OnlyExpected(Exception error, HashSet<Exception> expected) =>
        Nodes(error).All(node => node is AggregateException || expected.Contains(node));
    private sealed class Extension(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Actions : IExtensionSessionActionProvider
    {
        internal bool Fail;
        internal TaskCompletionSource Entered { get; } = Gate();
        internal TaskCompletionSource Cleanup { get; } = Gate();
        public ExtensionSessionSnapshot? Capture(IExtensionContext context) => new("actual-session", 1, null, []);
        public IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot) => new Scope(this, snapshot, Fail);
        private sealed class Scope(Actions owner, ExtensionSessionSnapshot snapshot, bool fail) : IExtensionSessionActionScope
        {
            public ExtensionSessionSnapshot Snapshot => snapshot;
            public CancellationToken SessionCancellationToken => CancellationToken.None;
            public ValueTask<ExtensionSessionEntryAcknowledgment> AppendAsync(string kind, int schema, JsonData data, CancellationToken token) => throw new InvalidOperationException("No action effect admitted.");
            public ValueTask<IExtensionSessionActionScope?> SwitchAsync(string path, bool latest, string? leaf, CancellationToken token) => throw new InvalidOperationException("No switch admitted.");
            public ValueTask DisposeAsync()
            {
                if (!fail) return ValueTask.CompletedTask;
                owner.Entered.TrySetResult(); return new(owner.Cleanup.Task); // The actual held, faulted Task with both siblings.
            }
        }
    }
    private sealed class Ui(Exception failure) : IExtensionUiProvider
    {
        internal bool Fail;
        public IExtensionUiScope OpenScope(IExtensionContext context)
        { if (Fail) throw failure; return new Scope(); }
        private sealed class Scope : IExtensionUiScope
        {
            public ExtensionUiCapabilities Capabilities => ExtensionUiCapabilities.NoUi;
            private static ValueTask<ExtensionUiOutcome<T>> No<T>() => ValueTask.FromResult(ExtensionUiOutcome<T>.Unavailable(ExtensionUiUnavailableReason.NoUi));
            public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken token = default) => No<string>();
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken token = default) => No<bool>();
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken token = default) => No<string>();
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken token = default) => No<string>();
            public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken token = default) => No<ExtensionUiPublication>();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
