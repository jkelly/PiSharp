using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Extensions.Runtime.Reloading;
using PiSharp.Sessions.Serialization;

internal static class CliReloadRoutingTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("CLI actual RPC caller routes reload through attached owner and closes candidate runtime", ActualCaller),
        ("CLI absent reload admission refuses command without provider work", AbsentAdmission),
        ("CLI profile reload retains original joins and published generation provenance", ProfileJoins)
    ];

    private static Task ActualCaller() => Caller(admitted: true);
    private static Task AbsentAdmission() => Caller(admitted: false);
    private static async Task Caller(bool admitted)
    {
        using var files = new Files();
        await File.WriteAllTextAsync(files.Script, "{\"schemaVersion\":1,\"turns\":[{\"events\":[{\"type\":\"response.completed\"}]}]}");
        var callbacks = new Callbacks();
        using var input = new CommandInput(); using var output = new ObservedOutput(input);
        using var error = new StringWriter();
        var running = RpcSessionCommand.RunAsync(["session", "rpc", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", files.Script, "--session-mode", "new-lazy"], input, output, error,
            reloadAdmission: admitted ? callbacks.Admission() : null);
        try
        {
            await Task.WhenAny(output.Response.Task, running);
            Check(output.Response.Task.IsCompletedSuccessfully);
            var response = await output.Response.Task;
            Check(response.GetProperty("success").GetBoolean() == admitted);
            if (admitted) Check(response.GetProperty("data").GetProperty("disposition").GetString() == "handled");
        }
        finally { input.End.TrySetResult(); await running; }
        Check(await running == 0 && error.ToString() == "");
        Check(callbacks.Commits == (admitted ? 1 : 0) && callbacks.Released == (admitted ? 1 : 0));
        Check(callbacks.Candidate is null || callbacks.Candidate.Session.Snapshot.IsDisposed);
        var wire = Encoding.UTF8.GetString(output.ToArray());
        Check(!wire.Contains("agent_start", StringComparison.Ordinal));
        Check(wire.Contains("session_switched", StringComparison.Ordinal) == admitted);
    }

    private static async Task ProfileJoins()
    {
        using var files = new Files();
        await using var profile = await OfflineSessionProfile.CreateAsync(files.Root, files.Session, null, [], [], [], default);
        var sequence = 0;
        var lifecycle = profile.CreateLifecycle(() => 1, () => "entry-" + ++sequence);
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
            { type = "session", version = 3, id = "reload", timestamp = "2026-10-05T00:00:00.000Z", cwd = files.Root }));
        var session = await lifecycle.CreateAsync(files.Session, header, profile.SelectedModel);
        await profile.AttachOwnerAsync(session, lifecycle: lifecycle);
        var owner = profile.Sessions!;
        var callbacks = new Callbacks(); profile.ConfigureReload(callbacks.Admission());
        callbacks.StartEntered = Gate(); callbacks.StartRelease = Gate();
        var initial = owner.Current;
        var first = profile.ReloadAsync(initial);
        try
        {
            await callbacks.StartEntered.Task;
            Check(!ReferenceEquals(initial, owner.Current));
            Check(ReferenceEquals(first, profile.ReloadAsync(initial)));
            Check(ReferenceEquals(first, profile.ReloadAsync(owner.Current)));
        }
        finally { callbacks.StartRelease.TrySetResult(); await Settle(first); }
        var receipt = await first;
        Check(receipt.Workflow.Authority == ResourceReloadAuthority.New && receipt.Workflow.Failures.IsEmpty);
        Check(ReferenceEquals(receipt.Current!.Session, session));
        Check(ReferenceEquals(first, profile.ReloadAsync(initial)));
        callbacks.BuildEntered = Gate(); callbacks.BuildRelease = Gate();
        var second = profile.ReloadAsync(owner.Current);
        await callbacks.BuildEntered.Task;
        Check(!second.IsCompleted && ReferenceEquals(second, profile.ReloadAsync(owner.Current)));
        Task? closing = null;
        try
        {
            closing = owner.DisposeAsync().AsTask(); Check(!closing.IsCompleted);
            callbacks.BuildRelease.TrySetResult();
            await Settle(second); await Settle(closing);
        }
        finally
        {
            callbacks.BuildRelease.TrySetResult(); await Settle(second);
            if (closing is not null) await Settle(closing);
        }
        Check(callbacks.Released == 2 && session.Snapshot.IsDisposed);
    }

    private sealed class Callbacks
    {
        internal int Commits, Released;
        internal AgentSessionAttachment? Candidate;
        internal TaskCompletionSource? BuildEntered, BuildRelease, StartEntered, StartRelease;
        internal NativeHostReloadAdmission Admission() => new(new(new object()),
            new Dictionary<string, HostReloadFlagValue> { ["flag"] = new(false) }, true, new([], []), new()
            {
                StageSettingsAsync = (_, _) => ValueTask.FromResult(new NativeHostReloadPayload(new object())),
                SyncQueueModesAsync = (_, _) => ValueTask.CompletedTask,
                ResetApiProvidersAsync = (_, _) => ValueTask.CompletedTask,
                ReloadResourcesAsync = (_, _) => ValueTask.CompletedTask,
                DescribeRuntimeAsync = (_, _) => ValueTask.FromResult(new HostReloadRuntime([], [])),
                BuildRuntimeAsync = async (candidate, generation, _) =>
                {
                    Candidate = candidate; Check(generation.Flags["flag"].Boolean == false);
                    var registry = new SessionRuntimeRegistry([new(candidate.Session.Snapshot.Agent.Model, new UnusedTransport())], [], new Policy());
                    var runtime = new SessionRuntimeLease(registry, new Release(() => Released++));
                    BuildEntered?.TrySetResult();
                    if (BuildRelease is not null) await BuildRelease.Task;
                    return new(runtime, () => Commits++);
                },
                SessionShutdownAsync = (_, reason, _) => { Check(reason == "reload"); return ValueTask.CompletedTask; },
                CleanupPreparationAsync = _ => ValueTask.CompletedTask,
                BeforeSessionStartAsync = (_, _) => ValueTask.CompletedTask,
                SessionStartAsync = async (_, reason, _) =>
                {
                    Check(reason == "reload"); StartEntered?.TrySetResult();
                    if (StartRelease is not null) await StartRelease.Task;
                },
                ReportUnhandledMcpServersAsync = (_, _) => ValueTask.CompletedTask,
                ExtendResourcesAsync = (_, reason, _) => { Check(reason == "reload"); return ValueTask.CompletedTask; }
            });
    }
    private sealed class Release(Action release) : IAsyncDisposable
    { public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; } }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class UnusedTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("Reload must not invoke a provider.")); yield break; }
    }
    private sealed class CommandInput : MemoryStream
    {
        internal readonly TaskCompletionSource End = Gate();
        internal CommandInput() : base(Encoding.UTF8.GetBytes("{\"id\":\"reload\",\"type\":\"prompt\",\"message\":\"/reload\"}\n")) { }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, token);
            await End.Task.WaitAsync(token); return 0;
        }
    }
    private sealed class ObservedOutput(CommandInput input) : MemoryStream
    {
        internal readonly TaskCompletionSource<JsonElement> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await base.WriteAsync(buffer, token);
            foreach (var line in Encoding.UTF8.GetString(ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                if (item.TryGetProperty("type", out var type) && type.GetString() == "response" &&
                    item.TryGetProperty("id", out var id) && id.GetString() == "reload")
                { Response.TrySetResult(item.Clone()); input.End.TrySetResult(); }
            }
        }
    }
    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-cli-reload-" + Guid.NewGuid().ToString("N"));
        internal string Session => Path.Combine(Root, "session.jsonl");
        internal string Script => Path.Combine(Root, "script.json");
        internal Files() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(Root).StartsWith("pisharp-cli-reload-", StringComparison.Ordinal)) Directory.Delete(Root, true);
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Settle(Task task) { try { await task; } catch (Exception) { } }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("CLI reload routing contract failed."); }
}
