using System.Collections.Immutable;
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
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class ReplacementSessionStartTests
{
    internal const string Prefix = "replacement session-start ";
    private static readonly ModelDescriptor Model = new("replace-start", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new();
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "actual switch resume new fork-before and fork-at publish exact previous path and fresh context", Payloads),
        (Prefix + "veto failed staging and precanceled operations never publish or retire source", Exclusions),
        (Prefix + "captured order survives removal addition foreign cancellation and throwing diagnostics", HandlerSnapshot),
        (Prefix + "held postcommit callback delays original replacement owner and registry disposal", HeldCompletion),
        (Prefix + "original context cleanup failure reports and continues to the next observer", CleanupFailure),
        (Prefix + "held failing diagnostic joins original replacement and all disposal leases", HeldDiagnostic),
        (Prefix + "new context rejects reentrant owner operations self-disposal and retained authority", ContextGuards),
        (Prefix + "delivery refusal after commit leaves new writer usable and stale binding never retargets", DeliveryRefusal),
        (Prefix + "attachment hook precedes publication and replacement compaction binding is installed", OrderingAndCompaction)
    ];
    private static async Task Payloads()
    {
        foreach (var kind in new[] { "switch", "resume", "new", "before", "at" })
        {
            await using var f = await Fixture.Create(); JsonData? seen = null; ExtensionSessionSnapshot? view = null;
            await f.Register((value, context, token) =>
            {
                Check(!token.CanBeCanceled, "Replacement observation received an event AbortSignal.");
                seen = value; view = ((IExtensionSessionContext)context).SessionSnapshot;
                Check(ReferenceEquals(f.Owner.Current.Session, f.Session) && f.Session.Snapshot.Fault is null, "Observation preceded committed publication.");
                return ValueTask.CompletedTask;
            }); f.Install(); var old = f.Owner.Current;
            var replacement = await f.Replace(kind) ?? throw new InvalidOperationException("Replacement vetoed.");
            var payload = seen ?? throw new InvalidOperationException("No replacement event.");
            Check(payload.Value.EnumerateObject().Select(p => p.Name).SequenceEqual(["type", "reason", "previousSessionFile"]), "Replacement payload shape differs.");
            Check(payload.Value.GetProperty("type").GetString() == "session_start" &&
                payload.Value.GetProperty("reason").GetString() == (kind is "switch" or "resume" ? "resume" : kind == "new" ? "new" : "fork") &&
                payload.Value.GetProperty("previousSessionFile").GetString() == f.Source, "Replacement source reason/path differs.");
            Check(replacement.Reason == (kind is "before" or "at" ? "fork" : kind) && ReferenceEquals(replacement.Previous, old) &&
                ReferenceEquals(replacement.Current, f.Owner.Current) && old.Session.Snapshot.IsRetired, "Event mapping changed core reason or attachment ownership.");
            Check(view is not null && view.Generation == 2 && view.SessionId == f.Session.Snapshot.Log.Header.Id &&
                view.SelectedLeafId == f.Session.Snapshot.Context.LeafId && view.BranchEntries.Length == (kind == "new" ? 0 : kind == "before" ? 2 : kind == "at" ? 3 : 4), "Callback saw wrong replacement context.");
            Check(f.Script.Calls == 0 && f.Diagnostics.Count == 0, "Replacement unexpectedly used transport or failed delivery.");
        }
    }
    private static async Task Exclusions()
    {
        await using var f = await Fixture.Create(); var calls = 0;
        await f.Register((_, _, _) => { calls++; return ValueTask.CompletedTask; }); f.Install(); var old = f.Owner.Current;
        f.Owner.BeforeReplacement = (_, _, _) => ValueTask.FromResult(false);
        Check(await f.Replace("switch") is null, "Switch veto ignored.");
        f.Owner.BeforeCreation = (_, _, _) => ValueTask.FromResult(false);
        Check(await f.Replace("new") is null, "Creation veto ignored.");
        f.Owner.BeforeReplacement = null; f.Owner.BeforeCreation = null;
        f.Owner.ValidateTargetAttachment = (_, _, _) => throw new IOException("actual staged target refusal");
        await Throws<IOException>(() => f.Replace("switch")); f.Owner.ValidateTargetAttachment = null;
        var missing = await Throws<SessionLogStoreException>(() => f.Owner.SwitchAsync(old, new(Path.Combine(f.Root, "missing.jsonl"))));
        Check(missing.Failure == SessionLogStoreFailure.OpenFailed, "Actual missing target did not fail storage admission.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => f.Owner.SwitchAsync(old, new(f.Other), cancellationToken: canceled.Token));
        Check(calls == 0 && f.Diagnostics.Count == 0 && ReferenceEquals(old, f.Owner.Current) && !old.Session.Snapshot.IsRetired && old.Session.Snapshot.Fault is null, "Excluded operation published, retired or poisoned source.");
        await f.Session.PromptAsync(User("source remains writable"));
    }
    private static async Task HandlerSnapshot()
    {
        await using var f = await Fixture.Create(); var order = new List<string>(); IExtensionRegistration? second = null; var changed = false;
        await f.Registry.ActivateAsync("ordered", new Plugin(api =>
        {
            f.Api = api;
            api.Observe(new("first", "session_start", (_, _, _) =>
            {
                order.Add("first");
                if (!changed)
                {
                    changed = true; second!.Dispose();
                    api.Observe(new("late", "session_start", (_, _, _) => { order.Add("late"); return ValueTask.CompletedTask; }));
                }
                throw new IOException("first original failure");
            }));
            second = api.Observe(new("second", "session_start", (_, _, _) =>
            { order.Add("second"); using var foreign = new CancellationTokenSource(); foreign.Cancel(); throw new OperationCanceledException(foreign.Token); }));
        }));
        await f.Registry.ActivateAsync("other-owner", new Plugin(api => api.Observe(new("third", "session_start", (_, _, token) =>
        { Check(!token.CanBeCanceled, "Third event got signal."); order.Add("third"); return ValueTask.CompletedTask; }))));
        f.Report = (diagnostic, token) => { Check(!token.CanBeCanceled, "Diagnostic got event signal."); f.Diagnostics.Add(diagnostic); throw new IOException("reporter original failure"); };
        var expectedDiagnostics = new[] { ExpectedDiagnostic(f.Registry, "ordered", "first"), ExpectedDiagnostic(f.Registry, "ordered", "second") };
        f.Install(); await f.Replace("switch");
        Check(order.SequenceEqual(["first", "second", "third"]) && f.Diagnostics.SequenceEqual(expectedDiagnostics) &&
            f.Session.Snapshot.Fault is null, "Snapshot/order/report continuation differs.");
        // Existing startup path uses generic dispatch; its error behavior is deliberately unchanged.
        await Throws<IOException>(() => f.Registry.DispatchObservationsAsync(f.Registry.CaptureSnapshot(), "session_start",
            JsonData.Parse("{\"type\":\"session_start\",\"reason\":\"startup\"}")).AsTask());
    }
    private static async Task HeldCompletion()
    {
        await using var f = await Fixture.Create(); using var canceled = new CancellationTokenSource(); var entered = Gate(); var release = Gate(); var joined = false;
        await f.Register(async (_, _, token) => { Check(!token.CanBeCanceled, "Committed callback got signal."); entered.TrySetResult(); await release.Task; joined = true; }); f.Install();
        Task<AgentSessionReplacement?>? original = null; Task? ownerClose = null; Task? registryClose = null;
        try
        {
            original = f.Owner.SwitchAsync(f.Owner.Current, new(f.Other), cancellationToken: canceled.Token);
            Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted && f.Owner.Current.Generation == 2, "Replacement settled before callback or publication.");
            canceled.Cancel(); ownerClose = f.Owner.DisposeAsync().AsTask(); registryClose = f.Registry.DisposeAsync().AsTask();
            Check(!original.IsCompleted && !ownerClose.IsCompleted && !registryClose.IsCompleted, "Original callback/transition leases were abandoned.");
            release.TrySetResult(); var replacement = await original; await ownerClose; await registryClose;
            Check(replacement is not null && joined && replacement.Current.Session.Snapshot.IsDisposed && replacement.Previous.Session.Snapshot.IsDisposed, "Original completion/physical retirement did not join.");
            Check(f.Diagnostics.Count == 0, "Successful held observer reported an unexpected callback/context cleanup failure after all original joins.");
        }
        finally
        {
            release.TrySetResult();
            try { if (original is not null) await original; }
            finally { try { if (ownerClose is not null) await ownerClose; } finally { if (registryClose is not null) await registryClose; } }
        }
    }
    private static async Task CleanupFailure()
    {
        await using var f = await Fixture.Create(); var order = new List<string>(); CancellationTokenRegistration? cleanup = null;
        await f.Registry.ActivateAsync("cleanup", new Plugin(api =>
        {
            api.Observe(new("first", "session_start", (_, context, _) =>
            {
                order.Add("first"); cleanup = context.SessionCancellationToken.Register(() => throw new IOException("original context close failure"));
                return ValueTask.CompletedTask;
            }));
            api.Observe(new("second", "session_start", (_, _, _) => { order.Add("second"); return ValueTask.CompletedTask; }));
        })); var expected = ExpectedDiagnostic(f.Registry, "cleanup", "first"); f.Install();
        try { await f.Replace("switch"); }
        finally { cleanup?.Dispose(); }
        Check(order.SequenceEqual(["first", "second"]) && f.Diagnostics.SequenceEqual(new[] { expected }) && f.Session.Snapshot.Fault is null,
            "Original context cleanup error abandoned later callback or poisoned replacement.");
        await f.Session.PromptAsync(User("writer after context close failure"));
    }
    private static async Task HeldDiagnostic()
    {
        await using var f = await Fixture.Create(); var entered = Gate(); var release = Gate(); var joined = false;
        await f.Register((_, _, _) => throw new IOException("handler original failure"));
        f.Report = async (diagnostic, token) =>
        {
            Check(!token.CanBeCanceled && diagnostic.RegistrationId == "observe-start", "Diagnostic identity/signal differs.");
            f.Diagnostics.Add(diagnostic); entered.TrySetResult(); await release.Task; joined = true; throw new IOException("reporter original failure");
        }; var expected = ExpectedDiagnostic(f.Registry, "observations", "observe-start"); f.Install();
        Task<AgentSessionReplacement?>? original = null; Task? ownerClose = null; Task? registryClose = null;
        try
        {
            original = f.Replace("switch");
            Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted, "Original replacement detached held report.");
            ownerClose = f.Owner.DisposeAsync().AsTask(); registryClose = f.Registry.DisposeAsync().AsTask();
            Check(!original.IsCompleted && !ownerClose.IsCompleted && !registryClose.IsCompleted, "Report escaped transition/admission ownership.");
            release.TrySetResult(); var replacement = await original; await ownerClose; await registryClose;
            Check(joined && replacement is not null && f.Diagnostics.SequenceEqual(new[] { expected }) && replacement.Current.Session.Snapshot.IsDisposed, "Report failure vetoed commit or original physical joins.");
        }
        finally
        {
            release.TrySetResult();
            try { if (original is not null) await original; }
            finally { try { if (ownerClose is not null) await ownerClose; } finally { if (registryClose is not null) await registryClose; } }
        }
    }
    private static async Task ContextGuards()
    {
        await using var f = await Fixture.Create(); IExtensionSessionContextEditContext? retained = null; IExtensionContext? callbackContext = null;
        Task? unexpectedDispose = null; var guardsPassed = false;
        await f.Register(async (_, context, _) =>
        {
            callbackContext = context; retained = (IExtensionSessionContextEditContext)context;
            Check(((IExtensionSessionContext)context).SessionSnapshot!.Generation == 2, "Context not bound to replacement generation.");
            await Throws<InvalidOperationException>(() => f.Owner.SwitchAsync(f.Owner.Current, new(f.Source)));
            await Throws<InvalidOperationException>(() => retained.AppendContextEditAsync("u0", JsonData.Null).AsTask());
            try { unexpectedDispose = f.Owner.DisposeAsync().AsTask(); }
            catch (InvalidOperationException) { guardsPassed = true; }
        }); f.Install(); var old = f.Owner.Current;
        try { await f.Replace("switch"); } finally { if (unexpectedDispose is not null) await unexpectedDispose; }
        Check(guardsPassed && unexpectedDispose is null && f.Diagnostics.Count == 0, "Reentrant guards or owner self-disposal contract failed.");
        var before = f.Session.Snapshot.Log.Entries.Length;
        var error = await Throws<OperationCanceledException>(() => (retained ?? throw new InvalidOperationException()).AppendContextEditAsync("u0", JsonData.Null).AsTask());
        Check(callbackContext is not null && error.CancellationToken.CanBeCanceled && error.CancellationToken.IsCancellationRequested &&
            (error.CancellationToken == callbackContext.SessionCancellationToken || error.CancellationToken == callbackContext.OperationCancellationToken ||
             error.CancellationToken == callbackContext.ExtensionLifetimeCancellationToken), "Retained capability cancellation lacks owned provenance.");
        await Throws<InvalidOperationException>(() => f.Owner.SwitchAsync(old, new(f.Source)));
        Check(f.Session.Snapshot.Log.Entries.Length == before && f.Session.Snapshot.Fault is null && f.Owner.Current.Generation == 2, "Retained/retired authority reached fresh writer.");
    }
    private static async Task DeliveryRefusal()
    {
        await using var f = await Fixture.Create(); var calls = 0;
        await f.Register((_, _, _) => { calls++; return ValueTask.CompletedTask; }); f.Install();
        await f.Registry.DisposeAsync(); var replacement = (await f.Replace("switch"))!;
        var firstExpected = new ExtensionEventDiagnostic("session_start", "native-host", replacement.Current.Generation, "publish", ExtensionEventFailure.HandlerFailed);
        Check(calls == 0 && f.Diagnostics.SequenceEqual(new[] { firstExpected }) && ReferenceEquals(f.Owner.Current, replacement.Current) && f.Session.Snapshot.Fault is null, "Admission refusal undid replacement or poisoned fresh writer.");
        await f.Session.PromptAsync(User("fresh writer survives delivery refusal"));
        var second = (await f.Replace("switch-source"))!;
        var secondExpected = new ExtensionEventDiagnostic("session_start", "native-host", second.Current.Generation, "publish", ExtensionEventFailure.HandlerFailed);
        Check(f.Diagnostics.SequenceEqual(new[] { firstExpected, secondExpected }), "Second intentional refusal produced unexpected diagnostics.");
        await f.Start!.PublishAsync(f.Owner, replacement);
        Check(f.Diagnostics.SequenceEqual(new[] { firstExpected, secondExpected, firstExpected }) &&
            ReferenceEquals(f.Owner.Current, second.Current) && calls == 0, "Stale binding retargeted fresh attachment.");
    }
    private static async Task OrderingAndCompaction()
    {
        await using var f = await Fixture.Create(); var order = new List<string>();
        await f.Registry.ActivateAsync("both", new Plugin(api =>
        {
            api.Observe(new("start", "session_start", (_, context, _) =>
            { order.Add("start"); Check(((IExtensionSessionContext)context).SessionSnapshot!.Generation == 2, "Wrong start context."); return ValueTask.CompletedTask; }));
            api.Observe(new("compact", "session_compact", (_, context, _) =>
            { order.Add("compact"); Check(((IExtensionSessionContext)context).SessionSnapshot!.Generation == 2, "Compaction binding not replaced."); return ValueTask.CompletedTask; }));
        })); f.Install();
        f.Owner.AttachmentChanged = replacement => { order.Add("attachment"); Check(ReferenceEquals(f.Owner.Current, replacement.Current), "Attachment hook preceded commit."); return ValueTask.CompletedTask; };
        await f.Replace("switch");
        await f.Owner.CompactAsync(f.Owner.Current, new(new(true, 128, 0), ExtensionSummary: new("replacement summary")), new Generator());
        Check(order.SequenceEqual(["attachment", "start", "compact"]) && f.Diagnostics.Count == 0, "Existing hooks or replacement compaction wiring changed.");
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-replacement-start-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal ReplaceableAgentSession Owner = null!; internal PersistentAgentSession Session => Owner.Current.Session;
        internal ExtensionRegistry Registry = null!; internal IExtensionRegistry? Api; internal Script Script = new();
        internal List<ExtensionEventDiagnostic> Diagnostics = []; internal Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? Report;
        internal NativeReplacementSessionStartBinding? Start;
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
        { Api = api; api.Observe(new("observe-start", "session_start", callback)); }));
        internal void Install()
        {
            var snapshot = Registry.CaptureSnapshot(); var report = Report ?? ((diagnostic, _) => { Diagnostics.Add(diagnostic); return ValueTask.CompletedTask; });
            var compaction = new NativeSessionCompactionObservationBinding(Registry, snapshot, report);
            compaction.Attach(Owner, Owner.Current); Start = new(Registry, snapshot, report);
            Owner.AfterReplacement = async replacement => { compaction.Attach(Owner, replacement.Current); await Start.PublishAsync(Owner, replacement); };
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
                _ => await Owner.SwitchAsync(attached, new(kind == "switch-source" ? Source : Other))
            };
        }
        public async ValueTask DisposeAsync()
        {
            // File deletion follows successful ownership closure, never a failed/unsettled close.
            try { if (Registry is not null) await Registry.DisposeAsync(); }
            finally { if (Owner is not null) await Owner.DisposeAsync(); }
            Check(Path.GetDirectoryName(Path.GetFullPath(Root)) == parent && Path.GetFileName(Root).StartsWith("PiSharp-replacement-start-", StringComparison.Ordinal), "Invalid owned fixture root.");
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
    private sealed class Generator : ISessionSummaryGenerator
    {
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new SessionGeneratedSummary("generated summary", TokenUsage.Zero)); }
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
    private static ExtensionEventDiagnostic ExpectedDiagnostic(ExtensionRegistry registry, string ownerId, string registrationId)
    {
        var registered = registry.CaptureSnapshot().Registrations.Single(item => item.OwnerId == ownerId && item.RegistrationId == registrationId);
        return new("session_start", registered.OwnerId, registered.OwnerGeneration, registered.RegistrationId, ExtensionEventFailure.HandlerFailed);
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
