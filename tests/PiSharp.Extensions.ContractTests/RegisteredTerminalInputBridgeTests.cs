using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class RegisteredTerminalInputBridgeTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("registered-terminal-input.hidden-marker-and-held-owner-close", HeldOwnerClose),
        ("registered-terminal-input.actual-native-self-and-unrelated-close", NativeReentry),
        ("registered-terminal-input.frame-free-real-parent-cleanup", FrameFreeCleanup)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Registered raw input control failed."); }

    private static async Task HeldOwnerClose()
    {
        var entered = Gate(); var release = Gate(); var disposed = Gate();
        RegisteredTerminalInputSession? input = null;
        var provider = new ExtensionTerminalInputUiProvider(new NoUi(),
            context => input!.SelectOwnerScope(context), context => input!.SelectRegistrationSink(context));
        var registry = new ExtensionRegistry(null, provider);
        input = new(registry, 2, 3);
        RegistrationScope? scope = null;
        var originals = new List<Task>(); var faults = new List<Exception>();
        try
        {
            scope = await registry.ActivateAsync("raw-owner", new Extension(entries => entries.RegisterCommand(
                new("register", "register-raw", "", (_, context, _) =>
                {
                    ((IExtensionTerminalInput)((IExtensionUiContext)context).Ui).OnTerminalInputAsync(async (data, fresh, _) =>
                    {
                        Check(fresh.OwnerId == "raw-owner" && fresh.OwnerGeneration == scope!.OwnerGeneration);
                        entered.TrySetResult(); await release.Task; return new(Data: data + "|raw");
                    });
                    return ValueTask.CompletedTask;
                })), () => { disposed.TrySetResult(); return ValueTask.CompletedTask; }));
            var register = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "register-raw", JsonData.EmptyObject).AsTask();
            originals.Add(register); await register;
            Check(registry.CaptureSnapshot().Registrations.Length == 1); // Actual raw marker is hidden.
            var actual = input.DispatchAsync("x", 2, 3); originals.Add(actual);
            await Task.WhenAny(entered.Task, actual); Check(entered.Task.IsCompleted);
            var closing = scope.DisposeAsync().AsTask(); originals.Add(closing);
            Check(!closing.IsCompleted && !disposed.Task.IsCompleted && !actual.IsCompleted);
            var late = input.DispatchAsync("late", 2, 3); originals.Add(late);
            Check((await late).Disposition == ExtensionTerminalInputDisposition.Forward && !disposed.Task.IsCompleted);
            release.TrySetResult(); await actual; await closing; Check(disposed.Task.IsCompleted);
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            release.TrySetResult();
            Acquire(() => input.DisposeAsync().AsTask(), originals, faults);
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask(), originals, faults);
            Acquire(() => registry.DisposeAsync().AsTask(), originals, faults);
            await Join(originals, faults);
        }
        if (faults.Count != 0) throw new AggregateException(faults);
    }

    private static async Task NativeReentry()
    {
        RegisteredTerminalInputSession? input = null; RegistrationScope? owner = null, unrelated = null;
        var provider = new ExtensionTerminalInputUiProvider(new NoUi(), context => input!.SelectOwnerScope(context),
            context => input!.SelectRegistrationSink(context));
        var registry = new ExtensionRegistry(null, provider); input = new(registry, 1, 1);
        var originals = new List<Task>(); var faults = new List<Exception>(); var refused = false;
        try
        {
            unrelated = await registry.ActivateAsync("unrelated", new Extension(_ => { }));
            owner = await registry.ActivateAsync("owner", new Extension(entries => entries.RegisterCommand(new("register", "raw", "",
                (_, context, _) =>
                {
                    ((IExtensionTerminalInput)((IExtensionUiContext)context).Ui).OnTerminalInputAsync(async (data, _, _) =>
                    {
                        try { await owner!.DisposeAsync(); }
                        catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { refused = true; }
                        var original = unrelated!.DisposeAsync().AsTask(); originals.Add(original); await original;
                        return new(Data: data + "|native");
                    });
                    return ValueTask.CompletedTask;
                }))));
            var register = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "raw", JsonData.EmptyObject).AsTask(); originals.Add(register); await register;
            var actual = input.DispatchAsync("x", 1, 1); originals.Add(actual);
            Check((await actual).Data == "x|native" && refused);
            Check(registry.CaptureSnapshot().Commands.Length == 1);
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            Acquire(() => input.DisposeAsync().AsTask(), originals, faults);
            if (owner is not null) Acquire(() => owner.DisposeAsync().AsTask(), originals, faults);
            if (unrelated is not null) Acquire(() => unrelated.DisposeAsync().AsTask(), originals, faults);
            Acquire(() => registry.DisposeAsync().AsTask(), originals, faults); await Join(originals, faults);
        }
        if (faults.Count != 0) throw new AggregateException(faults);
    }

    // Requires the accompanying two-line shared CallbackFrame owner patch. It is deliberately not silently skipped.
    private static async Task FrameFreeCleanup()
    {
        var ready = Gate(); var cleanupEntered = Gate(); var release = Gate(); RegisteredTerminalInputSession? input = null; RegistrationScope? owner = null;
        var provider = new ExtensionTerminalInputUiProvider(new NoUi(), context => input!.SelectOwnerScope(context),
            context => input!.SelectRegistrationSink(context));
        var registry = new ExtensionRegistry(null, provider); input = new(registry, 1, 1);
        var originals = new List<Task>(); var faults = new List<Exception>(); var refused = false;
        try
        {
            owner = await registry.ActivateAsync("owner", new Extension(entries => entries.RegisterCommand(new("register", "raw", "",
                (_, context, _) =>
                {
                    ((IExtensionTerminalInput)((IExtensionUiContext)context).Ui).OnTerminalInputAsync(async (_, _, token) =>
                    {
                        Task<CancellationTokenRegistration> worker;
                        using (ExecutionContext.SuppressFlow()) worker = Task.Run(() => token.Register(() =>
                        {
                            try { _ = owner!.DisposeAsync(); }
                            catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { refused = true; }
                            finally { cleanupEntered.TrySetResult(); }
                        }));
                        originals.Add(worker); var registration = await worker;
                        try { ready.TrySetResult(); await release.Task; return null; }
                        finally { registration.Dispose(); }
                    });
                    return ValueTask.CompletedTask;
                }))));
            var register = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "raw", JsonData.EmptyObject).AsTask(); originals.Add(register); await register;
            var dispatch = input.DispatchAsync("x", 1, 1); originals.Add(dispatch);
            await Task.WhenAny(ready.Task, dispatch); Check(ready.Task.IsCompleted);
            var close = owner.DisposeAsync().AsTask(); originals.Add(close);
            await cleanupEntered.Task;
            Check(refused && !close.IsCompleted); release.TrySetResult(); await dispatch; await close;
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            release.TrySetResult(); Acquire(() => input.DisposeAsync().AsTask(), originals, faults);
            if (owner is not null) Acquire(() => owner.DisposeAsync().AsTask(), originals, faults);
            Acquire(() => registry.DisposeAsync().AsTask(), originals, faults); await Join(originals, faults);
        }
        if (faults.Count != 0) throw new AggregateException(faults);
    }
    private static void Acquire(Func<Task> acquire, List<Task> originals, List<Exception> faults)
    { try { originals.Add(acquire()); } catch (Exception error) { faults.Add(error); } }
    private static async Task Join(List<Task> originals, List<Exception> faults)
    {
        var joined = new HashSet<Task>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < originals.Count; index++)
            if (joined.Add(originals[index]))
                try { await originals[index]; } catch (Exception error) { faults.Add(originals[index].Exception ?? error); }
    }
    private sealed class Extension(Action<IExtensionRegistry> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry entries, CancellationToken token) { initialize(entries); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
    private sealed class NoUi : IExtensionUiProvider
    {
        public IExtensionUiScope OpenScope(IExtensionContext context) => new Scope();
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
