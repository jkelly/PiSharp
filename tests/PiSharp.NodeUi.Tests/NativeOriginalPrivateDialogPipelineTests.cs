using System.Collections.Immutable;
using System.Text.Json;
using System.Reflection;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

// Requires the matching reviewed JS qualification load route and root-admitted readonly
// launch/source lease. Session token/generation/retirement belong to the actual caller's session.
// A real timer and typed native host are exercised; no physical terminal credit is claimed.
internal static class NativeOriginalPrivateDialogPipelineTests
{
    internal sealed record CapturedOriginal(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly List<CapturedOriginal> retained = [];
    internal static CapturedOriginal[] RawCapturedOriginals { get { lock (retained) return retained.ToArray(); } }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new IOException("Registered private original UI criteria failed."); }
    private sealed class Record(Task task, string role)
    {
        internal readonly Task Task = task; internal readonly List<string> Roles = [role];
        internal Exception? Direct; internal AggregateException? Aggregate; internal bool Captured, Joined, Published, FaultIncluded;
        internal CancellationToken? ExpectedCancellation;
    }
    private sealed class Ledger
    {
        private readonly Dictionary<Task, Record> records = new(ReferenceEqualityComparer.Instance);
        internal void Add(string role, Task task)
        { lock (records) { if (records.TryGetValue(task, out var row)) row.Roles.Add(role); else records.Add(task, new(task, role)); } }
        internal async Task Observe(Task task)
        {
            Record row; lock (records) { row = records[task]; if (row.Joined) return; }
            try { await task; }
            catch (Exception direct) { lock (records) { row.Direct ??= direct; if (!row.Captured) { row.Captured = true; if (task.IsFaulted) row.Aggregate = task.Exception; } } }
            finally { lock (records) row.Joined = true; }
        }
        internal void AcknowledgeCancelled(Task task, CancellationToken exactToken)
        {
            lock (records)
            {
                var row = records[task]; Check(row.Joined && task.IsCanceled && exactToken.IsCancellationRequested &&
                    row.Direct is OperationCanceledException error && error.CancellationToken == exactToken && row.Aggregate is null);
                row.ExpectedCancellation = exactToken;
            }
        }
        internal async Task Finish(List<Exception> errors)
        {
            while (true)
            {
                Task[] pending; lock (records) pending = records.Values.Where(row => !row.Joined).Select(row => row.Task).ToArray();
                if (pending.Length == 0) break;
                foreach (var task in pending) await Observe(task);
            }
            lock (records) foreach (var row in records.Values)
            {
                if (!row.Published) { lock (retained) retained.Add(new(string.Join("|", row.Roles), row.Task, row.Aggregate, row.Direct)); row.Published = true; }
                if (!row.FaultIncluded && row.Direct is not null && (errors.Count != 0 || row.ExpectedCancellation is null))
                { errors.Add(row.Aggregate ?? row.Direct); row.FaultIncluded = true; }
            }
        }
    }
    private sealed record SuppliedCallback(CancellationToken Token, IExtensionCommandContext Context, bool CancelledAtEntry);
    private sealed class CallbackCapture
    {
        private readonly Dictionary<string, SuppliedCallback> callbacks = new(StringComparer.Ordinal);
        internal void Enter(string name, IExtensionCommandContext context, CancellationToken token)
        { lock (callbacks) { Check(token.CanBeCanceled && context.OwnerId == "private-original-ui" && context.OwnerGeneration > 0); callbacks.Add(name, new(token, context, token.IsCancellationRequested)); } }
        internal SuppliedCallback Get(string name) { lock (callbacks) return callbacks[name]; }
    }
    // This native fixture wrapper forwards initialization and disposal originals unchanged.
    // Only the two descriptors needing token provenance are decorated: their actual Node
    // callback/context/arguments/ValueTask are forwarded directly, without an async adopter.
    private sealed class CapturingExtension(NodeCommandInputExtension original, CallbackCapture capture) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => original.InitializeAsync(new CapturingRegistry(registry, capture), token);
        public ValueTask DisposeAsync() => original.DisposeAsync();
    }
    private sealed class CapturingRegistry(IExtensionRegistry original, CallbackCapture capture) : IExtensionRegistry
    {
        public string OwnerId => original.OwnerId;
        public long OwnerGeneration => original.OwnerGeneration;
        public string ContractProfile => original.ContractProfile;
        public ImmutableArray<string> Features => original.Features;
        public CancellationToken ExtensionLifetimeCancellationToken => original.ExtensionLifetimeCancellationToken;
        public IExtensionRegistration RegisterCommand(ExtensionCommandDescriptor descriptor)
        {
            if (descriptor.Name is not ("qualification-parent" or "qualification-session")) return original.RegisterCommand(descriptor);
            var actualCallback = descriptor.ExecuteAsync;
            return original.RegisterCommand(descriptor with { ExecuteAsync = (arguments, context, token) =>
            { capture.Enter(descriptor.Name, context, token); return actualCallback(arguments, context, token); } });
        }
        public IExtensionRegistration RegisterTool(ExtensionToolDescriptor descriptor) => original.RegisterTool(descriptor);
        public IExtensionRegistration Observe(ExtensionObservationDescriptor descriptor) => original.Observe(descriptor);
        public IExtensionRegistration RegisterBeforeAgentStartHandler(ExtensionBeforeAgentStartHandlerDescriptor descriptor) => original.RegisterBeforeAgentStartHandler(descriptor);
        public IExtensionRegistration RegisterContextHandler(ExtensionContextHandlerDescriptor descriptor) => original.RegisterContextHandler(descriptor);
        public IExtensionRegistration RegisterContextWithSystemHandler(ExtensionContextWithSystemHandlerDescriptor descriptor) => original.RegisterContextWithSystemHandler(descriptor);
        public IExtensionRegistration RegisterInputHandler(ExtensionInputHandlerDescriptor descriptor) => original.RegisterInputHandler(descriptor);
        public IExtensionRegistration RegisterToolCallHandler(ExtensionToolCallHandlerDescriptor descriptor) => original.RegisterToolCallHandler(descriptor);
        public IExtensionRegistration RegisterToolResultHandler(ExtensionToolResultHandlerDescriptor descriptor) => original.RegisterToolResultHandler(descriptor);
    }
    internal static async Task RunAsync(NodeCommandInputWorkerLaunch admittedLaunch, string workspace,
        string heldSourceRoot, string privateSourcePath, CancellationToken actualSessionToken,
        Func<long> captureActualSessionGeneration, Func<Task> retireActualSession)
    {
        ArgumentNullException.ThrowIfNull(retireActualSession); ArgumentNullException.ThrowIfNull(captureActualSessionGeneration);
        Check(actualSessionToken.CanBeCanceled && !actualSessionToken.IsCancellationRequested && captureActualSessionGeneration() > 0);
        var ledger = new Ledger(); var errors = new List<Exception>();
        var source = OriginalUiQualificationSourceLease.AcquireHeld(heldSourceRoot, privateSourcePath);
        using var parent = new CancellationTokenSource();
        var ui = new Ui(ledger, captureActualSessionGeneration); var registry = new ExtensionRegistry(null, ui);
        var node = new NodeCommandInputExtension(admittedLaunch, workspace, qualificationSource: source);
        var supplied = new CallbackCapture();
        RegistrationScope? scope = null;
        try
        {
            var activation = registry.ActivateAsync("private-original-ui", new CapturingExtension(node, supplied)); ledger.Add("activation", activation); scope = await activation;
            Check(registry.CaptureSnapshot().Commands.Length == 8 && node.SourceLoadReport!.Value.GetProperty("admissionProfile").GetString() == "private-original-ui-qualification");
            // Probe the actual already-bound lease: a second owner cannot reuse its authority,
            // and a modified echo cannot become an accepted native registration receipt.
            try { _ = typeof(OriginalUiQualificationSourceLease).GetMethod("Bind", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, ["different-owner", scope.OwnerGeneration]); throw new IOException("Affine source reuse admitted."); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException refused && refused.Message == "Qualification source lease is affine.") { }
            var stale = System.Text.Json.Nodes.JsonNode.Parse(node.SourceLoadReport!.ToString())!;
            stale["qualificationLease"]!["ownerGeneration"] = scope.OwnerGeneration + 1;
            using (var changed = JsonDocument.Parse(stale.ToJsonString()))
                try { _ = typeof(OriginalUiQualificationSourceLease).GetMethod("ValidateReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, [changed.RootElement]); throw new IOException("Stale source receipt admitted."); }
                catch (TargetInvocationException error) when (error.InnerException is IOException refused && refused.Message == "Private qualification lease receipt differs.") { }
            Task Invoke(string command, string arguments = "", CancellationToken caller = default, CancellationToken session = default)
            { var task = registry.InvokeCommandAsync(registry.CaptureSnapshot(), command, JsonData.Parse(JsonSerializer.Serialize(arguments)), caller, session).AsTask(); ledger.Add(command, task); return task; }
            async Task Completed(string command, string kind, string? expected, string arguments = "")
            {
                var invocation = Invoke(command, arguments); await invocation;
                var notice = ui.Notices.Last(); Check(notice.GetProperty("kind").GetString() == kind &&
                    notice.GetProperty("presence").GetString() == (expected is null ? "undefined" : "json"));
                if (kind == "editor") Check(notice.GetProperty("value").GetRawText() == "\"line1\\nline2\\ud800\"");
                else if (expected is not null) Check(notice.GetProperty("value").GetString() == expected);
                var settlement = node.SourceOperations.Last().Value;
                Check(settlement.GetProperty("settled").GetBoolean() && settlement.GetProperty("kind").GetString() == "command" &&
                    settlement.GetProperty("status").GetString() == "fulfilled" && settlement.GetProperty("observation").GetProperty("publicationJoined").GetBoolean());
            }
            await Completed("qualification-input", "input", "");
            Check(ui.Placeholder == "" && ui.InputTimeout == 1234);
            await Completed("qualification-editor", "editor", "line1\nline2\ud800");
            Check(ui.Prefill == "line1\nline2\ud800");
            var acquired = ui.InputCalls; await Completed("qualification-preabort", "preabort", null);
            Check(ui.InputCalls == acquired);
            var timeout = Invoke("qualification-timeout"); await timeout;
            Check(ui.TimeoutOriginal is { IsCompletedSuccessfully: true } && ui.Notices.Last().GetProperty("kind").GetString() == "timeout" &&
                ui.Notices.Last().GetProperty("value").GetBoolean() == false);
            var child = Invoke("qualification-child", "held-child"); await Stage(ui.ChildEntered.Task, child);
            Check(!child.IsCompleted && ui.HeldOriginal is { IsCompleted: false });
            var abort = Invoke("qualification-abort", "held-child"); await abort; await child;
            Check(ui.Notices.Last().GetProperty("kind").GetString() == "child" && !ui.Notices.Last().GetProperty("value").GetBoolean());
            await ledger.Observe(ui.HeldOriginal!); ledger.AcknowledgeCancelled(ui.HeldOriginal!, ui.HeldToken);
            var cancelledParent = Invoke("qualification-parent", caller: parent.Token); await Stage(ui.ParentEntered.Task, cancelledParent);
            var parentCallback = supplied.Get("qualification-parent");
            Check(!parentCallback.CancelledAtEntry && parentCallback.Context.OperationCancellationToken == parent.Token && parentCallback.Token != parent.Token);
            parent.Cancel(); await ledger.Observe(cancelledParent); ledger.AcknowledgeCancelled(cancelledParent, parentCallback.Token);
            await ledger.Observe(ui.ParentOriginal!); ledger.AcknowledgeCancelled(ui.ParentOriginal!, ui.ParentToken);
            var session = Invoke("qualification-session", session: actualSessionToken); await Stage(ui.SessionEntered.Task, session);
            var sessionCallback = supplied.Get("qualification-session");
            Check(!sessionCallback.CancelledAtEntry && sessionCallback.Context.SessionCancellationToken == actualSessionToken && sessionCallback.Token != actualSessionToken);
            Check(!session.IsCompleted && ui.SessionOriginal is { IsCompleted: false });
            var retirement = retireActualSession() ?? throw new IOException("Actual session retirement returned no original.");
            ledger.Add("actual-session-retirement", retirement); await retirement;
            Check(actualSessionToken.IsCancellationRequested);
            await ledger.Observe(session); ledger.AcknowledgeCancelled(session, sessionCallback.Token);
            await ledger.Observe(ui.SessionOriginal!); ledger.AcknowledgeCancelled(ui.SessionOriginal!, ui.SessionToken);
            Check(node.ActiveContexts == 0 && node.WorkerSnapshot is { PendingCalls: 0 });
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            // Release actual held UI originals even after early assertions fail, then join all
            // callbacks before closing their registry/native owner. No cancellation is assumed expected.
            ui.Release.TrySetResult();
            await ledger.Finish(errors);
            try { if (scope is not null) { var task = scope.DisposeAsync().AsTask(); ledger.Add("scope-retirement", task); await task; } } catch (Exception error) { errors.Add(error); }
            try { var task = node.DisposeAsync().AsTask(); ledger.Add("node-retirement", task); await task; } catch (Exception error) { errors.Add(error); }
            try { var task = registry.DisposeAsync().AsTask(); ledger.Add("registry-retirement", task); await task; } catch (Exception error) { errors.Add(error); }
            try { source.Dispose(); } catch (Exception error) { errors.Add(error); }
            await ledger.Finish(errors);
        }
        if (errors.Count != 0) throw new AggregateException("Private UI criteria and full actual original inventory.", errors);
    }
    private static async Task Stage(Task entered, Task original)
    { var first = await Task.WhenAny(entered, original).WaitAsync(TimeSpan.FromSeconds(10)); Check(ReferenceEquals(first, entered)); await entered; }
    private sealed class Ui(Ledger ledger, Func<long> generation) : IExtensionUiProvider
    {
        internal readonly TaskCompletionSource ChildEntered = Gate(), ParentEntered = Gate(), SessionEntered = Gate(), Release = Gate();
        internal readonly List<JsonElement> Notices = [];
        internal Task? HeldOriginal, ParentOriginal, SessionOriginal, TimeoutOriginal;
        internal CancellationToken HeldToken, ParentToken, SessionToken;
        internal string? Placeholder, Prefill; internal double? InputTimeout; internal int InputCalls;
        public IExtensionUiScope OpenScope(IExtensionContext context)
        { Check(context.OwnerId == "private-original-ui" && context.OwnerGeneration > 0); return new Scope(this, ledger, generation()); }
        private sealed class Scope(Ui ui, Ledger ledger, long generation) : IExtensionUiScope
        {
            public ExtensionUiCapabilities Capabilities => new(ExtensionUiMode.Rpc, 1, generation,
                [ExtensionUiFeature.Input, ExtensionUiFeature.Editor, ExtensionUiFeature.Confirm, ExtensionUiFeature.Notify]);
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default)
            {
                ui.InputCalls++; ui.Placeholder = placeholder; ui.InputTimeout = options?.TimeoutMilliseconds;
                if (title == "Original SDK input") { Check(placeholder == "" && options?.TimeoutMilliseconds == 1234); var task = Task.FromResult(ExtensionUiOutcome<string>.FromValue("")); ledger.Add("native-input-value", task); return new(task); }
                TaskCompletionSource entered;
                if (title == "Held actual parent cancellation") entered = ui.ParentEntered;
                else { Check(title == "Held actual session retirement"); entered = ui.SessionEntered; }
                var original = HeldInput(cancellationToken); ledger.Add(title, original);
                if (ReferenceEquals(entered, ui.ParentEntered)) { ui.ParentOriginal = original; ui.ParentToken = cancellationToken; }
                else { ui.SessionOriginal = original; ui.SessionToken = cancellationToken; }
                entered.TrySetResult(); return new(original);
            }
            private async Task<ExtensionUiOutcome<string>> HeldInput(CancellationToken token)
            { await ui.Release.Task.WaitAsync(token); token.ThrowIfCancellationRequested(); return ExtensionUiOutcome<string>.Cancelled(); }
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); Check(title == "Original SDK editor" && prefill is not null); ui.Prefill = prefill; var task = Task.FromResult(ExtensionUiOutcome<string>.FromValue(prefill!)); ledger.Add("native-editor", task); return new(task); }
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default)
            {
                if (title == "Original SDK timeout")
                {
                    Check(message == "One millisecond" && options?.TimeoutMilliseconds == 1);
                    var original = Timeout(cancellationToken); ledger.Add("native-confirm-timeout", original); return new(original);
                }
                // Source supplied { signal }; the JS caller strips signal while retaining explicit empty options.
                Check(title == "Held child confirmation" && message == "Abort only this child" &&
                    options is not null && options.TimeoutMilliseconds is null);
                ui.HeldToken = cancellationToken; var held = Hold(cancellationToken); ui.HeldOriginal = held;
                ledger.Add("native-child-confirm", held); ui.ChildEntered.TrySetResult(); return new(held);
            }
            private async Task<ExtensionUiOutcome<bool>> Timeout(CancellationToken token)
            { var timer = Task.Delay(TimeSpan.FromMilliseconds(1), token); ui.TimeoutOriginal = timer; ledger.Add("actual-native-timeout-timer", timer); await timer; return ExtensionUiOutcome<bool>.TimedOut(); }
            private async Task<ExtensionUiOutcome<bool>> Hold(CancellationToken token)
            { await ui.Release.Task.WaitAsync(token); token.ThrowIfCancellationRequested(); return ExtensionUiOutcome<bool>.Cancelled(); }
            public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); var message = notification as ExtensionUiNotify ?? throw new IOException("Private notification type differs."); Check(message.Kind == ExtensionUiNotifyKind.Info); using var document = JsonDocument.Parse(message.Message); ui.Notices.Add(document.RootElement.Clone()); var task = Task.FromResult(ExtensionUiOutcome<ExtensionUiPublication>.FromValue(ExtensionUiPublication.Published)); ledger.Add("native-result-notification", task); return new(task); }
            public ValueTask DisposeAsync() { var task = Task.CompletedTask; ledger.Add("native-ui-scope-disposal", task); return new(task); }
        }
    }
}
