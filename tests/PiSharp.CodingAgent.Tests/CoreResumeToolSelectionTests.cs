using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Tools;

internal static class CoreResumeToolSelectionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (StartupToolSelectionTests.Prefix + "core initial read and empty precede unavailable restored binding", Initial),
        (StartupToolSelectionTests.Prefix + "core absent unknown and retained mismatched declaration fail without mutation", Rejection),
        (StartupToolSelectionTests.Prefix + "core held failed initial reporter joins runtime before release without append", Reporter),
        (StartupToolSelectionTests.Prefix + "core lifetime cap survives owner bind and later durable logical activation", Activation),
        (StartupToolSelectionTests.Prefix + "core lifetime cap filters refreshed replacement catalog and rejects widening factory", Replacement),
        (StartupToolSelectionTests.Prefix + "core unrestricted factory projection retains default extensions and incoming registrations", UnrestrictedExtensions),
        (StartupToolSelectionTests.Prefix + "core factory projection preserves exact empty and named caps", ExactExtensionCaps),
        (StartupToolSelectionTests.Prefix + "core legacy explicit exposure activation and reopen preserve every tool origin", LegacyExposureActivation),
        (StartupToolSelectionTests.Prefix + "core explicit script activation respects retained named and empty caps", CappedExposureActivation),
        (StartupToolSelectionTests.Prefix + "core actual initial open commits only effective declarable names under every cap and origin", ActualInitialExposureOpen)
    ];
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<Exception> Failure(Task task)
    { try { await task; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected original failure."); }
    private static void Names(PersistentAgentSession session, params string[] names) =>
        Check(session.GetActiveTools().SequenceEqual(names, StringComparer.Ordinal), "Unexpected actual active selection.");
    private static async Task Initial()
    {
        foreach (var initial in new ImmutableArray<string>[] { ["read"], [] })
        {
            using var f = new StartupSettingsTests.Fixture(); await using var profile = await Profile(f);
            var path = Path.Combine(f.Root, "old.jsonl"); await Seed(path, f.Root, "legacy"); var before = await Bytes(path);
            var registry = Registry(profile, f.Root, new() { InitialActiveToolNames = initial }); var ids = 0;
            await using var session = await PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 1, () => "effective-" + ++ids, fallbackModel: profile.SelectedModel);
            Names(session, initial.ToArray()); Check(session.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(initial), "Effective selection was not installed in actual scheduler.");
            var after = await Bytes(path); Check(after.Length > before.Length && after.Take(before.Length).SequenceEqual(before), "Initial selection rewrote historical bytes or skipped its checkpoint.");
            Check(profile.UsedTurns == 0 && profile.Actions.Length == 0, "Resume selection inferred or executed a tool.");
        }
    }
    private static async Task Rejection()
    {
        foreach (var kind in new[] { "absent", "unknown", "mismatch" })
        {
            using var f = new StartupSettingsTests.Fixture(); await using var profile = await Profile(f);
            var path = Path.Combine(f.Root, "old.jsonl"); await Seed(path, f.Root, kind == "mismatch" ? "read" : "legacy"); var before = await Bytes(path);
            ImmutableArray<string>? initial = kind == "absent" ? null : kind == "unknown" ? ["missing"] : ["read"];
            var registry = Registry(profile, f.Root, new() { InitialActiveToolNames = initial }); var ids = 0;
            var error = await Failure(PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 1, () => "reject-" + ++ids, fallbackModel: profile.SelectedModel));
            Check(error is SessionRuntimeRegistryException binding && binding.Failure == (kind == "mismatch" ? SessionRuntimeRegistryFailure.DeclarationMismatch : SessionRuntimeRegistryFailure.UnknownTool), "Wrong binding rejection.");
            var after = await Bytes(path); Check(before.SequenceEqual(after), "Rejected effective binding changed durable bytes.");
            await using var reopened = await SessionLogStore.OpenAsync(path);
        }
    }
    private static async Task Reporter()
    {
        using var f = new StartupSettingsTests.Fixture(); await using var profile = await Profile(f);
        var path = Path.Combine(f.Root, "old.jsonl"); await Seed(path, f.Root, "legacy"); var before = await Bytes(path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = new InvalidOperationException("original effective read preparation"); var reporter = new IOException("original effective read reporter");
        var captures = new List<Exception>(); var resources = new Resources(); var ids = 0; using var cancellation = new CancellationTokenSource();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = Registry(profile, f.Root, new() { InitialActiveToolNames = ["read"], ReportLoadoutDiagnostic = (_, error) => captures.Add(error),
            DrainLoadoutDiagnostics = async token => { using var registration = token.UnsafeRegister(_ => canceled.TrySetResult(), null); entered.TrySetResult(); await release.Task; throw reporter; } }, preparation);
        var original = PersistentAgentSession.OpenWithRuntimeFactoryAsync(path, (_, _) => ValueTask.FromResult(new SessionRuntimeLease(registry, resources)),
            () => 1, () => "held-" + ++ids, fallbackModel: profile.SelectedModel, cancellationToken: cancellation.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel(); await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!original.IsCompleted && !resources.Disposed, "Cancellation detached the original initial reporter before runtime cleanup."); }
        finally { release.TrySetResult(); await Failure(original); }
        Check(ReferenceEquals(await Failure(original), reporter) && captures.Count == 1 && ReferenceEquals(captures[0], preparation) && resources.Disposed,
            "Initial admission replaced original failures or failed runtime release after reporter join.");
        var after = await Bytes(path); Check(before.SequenceEqual(after), "Failed reporter admitted an initial loadout checkpoint.");
        await using var reopened = await SessionLogStore.OpenAsync(path);
    }
    private static async Task Activation()
    {
        using var f = new StartupSettingsTests.Fixture(); await using var profile = await Profile(f);
        var path = Path.Combine(f.Root, "old.jsonl"); await Seed(path, f.Root, "legacy"); var policy = AllowedToolSelection.Create(["read"]);
        var registry = Registry(profile, f.Root, new() { InitialActiveToolNames = ["read"], LifetimeToolSelection = policy, BindNestedCallsToSessionOwner = true }); var ids = 0;
        var session = await PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 1, () => "cap-" + ++ids, fallbackModel: profile.SelectedModel);
        await using var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("No replacement expected."));
        var log = session.Snapshot.Log; var before = await Bytes(path);
        foreach (var activation in new Func<Task>[] { () => session.SetActiveToolsAsync(["write"]), () => Task.FromResult(session.ScheduleToolActivation(["write"])) })
        {
            var error = await Failure(Call(activation)); Check(error is SessionRuntimeRegistryException { Failure: SessionRuntimeRegistryFailure.UnknownTool }, "Lifetime activation widened cap.");
            Names(session, "read"); Check(session.Snapshot.Log.Sequence == log.Sequence && session.Snapshot.Fault is null, "Denied activation changed acknowledged live state.");
        }
        var after = await Bytes(path); Check(before.SequenceEqual(after), "Denied activation durably changed selection.");
        await session.SetActiveToolsAsync([]); await session.SetActiveToolsAsync(["read"]); Names(session, "read");
        foreach (var exposure in new[] { ToolExposure.Hidden, ToolExposure.Codemode, ToolExposure.Deferred })
        {
            var selected = Registry(profile, f.Root, new() { InitialActiveToolNames = ["read"] }, exposure: exposure);
            Check(selected.Resolve(profile.SelectedModel, [], initialActiveToolNames: ["read"]).Configuration.Tools.IsEmpty,
                "Hidden/script-only catalog entry became model-active.");
        }
    }
    private static async Task Call(Func<Task> operation) => await operation();
    private static async Task Replacement()
    {
        using var f = new StartupSettingsTests.Fixture(); await using var profile = await Profile(f);
        var first = Path.Combine(f.Root, "first.jsonl"); var second = Path.Combine(f.Root, "second.jsonl"); await Seed(first, f.Root, "legacy"); await Seed(second, f.Root, "legacy");
        var cap = AllowedToolSelection.Create(["read"]); var root = Registry(profile, f.Root, new() { LifetimeToolSelection = cap, InitialActiveToolNames = ["read"] }); var ids = 0;
        var lifecycle = new PersistentSessionLifecycle(root, () => 1, () => "refresh-" + ++ids,
            registryForWorkingDirectory: _ => Registry(profile, f.Root, new()));
        var initial = await lifecycle.OpenAsync(new(first), profile.SelectedModel); await using var owner = lifecycle.Attach(initial); var prior = owner.Current;
        var replacement = await owner.SwitchAsync(prior, new(second)); Check(replacement is not null && owner.Current.Generation == prior.Generation + 1, "Actual refreshed catalog replacement failed.");
        Names(owner.Current.Session, "read"); var state = owner.Current.Session.Snapshot; var before = await Bytes(second);
        Check(await Failure(owner.Current.Session.SetActiveToolsAsync(["write"])) is SessionRuntimeRegistryException { Failure: SessionRuntimeRegistryFailure.UnknownTool }, "Refreshed registry discarded retained cap.");
        var after = await Bytes(second); Check(before.SequenceEqual(after) && state.Log.Sequence == owner.Current.Session.Snapshot.Log.Sequence, "Refreshed denied activation changed destination.");
        var third = Path.Combine(f.Root, "third.jsonl"); await Seed(third, f.Root, "legacy");
        // A custom factory is required to carry the exact immutable policy before opening, rather than widen after old retirement.
        await using var otherOwner = new ReplaceableAgentSession(await lifecycle.OpenAsync(new(third), profile.SelectedModel), async (request, token) =>
            await PersistentAgentSession.OpenWithRegistryAsync(request.Path, Registry(profile, f.Root, new() { InitialActiveToolNames = [] }), () => 1,
                () => "bad-" + ++ids, fallbackModel: profile.SelectedModel, cancellationToken: token));
        var fourth = Path.Combine(f.Root, "fourth.jsonl"); await Seed(fourth, f.Root, "legacy"); var retained = otherOwner.Current;
        Check(await Failure(otherOwner.SwitchAsync(retained, new(fourth))) is InvalidOperationException && ReferenceEquals(retained, otherOwner.Current) && !retained.Session.Snapshot.IsRetired,
            "Widening custom factory retired or published past the retained-policy guard.");
    }
    private static async Task UnrestrictedExtensions()
    {
        await ExtensionFactoryProjection(AllowedToolSelection.Create(noTools: NoToolsMode.Builtin), ["extension", "model", "later"]);
        await ExtensionFactoryProjection(AllowedToolSelection.Create(excluded: ["read", "excluded"], configuredDefaults: []),
            ["extension", "model", "later"]);
    }
    private static async Task ExactExtensionCaps()
    {
        await ExtensionFactoryProjection(AllowedToolSelection.Create([]), []);
        await ExtensionFactoryProjection(AllowedToolSelection.Create(noTools: NoToolsMode.All), []);
        await ExtensionFactoryProjection(AllowedToolSelection.Create(["read"]), ["read"]);
    }
    private static async Task ExtensionFactoryProjection(AllowedToolSelection policy, ImmutableArray<string> expected)
    {
        using var f = new StartupSettingsTests.Fixture(); await using var profile = await Profile(f);
        var first = Path.Combine(f.Root, "first.jsonl"); var second = Path.Combine(f.Root, "second.jsonl");
        await Seed(first, f.Root, "legacy"); await Seed(second, f.Root, "legacy");
        var beforeFirst = await Bytes(first); var beforeSecond = await Bytes(second); var ids = 0; var factories = 0;
        var rootCatalog = ExtensionCatalog(includeLater: false);
        var root = ExtensionRegistry(profile, rootCatalog, new() { LifetimeToolSelection = policy,
            InitialActiveToolNames = policy.SelectInitial(rootCatalog.Select(tool => new ToolSelectionDescriptor(tool.Adapter.Name,
                tool.Exposure, tool.DefaultActive, tool.IsExtension)).ToImmutableArray()), BindNestedCallsToSessionOwner = true });
        var lifecycle = new PersistentSessionLifecycle(root, () => 1, () => "extension-state-" + ++ids,
            registryForWorkingDirectory: _ =>
            {
                factories++;
                // A fresh incoming catalog adds a default extension absent from the retained root projection.
                return ExtensionRegistry(profile, ExtensionCatalog(includeLater: true), new() { BindNestedCallsToSessionOwner = true });
            });
        var initial = await lifecycle.OpenAsync(new(first), profile.SelectedModel);
        await using var owner = lifecycle.Attach(initial);
        Names(initial, expected.ToArray());
        Check(initial.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(expected, StringComparer.Ordinal),
            "Fresh factory lost default extension projection or promoted builtin/script-only metadata.");
        var openedFirst = await Bytes(first);
        Check(openedFirst.Length > beforeFirst.Length && openedFirst.Take(beforeFirst.Length).SequenceEqual(beforeFirst),
            "Factory initial checkpoint rewrote source history.");
        var retained = owner.Current; var replacement = await owner.SwitchAsync(retained, new(second));
        Check(replacement is not null && factories == 2 && owner.Current.Generation == retained.Generation + 1,
            "Actual lifecycle factory replacement was not published once with retained policy.");
        Names(owner.Current.Session, expected.ToArray());
        Check(owner.Current.Session.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(expected, StringComparer.Ordinal),
            "Replacement catalog projection lost declarable defaults or escaped exact cap.");
        var afterFirst = await Bytes(first); var afterSecond = await Bytes(second);
        Check(openedFirst.SequenceEqual(afterFirst) && afterSecond.Length > beforeSecond.Length && afterSecond.Take(beforeSecond.Length).SequenceEqual(beforeSecond),
            "Replacement changed retired source or rewrote target historical bytes.");
        Check(profile.UsedTurns == 0 && profile.Actions.Length == 0, "Projection executed a tool or provider call.");
    }
    private static ImmutableArray<SessionRegisteredTool> ExtensionCatalog(bool includeLater)
    {
        var catalog = ImmutableArray.Create(
            CatalogTool("read", isExtension: false),
            CatalogTool("extension", isExtension: true),
            CatalogTool("model", isExtension: true, exposure: ToolExposure.ModelOnly),
            CatalogTool("excluded", isExtension: true, defaultActive: false),
            CatalogTool("disabled", isExtension: true, defaultActive: false),
            CatalogTool("script", isExtension: true, exposure: ToolExposure.Codemode),
            CatalogTool("deferred", isExtension: true, exposure: ToolExposure.Deferred),
            CatalogTool("hidden", isExtension: true, exposure: ToolExposure.Hidden));
        return includeLater ? catalog.Add(CatalogTool("later", isExtension: true)) : catalog;
    }
    private static SessionRegisteredTool CatalogTool(string name, bool isExtension, bool defaultActive = true, ToolExposure exposure = ToolExposure.Direct)
        => new(JsonData.Parse(JsonSerializer.Serialize(new { name, description = "current " + name, parameters = new { type = "object" } })), new NeverExecute(name))
            { IsExtension = isExtension, DefaultActive = defaultActive, Exposure = exposure };
    private static SessionRuntimeRegistry ExtensionRegistry(OfflineSessionProfile profile, ImmutableArray<SessionRegisteredTool> catalog,
        SessionRuntimeRegistryOptions options) => new([new(profile.SelectedModel, profile.Registry.Resolve(profile.SelectedModel, []).Configuration.Transport)],
            catalog, new Deny(), options);
    private sealed class NeverExecute(string name) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => throw new InvalidOperationException("No extension execution expected.");
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("No extension validation expected.");
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("No extension effect expected.");
    }
    private static async Task LegacyExposureActivation()
    {
        foreach (var origin in new[] { false, true })
        {
            using var f = new StartupSettingsTests.Fixture(); await using var profile = await Profile(f);
            var path = Path.Combine(f.Root, "legacy-exposure.jsonl"); var ids = 0;
            var registry = ExposureRegistry(profile, origin, lifetime: null);
            var session = await PersistentAgentSession.CreateAsync(path, ExposureHeader(path, f.Root), registry, profile.SelectedModel, () => 1, () => "legacy-" + ++ids);
            await using (var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("No replacement expected.")))
            {
                await session.SetActiveToolsAsync(["hidden", "missing", "direct", "model", "code", "deferred", "code"]);
                Names(session, "direct", "model", "code", "deferred");
                Check(session.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(new[] { "direct", "model", "code", "deferred" }),
                    "Legacy explicit activation lost Codemode/Deferred for builtin-SDK or extension origin.");
                var sequence = session.Snapshot.Log.Sequence;
                await session.SetActiveToolsAsync(["direct", "model", "code", "deferred"]);
                Check(session.Snapshot.Log.Sequence == sequence, "Exposure-preserving no-op activation appended a duplicate checkpoint.");
                var logical = session.ScheduleToolActivation(["code", "deferred"]);
                Check(logical.Names.SequenceEqual(new[] { "code", "deferred" }), "Callback logical activation dropped callable script tools.");
                await session.SetActiveToolsAsync(["code", "deferred"]);
                Names(session, "code", "deferred");
            }
            var before = await Bytes(path);
            await using (var reopened = await PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 1, () => "reopen-" + ++ids, fallbackModel: profile.SelectedModel))
            {
                Names(reopened, "code", "deferred");
                Check(reopened.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(new[] { "code", "deferred" }),
                    "Ordinary recorded replay rejected legacy script exposure declarations.");
                var after = await Bytes(path); Check(before.SequenceEqual(after), "Ordinary exposure replay mutated durable source.");
            }
            Check(profile.UsedTurns == 0 && profile.Actions.Length == 0, "Exposure selection acquired execution effects.");
        }
    }
    private static async Task CappedExposureActivation()
    {
        foreach (var origin in new[] { false, true })
        {
            using var f = new StartupSettingsTests.Fixture(); await using var profile = await Profile(f); var ids = 0;
            var policy = AllowedToolSelection.Create(["code", "deferred", "hidden"]);
            var registry = ExposureRegistry(profile, origin, policy);
            // New initial projection still omits script-only/hidden entries; later explicit activation is a separate operation.
            Check(registry.Resolve(profile.SelectedModel, [], initialActiveToolNames: policy.InitialNames).Configuration.Tools.IsEmpty,
                "New initial projection advertised hidden/script-only entries.");
            var path = Path.Combine(f.Root, "capped-exposure.jsonl");
            var session = await PersistentAgentSession.CreateAsync(path, ExposureHeader(path, f.Root), registry, profile.SelectedModel, () => 1, () => "capped-" + ++ids);
            await using (var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("No replacement expected.")))
            {
                await session.SetActiveToolsAsync(["hidden", "code", "deferred"]); Names(session, "code", "deferred");
                var state = session.Snapshot; var before = await Bytes(path);
                Check(await Failure(session.SetActiveToolsAsync(["direct"])) is SessionRuntimeRegistryException { Failure: SessionRuntimeRegistryFailure.UnknownTool },
                    "Explicit script activation allowed an unrelated tool outside the cap.");
                var after = await Bytes(path); Check(before.SequenceEqual(after) && state.Log.Sequence == session.Snapshot.Log.Sequence && session.Snapshot.Fault is null,
                    "Forbidden capped activation changed authoritative state.");
                Names(session, "code", "deferred");
            }
            await using (var reopened = await PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 1, () => "cap-reopen-" + ++ids, fallbackModel: profile.SelectedModel))
                Names(reopened, "code", "deferred");
            var emptyPath = Path.Combine(f.Root, "empty-exposure.jsonl"); var empty = ExposureRegistry(profile, origin, AllowedToolSelection.Create([]));
            await using var emptySession = await PersistentAgentSession.CreateAsync(emptyPath, ExposureHeader(emptyPath, f.Root), empty, profile.SelectedModel,
                () => 1, () => "empty-" + ++ids);
            var emptyBefore = await Bytes(emptyPath); var emptyState = emptySession.Snapshot;
            Check(await Failure(emptySession.SetActiveToolsAsync(["code"])) is SessionRuntimeRegistryException { Failure: SessionRuntimeRegistryFailure.UnknownTool },
                "Legacy script activation escaped an exact empty cap.");
            var emptyAfter = await Bytes(emptyPath); Check(emptyBefore.SequenceEqual(emptyAfter) && emptyState.Log.Sequence == emptySession.Snapshot.Log.Sequence,
                "Empty-cap refusal appended a loadout."); Names(emptySession);
        }
    }
    private static async Task ActualInitialExposureOpen()
    {
        ImmutableArray<string> rawInitial = ["direct", "model", "code", "deferred", "hidden"];
        foreach (var origin in new[] { false, true })
            foreach (var kind in new[] { "unrestricted", "named", "empty" })
            {
                using var f = new StartupSettingsTests.Fixture(); await using var profile = await Profile(f); var ids = 0;
                var cap = kind switch
                {
                    "named" => AllowedToolSelection.Create(["code", "deferred", "hidden"]),
                    "empty" => AllowedToolSelection.Create([]),
                    _ => null
                };
                var expected = kind == "unrestricted" ? new[] { "direct", "model" } : Array.Empty<string>();
                var registry = ExposureRegistry(profile, origin, cap, rawInitial);
                var path = Path.Combine(f.Root, "initial-exposure.jsonl"); await Seed(path, f.Root, "legacy");
                var oldBytes = await Bytes(path); byte[] admittedBytes;
                var opened = await PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 1,
                    () => "effective-" + ++ids, fallbackModel: profile.SelectedModel);
                await using (var owner = new ReplaceableAgentSession(opened,
                    (_, _) => throw new InvalidOperationException("No replacement expected.")))
                {
                    Names(opened, expected);
                    Check(opened.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(expected, StringComparer.Ordinal),
                        "Actual startup scheduler reintroduced script-only or hidden raw initial names.");
                    admittedBytes = await Bytes(path);
                    Check(admittedBytes.Length > oldBytes.Length && admittedBytes.Take(oldBytes.Length).SequenceEqual(oldBytes),
                        "Initial checkpoint omitted effective selection or rewrote historical bytes.");
                    Check(opened.Snapshot.Fault is null, "Effective admission faulted the session.");
                }
                await using (var reopened = await PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 1,
                    () => "repeat-" + ++ids, fallbackModel: profile.SelectedModel))
                {
                    Names(reopened, expected);
                    Check(reopened.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(expected, StringComparer.Ordinal),
                        "Reopened startup selection was not the effective admitted subset.");
                    var repeatedBytes = await Bytes(path);
                    Check(admittedBytes.SequenceEqual(repeatedBytes), "Idempotent effective reopen appended a duplicate checkpoint.");
                }
                Check(profile.UsedTurns == 0 && profile.Actions.Length == 0, "Initial exposure selection executed a provider or tool action.");
            }
    }
    private static SessionRuntimeRegistry ExposureRegistry(OfflineSessionProfile profile, bool isExtension, AllowedToolSelection? lifetime,
        ImmutableArray<string>? initial = null)
    {
        var catalog = ImmutableArray.Create(CatalogTool("direct", isExtension), CatalogTool("model", isExtension, exposure: ToolExposure.ModelOnly),
            CatalogTool("code", isExtension, exposure: ToolExposure.Codemode), CatalogTool("deferred", isExtension, exposure: ToolExposure.Deferred),
            CatalogTool("hidden", isExtension, exposure: ToolExposure.Hidden));
        return ExtensionRegistry(profile, catalog, new() { LifetimeToolSelection = lifetime, InitialActiveToolNames = initial, BindNestedCallsToSessionOwner = true });
    }
    private static SessionEntry ExposureHeader(string path, string root) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path), timestamp = "2026-10-05T00:00:00.000Z", cwd = root }));
    private static Task<OfflineSessionProfile> Profile(StartupSettingsTests.Fixture fixture) => OfflineSessionProfile.CreateAsync(fixture.Root,
        Path.Combine(fixture.Root, "unused.jsonl"), null, [], [], [], default);
    private static SessionRuntimeRegistry Registry(OfflineSessionProfile profile, string root, SessionRuntimeRegistryOptions options, Exception? preparation = null,
        ToolExposure exposure = ToolExposure.Direct)
    {
        var transport = profile.Registry.Resolve(profile.SelectedModel, []).Configuration.Transport;
        var registrations = new BuiltinToolCatalog(root, root).Registered.Select(tool => new SessionRegisteredTool(tool.Declaration, tool.Adapter)
            { Exposure = tool.Name == "read" ? exposure : ToolExposure.Direct,
                PrepareLoadout = preparation is not null && tool.Name == "read" ? _ => throw preparation! : null }).ToImmutableArray();
        return new([new(profile.SelectedModel, transport)], registrations, new Deny(), options);
    }
    private static async Task Seed(string path, string root, string tool)
    {
        var codec = new SessionEntryCodec(); var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path),
            timestamp = "2026-10-05T00:00:00.000Z", cwd = root }));
        var entry = codec.Parse(JsonSerializer.Serialize(new { type = "message", id = "old-system", parentId = (string?)null, timestamp = "2026-10-05T00:00:00.000Z",
            message = new { role = "system", content = "old prompt retained", timestamp = 1, toolsAdded = new[] { new { name = tool, description = "old declaration", parameters = new { type = "object" } } } } }));
        await using var store = await SessionLogStore.CreateNewAsync(path, header); await store.AppendAsync([entry]);
    }
    private static async Task<byte[]> Bytes(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        Check(stream.Length is > 0 and <= 65_536, "Unbounded fixture read."); var length = stream.Length; var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes); Check(stream.Length == length, "Fixture read crossed mutation."); return bytes;
    }
    private sealed class Deny : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class Resources : IAsyncDisposable
    { internal bool Disposed; public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; } }
}