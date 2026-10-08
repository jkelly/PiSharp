using System.Collections.Immutable;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

// Unchanged pinned commands.ts -> genuine original loader/Runner -> shipped JS callers ->
// actual Node registry callback -> native dialog participant -> typed host UI. The host is a
// held UI probe; this does not qualify a terminal renderer or pretend to load an unadmitted plugin.
internal static class NativeOriginalCommandsDialogPipelineTests
{
    internal sealed record CapturedOriginal(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly List<CapturedOriginal> retained = [];
    internal static CapturedOriginal[] RawCapturedOriginals { get { lock (retained) return retained.ToArray(); } }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new IOException("Actual original registered dialog pipeline failed."); }
    private sealed class Record(Task original, string phase)
    {
        internal readonly Task Original = original; internal readonly List<string> Phases = [phase];
        internal AggregateException? Aggregate; internal Exception? Direct; internal bool Collected, AggregateCaptured;
    }
    private sealed class Originals
    {
        private readonly Dictionary<Task, Record> records = new(ReferenceEqualityComparer.Instance);
        internal void Add(string phase, Task original)
        {
            lock (records)
                if (records.TryGetValue(original, out var record)) record.Phases.Add(phase);
                else records.Add(original, new(original, phase));
        }
        internal async Task JoinAll(List<Exception> errors)
        {
            while (true)
            {
                Record[] pending; lock (records) pending = records.Values.Where(record => !record.Collected).ToArray();
                if (pending.Length == 0) break;
                foreach (var record in pending)
                {
                    lock (records) if (record.Collected) continue;
                    try { await record.Original; }
                    catch (Exception error)
                    {
                        record.Direct = error;
                        try { SeedBrokerEvidence(error); } catch (Exception metadata) { errors.Add(metadata); }
                        if (record.Original.IsFaulted && !record.AggregateCaptured)
                        { record.Aggregate = record.Original.Exception; record.AggregateCaptured = true; }
                    }
                    finally { lock (records) record.Collected = true; }
                }
            }
            lock (records)
                foreach (var record in records.Values)
                {
                    lock (retained) retained.Add(new(string.Join("|", record.Phases), record.Original, record.Aggregate, record.Direct));
                    if (record.Direct is not null) errors.Add(record.Aggregate ?? record.Direct);
                }
        }
        private void SeedBrokerEvidence(Exception root)
        {
            var pending = new Stack<Exception>(); pending.Push(root);
            var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var edges = 0;
            while (pending.TryPop(out var error))
            {
                if (!seen.Add(error)) continue;
                if (seen.Count > 1024) throw new IOException("Original dialog evidence graph node limit.");
                if (error is OriginalUiDialogOriginalException { Original: { } original, Evidence: AggregateException aggregate })
                    lock (records)
                        if (records.TryGetValue(original, out var record) && !record.AggregateCaptured)
                        { record.Aggregate = aggregate; record.AggregateCaptured = true; }
                IEnumerable<Exception> children = error is AggregateException grouped ? grouped.InnerExceptions :
                    error.InnerException is { } inner ? new[] { inner } : Array.Empty<Exception>();
                foreach (var child in children)
                { if (++edges > 4096) throw new IOException("Original dialog evidence graph edge limit."); pending.Push(child); }
            }
        }
    }
    internal static async Task RunAsync(NodeCommandInputWorkerLaunch admittedLaunch, string workspace)
    {
        ArgumentNullException.ThrowIfNull(admittedLaunch);
        var originals = new Originals(); var errors = new List<Exception>();
        var probe = new HostUi(originals); var registry = new ExtensionRegistry(null, probe);
        var node = new NodeCommandInputExtension(admittedLaunch, workspace, sourcePaths: [NodeTierAAdmission.CommandsSource]);
        RegistrationScope? scope = null; Task? invocation = null;
        try
        {
            var activation = registry.ActivateAsync("actual-original-dialogs", node); originals.Add("registry-activation", activation); scope = await activation;
            var actual = registry.CaptureSnapshot().Commands.Single();
            Check(actual.Name == "commands" && actual.SourcePath is not null &&
                actual.SourcePath.Replace('\\', '/').EndsWith("/" + NodeTierAAdmission.CommandsSource, StringComparison.Ordinal));
            probe.ExpectedSourcePath = actual.SourcePath;
            invocation = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "commands", JsonData.Parse("\"\"")).AsTask();
            originals.Add("actual-original-registered-command", invocation);
            await Stage(probe.SelectEntered.Task, invocation);
            Check(!invocation.IsCompleted && probe.SelectedTitle == "Available Commands" && probe.SelectTimeout is null &&
                probe.Choices.Contains("--- Extensions ---") && probe.Choices.Any(item => item.StartsWith("/commands", StringComparison.Ordinal)));
            probe.SelectRelease.TrySetResult();
            await Stage(probe.ConfirmEntered.Task, invocation);
            Check(!invocation.IsCompleted && probe.ConfirmedTitle == "commands" &&
                probe.ConfirmedMessage == "View source path?\n" + actual.SourcePath && probe.ConfirmTimeout is null);
            probe.ConfirmRelease.TrySetResult(); await invocation;
            Check(probe.Notices.SequenceEqual(new[] { actual.SourcePath }) && probe.SelectCalls == 1 && probe.ConfirmCalls == 1 &&
                node.ActiveContexts == 0 && node.WorkerSnapshot is { PendingCalls: 0 } &&
                node.SourceOperations.Last().Value.GetProperty("settled").GetBoolean() &&
                node.SourceOperations.Last().Value.GetProperty("kind").GetString() == "command" &&
                node.SourceOperations.Last().Value.GetProperty("status").GetString() == "fulfilled" &&
                node.SourceOperations.Last().Value.GetProperty("observation").GetProperty("status").GetString() == "fulfilled" &&
                node.SourceOperations.Last().Value.GetProperty("observation").GetProperty("publicationJoined").GetBoolean());
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            probe.SelectRelease.TrySetResult(); probe.ConfirmRelease.TrySetResult();
            // Join the real callback before owner retirement, including originals dynamically
            // acquired while it resumes. The caller provides the launch; no process is invented.
            if (invocation is not null) try { await invocation; } catch { /* retained by its ledger below */ }
            try { if (scope is not null) { var original = scope.DisposeAsync().AsTask(); originals.Add("scope-retirement", original); await original; } }
            catch (Exception error) { errors.Add(error); }
            try { var original = node.DisposeAsync().AsTask(); originals.Add("node-retirement", original); await original; }
            catch (Exception error) { errors.Add(error); }
            try { var original = registry.DisposeAsync().AsTask(); originals.Add("registry-retirement", original); await original; }
            catch (Exception error) { errors.Add(error); }
            await originals.JoinAll(errors);
        }
        if (errors.Count != 0) throw new AggregateException("Original command criteria and full acquired original inventory.", errors);
    }
    private static async Task Stage(Task entered, Task original)
    { var winner = await Task.WhenAny(entered, original).WaitAsync(TimeSpan.FromSeconds(10)); Check(ReferenceEquals(winner, entered)); await entered; }
    private sealed class HostUi(Originals originals) : IExtensionUiProvider
    {
        internal readonly TaskCompletionSource SelectEntered = Gate(), SelectRelease = Gate(), ConfirmEntered = Gate(), ConfirmRelease = Gate();
        internal readonly List<string> Notices = [];
        internal string? ExpectedSourcePath, SelectedTitle, ConfirmedTitle, ConfirmedMessage;
        internal ImmutableArray<string> Choices; internal double? SelectTimeout, ConfirmTimeout;
        internal int SelectCalls, ConfirmCalls, sequence;
        public IExtensionUiScope OpenScope(IExtensionContext context)
        { Check(context.OwnerId == "actual-original-dialogs" && context.OwnerGeneration > 0); return new Scope(this, originals, ++sequence); }
        private sealed class Scope(HostUi owner, Originals originals, int scopeNumber) : IExtensionUiScope
        {
            public ExtensionUiCapabilities Capabilities => new(ExtensionUiMode.Rpc, 1, 41, [ExtensionUiFeature.Select, ExtensionUiFeature.Confirm, ExtensionUiFeature.Notify]);
            public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default)
            { var original = Select(title, choices, options, cancellationToken); originals.Add("actual-native-select-scope-" + scopeNumber, original); return new(original); }
            private async Task<ExtensionUiOutcome<string>> Select(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options, CancellationToken token)
            { owner.SelectCalls++; owner.SelectedTitle = title; owner.Choices = choices; owner.SelectTimeout = options?.TimeoutMilliseconds;
                owner.SelectEntered.TrySetResult(); await owner.SelectRelease.Task; token.ThrowIfCancellationRequested();
                return ExtensionUiOutcome<string>.FromValue(choices.Single(item => item.StartsWith("/commands", StringComparison.Ordinal))); }
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default)
            { var original = Confirm(title, message, options, cancellationToken); originals.Add("actual-native-confirm-scope-" + scopeNumber, original); return new(original); }
            private async Task<ExtensionUiOutcome<bool>> Confirm(string title, string message, ExtensionUiDialogOptions? options, CancellationToken token)
            { owner.ConfirmCalls++; owner.ConfirmedTitle = title; owner.ConfirmedMessage = message; owner.ConfirmTimeout = options?.TimeoutMilliseconds;
                owner.ConfirmEntered.TrySetResult(); await owner.ConfirmRelease.Task; token.ThrowIfCancellationRequested(); return ExtensionUiOutcome<bool>.FromValue(true); }
            public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); var notify = notification as ExtensionUiNotify ?? throw new IOException("Unexpected original notification.");
                Check(notify.Kind == ExtensionUiNotifyKind.Info && notify.Message == owner.ExpectedSourcePath); owner.Notices.Add(notify.Message);
                var original = Task.FromResult(ExtensionUiOutcome<ExtensionUiPublication>.FromValue(ExtensionUiPublication.Published)); originals.Add("actual-native-notify-scope-" + scopeNumber, original); return new(original); }
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask DisposeAsync() { var original = Task.CompletedTask; originals.Add("actual-native-ui-scope-disposal-" + scopeNumber, original); return new(original); }
        }
    }
}
