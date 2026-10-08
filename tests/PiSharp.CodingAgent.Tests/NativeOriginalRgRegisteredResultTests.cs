using System.Collections.Immutable;
using PiSharp.Cli.Interactive;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;
using PiSharp.Tui;

// The caller supplies an already admitted actual launch. This adds no process/source authority
// and invokes only the unchanged original source factory and renderers, never rg.execute.
internal static class NativeOriginalRgRegisteredResultTests
{
    internal sealed record Evidence(string CallComponentId, string ResultComponentId,
        ImmutableArray<string> ResultRows, ImmutableArray<int> ResultCellWidths, int PhysicalWrites);
    internal static async Task<Evidence> RunAsync(NodeCommandInputWorkerLaunch admittedLaunch, string workspace)
    {
        var console = new PhysicalConsole(); var view = new TerminalSessionView(console, new Viewport());
        var input = new TerminalExtensionInputAdmission(1, () => 1);
        var provider = new TerminalCustomComponentUiProvider(new UnavailableExtensionUiProvider(), view, input, () => 1);
        var registry = new ExtensionRegistry(null, input.Decorate(provider));
        var node = new NodeCommandInputExtension(admittedLaunch, workspace, sourcePaths: [NodeTierAAdmission.TruncatedToolSource]);
        RegistrationScope? scope = null; NodeCommandInputExtension.RenderedComponent? callComponent = null, resultComponent = null;
        var originals = new List<Task>(); var failures = new List<Exception>(); Evidence? evidence = null;
        try
        {
            var start = view.StartAsync(CancellationToken.None).AsTask(); originals.Add(start); await start;
            var activation = registry.ActivateAsync("registered-original-rg", node); originals.Add(activation); scope = await activation;
            var descriptor = node.OriginalRenderers.Single();
            Check(descriptor.ToolName == "rg" && descriptor.SourcePath == NodeTierAAdmission.TruncatedToolSource &&
                descriptor.HasRenderCall && descriptor.HasRenderResult && node.SourceLoadReport!.Value.GetProperty("sourceFunctionsRemainInNode").GetBoolean());
            var arguments = JsonData.Parse("{\"pattern\":\"needle\",\"path\":\"src\",\"glob\":\"*.cs\"}");
            var result = JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"src/a.cs:7:needle\\nsrc/b.cs:9:needle\"}],\"details\":{\"matchCount\":2}}");
            var dispatcher = new RegisteredExtensionEventDispatcher(registry, _ => { });
            var call = dispatcher.DispatchToolCallAsync(registry.CaptureSnapshot(), new("rg", "registered-rg", arguments)).AsTask();
            originals.Add(call); await call;
            callComponent = node.CaptureRenderedTool("registered-rg", false); Check(callComponent is not null);
            var callRows = callComponent!.RenderAsync(96); originals.Add(callRows); var originalCall = await callRows;
            Check(originalCall.Rows.Any(row => row.Contains("needle", StringComparison.Ordinal)) &&
                originalCall.Rows.All(row => console.Writes.Any(write => write.Contains(row + "\u001b[0m", StringComparison.Ordinal))));
            var dispatchResult = dispatcher.DispatchToolResultAsync(registry.CaptureSnapshot(), new("rg", "registered-rg", arguments, result)).AsTask();
            originals.Add(dispatchResult); await dispatchResult;
            resultComponent = node.CaptureRenderedTool("registered-rg", true); Check(resultComponent is not null);
            var rendered = resultComponent!.RenderAsync(96); originals.Add(rendered); var projection = await rendered;
            Check(projection.Rows.Any(row => row.Contains("2 matches", StringComparison.Ordinal)) &&
                projection.Rows.Length == projection.CellWidths.Length && projection.CellWidths.All(width => width is >= 0 and <= 96));
            Check(projection.Rows.All(row => console.Writes.Any(write => write.Contains(row + "\u001b[0m", StringComparison.Ordinal))));
            var before = node.SourceOperations.Length;
            var signal = resultComponent.InvalidateAsync(); originals.Add(signal); await signal;
            var paints = resultComponent.JoinPaintsAsync(); originals.Add(paints); await paints;
            Check(node.SourceOperations.Length > before && node.ActiveContexts == 0 && console.Active == 0 && console.Started == console.Settled);
            var closeResult = resultComponent.DisposeAsync().AsTask(); originals.Add(closeResult); await closeResult;
            Check(node.CaptureRenderedTool("registered-rg", true) is null);
            var closeCall = callComponent.DisposeAsync().AsTask(); originals.Add(closeCall); await closeCall;
            Check(node.CaptureRenderedTool("registered-rg", false) is null);
            bool staleRefused = false; try { _ = resultComponent.InvalidateAsync(); } catch (InvalidOperationException) { staleRefused = true; }
            Check(staleRefused); evidence = new(callComponent.ComponentId, resultComponent.ComponentId, projection.Rows, projection.CellWidths, console.Started);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (resultComponent is not null) Acquire(() => resultComponent.DisposeAsync().AsTask());
            if (callComponent is not null) Acquire(() => callComponent.DisposeAsync().AsTask());
            Acquire(() => input.StopAdmissionAndJoinAsync().AsTask());
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask());
            Acquire(() => registry.DisposeAsync().AsTask());
            foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
                try { await original; } catch (Exception error) { failures.Add(original.Exception ?? error); }
            Task? viewCleanup = null;
            try { viewCleanup = view.DisposeAsync().AsTask(); await viewCleanup; }
            catch (Exception error) { failures.Add(viewCleanup?.Exception ?? error); }
            if (console.Active != 0 || console.Started != console.Settled || console.Disposed)
                failures.Add(new InvalidOperationException("Registered renderer physical originals were abandoned or disposed."));
        }
        if (failures.Count != 0) throw new AggregateException("Actual registered original rg rendering failed.", failures);
        return evidence ?? throw new InvalidOperationException("Registered renderer evidence absent.");
        void Acquire(Func<Task> acquire) { try { originals.Add(acquire()); } catch (Exception error) { failures.Add(error); } }
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Genuine registered renderer/native physical route control failed."); }
    private sealed class Viewport : ITerminalViewportSource { public TerminalViewport ReadViewport() => new(96, 12, 0, 0, 96, 12); }
    private sealed class PhysicalConsole : IConsoleTerminal
    {
        internal readonly List<string> Writes = []; internal int Active, Started, Settled; internal bool Disposed;
        public TerminalLeaseSnapshot Snapshot => new(new(0, 0, 65001, 65001, 25, true, 0, 0), new(0, 0, 65001, 65001, 25, true, 0, 0), null, false, false, 0, Active, 0, 0, Started, Settled);
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw new InvalidOperationException("No terminal input acquisition.");
        public ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Check(Interlocked.Increment(ref Active) == 1); Interlocked.Increment(ref Started);
            try { Writes.Add(text.ToString()); return ValueTask.CompletedTask; }
            finally { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Settled); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
