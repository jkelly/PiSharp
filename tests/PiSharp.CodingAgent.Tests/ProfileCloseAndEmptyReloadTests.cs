using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Runtime.Reloading;
using PiSharp.Sessions.Serialization;

internal static class ProfileCloseAndEmptyReloadTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("MCP profile native cleanup refuses cached profile close self-join and retains original faults", CleanupReentry),
        ("MCP profile dependency-only reload owns independent empty view through start and final close", () => EmptyReload(false)),
        ("MCP profile rejected dependency-only reload joins decorated candidate without old-view revival", () => EmptyReload(true))
    ];
    private static async Task CleanupReentry()
    {
        var entered = Gate(); var release = Gate(); var rejected = false; Task? accepted = null;
        var a = new IOException("native close A"); var b = new OperationCanceledException("native close faulted B");
        await using var fixture = await Fixture.Create();
        fixture.Native.Callback = () =>
        {
            try { accepted = fixture.Profile.DisposeAsync().AsTask(); }
            catch (InvalidOperationException) { rejected = true; }
            entered.TrySetResult(); return new(release.Task);
        };
        var close = fixture.Profile.DisposeAsync().AsTask();
        try
        {
            await Within(entered.Task);
            Check(rejected && accepted is null && !close.IsCompleted);
            Check(ReferenceEquals(close, fixture.Profile.DisposeAsync().AsTask()));
            release.SetException([a, b]);
            var failure = await Failure(close);
            Check(Contains(failure, a) && Contains(failure, b));
            Check(fixture.Native.Count == 1 && release.Task.IsFaulted);
        }
        finally
        {
            release.TrySetResult(); await Settle(close);
            if (accepted is not null) await Settle(accepted);
        }
    }
    private static async Task EmptyReload(bool rejectPublication)
    {
        await using var fixture = await Fixture.Create();
        var candidateResource = new Resource(); var starts = 0; var binds = 0;
        var registry = fixture.Profile.Registry;
        var rejection = new IOException("empty-view publication refused");
        var candidateLease = new SessionRuntimeLease(registry, candidateResource, (owner, attachment) =>
        {
            Check(ReferenceEquals(owner, fixture.Profile.Sessions) && ReferenceEquals(owner.Current, attachment)); binds++;
        });
        fixture.Profile.ConfigureReload(new(new(new object()), [], true, new([], []), new()
        {
            StageSettingsAsync = (_, _) => ValueTask.FromResult(new NativeHostReloadPayload(new object())),
            SyncQueueModesAsync = (_, _) => ValueTask.CompletedTask,
            ResetApiProvidersAsync = (_, _) => ValueTask.CompletedTask,
            ReloadResourcesAsync = (_, _) => ValueTask.CompletedTask,
            DescribeRuntimeAsync = (_, _) => ValueTask.FromResult(new HostReloadRuntime([], [])),
            BuildRuntimeAsync = (_, _, _) => ValueTask.FromResult(new PreparedNativeHostReload(
                candidateLease, () => { if (rejectPublication) throw rejection; })),
            SessionShutdownAsync = (_, _, _) => ValueTask.CompletedTask,
            CleanupPreparationAsync = _ => ValueTask.CompletedTask,
            BeforeSessionStartAsync = (_, _) => ValueTask.CompletedTask,
            SessionStartAsync = (_, _, _) => { starts++; return ValueTask.CompletedTask; },
            ReportUnhandledMcpServersAsync = (_, _) => ValueTask.CompletedTask,
            ExtendResourcesAsync = (_, _, _) => ValueTask.CompletedTask
        }));
        var previous = fixture.Profile.Sessions!.Current;
        var reload = fixture.Profile.ReloadAsync(previous);
        Check(ReferenceEquals(reload, fixture.Profile.ReloadAsync(previous)));
        if (rejectPublication)
        {
            Check(Contains(await Failure(reload), rejection));
            Check(starts == 0 && binds == 0 && candidateResource.Count == 1);
            try { _ = fixture.Profile.CommandCatalog; throw new IOException("Old view was revived."); }
            catch (InvalidOperationException) { }
            await Settle(fixture.Profile.DisposeAsync().AsTask());
            Check(candidateResource.Count == 1 && fixture.Native.Count == 1 && fixture.Discovery.Count == 1);
            return;
        }
        var result = await reload;
        Check(result.Workflow.Authority == ResourceReloadAuthority.New && result.Workflow.Failures.IsEmpty);
        Check(starts == 1 && fixture.Native.Count == 1 && fixture.Discovery.Count == 1);
        Check(binds == 1 && ReferenceEquals(result.Workflow.Candidate!.Payload.Prepared!.Runtime, candidateLease));
        Check(fixture.Profile.CommandCatalog.ToString() == "[]");
        // Direct owner close must carry the candidate wrapper's profile callback guard,
        // even though no outer profile close has installed its cached task yet.
        var rejected = false;
        candidateResource.Callback = () =>
        {
            try { _ = fixture.Profile.DisposeAsync(); }
            catch (InvalidOperationException) { rejected = true; }
            return ValueTask.CompletedTask;
        };
        await fixture.Profile.Sessions!.DisposeAsync();
        await fixture.Profile.DisposeAsync(); Check(candidateResource.Count == 1 && rejected);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "pisharp-profile-close-empty-" + Guid.NewGuid().ToString("N"));
        internal OfflineSessionProfile Profile = null!;
        internal readonly Resource Native = new(), Discovery = new();
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.root); var path = Path.Combine(f.root, "session.jsonl");
            f.Profile = await OfflineSessionProfile.CreateAsync(f.root, path, null, [], [], [], default,
                mcpAdmission: (_, _, registry, policy, _) => ValueTask.FromResult(new McpSessionRuntimeAdmission(
                    registry, f.Native, f.Discovery, policy, new McpServerCatalog([], []), [], false, (_, current) => new(current, []))));
            var lifecycle = f.Profile.CreateLifecycle(() => 1, () => Guid.NewGuid().ToString("N"));
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "close-empty", timestamp = "2026-10-05T00:00:00.000Z", cwd = f.root }));
            var session = await lifecycle.CreateAsync(path, header, f.Profile.SelectedModel);
            await f.Profile.AttachOwnerAsync(session, lifecycle: lifecycle);
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            await Settle(Profile.DisposeAsync().AsTask());
            if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(root).StartsWith("pisharp-profile-close-empty-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }
    private sealed class Resource : IAsyncDisposable
    {
        internal int Count;
        internal Func<ValueTask>? Callback;
        public ValueTask DisposeAsync() { Count++; return Callback?.Invoke() ?? ValueTask.CompletedTask; }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Within(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Settle(Task task) { try { await task; } catch (Exception) { } }
    private static async Task<Exception> Failure(Task task)
    { try { await Within(task); } catch (Exception error) { return task.Exception ?? error; } throw new IOException("Expected failure."); }
    private static bool Contains(Exception error, Exception original) => ReferenceEquals(error, original) ||
        (error is AggregateException aggregate ? aggregate.InnerExceptions.Any(child => Contains(child, original)) :
            error.InnerException is { } inner && Contains(inner, original));
    private static void Check(bool value) { if (!value) throw new IOException("Profile close/empty-view contract failed."); }
}
