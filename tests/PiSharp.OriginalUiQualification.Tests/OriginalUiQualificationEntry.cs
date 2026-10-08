using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.Qualification;

// Source-only entry. Root must separately admit the config, exact private source and matching
// reviewed JS/helper pins, then own a finite execution. There are four actual suite Tasks;
// eight private commands and two borrowed controls do not become invented per-case Tasks.
internal static class OriginalUiQualificationEntry
{
    private sealed record Configuration(string NodeExecutable, string RepositoryRoot, string OracleRoot, string JitiRoot,
        string ReferenceRoot, string CommandInputReference, string CommandsRunRoot, string PrivateRunRoot, string BorrowRunRoot,
        string Workspace, string SessionNamespace, string PrivateSourceRoot, string PrivateSourcePath, long WorkerGeneration, long SessionGeneration);
    private sealed class Original(Task task, string phase)
    {
        internal readonly Task Task = task; internal readonly List<string> Phases = [phase];
        internal AggregateException? Aggregate; internal Exception? Direct; internal bool Captured, Joined;
    }
    private static readonly List<Original> retained = [];
    private static readonly ModelDescriptor OfflineModel = new("original-ui-offline", "openai-responses", "offline");
    private static void Check(bool value) { if (!value) throw new IOException("Original UI owned setup criteria failed."); }
    private static Task<int> Main(string[] args)
    {
        var reportArgs = args.Length == 4 ? args[2..] : args;
        return QualificationEvidence.RunAsync(reportArgs, () =>
        {
            if (args.Length != 4 || args[0] != "--config" || !Path.IsPathFullyQualified(args[1])) throw new IOException("Explicit admitted --config path required.");
            var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(args[1])) ?? throw new IOException("Qualification config missing.");
            foreach (var path in new[] { config.CommandsRunRoot, config.PrivateRunRoot, config.BorrowRunRoot, config.Workspace,
                config.SessionNamespace, config.PrivateSourceRoot, config.PrivateSourcePath }) Check(Path.IsPathFullyQualified(path));
            var roots = new[] { config.CommandsRunRoot, config.PrivateRunRoot, config.BorrowRunRoot }.Select(Path.GetFullPath).ToArray();
            Check(roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3 && config.WorkerGeneration > 0 && config.SessionGeneration > 0);
            NodeCommandInputWorkerLaunch Launch(string runRoot, long offset) => NodeCommandInputWorkerLaunch.ForCommandsAndInput(
                config.NodeExecutable, config.RepositoryRoot, runRoot, config.OracleRoot, config.JitiRoot, config.ReferenceRoot,
                config.CommandInputReference, checked(config.WorkerGeneration + offset), config.SessionGeneration);
            return [
                ("original-ui.registered-original-commands-select-confirm-publication", () => NativeOriginalCommandsDialogPipelineTests.RunAsync(Launch(roots[0], 0), config.Workspace)),
                ("original-ui.registered-private-eight-dialogs-owned-session-and-exact-source-lease", () => OwnedPrivateAsync(Launch(roots[1], 1), config)),
                ("original-ui.actual-native-editor-result-utf16-code-units", OriginalUiDialogUtf16OutcomeTests.RunAsync),
                ("original-ui.actual-borrow-finalizers-two-controls-retain-sequential-raw-originals", () => BorrowedAsync(Launch(roots[2], 2), config.Workspace))
            ];
        }, 4, Capture);
    }
    private static CapturedOriginal[] Capture()
    {
        CapturedOriginal[] setup; lock (retained) setup = retained.Select(row => new CapturedOriginal(string.Join("|", row.Phases), row.Task, row.Aggregate, row.Direct)).ToArray();
        return setup.Concat(NativeOriginalCommandsDialogPipelineTests.RawCapturedOriginals.Select(row => new CapturedOriginal("commands:" + row.Phase, row.Original, row.Aggregate, row.Direct)))
            .Concat(NativeOriginalPrivateDialogPipelineTests.RawCapturedOriginals.Select(row => new CapturedOriginal("private:" + row.Phase, row.Original, row.Aggregate, row.Direct)))
            .Concat(OriginalUiDialogUtf16OutcomeTests.RawCapturedOriginals.Select(row => new CapturedOriginal("utf16:" + row.Phase, row.Original, row.Aggregate, row.Direct)))
            .Concat(NativeDialogBorrowFailureTests.RawCapturedOriginals.Select(row => new CapturedOriginal("borrowed:" + row.Phase, row.Original, row.Aggregate, row.Direct))).ToArray();
    }
    private sealed class Ledger
    {
        private readonly Dictionary<Task, Original> records = new(ReferenceEqualityComparer.Instance);
        internal readonly List<Exception> Errors = [];
        internal Original Track(Task task, string phase)
        {
            lock (records)
            {
            if (records.TryGetValue(task, out var row)) { row.Phases.Add(phase); return row; }
            var record = new Original(task, phase); records.Add(task, record); lock (retained) retained.Add(record); return record;
            }
        }
        private static void CaptureOnce(Original row, Exception? direct)
        { lock (row) { if (row.Captured) return; row.Captured = true; row.Direct = direct; if (row.Task.IsFaulted) row.Aggregate = row.Task.Exception; } }
        internal async Task Own(Task task, string phase)
        {
            var row = Track(task, phase);
            try { await task; } catch (Exception direct) { CaptureOnce(row, direct); throw; }
            finally { CaptureOnce(row, null); lock (row) row.Joined = true; }
        }
        internal void Acquire(Func<Task> acquire, string phase) { try { Track(acquire(), phase); } catch (Exception error) { Errors.Add(error); } }
        internal async Task JoinAll()
        {
            while (true)
            {
                Original? row; lock (records) row = records.Values.FirstOrDefault(row => !row.Joined);
                if (row is null) break;
                lock (row) if (row.Joined) continue;
                try { await row.Task; } catch (Exception direct) { CaptureOnce(row, direct); }
                finally { CaptureOnce(row, null); lock (row) row.Joined = true; }
            }
        }
        internal void ThrowIfFailed(string message)
        {
            lock (records)
            {
                if (Errors.Count == 0 && records.Values.All(row => row.Direct is null)) return;
                throw new QualificationFailure(message, records.Keys, Errors.Concat(records.Values.SelectMany(row => new Exception?[] { row.Aggregate, row.Direct }).OfType<Exception>()));
            }
        }
    }
    private static async Task OwnedPrivateAsync(NodeCommandInputWorkerLaunch launch, Configuration config)
    {
        var ledger = new Ledger(); var storage = new SessionStorageBackend(config.SessionNamespace, SessionStorageMode.InMemory);
        PersistentAgentSession? session = null; ReplaceableAgentSession? owner = null;
        try
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = "actual-original-ui", timestamp = "2026-01-01T00:00:00Z", cwd = config.Workspace }));
            var next = 0;
            var creation = PersistentAgentSession.CreateAsync(Path.Combine(config.SessionNamespace, "original-ui.jsonl"), header,
                new AgentConfiguration(OfflineModel, new DeniedTransport(), []), () => 123, () => "ui-entry-" + (++next),
                new() { SessionLogStoreOptions = new(StorageFactory: storage) });
            await ledger.Own(creation, "actual-session-creation"); session = creation.GetAwaiter().GetResult();
            owner = new ReplaceableAgentSession(session, (_, _) => throw new IOException("No session replacement authority in pure dialog qualification."));
            var actualOwner = owner; var attachment = actualOwner.Current; actualOwner.ValidateAttachment(attachment);
            Check(attachment.LifetimeToken.CanBeCanceled && !attachment.LifetimeToken.IsCancellationRequested && attachment.Generation > 0 && storage.ActiveWriterCount == 1);
            Task Retire()
            { var original = actualOwner.DisposeAsync().AsTask(); ledger.Track(original, "real-session-retirement-original"); return original; }
            var pipeline = NativeOriginalPrivateDialogPipelineTests.RunAsync(launch, config.Workspace, config.PrivateSourceRoot,
                config.PrivateSourcePath, attachment.LifetimeToken, () => attachment.Generation, Retire);
            await ledger.Own(pipeline, "actual-registered-private-dialog-suite");
            Check(attachment.LifetimeToken.IsCancellationRequested && session.Snapshot.IsDisposed && storage.ActiveWriterCount == 0);
        }
        catch (Exception error) { ledger.Errors.Add(error); }
        finally
        {
            if (owner is not null) ledger.Acquire(() => owner.DisposeAsync().AsTask(), "actual-session-owner-close");
            else if (session is not null) ledger.Acquire(() => session.DisposeAsync().AsTask(), "actual-session-close");
            await ledger.JoinAll(); if (storage.ActiveWriterCount != 0) ledger.Errors.Add(new IOException("Actual UI session writer still active."));
        }
        ledger.ThrowIfFailed("Actual session setup/private dialogs/cleanup originals failed.");
    }
    private static async Task BorrowedAsync(NodeCommandInputWorkerLaunch launch, string workspace)
    {
        var ledger = new Ledger(); var ui = new NativeDialogBorrowFailureTests.RetirementFaultUiProvider();
        var registry = new ExtensionRegistry(null, ui); var node = new NodeCommandInputExtension(launch, workspace, sourcePaths: [NodeTierAAdmission.CommandsSource]);
        RegistrationScope? scope = null;
        try
        {
            var activation = registry.ActivateAsync("actual-borrow-controls", new BorrowOwner(node, ui, ledger));
            await ledger.Own(activation, "borrow-owner-activation"); scope = activation.GetAwaiter().GetResult();
            Check(node.WorkerSnapshot is { Ready: true, Stopped: false, PendingCalls: 0 });
            var invocation = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "actual-borrow-controls", JsonData.Parse("\"\"")).AsTask();
            await ledger.Own(invocation, "same-owner-real-registry-command");
            Check(NativeDialogBorrowFailureTests.RawCapturedOriginals.Any(row => row.Phase == "actual-overbudget-invoke-before-request") &&
                NativeDialogBorrowFailureTests.RawCapturedOriginals.Any(row => row.Phase == "parent-canceled-source-invoke-with-retirement-fault"));
        }
        catch (Exception error) { ledger.Errors.Add(error); }
        finally
        {
            ui.Release.TrySetResult(); await ledger.JoinAll();
            if (scope is not null) ledger.Acquire(() => scope.DisposeAsync().AsTask(), "borrow-scope-close");
            await ledger.JoinAll(); ledger.Acquire(() => registry.DisposeAsync().AsTask(), "borrow-registry-close"); await ledger.JoinAll();
        }
        ledger.ThrowIfFailed("Borrowed finalization criteria and all owner originals failed.");
    }
    private sealed class BorrowOwner(NodeCommandInputExtension node, NativeDialogBorrowFailureTests.RetirementFaultUiProvider ui, Ledger ledger) : IPiSharpExtension
    {
        public async ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        {
            var original = node.InitializeAsync(registry, token).AsTask(); await ledger.Own(original, "actual-original-worker-initialize");
            registry.RegisterCommand(new("actual-borrow-controls", "actual-borrow-controls", "Owned native finalization qualification", async (_, context, _) =>
            {
                var beforeRequest = NativeDialogBorrowFailureTests.RunAsync(node, context); await ledger.Own(beforeRequest, "actual-pre-request-failure-control");
                var retirement = NativeDialogBorrowFailureTests.RunRetirementFaultAsync(node, context, ui); await ledger.Own(retirement, "actual-parent-cancel-retirement-fault-control");
            }));
        }
        public ValueTask DisposeAsync()
        { var original = node.DisposeAsync().AsTask(); ledger.Track(original, "actual-original-node-close"); return new(original); }
    }
    private sealed class DeniedTransport : IChatTransport
    { public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) => throw new IOException("Pure UI qualifier grants no model/provider call."); }
}
