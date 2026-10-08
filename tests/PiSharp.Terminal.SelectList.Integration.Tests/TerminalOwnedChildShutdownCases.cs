using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;

// AUTHORED ONLY / UNCOMPILED / UNEXECUTED. Requires an explicitly allocated physical
// child invocation from the complete admitted CodingAgent test output. No synthetic process.
internal static class TerminalOwnedChildShutdownCases
{
    private const string PackageId = "fixture.shutdown.owned-child";
    private const string EntryType = "OwnedChildShutdownFixture.Entry";
    private const string ChildSwitch = "--owned-shutdown-child-fixture";
    private const string ConfigurationEnvironment = "PISHARP_SHUTDOWN_CHILD_CONFIGURATION";
    private static readonly string[] ChildErrors =
    ["AUTHORED_OWNED_CHILD_EXIT_CLEANUP", "AUTHORED_OWNED_CHILD_STDOUT_CLOSE", "AUTHORED_OWNED_CHILD_STDERR_CLOSE"];

    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("two-phase-terminal-owned-child-exit-and-both-stream-originals-before-success", e => Combined(args[1], e, false)),
        ("two-phase-terminal-leave-restore-and-owned-child-cleanup-original-failures", e => Combined(args[1], e, true))
    ];

    private static async Task Combined(string reviewRoot, ConsumerEvidence e, bool failCleanup)
    {
        // Create before fixture activation: baseline creation must not own a child.
        var files = await StartupOwnedFiles.Create(reviewRoot, plugin: false, e);
        var childAssembly = Path.Combine(Path.GetFullPath(reviewRoot), "tests", "PiSharp.CodingAgent.Tests",
            "bin", "Release", "net10.0", "PiSharp.CodingAgent.Tests.dll");
        var dotnetHost = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Admitted host required."));
        Check(File.Exists(childAssembly) && Path.GetFullPath(Assembly.GetExecutingAssembly().Location) != childAssembly,
            "Physical child must use the separately admitted CodingAgent fixture output, not the same-named terminal consumer.");
        var childRoot = Path.Combine(files.Root, "owned-shutdown-child"); Directory.CreateDirectory(childRoot);
        Mark(childRoot, "fixture.owned");
        var configuration = Path.Combine(childRoot, "configuration.json");
        await NewFile(configuration, JsonSerializer.Serialize(new { Root = childRoot, DotnetHost = dotnetHost,
            ChildAssembly = childAssembly, FailCleanup = failCleanup }));
        var extensionArgs = await PreparePackage(reviewRoot, files, childAssembly, e);
        var fixtureSource = Path.Combine(Path.GetFullPath(reviewRoot), "tests", "PiSharp.CodingAgent.Tests", "OwnedChildShutdownFixture.cs");
        e.Observe("allocated-original-owned-child-invocation", new { dotnetHost, hostSha256 = Hash(dotnetHost), childAssembly,
            childAssemblySha256 = Hash(childAssembly), childSwitch = ChildSwitch, childRoot, failCleanup,
            fixtureSource, fixtureSourceSha256 = Hash(fixtureSource),
            fullGraphAdmittedByCentralReceipt = true, noSyntheticRunner = true });
        var previousConfiguration = Environment.GetEnvironmentVariable(ConfigurationEnvironment);
        var leaveError = failCleanup ? new IOException("AUTHORED_COMBINED_TERMINAL_LEAVE") : null;
        var restoreError = failCleanup ? new IOException("AUTHORED_COMBINED_TERMINAL_RESTORE") : null;
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace, leaveFailure: leaveError);
        SelectorHold? drainRead = null;
        var clock = new TerminalInputDrainCases.Clock(() => drainRead = terminal.HoldNextRead());
        var restore = new SelectorHold(); var restores = 0;
        var phaseOne = new TaskCompletionSource<RpcSessionShutdownSettlement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(); using var errors = new StringWriter();
        Task<int>? original = null; RpcSessionShutdownSettlement? captured = null;
        async ValueTask Restore()
        {
            Interlocked.Increment(ref restores); restore.Entered.TrySetResult(); await restore.Release.Task;
            if (restoreError is not null) throw restoreError;
        }
        try
        {
            // Scoped test configuration only; the original complete admitted output is never changed.
            Environment.SetEnvironmentVariable(ConfigurationEnvironment, configuration);
            original = TerminalSessionCommand.RunObservedConfiguredAsync(files.Args().Concat(extensionArgs).ToArray(),
                terminal, terminal, errors, trace.Observe, files.Configuration, cancellation.Token,
                shutdownTimeProvider: clock, restoreTerminalAndJoin: Restore,
                shutdownObserver: settlement => phaseOne.TrySetResult(settlement));
            try { await Milestone(childRoot, "child.ready", original); await Milestone(childRoot, "owner.started", original); }
            catch (Exception error)
            {
                e.Observe("owned-child-readiness-failure", new { milestone = "child.ready/owner.started", childRoot,
                    exception = ConsumerException.From(error), originalCompleted = original.IsCompleted,
                    activationDiagnostics = errors.ToString(), terminal = terminal.Evidence,
                    markers = Directory.GetFiles(childRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray() });
                throw;
            }
            using var owner = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(childRoot, "owner.started")));
            using var child = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(childRoot, "child.ready")));
            var childId = child.RootElement.GetProperty("processId").GetInt32();
            Check(childId > 0 && childId != Environment.ProcessId && childId == owner.RootElement.GetProperty("processId").GetInt32() &&
                child.RootElement.GetProperty("protocol").GetString() == ChildSwitch && owner.RootElement.GetProperty("protocol").GetString() == ChildSwitch,
                "Readiness identity differs from the actual owned Process handle/protocol.");
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1);
            var leave = terminal.HoldWrite("\u001b[?1049l"); await terminal.Feed("\u0004");
            captured = await phaseOne.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await clock.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); await drainRead!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(50); await drainRead.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!original.IsCompleted && !captured.RuntimeCleanup.IsCompleted && !leave.Entered.Task.IsCompleted &&
                !Exists(childRoot, "dispose.entered"), "Original drain detached or extension disposal preceded the terminal acknowledgment.");
            await files.AssertWriterOwned(); drainRead.Release.TrySetResult(); await leave.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!restore.Entered.Task.IsCompleted && !Exists(childRoot, "dispose.entered"), "Original leave write did not hold restore/extension release.");
            leave.Release.TrySetResult(); await restore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var writesAtRestore = terminal.Snapshot.WriteWorkersStarted;
            Check(restores == 1 && !original.IsCompleted && !captured.RuntimeCleanup.IsCompleted &&
                !Exists(childRoot, "dispose.entered") && captured.Session is { IsDisposed: false },
                "Owned extension child release began before original terminal restoration joined.");
            terminal.AssertJoined(); await files.AssertWriterOwned(); restore.Release.TrySetResult();
            await Milestone(childRoot, "dispose.entered", original); await Milestone(childRoot, "stop.requested", original);
            Check(!original.IsCompleted && !captured.RuntimeCleanup.IsCompleted && !Exists(childRoot, "exit.joined") && errors.ToString().Length == 0,
                "Terminal error detached the original physical child exit or emitted final diagnostics early.");
            Mark(childRoot, "exit.release"); await Milestone(childRoot, "exit.joined", original);
            await Milestone(childRoot, "stdout.eof", original); await Milestone(childRoot, "stderr.eof", original);
            Check(!original.IsCompleted && !captured.RuntimeCleanup.IsCompleted && !Exists(childRoot, "stdout.closed") && !Exists(childRoot, "stderr.closed"),
                "Physical exit/EOF skipped held original redirected cleanup tasks.");
            Mark(childRoot, "stdout.release"); await Milestone(childRoot, "stdout.closed", original);
            Check(!original.IsCompleted && !captured.RuntimeCleanup.IsCompleted && !Exists(childRoot, "stderr.closed"),
                "Stdout cleanup failure detached the original stderr cleanup.");
            Mark(childRoot, "stderr.release"); var result = await original;
            var runtimeFailures = await captured.RuntimeCleanup; var completionFailures = await captured.Completion;
            var runtimeCauses = Causes(runtimeFailures); var completionCauses = Causes(completionFailures);
            Check(result == (failCleanup ? 1 : 0) && restores == 1 && Exists(childRoot, "owner.joined") && Exists(childRoot, "stderr.closed"),
                "Original terminal/child joins were omitted, repeated, or turned cleanup failure into success.");
            Check(terminal.Snapshot.WriteWorkersStarted == writesAtRestore, "Runtime diagnostics repainted the restored terminal.");
            if (failCleanup)
            {
                Check(completionCauses.Any(error => ReferenceEquals(error, leaveError)) &&
                    completionCauses.Any(error => ReferenceEquals(error, restoreError)), "Final settlement replaced an original terminal exception.");
                foreach (var message in ChildErrors)
                {
                    var actual = runtimeCauses.Where(error => error is IOException && error.Message == message).ToArray();
                    Check(actual.Length == 1 && completionCauses.Any(error => ReferenceEquals(error, actual[0])),
                        "Final settlement lost/replaced an original joined extension-child cleanup error: " + message);
                }
                Check(errors.ToString().Contains("CleanupFailed", StringComparison.Ordinal), "Combined cleanup failure lost its real failure diagnostic.");
            }
            else Check(runtimeFailures.IsEmpty && completionFailures.IsEmpty && errors.ToString().Length == 0,
                "Clean physically joined child/terminal shutdown retained a synthetic failure.");
            terminal.AssertJoined(); await files.Complete(e);
            e.Observe("actual-terminal-and-owned-extension-child-original-joins", new { result, failCleanup, restores,
                ownerIdentity = owner.RootElement.Clone(), childIdentity = child.RootElement.Clone(),
                childMarkerFiles = Directory.GetFiles(childRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray(),
                postPhysicalCleanupFaults = runtimeCauses.Where(error => ChildErrors.Contains(error.Message, StringComparer.Ordinal)).Select(error => error.Message).ToArray(),
                originalExceptionInstancesRetained = true, terminal = terminal.Evidence, trace = trace.Rows() });
        }
        finally
        {
            try
            {
                // Exact fixture release protocol. These are controls, never exit/stream settlement receipts.
                var cleanupFailures = new List<Exception>();
                int? originalResult = null;
                try { ReleaseAll(childRoot); } catch (Exception error) { cleanupFailures.Add(error); }
                try { cancellation.Cancel(); } catch (Exception error) { cleanupFailures.Add(error); }
                terminal.End(); terminal.Release(); restore.Release.TrySetResult();
                if (original is not null) try { originalResult = await original; } catch (Exception error) { cleanupFailures.Add(error); }
                captured ??= phaseOne.Task.IsCompletedSuccessfully ? phaseOne.Task.Result : null;
                if (captured is not null)
                {
                    try { await captured.RuntimeCleanup; } catch (Exception error) { cleanupFailures.Add(error); }
                    try { await captured.Completion; } catch (Exception error) { cleanupFailures.Add(error); }
                }
                e.Observe("owned-child-original-fallback-settlement", new { originalResult, originalStarted = original is not null,
                    originalJoined = original?.IsCompleted, runtimeJoined = captured?.RuntimeCleanup.IsCompleted,
                    completionJoined = captured?.Completion.IsCompleted,
                    activationDiagnostics = errors.ToString(), cleanupFailures = cleanupFailures.Select(ConsumerException.From).ToArray(),
                    markers = Directory.GetFiles(childRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray() });
                try { terminal.AssertJoined(); } catch (Exception error) { cleanupFailures.Add(error); }
                if (cleanupFailures.Count > 0) throw new AggregateException("Combined fixture fallback failures after original joins.", cleanupFailures);
            }
            finally { Environment.SetEnvironmentVariable(ConfigurationEnvironment, previousConfiguration); }
        }
    }

    private static async Task<string[]> PreparePackage(string reviewRoot, StartupOwnedFiles files, string childAssembly, ConsumerEvidence e)
    {
        var package = Path.Combine(files.Root, "owned-child-package"); Directory.CreateDirectory(package);
        var manifestPath = Path.Combine(files.Root, "owned-child-manifest.json"); var approvalPath = Path.Combine(files.Root, "owned-child-approval.json");
        // The extension has a project-only publication; the physical child still names
        // the original, complete admitted CodingAgent executable and its genuine deps/config.
        var hashes = StartupOwnedFiles.PreparePublishedFixture(reviewRoot, package, e);
        var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id = PackageId, packageVersion = "0.0.1",
            hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = StartupOwnedFiles.FixtureAssembly + ".dll",
            entryType = EntryType, tfm = "net10.0", rids = new[] { "win-x64" }, requiredFeatures = Array.Empty<string>(), declaredCapabilities = Array.Empty<string>(),
            resourcePaths = hashes.Keys.Where(name => name != StartupOwnedFiles.FixtureAssembly + ".dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
        await NewFile(manifestPath, manifest);
        await NewFile(approvalPath, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution", packageRoot = package,
            manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), artifactHashes = hashes,
            sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
            sessionPath = files.Session, workspace = files.Root, snapshotRoot = files.Snapshots, enabledTools = Array.Empty<string>() }));
        e.Observe("actual-admitted-owned-child-native-package", new { package, childAssembly, entryType = EntryType,
            originalAssemblySha256 = Hash(childAssembly), manifestSha256 = Hash(manifestPath), approvalSha256 = Hash(approvalPath), artifacts = hashes });
        return ["--extension-package", package, "--extension-manifest", manifestPath, "--extension-approval", approvalPath, "--extension-snapshot-root", files.Snapshots];
    }

    private static Exception[] Causes(IEnumerable<Exception> failures)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        foreach (var error in failures) Visit(error);
        return seen.ToArray();
        void Visit(Exception error)
        {
            if (!seen.Add(error)) return;
            if (error is AggregateException aggregate) foreach (var cause in aggregate.InnerExceptions) Visit(cause);
            else if (error.InnerException is { } inner) Visit(inner);
        }
    }
    private static async Task Milestone(string root, string name, Task<int> original)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!Exists(root, name))
        {
            if (original.IsCompleted) { await original; throw new InvalidOperationException("Original terminal command ended before child milestone: " + name); }
            await Task.Delay(10, deadline.Token); // Original polling task itself is joined.
        }
    }
    private static bool Exists(string root, string name) => File.Exists(Path.Combine(root, name));
    private static void Mark(string root, string name) => File.WriteAllText(Path.Combine(root, name), "owned fixture\n");
    private static void ReleaseAll(string root)
    {
        var failures = new List<Exception>();
        foreach (var name in new[] { "stop.requested", "exit.release", "stdout.release", "stderr.release" })
            try { Mark(root, name); } catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("Owned fixture release protocol failed.", failures);
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static async Task NewFile(string path, string value)
    { await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await stream.WriteAsync(Encoding.UTF8.GetBytes(value)); await stream.FlushAsync(); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
