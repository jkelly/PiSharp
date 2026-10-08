using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Rpc.Ui;
using PiSharp.Sessions.Serialization;

internal static class NativeShutdownPlacementTests
{
    internal const string Prefix = "native shutdown placement ";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "stopped actual attachment retains final read view without actions and joins held handler", RetainedContext),
        (Prefix + "own lifetime cancellation listener and reporter faults retain causes and release leases", LifetimeFailures),
        (Prefix + "fixture retains locked evidence and preserves original and cleanup exception identities", FixtureEvidence),
        (Prefix + "unbound stream fixture initialization cleanup preserves its original cause", UnboundInitializer),
        (Prefix + "actual offline RPC notifies after terminal join before native disposal", () => Host(false, false)),
        (Prefix + "actual live RPC shared profile notifies without any HTTP request", () => Host(true, false)),
        (Prefix + "operation terminal reporter and extension cleanup failures all survive", () => Host(false, true))
    ];
    private static JsonData Shutdown => JsonData.Parse("{\"type\":\"session_shutdown\",\"reason\":\"quit\"}");

    private static Task RetainedContext() => WithFiles(RetainedContext);
    private static async Task RetainedContext(Files files)
    {
        await using var profile = await OfflineSessionProfile.CreateAsync(files.Root, files.Session, null, [], [], [], default);
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
            id = "shutdown-read", timestamp = "2026-10-04T00:00:00.000Z", cwd = files.Root }));
        var next = 0;
        var session = await PersistentAgentSession.CreateAsync(files.Session, header, profile.Registry, profile.SelectedModel,
            () => 1, () => "shutdown-" + ++next);
        profile.AttachOwner(session);
        var attached = profile.Sessions!.Current; var state = session.Snapshot;
        var retained = new ExtensionSessionSnapshot(state.Log.Header.Id, attached.Generation, state.Context.LeafId,
            state.Context.Ancestry.Select(entry => entry.WireBody).ToImmutableArray());
        await profile.Sessions!.StopAdmissionAndJoinAsync();
        Check(attached.LifetimeToken.IsCancellationRequested && !session.Snapshot.IsDisposed, "Test did not retire the original attachment while retaining resources.");
        await using var registry = new ExtensionRegistry();
        var entered = Gate(); var release = Gate(); var cleanup = 0; IExtensionContext? seen = null;
        var scope = await registry.ActivateAsync("read-only", new Plugin(entries => entries.Observe(new("shutdown", "session_shutdown",
            async (_, context, token) =>
            {
                seen = context;
                var view = ((IExtensionSessionContext)context).SessionSnapshot;
                Check(view is not null && view.SessionId == retained.SessionId && view.Generation == attached.Generation &&
                    view.BranchEntries.Select(entry => entry.ToString()).SequenceEqual(retained.BranchEntries.Select(entry => entry.ToString())),
                    "Stopped callback saw a recaptured, missing or altered acknowledged view.");
                Check(context is not IExtensionSessionActionsContext && context is not IExtensionToolContext &&
                    context is not IExtensionCommandContext && context is not IExtensionSessionCatalogContext &&
                    context is not IExtensionSessionCompactionContext && context is not IExtensionSessionContextEditContext &&
                    context is not IExtensionToolActivationContext, "Read context exposes privileged action interfaces.");
                var ui = ((IExtensionUiContext)context).Ui;
                Check(ui.Capabilities.Features.IsEmpty && (await ui.ConfirmAsync("forbidden", "forbidden")).Kind == ExtensionUiOutcomeKind.Unavailable,
                    "Retired shutdown UI became interactive.");
                Check(token == context.OperationCancellationToken && token.CanBeCanceled && !token.IsCancellationRequested &&
                    !context.SessionCancellationToken.CanBeCanceled, "Shutdown inherited the retired attachment cancellation.");
                entered.TrySetResult(); await release.Task;
            })), () => { cleanup++; return ValueTask.CompletedTask; }));
        var original = registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), Shutdown, retainedSessionSnapshot: retained).AsTask();
        Task? disposal = null;
        try
        {
            await Reach(entered.Task, original);
            disposal = scope.DisposeAsync().AsTask();
            Check(!original.IsCompleted && !disposal.IsCompleted && cleanup == 0 && seen is not null &&
                seen.ExtensionLifetimeCancellationToken.IsCancellationRequested && !seen.OperationCancellationToken.IsCancellationRequested,
                "Generation disposal detached notification, released resources or borrowed shutdown lifetime ownership.");
        }
        finally
        {
            release.TrySetResult();
            try { Check(await original, "Admitted shutdown callback was lost."); }
            finally { if (disposal is not null) await disposal; }
        }
        Check(cleanup == 1 && seen!.OperationCancellationToken.IsCancellationRequested,
            "Original notification lifetime or owner cleanup did not settle.");
    }

    private static async Task LifetimeFailures()
    {
        var registry = new ExtensionRegistry(new() { MaximumConcurrentDispatches = 1 });
        var listener = new IOException("authored shutdown lifetime listener failure");
        var reporter = new IOException("authored shutdown reporter failure");
        var disposal = new IOException("authored shutdown plugin cleanup failure");
        using var foreign = new CancellationTokenSource(); foreign.Cancel();
        CancellationTokenRegistration listenerRegistration = default;
        var scope = await registry.ActivateAsync("failure", new Plugin(entries => entries.Observe(new("shutdown", "session_shutdown",
            (_, _, token) =>
            {
                listenerRegistration = token.Register(() => throw listener);
                throw new OperationCanceledException(foreign.Token);
            })), () => throw disposal));
        try
        {
            var failure = await Throws<AggregateException>(() => registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), Shutdown,
                (_, _) => throw reporter).AsTask());
            Check(Contains(failure, reporter) && Contains(failure, listener), "Own lifetime failure masked the original reporter or detached its cancellation listener.");
            var cleanup = await Throws<ExtensionRegistrationException>(() => scope.DisposeAsync().AsTask());
            Check(Contains(cleanup, disposal), "Actual plugin cleanup exception identity was discarded.");
            Check(!await registry.DispatchSessionShutdownAsync(registry.CaptureSnapshot(), Shutdown), "Failure retained shared dispatch charge or live registration authority.");
        }
        finally
        {
            listenerRegistration.Dispose();
            try { await registry.DisposeAsync(); } catch (Exception error) when (Contains(error, disposal)) { }
        }
    }

    // Called by the existing unknown-startup case, so the bounded diagnostic lane
    // covers both empty and real extension-bearing startup views.
    internal static Task RejectedStartup(PiSharp.Cli.Mcp.McpProfileRuntimeAdmission admission,
        Task closing, Action release, Action assertClosed) => WithFiles(async files =>
    {
        await files.Publish(false);
        using var input = new MemoryStream(); using var output = new MemoryStream();
        using var errors = new StringWriter();
        var original = RpcSessionCommand.RunAsync([.. files.Args(false), "--tools", "mcp__profile__missing"],
            input, output, errors, mcpAdmission: admission);
        Exception? assertion = null;
        try
        {
            await Reach(closing, original);
            Check(!original.IsCompleted && !File.Exists(files.Session),
                "Rejected extension-bearing startup escaped held cleanup or created a durable writer.");
        }
        catch (Exception failure) { assertion = failure; }
        finally { release(); }
        int code;
        try { code = await original; }
        catch (Exception failure)
        {
            var retained = original.Exception is { } faults ? (Exception)faults : failure;
            if (assertion is not null) throw new AggregateException(assertion, retained);
            ExceptionDispatchInfo.Capture(retained).Throw(); throw;
        }
        if (assertion is not null) ExceptionDispatchInfo.Capture(assertion).Throw();
        assertClosed();
        var markers = File.Exists(files.Marker) ? File.ReadAllLines(files.Marker) : [];
        using var error = JsonDocument.Parse(errors.ToString());
        var invalid = error.RootElement.GetProperty("code").GetString() == "InvalidArguments";
        var cleanupCount = error.RootElement.GetProperty("cleanupFailureCount").GetInt32();
        Console.Error.WriteLine("DIAGNOSTIC " + JsonSerializer.Serialize(new
        { source = "mcp-profile-review.unknown-startup", variant = "rpc-extension", exitCode = code,
            publicCode = invalid ? "InvalidArguments" : "other", cleanupCount, frameBytes = output.Length,
            disposeCount = markers.Count(value => value == "dispose"), shutdownCount = markers.Count(value => value == "shutdown") }));
        Check(markers.SequenceEqual(new[] { "dispose" }) && !File.Exists(files.View),
            "Unattached extension startup was notified, not disposed, or disposed more than once.");
        Check(code == 2 && invalid && cleanupCount == 0 && output.Length == 0,
            "Extension-bearing startup rejection changed its public failure or added cleanup/output.");
    });
    private static Task Host(bool live, bool fail) => WithFiles(files => Host(files, live, fail));
    private static async Task Host(Files files, bool live, bool fail)
    {
        await files.Publish(fail);
        using var output = new MemoryStream();
        var reporter = new IOException("authored original host reporter failure");
        using var errors = new ReporterWriter(fail ? reporter : null);
        using var handler = new RejectHttp();
        using var input = fail ? new FaultedInput(new IOException("authored original input failure")) : new FaultedInput(null);
        var terminalFailure = new IOException("authored original terminal failure");
        var entered = Gate(); var release = Gate(); RpcSessionShutdownSettlement? captured = null;
        var args = files.Args(live);
        var original = RpcSessionCommand.RunWithPresentationAsync(args, input, output, errors, new Observer(), default,
            stopTerminalAndJoin: async settlement =>
            {
                captured = settlement;
                Check(!File.Exists(files.Marker) && !settlement.RuntimeCleanup.IsCompleted,
                    "Shutdown handler/disposal started before the original terminal join.");
                entered.TrySetResult(); await release.Task;
                return settlement.AcknowledgeTerminalStopped(fail ? [terminalFailure] : []);
            }, liveRuntime: live ? new LiveSessionRuntime(_ => "authored-inert-shutdown-fixture", () => handler) : null);
        int result;
        try
        {
            await Reach(entered.Task, original);
            Check(!original.IsCompleted && captured?.Session is not null, "Actual caller bypassed the held terminal boundary or lacked its retained session.");
        }
        finally
        {
            release.TrySetResult();
            try { result = await original; }
            finally { files.RecordHostDiagnostics(errors.ToString(), output.ToArray()); }
        }
        Check(File.ReadAllLines(files.Marker).SequenceEqual(new[] { "shutdown", "dispose" }),
            "Actual loader callback did not run after retirement or resource disposal overtook it.");
        var view = JsonData.Parse(await File.ReadAllTextAsync(files.View));
        var settlement = captured ?? throw new InvalidOperationException("Terminal settlement was not captured.");
        var final = settlement.Session ?? throw new InvalidOperationException("Terminal settlement has no final session.");
        Check(view.Value.GetProperty("SessionId").GetString() == final.Log.Header.Id &&
            view.Value.GetProperty("Generation").GetInt64() == 1 &&
            view.Value.GetProperty("persistence").GetString() == "VolatileMemory" &&
            view.Value.GetProperty("entries").EnumerateArray().Select(entry => entry.GetRawText()).SequenceEqual(
                final.Context.Ancestry.Select(entry => entry.WireBody.Value.GetRawText())) &&
            view.Value.GetProperty("noActions").GetBoolean() && view.Value.GetProperty("noUi").GetBoolean(),
            "Actual shared-profile shutdown view or authority did not match the acknowledged final state.");
        Check(handler.Requests == 0 && result == (fail ? 1 : 0), "Shutdown sent HTTP or misclassified clean/failing original joins.");
        var failures = await settlement.Completion;
        if (fail)
        {
            Check(failures.Any(error => Contains(error, input.Failure!)) && failures.Any(error => Contains(error, terminalFailure)) &&
                failures.Any(error => Contains(error, reporter)) && failures.Any(error => ContainsMessage(error, "authored shutdown fixture disposal failure")),
                "Operation, terminal, reporter or plugin cleanup failure facts were lost.");
            Check((await settlement.RuntimeCleanup).Any(error => Contains(error, reporter)), "Notification failure was excluded from runtime cleanup evidence.");
        }
        else Check(failures.IsEmpty && (await settlement.RuntimeCleanup).IsEmpty, "Clean notification invented a failure.");
    }

    private static async Task FixtureEvidence()
    {
        var files = new Files(); files.RetainEvidence();
        await File.WriteAllTextAsync(files.Marker, "authored retained evidence");
        using var denyDelete = new FileStream(files.Marker, FileMode.Open, FileAccess.Read, FileShare.None);
        var primary = new IOException("authored original fixture assertion failure");
        var observed = await Throws<IOException>(() => CompleteFixtureAsync(() => throw primary, files.Dispose));
        Check(ReferenceEquals(observed, primary) && Directory.Exists(files.Root) && File.Exists(files.Marker),
            "Fixture deletion masked the original failure or removed lock-held evidence.");
        var cleanup = new IOException("authored actual fixture cleanup failure");
        var combined = await Throws<AggregateException>(() => CompleteFixtureAsync(() => throw primary, () => throw cleanup));
        Check(combined.InnerExceptions.SequenceEqual(new Exception[] { primary, cleanup }),
            "Fixture wrapper dropped, reordered or substituted original/cleanup causes.");
        var cleanupOnly = await Throws<IOException>(() => CompleteFixtureAsync(() => Task.CompletedTask, () => throw cleanup));
        Check(ReferenceEquals(cleanupOnly, cleanup), "A genuine fixture cleanup failure became success.");
        // Retain the small deny-delete evidence after closing its actual handle, as the host fixture does.
    }

    private static async Task UnboundInitializer()
    {
        await using var registry = new ExtensionRegistry();
        var failure = await Throws<ExtensionRegistrationException>(() =>
            registry.ActivateAsync("unbound.shutdown." + Guid.NewGuid().ToString("N"), new NativeShutdownPlacementFixture.Entry()));
        Check(ContainsMessage(failure, "Shutdown fixture has no scoped settings bridge.") &&
            !ContainsMessage(failure, "Shutdown fixture settings were not bound.") && registry.CaptureSnapshot().Registrations.IsEmpty,
            "Unbound initializer cleanup masked its original cause or published registrations.");
    }

    private static Task WithFiles(Func<Files, Task> body)
    {
        var files = new Files();
        return CompleteFixtureAsync(() => body(files), files.Dispose);
    }
    private static async Task CompleteFixtureAsync(Func<Task> body, Action cleanup)
    {
        Exception? primary = null;
        try { await body().ConfigureAwait(false); } catch (Exception error) { primary = error; }
        try { cleanup(); }
        catch (Exception error)
        {
            if (primary is not null) throw new AggregateException(primary, error);
            throw;
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-shutdown-placement-" + Guid.NewGuid().ToString("N"));
        internal string Session => Path.Combine(Root, "session.jsonl");
        internal string Marker => Path.Combine(Root, "order.txt");
        internal string View => Path.Combine(Root, "view.json");
        private string Package => Path.Combine(Root, "package");
        private string Snapshots => Path.Combine(Root, "snapshots");
        private string Manifest => Path.Combine(Root, "manifest.json");
        private string Approval => Path.Combine(Root, "approval.json");
        private string Script => Path.Combine(Root, "script.json");
        private readonly string ownerId = "fixture.shutdown." + Guid.NewGuid().ToString("N");
        private string SettingsKey => "PiSharp.ShutdownPlacementFixture." + ownerId;
        private JsonData? bridgedSettings;
        private bool bridgeOwned;
        private string? hostDiagnostics;
        private byte[]? hostOutput;
        private bool disposed;
        internal Files() => Directory.CreateDirectory(Root);
        internal void RetainEvidence() => Directory.CreateDirectory(Snapshots);
        internal void RecordHostDiagnostics(string diagnostics, byte[] output)
        { hostDiagnostics = diagnostics; hostOutput = output; }
        internal string[] Args(bool live) => new[] { "session", "rpc", "--session", Session, "--workspace", Root,
            "--session-mode", "new-memory", "--extension-package", Package, "--extension-manifest", Manifest,
            "--extension-approval", Approval, "--extension-snapshot-root", Snapshots }.Concat(live
                ? new[] { "--live", "--provider", "openai", "--model", "gpt-4o-mini" }
                : new[] { "--offline-script", Script, "--offline-api", "openai-completions" }).ToArray();
        internal async Task Publish(bool fail)
        {
            Directory.CreateDirectory(Package); Directory.CreateDirectory(Snapshots);
            NativeSessionFixturePackage.CopyTo(Package);
            await File.WriteAllTextAsync(Path.Combine(Package, "shutdown-settings.json"), JsonSerializer.Serialize(
                new { marker = Marker, view = View, throwHandler = fail, throwDispose = fail }));
            var hashes = Directory.GetFiles(Package).Order(StringComparer.Ordinal).ToDictionary(file => Path.GetFileName(file)!,
                file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.Ordinal);
            var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id = ownerId, packageVersion = "0.0.1",
                hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = NativeSessionFixturePackage.AssemblyFile,
                entryType = "NativeShutdownPlacementFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                requiredFeatures = new[] { "owned-descriptor-callbacks" }, declaredCapabilities = new[] { "observations" },
                resourcePaths = hashes.Keys.Where(name => name != NativeSessionFixturePackage.AssemblyFile).ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
            await File.WriteAllTextAsync(Manifest, manifest);
            await File.WriteAllTextAsync(Approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution",
                packageRoot = Package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), artifactHashes = hashes,
                sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
                sessionPath = Session, workspace = Root, snapshotRoot = Snapshots, enabledTools = Array.Empty<string>() }));
            await File.WriteAllTextAsync(Script, JsonSerializer.Serialize(new { schemaVersion = 1,
                turns = new[] { SessionCommandTests.CompletionsText("unused shutdown fixture bytes") } }));
            bridgedSettings = JsonData.Parse(await File.ReadAllTextAsync(Path.Combine(Package, "shutdown-settings.json")));
            if (AppContext.GetData(SettingsKey) is not null) throw new InvalidOperationException("Fixture settings key is already owned.");
            AppContext.SetData(SettingsKey, bridgedSettings);
            bridgeOwned = true;
        }
        public void Dispose()
        {
            if (disposed) return;
            var full = Path.GetFullPath(Root);
            Check(Path.GetDirectoryName(full) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(full).StartsWith("PiSharp-shutdown-placement-", StringComparison.Ordinal), "Unowned fixture cleanup path.");
            var failures = new List<Exception>();
            if (hostDiagnostics is not null)
            {
                try { File.WriteAllText(Path.Combine(full, "host-diagnostics.jsonl"), hostDiagnostics); }
                catch (Exception error) { failures.Add(error); }
                try { File.WriteAllBytes(Path.Combine(full, "host-output.jsonl"), hostOutput!); }
                catch (Exception error) { failures.Add(error); }
            }
            if (bridgeOwned && bridgedSettings is not null)
            {
                try
                {
                    if (!ReferenceEquals(AppContext.GetData(SettingsKey), bridgedSettings))
                        throw new InvalidOperationException("Fixture settings bridge ownership changed during the original host operation.");
                    AppContext.SetData(SettingsKey, null); bridgedSettings = null; bridgeOwned = false;
                }
                catch (Exception error) { failures.Add(error); }
            }
            disposed = true;
            // Published snapshots can deliberately retain read handles after failed plugin cleanup.
            // Keep package/snapshot/marker evidence without trying to delete or force-unload it.
            // Actual host/loader cleanup failures are still asserted by Host and never suppressed.
            if (!Directory.Exists(Package) && !Directory.Exists(Snapshots))
                try { Directory.Delete(full, true); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }
    private sealed class RejectHttp : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; throw new InvalidOperationException("Shutdown fixture must not send HTTP."); }
    }
    private sealed class FaultedInput(IOException? failure) : MemoryStream
    {
        internal IOException? Failure => failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            failure is null ? ValueTask.FromResult(0) : ValueTask.FromException<int>(failure);
    }
    private sealed class ReporterWriter(IOException? failure) : StringWriter
    {
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            if (failure is not null && buffer.Span.Contains("\"eventName\":\"session_shutdown\"", StringComparison.Ordinal)) throw failure;
            return base.WriteAsync(buffer, token);
        }
    }
    private sealed class Observer : IRpcExtensionUiPresentationObserver
    {
        public ValueTask PublishedAsync(RpcExtensionUiPresentation presentation, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask RetiredAsync(RpcExtensionUiRetirement retirement, CancellationToken token) => ValueTask.CompletedTask;
    }
    private sealed class Plugin(Action<IExtensionRegistry> initialize, Func<ValueTask>? dispose = null) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        { token.ThrowIfCancellationRequested(); initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reach(Task gate, Task original)
    { if (await Task.WhenAny(gate, original) == original) { await original; throw new InvalidOperationException("Original completed before held boundary."); } await gate; }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static bool Contains(Exception error, Exception marker) => ReferenceEquals(error, marker) ||
        error is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => Contains(inner, marker)) ||
        error.InnerException is { } cause && Contains(cause, marker);
    private static bool ContainsMessage(Exception error, string message) => error.Message == message ||
        error is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => ContainsMessage(inner, message)) ||
        error.InnerException is { } cause && ContainsMessage(cause, message);
}
