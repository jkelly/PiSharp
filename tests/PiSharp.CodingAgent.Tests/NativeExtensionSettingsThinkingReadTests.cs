using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Reloading;
using PiSharp.Cli.Settings;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Facade.Context;
using PiSharp.Sessions.Serialization;

// Authored, unexecuted. Composition requires the separately owned R594 typed commit bridge.
// Shared runner registration and all native execution remain coordinator-owned.
internal static class NativeExtensionSettingsThinkingReadTests
{
    internal const string Prefix = "settings-thinking-read.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "registered-actions-and-command-view-read-live-acknowledged-thinking", () => RunCase("LiveThinking", LiveThinking)),
        (Prefix + "whole-merged-settings-copy-survives-actual-typed-reload", () => RunCase("MergedReloadCopy", MergedReloadCopy)),
        (Prefix + "saved-facets-foreign-context-and-foreign-registry-refuse-before-capture", () => RunCase("CallbackOwnership", CallbackOwnership)),
        (Prefix + "cancellation-before-after-capture-and-equal-generation-attachment-refusal", () => RunCase("CaptureGuards", CaptureGuards)),
        (Prefix + "initializer-missing-settings-and-unsupported-host-refuse-explicitly", () => RunCase("Unavailable", Unavailable)),
        (Prefix + "no-layer-actual-merge-has-no-input-reads-and-preserves-diagnostics-flush-original", () => RunCase("EmptyMerge", EmptyMerge))
    ];
    private static void Check(bool value) { if (!value) throw new IOException("Settings/thinking read assertion failed."); }
    private static readonly AsyncLocal<Ledger?> currentLedger = new();
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { currentLedger.Value?.AddDirect("expected.sync-" + typeof(T).Name, error, cleanup: false); return; }
        throw new IOException("Expected " + typeof(T).Name);
    }

    private static void ThrowsCancellation(Action action, CancellationToken token)
    {
        try { action(); }
        catch (OperationCanceledException error)
        {
            currentLedger.Value?.AddDirect("expected.sync-cancellation", error, cleanup: false);
            Check(error.CancellationToken == token && token.IsCancellationRequested);
            return;
        }
        throw new IOException("Expected cancellation with the supplied token.");
    }

    internal sealed record CapturedOriginal(Task Original, ImmutableArray<string> Phases, TaskStatus Status,
        AggregateException? Aggregate, Exception? Direct, bool Joined, bool ExpectedFailure, bool Cleanup);
    internal sealed record CapturedDirect(string Phase, Exception Exception, bool Cleanup);
    internal sealed record CapturedBeforeFilesystemCleanup(Exception? Primary, ImmutableArray<CapturedOriginal> Originals,
        ImmutableArray<CapturedDirect> Directs);
    internal sealed record CapturedCase(string Name, Exception? Primary, ImmutableArray<CapturedOriginal> Originals,
        ImmutableArray<CapturedDirect> Directs, CapturedBeforeFilesystemCleanup? BeforeFilesystemCleanup);
    private static readonly object evidenceGate = new();
    private static readonly List<Ledger> caseLedgers = [];
    // Actual object references are retained for the coordinator before any fallible report formatting.
    // Completed case inventories are append-only; repeated reads never reread Task.Exception.
    internal static ImmutableArray<CapturedCase> CapturedEvidence
    { get { lock (evidenceGate) return caseLedgers.Select(ledger => ledger.Export()).ToImmutableArray(); } }
    internal static ImmutableArray<CapturedOriginal> CapturedOriginals
        => CapturedEvidence.SelectMany(item => item.Originals).ToImmutableArray();

    internal sealed class FixtureEvidenceFailure(CapturedCase evidence, ImmutableArray<Exception> failures)
        : IOException("Settings/thinking fixture failed; inspect the unchanged raw task ledger.", new AggregateException(failures))
    {
        public CapturedCase Evidence { get; } = evidence;
        public Exception? Primary => Evidence.Primary;
        public ImmutableArray<CapturedOriginal> CapturedOriginals => Evidence.Originals;
        public ImmutableArray<Exception> Failures { get; } = failures;
    }

    private static async Task RunCase(string name, Func<Ledger, Task> run)
    {
        var ledger = new Ledger(name);
        lock (evidenceGate) caseLedgers.Add(ledger);
        var previous = currentLedger.Value; currentLedger.Value = ledger;
        try
        {
            var original = ledger.Track(run(ledger), "case.body");
            try { await original; ledger.CaptureJoined(original, null); }
            catch (Exception error) { ledger.CaptureJoined(original, error); throw; }
        }
        catch (Exception error) { ledger.Primary = error; }
        finally
        {
            try
            {
                // This is the real cleanup owner Task, not a replacement for any child original.
                // It cannot join itself; the caller joins and captures it after all child joins.
                var closing = ledger.Track(ledger.JoinOwnedAndOriginalsAsync(), "case.cleanup-owner", ownerControl: true);
                try { await closing; ledger.CaptureJoined(closing, null); }
                catch (Exception error) { ledger.CaptureJoined(closing, error); ledger.AddDirect("case.cleanup-owner", error, cleanup: true); }
                ledger.CaptureBeforeFilesystemCleanup();
                ledger.CleanFilesystem();
            }
            finally { currentLedger.Value = previous; }
        }
        var evidence = ledger.Export(); var failures = ledger.Failures();
        if (!failures.IsEmpty) throw new FixtureEvidenceFailure(evidence, failures);
    }

    private sealed class Ledger(string name)
    {
        private sealed class Original(Task original, string phase, bool expected, bool cleanup, bool ownerControl)
        {
            internal readonly Task Task = original; internal readonly List<string> Phases = [phase];
            internal bool Expected = expected, Cleanup = cleanup, OwnerControl = ownerControl, Joined;
            internal TaskStatus Status; internal AggregateException? Aggregate; internal Exception? Direct; internal Exception? ExpectedLeaf;
        }
        private readonly object gate = new();
        private readonly Dictionary<Task, Original> byIdentity = new(ReferenceEqualityComparer.Instance);
        private readonly List<Original> originals = [];
        private readonly List<CapturedDirect> directs = [];
        private readonly List<(string Phase, Func<Task?> Invoke)> owned = [];
        private readonly List<(string Phase, Action Invoke)> filesystem = [];
        private CapturedBeforeFilesystemCleanup? beforeFilesystem;
        internal Exception? Primary;
        internal Task Track(Task original, string phase, bool expectedFailure = false, bool cleanup = false, bool ownerControl = false)
        {
            ArgumentNullException.ThrowIfNull(original);
            lock (gate)
            {
                if (byIdentity.TryGetValue(original, out var row))
                {
                    row.Phases.Add(phase); row.Expected &= expectedFailure; row.Cleanup |= cleanup;
                    // The completed Task singleton can represent several completed operations.
                    // Preserve every invocation label, while caching that one Task only once.
                    row.OwnerControl &= ownerControl;
                }
                else { var capture = new Original(original, phase, expectedFailure, cleanup, ownerControl); byIdentity.Add(original, capture); originals.Add(capture); }
            }
            return original;
        }
        internal Task<T> Track<T>(Task<T> original, string phase, bool expectedFailure = false)
        { Track((Task)original, phase, expectedFailure); return original; }
        internal void CaptureJoined(Task original, Exception? direct)
        {
            lock (gate)
            {
                var row = byIdentity[original]; if (row.Joined) return;
                if (!original.IsCompleted) throw new IOException("An original was not joined.");
                row.Status = original.Status;
                row.Aggregate = original.IsFaulted ? original.Exception : null; // Exactly one read per actual Task identity.
                row.Direct = direct; row.Joined = true;
            }
        }
        internal void ExpectSingletonFailure(Task original, Exception expectedLeaf)
        {
            lock (gate)
            {
                var row = byIdentity[original];
                Check(row.Expected && (row.ExpectedLeaf is null || ReferenceEquals(row.ExpectedLeaf, expectedLeaf)));
                row.ExpectedLeaf = expectedLeaf;
            }
        }
        // A declaration at Track is never an exemption. Validate only cached evidence after the actual join.
        private static bool IsValidatedExpectedFailure(Original row)
            => row.Expected && !row.Cleanup && !row.OwnerControl && row.Joined && row.Status == TaskStatus.Faulted &&
                row.ExpectedLeaf is { InnerException: null } && ReferenceEquals(row.Direct, row.ExpectedLeaf) &&
                row.Aggregate is { InnerExceptions.Count: 1 } &&
                ReferenceEquals(row.Aggregate.InnerExceptions[0], row.ExpectedLeaf);
        internal void OwnAsync(string phase, Func<Task?> invoke) { lock (gate) owned.Add((phase, invoke)); }
        internal void Own(string phase, Action invoke) => OwnAsync(phase, () => { invoke(); return null; });
        internal void OwnFilesystemCleanup(string phase, Action invoke) { lock (gate) filesystem.Add((phase, invoke)); }
        internal void AddDirect(string phase, Exception error, bool cleanup)
        { lock (gate) directs.Add(new(phase, error, cleanup)); }
        internal async Task JoinOwnedAndOriginalsAsync()
        {
            (string Phase, Func<Task?> Invoke)[] cleanup;
            lock (gate) cleanup = owned.ToArray();
            for (var index = cleanup.Length - 1; index >= 0; index--)
            {
                var entry = cleanup[index]; Task? original;
                try { original = entry.Invoke(); }
                catch (Exception error) { AddDirect(entry.Phase, error, cleanup: true); continue; }
                if (original is null) continue; // Ownership transferred; no close Task was started.
                Track(original, entry.Phase, cleanup: true);
                Exception? direct = null;
                try { await original; } catch (Exception error) { direct = error; }
                CaptureJoined(original, direct);
            }
            // Join every actual started original, including failed setup, ignored returns,
            // expected negative operations and Tasks started by the owned cleanup callbacks.
            while (true)
            {
                Original? row;
                lock (gate) row = originals.FirstOrDefault(item => !item.Joined && !item.OwnerControl);
                if (row is null) return;
                Exception? direct = null;
                try { await row.Task; } catch (Exception error) { direct = error; }
                CaptureJoined(row.Task, direct);
            }
        }
        private ImmutableArray<CapturedOriginal> Snapshot()
            => originals.Select(row => new CapturedOriginal(row.Task, row.Phases.ToImmutableArray(), row.Joined ? row.Status : row.Task.Status,
                row.Aggregate, row.Direct, row.Joined, IsValidatedExpectedFailure(row), row.Cleanup)).ToImmutableArray();
        internal void CaptureBeforeFilesystemCleanup()
        { lock (gate) beforeFilesystem = new(Primary, Snapshot(), directs.ToImmutableArray()); }
        internal void CleanFilesystem()
        {
            (string Phase, Action Invoke)[] cleanup;
            lock (gate)
            {
                if (originals.Any(row => !row.Joined))
                {
                    directs.Add(new("filesystem.cleanup-blocked", new IOException("An original remains unjoined; retain the fixture directory and ownership evidence."), true));
                    return;
                }
                cleanup = filesystem.ToArray();
            }
            for (var index = cleanup.Length - 1; index >= 0; index--)
                try { cleanup[index].Invoke(); } catch (Exception error) { AddDirect(cleanup[index].Phase, error, cleanup: true); }
        }
        internal CapturedCase Export()
        { lock (gate) return new(name, Primary, Snapshot(), directs.ToImmutableArray(), beforeFilesystem); }
        internal ImmutableArray<Exception> Failures()
        {
            lock (gate)
            {
                var failures = new List<Exception>(); if (Primary is not null) failures.Add(Primary);
                foreach (var row in originals)
                {
                    if (IsValidatedExpectedFailure(row)) continue;
                    if (row.Expected) failures.Add(new IOException("Expected original has no validated singleton fault: " + string.Join(", ", row.Phases)));
                    if (!row.Joined) failures.Add(new IOException("An original remains unjoined."));
                    if (row.Aggregate is not null) failures.Add(row.Aggregate);
                    else if (row.Direct is not null) failures.Add(row.Direct);
                }
                failures.AddRange(directs.Where(item => item.Cleanup).Select(item => item.Exception));
                return failures.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToImmutableArray();
            }
        }
    }
    private static async Task LiveThinking(Ledger ledger)
    {
        var fixture = await ledger.Track(Fixture.Create(ledger), "fixture.create");
        var observed = new List<string>();
        await fixture.Register((_, actions, context, _) =>
        {
            var read = (IExtensionSettingsThinkingReadFacade)actions;
            observed.Add(read.GetThinkingLevel());
            var admitted = (IExtensionSettingsThinkingReadHost)((IExtensionFacadeHostContext)context).FacadeHost;
            Check(admitted.GetThinkingLevel(context) == read.GetThinkingLevel());
            return ValueTask.CompletedTask;
        });
        var before = fixture.Owner.Current.Session.Snapshot.Context.ThinkingLevel;
        await fixture.Invoke(); Check(observed.Single() == before);
        await fixture.Track(fixture.Owner.Current.Session.ConfigureAsync(new(ThinkingLevel: "high")));
        await fixture.Invoke(); Check(observed[1] == "high");
        var command = ExtensionCommandFacade.CreateCommand("view", "read-view", "read-view", (_, view, _) =>
        {
            var read = (IExtensionSettingsThinkingReadFacade)view;
            Check(read.GetThinkingLevel() == "high");
            Check(read.GetSettings().ToString() == fixture.Settings.Values.ToString());
            return ValueTask.CompletedTask;
        });
        await fixture.Track(fixture.Registry.ActivateAsync("view-owner", new Plugin(command)));
        await fixture.Track(fixture.Registry.InvokeCommandAsync(fixture.Registry.CaptureSnapshot(), "read-view", JsonData.EmptyObject).AsTask());
        Check(fixture.Transport.Calls == 0);
    }

    private static async Task MergedReloadCopy(Ledger ledger)
    {
        var files = new SettingsFiles(); var user = Path.Combine(Path.GetTempPath(), "settings-read-user.json");
        var project = Path.Combine(Path.GetTempPath(), "settings-read-project.json");
        files.Text[user] = "{\"nested\":{\"a\":1,\"b\":2},\"defaultTools\":[\"read\"],\"unknown\":null}";
        files.Text[project] = "{\"nested\":{\"b\":3},\"defaultTools\":[\"+bash\"],\"list\":[2,1]}";
        var settings = await ledger.Track(StartupSettings.LoadAsync(new(user, project, JsonData.Parse("{\"nested\":{\"a\":4}}")), files), "settings.initial-merge");
        var fixture = await ledger.Track(Fixture.Create(ledger, settings), "fixture.create");
        var returned = new List<JsonData>();
        await fixture.Register((_, actions, _, _) =>
        { returned.Add(((IExtensionSettingsThinkingReadFacade)actions).GetSettings()); return ValueTask.CompletedTask; });
        await fixture.Invoke(); var first = returned[0];
        Check(!ReferenceEquals(first, settings.Values));
        Check(first.Value.GetProperty("nested").GetProperty("a").GetInt32() == 4);
        Check(first.Value.GetProperty("nested").GetProperty("b").GetInt32() == 3);
        Check(first.Value.GetProperty("unknown").ValueKind == JsonValueKind.Null);
        Check(first.Value.GetProperty("defaultTools").GetArrayLength() == 2 && first.Value.GetProperty("list")[0].GetInt32() == 2);
        files.Text[project] = "{\"nested\":{\"b\":9},\"unknown\":{\"complete\":true}}";
        var next = await ledger.Track(StartupSettings.LoadAsync(new(user, project), files), "settings.reload-merge");
        var initial = fixture.Owner.Current; var commits = 0;
        fixture.Profile.ConfigureReload(fixture.Reload(next, () => commits++));
        var reload = fixture.Track(fixture.Profile.ReloadAsync(initial)); var receipt = await reload;
        Check(commits == 1 && receipt.Current is not null && ReferenceEquals(receipt.Current, fixture.Owner.Current));
        await fixture.Invoke();
        Check(returned[1].ToString() == next.Values.ToString());
        Check(first.Value.GetProperty("nested").GetProperty("b").GetInt32() == 3);
        Throws<InvalidOperationException>(() => fixture.Profile.CaptureEffectiveSettings(initial));
        Check(fixture.Transport.Calls == 0);
    }

    private static async Task CallbackOwnership(Ledger ledger)
    {
        var fixture = await ledger.Track(Fixture.Create(ledger), "fixture.create");
        IExtensionSettingsThinkingReadFacade? savedActions = null, savedView = null;
        IExtensionSettingsThinkingReadHost? savedHost = null; IExtensionCommandContext? savedContext = null;
        var calls = 0;
        await fixture.Register((_, actions, context, _) =>
        {
            if (++calls == 1)
            {
                savedActions = (IExtensionSettingsThinkingReadFacade)actions;
                savedContext = context; savedHost = (IExtensionSettingsThinkingReadHost)((IExtensionFacadeHostContext)context).FacadeHost;
                var captured = ((IExtensionSessionContext)context).SessionSnapshot!;
                var count = fixture.Captures;
                Throws<InvalidOperationException>(() => savedHost.GetSettings(new Context(captured)));
                Throws<InvalidOperationException>(() => savedHost.GetThinkingLevel(new Context(captured)));
                Check(count == fixture.Captures);
            }
            else
            {
                var count = fixture.Captures;
                Throws<InvalidOperationException>(() => savedActions!.GetSettings());
                Throws<InvalidOperationException>(() => savedActions!.GetThinkingLevel());
                Throws<InvalidOperationException>(() => savedView!.GetSettings());
                Throws<InvalidOperationException>(() => savedHost!.GetSettings(savedContext!));
                Check(count == fixture.Captures);
            }
            return ValueTask.CompletedTask;
        });
        await fixture.Invoke();
        var command = ExtensionCommandFacade.CreateCommand("saved", "save-view", "save-view", (_, view, _) =>
        { savedView = (IExtensionSettingsThinkingReadFacade)view; return ValueTask.CompletedTask; });
        await fixture.Track(fixture.Registry.ActivateAsync("saved-view", new Plugin(command)));
        await fixture.Track(fixture.Registry.InvokeCommandAsync(fixture.Registry.CaptureSnapshot(), "save-view", JsonData.EmptyObject).AsTask());
        await fixture.Invoke();
        var foreign = new ExtensionRegistry(); ledger.OwnAsync("foreign.registry.close", () => foreign.DisposeAsync().AsTask()); var entered = false;
        var descriptor = ExtensionRegistrationCommands.Create("foreign", "foreign", "foreign", fixture.Providers,
            new NoActions(), (_, _, _, _) => { entered = true; return ValueTask.CompletedTask; });
        await ledger.Track(foreign.ActivateAsync("foreign-owner", new Plugin(descriptor)), "foreign.activate");
        var original = ledger.Track(foreign.InvokeCommandAsync(foreign.CaptureSnapshot(), "foreign", JsonData.EmptyObject).AsTask(), "foreign.invoke", expectedFailure: true);
        Exception? refusal = null;
        try { await original; } catch (Exception error) { ledger.CaptureJoined(original, error); refusal = error; }
        Check(refusal is not null && refusal.GetType() == typeof(InvalidOperationException) &&
            refusal.Message == "Actual admitted native command context required." && !entered && original.IsFaulted);
        ledger.ExpectSingletonFailure(original, refusal!);
    }

    private static async Task CaptureGuards(Ledger ledger)
    {
        var fixture = await ledger.Track(Fixture.Create(ledger), "fixture.create");
        var attached = fixture.Owner.Current; var snapshot = new ExtensionSessionSnapshot(
            attached.Session.Snapshot.Log.Header.Id, attached.Generation, null, []);
        var equalNumberForeign = new AgentSessionAttachment(attached.Session, attached.Generation, attached.LifetimeToken);
        Throws<InvalidOperationException>(() => fixture.Profile.CaptureEffectiveSettings(equalNumberForeign));
        Throws<InvalidOperationException>(() => fixture.Host.GetSettings(new Context(snapshot with { Generation = attached.Generation + 1 })));
        foreach (var index in new[] { 0, 1, 2 })
        {
            using var cancel = new CancellationTokenSource();
            var context = new Context(snapshot, index == 0 ? cancel.Token : default, index == 1 ? cancel.Token : default, index == 2 ? cancel.Token : default);
            var count = fixture.Captures; cancel.Cancel();
            ThrowsCancellation(() => fixture.Host.GetSettings(context), cancel.Token);
            ThrowsCancellation(() => fixture.Host.GetThinkingLevel(context), cancel.Token); Check(count == fixture.Captures);
        }
        using var crossed = new CancellationTokenSource();
        fixture.AfterCapture = () => crossed.Cancel(); var before = fixture.Captures;
        ThrowsCancellation(() => fixture.Host.GetSettings(new Context(snapshot, crossed.Token)), crossed.Token);
        Check(fixture.Captures == before + 1); fixture.AfterCapture = null;
    }

    private static async Task Unavailable(Ledger ledger)
    {
        var context = new Context(new("unattached", 1, null, [])); var host = new NativeExtensionContextFacadeHost();
        var startup = await ledger.Track(StartupSettings.LoadAsync(new(Overrides: JsonData.Parse("{\"opaqueStartup\":true}"))), "initializer.startup-merge");
        var startupReads = 0;
        host.ConfigureSettingsThinkingReads(_ => { startupReads++; return startup; });
        var initialized = false;
        var registry = new ExtensionRegistry(); ledger.OwnAsync("initializer.registry.close", () => registry.DisposeAsync().AsTask());
        {
            await ledger.Track(registry.ActivateAsync("initializer", new Initializer(() =>
            {
                Throws<NotSupportedException>(() => host.GetSettings(context));
                Throws<NotSupportedException>(() => host.GetThinkingLevel(context)); initialized = true;
            })), "initializer.activate");
        }
        Check(initialized && startupReads == 0);
        var fixture = await ledger.Track(Fixture.Create(ledger), "fixture.create");
        var missing = new NativeExtensionContextFacadeHost(); missing.Attach(fixture.Owner);
        var attached = fixture.Owner.Current;
        var current = new Context(new(attached.Session.Snapshot.Log.Header.Id, attached.Generation, null, []));
        Throws<NotSupportedException>(() => missing.GetSettings(current));
        Check(missing.GetThinkingLevel(current) == attached.Session.Snapshot.Context.ThinkingLevel);
        var absent = new NativeExtensionContextFacadeHost(); absent.ConfigureSettingsThinkingReads(_ => null); absent.Attach(fixture.Owner);
        Throws<NotSupportedException>(() => absent.GetSettings(current));
        var legacy = new ExtensionRegistry(facadeHost: new LegacyHost()); ledger.OwnAsync("legacy.registry.close", () => legacy.DisposeAsync().AsTask());
        var providers = new ExtensionProviderRegistrationHost(legacy, [], new NoProvider()); ledger.Own("legacy.providers.close", providers.Dispose);
        await ledger.Track(legacy.ActivateAsync("legacy", new Plugin(ExtensionRegistrationCommands.Create("legacy", "legacy", "legacy", providers,
            (_, actions, _, _) =>
            {
                var read = (IExtensionSettingsThinkingReadFacade)actions;
                Throws<NotSupportedException>(() => read.GetSettings()); Throws<NotSupportedException>(() => read.GetThinkingLevel());
                return ValueTask.CompletedTask;
            }))), "legacy.activate");
        await ledger.Track(legacy.InvokeCommandAsync(legacy.CaptureSnapshot(), "legacy", JsonData.EmptyObject).AsTask(), "legacy.invoke");
        await ledger.Track(legacy.ActivateAsync("legacy-view", new Plugin(ExtensionCommandFacade.CreateCommand("legacy-view", "legacy-view", "legacy-view",
            (_, view, _) =>
            {
                var read = (IExtensionSettingsThinkingReadFacade)view;
                Throws<NotSupportedException>(() => read.GetSettings()); Throws<NotSupportedException>(() => read.GetThinkingLevel());
                return ValueTask.CompletedTask;
            }))), "legacy.view-activate");
        await ledger.Track(legacy.InvokeCommandAsync(legacy.CaptureSnapshot(), "legacy-view", JsonData.EmptyObject).AsTask(), "legacy.view-invoke");
    }

    private static async Task EmptyMerge(Ledger ledger)
    {
        var files = new SettingsFiles(); var diagnostics = new FlushWriter(ledger); ledger.Own("diagnostics.close", diagnostics.Dispose);
        var actual = await ledger.Track(SettingsStartupConfiguration.LoadAsync(null, diagnostics, files, default), "settings.no-layer-merge");
        Check(actual is not null && actual.Values.ToString() == "{}" && files.Reads == 0 && diagnostics.Flushes == 1);
        var withDiagnostic = await ledger.Track(SettingsStartupConfiguration.LoadAsync(new(Overrides: JsonData.Parse("{\"steeringMode\":7,\"opaque\":null}")), diagnostics, files, default), "settings.diagnostic-merge");
        Check(withDiagnostic!.Diagnostics.Single().Code == "InvalidQueueMode:steeringMode");
        Check(withDiagnostic.Values.Value.GetProperty("opaque").ValueKind == JsonValueKind.Null);
        Check(diagnostics.ToString().Contains("settings_diagnostic", StringComparison.Ordinal) && files.Reads == 0);
        var failure = new IOException("actual diagnostics flush failure"); var failed = new FlushWriter(ledger, failure); ledger.Own("failed-diagnostics.close", failed.Dispose);
        var original = ledger.Track(SettingsStartupConfiguration.LoadAsync(null, failed, files, default), "settings.flush-failure-merge", expectedFailure: true);
        ledger.ExpectSingletonFailure(original, failure);
        Exception? observed = null;
        try { await original; } catch (Exception error) { ledger.CaptureJoined(original, error); observed = error; }
        Check(ReferenceEquals(failure, observed) && original.IsFaulted && failed.Flushes == 1);
    }

    private sealed class Fixture
    {
        private readonly Ledger ledger;
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "pisharp-settings-thinking-read-" + Guid.NewGuid().ToString("N"));
        internal readonly NoTransport Transport;
        private Fixture(Ledger ledger) { this.ledger = ledger; Transport = new(ledger); }
        internal OfflineSessionProfile Profile = null!; internal StartupSettingsSnapshot Settings = null!;
        internal NativeExtensionContextFacadeHost Host = null!; internal ExtensionRegistry Registry = null!;
        internal ExtensionProviderRegistrationHost Providers = null!; internal int Captures; internal Action? AfterCapture;
        internal ReplaceableAgentSession Owner => Profile.Sessions!;
        internal Task Track(Task original, [CallerArgumentExpression(nameof(original))] string phase = "") => ledger.Track(original, phase);
        internal Task<T> Track<T>(Task<T> original, [CallerArgumentExpression(nameof(original))] string phase = "") => ledger.Track(original, phase);
        internal static async Task<Fixture> Create(Ledger ledger, StartupSettingsSnapshot? settings = null)
        {
            var fixture = new Fixture(ledger);
            ledger.OwnFilesystemCleanup("fixture.directory.delete", () =>
            {
                var absolute = Path.GetFullPath(fixture.Root);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!string.Equals(Path.GetDirectoryName(absolute), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), comparison) ||
                    !Path.GetFileName(absolute).StartsWith("pisharp-settings-thinking-read-", StringComparison.Ordinal))
                    throw new IOException("Invalid owned fixture directory.");
                if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
            });
            Directory.CreateDirectory(fixture.Root);
            fixture.Settings = settings ?? await ledger.Track(StartupSettings.LoadAsync(new()), "fixture.settings-merge");
            var path = Path.Combine(fixture.Root, "session.jsonl");
            fixture.Profile = await ledger.Track(OfflineSessionProfile.CreateAsync(fixture.Root, path, null, [], [], [], default), "fixture.profile-create");
            ledger.OwnAsync("fixture.profile.close", () => fixture.Profile.DisposeAsync().AsTask());
            fixture.Profile.ConfigureEffectiveSettings(fixture.Settings); fixture.Profile.BindSettingsThinkingReads();
            var sequence = 0; var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "settings-thinking-session", timestamp = "2026-01-01T00:00:00Z", cwd = fixture.Root }));
            var session = await ledger.Track(PersistentAgentSession.CreateAsync(path, header, fixture.Runtime(),
                fixture.Profile.SelectedModel, () => 1, () => "read-entry-" + ++sequence), "fixture.session-create");
            var transferred = false;
            ledger.OwnAsync("fixture.untransferred-session.close", () => transferred ? null : session.DisposeAsync().AsTask());
            await ledger.Track(fixture.Profile.AttachOwnerAsync(session), "fixture.owner-attach"); transferred = true;
            var views = new NativeSessionSnapshotProvider(); views.Attach(fixture.Owner);
            fixture.Host = new(); fixture.Host.ConfigureSettingsThinkingReads(attachment =>
            {
                fixture.Captures++; var snapshot = fixture.Profile.CaptureEffectiveSettings(attachment);
                fixture.AfterCapture?.Invoke(); return snapshot;
            });
            fixture.Host.Attach(fixture.Owner);
            fixture.Registry = new(null, null, views, new NativeExtensionRegistrationFacadeHost(fixture.Host, new NoActions()));
            ledger.OwnAsync("fixture.registry.close", () => fixture.Registry.DisposeAsync().AsTask());
            fixture.Providers = new(fixture.Registry, [], new NoProvider());
            ledger.Own("fixture.providers.close", fixture.Providers.Dispose); return fixture;
        }
        internal SessionRuntimeRegistry Runtime() => new([new(OfflineSessionProfile.Model, Transport)], [], new Policy());
        internal Task Register(ExtensionRegistrationCommandCallback callback) => Track(Registry.ActivateAsync("read-owner",
            new Plugin(ExtensionRegistrationCommands.Create("read", "read", "read", Providers, callback))));
        internal Task Invoke() => Track(Registry.InvokeCommandAsync(Registry.CaptureSnapshot(), "read", JsonData.EmptyObject).AsTask());
        internal NativeHostReloadAdmission Reload(StartupSettingsSnapshot next, Action commit) => new(new(new object()),
            new Dictionary<string, PiSharp.Extensions.Abstractions.Reloading.HostReloadFlagValue>(), true, new([], []), new()
            {
                StageSettingsAsync = (_, _) => ValueTask.FromResult(new NativeHostReloadPayload(new object(), next)),
                SyncQueueModesAsync = (_, _) => ValueTask.CompletedTask, ResetApiProvidersAsync = (_, _) => ValueTask.CompletedTask,
                ReloadResourcesAsync = (_, _) => ValueTask.CompletedTask,
                DescribeRuntimeAsync = (_, _) => ValueTask.FromResult(new PiSharp.Extensions.Abstractions.Reloading.HostReloadRuntime([], [])),
                BuildRuntimeAsync = (_, _, _) => ValueTask.FromResult(new PreparedNativeHostReload(new(Runtime(), new Release()), commit)),
                SessionShutdownAsync = (_, _, _) => ValueTask.CompletedTask, CleanupPreparationAsync = _ => ValueTask.CompletedTask,
                BeforeSessionStartAsync = (_, _) => ValueTask.CompletedTask, SessionStartAsync = (_, _, _) => ValueTask.CompletedTask,
                ReportUnhandledMcpServersAsync = (_, _) => ValueTask.CompletedTask, ExtendResourcesAsync = (_, _, _) => ValueTask.CompletedTask
            });
    }
    private sealed class Plugin(ExtensionCommandDescriptor command) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        { registry.RegisterCommand(command); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Initializer(Action initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Context(ExtensionSessionSnapshot snapshot, CancellationToken operation = default,
        CancellationToken session = default, CancellationToken extension = default) : IExtensionSessionContext
    {
        public string OwnerId => "read-fixture"; public long OwnerGeneration => 1;
        public CancellationToken OperationCancellationToken => operation; public CancellationToken SessionCancellationToken => session;
        public CancellationToken ExtensionLifetimeCancellationToken => extension; public ExtensionSessionSnapshot? SessionSnapshot => snapshot;
    }
    private sealed class NoActions : IExtensionRegistrationActionHost
    {
        public ValueTask<bool> SetModelAsync(ModelDescriptor model, CancellationToken token) => throw new IOException("No model mutation admitted.");
        public ValueTask SendMessageAsync(ExtensionCustomMessage message, ExtensionMessageOptions? options, CancellationToken token) => throw new IOException("No message admitted.");
        public ValueTask SendUserMessageAsync(JsonData content, ExtensionUserMessageOptions? options, CancellationToken token) => throw new IOException("No prompt admitted.");
    }
    private sealed class NoProvider : IExtensionProviderConfigurationAdapter
    { public ExtensionProviderDefinition Resolve(string name, JsonData configuration) => throw new IOException("No provider acquisition admitted."); }
    private sealed class LegacyHost : IExtensionContextReadHost, IExtensionRegistrationActionHost
    {
        public string GetCwd(IExtensionContext context) => "legacy"; public JsonData? GetModel(IExtensionContext context) => null;
        public bool IsIdle(IExtensionContext context) => true; public bool HasPendingMessages(IExtensionContext context) => false;
        public string GetSystemPrompt(IExtensionContext context) => "";
        public ValueTask<bool> SetModelAsync(ModelDescriptor model, CancellationToken token) => throw new IOException("No action.");
        public ValueTask SendMessageAsync(ExtensionCustomMessage message, ExtensionMessageOptions? options, CancellationToken token) => throw new IOException("No action.");
        public ValueTask SendUserMessageAsync(JsonData content, ExtensionUserMessageOptions? options, CancellationToken token) => throw new IOException("No action.");
    }
    private sealed class NoTransport(Ledger ledger) : IChatTransport, IThinkingLevelTransport
    {
        internal int Calls;
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) => ["off", "low", "medium", "high"];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await ledger.Track(Task.FromException(new IOException("No provider call admitted.")), "unexpected.provider-failure"); yield break; }
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class Release : IAsyncDisposable
    { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class SettingsFiles : IStartupSettingsFileSystem
    {
        internal readonly Dictionary<string, string?> Text = []; internal int Reads;
        public ValueTask<string?> ReadTextAsync(string path, CancellationToken token)
        { Reads++; token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Text.GetValueOrDefault(path)); }
    }
    private sealed class FlushWriter(Ledger ledger, Exception? failure = null) : StringWriter
    {
        internal int Flushes;
        public override Task FlushAsync(CancellationToken token)
        {
            Flushes++;
            var original = ledger.Track(failure is null ? Task.CompletedTask : Task.FromException(failure), "diagnostics.flush", expectedFailure: failure is not null);
            if (failure is not null) ledger.ExpectSingletonFailure(original, failure);
            return original;
        }
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
            => ledger.Track(base.WriteLineAsync(buffer, token), "diagnostics.write");
    }
}
