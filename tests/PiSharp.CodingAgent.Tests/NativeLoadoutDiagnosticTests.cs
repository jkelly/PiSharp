using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

internal static class NativeLoadoutDiagnosticTests
{
    internal const string Prefix = "native loadout diagnostic ";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "actual loader binding bootstrap and session startup deliver sanitized captured occurrences once", Startup),
        (Prefix + "actual activation close retires generation and joins held original reporter before unload", CloseJoin),
        (Prefix + "reporter initiated profile disposal refuses before close admission and external close still joins", ReentrantClose),
        (Prefix + "cleanup attempts every original settlement and disposal retaining exact distinct failures", CleanupFailures)
    ];
    private static async Task Startup()
    {
        using var fixture = new Fixture(); var seen = new List<ExtensionEventDiagnostic>();
        var beforePrompt = -1; var providerObserved = false; var providerEntered = false; var diagnosticsAtProvider = -1;
        await using var profile = await fixture.Profile((diagnostic, _) => { seen.Add(diagnostic); return ValueTask.CompletedTask; },
            [JsonData.Parse(JsonSerializer.Serialize(SessionCommandTests.CompletionsText("loadout response")))], _ =>
            {
                providerEntered = true; diagnosticsAtProvider = seen.Count;
                Check(beforePrompt >= 0 && seen.Count > beforePrompt, "Actual provider request preceded owned loadout diagnostic delivery.");
                providerObserved = true; return ValueTask.CompletedTask;
            });
        Check(seen.Count == 0, "Synchronous binding construction started reporter effects.");
        var sequence = 0;
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "loadout-startup",
            timestamp = "2026-10-05T00:00:00.000Z", cwd = profile.Workspace }));
        await using var session = await PersistentAgentSession.CreateAsync(fixture.Log, header, profile.Registry,
            profile.SelectedModel, () => 1, () => "entry-" + ++sequence);
        await session.ConfigureAsync(new(SystemMessage: new TranscriptEntry("system", profile.InitialSystem)));
        profile.AttachOwner(session); await profile.ApplyInitialToolSelectionAsync(session, default);
        await profile.StartLifecycleAsync("new", default);
        Check(seen.Count > 0 && seen.All(item => item.EventName == "prepare_loadout"), "Actual startup did not deliver preparation failures.");
        var count = seen.Count; await profile.DrainLoadoutDiagnosticsAsync(default);
        Check(seen.Count == count && !JsonSerializer.Serialize(seen).Contains("private authored", StringComparison.Ordinal),
            "Startup replayed a capture or exposed original plugin exception text.");
        Check(session.Snapshot.Fault is null && session.GetActiveTools().Contains(NativeLoadoutFailureFixture.Entry.Tool),
            "Ordinary fallback registration/session state changed.");
        var beforeSelection = seen.Count;
        await session.SetActiveToolsAsync(["read", NativeLoadoutFailureFixture.Entry.Tool]);
        Check(seen.Count > beforeSelection, "Actual tool selection did not invoke the injected core drain.");
        beforePrompt = seen.Count;
        // Awaited selection above already drained its occurrences. Capture one real pending
        // preparation now; the prompt must join its delivery before the response-body callback.
        session.ScheduleToolActivation(["read", "write", NativeLoadoutFailureFixture.Entry.Tool]);
        Check(seen.Count == beforePrompt, "Synchronous selection unexpectedly started diagnostic delivery.");
        var result = await session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"loadout request","timestamp":1}""")));
        Check(providerObserved && profile.UsedTurns == 1 && session.Snapshot.Fault is null &&
            result.Reason == PiSharp.Agent.AgentLoopStopReason.Completed && result.Turns.Length == 1 &&
            result.Turns[0].Result.Chat.Failure is null && result.Turns[0].Result.CleanupFailure is null,
            "Actual injected request boundary did not settle authored offline transport. Witness: " + JsonSerializer.Serialize(new
            {
                providerEntered, providerObserved, beforePrompt, diagnosticsAtProvider, diagnosticsAfterPrompt = seen.Count,
                usedTurns = profile.UsedTurns, sessionFault = session.Snapshot.Fault, reason = result.Reason.ToString(),
                turnCount = result.Turns.Length, rejectedAssistant = result.RejectedAssistant,
                turns = result.Turns.Select(turn => new
                {
                    turn.TurnIndex, stopReason = turn.Result.Chat.Message.StopReason.ToString(),
                    turn.Result.Chat.Failure, turn.Result.Chat.NativeDiagnostic, turn.Result.Chat.NativeCleanupDiagnostic,
                    turn.Result.CleanupFailure
                })
            }));
        Check(seen.Count == beforePrompt + 1 && session.GetActiveTools().SequenceEqual(["read", "write", NativeLoadoutFailureFixture.Entry.Tool]),
            "Pending selection was not published with exactly one preparation diagnostic.");
        await profile.DrainLoadoutDiagnosticsAsync(default);
        Check(seen.Count == beforePrompt + 1, "Prompt delivery replayed a captured occurrence.");
    }
    private static async Task CloseJoin()
    {
        var fixture = new Fixture(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken reporterLifetime = default;
        OfflineSessionProfile? profile = null; Task? drain = null, close = null; Exception? primary = null;
        try
        {
            profile = await fixture.Profile(async (_, token) => { reporterLifetime = token; started.SetResult(); await release.Task; });
            drain = profile.DrainLoadoutDiagnosticsAsync(default).AsTask(); await started.Task;
            close = profile.DisposeAsync().AsTask(); await Task.Yield();
            Check(reporterLifetime.IsCancellationRequested && !close.IsCompleted && !drain.IsCompleted,
                "Activation close detached the original reporter or failed to retire its generation.");
            release.SetResult(); await drain; await close;
        }
        catch (Exception error) { primary = error; }
        finally
        {
            release.TrySetResult();
            await SettleCleanupAsync(primary, () => drain ?? Task.CompletedTask,
                () => close ?? profile?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                () => { fixture.Dispose(); return Task.CompletedTask; });
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task ReentrantClose()
    {
        var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OfflineSessionProfile? profile = null; CancellationToken reporterLifetime = default; var refused = false;
        async ValueTask Report(PiSharp.Extensions.Events.ExtensionEventDiagnostic _, CancellationToken token)
        {
            reporterLifetime = token;
            try { await profile!.DisposeAsync(); }
            catch (InvalidOperationException error) when (error.Message == "A diagnostic reporter cannot dispose its own activation.") { refused = true; }
            finally { started.TrySetResult(); }
            await release.Task;
        }
        Task? drain = null, close = null; Exception? primary = null;
        try
        {
            profile = await fixture.Profile(Report);
            drain = profile.DrainLoadoutDiagnosticsAsync(default).AsTask(); await started.Task;
            Check(refused && !reporterLifetime.IsCancellationRequested && !drain.IsCompleted,
                "Reporter disposal admitted retirement or failed to refuse deterministically.");
            // A genuine leased preparation remains possible: reporter refusal did not unload its generation.
            var selection = profile.Registry.Resolve(profile.SelectedModel, [new TranscriptEntry("system", profile.InitialSystem)]);
            Check(selection.LoadoutDiagnostics.Any(item => item.Code == "CallbackFailure"), "Reporter disposal retired actual callback ownership.");
            close = profile.DisposeAsync().AsTask(); await Task.Yield();
            Check(reporterLifetime.IsCancellationRequested && !close.IsCompleted && !drain.IsCompleted,
                "External close reused a poisoned disposal or bypassed the original reporter join.");
            release.SetResult(); await drain; await close;
        }
        catch (Exception error) { primary = error; }
        finally
        {
            release.TrySetResult();
            await SettleCleanupAsync(primary, () => drain ?? Task.CompletedTask,
                () => close ?? profile?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                () => { fixture.Dispose(); return Task.CompletedTask; });
        }
    }
    private static async Task SettleCleanupAsync(Exception? primary, params Func<Task>[] originals)
    {
        var failures = new List<Exception>(); if (primary is not null) failures.Add(primary);
        foreach (var original in originals)
            try { await original(); }
            catch (Exception error) { if (!failures.Any(retained => ReferenceEquals(retained, error))) failures.Add(error); }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Fixture operation and original cleanup failed.", failures);
    }
    private static async Task CleanupFailures()
    {
        var operation = new InvalidOperationException("original operation"); var drain = new IOException("original drain");
        var disposal = new InvalidOperationException("original disposal"); var calls = new List<string>();
        try
        {
            await SettleCleanupAsync(operation,
                () => { calls.Add("drain"); return Task.FromException(drain); },
                () => { calls.Add("disposal"); return Task.FromException(disposal); },
                () => { calls.Add("fixture"); return Task.CompletedTask; });
            throw new InvalidOperationException("Distinct original failures were discarded.");
        }
        catch (AggregateException error)
        {
            Check(calls.SequenceEqual(["drain", "disposal", "fixture"]) && error.InnerExceptions.Count == 3 &&
                ReferenceEquals(error.InnerExceptions[0], operation) && ReferenceEquals(error.InnerExceptions[1], drain) &&
                ReferenceEquals(error.InnerExceptions[2], disposal), "Cleanup skipped an owner or masked an original exception identity.");
        }
        try { await SettleCleanupAsync(drain, () => Task.FromException(drain)); }
        catch (IOException error) { Check(ReferenceEquals(error, drain), "Single original failure identity was replaced."); return; }
        throw new InvalidOperationException("Original drain failure was swallowed.");
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        private readonly string _root; private readonly NativeExtensionConfiguration _configuration;
        internal string Log => Path.Combine(_root, "session.jsonl");
        internal Fixture()
        {
            _root = Path.Combine(_parent, "pisharp-loadout-diagnostics-" + Guid.NewGuid().ToString("N"));
            var package = Path.Combine(_root, "package"); var snapshots = Path.Combine(_root, "snapshots");
            Directory.CreateDirectory(package); Directory.CreateDirectory(snapshots); NativeSessionFixturePackage.CopyTo(package);
            var hashes = Directory.GetFiles(package).Order(StringComparer.Ordinal).ToDictionary(file => Path.GetFileName(file)!,
                file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.Ordinal);
            var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.loadout", packageVersion = "0.0.1",
                hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = NativeSessionFixturePackage.AssemblyFile,
                entryType = "NativeLoadoutFailureFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" }, requiredFeatures = new[] { "owned-descriptor-callbacks" },
                declaredCapabilities = new[] { "tools" }, resourcePaths = hashes.Keys.Where(name => name != NativeSessionFixturePackage.AssemblyFile).ToArray(),
                explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
            var manifestPath = Path.Combine(_root, "manifest.json"); var approvalPath = Path.Combine(_root, "approval.json");
            File.WriteAllText(manifestPath, manifest);
            File.WriteAllText(approvalPath, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution",
                packageRoot = package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), artifactHashes = hashes,
                sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
                sessionPath = Log, workspace = _root, snapshotRoot = snapshots, enabledTools = new[] { NativeLoadoutFailureFixture.Entry.Tool } }));
            _configuration = NativeExtensionConfiguration.Optional(package, manifestPath, approvalPath, snapshots, [NativeLoadoutFailureFixture.Entry.Tool])!;
        }
        internal Task<OfflineSessionProfile> Profile(Func<ExtensionEventDiagnostic, CancellationToken, ValueTask> report,
            ImmutableArray<JsonData> turns = default, Func<CancellationToken, ValueTask>? beforeSend = null) =>
            OfflineSessionProfile.CreateAsync(_root, Log, null, turns.IsDefault ? [] : turns, [], [], default,
                beforeSendAsync: beforeSend, offlineApi: "openai-completions", extension: _configuration, reportInputDiagnostic: report);
        public void Dispose()
        {
            if (Path.GetDirectoryName(_root) != _parent || !Path.GetFileName(_root).StartsWith("pisharp-loadout-diagnostics-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid fixture root.");
            Directory.Delete(_root, recursive: true);
        }
    }
}
