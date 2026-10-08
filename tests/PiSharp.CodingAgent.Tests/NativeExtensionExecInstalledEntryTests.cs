using System.Collections.Immutable;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Extensions.Execution;
using PiSharp.Extensions.Facade.Execution;
using PiSharp.Extensions.Runtime;
using PiSharp.Tools.Processes;

internal static class NativeExtensionExecInstalledEntryTests
{
    // The qualifier supplies a genuinely published, separately approved minimal plugin.
    // No fixture creates executable authority, substituted loader, or synthetic activation.
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(NativeExtensionConfiguration configuration,
        NativeExtensionPreflight preflight, string workspace, string sessionPath,
        NativeExtensionInitializerInstallation initializer) =>
    [
        ("exec.actual-profile-initializer-composed-before-session-and-joined-close", () => Profile(configuration, workspace, sessionPath, initializer)),
        ("exec.actual-loader-foreign-factory-refused-before-runner", () => Foreign(preflight)),
        ("exec.actual-profile-multicast-factory-refused-before-effects", () => Multicast(configuration, workspace, sessionPath)),
        ("exec.actual-loader-unowned-sync-cancellation-fault-retained", () => FactoryCancellation(preflight, false)),
        ("exec.actual-loader-requested-caller-unmatched-callback-cancellation-fault-retained", () => FactoryCancellation(preflight, true))
    ];
    private static readonly NativeExtensionExecOriginals Originals = new();
    private static readonly List<NativeExtensionExecOriginal> Installed = [];
    internal static NativeExtensionExecOriginal[] RawCapturedOriginals()
    { lock (Installed) return Installed.Concat(Originals.Capture()).ToArray(); }
    private static void Require(bool value) { if (!value) throw new IOException("Actual exec entry assertion failed."); }
    private static async Task<T> Observe<T>(Task<T> original, string phase)
    {
        Exception? direct = null; T result = default!;
        try { result = await original; } catch (Exception error) { direct = error; }
        Originals.Record(phase, original, direct);
        if (direct is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(direct).Throw();
        return result;
    }
    private static async Task Join(Task original, string phase, List<Exception> failures)
    {
        Exception? direct = null; try { await original; } catch (Exception error) { direct = error; }
        if (Originals.Record(phase, original, direct) is { } fault) failures.Add(fault);
    }
    private static void Throw(List<Exception> failures) { if (failures.Count > 0) throw new AggregateException(failures); }
    private sealed class Runner : ISeparatedProcessRunner
    {
        internal int Calls;
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Only separated runs are admitted.");
        public ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken token = default)
        {
            Calls++; token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SeparatedProcessRunResult(new(ProcessRunStatus.NonZeroExit, 7, 17, true, true, true,
                new("", default!, 0, 0, null), new("merged", false), 0, []), new("stdout", false), new("stderr", false)));
        }
    }
    private static ProcessRequest Admit(NativeExtensionExecInvocation invocation) => new(
        Path.Combine(invocation.WorkingDirectory, "explicit-test-executable.exe"), invocation.Arguments, invocation.WorkingDirectory,
        ImmutableDictionary<string, string>.Empty, Path.Combine(invocation.WorkingDirectory, Guid.NewGuid().ToString("N") + ".spill"), invocation.TimeoutSeconds);
    private static async Task Profile(NativeExtensionConfiguration configuration, string workspace, string sessionPath,
        NativeExtensionInitializerInstallation initializer)
    {
        var failures = new List<Exception>(); var runner = new Runner(); OfflineSessionProfile? profile = null;
        NativeExtensionExecInstallation? installation = null; Task<ExtensionExecResult>? execution = null;
        IExtensionExecFacade? facade = null; var factories = 0; var binders = 0;
        try
        {
            profile = await Observe(OfflineSessionProfile.CreateAsync(workspace, sessionPath, null, [], [], [], default,
                extension: configuration, configuredInitializerInstallation: initializer,
                configuredExecInstallation: actualRegistry =>
                {
                    factories++;
                    return installation = new(actualRegistry, runner, (request, _) => ValueTask.FromResult(Admit(request)), workspace,
                        (_, capability) => { binders++; facade = capability; execution = capability.ExecAsync("literal", ["a b", "&literal"]); });
                }), "actual-profile-create-with-exec");
            Require(factories == 1 && binders == 1 && installation is not null && facade is not null && execution is not null);
            var result = await Observe(execution!, "actual-binder-exec-result");
            Require(result == new ExtensionExecResult("stdout", "stderr", 7, false) && runner.Calls == 1);
            Require(installation!.Originals.Capture().Count(row => row.Phase == "actual-plugin-initialization") == 1);
            await Join(profile.DisposeAsync().AsTask(), "actual-profile-close-with-exec", failures);
            var refused = false; try { _ = facade!.ExecAsync("late", []); } catch (ObjectDisposedException) { refused = true; }
            Require(refused);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (profile is not null) await Join(profile.DisposeAsync().AsTask(), "actual-profile-final-close-with-exec", failures);
            if (execution is not null) await Join(execution, "actual-binder-exec-final-join", failures);
            if (installation is not null) lock (Installed) Installed.AddRange(installation.Originals.Capture());
        }
        Throw(failures);
    }
    private static async Task Foreign(NativeExtensionPreflight preflight)
    {
        var foreign = new ExtensionRegistry(); var runner = new Runner(); var failures = new List<Exception>();
        NativeExtensionActivation? activation = null; var factories = 0;
        try
        {
            Exception? refused = null;
            try
            {
                activation = await Observe(NativeExtensionActivation.LoadAsync(preflight, default, configuredExecInstallation: _ =>
                { factories++; return new(foreign, runner, (request, _) => ValueTask.FromResult(Admit(request)), Path.GetTempPath(), (_, _) => { }); }),
                    "actual-loader-foreign-exec-factory");
            }
            catch (NativeExtensionException error) { refused = error; }
            Require(refused is NativeExtensionException { Failure: NativeExtensionFailure.ActivationFailed,
                InnerException: InvalidOperationException { Message: "Execution installation returned a foreign registry." } } &&
                factories == 1 && runner.Calls == 0 && foreign.CaptureSnapshot().Tools.IsEmpty);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (activation is not null) await Join(activation.DisposeAsync().AsTask(), "foreign-unexpected-activation-close", failures);
            await Join(foreign.DisposeAsync().AsTask(), "foreign-registry-close", failures);
        }
        Throw(failures);
    }
    private static async Task Multicast(NativeExtensionConfiguration configuration, string workspace, string sessionPath)
    {
        var effects = 0; var failures = new List<Exception>(); OfflineSessionProfile? profile = null;
        Func<ExtensionRegistry, NativeExtensionExecInstallation> factory = _ => { effects++; throw new IOException("Must not invoke multicast."); };
        try
        {
            var refused = false;
            try { profile = await Observe(OfflineSessionProfile.CreateAsync(workspace, sessionPath, null, [], [], [], default,
                extension: configuration, configuredExecInstallation: factory + factory), "actual-profile-multicast-exec-refusal"); }
            catch (ArgumentException) { refused = true; }
            Require(refused && effects == 0);
        }
        catch (Exception error) { failures.Add(error); }
        finally { if (profile is not null) await Join(profile.DisposeAsync().AsTask(), "multicast-unexpected-profile-close", failures); }
        Throw(failures);
    }
    private static async Task FactoryCancellation(NativeExtensionPreflight preflight, bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var injected = new OperationCanceledException("Unmatched clear-token supplied factory failure.", CancellationToken.None);
        var original = NativeExtensionActivation.LoadAsync(preflight, caller.Token, configuredExecInstallation: _ =>
        { if (cancelCaller) caller.Cancel(); throw injected; });
        NativeExtensionActivation? activation = null; var failures = new List<Exception>();
        try
        {
            Exception? direct = null;
            try { activation = await original; } catch (Exception error) { direct = error; }
            Originals.Record("actual-loader-unowned-factory-cancellation", original, direct);
            Require(original.IsFaulted && direct is NativeExtensionException { Failure: NativeExtensionFailure.ActivationFailed } native &&
                native.InnerException is NativeExtensionExecFault { Original: null } callback &&
                ReferenceEquals(callback.Evidence, injected) && ReferenceEquals(callback.InnerException, injected) &&
                caller.IsCancellationRequested == cancelCaller && injected.CancellationToken == CancellationToken.None);
        }
        catch (Exception error) { failures.Add(error); }
        finally { if (activation is not null) await Join(activation.DisposeAsync().AsTask(), "cancellation-unexpected-activation-close", failures); }
        Throw(failures);
    }
}
