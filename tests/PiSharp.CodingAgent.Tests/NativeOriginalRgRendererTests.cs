using System.Collections.Immutable;
using PiSharp.Cli.Interactive;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;
using PiSharp.Extensions.Events;
using PiSharp.Tui;

/// <summary>Root supplies an actually admitted launch. This fixture invents no Node executable,
/// source/package authority, worker generation or process grants. Unexecuted author-only controls.</summary>
internal static class NativeOriginalRgRendererTests
{
    internal sealed record CaseEvidence(string Name, string ComponentId, ImmutableArray<string> Rows,
        ImmutableArray<int> CellWidths, int PhysicalWrites, bool StaleDenied);

    internal static async Task<ImmutableArray<CaseEvidence>> RunAsync(NodeCommandInputWorkerLaunch admittedLaunch, string workspace)
    {
        var console = new ConsoleFixture(); var view = new TerminalSessionView(console, new Viewport());
        long nativeGeneration = 1;
        var input = new TerminalExtensionInputAdmission(1, () => nativeGeneration);
        var provider = new TerminalCustomComponentUiProvider(new UnavailableExtensionUiProvider(), view, input, () => nativeGeneration);
        var registry = new ExtensionRegistry(null, input.Decorate(provider));
        var node = new NodeCommandInputExtension(admittedLaunch, workspace, sourcePaths: [NodeTierAAdmission.TruncatedToolSource]);
        RegistrationScope? scope = null;
        Func<IExtensionContext, CancellationToken, Task>? hostCase = null;
        var originals = new List<Task>(); var errors = new List<Exception>(); var evidence = ImmutableArray.CreateBuilder<CaseEvidence>();
        var components = new List<NodeCommandInputExtension.RenderedComponent>();
        try
        {
            var start = view.StartAsync(CancellationToken.None).AsTask(); originals.Add(start); await start;
            var activation = registry.ActivateAsync("original-rg", new Extension(node, (context, token) =>
                (hostCase ?? throw new InvalidOperationException("Missing actual native case."))(context, token))); originals.Add(activation); scope = await activation;
            var descriptor = node.OriginalRenderers.Single();
            Check(descriptor.ToolName == "rg" && descriptor.SourcePath == NodeTierAAdmission.TruncatedToolSource && descriptor.HasRenderCall && descriptor.HasRenderResult);
            Check(node.SourceLoadReport!.Value.GetProperty("sourceFunctionsRemainInNode").GetBoolean());
            var dispatcher = new RegisteredExtensionEventDispatcher(registry, result => { });
            var call = dispatcher.DispatchToolCallAsync(registry.CaptureSnapshot(), new("rg", "native-rg-call",
                JsonData.Parse("{\"pattern\":\"needle\",\"path\":\"src\",\"glob\":\"*.cs\"}"))).AsTask();
            originals.Add(call); await call;
            var renderedCall = node.CaptureRenderedTool("native-rg-call", false) ?? throw new InvalidOperationException("Actual registered original renderer did not attach.");
            components.Add(renderedCall);
            await Inspect("rg.call.registered-native-renderer", renderedCall, "needle");
            await Close(renderedCall);

            foreach (var scenario in new[] { "partial", "no-match", "expanded" })
            {
                NodeCommandInputExtension.RenderedComponent? component = null;
                hostCase = async (context, token) =>
                {
                    var result = scenario == "expanded"
                        ? JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"src/a.cs:7:needle\\nsrc/b.cs:9:needle\"}],\"details\":{\"matchCount\":2}}")
                        : JsonData.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"\"}],\"details\":{\"matchCount\":0}}");
                    var options = JsonData.Parse(scenario == "partial" ? "{\"isPartial\":true,\"expanded\":false}" :
                        scenario == "expanded" ? "{\"isPartial\":false,\"expanded\":true}" : "{\"isPartial\":false,\"expanded\":false}");
                    var original = node.RenderToolResultAsync(descriptor.ToolCallbackId, "native-rg-" + scenario, result, options, JsonData.EmptyObject, context, token);
                    originals.Add(original); component = await original; components.Add(component);
                };
                var invocation = registry.InvokeCommandAsync(registry.CaptureSnapshot(), "native-render-case", JsonData.EmptyObject).AsTask();
                originals.Add(invocation); await invocation;
                await Inspect("rg.result." + scenario, component!, scenario == "partial" ? "Searching..." : scenario == "no-match" ? "No matches found" : "src/a.cs:7:needle");
                await Close(component!);
            }
            Check(node.ActiveContexts == 0 && console.Active == 0 && console.Started == console.Settled && !console.Disposed);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            foreach (var component in components) Acquire(() => component.DisposeAsync().AsTask());
            Acquire(() => input.StopAdmissionAndJoinAsync().AsTask());
            if (scope is not null) Acquire(() => scope.DisposeAsync().AsTask());
            Acquire(() => registry.DisposeAsync().AsTask());

            foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
                try { await original; } catch (Exception error) { errors.Add(original.Exception ?? error); }
            // The view remains usable until every physical-retirement and registry original settles.
            Task? viewCleanup = null;
            try { viewCleanup = view.DisposeAsync().AsTask(); await viewCleanup; }
            catch (Exception error) { errors.Add(viewCleanup?.Exception ?? error); }
            if (console.Active != 0 || console.Started != console.Settled || console.Disposed) errors.Add(new InvalidOperationException("Actual borrowed writer/cleanup original not joined."));
        }
        if (errors.Count != 0) throw new AggregateException("Actual original rg native fixture failed.", errors);
        return evidence.ToImmutable();

        void Acquire(Func<Task> factory) { try { originals.Add(factory()); } catch (Exception error) { errors.Add(error); } }
        async Task Inspect(string name, NodeCommandInputExtension.RenderedComponent component, string expectedText)
        {
            var original = component.RenderAsync(96); originals.Add(original); var projection = await original;
            Check(projection.Rows.Length > 0 && projection.Rows.Any(row => row.Contains(expectedText, StringComparison.Ordinal)));
            Check(projection.Rows.Length == projection.CellWidths.Length && projection.CellWidths.All(width => width is >= 0 and <= 96));
            Check(projection.Rows.All(row => console.Writes.Any(write => write.Contains(row + "\u001b[0m", StringComparison.Ordinal))));
            var before = node.SourceOperations.Length;
            var signal = component.InvalidateAsync(); originals.Add(signal); await signal;
            var paints = component.JoinPaintsAsync(); originals.Add(paints); await paints;
            Check(node.SourceOperations.Length > before && console.Active == 0 && console.Started == console.Settled);
            evidence.Add(new(name, component.ComponentId, projection.Rows, projection.CellWidths, console.Started, false));
        }
        async Task Close(NodeCommandInputExtension.RenderedComponent component)
        {
            var original = component.DisposeAsync().AsTask(); originals.Add(original); await original;
            bool denied = false;
            try { _ = component.InvalidateAsync(); } catch (InvalidOperationException) { denied = true; }
            Check(denied); var last = evidence.Count - 1; evidence[last] = evidence[last] with { StaleDenied = true };
        }
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Genuine original renderer/native view control failed."); }
    private sealed class Extension(NodeCommandInputExtension node, Func<IExtensionContext, CancellationToken, Task> hostCase) : IPiSharpExtension
    {
        public async ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        {
            await node.InitializeAsync(registry, token);
            registry.RegisterCommand(new("native-render-case", "native-render-case", "Root-owned pure native rendering fixture", async (_, context, original) => await hostCase(context, original)));
        }
        public ValueTask DisposeAsync() => node.DisposeAsync();
    }
    private sealed class Viewport : ITerminalViewportSource { public TerminalViewport ReadViewport() => new(96, 12, 0, 0, 96, 12); }
    private sealed class ConsoleFixture : IConsoleTerminal
    {
        internal readonly List<string> Writes = [];
        internal int Active, Started, Settled; internal bool Disposed;
        public TerminalLeaseSnapshot Snapshot => new(new(0, 0, 65001, 65001, 25, true, 0, 0), new(0, 0, 65001, 65001, 25, true, 0, 0), null, false, false, 0, Active, 0, 0, Started, Settled);
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw new InvalidOperationException("Renderer borrowed input authority.");
        public ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Check(Interlocked.Increment(ref Active) == 1); Interlocked.Increment(ref Started);
            try { Writes.Add(text.ToString()); return ValueTask.CompletedTask; }
            finally { Interlocked.Decrement(ref Active); Interlocked.Increment(ref Settled); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
