using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class NativeBeforeSwitchPayloadTests
{
    internal const string Prefix = "native before-switch payload ";
    private static readonly ModelDescriptor Model = new("before-switch", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new();
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "actual switch catalog resume and SDK switch expose exact source payload before publication", Payloads),
        (Prefix + "typed native veto precedes observation and joins staged rollback without effects", Veto),
        (Prefix + "missing target and precanceled admission emit nothing and preserve source authority", Exclusions),
        (Prefix + "held original observer cancellation rolls back stage and joins owner and registry disposal", HeldCancellation),
        (Prefix + "original observer failure propagates unchanged and rollback leaves source reusable", Failure)
    ];
    private static async Task Payloads()
    {
        foreach (var route in new[] { "switch", "resume", "sdk" })
        {
            await using var f = await Fixture.Create(); var order = new List<string>(); JsonData? seen = null; ExtensionSessionSnapshot? view = null;
            await f.Register((proposal, context, _) =>
            {
                order.Add("typed"); Check(proposal.PreviousSessionId == "source" && proposal.TargetSessionId == "other" && proposal.TargetPath == f.Other &&
                    proposal.SelectedLeafId == "u0" && ((IExtensionSessionContext)context).SessionSnapshot!.Generation == 1, "Typed preflight proposal/context changed.");
                return ValueTask.FromResult(ExtensionSessionSwitchDecision.Continue);
            }, (value, context, _) =>
            {
                order.Add("observe"); seen = value; view = ((IExtensionSessionContext)context).SessionSnapshot;
                Check(f.Owner.Current.Generation == 1 && f.Owner.Current.Session.Path == f.Source && !f.Owner.Current.Session.Snapshot.IsRetired,
                    "Before-switch observation ran after source retirement/publication.");
                return ValueTask.CompletedTask;
            }); f.Install();
            f.Owner.AttachmentChanged = _ => { order.Add("publish"); return ValueTask.CompletedTask; };
            await f.Invoke(route);
            var value = seen ?? throw new InvalidOperationException("No pre-switch observation.");
            Check(value.Value.EnumerateObject().Select(property => property.Name).SequenceEqual(["type", "reason", "targetSessionFile"]), "Wrong before-switch payload shape.");
            Check(value.Value.GetProperty("type").GetString() == "session_before_switch" && value.Value.GetProperty("reason").GetString() == "resume" &&
                value.Value.GetProperty("targetSessionFile").GetString() == f.Other && !value.Value.TryGetProperty("sessionFile", out _), "Source reason/target path missing or legacy field leaked.");
            Check(view is not null && view.SessionId == "source" && view.Generation == 1 && view.SelectedLeafId == "u0" &&
                view.BranchEntries.Single().Value.GetProperty("message").GetProperty("content").GetString() == "source text", "Observer received staged target context instead of source authority.");
            Check(order.SequenceEqual(["typed", "observe", "publish"]) && f.Owner.Current.Generation == 2 && f.Owner.Current.Session.Path == f.Other &&
                f.Script.Calls == 0, "Ordering, actual replacement or transport exclusion changed.");
        }
    }
    private static async Task Veto()
    {
        await using var f = await Fixture.Create(); var typed = 0; var observed = 0;
        await f.Register((_, _, _) => { typed++; return ValueTask.FromResult(ExtensionSessionSwitchDecision.Cancel); },
            (_, _, _) => { observed++; return ValueTask.CompletedTask; }); f.Install(); var source = f.Owner.Current;
        var bytes = source.Session.Snapshot.Log.CommittedByteLength;
        Check(await f.Owner.SwitchAsync(source, new(f.Other)) is null, "Typed veto did not cancel.");
        Check(typed == 1 && observed == 0 && ReferenceEquals(source, f.Owner.Current) && !source.Session.Snapshot.IsRetired &&
            source.Session.Snapshot.Log.CommittedByteLength == bytes && source.Session.Snapshot.Fault is null && f.Target is { Snapshot.IsDisposed: true },
            "Typed veto published/observed/mutated source or detached actual staged cleanup.");
    }
    private static async Task Exclusions()
    {
        await using var f = await Fixture.Create(); var calls = 0;
        await f.Register((_, _, _) => { calls++; return ValueTask.FromResult(ExtensionSessionSwitchDecision.Continue); },
            (_, _, _) => { calls++; return ValueTask.CompletedTask; }); f.Install(); var source = f.Owner.Current;
        var missing = await Throws<SessionLogStoreException>(() => f.Owner.SwitchAsync(source, new(Path.Combine(f.Root, "missing.jsonl"))));
        Check(missing.Failure == SessionLogStoreFailure.OpenFailed, "Wrong missing-stage failure.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => f.Owner.SwitchAsync(source, new(f.Other), cancellationToken: canceled.Token));
        Check(calls == 0 && f.Target is null && ReferenceEquals(source, f.Owner.Current) && !source.Session.Snapshot.IsRetired && source.Session.Snapshot.Fault is null,
            "Failed/pre-canceled admission reached preflight effects or changed authority.");
    }
    private static async Task HeldCancellation()
    {
        await using var f = await Fixture.Create(); using var caller = new CancellationTokenSource(); var entered = Gate(); var release = Gate(); CancellationToken observerToken = default; OperationCanceledException? observerCancellation = null;
        await f.Register((_, _, _) => ValueTask.FromResult(ExtensionSessionSwitchDecision.Continue), async (_, _, token) =>
        { observerToken = token; entered.TrySetResult(); await release.Task; try { token.ThrowIfCancellationRequested(); } catch (OperationCanceledException error) { observerCancellation = error; throw; } }); f.Install(); var source = f.Owner.Current;
        Task<AgentSessionReplacement?>? original = null; Task? ownerClose = null; Task? registryClose = null;
        try
        {
            original = f.Owner.SwitchAsync(source, new(f.Other), cancellationToken: caller.Token);
            Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted && ReferenceEquals(source, f.Owner.Current), "Original switch escaped held preflight observer.");
            caller.Cancel(); Check(observerToken.CanBeCanceled && observerToken.IsCancellationRequested, "Existing native preflight cancellation was detached.");
            ownerClose = f.Owner.DisposeAsync().AsTask(); registryClose = f.Registry.DisposeAsync().AsTask();
            Check(!original.IsCompleted && !ownerClose.IsCompleted && !registryClose.IsCompleted, "Physical transition/admission cleanup abandoned original observer.");
            release.TrySetResult(); var error = await Throws<OperationCanceledException>(() => original);
            Check(error.CancellationToken == f.PreflightToken && error.CancellationToken.IsCancellationRequested && observerCancellation is not null &&
                observerCancellation.CancellationToken == observerToken && ReferenceEquals(error.InnerException, observerCancellation), "Cancellation lost native admission origin or original observer cause.");
            await ownerClose; await registryClose;
            Check(ReferenceEquals(source, f.Owner.Current) && f.Owner.Current.Generation == 1 && source.Session.Snapshot.IsDisposed &&
                f.Target is { Snapshot.IsDisposed: true }, "Canceled preflight published a new attachment or leaked either actual coordinator.");
        }
        finally
        {
            release.TrySetResult();
            try
            {
                if (original is not null)
                    try { await original; }
                    catch (OperationCanceledException error) when (error.CancellationToken == f.PreflightToken && f.PreflightToken.IsCancellationRequested) { }
            }
            finally { try { if (ownerClose is not null) await ownerClose; } finally { if (registryClose is not null) await registryClose; } }
        }
    }
    private static async Task Failure()
    {
        await using var f = await Fixture.Create(); var failure = new IOException("original native preflight observation failure");
        await f.Register((_, _, _) => ValueTask.FromResult(ExtensionSessionSwitchDecision.Continue), (_, _, _) => throw failure); f.Install(); var source = f.Owner.Current;
        var bytes = source.Session.Snapshot.Log.CommittedByteLength;
        var actual = await Throws<IOException>(() => f.Owner.SwitchAsync(source, new(f.Other)));
        Check(ReferenceEquals(actual, failure) && ReferenceEquals(source, f.Owner.Current) && source.Session.Snapshot.Fault is null &&
            source.Session.Snapshot.Log.CommittedByteLength == bytes && !source.Session.Snapshot.IsRetired && f.Target is { Snapshot.IsDisposed: true },
            "Payload correction changed native failure propagation, durable state or original stage rollback.");
        f.Observation!.Dispose(); f.Install(); await f.Invoke("switch");
        Check(f.Owner.Current.Generation == 2 && f.Owner.Current.Session.Path == f.Other && f.Script.Calls == 0, "Source was unusable after rolled-back observer failure.");
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-before-switch-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal ReplaceableAgentSession Owner = null!; internal ExtensionRegistry Registry = null!; internal PersistentAgentSession? Target;
        internal IExtensionRegistration? Observation; internal Script Script = new(); internal CancellationToken PreflightToken; private NativeSessionSnapshotProvider views = null!;
        private int id; private long ticks = 1711929600000;
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            try
            {
                foreach (var path in new[] { f.Source, f.Other })
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var header = Codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = name, timestamp = "2024-04-01T00:00:00.000Z", cwd = f.Root }));
                    await using var store = await SessionLogStore.CreateNewAsync(path, header);
                    await store.AppendAsync([Codec.Parse(JsonSerializer.Serialize(new { type = "message", id = "u0", parentId = (string?)null,
                        timestamp = "2024-04-01T00:00:00.000Z", message = new { role = "user", content = name + " text", timestamp = 10 } }))]);
                }
                var runtime = new SessionRuntimeRegistry([new(Model, f.Script)], [], new NoPolicy());
                var lifecycle = new PersistentSessionLifecycle(runtime, () => Interlocked.Increment(ref f.ticks), () => "entry-" + Interlocked.Increment(ref f.id),
                    catalog: new SessionCatalog([new("fixtures", f.Root)]));
                var initial = await lifecycle.OpenAsync(new(f.Source), Model); f.Owner = lifecycle.Attach(initial);
                f.views = new NativeSessionSnapshotProvider(); f.views.Attach(f.Owner); f.Registry = new(null, null, f.views);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Task Register(ExtensionSessionSwitchCallback typed, ExtensionObservationCallback observation) => Registry.ActivateAsync("before", new Plugin(api =>
        {
            ((IExtensionSessionLifecycleRegistry)api).RegisterSessionSwitchHandler(new("typed", typed));
            Observation = api.Observe(new("before-observation", "session_before_switch", observation));
            api.RegisterCommand(new("sdk-switch", "sdk-switch", "", async (_, context, token) =>
            {
                var fresh = await ((IExtensionSessionCommandContext)context).SwitchSessionAsync(Other, cancellationToken: token);
                Check(fresh is not null && fresh.SessionSnapshot!.Generation == 2 && fresh.SessionSnapshot.SessionId == "other", "SDK did not receive actual fresh attachment.");
            }));
        }));
        internal void Install()
        {
            var binding = new NativeSessionBeforeSwitchBinding(Registry, Registry.CaptureSnapshot(), CancellationToken.None);
            views.BeforeSwitch = async (previous, target, token) => { Target = target; PreflightToken = token; return await binding.BeforeSwitchAsync(previous, target, token); };
            Owner.BeforeReplacement = views.BeforeSwitch;
        }
        internal async Task Invoke(string route)
        {
            if (route == "sdk") { await Registry.InvokeCommandAsync(Registry.CaptureSnapshot(), "sdk-switch", JsonData.Null); return; }
            AgentSessionReplacement? result;
            if (route == "resume")
            {
                var page = await Owner.ListSessionsAsync(Owner.Current, new());
                result = await Owner.ResumeAsync(Owner.Current, new(page.Items.Single(item => item.Path == Other).Key));
            }
            else result = await Owner.SwitchAsync(Owner.Current, new(Other));
            Check(result is not null, "Replacement unexpectedly vetoed.");
        }
        public async ValueTask DisposeAsync()
        {
            try { if (Registry is not null) await Registry.DisposeAsync(); }
            finally { if (Owner is not null) await Owner.DisposeAsync(); }
            Check(Script.Calls == 0, "Payload fixtures invoked transport.");
            Check(Path.GetDirectoryName(Path.GetFullPath(Root)) == parent && Path.GetFileName(Root).StartsWith("PiSharp-before-switch-", StringComparison.Ordinal), "Invalid owned cleanup root.");
            if (Directory.Exists(Root))
            {
                Check((File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "Linked cleanup root rejected.");
                Directory.Delete(Root, recursive: true);
            }
        }
    }
    private sealed class Plugin(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { token.ThrowIfCancellationRequested(); initialize(registry); return ValueTask.CompletedTask; } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Script : IChatTransport
    {
        internal int Calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new InvalidOperationException("Preflight payload fixture acquired a provider.")); yield break; }
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Preflight payload fixture acquired a tool."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
