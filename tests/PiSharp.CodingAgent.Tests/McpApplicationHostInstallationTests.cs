using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Runtime;

internal static class McpApplicationHostInstallationTests
{
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] RawCapturedOriginals => McpApplicationHostOriginals.Capture();
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("mcp-application-host.all-enabled-configuration-validates-before-any-acquisition", ValidateBeforeAcquire),
        ("mcp-application-host.held-acquisition-cancellation-joins-returned-resource-owners", HeldCancellation),
        ("mcp-application-host.held-close-preserves-both-independent-cleanup-faults", HeldClose),
        ("mcp-application-host.faulted-generation-retains-original-and-every-source-fault", SourceFailure),
        ("mcp-application-host.synchronous-callback-cancellation-remains-faulted", CallbackCancellation),
        ("mcp-application-host.downstream-second-validation-cancellation-remains-faulted", DownstreamCancellation),
        ("mcp-application-host.reused-generation-owner-refused-without-closing-live-owner", ReusedOwner),
        ("mcp-application-host.actual-profile-refresh-withdraws-and-closes-each-generation", McpApplicationHostProfileTests.Refresh),
        .. McpApplicationInitializerAdmissionTests.Cases()
    ];
    private static readonly ModelDescriptor Model = new("application-host", "openai-responses", "fixture");
    private static SessionRuntimeRegistry Runtime(Policy policy) => new([new(Model, new NoTransport())], [], policy);
    private static void Check(bool value) { if (!value) throw new IOException("Application host assertion failed."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static McpLoadedConfiguration Empty => McpConfigurationReader.Load(null, null, true);
    private static Task<McpSessionRuntimeAdmission> Acquire(McpApplicationHostInstallation host, Policy policy,
        CancellationToken token = default) => host.CreateRegisteredAdmission().CreateProfileAdmission()(Path.GetTempPath(), 1, Runtime(policy), policy, token).AsTask();
    private static IEnumerable<Exception> Evidence(Exception error)
    {
        yield return error;
        if (error is AggregateException all)
            foreach (var child in all.InnerExceptions) foreach (var descendant in Evidence(child)) yield return descendant;
        else if (error.InnerException is { } child)
            foreach (var descendant in Evidence(child)) yield return descendant;
    }
    private static async Task<Exception> Failure(Task original)
    { try { await McpApplicationHostOriginals.Observe(original, "expected-failure"); } catch (Exception error) { return McpApplicationHostOriginals.Evidence(original).Aggregate ?? error; } throw new IOException("Expected fault."); }
    private static async Task ValidateBeforeAcquire()
    {
        var metadata = new ExtensionRegistry(); var policy = new Policy(); var factoryCalls = 0; var acquisitions = 0;
        var loaded = McpConfigurationReader.Load(new("test", "{\"mcpServers\":{\"one\":{\"command\":\"inert\"},\"two\":{\"command\":\"inert\"}}}"), null, true);
        var host = new McpApplicationHostInstallation(metadata, loaded,
            [new("one", McpTransportKind.Stdio, _ => { }, (_, _) => { factoryCalls++; throw new IOException("Should not construct."); }),
             new("two", McpTransportKind.Stdio, _ => throw new IOException("second configuration refused"), (_, _) => throw new IOException("Should not construct."))],
            (_, _) => { acquisitions++; throw new IOException("Should not acquire."); });
        var task = Acquire(host, policy); var failures = new List<Exception>();
        try { var fault = await Failure(task); Check(Evidence(fault).Any(error => error.Message == "second configuration refused") && factoryCalls == 0 && acquisitions == 0); }
        catch (Exception error) { failures.Add(error); }
        finally { await Join(metadata.DisposeAsync().AsTask(), failures); }
        Throw(failures);
    }
    private static async Task HeldCancellation()
    {
        var metadata = new ExtensionRegistry(); var policy = new Policy(); var entered = Gate(); var release = Gate();
        var mark = McpApplicationHostOriginals.MarkTracked();
        using var token = new CancellationTokenSource(); var nativeCloses = 0; var discoveryCloses = 0;
        var host = new McpApplicationHostInstallation(metadata, Empty, [], McpApplicationHostOriginals.Generation(async (request, cancellation) =>
        {
            entered.TrySetResult(); await release.Task.ConfigureAwait(false);
            return new(request, new Resource(() => { nativeCloses++; return Task.CompletedTask; }),
                new Resource(() => { discoveryCloses++; return Task.CompletedTask; }), [], (_, current) => new(current, []));
        }));
        var acquisition = Acquire(host, policy, token.Token); var failures = new List<Exception>();
        try
        {
            await Reach(entered.Task, acquisition); token.Cancel(); Check(!acquisition.IsCompleted);
        }
        catch (Exception error) { failures.Add(error); }
        finally { release.TrySetResult(); }
        try { var fault = await Failure(acquisition); Check(Evidence(fault).Any(error => error is OperationCanceledException) && nativeCloses == 1 && discoveryCloses == 1); }
        catch (Exception error) { failures.Add(error); }
        finally { await Join(metadata.DisposeAsync().AsTask(), failures); await McpApplicationHostOriginals.JoinTrackedSince(mark, failures); }
        Throw(failures);
    }
    private static async Task HeldClose()
    {
        var metadata = new ExtensionRegistry(); var policy = new Policy(); var entered = Gate(); var release = Gate();
        var mark = McpApplicationHostOriginals.MarkTracked();
        var nativeFault = new IOException("native original"); var discoveryFault = new IOException("discovery original");
        var native = new Resource(() => Task.FromException(nativeFault));
        var discovery = new Resource(async () => { entered.TrySetResult(); await release.Task; throw discoveryFault; });
        var host = new McpApplicationHostInstallation(metadata, Empty, [], McpApplicationHostOriginals.Generation((request, _) => ValueTask.FromResult(
            new McpApplicationGenerationResources(request, native, discovery, [], (_, current) => new(current, [])))));
        var factory = new McpSessionRuntimeFactory((cwd, generation, cancellation) =>
            host.CreateRegisteredAdmission().CreateProfileAdmission()(cwd, generation, Runtime(policy), policy, cancellation));
        var failures = new List<Exception>(); Task? close = null; Task? repeated = null;
        try
        {
            var lease = await McpApplicationHostOriginals.Observe(factory.AcquireAsync(Path.GetTempPath(), 1).AsTask(), "held-close-acquisition");
            close = lease.DisposeAsync().AsTask(); repeated = lease.DisposeAsync().AsTask();
            await Reach(entered.Task, close); Check(ReferenceEquals(close, repeated) && !close.IsCompleted);
        }
        catch (Exception error) { failures.Add(error); }
        finally { release.TrySetResult(); }
        if (close is not null)
            try { var fault = await Failure(close); Check(Evidence(fault).Contains(nativeFault) && Evidence(fault).Contains(discoveryFault)); }
            catch (Exception error) { failures.Add(error); }
        if (repeated is not null && !ReferenceEquals(repeated, close)) await Join(repeated, failures);
        await Join(metadata.DisposeAsync().AsTask(), failures);
        await McpApplicationHostOriginals.JoinTrackedSince(mark, failures, native.Original, discovery.Original); Throw(failures);
    }
    private static async Task SourceFailure()
    {
        var metadata = new ExtensionRegistry(); var policy = new Policy(); var first = new IOException("source left"); var second = new IOException("source right");
        var mark = McpApplicationHostOriginals.MarkTracked();
        var source = new TaskCompletionSource<McpApplicationGenerationResources>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetException([first, second]);
        var host = new McpApplicationHostInstallation(metadata, Empty, [], McpApplicationHostOriginals.Generation((_, _) => new(source.Task)));
        var acquisition = Acquire(host, policy); var failures = new List<Exception>();
        try
        {
            var fault = await Failure(acquisition);
            Check(Evidence(fault).Contains(first) && Evidence(fault).Contains(second) &&
                Evidence(fault).OfType<McpProfileResourceAcquisitionException>().Any(error => ReferenceEquals(error.Original, source.Task)));
        }
        catch (Exception error) { failures.Add(error); }
        finally { await Join(source.Task, new()); await Join(metadata.DisposeAsync().AsTask(), failures); await McpApplicationHostOriginals.JoinTrackedSince(mark, failures, source.Task); }
        Throw(failures);
    }
    private static async Task CallbackCancellation()
    {
        foreach (var construction in new[] { false, true })
        {
            var metadata = new ExtensionRegistry(); var policy = new Policy(); var cause = new OperationCanceledException("Unowned callback cancellation");
            var loaded = McpConfigurationReader.Load(new("test", "{\"mcpServers\":{\"one\":{\"command\":\"inert\"}}}"), null, true);
            var host = new McpApplicationHostInstallation(metadata, loaded,
                [new("one", McpTransportKind.Stdio, _ => { if (!construction) throw cause; }, (_, _) => throw cause)],
                (_, _) => throw new IOException("Callback refusal must precede resource acquisition."));
            var task = Acquire(host, policy); var failures = new List<Exception>();
            try { var fault = await Failure(task); Check(task.IsFaulted && Evidence(fault).Contains(cause)); }
            catch (Exception error) { failures.Add(error); }
            finally { await Join(metadata.DisposeAsync().AsTask(), failures); }
            Throw(failures);
        }
    }
    private static async Task DownstreamCancellation()
    {
        var metadata = new ExtensionRegistry(); var serverRegistry = new ExtensionRegistry(); var policy = new Policy();
        var mark = McpApplicationHostOriginals.MarkTracked(); var calls = 0; var closes = 0;
        var cause = new OperationCanceledException("second actual activation validator"); var failures = new List<Exception>();
        try
        {
            var scope = await McpApplicationHostOriginals.Observe(serverRegistry.ActivateAsync("capture", new EmptyExtension()), "second-validation-scope");
            var loaded = McpConfigurationReader.Load(new("test", "{\"mcpServers\":{\"one\":{\"command\":\"inert\",\"exposure\":\"direct\"}}}"), null, true);
            var host = new McpApplicationHostInstallation(metadata, loaded,
                [new("one", McpTransportKind.Stdio, _ => { if (++calls == 2) throw cause; },
                    (_, _) => (_, _) => throw new IOException("No channel may acquire after downstream validation refusal."))],
                McpApplicationHostOriginals.Generation((request, _) => ValueTask.FromResult(new McpApplicationGenerationResources(request,
                    new Resource(() => { closes++; return serverRegistry.DisposeAsync().AsTask(); }), new Resource(() => { closes++; return Task.CompletedTask; }),
                    [new(request.Catalog.Servers.Single(), serverRegistry, scope, (_, _, _) => ValueTask.FromResult(true), new(1, "0.99.1"), (_, _) => null)], (_, current) => new(current, [])))));
            var factory = new McpSessionRuntimeFactory((cwd, generation, token) => host.CreateRegisteredAdmission().CreateProfileAdmission()(cwd, generation, Runtime(policy), policy, token));
            var original = factory.AcquireAsync(Path.GetTempPath(), 1).AsTask(); var fault = await Failure(original);
            Check(original.IsFaulted && calls == 2 && closes == 2 && Evidence(fault).Contains(cause));
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            await Join(serverRegistry.DisposeAsync().AsTask(), failures); await Join(metadata.DisposeAsync().AsTask(), failures);
            await McpApplicationHostOriginals.JoinTrackedSince(mark, failures);
        }
        Throw(failures);
    }
    private static async Task ReusedOwner()
    {
        var metadata = new ExtensionRegistry(); var policy = new Policy(); var mark = McpApplicationHostOriginals.MarkTracked();
        var nativeCloses = 0; var discoveryCloses = 0; var rejectedCloses = 0; var count = 0; var failures = new List<Exception>();
        var native = new Resource(() => { nativeCloses++; return Task.CompletedTask; });
        var host = new McpApplicationHostInstallation(metadata, Empty, [], McpApplicationHostOriginals.Generation((request, _) =>
        {
            var first = count++ == 0;
            return ValueTask.FromResult(new McpApplicationGenerationResources(request, native,
                new Resource(() => { if (first) discoveryCloses++; else rejectedCloses++; return Task.CompletedTask; }), [], (_, current) => new(current, [])));
        }));
        var factory = new McpSessionRuntimeFactory((cwd, generation, token) => host.CreateRegisteredAdmission().CreateProfileAdmission()(cwd, generation, Runtime(policy), policy, token));
        PiSharp.CodingAgent.SessionRuntimeLease? lease = null;
        try
        {
            lease = await McpApplicationHostOriginals.Observe(factory.AcquireAsync(Path.GetTempPath(), 1).AsTask(), "first-affine-runtime");
            var second = factory.AcquireAsync(Path.GetTempPath(), 2).AsTask(); _ = await Failure(second);
            Check(second.IsFaulted && nativeCloses == 0 && discoveryCloses == 0 && rejectedCloses == 1);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (lease is not null) await Join(lease.DisposeAsync().AsTask(), failures);
            await Join(metadata.DisposeAsync().AsTask(), failures); await McpApplicationHostOriginals.JoinTrackedSince(mark, failures);
        }
        try { Check(nativeCloses == 1 && discoveryCloses == 1 && rejectedCloses == 1); } catch (Exception error) { failures.Add(error); }
        Throw(failures);
    }
    internal static async Task Reach(Task signal, Task actual)
    {
        using var deadline = new CancellationTokenSource(); var timer = Task.Delay(TimeSpan.FromSeconds(10), deadline.Token);
        try { var completed = await Task.WhenAny(signal, actual, timer); if (!ReferenceEquals(completed, signal)) throw new IOException("Actual operation did not reach held boundary."); await signal; }
        finally { deadline.Cancel(); try { await timer; } catch (OperationCanceledException) { } }
    }
    internal static async Task Join(Task original, List<Exception> failures)
    { try { await McpApplicationHostOriginals.Observe(original, "finally-direct-join"); } catch (Exception error) { failures.Add(McpApplicationHostOriginals.Evidence(original).Aggregate ?? error); } }
    internal static void Throw(List<Exception> failures)
    { if (failures.Count > 0) throw new AggregateException(failures); }
    private sealed class Resource(Func<Task> dispose) : IAsyncDisposable
    { private Task? original; internal Task? Original => original; public ValueTask DisposeAsync() => new(original ??= McpApplicationHostOriginals.Track(dispose(), "actual-resource-disposal")); }
    private sealed class EmptyExtension : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new IOException("Provider not admitted.")); yield break; }
    }
}
