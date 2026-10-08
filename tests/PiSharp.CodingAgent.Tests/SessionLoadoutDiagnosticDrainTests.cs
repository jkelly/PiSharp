using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionLoadoutDiagnosticDrainTests
{
    internal const string Prefix = "session loadout drain ";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("loadout-drain", "openai-responses", "fixture");
    private static readonly JsonData A = Declaration("a"), B = Declaration("b");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "initial create bootstrap and rejected replay join before writer runtime release", Admission),
        (Prefix + "SetActiveTools waits report before durable loadout publication", Configure),
        (Prefix + "capture refusal plus reporter failure retain original causes without checkpoint", FailureIdentity),
        (Prefix + "synchronous selection drains before prompt provider and preserves presentation fallback", Prompt),
        (Prefix + "replacement owner bind drains before retirement and rechecks late cancellation", Replacement),
        (Prefix + "tree replay drains before selected branch publication without append", Navigation),
        (Prefix + "original public drain close join and reporter exception precede runtime disposal", Close),
        (Prefix + "serialized subsequent batch delivery and reporter self wait refusal", Serial)
    ];
    private static JsonData Declaration(string name) => JsonData.Parse(JsonSerializer.Serialize(new { name, description = name + " original", parameters = new { type = "object" } }));
    private static SessionEntry Record(string id, string? parent, object message) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
    { type = "message", id, parentId = parent, timestamp = "2026-10-01T00:00:00.000Z", message }));
    private static TranscriptEntry Input() => new("user", JsonData.Parse("""{"role":"user","content":"hello","timestamp":123}"""));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected original operation failure."); }
    private static async Task<Exception> Failure(Func<Task> original)
    { try { await original(); } catch (Exception error) { return error; } throw new InvalidOperationException("Expected original operation failure."); }
    private static IEnumerable<Exception> Leaves(Exception error) => error is AggregateException aggregate ? aggregate.InnerExceptions.SelectMany(Leaves) : [error];
    // Only the documented phase-two receipt is transparent. Unexpected wrappers remain unexpected causes.
    private static IEnumerable<Exception> CleanupLeaves(Exception error) => error switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(CleanupLeaves),
        PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed, InnerException: AggregateException aggregate }
            => CleanupLeaves(aggregate),
        _ => [error]
    };
    private static void Exact(Exception error, params Exception[] expected) => Check(Leaves(error).Count() == expected.Length &&
        expected.All(cause => Leaves(error).Any(value => ReferenceEquals(value, cause))), "Original exception identities were replaced or causes lost.");
    private static async Task Admission()
    {
        await using (var f = await Fixture.Prepare())
        {
            f.Probe.Capture("bootstrap", f.Probe.AFailure); var gate = f.Probe.Arm(); var path = Path.Combine(f.Root, "new.jsonl");
            var original = PersistentAgentSession.CreateAsync(path, f.Header("new"), f.Registry, Model, () => 123, f.NextId);
            PersistentAgentSession? created = null;
            try { await gate.Entered.Task.WaitAsync(Bound); Check(!original.IsCompleted && !File.Exists(path), "Initial drain followed session file creation/publication."); }
            finally { gate.Release.TrySetResult(); created = await original; }
            try { Check(f.Probe.Reported.Single().Error == f.Probe.AFailure && created!.Snapshot.Log.Entries.Length == 2 && f.Transport.Calls == 0, "Initial bootstrap was dropped or inferred."); }
            finally { await created!.DisposeAsync(); }
        }
        await using (var f = await Fixture.Prepare())
        {
            var before = await Bytes(f.Source); var gate = f.Probe.Arm(); f.Probe.ReporterFailure = new InvalidOperationException("original replay reporter");
            var original = f.Open(f.Source);
            try { await gate.Entered.Task.WaitAsync(Bound); Check(!original.IsCompleted && !f.Resources.Single().Disposed, "Replay released runtime while its reporter remained owned."); }
            finally { gate.Release.TrySetResult(); await Failure(original); }
            Exact(await Failure(original), f.Probe.AFailure, f.Probe.ReporterFailure!);
            Check(f.Resources.Single().Disposed && f.Probe.Active == 0 && f.Transport.Calls == 0, "Rejected replay did not join reporter then runtime release.");
            var after = await Bytes(f.Source); Check(before.SequenceEqual(after), "Rejected replay rewrote source.");
            await using var reopened = await SessionLogStore.OpenAsync(f.Source);
        }
    }
    private static async Task Configure()
    {
        await using var f = await Fixture.Create(); var before = await Bytes(f.Source); var snapshot = f.Session.Snapshot; var reports = f.Probe.Reported.Count;
        var gate = f.Probe.Arm(); var original = f.Session.SetActiveToolsAsync(["b"]);
        try { await gate.Entered.Task.WaitAsync(Bound); Check(!original.IsCompleted && f.Session.Snapshot.IsConfiguring && f.Session.Snapshot.Log.Sequence == snapshot.Log.Sequence &&
            f.Session.Snapshot.Agent.Tools.Single().Name == "a" && f.Transport.Calls == 0, "SetActiveTools acknowledged or changed scheduler before report join."); }
        finally { gate.Release.TrySetResult(); await original; }
        var after = await Bytes(f.Source); Check(after.Length > before.Length && after.AsSpan(0, before.Length).SequenceEqual(before) &&
            f.Session.GetActiveTools().SequenceEqual(new[] { "b" }) && f.Probe.Reported.Count == reports + 1 && f.Probe.Reported[^1].Error == f.Probe.BFailure && f.Session.Snapshot.Fault is null,
            "Configured loadout did not preserve fallback/actual checkpoint or report original failure once.");
    }
    private static async Task FailureIdentity()
    {
        await using var f = await Fixture.Create(); var before = await Bytes(f.Source); var log = f.Session.Snapshot.Log;
        f.Probe.CaptureRefusal = new InvalidOperationException("original capture refusal", f.Probe.BFailure);
        f.Probe.ReporterFailure = new IOException("original reporter failure");
        var original = f.Session.SetActiveToolsAsync(["b"]); var error = await Failure(original);
        Exact(error, f.Probe.CaptureRefusal!, f.Probe.BFailure, f.Probe.ReporterFailure!);
        Check(ReferenceEquals(f.Probe.CaptureRefusal!.InnerException, f.Probe.BFailure) && f.Session.Snapshot.Log.Sequence == log.Sequence &&
            f.Session.GetActiveTools().SequenceEqual(new[] { "a" }) && f.Session.Snapshot.Fault is null, "Failed diagnostic boundary published a loadout or replaced preparation cause.");
        var after = await Bytes(f.Source); Check(before.SequenceEqual(after), "Reporter refusal checkpointed prospective state.");
        f.Probe.CaptureRefusal = null; f.Probe.ReporterFailure = null; await f.Session.SetActiveToolsAsync(["b"]);
    }
    private static async Task Prompt()
    {
        await using var f = await Fixture.Create(); var before = f.Session.Snapshot.Log; var reports = f.Probe.Reported.Count; var gate = f.Probe.Arm();
        var selection = f.Session.ScheduleToolActivation(["b"]);
        Check(f.Probe.Pending == 1 && f.Probe.Active == 0 && !gate.Entered.Task.IsCompleted && f.Session.Snapshot.Log.Sequence == before.Sequence,
            "Synchronous logical selection started detached reporting or durable publication.");
        var original = f.Session.PromptAsync(Input());
        try { await gate.Entered.Task.WaitAsync(Bound); Check(!original.IsCompleted && f.Transport.Calls == 0 && f.Session.Snapshot.Log.Sequence == before.Sequence,
            "Prompt reached provider or durable loadout before reporting pending selection."); }
        finally { gate.Release.TrySetResult(); await original; }
        Check(f.Transport.Calls == 1 && f.Transport.SawSettledDiagnostics && f.Probe.Reported.Count == reports + 1 && f.Probe.Reported[^1].Error == f.Probe.BFailure &&
            f.Session.GetActiveTools().SequenceEqual(selection.Names) && f.Session.Snapshot.Agent.Tools.Single().Name == "b", "Prompt lost report/fallback or pending activation publication.");
        await f.Session.ScheduleToolActivationAsync(["a"]); Check(f.Probe.Pending == 0 && f.Probe.Reported[^1].Error == f.Probe.AFailure, "Awaited selection left captured diagnostics undrained.");
    }
    private static async Task Replacement()
    {
        await using var f = await Fixture.Create(bindNested: true); var expected = f.Owner!.Current; var before = await Bytes(f.Source);
        var gate = f.Probe.Arm(f.Probe.Attempts + 2); using var cancellation = new CancellationTokenSource();
        var original = f.Owner.SwitchAsync(expected, new(f.Other), cancellationToken: cancellation.Token);
        try { await gate.Entered.Task.WaitAsync(Bound); Check(ReferenceEquals(f.Owner.Current, expected) && !expected.Session.Snapshot.IsRetired && !original.IsCompleted,
            "Replacement published or retired source before staged owner-bind reporter joined."); cancellation.Cancel(); await gate.Canceled.Task.WaitAsync(Bound);
            Check(!original.IsCompleted && f.Probe.Active == 1, "Replacement cancellation abandoned original staged reporter."); }
        finally { gate.Release.TrySetResult(); await Failure(original); }
        Check(await Failure(original) is OperationCanceledException && ReferenceEquals(expected, f.Owner.Current) && !expected.Session.Snapshot.IsRetired &&
            f.Resources[^1].Disposed && f.Probe.Active == 0, "Late staged drain cancellation retired source or leaked staged runtime.");
        var after = await Bytes(f.Source); Check(before.SequenceEqual(after), "Cancelled replacement changed source.");
        var replaced = await f.Owner.SwitchAsync(expected, new(f.Other)); Check(replaced is not null && f.Owner.Current.Generation == expected.Generation + 1 && f.Probe.Pending == 0,
            "Actual replacement failed to drain replay and owner-bind diagnostics before new generation publication.");
        var attempts = f.Probe.Attempts; Check(await Failure(() => expected.Session.DrainLoadoutDiagnosticsAsync()) is PersistentAgentSessionException && f.Probe.Attempts == attempts,
            "Retired session started a new diagnostic report.");
    }
    private static async Task Navigation()
    {
        await using var f = await Fixture.Create(); var expected = f.Owner!.Current; var view = f.Owner.CaptureTree(expected); var log = f.Session.Snapshot.Log; var before = await Bytes(f.Source);
        var gate = f.Probe.Arm(); var original = f.Owner.NavigateTreeAsync(expected, new("b-system", view.Revision));
        try { await gate.Entered.Task.WaitAsync(Bound); Check(!original.IsCompleted && f.Session.Snapshot.Context.LeafId == "left" && f.Session.Snapshot.IsCompacting && f.Transport.Calls == 0,
            "Replay navigation changed selection before original reporter join."); }
        finally { gate.Release.TrySetResult(); await original; }
        var after = await Bytes(f.Source); Check(before.SequenceEqual(after) && f.Session.Snapshot.Context.LeafId == "b-system" &&
            f.Session.GetActiveTools().SequenceEqual(new[] { "b" }) && ReferenceEquals(log, f.Session.Snapshot.Log) && ReferenceEquals(expected, f.Owner.Current), "Read-only tree replay checkpointed/replaced authority or lost loadout.");
    }
    private static async Task Close()
    {
        await using var f = await Fixture.Create(); f.Probe.Capture("a", f.Probe.AFailure); f.Probe.ReporterFailure = new IOException("original close reporter");
        f.ExpectedCloseCauses = [f.Probe.AFailure, f.Probe.ReporterFailure!]; var gate = f.Probe.Arm(); var original = f.Session.DrainLoadoutDiagnosticsAsync(); Task? close = null;
        try { await gate.Entered.Task.WaitAsync(Bound); close = f.Session.StopAdmissionAndJoinAsync(); await gate.Canceled.Task.WaitAsync(Bound);
            Check(!original.IsCompleted && !close.IsCompleted && !f.Resources.Single().Disposed && f.Probe.Active == 1, "Close released runtime or detached admitted reporter."); }
        finally { gate.Release.TrySetResult(); try { await Failure(original); } finally { if (close is not null) await Failure(close); } }
        Exact(await Failure(original), f.Probe.AFailure, f.Probe.ReporterFailure!); if (close is not null) Exact(await Failure(close), f.Probe.AFailure, f.Probe.ReporterFailure!);
        Check(gate.Joined.Task.IsCompletedSuccessfully && f.Probe.Active == 0 && !f.Resources.Single().Disposed, "StopAdmission disposed resources before explicit release or failed original join.");
        var release = f.Session.DisposeAsync().AsTask(); var receipt = await Failure(release);
        Check(receipt is PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed, InnerException: AggregateException },
            "Phase two did not return the established typed cleanup receipt.");
        Exact(receipt.InnerException!, f.Probe.AFailure, f.Probe.ReporterFailure!);
        Check(((AggregateException)receipt.InnerException!).InnerExceptions.SequenceEqual(f.ExpectedCloseCauses, ReferenceEqualityComparer.Instance),
            "Phase-two receipt reordered or duplicated the original preparation/reporter causes.");
        Check(ReferenceEquals(release, f.Session.DisposeAsync().AsTask()) && ReferenceEquals(receipt, await Failure(release)) && f.Resources.Single().Disposed,
            "Repeated disposal replaced its original settlement/receipt or resource release preceded reporter join.");
    }
    private static async Task Serial()
    {
        await using var f = await Fixture.Create(); var refusals = 0;
        f.Probe.OnReport = () =>
        {
            _ = f.Session.Snapshot;
            foreach (var self in new Action[] { () => { _ = f.Session.DrainLoadoutDiagnosticsAsync(); }, () => { _ = f.Session.StopAdmissionAndJoinAsync(); },
                () => { _ = f.Session.WaitForIdleAsync(); }, () => { _ = f.Session.DisposeAsync(); }, () => { _ = f.Owner!.DisposeAsync(); }, () => { _ = f.Session.ScheduleToolActivation(["b"]); } })
                try { self(); throw new Exception("Reporter self-wait was admitted."); } catch (InvalidOperationException) { refusals++; }
            Check(!f.Session.Snapshot.IsDisposed && f.Resources.All(value => !value.Disposed), "Reporter refusal installed disposal or released runtime resources.");
        };
        var reports = f.Probe.Reported.Count; f.Probe.Capture("a", f.Probe.AFailure); var gate = f.Probe.Arm(); var first = f.Session.DrainLoadoutDiagnosticsAsync(); Task? second = null;
        try { await gate.Entered.Task.WaitAsync(Bound); f.Probe.Capture("b", f.Probe.BFailure); second = f.Session.DrainLoadoutDiagnosticsAsync();
            Check(!first.IsCompleted && !second.IsCompleted && f.Probe.Active == 1 && f.Probe.Pending == 1, "Concurrent drain overlapped or consumed next batch before original settled."); }
        finally { gate.Release.TrySetResult(); try { await first; } finally { if (second is not null) await second; } }
        Check(f.Probe.Reported.Count == reports + 2 && f.Probe.Pending == 0 && f.Probe.MaximumActive == 1 && refusals == 12 && f.Probe.Active == 0,
            "Subsequent captures escaped the second serialized drain or reporter acquired self-wait authority.");
    }
    private static async Task<byte[]> Bytes(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Check(stream.Length is >= 0 and <= 1_048_576, "Unbounded fixture read."); var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes);
        Check(stream.Length == bytes.Length && stream.Position == bytes.Length, "Fixture read crossed mutation."); return bytes;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PiSharp-loadout-drain-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal readonly Probe Probe = new(); internal readonly List<Resources> Resources = []; internal readonly Transport Transport;
        internal SessionRuntimeRegistry Registry = null!; internal ReplaceableAgentSession? Owner; internal PersistentAgentSession Session => Owner!.Current.Session;
        internal Exception[] ExpectedCloseCauses = []; private int ids; private readonly List<PersistentAgentSession> sessions = [];
        private Fixture() { Transport = new(Probe); }
        internal string NextId() => "drain-" + Interlocked.Increment(ref ids);
        internal SessionEntry Header(string id) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id, timestamp = "2026-10-01T00:00:00.000Z", cwd = Root }));
        internal static async Task<Fixture> Prepare(bool bindNested = false)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            var tools = new[] { A, B }.Select(declaration => new SessionRegisteredTool(declaration, new Adapter(declaration.Value.GetProperty("name").GetString()!))
                { PrepareLoadout = _ => throw (declaration.Value.GetProperty("name").GetString() == "a" ? f.Probe.AFailure : f.Probe.BFailure) }).ToImmutableArray();
            f.Registry = new([new(Model, f.Transport)], tools, new DenyPolicy(), new() { ReportLoadoutDiagnostic = f.Probe.Capture,
                DrainLoadoutDiagnostics = f.Probe.Drain, BindNestedCallsToSessionOwner = bindNested });
            foreach (var path in new[] { f.Source, f.Other })
            {
                await using var store = await SessionLogStore.CreateNewAsync(path, f.Header(Path.GetFileNameWithoutExtension(path)));
                await store.AppendAsync([Record("a-system", null, new { role = "system", content = "original", timestamp = 123, toolsAdded = new[] { A.Value } }),
                    Record("left", "a-system", new { role = "user", content = "left", timestamp = 123 }),
                    Record("b-system", null, new { role = "system", content = "original", timestamp = 123, toolsAdded = new[] { B.Value } }),
                    Record("right", "b-system", new { role = "user", content = "right", timestamp = 123 })]);
            }
            return f;
        }
        internal async Task<PersistentAgentSession> Open(string path, CancellationToken token = default)
        {
            var session = await PersistentAgentSession.OpenWithRuntimeFactoryAsync(path, (_, _) => { var resources = new Resources(Probe); Resources.Add(resources);
                return ValueTask.FromResult(new SessionRuntimeLease(Registry, resources)); }, () => 123, NextId,
                new(UseLatestLeaf: false, SelectedLeafId: path == Source ? "left" : "right"), fallbackModel: Model, cancellationToken: token);
            sessions.Add(session); return session;
        }
        internal static async Task<Fixture> Create(bool bindNested = false)
        { var f = await Prepare(bindNested); var session = await f.Open(f.Source); f.Owner = new(session, (request, token) => f.Open(request.Path, token));
            await session.DrainLoadoutDiagnosticsAsync(); return f; }
        public async ValueTask DisposeAsync()
        {
            Probe.ReleaseAll();
            try
            {
                if (Owner is not null) await Owner.DisposeAsync(); else foreach (var session in sessions) await session.DisposeAsync();
            }
            catch (Exception error) { if (ExpectedCloseCauses.Length == 0) throw;
                var causes = CleanupLeaves(error).ToArray();
                if (!causes.All(cause => ExpectedCloseCauses.Any(expected => ReferenceEquals(cause, expected))) ||
                    !ExpectedCloseCauses.All(expected => causes.Any(cause => ReferenceEquals(cause, expected))))
                    throw new AggregateException("Owner cleanup replaced or introduced failure causes.", error,
                        new InvalidOperationException("Expected original preparation/reporter identities through typed cleanup receipts.")); }
            Check(Probe.Active == 0 && Resources.All(value => value.Disposed), "Original reporter/runtime lease cleanup did not settle.");
        }
    }
    private sealed class Hold
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Attempt;
    }
    private sealed class Probe
    {
        private readonly object gate = new(); private readonly Queue<(string Name, Exception Error)> pending = new(); private readonly List<Hold> holds = [];
        internal readonly Exception AFailure = new InvalidOperationException("original a preparation"), BFailure = new InvalidOperationException("original b preparation");
        internal readonly List<(string Name, Exception Error)> Reported = []; internal Exception? ReporterFailure, CaptureRefusal; internal Action? OnReport;
        internal int Attempts, Active, MaximumActive; internal int Pending { get { lock (gate) return pending.Count; } }
        internal void Capture(string name, Exception error) { lock (gate) pending.Enqueue((name, error)); if (CaptureRefusal is not null) throw CaptureRefusal; }
        internal Hold Arm(int? attempt = null) { var value = new Hold { Attempt = attempt ?? Attempts + 1 }; holds.Add(value); return value; }
        internal void ReleaseAll() { foreach (var hold in holds) hold.Release.TrySetResult(); }
        internal async ValueTask Drain(CancellationToken token)
        {
            (string Name, Exception Error)[] batch; lock (gate) { batch = pending.ToArray(); pending.Clear(); } if (batch.Length == 0) return;
            token.ThrowIfCancellationRequested(); Attempts++; Active++; MaximumActive = Math.Max(MaximumActive, Active); var hold = holds.SingleOrDefault(value => value.Attempt == Attempts);
            try
            {
                if (hold is not null) { using var canceled = token.UnsafeRegister(_ => hold.Canceled.TrySetResult(), null); hold.Entered.TrySetResult(); await hold.Release.Task; }
                OnReport?.Invoke(); if (ReporterFailure is not null) throw new AggregateException(batch.Select(value => value.Error).Append(ReporterFailure!));
                Reported.AddRange(batch);
            }
            finally { Active--; hold?.Joined.TrySetResult(); }
        }
    }
    private sealed class Resources(Probe probe) : IAsyncDisposable
    { internal bool Disposed; public ValueTask DisposeAsync() { Check(probe.Active == 0, "Runtime released before original reporter joined."); Disposed = true; return ValueTask.CompletedTask; } }
    private sealed class Adapter(string name) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => throw new InvalidOperationException("No tool effect expected.");
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("No tool effect expected.");
    }
    private sealed class DenyPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class Transport(Probe probe) : IChatTransport
    {
        internal int Calls; internal bool SawSettledDiagnostics;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; Calls++; SawSettledDiagnostics = probe.Active == 0 && probe.Pending == 0; Check(SawSettledDiagnostics, "Provider preceded diagnostic drain.");
            token.ThrowIfCancellationRequested(); var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(message with { StopReason = StopReason.Pending }); yield return new StreamDone(StopReason.Stop, message); }
    }
}
