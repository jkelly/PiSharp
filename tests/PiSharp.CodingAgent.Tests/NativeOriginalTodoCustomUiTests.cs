using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Interactive;
using PiSharp.CodingAgent;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Tui;
using PiSharp.Tui.Input;

// Root supplies a genuine admitted launch and an already-owned actual engine attachment.
// This fixture neither creates a session nor acquires Node/source/package authority.
internal static class NativeOriginalTodoCustomUiTests
{
    internal sealed record Evidence(string SessionId, long Generation, string? LeafId,
        int PhysicalWrites, int FocusTransitions, bool EscapeConsumed);
    private sealed class Original(Task task)
    {
        internal Task Task = task; internal readonly HashSet<string> Roles = [];
        internal AggregateException? Aggregate; internal Exception? Direct; internal bool Joined;
    }
    private static readonly ConcurrentQueue<Original> captured = new();
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] RawCapturedOriginals =>
        captured.SelectMany(record => record.Roles.Select(role => (role, record.Task, record.Aggregate, record.Direct))).ToArray();
    private static void Check(bool value) { if (!value) throw new IOException("Actual original todo custom UI control failed."); }

    internal static async Task<Evidence> RunAsync(NodeCommandInputWorkerLaunch admittedLaunch,
        string workspace, ReplaceableAgentSession borrowedOwner, string expectedTodoText)
    {
        ArgumentNullException.ThrowIfNull(admittedLaunch); ArgumentNullException.ThrowIfNull(borrowedOwner);
        Check(!string.IsNullOrWhiteSpace(expectedTodoText) && expectedTodoText.Length <= 128 &&
            expectedTodoText.All(character => character is >= ' ' and <= '~'));
        var attached = borrowedOwner.Current;
        borrowedOwner.ValidateAttachment(attached);
        // Expected state must already exist in actual acknowledged engine ancestry.
        Check(attached.Session.Snapshot.Context.Ancestry.Any(entry =>
            entry.WireBody.Value.TryGetProperty("message", out var message) &&
            message.TryGetProperty("role", out var role) && role.GetString() == "toolResult" &&
            message.TryGetProperty("toolName", out var tool) && tool.GetString() == "todo" &&
            message.TryGetProperty("details", out var details) &&
            details.TryGetProperty("todos", out var todos) && todos.EnumerateArray().Any(todo =>
                todo.GetProperty("text").GetString() == expectedTodoText)));
        var originals = new Dictionary<Task, Original>(ReferenceEqualityComparer.Instance);
        var errors = new List<Exception>(); var gate = new object();
        var console = new Console(expectedTodoText);
        var focusOwner = new TerminalEditorFocusOwner();
        var view = new TerminalSessionView(console, new Viewport(), null, focusOwner);
        var input = new TerminalExtensionInputAdmission(1, () => borrowedOwner.Current.Generation);
        var ui = new TerminalCustomComponentUiProvider(new UnavailableExtensionUiProvider(), view, input,
            () => borrowedOwner.Current.Generation);
        var sessions = new NativeSessionSnapshotProvider(); sessions.Attach(borrowedOwner);
        var registry = new ExtensionRegistry(null, input.Decorate(ui), sessions);
        var node = new NodeCommandInputExtension(admittedLaunch, workspace, sourcePaths: [NodeTierAAdmission.TodoSource]);
        RegistrationScope? scope = null; TerminalEditorFocusOwner.Attachment? focus = null;
        var transitions = new List<TerminalEditorFocusSnapshot>(); Task? command = null; Evidence? evidence = null;
        try
        {
            focus = focusOwner.AttachEditor(Guid.NewGuid(), request =>
            {
                var phase = transitions.Count == 0 ? "lose" : "restore";
                Track(request.Completion, "focus-request-" + phase);
                Track(Apply(request, phase), "focus-apply-" + phase);
            });
            await Own(view.StartAsync(CancellationToken.None).AsTask(), "view-start");
            await Own(view.SetDraftAsync(new("", 0) { EditorFocus = focus.Snapshot }, CancellationToken.None).AsTask(), "initial-draft");
            var activation = registry.ActivateAsync("actual-original-todo", node); Track(activation, "activation");
            await Own(activation, "activation"); scope = activation.GetAwaiter().GetResult();
            Check(node.SourceLoadReport!.Value.GetProperty("sourceFunctionsRemainInNode").GetBoolean());
            var snapshot = registry.CaptureSnapshot();
            await Own(registry.DispatchObservationsAsync(snapshot, "session_start",
                JsonData.Parse("{\"type\":\"session_start\",\"reason\":\"startup\"}")).AsTask(), "actual-session-start");
            borrowedOwner.ValidateAttachment(attached);
            command = registry.InvokeCommandAsync(snapshot, "todos", JsonData.Parse("\"\"")).AsTask();
            Track(command, "original-todos-command");
            var winner = await Task.WhenAny(console.TodoPainted.Task, command).WaitAsync(TimeSpan.FromSeconds(10));
            Check(ReferenceEquals(winner, console.TodoPainted.Task) && !command.IsCompleted);
            Check(transitions.Count == 1 && !transitions[0].EditorOwnsFocus &&
                focusOwner.IsCurrent(transitions[0]) && console.Writes.Any(row => row.Contains(expectedTodoText, StringComparison.Ordinal)));
            // The actual native raw listener calls genuine TodoListComponent.handleInput/matchesKey.
            var escape = input.DispatchAsync("\u001b", attached.Generation, CancellationToken.None);
            await Own(escape, "actual-escape-input"); var outcome = escape.GetAwaiter().GetResult();
            Check(outcome.Disposition == ExtensionTerminalInputDisposition.Consumed);
            await Own(command, "original-todos-command");
            Check(node.ActiveContexts == 0 && node.WorkerSnapshot is { ActiveCallbacks: 0, PendingCalls: 0 });
            Check(transitions.Count == 2 && transitions[1].EditorOwnsFocus &&
                transitions[1].EditorLifetimeId == transitions[0].EditorLifetimeId &&
                transitions[1].Revision > transitions[0].Revision && focusOwner.IsCurrent(transitions[1]));
            borrowedOwner.ValidateAttachment(attached);
            var state = attached.Session.Snapshot;
            evidence = new(state.Log.Header.Id, attached.Generation, state.Context.LeafId,
                console.Writes.Count, transitions.Count, true);
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            // Admission retirement and component owners settle while focus/view are still alive.
            Cleanup(() => input.StopAdmissionAndJoinAsync().AsTask(), "input-retirement");
            if (scope is not null) Cleanup(() => scope.DisposeAsync().AsTask(), "scope-retirement");
            Cleanup(() => registry.DisposeAsync().AsTask(), "registry-retirement");
            await JoinAll();
            try { focus?.Dispose(); } catch (Exception error) { Fail(error); }
            Cleanup(() => view.DisposeAsync().AsTask(), "view-cleanup");
            await JoinAll();
            if (console.Active != 0 || console.Started != console.Settled || console.Disposed)
                Fail(new IOException("Borrowed actual physical writer did not settle."));
        }
        if (errors.Count != 0) throw new AggregateException("Actual original todo custom UI originals failed.", errors);
        return evidence ?? throw new IOException("Original todo evidence missing.");

        void Fail(Exception error) { lock (gate) errors.Add(error); }
        void Track(Task task, string phase)
        {
            lock (gate)
            {
                if (!originals.TryGetValue(task, out var record))
                { record = new(task); originals.Add(task, record); captured.Enqueue(record); }
                record.Roles.Add(phase);
            }
        }
        async Task Own(Task task, string phase)
        {
            Track(task, phase); Original record; lock (gate) record = originals[task];
            try { await task.ConfigureAwait(false); }
            catch (Exception error)
            {
                CaptureOnce(record, error);
                throw;
            }
            finally { CaptureOnce(record, null); }
        }
        void CaptureOnce(Original record, Exception? direct)
        {
            lock (gate)
            {
                if (record.Joined) return;
                record.Direct = direct;
                record.Aggregate = record.Task.IsFaulted ? record.Task.Exception : null;
                if (direct is not null) Fail(record.Aggregate ?? direct);
                record.Joined = true;
            }
        }
        async Task Apply(TerminalEditorFocusOwner.Request request, string phase)
        {
            try
            {
                Check(request.TryApply());
                var ownership = focus!.Snapshot; transitions.Add(ownership);
                await Own(view.SetDraftAsync(new("", 0) { EditorFocus = ownership,
                    EditorOwnsFocus = ownership.EditorOwnsFocus }, CancellationToken.None).AsTask(), "focus-paint-" + phase);
                request.Complete();
            }
            catch (Exception error) { request.Complete(error); throw; }
        }
        void Cleanup(Func<Task> acquire, string phase)
        {
            try { Track(acquire(), phase); }
            catch (Exception error) { Fail(error); }
        }
        async Task JoinAll()
        {
            while (true)
            {
                Original[] pending; lock (gate) pending = originals.Values.Where(record => !record.Joined).ToArray();
                if (pending.Length == 0) return;
                foreach (var record in pending)
                {
                    // Own may have observed this entry while another original in the snapshot joined.
                    lock (gate) if (record.Joined) continue;
                    try { await record.Task.ConfigureAwait(false); }
                    catch (Exception error) { CaptureOnce(record, error); }
                    finally { CaptureOnce(record, null); }
                }
            }
        }
    }
    private sealed class Viewport : ITerminalViewportSource
    { public TerminalViewport ReadViewport() => new(100, 16, 0, 0, 100, 16); }
    private sealed class Console(string expected) : IConsoleTerminal
    {
        internal readonly List<string> Writes = []; internal readonly TaskCompletionSource TodoPainted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Active, Started, Settled; internal bool Disposed;
        public TerminalLeaseSnapshot Snapshot => new(new(0, 0, 65001, 65001, 25, true, 0, 0), new(0, 0, 65001, 65001, 25, true, 0, 0),
            null, false, false, 0, Active, 0, 0, Started, Settled);
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw new IOException("No console input acquisition.");
        public ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Check(Interlocked.Increment(ref Active) == 1); Interlocked.Increment(ref Started);
            try
            {
                var row = text.ToString(); Writes.Add(row);
                if (row.Contains(expected, StringComparison.Ordinal) && row.Contains("Press Escape", StringComparison.Ordinal)) TodoPainted.TrySetResult();
                return ValueTask.CompletedTask;
            }
            finally { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Settled); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
