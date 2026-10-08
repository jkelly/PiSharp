using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

// Held genuine precommit and sequential publication schedules; no runtime execution authorized here.
internal static class ShutdownNotificationPhaseTests
{
    private static readonly ModelDescriptor Model = new("notification-phase", "openai-responses", "fixture");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Notification phase assertion failed."); }
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("shutdown-phase. held precommit retirement retains original lifetime until genuine acknowledgment", () => Precommit(1)),
        ("shutdown-phase. pending second publisher retains lifetime after first original settles", () => Precommit(2)),
    ];
    private static async Task<Exception?> Failure(Task task)
    { try { await task; return null; } catch (Exception error) { return error; } }
    private static async Task Precommit(int bodies)
    {
        using var fixture = new Fixture(); var owner = await fixture.Owner(); var attachment = owner.Current;
        var precommit = Gate(); var beginRetirement = Gate(); var closing = Gate();
        var entered = Enumerable.Range(0, bodies).Select(_ => Gate()).ToArray();
        var release = Enumerable.Range(0, bodies).Select(_ => Gate()).ToArray();
        var acknowledged = new bool[bodies]; var ended = new bool[bodies]; var bodyCalls = 0;
        CancellationTokenRegistration registration = default;
        owner.BeforeRetirement = async (_, _, _) =>
        {
            registration = fixture.TransitionToken.Register(() => closing.TrySetResult());
            precommit.TrySetResult(); await beginRetirement.Task;
        };
        owner.AfterReplacement = replacement =>
        { Check(replacement.Current.LifetimeToken.IsCancellationRequested); return ValueTask.CompletedTask; };
        for (var index = 0; index < bodies; index++)
        {
            var selected = index;
            owner.RegisterOwnedResource(attachment, async tx =>
            {
                bodyCalls++;
                Check(!attachment.LifetimeToken.IsCancellationRequested && selected == bodyCalls - 1);
                await tx.PrepareAndPublishCatalogAsync(async (registry, names, token) =>
                {
                    Check(!token.CanBeCanceled && !attachment.LifetimeToken.IsCancellationRequested);
                    entered[selected].TrySetResult(); await release[selected].Task;
                    Check(!attachment.LifetimeToken.IsCancellationRequested);
                    return new PreparedSessionToolCatalog(registry.WithToolCatalog(registry.RegisteredTools, registry.PreparedToolHooks),
                        names, () => acknowledged[selected] = true);
                });
                Check(acknowledged[selected] && !attachment.LifetimeToken.IsCancellationRequested);
                ended[selected] = true;
            });
        }
        var switching = owner.SwitchAsync(attachment, new(fixture.B)); Task? stop = null;
        try
        {
            await Task.WhenAny(precommit.Task, switching); Check(precommit.Task.IsCompleted);
            stop = owner.DisposeAsync().AsTask(); Check(ReferenceEquals(stop, owner.DisposeAsync().AsTask()));
            await Task.WhenAny(closing.Task, stop); Check(closing.Task.IsCompleted);
            Check(!stop.IsCompleted && !attachment.LifetimeToken.IsCancellationRequested && bodyCalls == 0);
            beginRetirement.TrySetResult();
            for (var index = 0; index < bodies; index++)
            {
                await Task.WhenAny(entered[index].Task, switching); Check(entered[index].Task.IsCompleted);
                Check(!stop.IsCompleted && !attachment.LifetimeToken.IsCancellationRequested && !acknowledged[index]);
                if (index != 0) Check(acknowledged[index - 1] && ended[index - 1]);
                release[index].TrySetResult();
            }
            Check(await Failure(switching) is null); await stop;
            Check(bodyCalls == bodies && acknowledged.All(value => value) && ended.All(value => value)
                && attachment.LifetimeToken.IsCancellationRequested && owner.Current.Generation == 2 && owner.Current.Session.Snapshot.IsDisposed);
        }
        finally
        {
            beginRetirement.TrySetResult(); foreach (var gate in release) gate.TrySetResult();
            var close = stop ?? owner.DisposeAsync().AsTask(); await Failure(switching); await Failure(close); registration.Dispose();
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "PiSharp-notification-phase-" + Guid.NewGuid().ToString("N"));
        public string B => Path.Combine(root, "b.jsonl");
        public CancellationToken TransitionToken { get; private set; }
        private readonly SessionRuntimeRegistry registry = new([new(Model, new NoTransport())], [], new Deny());
        private int ids;
        public async Task<ReplaceableAgentSession> Owner()
        {
            Directory.CreateDirectory(root); await using (var staged = await Create(B)) { }
            var initial = await Create(Path.Combine(root, "a.jsonl"));
            return new(initial, (request, token) =>
            {
                TransitionToken = token;
                return PersistentAgentSession.OpenWithRegistryAsync(request.Path, registry, () => 1,
                    () => "open-" + Interlocked.Increment(ref ids), fallbackModel: Model, cancellationToken: token);
            });
        }
        private Task<PersistentAgentSession> Create(string path)
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = Path.GetFileNameWithoutExtension(path), timestamp = "2026-10-05T00:00:00.000Z", cwd = root }));
            return PersistentAgentSession.CreateAsync(path, header, registry, Model, () => 1, () => "entry-" + Interlocked.Increment(ref ids));
        }
        public void Dispose()
        {
            var absolute = Path.GetFullPath(root); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(absolute) != parent || !Path.GetFileName(absolute).StartsWith("PiSharp-notification-phase-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid fixture cleanup path.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new InvalidOperationException("Notification fixture must not invoke provider.")); yield break; }
    }
    private sealed class Deny : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(false));
    }
}
