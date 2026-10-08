using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

internal static class RegisteredCustomComponentTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("custom-registry.held-original-and-dispose-before-plugin-unload", HeldClose),
        ("custom-registry.self-native-ancestor-and-unrelated-component-close", Reentry),
        ("custom-registry.session-cancel-frame-free-register-held-original", SessionCancellation)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Actual custom registry control failed."); }
    private static async Task HeldClose()
    {
        var registry = new ExtensionRegistry(); RegistrationScope? scope = null;
        ExtensionRegistry.RegisteredExtensionComponent? component = null;
        var entered = Gate(); var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeEntered = Gate(); var disposeOriginal = Gate(); var signalEntered = Gate(); var unloaded = false; var calls = 0;
        var originals = new List<Task> { source.Task, disposeOriginal.Task }; var errors = new List<Exception>();
        try
        {
            var activation = registry.ActivateAsync("custom-owner", new Extension(entries => entries.RegisterCommand(new("bind", "bind", "",
                (_, context, _) =>
                {
                    component = ExtensionRegistry.BindCustomComponentContext(context, "component-1", 1, () => 1,
                        () => { signalEntered.TrySetResult(); return Task.CompletedTask; },
                        _ => { disposeEntered.TrySetResult(); return disposeOriginal.Task; });
                    return ValueTask.CompletedTask;
                })), () => { unloaded = true; return ValueTask.CompletedTask; })); originals.Add(activation); scope = await activation;
            var bind = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "bind", JsonData.EmptyObject).AsTask(); originals.Add(bind); await bind;
            Check(registry.CaptureSnapshot().Registrations.Length == 1);
            var actual = component!.InvokeAsync((context, token) =>
            { Check(context.OwnerGeneration == scope.OwnerGeneration); calls++; entered.TrySetResult(); return source.Task; });
            originals.Add(actual); await Task.WhenAny(entered.Task, actual); Check(entered.Task.IsCompleted);
            var close = scope.DisposeAsync().AsTask(); originals.Add(close);
            await Task.WhenAny(signalEntered.Task, close); Check(signalEntered.Task.IsCompleted && !close.IsCompleted && !unloaded);
            var refused = false;
            try { _ = component.InvokeAsync((_, _) => { calls++; return Task.FromResult(0); }); }
            catch (ExtensionRegistrationException) { refused = true; }
            Check(refused && calls == 1);
            source.TrySetResult(7); Check(await actual == 7);
            await Task.WhenAny(disposeEntered.Task, close); Check(disposeEntered.Task.IsCompleted && !close.IsCompleted && !unloaded);
            disposeOriginal.TrySetResult(); await close; Check(unloaded);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            source.TrySetResult(7); disposeOriginal.TrySetResult();
            if (component is not null) Acquire(() => component.DisposeAsync().AsTask(), originals, errors);
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask(), originals, errors);
            Acquire(() => registry.DisposeAsync().AsTask(), originals, errors); await Join(originals, errors);
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private static async Task Reentry()
    {
        var registry = new ExtensionRegistry(); RegistrationScope? scope = null;
        ExtensionRegistry.RegisteredExtensionComponent? first = null, other = null;
        var originals = new List<Task>(); var errors = new List<Exception>(); bool selfRefused = false, nativeRefused = false, ancestorRefused = false;
        try
        {
            var activation = registry.ActivateAsync("custom-owner", new Extension(entries => entries.RegisterCommand(new("bind", "bind", "",
                (_, context, _) =>
                {
                    first = ExtensionRegistry.BindCustomComponentContext(context, "component-1", 1, () => 1,
                        () => Task.CompletedTask, _ => Task.CompletedTask);
                    other = ExtensionRegistry.BindCustomComponentContext(context, "component-2", 1, () => 1,
                        () => Task.CompletedTask, async _ =>
                        {
                            try { await first!.DisposeAsync(); }
                            catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { ancestorRefused = true; }
                        });
                    return ValueTask.CompletedTask;
                })))); originals.Add(activation); scope = await activation;
            var bind = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "bind", JsonData.EmptyObject).AsTask(); originals.Add(bind); await bind;
            var actual = first!.InvokeAsync(async (_, _) =>
            {
                try { await first.DisposeAsync(); }
                catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { selfRefused = true; }
                try { await scope.DisposeAsync(); }
                catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { nativeRefused = true; }
                // Distinct component close is allowed. Its real disposal callback cannot close the
                // active original ancestor that is joining it; actual inherited active frames fence that cycle.
                var close = other!.DisposeAsync().AsTask(); originals.Add(close); await close; return true;
            });
            originals.Add(actual); Check(await actual && selfRefused && nativeRefused && ancestorRefused);
            Check(registry.CaptureSnapshot().Commands.Length == 1);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            if (first is not null) Acquire(() => first.DisposeAsync().AsTask(), originals, errors);
            if (other is not null) Acquire(() => other.DisposeAsync().AsTask(), originals, errors);
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask(), originals, errors);
            Acquire(() => registry.DisposeAsync().AsTask(), originals, errors); await Join(originals, errors);
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private static void Acquire(Func<Task> acquire, List<Task> originals, List<Exception> errors)
    { try { originals.Add(acquire()); } catch (Exception error) { errors.Add(error); } }
    private static async Task Join(List<Task> originals, List<Exception> errors)
    {
        foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
            try { await original; } catch (Exception error) { errors.Add(original.Exception ?? error); }
    }

    private static async Task SessionCancellation()
    {
        var registry = new ExtensionRegistry(); RegistrationScope? scope = null;
        ExtensionRegistry.RegisteredExtensionComponent? component = null;
        using var session = new CancellationTokenSource();
        var ready = Gate(); var registeredCallback = Gate(); var release = Gate();
        var originals = new List<Task> { release.Task }; var errors = new List<Exception>();
        bool componentRefused = false, scopeRefused = false;
        try
        {
            var activation = registry.ActivateAsync("custom-owner", new Extension(entries => entries.RegisterCommand(new("bind", "bind", "",
                (_, context, _) =>
                {
                    component = ExtensionRegistry.BindCustomComponentContext(context, "component-1", 1, () => 1,
                        () => Task.CompletedTask, _ => Task.CompletedTask, session.Token);
                    return ValueTask.CompletedTask;
                })))); originals.Add(activation); scope = await activation;
            var bind = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "bind", JsonData.EmptyObject).AsTask(); originals.Add(bind); await bind;
            var actual = component!.InvokeAsync(async (freshContext, token) =>
            {
                Task<CancellationTokenRegistration> registration;
                using (ExecutionContext.SuppressFlow()) registration = Task.Run(() => token.Register(() =>
                {
                    try { _ = component.DisposeAsync(); }
                    catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { componentRefused = true; }
                    try { _ = scope.DisposeAsync(); }
                    catch (ExtensionRegistrationException error) when (error.Failure == ExtensionRegistrationFailure.ReentrantDisposal) { scopeRefused = true; }
                    registeredCallback.TrySetResult();
                }));
                originals.Add(registration);
                using var acquired = await registration;
                ready.TrySetResult(); await release.Task; return true;
            });
            originals.Add(actual); await Task.WhenAny(ready.Task, actual); Check(ready.Task.IsCompleted);
            Task cancellation;
            using (ExecutionContext.SuppressFlow()) cancellation = Task.Run(() => session.Cancel());
            originals.Add(cancellation);
            await Task.WhenAny(registeredCallback.Task, cancellation);
            Check(registeredCallback.Task.IsCompleted && componentRefused && scopeRefused && !actual.IsCompleted && !cancellation.IsCompleted);
            bool denied = false;
            try { _ = component.InvokeAsync((_, _) => Task.FromResult(true)); }
            catch (OperationCanceledException) { denied = true; }
            Check(denied);
            release.TrySetResult(); await actual; await cancellation;
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            release.TrySetResult();
            if (component is not null) Acquire(() => component.DisposeAsync().AsTask(), originals, errors);
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask(), originals, errors);
            Acquire(() => registry.DisposeAsync().AsTask(), originals, errors); await Join(originals, errors);
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private sealed class Extension(Action<IExtensionRegistry> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
}
