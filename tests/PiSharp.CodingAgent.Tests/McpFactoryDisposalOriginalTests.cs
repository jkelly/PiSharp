using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;

// Actual factory success-release and acquisition-rollback controls. Unregistered/unexecuted.
internal static class McpFactoryDisposalOriginalTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("mcp-factory-disposal.held-success-close-joins-both-multifault-originals", () => Held(false));
        yield return ("mcp-factory-disposal.held-rollback-joins-body-and-both-multifault-originals", () => Held(true));
        foreach (var rollback in new[] { false, true })
            foreach (var discovery in new[] { false, true })
                foreach (var kind in new[] { "faulted-oce", "faulted-duplicate-oce", "synchronous-oce", "synchronous-aggregate-oce", "canceled-original" })
                {
                    var capturedRollback = rollback; var capturedDiscovery = discovery; var capturedKind = kind;
                    yield return ($"mcp-factory-disposal.{(rollback ? "rollback" : "release")}-{(discovery ? "discovery" : "native")}-{kind}",
                        () => CancellationState(capturedRollback, capturedDiscovery, capturedKind));
                }
    }
    private static void Check(bool value) { if (!value) throw new IOException("Factory disposal original control failed."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new IOException("Expected owning failure."); }
    private sealed class Policy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(false));
    }
    private sealed class Transport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new IOException("No provider invocation admitted.")); yield break; }
    }
    private sealed class Resource(Func<ValueTask> dispose) : IAsyncDisposable
    {
        internal int Calls; internal readonly TaskCompletionSource Entered = Gate();
        public ValueTask DisposeAsync() { Calls++; Entered.TrySetResult(); return dispose(); }
    }
    private static McpSessionRuntimeFactory Factory(Resource native, Resource discovery, Exception? rejection)
    {
        var policy = new Policy(); var model = new ModelDescriptor("disposal", "openai-responses", "fixture");
        var registry = new SessionRuntimeRegistry([new(model, new Transport())], [], policy, new() { BindNestedCallsToSessionOwner = true });
        return new((cwd, generation, token) => ValueTask.FromResult(new McpSessionRuntimeAdmission(registry, native, discovery, policy,
            new McpServerCatalog([], []), [], false, (plan, current) => rejection is null ? new(current, []) : throw rejection)));
    }
    private static IEnumerable<Exception> Walk(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) foreach (var item in Walk(inner)) yield return item;
        else if (error.InnerException is { } inner)
            foreach (var item in Walk(inner)) yield return item;
    }
    private static bool SameFaults(Exception? evidence, Task original) => evidence is AggregateException retained && original.Exception is { } actual &&
        retained.InnerExceptions.Count == actual.InnerExceptions.Count && retained.InnerExceptions.Zip(actual.InnerExceptions).All(pair => ReferenceEquals(pair.First, pair.Second));
    private static async Task Held(bool rollback)
    {
        var discoveryOriginal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeOriginal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new Resource(() => new(discoveryOriginal.Task)); var native = new Resource(() => new(nativeOriginal.Task));
        var body = new IOException("actual discovery preparation body");
        var factory = Factory(native, discovery, rollback ? body : null);
        SessionRuntimeLease? lease = null;
        Task work;
        if (rollback) work = factory.AcquireAsync(Path.GetTempPath(), 7).AsTask();
        else { lease = await factory.AcquireAsync(Path.GetTempPath(), 7); work = lease.DisposeAsync().AsTask(); }
        var first = new IOException("discovery first"); var second = new IOException("discovery sibling");
        var third = new IOException("native first"); var fourth = new IOException("native sibling");
        Exception? assertion = null;
        try
        {
            await discovery.Entered.Task; Check(!work.IsCompleted && native.Calls == 0 && discovery.Calls == 1);
            if (lease is not null) Check(ReferenceEquals(work, lease.DisposeAsync().AsTask()));
            discoveryOriginal.TrySetException(new[] { first, second });
            await native.Entered.Task; Check(!work.IsCompleted && discoveryOriginal.Task.IsFaulted && native.Calls == 1);
        }
        catch (Exception error) { assertion = error; }
        finally
        {
            discoveryOriginal.TrySetException(new[] { first, second }); nativeOriginal.TrySetException(new[] { third, fourth });
        }
        var errorResult = await Failure(work); var found = Walk(errorResult).ToArray();
        Check(work.IsFaulted && found.Contains(first) && found.Contains(second) && found.Contains(third) && found.Contains(fourth));
        Check(found.OfType<McpFactoryDisposalException>().Any(error => error.Owner == "discovery" && ReferenceEquals(error.Original, discoveryOriginal.Task) && SameFaults(error.InnerException, discoveryOriginal.Task)));
        Check(found.OfType<McpFactoryDisposalException>().Any(error => error.Owner == "native" && ReferenceEquals(error.Original, nativeOriginal.Task) && SameFaults(error.InnerException, nativeOriginal.Task)));
        Check(!rollback || found.Contains(body)); Check(discovery.Calls == 1 && native.Calls == 1);
        if (lease is not null) Check(ReferenceEquals(errorResult, await Failure(lease.DisposeAsync().AsTask())));
        if (assertion is not null) throw assertion;
    }
    private static async Task CancellationState(bool rollback, bool discoveryFailure, string kind)
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var physicalOce = new OperationCanceledException("physical disposal fault", canceled.Token);
        var synchronousSibling = new IOException("synchronous aggregate sibling");
        Exception synchronousEvidence = kind == "synchronous-aggregate-oce" ? new AggregateException(physicalOce, synchronousSibling) : physicalOce;
        Task DuplicatedFault()
        {
            var duplicated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            duplicated.SetException(new[] { physicalOce, physicalOce }); return duplicated.Task;
        }
        Task? original = kind switch
        {
            "faulted-oce" => Task.FromException(physicalOce),
            "faulted-duplicate-oce" => DuplicatedFault(),
            "canceled-original" => Task.FromCanceled(canceled.Token),
            _ => null
        };
        var failing = new Resource(() => original is { } task ? new(task) : throw synchronousEvidence);
        var successful = new Resource(() => ValueTask.CompletedTask);
        var discovery = discoveryFailure ? failing : successful; var native = discoveryFailure ? successful : failing;
        var body = new IOException("rollback body"); var factory = Factory(native, discovery, rollback ? body : null);
        Task work;
        if (rollback) work = factory.AcquireAsync(Path.GetTempPath(), 9).AsTask();
        else { var lease = await factory.AcquireAsync(Path.GetTempPath(), 9); work = lease.DisposeAsync().AsTask(); }
        var result = await Failure(work); var found = Walk(result).ToArray();
        Check(discovery.Calls == 1 && native.Calls == 1 && (!rollback || found.Contains(body)));
        if (kind == "canceled-original")
        {
            Check(original!.IsCanceled && (rollback ? work.IsFaulted : work.IsCanceled));
            Check(found.OfType<McpFactoryDisposalCancellation>().Any(error => ReferenceEquals(error.Original, original) && error.CancellationToken == canceled.Token));
        }
        else
        {
            Check(work.IsFaulted && !work.IsCanceled && found.Contains(physicalOce));
            var wrapper = found.OfType<McpFactoryDisposalException>().Single(error => error.Owner == (discoveryFailure ? "discovery" : "native"));
            Check(ReferenceEquals(wrapper.Original, original));
            Check(original is null ? ReferenceEquals(wrapper.InnerException, synchronousEvidence) : original.IsFaulted && SameFaults(wrapper.InnerException, original));
            if (kind == "synchronous-aggregate-oce") Check(found.Contains(synchronousSibling));
            if (kind == "faulted-duplicate-oce") Check(wrapper.InnerException is AggregateException { InnerExceptions.Count: 2 });
        }
    }
}
