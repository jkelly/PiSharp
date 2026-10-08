using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Mcp.Configuration;

internal static class McpProfileAdmissionRollbackTests
{
    internal const string Prefix = "mcp-profile-admission-rollback.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "registry-mismatch-joins-both-held-original-fault-inventories", () => Held(false)),
        (Prefix + "policy-mismatch-joins-both-held-original-fault-inventories", () => Held(true)),
        (Prefix + "synchronous-and-task-faulted-OCE-retain-provenance", CancellationFaults),
        (Prefix + "aliased-rejected-owner-disposes-once", Aliased)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new IOException("Profile admission rollback control failed."); }
    private sealed class Policy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(false));
    }
    private sealed class Resource(Func<ValueTask> release) : IAsyncDisposable
    {
        internal int Calls;
        internal readonly TaskCompletionSource Entered = Gate();
        public ValueTask DisposeAsync() { Calls++; Entered.TrySetResult(); return release(); }
    }
    private static IEnumerable<Exception> Walk(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
            foreach (var child in aggregate.InnerExceptions) foreach (var found in Walk(child)) yield return found;
        else if (error.InnerException is { } inner)
            foreach (var found in Walk(inner)) yield return found;
    }
    private static async Task<Exception> Failure(Task work)
    { try { await work; } catch (Exception selected) { return work.Exception ?? selected; } throw new IOException("Expected rollback failure."); }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "mcp-profile-rollback-" + Guid.NewGuid().ToString("N"));
        internal OfflineSessionProfile? Profile;
        internal int Preparations;
        internal async Task<PersistentAgentSession> Start(Resource native, Resource discovery, bool policyMismatch)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "session.jsonl");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            { type = "session", version = 3, id = "profile-rollback", timestamp = "2026-10-05T00:00:00.000Z", cwd = root }) + "\n");
            Profile = await OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], CancellationToken.None,
                mcpAdmission: (cwd, generation, registry, policy, token) => ValueTask.FromResult(new McpSessionRuntimeAdmission(
                    policyMismatch ? registry : registry.WithToolCatalog(registry.RegisteredTools, registry.PreparedToolHooks),
                    native, discovery, policyMismatch ? new Policy() : policy, new McpServerCatalog([], []), [], false,
                    (plan, current) => { Preparations++; return new(current, []); })));
            return await Profile.CreateLifecycle(() => 1, () => Guid.NewGuid().ToString("N")).OpenAsync(new(path), Profile.SelectedModel);
        }
        public async ValueTask DisposeAsync()
        {
            if (Profile is not null) await Profile.DisposeAsync();
            var actual = Path.GetFullPath(root);
            Check(actual.StartsWith(Path.GetFullPath(Path.GetTempPath()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Path.GetFileName(actual).StartsWith("mcp-profile-rollback-", StringComparison.Ordinal));
            if (Directory.Exists(actual)) Directory.Delete(actual, true);
        }
    }
    private static async Task Held(bool policyMismatch)
    {
        await using var fixture = new Fixture();
        var discoveryTask = Gate(); var nativeTask = Gate();
        var discovery = new Resource(() => new(discoveryTask.Task)); var native = new Resource(() => new(nativeTask.Task));
        var first = new IOException("discovery first"); var sibling = new OperationCanceledException("discovery faulted OCE");
        var third = new IOException("native first"); var fourth = new IOException("native sibling");
        var original = fixture.Start(native, discovery, policyMismatch); Exception? assertion = null;
        try
        {
            Check(ReferenceEquals(await Task.WhenAny(discovery.Entered.Task, original).WaitAsync(TimeSpan.FromSeconds(30)), discovery.Entered.Task));
            Check(!original.IsCompleted && native.Calls == 0);
            discoveryTask.TrySetException([first, sibling]);
            Check(ReferenceEquals(await Task.WhenAny(native.Entered.Task, original).WaitAsync(TimeSpan.FromSeconds(30)), native.Entered.Task));
            Check(!original.IsCompleted && discoveryTask.Task.IsFaulted);
        }
        catch (Exception error) { assertion = error; }
        finally { discoveryTask.TrySetException([first, sibling]); nativeTask.TrySetException([third, fourth]); }
        var failure = await Failure(original); var evidence = Walk(failure).ToArray();
        Check(original.IsFaulted && !original.IsCanceled && fixture.Preparations == 0 && native.Calls == 1 && discovery.Calls == 1);
        foreach (var expected in new Exception[] { first, sibling, third, fourth }) Check(evidence.Any(item => ReferenceEquals(item, expected)));
        Check(evidence.OfType<McpFactoryDisposalException>().Any(item => ReferenceEquals(item.Original, discoveryTask.Task)));
        Check(evidence.OfType<McpFactoryDisposalException>().Any(item => ReferenceEquals(item.Original, nativeTask.Task)));
        if (assertion is not null) throw assertion;
    }
    private static async Task CancellationFaults()
    {
        await using var fixture = new Fixture();
        var synchronous = new OperationCanceledException("synchronous discovery fault");
        var taskFault = new OperationCanceledException("native task fault");
        var nativeTask = Task.FromException(taskFault);
        var discovery = new Resource(() => throw synchronous); var native = new Resource(() => new(nativeTask));
        var original = fixture.Start(native, discovery, false); var failure = await Failure(original); var evidence = Walk(failure).ToArray();
        Check(original.IsFaulted && !original.IsCanceled && fixture.Preparations == 0 && native.Calls == 1 && discovery.Calls == 1);
        Check(evidence.OfType<McpFactoryDisposalException>().Any(item => item.Original is null && ReferenceEquals(item.InnerException, synchronous)));
        Check(evidence.OfType<McpFactoryDisposalException>().Any(item => ReferenceEquals(item.Original, nativeTask)));
        Check(evidence.Any(item => ReferenceEquals(item, taskFault)));
    }
    private static async Task Aliased()
    {
        await using var fixture = new Fixture(); var resource = new Resource(() => ValueTask.CompletedTask);
        var original = fixture.Start(resource, resource, false); await Failure(original);
        Check(resource.Calls == 1 && fixture.Preparations == 0 && original.IsFaulted);
    }
}
