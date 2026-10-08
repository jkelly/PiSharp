using System.Collections.Immutable;
using PiSharp.Cli.Extensions.Execution;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Execution;
using PiSharp.Extensions.Runtime;
using PiSharp.Tools.Processes;

internal static class NativeExtensionExecHostTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("exec.native-initializer-original-and-literal-argv-result", InitializerResult),
        ("exec.foreign-registry-and-multicast-before-effects", ForeignAndMulticast),
        ("exec.ignored-held-process-retirement-joins-and-late-refusal", HeldRetirement),
        ("exec.synchronous-unowned-cancellation-is-fault", SynchronousCancellation),
        ("exec.full-fault-siblings-and-alias-capture-once", FaultSiblings),
        ("exec.supplier-reentry-and-self-close-refused", Reentry),
        ("exec.pre-canceled-signal-no-admission-and-no-process", PreCanceled),
        ("exec.incomplete-capture-never-success", Incomplete),
        ("exec.frame-free-owner-cancellation-close-refused-and-joined", () => CancellationBoundary(false)),
        ("exec.frame-free-signal-cancellation-close-refused-and-joined", () => CancellationBoundary(true)),
        ("exec.supplier-a-b-a-ancestry-refused", Ancestors),
        ("exec.frame-free-noncanceled-admission-reentry-refused", () => PhysicalSupplier(false)),
        ("exec.frame-free-noncanceled-runner-reentry-refused", () => PhysicalSupplier(true)),
        ("exec.distinct-logical-a-physical-c-b-frame-free-ancestry-refused", DualParents)
    ];
    private static readonly NativeExtensionExecOriginals ControlOriginals = new();
    private static readonly List<NativeExtensionExecOriginal> InstalledOriginals = [];
    internal static NativeExtensionExecOriginal[] RawCapturedOriginals()
    { lock (InstalledOriginals) return InstalledOriginals.Concat(ControlOriginals.Capture()).ToArray(); }
    private static void Capture(NativeExtensionExecInstallation installation)
    { lock (InstalledOriginals) InstalledOriginals.AddRange(installation.Originals.Capture()); }
    private static string Root => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "authored-exec-controls"));
    private static void Check(bool value) { if (!value) throw new IOException("Exec control failed."); }
    private static bool Contains(Exception value, Exception expected) => ReferenceEquals(value, expected) ||
        (value is AggregateException aggregate ? aggregate.InnerExceptions.Any(child => Contains(child, expected)) :
            value.InnerException is { } inner && Contains(inner, expected));
    private static ProcessRequest Request(NativeExtensionExecInvocation invocation) => new(
        Path.Combine(Root, "caller-admitted.exe"), invocation.Arguments, invocation.WorkingDirectory,
        ImmutableDictionary<string, string>.Empty, Path.Combine(Root, Guid.NewGuid().ToString("N") + ".spill"), invocation.TimeoutSeconds);
    private static SeparatedProcessRunResult Result(ProcessRunStatus status = ProcessRunStatus.NonZeroExit,
        bool complete = true, bool truncated = false) => new(new(status, 7, 17, true, true, complete,
            new("", default!, 0, 0, null), new("merged", false), 0, []), new("stdout", truncated), new("stderr", false));
    private static async Task Join(Task original, List<Exception> failures)
    {
        Exception? direct = null; try { await original; } catch (Exception error) { direct = error; }
        if (ControlOriginals.Record("control-original-join", original, direct) is { } retainedFault) failures.Add(retainedFault);
    }
    private static bool Allowed(Exception error, Exception[] expected)
    {
        if (expected.Any(value => ReferenceEquals(value, error))) return true;
        if (error is AggregateException aggregate) return aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(child => Allowed(child, expected));
        return error is NativeExtensionExecFault or ExtensionRegistrationException &&
            error.InnerException is { } inner && Allowed(inner, expected);
    }
    private static async Task ExpectedJoin(Task original, NativeExtensionExecOriginals originals, string phase,
        List<Exception> unexpected, Exception[] expected)
    {
        Exception? direct = null; try { await original; } catch (Exception error) { direct = error; }
        var retained = originals.Record(phase, original, direct);
        if (retained is not null && !Allowed(retained, expected)) unexpected.Add(retained);
    }
    private static Exception[] CanceledProcesses(NativeExtensionExecInstallation installation) => installation.Originals.Capture()
        .Where(row => row.Phase == "separated-process" && row.Original.IsCanceled && row.Direct is OperationCanceledException)
        .Select(row => row.Direct!).ToArray();
    private static async Task<RegistrationScope> Activate(ExtensionRegistry registry, string id, IPiSharpExtension plugin)
    {
        var original = registry.ActivateAsync(id, plugin); RegistrationScope? scope = null; Exception? direct = null;
        try { scope = await original; } catch (Exception error) { direct = error; }
        ControlOriginals.Record("control-actual-registry-activation", original, direct);
        if (direct is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(direct).Throw();
        return scope!;
    }
    private static void Throw(List<Exception> failures) { if (failures.Count > 0) throw new AggregateException(failures); }
    private static async Task InitializerResult()
    {
        var registry = new ExtensionRegistry(); var failures = new List<Exception>(); var runner = new Runner();
        IExtensionExecFacade? facade = null; ExtensionExecResult? result = null;
        var plugin = new Plugin(async () => result = await facade!.ExecAsync("admitted", ["a b", "&literal"], new(TimeoutMilliseconds: 250)));
        var install = new NativeExtensionExecInstallation(registry, runner, (request, _) => ValueTask.FromResult(Request(request)), Root, (_, value) => facade = value);
        try
        {
            await Activate(registry, "exec-owner", install.Decorate(registry, plugin));
            Check(result == new ExtensionExecResult("stdout", "stderr", 7, false) && plugin.Initializations == 1 && runner.Calls == 1);
            Check(runner.Last!.Arguments.SequenceEqual(["a b", "&literal"]) && runner.Last.TimeoutSeconds == .25);
        }
        catch (Exception error) { failures.Add(error); }
        finally { await Join(registry.DisposeAsync().AsTask(), failures); Capture(install); }
        Check(plugin.Disposals == 1 && install.Originals.Capture().Any(row => row.Phase == "actual-plugin-initialization")); Throw(failures);
    }
    private static async Task ForeignAndMulticast()
    {
        var registry = new ExtensionRegistry(); var foreign = new ExtensionRegistry(); var runner = new Runner(); var failures = new List<Exception>();
        Action<IExtensionRegistry, IExtensionExecFacade> bind = (_, _) => { };
        try
        {
            var install = new NativeExtensionExecInstallation(registry, runner, (request, _) => ValueTask.FromResult(Request(request)), Root, bind);
            var refused = false; try { _ = install.Decorate(foreign, new Plugin(() => Task.CompletedTask)); } catch (InvalidOperationException) { refused = true; }
            Check(refused && runner.Calls == 0);
            refused = false; try { _ = new NativeExtensionExecInstallation(registry, runner, (request, _) => ValueTask.FromResult(Request(request)), Root, bind + bind); }
            catch (ArgumentException) { refused = true; } Check(refused);
            Func<NativeExtensionExecInvocation, CancellationToken, ValueTask<ProcessRequest>> admission = (request, _) => ValueTask.FromResult(Request(request));
            refused = false; try { _ = new NativeExtensionExecInstallation(registry, runner, admission + admission, Root, bind); }
            catch (ArgumentException) { refused = true; } Check(refused);
        }
        catch (Exception error) { failures.Add(error); }
        finally { await Join(registry.DisposeAsync().AsTask(), failures); await Join(foreign.DisposeAsync().AsTask(), failures); }
        Throw(failures);
    }
    private static async Task HeldRetirement()
    {
        var registry = new ExtensionRegistry(); var failures = new List<Exception>(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IExtensionExecFacade? facade = null; Task<ExtensionExecResult>? ignored = null;
        var runner = new Runner(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Result(); });
        var install = new NativeExtensionExecInstallation(registry, runner, (request, _) => ValueTask.FromResult(Request(request)), Root, (_, value) => facade = value);
        var plugin = new Plugin(() => { ignored = facade!.ExecAsync("held", []); return Task.CompletedTask; });
        try
        {
            await Activate(registry, "held", install.Decorate(registry, plugin));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close = registry.DisposeAsync().AsTask(); Exception? closeFault = null;
            try { await close; } catch (Exception error) { closeFault = error; }
            await ExpectedJoin(close, ControlOriginals, "held-actual-registry-close", failures, CanceledProcesses(install));
            Check(closeFault is not null && ignored!.IsCompleted && plugin.Disposals == 1);
            var refused = false; try { _ = facade!.ExecAsync("late", []); } catch (ObjectDisposedException) { refused = true; } Check(refused);
            Check(install.Originals.Capture().Any(row => row.Phase == "separated-process" && row.Original.IsCanceled));
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            await ExpectedJoin(registry.DisposeAsync().AsTask(), ControlOriginals, "held-final-registry-close", failures, CanceledProcesses(install));
            if (ignored is not null)
                await ExpectedJoin(ignored, install.Originals, "held-ignored-mapping-join", failures, CanceledProcesses(install));
            Capture(install);
        }
        Throw(failures);
    }
    private static async Task SynchronousCancellation()
    {
        var injected = new OperationCanceledException(new CancellationToken(true));
        await FailedAdmission((_, _) => throw injected, injected);
    }
    private static async Task FaultSiblings()
    {
        var left = new IOException("left"); var right = new IOException("right"); var original = new TaskCompletionSource<ProcessRequest>();
        original.SetException([left, right]);
        await FailedAdmission((_, _) => new(original.Task), left, right);
    }
    private static async Task FailedAdmission(Func<NativeExtensionExecInvocation, CancellationToken, ValueTask<ProcessRequest>> admission, params Exception[] expected)
    {
        var registry = new ExtensionRegistry(); IExtensionExecFacade? facade = null; var runner = new Runner(); Exception? observed = null;
        var install = new NativeExtensionExecInstallation(registry, runner, admission, Root, (_, value) => facade = value);
        var plugin = new Plugin(async () => { await facade!.ExecAsync("fault", []); });
        try { await Activate(registry, "fault", install.Decorate(registry, plugin)); }
        catch (Exception error) { observed = error; }
        finally
        {
            var unexpected = new List<Exception>();
            await ExpectedJoin(registry.DisposeAsync().AsTask(), ControlOriginals, "fault-final-registry-close", unexpected, expected);
            if (unexpected.Count > 0) observed = new AggregateException(unexpected.Prepend(observed ?? new IOException("Missing admission fault.")));
            Capture(install);
        }
        Check(observed is not null && expected.All(value => Contains(observed, value)) && runner.Calls == 0 && plugin.Disposals == 1);
        Check(Allowed(observed!, expected));
        foreach (var aliases in install.Originals.Capture().GroupBy(row => row.Original, ReferenceEqualityComparer.Instance))
        { var first = aliases.First(); Check(aliases.All(row => ReferenceEquals(row.Aggregate, first.Aggregate) && ReferenceEquals(row.Direct, first.Direct))); }
    }
    private static async Task Reentry()
    {
        var registry = new ExtensionRegistry(); var failures = new List<Exception>(); IExtensionExecFacade? facade = null; var runner = new Runner();
        var refused = 0;
        var install = new NativeExtensionExecInstallation(registry, runner, (request, admittedToken) =>
        {
            try { _ = facade!.ExecAsync("nested", []); } catch (InvalidOperationException) { refused++; }
            try { _ = ((IAsyncDisposable)facade!).DisposeAsync(); } catch (InvalidOperationException) { refused++; }
            return ValueTask.FromResult(Request(request));
        }, Root, (_, value) => facade = value);
        try { await Activate(registry, "reentry", install.Decorate(registry, new Plugin(async () => { await facade!.ExecAsync("outer", []); }))); Check(refused == 2 && runner.Calls == 1); }
        catch (Exception error) { failures.Add(error); }
        finally { await Join(registry.DisposeAsync().AsTask(), failures); Capture(install); } Throw(failures);
    }
    private static async Task PreCanceled()
    {
        var registry = new ExtensionRegistry(); var failures = new List<Exception>(); IExtensionExecFacade? facade = null; var runner = new Runner(); var admissions = 0;
        var install = new NativeExtensionExecInstallation(registry, runner, (request, _) => { admissions++; return ValueTask.FromResult(Request(request)); }, Root, (_, value) => facade = value);
        var plugin = new Plugin(async () => { try { await facade!.ExecAsync("canceled", [], new(Signal: new CancellationToken(true))); } catch (OperationCanceledException) { } });
        try { await Activate(registry, "cancel", install.Decorate(registry, plugin)); Check(admissions == 0 && runner.Calls == 0); }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            var close = registry.DisposeAsync().AsTask(); Exception? direct = null;
            try { await close; } catch (Exception error) { direct = error; }
            var expected = install.Originals.Capture().Where(row => row.Phase == "exec-mapping-join" && row.Original.IsCanceled && row.Direct is OperationCanceledException).Select(row => row.Direct!).ToArray();
            if (ControlOriginals.Record("pre-canceled-registry-close", close, direct) is { } retainedFault && !Allowed(retainedFault, expected)) failures.Add(retainedFault);
            Capture(install);
        }
        Throw(failures);
    }
    private static async Task Incomplete()
    {
        var registry = new ExtensionRegistry(); IExtensionExecFacade? facade = null; var runner = new Runner((_, _) => Task.FromResult(Result(truncated: true))); Exception? observed = null;
        var install = new NativeExtensionExecInstallation(registry, runner, (request, _) => ValueTask.FromResult(Request(request)), Root, (_, value) => facade = value);
        try { await Activate(registry, "partial", install.Decorate(registry, new Plugin(async () => { await facade!.ExecAsync("partial", []); }))); }
        catch (Exception error) { observed = error; }
        finally
        {
            var expected = install.Originals.Capture().Where(row => row.Phase == "exec-mapping-join" && row.Direct is IOException { Message: "Exec output or process cleanup is not complete; no successful result is admitted." }).Select(row => row.Direct!).ToArray();
            var failures = new List<Exception>();
            await ExpectedJoin(registry.DisposeAsync().AsTask(), ControlOriginals, "incomplete-registry-close", failures, expected);
            Capture(install); Throw(failures);
        }
        Check(observed is not null && runner.Calls == 1);
    }
    private static async Task CancellationBoundary(bool signalRoute)
    {
        var unrelated = ExecutionContext.Capture() ?? throw new IOException("Unrelated control execution context required.");
        var registry = new ExtensionRegistry(); using var signal = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var marker = new IOException("source-declared cancellation callback marker");
        var failures = new List<Exception>(); Exception? refusal = null;
        IExtensionExecFacade? facade = null; Task<ExtensionExecResult>? ignored = null; Task? cancel = null; Task? close = null;
        var runner = new Runner(async (request, token) =>
        {
            CancellationTokenRegistration registration = default;
            ExecutionContext.Run(unrelated.CreateCopy(), callbackState => registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (!release.Task.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Cancellation callback control gate expired.");
                try { ((IAsyncDisposable)facade!).DisposeAsync().GetAwaiter().GetResult(); }
                catch (Exception error) { refusal = error; marker = new IOException("source-declared cancellation callback marker", error); throw marker; }
                throw new IOException("Frame-free cancellation self-close was not refused.");
            }), null);
            using (registration)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Result();
        });
        var install = new NativeExtensionExecInstallation(registry, runner, (request, _) => ValueTask.FromResult(Request(request)), Root, (_, value) => facade = value);
        try
        {
            await Activate(registry, "cancellation-boundary", install.Decorate(registry, new Plugin(() =>
            { ignored = facade!.ExecAsync("held", [], new(Signal: signal.Token)); return Task.CompletedTask; })));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (signalRoute) cancel = signal.CancelAsync(); else close = registry.DisposeAsync().AsTask();
            var trigger = cancel ?? close!;
            var winner = await Task.WhenAny(callbackEntered.Task, trigger, Task.Delay(TimeSpan.FromSeconds(5)));
            Check(ReferenceEquals(winner, callbackEntered.Task));
            var externalRefused = false;
            try { _ = ((IAsyncDisposable)facade!).DisposeAsync(); } catch (InvalidOperationException) { externalRefused = true; }
            Check(externalRefused); // conservative external refusal is the explicitly documented boundary
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            release.TrySetResult();
            if (cancel is not null) await ExpectedJoin(cancel, ControlOriginals, "control-external-signal-cancellation", failures, [marker]);
            close ??= registry.DisposeAsync().AsTask();
            Exception? direct = null; try { await close; } catch (Exception error) { direct = error; }
            var expected = CanceledProcesses(install).Append(marker).ToArray();
            if (ControlOriginals.Record("control-cancellation-owner-close", close, direct) is { } retainedFault && !Allowed(retainedFault, expected)) failures.Add(retainedFault);
            if (ignored is not null) await ExpectedJoin(ignored, install.Originals, "control-canceled-ignored-mapping", failures, expected);
            if (facade is not null)
            {
                var first = ((IAsyncDisposable)facade).DisposeAsync().AsTask();
                var second = ((IAsyncDisposable)facade).DisposeAsync().AsTask();
                Check(ReferenceEquals(first, second)); // stable original after the rejected interval
                await ExpectedJoin(first, install.Originals, "control-post-boundary-close", failures, expected);
            }
            await ExpectedJoin(registry.DisposeAsync().AsTask(), ControlOriginals, "control-final-cancellation-owner-close", failures, expected);
            Capture(install);
        }
        Check(refusal is InvalidOperationException && ignored is { IsCompleted: true }); Throw(failures);
    }
    private static async Task Ancestors()
    {
        var left = new ExtensionRegistry(); var right = new ExtensionRegistry(); var failures = new List<Exception>();
        IExtensionExecFacade? a = null; IExtensionExecFacade? b = null; var refused = 0; var leftRunner = new Runner(); var rightRunner = new Runner();
        var rightInstall = new NativeExtensionExecInstallation(right, rightRunner, (request, admittedToken) =>
        {
            try { _ = a!.ExecAsync("ancestor", []); } catch (InvalidOperationException) { refused++; }
            try { _ = ((IAsyncDisposable)a!).DisposeAsync(); } catch (InvalidOperationException) { refused++; }
            return ValueTask.FromResult(Request(request));
        }, Root, (_, value) => b = value);
        var leftInstall = new NativeExtensionExecInstallation(left, leftRunner, async (request, admittedToken) =>
        { await b!.ExecAsync("child", [], new(Signal: admittedToken)); return Request(request); }, Root, (_, value) => a = value);
        Task<ExtensionExecResult>? operation = null;
        try
        {
            await Activate(left, "ancestor-a", leftInstall.Decorate(left, new Plugin(() => Task.CompletedTask)));
            await Activate(right, "ancestor-b", rightInstall.Decorate(right, new Plugin(() => Task.CompletedTask)));
            operation = a!.ExecAsync("outer", []); await operation;
            leftInstall.Originals.Record("control-ancestor-mapping", operation, null);
            Check(refused == 2 && leftRunner.Calls == 1 && rightRunner.Calls == 1);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            await Join(left.DisposeAsync().AsTask(), failures); await Join(right.DisposeAsync().AsTask(), failures);
            if (operation is not null) await ExpectedJoin(operation, leftInstall.Originals, "control-final-ancestor-mapping", failures, []);
            Capture(leftInstall); Capture(rightInstall);
        }
        Throw(failures);
    }
    private static async Task PhysicalSupplier(bool runnerRoute)
    {
        var unrelated = ExecutionContext.Capture() ?? throw new IOException("Unrelated control execution context required.");
        var registry = new ExtensionRegistry(); var failures = new List<Exception>(); IExtensionExecFacade? facade = null; var refused = 0;
        void Probe()
        {
            ExecutionContext.Run(unrelated.CreateCopy(), callbackState =>
            {
                try { _ = facade!.ExecAsync("frame-free-nested", []); } catch (InvalidOperationException) { refused++; }
                try { _ = ((IAsyncDisposable)facade!).DisposeAsync(); } catch (InvalidOperationException) { refused++; }
            }, null);
        }
        var runner = new Runner((request, token) => { if (runnerRoute) Probe(); return Task.FromResult(Result()); });
        var install = new NativeExtensionExecInstallation(registry, runner, (request, token) =>
        { if (!runnerRoute) Probe(); return ValueTask.FromResult(Request(request)); }, Root, (_, value) => facade = value);
        try
        {
            await Activate(registry, "physical-supplier", install.Decorate(registry, new Plugin(async () =>
            { var result = await facade!.ExecAsync("outer", []); Check(result.Code == 7); })));
            Check(refused == 2 && runner.Calls == 1);
        }
        catch (Exception error) { failures.Add(error); }
        finally { await Join(registry.DisposeAsync().AsTask(), failures); Capture(install); }
        Throw(failures);
    }
    private static async Task DualParents()
    {
        var unrelated = ExecutionContext.Capture() ?? throw new IOException("Unrelated control execution context required.");
        var aRegistry = new ExtensionRegistry(); var bRegistry = new ExtensionRegistry(); var cRegistry = new ExtensionRegistry();
        var failures = new List<Exception>(); var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captured = new TaskCompletionSource<ExecutionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        IExtensionExecFacade? a = null; IExtensionExecFacade? b = null; IExtensionExecFacade? c = null; var refused = 0;
        Task<ExtensionExecResult>? aOperation = null; Task<ExtensionExecResult>? bOperation = null; Task<ExtensionExecResult>? cOperation = null;
        var aRunner = new Runner(); var bRunner = new Runner(); var cRunner = new Runner();
        var aInstall = new NativeExtensionExecInstallation(aRegistry, aRunner, async (request, token) =>
        {
            captured.TrySetResult(ExecutionContext.Capture() ?? throw new IOException("Logical A context required."));
            await released.Task; return Request(request);
        }, Root, (_, value) => a = value);
        var bInstall = new NativeExtensionExecInstallation(bRegistry, bRunner, (request, token) =>
        {
            ExecutionContext.Run(unrelated.CreateCopy(), state =>
            {
                try { _ = a!.ExecAsync("lost-a", []); } catch (InvalidOperationException) { refused++; }
                try { _ = ((IAsyncDisposable)a!).DisposeAsync(); } catch (InvalidOperationException) { refused++; }
            }, null);
            return ValueTask.FromResult(Request(request));
        }, Root, (_, value) => b = value);
        var cInstall = new NativeExtensionExecInstallation(cRegistry, cRunner, (request, token) =>
        {
            ExecutionContext.Run(captured.Task.GetAwaiter().GetResult().CreateCopy(), state => bOperation = b!.ExecAsync("b-under-c-and-a", []), null);
            return ValueTask.FromResult(Request(request));
        }, Root, (_, value) => c = value);
        try
        {
            await Activate(aRegistry, "dual-a", aInstall.Decorate(aRegistry, new Plugin(() => Task.CompletedTask)));
            await Activate(bRegistry, "dual-b", bInstall.Decorate(bRegistry, new Plugin(() => Task.CompletedTask)));
            await Activate(cRegistry, "dual-c", cInstall.Decorate(cRegistry, new Plugin(() => Task.CompletedTask)));
            aOperation = a!.ExecAsync("held-a", []); await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cOperation = c!.ExecAsync("physical-c", []); await cOperation;
            Check(refused == 2 && bOperation is not null);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            released.TrySetResult();
            if (aOperation is not null) await ExpectedJoin(aOperation, aInstall.Originals, "control-dual-a-mapping", failures, []);
            if (bOperation is not null) await ExpectedJoin(bOperation, bInstall.Originals, "control-dual-b-mapping", failures, []);
            if (cOperation is not null) await ExpectedJoin(cOperation, cInstall.Originals, "control-dual-c-mapping", failures, []);
            await Join(aRegistry.DisposeAsync().AsTask(), failures); await Join(bRegistry.DisposeAsync().AsTask(), failures); await Join(cRegistry.DisposeAsync().AsTask(), failures);
            Capture(aInstall); Capture(bInstall); Capture(cInstall);
        }
        Check(aRunner.Calls == 1 && bRunner.Calls == 1 && cRunner.Calls == 1); Throw(failures);
    }
    private sealed class Plugin(Func<Task> initialize) : IPiSharpExtension
    {
        internal int Initializations, Disposals;
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { Initializations++; return new(initialize()); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Runner(Func<ProcessRequest, CancellationToken, Task<SeparatedProcessRunResult>>? run = null) : ISeparatedProcessRunner
    {
        internal int Calls; internal ProcessRequest? Last;
        public ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken token = default)
        { Calls++; Last = request; return new(run?.Invoke(request, token) ?? Task.FromResult(Result())); }
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null, CancellationToken cancellationToken = default)
            => throw new IOException("Separated original required; merged runner is not admitted.");
    }
}
