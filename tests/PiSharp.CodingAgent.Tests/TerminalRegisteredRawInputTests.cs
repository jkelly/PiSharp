using System.Collections.Immutable;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Tui;

internal static class TerminalRegisteredRawInputTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [ ("terminal-registered-input.actual-reader-transformation-and-protocol-order", ReaderAndProtocol) ];
    private static async Task ReaderAndProtocol()
    {
        var terminal = new ConsoleFixture(); var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new TerminalExtensionInputAdmission(1, () => 1);
        var registry = new ExtensionRegistry(null, admission.Decorate(new NoUi()));
        RegistrationScope? scope = null;
        var originals = new List<Task>(); var faults = new List<Exception>();
        var submitted = new List<string>(); var seen = new List<string>();
        try
        {
            scope = await registry.ActivateAsync("raw", new Extension(entries => entries.RegisterCommand(new("register", "register", "",
                (_, context, _) =>
                {
                    ((IExtensionTerminalInput)((IExtensionUiContext)context).Ui).OnTerminalInput(data =>
                    {
                        seen.Add(data);
                        return new(Data: data == "x" ? "mapped" : data == "\u001b[6;20;10t" ? "c" : data);
                    });
                    return ValueTask.CompletedTask;
                }))));
            var registration = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "register", JsonData.EmptyObject).AsTask();
            originals.Add(registration); await registration;
            var actualReader = new TerminalChatInput(terminal, null, null, null,
                terminalInputAdmission: admission, captureTerminalSessionGeneration: () => 1).RunAsync(
                    (line, _) => { submitted.Add(line); return Task.FromResult(true); },
                    (_, _) => { rendered.TrySetResult(); return ValueTask.CompletedTask; }, () => { }, CancellationToken.None);
            originals.Add(actualReader);
            await terminal.Input.Writer.WriteAsync("\u001b[?997;2n"); // Recognized before any raw listener.
            await terminal.Input.Writer.WriteAsync("x");
            await Task.WhenAny(rendered.Task, actualReader); Check(rendered.Task.IsCompleted);
            await terminal.Input.Writer.WriteAsync("\u001b[6;20;10t"); // Raw listener transforms before cell recognition.
            await terminal.Input.Writer.WriteAsync("\r"); terminal.Input.Writer.TryComplete();
            Check(await actualReader == TerminalInputExit.Eof);
            Check(submitted.Count == 1 && submitted[0] == "mappedc");
            Check(seen.Count == 3 && seen[0] == "x" && seen[1] == "\u001b[6;20;10t" && seen[2] == "\r");
            Check(terminal.Active == 0 && terminal.Started == terminal.Settled && !terminal.Disposed);
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            terminal.Input.Writer.TryComplete();
            Acquire(() => admission.StopAdmissionAndJoinAsync().AsTask(), originals, faults);
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask(), originals, faults);
            Acquire(() => registry.DisposeAsync().AsTask(), originals, faults);
            foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
                try { await original; } catch (Exception error) { faults.Add(original.Exception ?? error); }
            if (terminal.Active != 0 || terminal.Started != terminal.Settled) faults.Add(new InvalidOperationException("Physical reader was not joined."));
        }
        if (faults.Count != 0) throw new AggregateException(faults);
    }
    private static void Acquire(Func<Task> acquire, List<Task> tasks, List<Exception> faults)
    { try { tasks.Add(acquire()); } catch (Exception error) { faults.Add(error); } }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Actual terminal raw input control failed."); }
    private sealed class Extension(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ConsoleFixture : IConsoleTerminal
    {
        internal readonly Channel<string> Input = Channel.CreateBounded<string>(8);
        internal int Active, Started, Settled;
        internal bool Disposed;
        public TerminalLeaseSnapshot Snapshot => new(new(0, 0, 65001, 65001, 25, true, 0, 0),
            new(0, 0, 65001, 65001, 25, true, 0, 0), null, false, false, Active, 0, Started, Settled, 0, 0);
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            Check(Interlocked.Increment(ref Active) == 1); Interlocked.Increment(ref Started);
            try
            {
                if (!await Input.Reader.WaitToReadAsync(token)) return 0;
                Check(Input.Reader.TryRead(out var text)); text!.AsMemory().CopyTo(destination); return text!.Length;
            }
            finally { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Settled); }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default) => throw new InvalidOperationException("Input reader acquired output authority.");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class NoUi : IExtensionUiProvider
    {
        public IExtensionUiScope OpenScope(IExtensionContext context) => new Scope();
        private sealed class Scope : IExtensionUiScope
        {
            public ExtensionUiCapabilities Capabilities => ExtensionUiCapabilities.NoUi;
            private static ValueTask<ExtensionUiOutcome<T>> No<T>() => ValueTask.FromResult(ExtensionUiOutcome<T>.Unavailable(ExtensionUiUnavailableReason.NoUi));
            public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken token = default) => No<string>();
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken token = default) => No<bool>();
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken token = default) => No<string>();
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken token = default) => No<string>();
            public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken token = default) => No<ExtensionUiPublication>();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
