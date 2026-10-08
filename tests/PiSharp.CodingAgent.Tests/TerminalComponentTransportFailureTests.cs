using System.Collections.Immutable;
using System.Text;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Tui;
using PiSharp.Tui.Input;

// Actual borrowed protocol + registry/native UI composition. No Node process, source factory,
// supervisor injection, worker authority or whole-plugin acceptance is implied by these controls.
internal static class TerminalComponentTransportFailureTests
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Originals> capturedLedgers = new();
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] RawCapturedOriginals =>
        capturedLedgers.SelectMany(ledger => ledger.Capture()).ToArray();
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [ ("custom-native.actual-protocol-write-failure-joins-held-open-write-and-disposal", () => Run(false)),
      ("custom-native.actual-protocol-write-failure-retains-held-disposal-sibling-originals", () => Run(true)) ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Actual protocol/native-open failure control failed."); }

    private static async Task Run(bool disposalFault)
    {
        var ledger = new Originals(disposalFault ? "held-disposal-siblings" : "held-open-write-disposal"); var errors = new List<Exception>();
        var reader = new FeedReader(); var writer = new FaultWriter(ledger);
        var console = new PhysicalConsole(ledger); var focusOwner = new TerminalEditorFocusOwner();
        var view = new TerminalSessionView(console, new Viewport(), null, focusOwner);
        TerminalEditorFocusOwner.Attachment? focus = null;
        var focusChanges = new List<TerminalEditorFocusSnapshot>();
        var input = new TerminalExtensionInputAdmission(1, () => 1);
        var provider = new TerminalCustomComponentUiProvider(new UnavailableExtensionUiProvider(), view, input, () => 1);
        var registry = new ExtensionRegistry(null, input.Decorate(provider));
        var disposalEntered = Gate(); var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ledger.Add(disposal.Task, "source-disposal");
        var left = new IOException("Exact held source-disposal left original.");
        var right = new IOException("Exact held source-disposal sibling original.");
        RegistrationScope? scope = null; Task? nativeOpen = null, invocation = null;
        var commandEntered = Gate(); bool assertionsReached = false;
        var protocol = new WorkerProtocolConnection(new WorkerProtocolTransport(reader, writer), 3, 7,
            async (_, token) =>
            {
                invocation = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "native-open", JsonData.EmptyObject, token).AsTask();
                ledger.Add(invocation, "invocation"); commandEntered.TrySetResult();
                await invocation.ConfigureAwait(false); return WorkerValue.Undefined;
            });
        ledger.Add(protocol.Completion, "protocol-completion");
        try
        {
            focus = focusOwner.AttachEditor(Guid.NewGuid(), request =>
            {
                var role = focusChanges.Count == 0 ? "lose" : "restore";
                ledger.Add(request.Completion, "focus-request-" + role);
                ledger.Add(ApplyFocus(request, role), "focus-apply-" + role);
            });
            var startView = view.StartAsync(CancellationToken.None).AsTask(); ledger.Add(startView, "view-start"); await startView;
            var initialDraft = view.SetDraftAsync(new("", 0) { EditorFocus = focus.Snapshot }, CancellationToken.None).AsTask();
            ledger.Add(initialDraft, "initial-draft"); await initialDraft;
            var activation = registry.ActivateAsync("native-transport", new Extension(entries => entries.RegisterCommand(new("native-open", "native-open", "",
                async (_, context, token) =>
                {
                    var identity = new ExtensionCustomComponentIdentity(context.OwnerId, context.OwnerGeneration, 1, "held-native-open");
                    var callbacks = new ExtensionCustomComponentCallbacks(identity,
                        (_, _, _) => Task.FromResult(new ExtensionCustomComponentRows(["actual-native-row"], [17])),
                        (_, _, _) => Task.CompletedTask,
                        _ => { disposalEntered.TrySetResult(); return disposal.Task; });
                    nativeOpen = ((IExtensionCustomComponentUi)((IExtensionUiContext)context).Ui).OpenCustomComponentAsync(callbacks, token);
                    ledger.Add(nativeOpen, "native-open"); await nativeOpen.ConfigureAwait(false);
                }))));
            ledger.Add(activation, "registry-activation"); scope = await activation;
            var codec = new WorkerFrameCodec();
            reader.Feed(codec.Encode(new(WorkerMessageKind.Hello, 3, 7,
                Features: ["requests", "callbacks", "progress", "cancel", "tagged-values"])));
            var start = protocol.StartAsync(); ledger.Add(start, "protocol-start"); await start;
            console.HoldNext = true;
            reader.Feed(codec.Encode(new(WorkerMessageKind.Request, 3, 7, Id: 1, Method: "native-open", Value: WorkerValue.Absent)));
            await Stage(commandEntered.Task, protocol.Completion);
            await Stage(console.WriteEntered.Task, invocation!, protocol.Completion);
            Check(console.WriteEntered.Task.IsCompleted && nativeOpen is { IsCompleted: false } &&
                invocation is { IsCompleted: false } && protocol.Snapshot.ActiveCallbacks == 1 && console.Active == 1);
            Check(console.Writes.Any(row => row.Contains("actual-native-row\u001b[0m", StringComparison.Ordinal)));
            Check(focusChanges.Count == 1 && !focusChanges[0].EditorOwnsFocus &&
                focusOwner.IsCurrent(focusChanges[0]) && !focus.Snapshot.EditorOwnsFocus);
            writer.FailNext = true;
            var failed = protocol.StartRequest("force-actual-write-failure", WorkerValue.Absent);
            ledger.Add(failed.Result, "request-result"); ledger.Add(failed.CancellationWrite, "cancellation-write");
            await ledger.Expect(failed.Result, error => IsExpected(error, writer.Failure, left, right));
            await Stage(writer.FailureEntered.Task, protocol.Completion);
            Check(protocol.Snapshot.Stopped && !protocol.Completion.IsCompleted && !nativeOpen!.IsCompleted && console.Active == 1);
            console.Release.TrySetResult();
            await Stage(disposalEntered.Task, nativeOpen!, protocol.Completion);
            Check(disposalEntered.Task.IsCompleted && !nativeOpen!.IsCompleted && !protocol.Completion.IsCompleted && console.Active == 0);
            if (disposalFault) disposal.TrySetException([left, right]); else disposal.TrySetResult();
            await ledger.Expect(nativeOpen!, error => IsExpected(error, writer.Failure, left, right));
            await ledger.Expect(invocation!, error => IsExpected(error, writer.Failure, left, right));
            await ledger.Expect(protocol.Completion, error => IsExpected(error, writer.Failure, left, right));
            await ledger.Expect(failed.CancellationWrite, error => IsExpected(error, writer.Failure, left, right));
            foreach (var original in writer.FailedOriginals) await ledger.Expect(original, error => ReferenceEquals(error, writer.Failure) ||
                error is AggregateException aggregate && aggregate.InnerExceptions.All(child => ReferenceEquals(child, writer.Failure)));
            if (disposalFault)
            {
                await ledger.Expect(disposal.Task, error => IsExpected(error, writer.Failure, left, right));
                Check(Contains(ledger.Error(nativeOpen!), left) && Contains(ledger.Error(nativeOpen!), right) &&
                    Contains(ledger.Error(disposal.Task), left) && Contains(ledger.Error(disposal.Task), right));
            }
            Check(protocol.Snapshot.ActiveCallbacks == 0 && protocol.Snapshot.PendingCalls == 0 &&
                protocol.Snapshot.PendingWrites == 0 && protocol.Snapshot.BufferedBytes == 0); assertionsReached = true;
            Check(focusChanges.Count == 2 && focusChanges[1].EditorOwnsFocus &&
                focusChanges[1].EditorLifetimeId == focusChanges[0].EditorLifetimeId &&
                focusChanges[1].Revision > focusChanges[0].Revision && focusOwner.IsCurrent(focusChanges[1]));
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            console.Release.TrySetResult(); disposal.TrySetResult(); reader.Finish();
            Acquire(() => protocol.DisposeAsync().AsTask(), "protocol-retirement");
            Acquire(() => input.StopAdmissionAndJoinAsync().AsTask(), "input-retirement");
            if (scope is not null) await AcquireRetirement(() => scope.DisposeAsync().AsTask(), "scope-retirement");
            await AcquireRetirement(() => registry.DisposeAsync().AsTask(), "registry-retirement");
            // Full independent collectors: retirement remains able to clear the actual view.
            await ledger.JoinAll(errors);
            try { focus?.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (focusChanges.Count != 0 && focusOwner.IsCurrent(focusChanges[^1]))
                errors.Add(new InvalidOperationException("Editor focus attachment survived joined native retirement."));
            Task? viewCleanup = null;
            try { viewCleanup = view.DisposeAsync().AsTask(); ledger.Add(viewCleanup, "view-cleanup"); await ledger.JoinCleanup(viewCleanup, errors); }
            catch (Exception error) { errors.Add(error); }
            await ledger.JoinAll(errors);
            if (console.Active != 0 || console.Started != console.Settled || console.Disposed || reader.Disposed || writer.Disposed)
                errors.Add(new InvalidOperationException("Borrowed transport/physical originals were abandoned or disposed."));
        }
        if (errors.Count != 0) throw new AggregateException("Actual protocol/native-open control failed.", errors);
        async Task ApplyFocus(TerminalEditorFocusOwner.Request request, string role)
        {
            try
            {
                Check(request.TryApply());
                var snapshot = focus!.Snapshot; focusChanges.Add(snapshot);
                var paint = view.SetDraftAsync(new("", 0)
                    { EditorFocus = snapshot, EditorOwnsFocus = snapshot.EditorOwnsFocus }, CancellationToken.None).AsTask();
                ledger.Add(paint, "focus-paint-" + role); await paint;
                request.Complete();
            }
            catch (Exception error) { request.Complete(error); throw; }
        }
        void Acquire(Func<Task> acquire, string role) { try { ledger.Add(acquire(), role); } catch (Exception error) { errors.Add(error); } }
        async Task AcquireRetirement(Func<Task> acquire, string role)
        {
            Task? original = null;
            try
            {
                original = acquire(); ledger.Add(original, role);
                if (assertionsReached && disposalFault)
                    await ledger.AllowKnownFault(original, error => Graph(error).All(node => node is AggregateException || ReferenceEquals(node, left) || ReferenceEquals(node, right)));
            }
            catch (Exception error) { errors.Add(error); }
        }
    }

    // Deadline is diagnostic only: it neither cancels nor replaces any actual original.
    private static async Task Stage(Task entered, params Task[] terminals)
    {
        var winner = await Task.WhenAny(new[] { entered }.Concat(terminals)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(ReferenceEquals(winner, entered));
    }
    private static bool IsExpected(Exception root, Exception write, Exception left, Exception right)
    {
        foreach (var node in Graph(root))
            if (node is not AggregateException && node is not OperationCanceledException { CancellationToken.IsCancellationRequested: true } &&
                node is not WorkerProtocolException { Failure: WorkerProtocolFailure.Transport } &&
                !ReferenceEquals(node, write) && !ReferenceEquals(node, left) && !ReferenceEquals(node, right)) return false;
        return true;
    }
    private static bool Contains(Exception? root, Exception target) => root is not null && Graph(root).Contains(target);
    private static HashSet<Exception> Graph(Exception root)
    {
        var result = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var todo = new Stack<Exception>(); todo.Push(root); int edges = 0;
        while (todo.TryPop(out var current))
        {
            if (!result.Add(current)) continue;
            if (result.Count > 1024) throw new InvalidOperationException("Fault graph node limit.");
            var children = current is AggregateException aggregate ? aggregate.InnerExceptions :
                current.InnerException is { } inner ? (IEnumerable<Exception>)[inner] : [];
            foreach (var child in children) { if (++edges > 4096) throw new InvalidOperationException("Fault graph edge limit."); todo.Push(child); }
        }
        return result;
    }
    private sealed class Originals
    {
        private readonly object gate = new(); private readonly Dictionary<Task, Record> records = new(ReferenceEqualityComparer.Instance);
        private readonly string phase;
        internal Originals(string phase) { this.phase = phase; capturedLedgers.Enqueue(this); }
        private sealed class Record { internal readonly HashSet<string> Phases = []; internal Exception? Error, Direct; internal AggregateException? Aggregate; internal bool Joined, Expected, Collected; }
        internal void Add(Task original, string role)
        {
            lock (gate)
            {
                if (!records.TryGetValue(original, out var record)) records.Add(original, record = new());
                record.Phases.Add(phase + ":" + role);
            }
        }
        internal (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] Capture()
        { lock (gate) return records.SelectMany(pair => pair.Value.Phases.Select(role => (role, pair.Key, pair.Value.Aggregate, pair.Value.Direct))).ToArray(); }
        internal Exception? Error(Task original) { lock (gate) return records[original].Error; }
        private async Task<Record> Join(Task original)
        {
            Record record; lock (gate) record = records[original];
            if (record.Joined) return record;
            try { await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                record.Direct = error;
                record.Aggregate = original.IsFaulted ? original.Exception : null;
                record.Error = record.Aggregate ?? error;
            }
            finally { record.Joined = true; }
            return record;
        }
        internal async Task JoinCleanup(Task original, List<Exception> errors)
        {
            var record = await Join(original);
            if (record.Error is not null) errors.Add(record.Error);
            lock (gate) record.Collected = true;
        }
        internal async Task Expect(Task original, Func<Exception, bool> assert)
        {
            var record = await Join(original);
            Check(record.Error is not null && assert(record.Error)); record.Expected = true;
        }
        internal async Task AllowKnownFault(Task original, Func<Exception, bool> assert)
        {
            var record = await Join(original);
            if (record.Error is not null) { Check(assert(record.Error)); record.Expected = true; }
        }
        internal async Task JoinAll(List<Exception> errors)
        {
            while (true)
            {
                Task[] originals; lock (gate) originals = records.Where(pair => !pair.Value.Collected).Select(pair => pair.Key).ToArray();
                if (originals.Length == 0) return;
                foreach (var original in originals)
                {
                    var record = await Join(original);
                    if (record.Error is not null && !record.Expected) errors.Add(record.Error);
                    lock (gate) record.Collected = true;
                }
            }
        }
    }
    private sealed class Extension(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FeedReader : TextReader
    {
        private readonly Channel<string> frames = Channel.CreateUnbounded<string>(); private string current = ""; private int position;
        internal bool Disposed; internal void Feed(string frame) { Check(frames.Writer.TryWrite(frame)); }
        internal void Finish() => frames.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken token = default)
        {
            if (position == current.Length)
            {
                if (!await frames.Reader.WaitToReadAsync(token)) return 0;
                Check(frames.Reader.TryRead(out var next)); current = next!; position = 0;
            }
            var count = Math.Min(buffer.Length, current.Length - position); current.AsMemory(position, count).CopyTo(buffer); position += count; return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class FaultWriter(Originals ledger) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        internal volatile bool FailNext; internal bool Disposed;
        internal readonly IOException Failure = new("Exact original borrowed protocol write failure.");
        internal readonly TaskCompletionSource FailureEntered = Gate();
        internal readonly List<Task> FailedOriginals = [];
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            if (!FailNext) return Task.CompletedTask;
            FailNext = false; var original = Task.FromException(Failure); FailedOriginals.Add(original); ledger.Add(original, "failed-protocol-write"); FailureEntered.TrySetResult(); return original;
        }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Viewport : ITerminalViewportSource { public TerminalViewport ReadViewport() => new(80, 8, 0, 0, 80, 8); }
    private sealed class PhysicalConsole(Originals ledger) : IConsoleTerminal
    {
        internal bool HoldNext, Disposed; internal int Active, Started, Settled;
        internal readonly List<string> Writes = []; internal readonly TaskCompletionSource WriteEntered = Gate(), Release = Gate();
        public TerminalLeaseSnapshot Snapshot => new(new(0, 0, 65001, 65001, 25, true, 0, 0), new(0, 0, 65001, 65001, 25, true, 0, 0), null, false, false, 0, Active, 0, 0, Started, Settled);
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw new InvalidOperationException("No physical terminal input acquisition in this control.");
        public ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        {
            var held = HoldNext && text.Span.Contains("actual-native-row", StringComparison.Ordinal);
            var original = WriteOriginal(text, held);
            ledger.Add(original, held ? "held-physical-write" : "physical-write");
            return new(original);
        }
        private async Task WriteOriginal(ReadOnlyMemory<char> text, bool held)
        {
            Check(Interlocked.Increment(ref Active) == 1); Interlocked.Increment(ref Started);
            try { Writes.Add(text.ToString()); if (held) { HoldNext = false; WriteEntered.TrySetResult(); await Release.Task; } }
            finally { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Settled); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
