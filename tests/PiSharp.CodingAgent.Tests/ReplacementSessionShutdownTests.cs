using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class ReplacementSessionShutdownTests
{
    internal const string Prefix = "replacement session-shutdown ";
    private static readonly ModelDescriptor Model = new("replace-shutdown", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new();
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "new resume switch fork-before fork-at and clone observe outgoing reserved attachment", Payloads),
        (Prefix + "veto staging target refusal and precancellation exclude shutdown", Exclusions),
        (Prefix + "held callback joins cancellation and original owner disposal before retirement", HeldCompletion),
        (Prefix + "handler failures continue and throwing reporter preserves source and original cause", Failures),
        (Prefix + "shutdown precedes retirement attachment and existing start for successive generations", SuccessiveOrdering)
    ];

    private static async Task Payloads()
    {
        foreach (var kind in new[] { "new", "resume", "switch", "before", "at", "clone" })
        {
            await using var f = await Fixture.Create(); var old = f.Owner.Current;
            var acknowledged = old.Session.Snapshot.Context.Ancestry.Select(e => e.WireBody.ToString()).ToArray();
            JsonData? seen = null; IExtensionContext? retainedContext = null;
            await f.Register(async (value, context, token) =>
            {
                seen = value; retainedContext = context;
                Check(ReferenceEquals(old, f.Owner.Current) && !old.Session.Snapshot.IsRetired && !old.Session.Snapshot.IsDisposed &&
                    !old.LifetimeToken.IsCancellationRequested, "Shutdown followed retirement or publication.");
                var view = ((IExtensionSessionContext)context).SessionSnapshot!;
                Check(view.Generation == old.Generation && view.SessionId == "source" && view.SelectedLeafId == "a1" &&
                    view.Persistence == ExtensionSessionPersistence.DurableLocalFile &&
                    view.BranchEntries.Select(e => e.ToString()).SequenceEqual(acknowledged), "Shutdown read the target or altered acknowledged source.");
                Check(token.CanBeCanceled && token == context.OperationCancellationToken && !token.IsCancellationRequested &&
                    !context.SessionCancellationToken.CanBeCanceled && context is not IExtensionSessionActionsContext &&
                    context is not IExtensionToolContext && context is not IExtensionCommandContext &&
                    context is not IExtensionSessionCatalogContext && context is not IExtensionSessionContextEditContext &&
                    context is not IExtensionSessionCompactionContext && context is not IExtensionToolActivationContext,
                    "Shutdown regained interactive authority or borrowed attachment cancellation.");
                var ui = ((IExtensionUiContext)context).Ui;
                Check(ui.Capabilities.Features.IsEmpty && (await ui.PublishAsync(new ExtensionUiTitle("unavailable"))).Kind == ExtensionUiOutcomeKind.Unavailable,
                    "Shutdown reopened UI authority.");
                await Throws<InvalidOperationException>(() => f.Owner.SwitchAsync(old, new(f.Other)));
            });
            f.Install(); var result = await f.Replace(kind) ?? throw new InvalidOperationException("Replacement vetoed.");
            var payload = seen ?? throw new InvalidOperationException("Missing shutdown notification.");
            Check(payload.Value.EnumerateObject().Select(p => p.Name).SequenceEqual(["type", "reason", "targetSessionFile"]) &&
                payload.Value.GetProperty("type").GetString() == "session_shutdown" &&
                payload.Value.GetProperty("reason").GetString() == (kind is "switch" or "resume" ? "resume" : kind == "new" ? "new" : "fork") &&
                payload.Value.GetProperty("targetSessionFile").GetString() == result.Current.Session.SessionFile,
                "Shutdown reason or actual staged target differs.");
            Check(retainedContext!.OperationCancellationToken.IsCancellationRequested && old.Session.Snapshot.IsRetired &&
                result.Current.Generation == 2 && f.Diagnostics.Count == 0 && f.Script.Calls == 0, "Original lifetime or transition did not settle.");
        }
    }

    private static async Task Exclusions()
    {
        await using var f = await Fixture.Create(); var calls = 0; var old = f.Owner.Current;
        await f.Register((_, _, _) => { calls++; return ValueTask.CompletedTask; }); f.Install();
        f.Owner.BeforeCreation = (_, _, _) => ValueTask.FromResult(false);
        Check(await f.Replace("new") is null, "Creation veto ignored."); f.Owner.BeforeCreation = null;
        f.Owner.BeforeReplacement = (_, _, _) => ValueTask.FromResult(false);
        Check(await f.Replace("resume") is null, "Resume veto ignored."); f.Owner.BeforeReplacement = null;
        f.Owner.ValidateTargetAttachment = (_, _, _) => throw new IOException("target refusal");
        await Throws<IOException>(() => f.Replace("new")); f.Owner.ValidateTargetAttachment = null;
        await Throws<SessionLogStoreException>(() => f.Owner.SwitchAsync(old, new(Path.Combine(f.Root, "missing.jsonl"))));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => f.Owner.SwitchAsync(old, new(f.Other), cancellationToken: canceled.Token));
        Check(calls == 0 && f.Diagnostics.Count == 0 && ReferenceEquals(old, f.Owner.Current) &&
            !old.Session.Snapshot.IsRetired && !old.LifetimeToken.IsCancellationRequested, "Rejected transition notified or retired source.");
        await old.Session.PromptAsync(User("source still usable"));
    }

    private static async Task HeldCompletion()
    {
        await using var f = await Fixture.Create(); using var caller = new CancellationTokenSource();
        var old = f.Owner.Current; var entered = Gate(); var release = Gate(); var joined = false;
        IExtensionContext? retained = null;
        await f.Register(async (_, context, _) => { retained = context; entered.TrySetResult(); await release.Task; joined = true; }); f.Install();
        Task<AgentSessionReplacement?>? original = null; Task? close = null; Task? registryClose = null;
        try
        {
            original = f.Owner.SwitchAsync(old, new(f.Other), cancellationToken: caller.Token);
            Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted, "Notification detached original transition.");
            caller.Cancel(); close = f.Owner.DisposeAsync().AsTask(); registryClose = f.Registry.DisposeAsync().AsTask();
            Check(!original.IsCompleted && !close.IsCompleted && !registryClose.IsCompleted &&
                !old.Session.Snapshot.IsRetired && !old.Session.Snapshot.IsDisposed &&
                !retained!.OperationCancellationToken.IsCancellationRequested, "Held shutdown lost lifetime or resource ownership.");
            release.TrySetResult(); var result = await original; await close; await registryClose;
            Check(joined && result is not null && old.Session.Snapshot.IsDisposed && result.Current.Session.Snapshot.IsDisposed &&
                retained!.OperationCancellationToken.IsCancellationRequested, "Admitted replacement or original cleanup was abandoned.");
        }
        finally
        {
            release.TrySetResult();
            try { if (original is not null) await original; }
            finally { try { if (close is not null) await close; } finally { if (registryClose is not null) await registryClose; } }
        }
    }

    private static async Task Failures()
    {
        await using (var f = await Fixture.Create())
        {
            var order = new List<int>();
            await f.Registry.ActivateAsync("errors", new Plugin(api =>
            {
                api.Observe(new("first", "session_shutdown", (_, _, _) => { order.Add(1); throw new IOException("handler failure"); }));
                api.Observe(new("second", "session_shutdown", (_, _, _) => { order.Add(2); return ValueTask.CompletedTask; }));
            })); f.Install(); var result = await f.Replace("new");
            Check(result is not null && order.SequenceEqual([1, 2]) && f.Diagnostics.Count == 1 &&
                f.Diagnostics[0].RegistrationId == "first" && f.Session.Snapshot.Fault is null, "Handler failure stopped notification or transition.");
        }
        await using (var f = await Fixture.Create())
        {
            var original = new IOException("original trusted reporter failure"); var later = 0; var old = f.Owner.Current;
            await f.Registry.ActivateAsync("report", new Plugin(api =>
            {
                api.Observe(new("first", "session_shutdown", (_, _, _) => throw new IOException("handler failure")));
                api.Observe(new("later", "session_shutdown", (_, _, _) => { later++; return ValueTask.CompletedTask; }));
            })); f.Report = (_, _) => throw original; f.Install();
            var actual = await Throws<IOException>(() => f.Replace("new"));
            Check(ReferenceEquals(actual, original) && later == 0 && ReferenceEquals(old, f.Owner.Current) &&
                !old.Session.Snapshot.IsRetired && !old.LifetimeToken.IsCancellationRequested &&
                Directory.GetFiles(f.Root, "*.jsonl").Length == 2, "Reporter failure lost identity, continued delivery or retained staging.");
            await old.Session.PromptAsync(User("source survives reporter"));
        }
    }

    private static async Task SuccessiveOrdering()
    {
        await using var f = await Fixture.Create(); var events = new List<string>();
        await f.Register((_, context, _) => { events.Add("shutdown:" + ((IExtensionSessionContext)context).SessionSnapshot!.Generation); return ValueTask.CompletedTask; });
        await f.Registry.ActivateAsync("start", new Plugin(api => api.Observe(new("start", "session_start", (_, context, _) =>
        { events.Add("start:" + ((IExtensionSessionContext)context).SessionSnapshot!.Generation); return ValueTask.CompletedTask; }))));
        f.Install(); var start = new NativeReplacementSessionStartBinding(f.Registry, f.Registry.CaptureSnapshot());
        f.Owner.AttachmentChanged = result =>
        {
            Check(result.Previous.Session.Snapshot.IsRetired && result.Previous.LifetimeToken.IsCancellationRequested,
                "Attachment publication preceded source retirement.");
            events.Add("attachment:" + result.Current.Generation); return ValueTask.CompletedTask;
        };
        f.Owner.AfterReplacement = result => start.PublishAsync(f.Owner, result);
        await f.Replace("switch"); await f.Replace("switch-source");
        Check(events.SequenceEqual(["shutdown:1", "attachment:2", "start:2", "shutdown:2", "attachment:3", "start:3"]) &&
            f.Session.Snapshot.Fault is null && f.Script.Calls == 0, "Notifications were reordered, suppressed or retargeted.");
    }

    // Fixture support follows the existing framework-only replacement lifecycle harness.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-replacement-shutdown-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal ReplaceableAgentSession Owner = null!; internal PersistentAgentSession Session => Owner.Current.Session;
        internal ExtensionRegistry Registry = null!; internal Script Script = new();
        internal List<ExtensionEventDiagnostic> Diagnostics = []; internal Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? Report;
        internal NativeSessionShutdownBinding? Shutdown;
        private SessionRuntimeRegistry runtime = null!; private int id, sessionId; private long ticks = 1711929600000;
        internal long Clock() => Interlocked.Increment(ref ticks);
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root); f.Script.Clock = f.Clock;
            f.runtime = new([new(Model, f.Script)], [], new NoPolicy());
            try
            {
                foreach (var path in new[] { f.Source, f.Other })
                {
                    var header = Codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path), timestamp = "2024-04-01T00:00:00.000Z", cwd = f.Root }));
                    await using var store = await SessionLogStore.CreateNewAsync(path, header);
                    await store.AppendAsync([Entry("u0", null, User(new string('x', 8000))), Entry("a0", "u0", Assistant()),
                        Entry("u1", "a0", User(new string('y', 8000))), Entry("a1", "u1", Assistant())]);
                }
                var lifecycle = new PersistentSessionLifecycle(f.runtime, f.Clock, () => "entry-" + Interlocked.Increment(ref f.id),
                    nextSessionId: () => "created-" + Interlocked.Increment(ref f.sessionId), catalog: new SessionCatalog([new("fixtures", f.Root)]));
                var initial = await lifecycle.OpenAsync(new(f.Source), Model); f.Owner = lifecycle.Attach(initial);
                var views = new NativeSessionSnapshotProvider(); views.Attach(f.Owner); f.Registry = new(null, null, views);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Task Register(ExtensionObservationCallback callback) => Registry.ActivateAsync("observations", new Plugin(api =>
        { api.Observe(new("observe-shutdown", "session_shutdown", callback)); }));
        internal void Install()
        {
            var snapshot = Registry.CaptureSnapshot(); var report = Report ?? ((diagnostic, _) => { Diagnostics.Add(diagnostic); return ValueTask.CompletedTask; });
            Shutdown = new(Registry, snapshot, report);
            Owner.BeforeRetirement = Shutdown.PublishAsync;
        }
        internal async Task<AgentSessionReplacement?> Replace(string kind)
        {
            var attached = Owner.Current;
            if (kind == "resume")
            {
                var page = await Owner.ListSessionsAsync(attached, new());
                return await Owner.ResumeAsync(attached, new(page.Items.Single(item => item.Path == Other).Key));
            }
            return kind switch
            {
                "new" => await Owner.CreateAsync(attached, new(AgentSessionCreationKind.New)),
                "before" => await Owner.CreateAsync(attached, new(AgentSessionCreationKind.ForkBefore, "u1")),
                "at" => await Owner.CreateAsync(attached, new(AgentSessionCreationKind.ForkAt, "u1")),
                "clone" => await Owner.CreateAsync(attached, new(AgentSessionCreationKind.Clone)),
                _ => await Owner.SwitchAsync(attached, new(kind == "switch-source" ? Source : Other))
            };
        }
        public async ValueTask DisposeAsync()
        {
            // File deletion follows successful ownership closure, never a failed/unsettled close.
            try { if (Registry is not null) await Registry.DisposeAsync(); }
            finally { if (Owner is not null) await Owner.DisposeAsync(); }
            Check(Path.GetDirectoryName(Path.GetFullPath(Root)) == parent && Path.GetFileName(Root).StartsWith("PiSharp-replacement-shutdown-", StringComparison.Ordinal), "Invalid owned fixture root.");
            if (Directory.Exists(Root))
            {
                Check((File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "Linked fixture root rejected.");
                Directory.Delete(Root, recursive: true);
            }
        }
    }
    private sealed class Plugin(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { token.ThrowIfCancellationRequested(); initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Script : IChatTransport
    {
        internal Func<long> Clock = () => 0; internal int Calls, Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls++; var final = Message() with { Timestamp = Clock() };
            try
            {
                await Task.CompletedTask; yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
                yield return new TextStarted(0, new("")); yield return new TextEnded(0, ((TextContent)final.Content.Single()).Text); yield return new StreamDone(final.StopReason, final);
            }
            finally { Cleanups++; }
        }
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Observation acquired a tool"); }
    private static SessionEntry Entry(string id, string? parent, TranscriptEntry message) => Codec.Parse(JsonSerializer.Serialize(new
    { type = "message", id, parentId = parent, timestamp = "2024-04-01T00:00:00.000Z", message = message.WireBody.Value }));
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 10 })));
    private static TranscriptEntry Assistant() => new("assistant", PiWireJson.WriteMessage(Message() with { Timestamp = 10 }));
    private static AssistantMessage Message() => new(Model.Api, Model.Provider, Model.Id, 0, [new TextContent("fake completed text")], TokenUsage.Zero, StopReason.Stop);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
