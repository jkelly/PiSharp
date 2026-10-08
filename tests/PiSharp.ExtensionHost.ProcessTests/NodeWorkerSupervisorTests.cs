using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.ExtensionHost.Supervision;

internal static class NodeWorkerSupervisorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static WorkerValue Json(string raw) => WorkerValue.FromJson(JsonData.Parse(raw));
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string nodePath, string repoRoot)
    {
        var context = new Context(dotnetHost, nodePath, repoRoot);
        yield return ("node-worker.launch-identity-entry-links-and-run-root-admission", () => Admission(context));
        yield return ("node-worker.real-runtime-clean-environment-and-owned-unicode-values", () => Runtime(context));
        yield return ("node-worker.startup-no-hello-wrong-generation-and-early-exit-joined", () => Startup(context));
        yield return ("node-worker.real-nested-callback-request-and-complete-progress", () => Nested(context));
        yield return ("node-worker.sent-cancellation-retains-slot-until-real-release", () => Cancellation(context));
        yield return ("node-worker.real-pipe-backpressure-queued-cancel-and-joined-writes", () => Backpressure(context));
        yield return ("node-worker.effect-before-exit-is-unknown-and-never-retried", () => UncertainExit(context));
        yield return ("node-worker.stderr-cap-and-stdout-contamination-stop-owned-process", () => Contamination(context));
        yield return ("node-worker.shutdown-grace-kills-and-joins-fixed-root-and-child", () => ProcessTree(context));
        yield return ("node-worker.generation-replacement-callback-cleanup-and-shared-dispose", () => Generation(context));
    }
    private static async Task Admission(Context context)
    {
        await using var fixture = new Fixture(context);
        var root = fixture.FreshRoot();
        ThrowsIo(() => NodeWorkerLaunch.ForProbe(Path.Combine(fixture.Parent, "missing-node.exe"), context.Repo, root, 3, 7));
        Check(!Path.Exists(root), "Missing-runtime validation created scratch.");
        var wrong = Path.Combine(fixture.Parent, "wrong-node.exe"); await File.WriteAllTextAsync(wrong, "authored non-executable bytes");
        ThrowsIo(() => NodeWorkerLaunch.ForProbe(wrong, context.Repo, root, 3, 7)); Check(!Path.Exists(root), "Wrong runtime started/created scratch.");
        var modifiedRepo = Path.Combine(fixture.Parent, "tampered-repo");
        Directory.CreateDirectory(Path.Combine(modifiedRepo, "tools/NodeWorker"));
        await File.WriteAllTextAsync(Path.Combine(modifiedRepo, NodeWorkerLaunch.EntryRelativePath), "// authored wrong entry\n");
        ThrowsIo(() => NodeWorkerLaunch.ForProbe(context.Node, modifiedRepo, root, 3, 7)); Check(!Path.Exists(root), "Entry tamper created scratch.");
        ThrowsIo(() => NodeWorkerLaunch.ForProbe(context.Node, context.Repo, Path.Combine(context.Repo, "forbidden-run"), 3, 7));
        Directory.CreateDirectory(root); ThrowsIo(() => NodeWorkerLaunch.ForProbe(context.Node, context.Repo, root, 3, 7));
        var link = Path.Combine(fixture.Parent, "linked-repo");
        var targetEntry = Path.Combine(context.Repo, NodeWorkerLaunch.EntryRelativePath);
        var targetBefore = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(targetEntry)));
        Equal(NodeWorkerLaunch.EntrySha256, targetBefore);
        // An actual task-local mount-point reparse point needs no symbolic-link privilege.
        // Failure to create/verify it still fails this group; no environment/security change or skip.
        CreateFixtureJunction(fixture.Parent, link, context.Repo);
        try
        {
            VerifyFixtureJunction(fixture.Parent, link, context.Repo);
            var linkedRoot = fixture.FreshRoot();
            ThrowsIo(() => NodeWorkerLaunch.ForProbe(context.Node, link, linkedRoot, 3, 7));
            Check(!Path.Exists(linkedRoot), "Linked launch input created scratch.");
        }
        finally
        {
            VerifyFixtureJunction(fixture.Parent, link, context.Repo);
            Directory.Delete(link, recursive: false); // Remove only the checked junction, never traverse the target.
            Check(!Path.Exists(link) && Directory.Exists(context.Repo), "Fixture junction removal changed its target.");
            Equal(targetBefore, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(targetEntry))));
        }
        var launch = fixture.Launch();
        await File.WriteAllTextAsync(Path.Combine(fixture.Parent, "late-root-owner.txt"), "root ownership guard");
        Directory.CreateDirectory(launch.RunRoot);
        await FailsIo(() => NodeWorkerSupervisor.StartProbeAsync(launch));
        Check(!File.Exists(Path.Combine(launch.RunRoot, "launch.receipt.json")), "Late-existing root was adopted.");
    }
    private static async Task Runtime(Context context)
    {
        await using var fixture = new Fixture(context); var owner = await fixture.Start();
        var runtime = (await owner.RequestAsync("probe.runtime", WorkerValue.Absent)).Json!.Value;
        Equal(NodeWorkerLaunch.RuntimeVersion, runtime.GetProperty("version").GetString()); Equal("win32", runtime.GetProperty("platform").GetString());
        Equal("x64", runtime.GetProperty("architecture").GetString()); Equal(owner.ProcessId, runtime.GetProperty("pid").GetInt32());
        var run = fixture.RootOf(owner); Equal(run, runtime.GetProperty("cwd").GetString());
        var environment = runtime.GetProperty("environment").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["HOME"] = Path.Combine(run, "home"), ["USERPROFILE"] = Path.Combine(run, "home"), ["APPDATA"] = Path.Combine(run, "appdata"),
          ["LOCALAPPDATA"] = Path.Combine(run, "localappdata"), ["TMP"] = Path.Combine(run, "temp"), ["TEMP"] = Path.Combine(run, "temp"),
          ["TMPDIR"] = Path.Combine(run, "temp"), ["SystemRoot"] = Directory.GetParent(Environment.SystemDirectory)!.FullName,
          ["WINDIR"] = Directory.GetParent(Environment.SystemDirectory)!.FullName };
        Equal(expected.Count, environment.Count);
        foreach (var item in expected) Equal(item.Value, environment[item.Key]);
        foreach (var value in new[] { WorkerValue.Absent, WorkerValue.Undefined, Json("null"), Json("{\"s\":\"\\u0000a\u2028b\u2029\uD83D\uDC4B\",\"array\":[null,true,1],\"empty\":\"\"}") })
        {
            var result = await owner.RequestAsync("probe.echo", value).WaitAsync(Deadline); Equal(value.Presence, result.Presence);
            if (value.Json is { } json) EqualJson(json.Value, result.Json!.Value, "/echo");
        }
        await owner.ProbeLivenessAsync();
        await owner.DisposeAsync(); var end = await owner.Termination;
        VerifyJoined(end); Equal(0, end.ExitCode); Equal(false, end.KillAttempted); Equal(0, end.Failures.Length);
        Equal("probe-start\nprobe-stop\n", Encoding.UTF8.GetString(end.Stderr.AsSpan()));
        Equal(end.Stderr.Length, checked((int)end.ObservedStderrBytes));
        Check(File.Exists(Path.Combine(run, "stderr.bin")) && File.Exists(Path.Combine(run, "termination.json")), "Owned evidence is missing.");
    }
    private static async Task Startup(Context context)
    {
        await using var fixture = new Fixture(context);
        foreach (var mode in new[] { NodeWorkerProbeMode.NoHello, NodeWorkerProbeMode.WrongGeneration, NodeWorkerProbeMode.ExitBeforeHello })
        {
            var launch = fixture.Launch(mode: mode, options: new(StartupMilliseconds: 700));
            var error = await FailsOwner(() => NodeWorkerSupervisor.StartProbeAsync(launch)); VerifyJoined(error.Termination);
            Check(error.Termination.Failures.All(code => new[] { "Startup", "StartupTimeout", "UnexpectedExit", "Protocol", "ProtocolShutdown" }.Contains(code)), "Unexpected startup cleanup failure.");
            if (mode == NodeWorkerProbeMode.NoHello) Check(error.Termination.Failures.Contains("StartupTimeout"), "Missing hello did not hit the owned startup bound.");
            if (mode == NodeWorkerProbeMode.WrongGeneration) Equal(WorkerProtocolFailure.StaleGeneration, error.Termination.ProtocolFailure);
            if (mode == NodeWorkerProbeMode.ExitBeforeHello) Equal(19, error.Termination.ExitCode);
        }
    }
    private static Task Nested(Context context) => RunWithFixture(context, async fixture =>
    {
        var owner = await fixture.Start(); var trace = new List<string>();
        var handle = owner.RegisterCallback("authored-extension", 1, async (request, token) =>
        {
            Equal("probe.callback", request.Method); Equal(WorkerValuePresence.Undefined, request.Value.Presence); trace.Add("callback-enter");
            await request.ReportProgressAsync(Json("{\"stage\":\"native-callback\",\"nil\":null}"), token);
            var leaf = owner.StartRequest("probe.leaf", WorkerValue.Absent, cancellationToken: token);
            var leafProgress = new List<WorkerValue>(); await foreach (var step in leaf.Progress.WithCancellation(token)) leafProgress.Add(step);
            Equal(1, leafProgress.Count); EqualJson(JsonData.Parse("{\"stage\":\"leaf\",\"nil\":null}").Value, leafProgress[0].Json!.Value, "/leaf-progress");
            var final = await leaf.Result; trace.Add("callback-return"); return final;
        });
        var input = WorkerValue.FromJson(JsonData.Parse(JsonSerializer.Serialize(new { handle = new
        { ownerId = handle.OwnerId, ownerGeneration = handle.OwnerGeneration, registrationId = handle.RegistrationId, callbackId = handle.CallbackId } })));
        var call = owner.StartRequest("probe.nested", input); var progress = new List<WorkerValue>();
        await foreach (var step in call.Progress) progress.Add(step);
        var result = await call.Result.WaitAsync(Deadline); Equal(2, progress.Count);
        EqualJson(JsonData.Parse("{\"stage\":\"native-callback\",\"nil\":null}").Value, progress[0].Json!.Value, "/nested-progress/0");
        EqualJson(JsonData.Parse("{\"stage\":\"after-callback\"}").Value, progress[1].Json!.Value, "/nested-progress/1");
        EqualJson(JsonData.Parse("{\"text\":\"\\u0000a\\u2028b\\u2029\\uD83D\\uDC4B\",\"n\":1}").Value, result.Json!.Value, "/nested-final");
        Check(trace.SequenceEqual(["callback-enter", "callback-return"]), "Nested callback partial order changed.");
        await Until(() => owner.Snapshot.ActiveCallbacks == 0 && owner.Snapshot.PendingCalls == 0);
        await owner.DisposeAsync(); VerifyNaturalShutdown(await owner.Termination);
        await VerifyCleanupEvidence(fixture.RootOf(owner), owner.ProcessId);
    });
    private static Task Cancellation(Context context) => RunWithFixture(context, async fixture =>
    {
        var owner = await fixture.Start(protocol: new(MaximumPendingCalls: 2));
        using var stop = new CancellationTokenSource(); var held = owner.StartRequest("probe.hold", WorkerValue.Absent, cancellationToken: stop.Token);
        await using var progress = held.Progress.GetAsyncEnumerator(); Check(await progress.MoveNextAsync().AsTask().WaitAsync(Deadline), "Held request emitted no admission observation.");
        EqualJson(JsonData.Parse("{\"stage\":\"held\"}").Value, progress.Current.Json!.Value, "/held");
        stop.Cancel(); var cancelled = await FailsProtocol(() => held.Result, WorkerProtocolFailure.Cancelled); Equal(WorkerOutcome.Unknown, cancelled.Outcome);
        Equal(1, owner.Snapshot.PendingCalls);
        _ = await owner.RequestAsync("probe.release", WorkerValue.Absent).WaitAsync(Deadline);
        await Until(() => owner.Snapshot.PendingCalls == 0 && owner.Snapshot.PendingWrites == 0);
        Equal(false, await progress.MoveNextAsync());
        // Releasing immediately on admitted progress exercises the peer's real waiter-publication
        // boundary without adding cancellations or weakening the original exact-one-cancel assertion.
        for (var index = 0; index < 4; index++)
        {
            var immediate = owner.StartRequest("probe.hold", WorkerValue.Absent);
            await using var steps = immediate.Progress.GetAsyncEnumerator();
            Check(await steps.MoveNextAsync().AsTask().WaitAsync(Deadline), "Immediate release had no admitted waiter.");
            EqualJson(JsonData.Parse("{\"stage\":\"held\"}").Value, steps.Current.Json!.Value, "/immediate-held");
            _ = await owner.RequestAsync("probe.release", WorkerValue.Absent).WaitAsync(Deadline);
            Equal(WorkerValuePresence.Undefined, (await immediate.Result.WaitAsync(Deadline)).Presence);
            Equal(false, await steps.MoveNextAsync());
            await Until(() => owner.Snapshot.PendingCalls == 0 && owner.Snapshot.PendingWrites == 0);
        }
        _ = await owner.RequestAsync("probe.ping", WorkerValue.Absent);
        await owner.DisposeAsync(); var end = await owner.Termination; VerifyJoined(end);
        VerifyNaturalShutdown(end);
        var text = Encoding.UTF8.GetString(end.Stderr.AsSpan()); Equal(1, text.Split("probe-cancel:", StringSplitOptions.None).Length - 1);
        await VerifyCleanupEvidence(fixture.RootOf(owner), owner.ProcessId);
    });
    private static async Task Backpressure(Context context)
    {
        await using var fixture = new Fixture(context); var owner = await fixture.Start(options: new(ShutdownGraceMilliseconds: 150), protocol: new(MaximumPendingCalls: 3));
        _ = await owner.RequestAsync("probe.pause-input", WorkerValue.Absent);
        var large = owner.StartRequest("probe.echo", Json("{\"s\":\"" + new string('x', 700_000) + "\"}"));
        await Until(() => owner.Snapshot.PendingWrites == 1); await Task.Delay(100);
        Check(!large.Result.IsCompleted && owner.Snapshot.PendingWrites == 1, "Actual process pipe did not retain the large write.");
        using var stop = new CancellationTokenSource(); var queued = owner.StartRequest("probe.echo", WorkerValue.Undefined, cancellationToken: stop.Token); stop.Cancel();
        var unsent = await FailsProtocol(() => queued.Result, WorkerProtocolFailure.Cancelled); Equal(WorkerOutcome.NotSent, unsent.Outcome); Equal(2, owner.Snapshot.PendingCalls);
        fixture.Expect(owner, "ShutdownTimeout", "Protocol", "ProtocolShutdown");
        var one = owner.DisposeAsync().AsTask(); var two = owner.DisposeAsync().AsTask(); Check(ReferenceEquals(one, two), "Owned cleanup task was not shared.");
        var failure = await FailsOwner(() => one); Check(failure.Termination.Failures.Contains("ShutdownTimeout"), "Blocked pipe was not bounded by grace.");
        var sent = await FailsProtocol(() => large.Result); Equal(WorkerOutcome.Unknown, sent.Outcome);
        Check(sent.Failure is WorkerProtocolFailure.Transport or WorkerProtocolFailure.EndOfInput or WorkerProtocolFailure.Closed, "Unexpected write fault category.");
        VerifyJoined(failure.Termination); Equal(true, failure.Termination.KillAttempted); Equal(0, failure.Termination.Protocol.PendingWrites);
    }
    private static async Task UncertainExit(Context context)
    {
        await using var fixture = new Fixture(context); var owner = await fixture.Start();
        fixture.Expect(owner, "UnexpectedExit", "Protocol", "ProtocolShutdown");
        var call = owner.StartRequest("probe.effect-exit", WorkerValue.Absent);
        var failure = await FailsProtocol(() => call.Result); Equal(WorkerOutcome.Unknown, failure.Outcome);
        Check(failure.Failure is WorkerProtocolFailure.EndOfInput or WorkerProtocolFailure.Closed or WorkerProtocolFailure.Transport, "Unexpected effect-exit call category.");
        var ended = await FailsOwner(() => owner.Completion); VerifyJoined(ended.Termination); Equal(23, ended.Termination.ExitCode);
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.RootOf(owner), "effect.receipt.json")));
        Equal(1, receipt.RootElement.GetProperty("effects").GetInt32()); Equal(3L, receipt.RootElement.GetProperty("worker").GetInt64());
        Equal(7L, receipt.RootElement.GetProperty("session").GetInt64());
        ThrowsProtocol(() => owner.StartRequest("probe.effect-exit", WorkerValue.Absent), WorkerProtocolFailure.Closed);
        Equal(1, Directory.GetFiles(fixture.RootOf(owner), "effect.receipt.json").Length);
    }
    private static async Task Contamination(Context context)
    {
        await using var fixture = new Fixture(context);
        var noisy = await fixture.Start(options: new(MaximumStderrBytes: 64)); fixture.Expect(noisy, "StderrLimit", "Protocol", "ProtocolShutdown");
        var flood = noisy.StartRequest("probe.stderr-flood", WorkerValue.Absent); var error = await FailsOwner(() => noisy.Completion);
        VerifyJoined(error.Termination); Equal(true, error.Termination.StderrOverflow); Equal(64, error.Termination.Stderr.Length);
        Check(error.Termination.ObservedStderrBytes > 64 && error.Termination.Failures.Contains("StderrLimit"), "Stderr overflow facts are absent.");
        var floodResult = await FailsProtocol(() => flood.Result); Equal(WorkerOutcome.Unknown, floodResult.Outcome);
        var dirty = await fixture.Start(); fixture.Expect(dirty, "Protocol", "ProtocolShutdown");
        var contamination = dirty.StartRequest("probe.stdout-contamination", WorkerValue.Absent);
        var result = await FailsProtocol(() => contamination.Result, WorkerProtocolFailure.MalformedFrame); Equal(WorkerOutcome.Unknown, result.Outcome);
        var end = await FailsOwner(() => dirty.Completion); VerifyJoined(end.Termination); Equal(WorkerProtocolFailure.MalformedFrame, end.Termination.ProtocolFailure);
        var tail = await fixture.Start(options: new(MaximumStdoutAfterProtocolBytes: 64));
        var callbacks = 0;
        _ = tail.RegisterCallback("authored-tail-control", 1, (_, _) =>
        { Interlocked.Increment(ref callbacks); return ValueTask.FromResult(WorkerValue.Absent); });
        _ = await tail.RequestAsync("probe.shutdown-tail", WorkerValue.Absent).WaitAsync(Deadline);
        fixture.Expect(tail, "StdoutAfterProtocolLimit", "Protocol", "ProtocolShutdown");
        var overflow = await FailsOwner(() => tail.DisposeAsync().AsTask()); VerifyJoined(overflow.Termination);
        Equal(true, overflow.Termination.StdoutAfterProtocolOverflow); Equal(64, overflow.Termination.StdoutAfterProtocol.Length);
        Check(overflow.Termination.ObservedStdoutAfterProtocolBytes > 64 && overflow.Termination.Failures.Contains("StdoutAfterProtocolLimit"),
            "Post-protocol stdout drain was not independently bounded.");
        Equal(true, overflow.Termination.KillAttempted); Equal(true, overflow.Termination.StdoutAfterProtocolEof); Equal(0, callbacks);
        var prefix = await File.ReadAllBytesAsync(Path.Combine(fixture.RootOf(tail), "stdout-after-protocol.bin"));
        Check(prefix.AsSpan().SequenceEqual(overflow.Termination.StdoutAfterProtocol.AsSpan()), "Physical stdout prefix differs from owned facts.");
        using var attempted = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.RootOf(tail), "shutdown-tail.receipt.json")));
        Equal(131_072, attempted.RootElement.GetProperty("attemptedBytes").GetInt32());
    }
    private static async Task ProcessTree(Context context)
    {
        await using var fixture = new Fixture(context); var owner = await fixture.Start(mode: NodeWorkerProbeMode.IgnoreShutdown, options: new(ShutdownGraceMilliseconds: 150));
        var child = await owner.SpawnOwnedProbeChildAsync().WaitAsync(Deadline); Equal(owner.ProcessId, child.ParentProcessId);
        using var observedChild = Process.GetProcessById(child.ProcessId); Check(!observedChild.HasExited, "Fixed child was not alive before ownership test.");
        fixture.Expect(owner, "ShutdownTimeout"); var shared = owner.DisposeAsync().AsTask(); Check(ReferenceEquals(shared, owner.DisposeAsync().AsTask()), "Process-tree disposal was not shared.");
        var failure = await FailsOwner(() => shared); VerifyJoined(failure.Termination); Equal(true, failure.Termination.KillAttempted);
        Equal(child.ProcessId, failure.Termination.ChildProcessId); Equal(true, failure.Termination.ChildHasExited);
        await observedChild.WaitForExitAsync().WaitAsync(Deadline); Equal(true, observedChild.HasExited);
        Equal(1, failure.Termination.Failures.Length); Equal("ShutdownTimeout", failure.Termination.Failures[0]);
    }
    private static Task Generation(Context context) => RunWithFixture(context, async fixture =>
    {
        var old = await fixture.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = old.RegisterCallback("authored-extension", 1, async (_, token) =>
        { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, token); return WorkerValue.Absent; } finally { exited.TrySetResult(); } });
        var value = Json(JsonSerializer.Serialize(new { handle = new { ownerId = handle.OwnerId, ownerGeneration = handle.OwnerGeneration,
            registrationId = handle.RegistrationId, callbackId = handle.CallbackId } }));
        var pending = old.StartRequest("probe.nested", value); await entered.Task.WaitAsync(Deadline);
        fixture.Expect(old, "Generation", "Protocol", "ProtocolShutdown");
        var ended = await FailsOwner(() => old.InvalidateSessionAsync(8).AsTask()); await exited.Task.WaitAsync(Deadline); VerifyJoined(ended.Termination);
        Equal(NodeWorkerStopReason.Generation, ended.Termination.Reason); Equal(0, ended.Termination.Protocol.ActiveCallbacks);
        var uncertain = await FailsProtocol(() => pending.Result); Equal(WorkerOutcome.Unknown, uncertain.Outcome);
        var current = await fixture.Start(worker: 4, session: 8);
        var stale = await FailsProtocol(() => current.RequestAsync("probe.nested", value), WorkerProtocolFailure.RemoteError);
        Equal("CallbackFailed", stale.RemoteCode); Equal(0, current.Snapshot.ActiveCallbacks);
        _ = await current.RequestAsync("probe.ping", WorkerValue.Absent);
        var one = current.DisposeAsync().AsTask(); var two = current.DisposeAsync().AsTask(); Check(ReferenceEquals(one, two), "Fresh epoch did not share cleanup.");
        await one.WaitAsync(Deadline); VerifyJoined(await current.Termination);
        VerifyNaturalShutdown(await current.Termination);
        await VerifyCleanupEvidence(fixture.RootOf(current), current.ProcessId);
    });

    private sealed record Context(string Dotnet, string Node, string Repo);
    private static async Task RunWithFixture(Context context, Func<Fixture, Task> body)
    {
        var fixture = new Fixture(context); Exception? primary = null;
        try { await body(fixture); }
        catch (Exception error)
        {
            primary = error;
            try { fixture.RecordBodyFailure(error); }
            catch (Exception diagnostic) { primary = new AggregateException("Primary test and diagnostic persistence failed.", error, diagnostic); }
        }
        try { await fixture.DisposeAsync(); }
        catch (Exception cleanup)
        {
            if (primary is not null && !ReferenceEquals(primary, cleanup))
                throw new AggregateException("Primary test and awaited fixture cleanup both failed.", primary, cleanup);
            throw;
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Context _context; private readonly Dictionary<NodeWorkerSupervisor, (string Root, string[]? Expected)> _owners = [];
        private int _next;
        public string Parent { get; }
        public Fixture(Context context)
        {
            _context = context;
            foreach (var path in new[] { context.Dotnet, context.Node, context.Repo }) Check(Path.IsPathFullyQualified(path), "Test gate must pass absolute paths.");
            Check(File.Exists(context.Dotnet), "The explicit native test host is missing.");
            var temp = Environment.GetEnvironmentVariable("TMPDIR") ?? Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
            Check(Path.IsPathFullyQualified(temp) && Directory.Exists(temp), "Set an existing caller-owned TMPDIR/TEMP before this gate.");
            Parent = Path.Combine(temp, "pisharp-node-worker-process-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Parent);
            File.WriteAllText(Path.Combine(Parent, "test-owner.json"), JsonSerializer.Serialize(new { dotnetHost = context.Dotnet, nodePath = context.Node,
                repositoryRoot = context.Repo, sourceCapture = false, testAssembly = typeof(NodeWorkerSupervisorTests).Assembly.Location }) + "\n");
        }
        public string FreshRoot() => Path.Combine(Parent, "worker-" + (++_next));
        public NodeWorkerLaunch Launch(NodeWorkerProbeMode mode = NodeWorkerProbeMode.Normal, NodeWorkerSupervisionOptions? options = null,
            WorkerProtocolOptions? protocol = null, long worker = 3, long session = 7) =>
            NodeWorkerLaunch.ForProbe(_context.Node, _context.Repo, FreshRoot(), worker, session, mode, options, protocol);
        public async Task<NodeWorkerSupervisor> Start(NodeWorkerProbeMode mode = NodeWorkerProbeMode.Normal, NodeWorkerSupervisionOptions? options = null,
            WorkerProtocolOptions? protocol = null, long worker = 3, long session = 7)
        {
            var launch = Launch(mode, options, protocol, worker, session); var owner = await NodeWorkerSupervisor.StartProbeAsync(launch).WaitAsync(Deadline);
            _owners.Add(owner, (launch.RunRoot, null)); return owner;
        }
        public string RootOf(NodeWorkerSupervisor owner) => _owners[owner].Root;
        public void Expect(NodeWorkerSupervisor owner, params string[] codes) => _owners[owner] = (_owners[owner].Root, codes);
        public void RecordBodyFailure(Exception error)
        {
            var text = error.ToString(); const int cap = 65_536;
            File.WriteAllText(Path.Combine(Parent, "primary-test-failure.json"), JsonSerializer.Serialize(new
            { stage = "body-failure-before-fixture-dispose", errorCharacters = text.Length, errorTruncated = text.Length > cap,
              error = text[..Math.Min(text.Length, cap)], owners = _owners.Select(item => new
              { item.Value.Root, item.Key.ProcessId, protocol = item.Key.Snapshot, completion = item.Key.Completion.IsCompleted,
                termination = item.Key.Termination.IsCompleted }).ToArray() }) + "\n");
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var item in _owners)
            {
                if (item.Value.Expected is { } expected)
                {
                    var failure = await FailsOwner(() => item.Key.DisposeAsync().AsTask()); VerifyJoined(failure.Termination);
                    Check(failure.Termination.Failures.Length != 0 && failure.Termination.Failures.All(code => expected.Contains(code)), "Unexpected cleanup fault was not admitted by this negative test.");
                }
                else { await item.Key.DisposeAsync().AsTask().WaitAsync(Deadline); VerifyJoined(await item.Key.Termination); }
            }
            // All authored receipts remain for independent inspection; no recursive cleanup or uncertain root adoption.
        }
    }
    private static void VerifyJoined(NodeWorkerTermination end)
    {
        Check(end.HasExited && end.ChildHasExited && end.PinsRechecked, "Actual owned exits/pins were not joined.");
        Equal(3, end.ExplicitStreamCloses); Equal(0, end.Protocol.PendingCalls); Equal(0, end.Protocol.PendingWrites);
        Equal(0, end.Protocol.ActiveCallbacks); Equal(0L, end.Protocol.BufferedBytes); Equal(0, end.Protocol.RegisteredHandles);
    }
    private static void VerifyNaturalShutdown(NodeWorkerTermination end)
    {
        VerifyJoined(end); Equal(NodeWorkerStopReason.Shutdown, end.Reason); Equal(0, end.ExitCode);
        Equal(false, end.KillAttempted); Equal(0, end.Failures.Length);
        Equal(false, end.StdoutAfterProtocolOverflow); Equal(true, end.StdoutAfterProtocolEof);
        Equal((long)end.StdoutAfterProtocol.Length, end.ObservedStdoutAfterProtocolBytes);
        Equal(Convert.ToHexStringLower(SHA256.HashData(end.StdoutAfterProtocol.AsSpan())), end.StdoutAfterProtocolSha256);
        var stderr = Encoding.UTF8.GetString(end.Stderr.AsSpan());
        Check(stderr.StartsWith("probe-start\n", StringComparison.Ordinal) && stderr.EndsWith("probe-stop\n", StringComparison.Ordinal),
            "Complete peer startup/shutdown diagnostics were not joined.");
        Equal(end.Stderr.Length, checked((int)end.ObservedStderrBytes));
    }
    private static async Task VerifyCleanupEvidence(string root, int pid)
    {
        using var launch = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "launch.receipt.json")));
        using var peer = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "peer-cleanup.receipt.json")));
        var steps = peer.RootElement.GetProperty("observations").EnumerateArray().ToArray();
        Check(steps.Select(step => step.GetProperty("stage").GetString()).SequenceEqual([
            "handlers-and-writes-settled", "ack-and-stderr-settled", "stdin-destroy-dispatched", "stdout-end-settled", "stdio-unref-dispatched"]),
            "Peer's actual cleanup pipeline did not complete.");
        foreach (var step in steps)
        {
            Equal(pid, step.GetProperty("pid").GetInt32());
            Equal(launch.RootElement.GetProperty("workerGeneration").GetInt64(), step.GetProperty("worker").GetInt64());
            Equal(launch.RootElement.GetProperty("sessionGeneration").GetInt64(), step.GetProperty("session").GetInt64());
            foreach (var name in new[] { "activeHandlers", "activeTasks", "pendingCallbacks", "writeTasks", "writes", "writeBytes", "activeBytes" })
                Equal(0L, step.GetProperty(name).GetInt64());
            var count = step.GetProperty("activeResourceCount").GetInt32();
            Equal(Math.Min(count, 64), step.GetProperty("activeResources").GetArrayLength());
            Equal(count > 64, step.GetProperty("resourcesTruncated").GetBoolean());
        }
        // Pinned process.stdout's end callback is observed separately from ordinary Writable flags:
        // the actual successful runtime receipt retains false for both, even after its callback.
        var last = steps[^1]; Equal(false, last.GetProperty("stdout").GetProperty("writableFinished").GetBoolean());
        Equal(false, last.GetProperty("stdout").GetProperty("writableEnded").GetBoolean());
        Check(!last.GetProperty("activeResources").EnumerateArray().Any(value => value.GetString() == "Timeout"), "Peer retained its keep-alive timer.");
        using var native = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "termination.json")));
        var samples = native.RootElement.GetProperty("cleanupObservations").EnumerateArray().ToArray();
        Check(samples.Length is >= 5 and <= 6, "Native cleanup sample bounds differ.");
        var barrier = samples.Single(step => step.GetProperty("Stage").GetString() == "input-write-barrier");
        Equal(true, barrier.GetProperty("Protocol").GetProperty("Stopped").GetBoolean());
        Equal(0, barrier.GetProperty("Protocol").GetProperty("PendingWrites").GetInt32());
        Equal(true, samples.Single(step => step.GetProperty("Stage").GetString() == "input-closed").GetProperty("InputClosed").GetBoolean());
        var joined = samples.Single(step => step.GetProperty("Stage").GetString() == "all-io-joined");
        foreach (var name in new[] { "InputClosed", "InputCloseCompleted", "ProtocolCompleted", "ExitCompleted", "StderrCompleted", "StdoutReaderJoined", "StdoutAfterProtocolEof" })
            Equal(true, joined.GetProperty(name).GetBoolean());
    }
    private static void VerifyFixtureJunction(string parent, string link, string target)
    {
        Check(StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(Path.GetFullPath(link)), Path.GetFullPath(parent)),
            "Junction is outside its owned fixture parent.");
        Check((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Fixture is not an actual reparse point.");
        var resolved = new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: true);
        Check(resolved is not null && StringComparer.OrdinalIgnoreCase.Equals(Path.TrimEndingDirectorySeparator(resolved.FullName),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(target))), "Fixture junction target differs.");
    }
    private static void CreateFixtureJunction(string parent, string link, string target)
    {
        Check(OperatingSystem.IsWindows() && Path.IsPathFullyQualified(target) && Directory.Exists(target), "Junction target/profile is invalid.");
        Check(StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(Path.GetFullPath(link)), Path.GetFullPath(parent)) &&
            !Path.Exists(link) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0, "Junction fixture parent/ownership is invalid.");
        if (!CreateDirectoryW(link, IntPtr.Zero)) throw new IOException("Fresh fixture junction directory could not be created.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var dataLength = checked(8 + substitute.Length + 2 + print.Length + 2);
        Check(dataLength + 8 <= 16_384, "Fixture junction target exceeds the bounded reparse buffer.");
        var buffer = new byte[dataLength + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 16 + substitute.Length + 2);
        using var handle = CreateFileW(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Fixture junction handle could not be opened.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if (!DeviceIoControl(handle, 0x000900A4, buffer, checked((uint)buffer.Length), IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Fixture mount-point reparse creation failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr templateFile);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    private static async Task Until(Func<bool> condition)
    { using var stop = new CancellationTokenSource(Deadline); while (!condition()) await Task.Delay(5, stop.Token); }
    private static async Task<NodeWorkerSupervisorException> FailsOwner(Func<Task> action)
    { try { await action().WaitAsync(Deadline); throw new Exception("Expected joined supervisor failure."); } catch (NodeWorkerSupervisorException failure) { return failure; } }
    private static async Task<WorkerProtocolException> FailsProtocol(Func<Task> action, WorkerProtocolFailure? expected = null)
    {
        try { await action().WaitAsync(Deadline); throw new Exception("Expected protocol failure."); }
        catch (WorkerProtocolException failure) { if (expected is { } code) Equal(code, failure.Failure); return failure; }
    }
    private static void ThrowsProtocol(Action action, WorkerProtocolFailure expected)
    { try { action(); throw new Exception("Expected synchronous protocol failure."); } catch (WorkerProtocolException failure) { Equal(expected, failure.Failure); } }
    private static void ThrowsIo(Action action)
    { try { action(); throw new Exception("Expected read-only launch rejection."); } catch (IOException) { } }
    private static async Task FailsIo(Func<Task> action)
    { try { await action(); throw new Exception("Expected launch rejection."); } catch (IOException) { } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static void EqualJson(JsonElement expected, JsonElement actual, string pointer)
    {
        Equal(expected.ValueKind, actual.ValueKind);
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var fields = expected.EnumerateObject().ToArray();
            Check(fields.Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(actual.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)), "Complete fieldset differs at " + pointer);
            foreach (var field in fields) EqualJson(field.Value, actual.GetProperty(field.Name), pointer + "/" + field.Name);
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (var i = 0; i < expected.GetArrayLength(); i++) EqualJson(expected[i], actual[i], pointer + "/" + i); }
        else if (expected.ValueKind == JsonValueKind.String) Equal(expected.GetString(), actual.GetString());
        else Equal(expected.GetRawText(), actual.GetRawText());
    }
}
