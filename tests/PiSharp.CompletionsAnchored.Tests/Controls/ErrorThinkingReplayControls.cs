using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

// AUTHORED SYNTHETIC CONTROLS, UNEXECUTED. This source is excluded from the accepted
// anchored fixture runner. Root may compile it in a separate qualification host.
public static class ErrorThinkingReplayControls
{
    public static List<object> Evidence { get; } = [];
    public static int CompletedScenarios { get; private set; }
    public static async Task RunAsync()
    {
        Evidence.Clear(); CompletedScenarios = 0;
        await FaultAsync(cancel: false);
        CompletedScenarios++;
        await FaultAsync(cancel: true);
        CompletedScenarios++;
        await BoundedAsync();
        CompletedScenarios++;
        await SuccessAsync();
        CompletedScenarios++;
    }

    private static readonly ModelDescriptor Model = new("synthetic", "openai-completions", "openai");
    private static ChatRequest Request() => new(Model, ImmutableArray<TranscriptEntry>.Empty, 123);
    private const string DetailChunk = "{\"id\":\"replay\",\"choices\":[{\"delta\":{\"reasoning_content\":\"visible\",\"reasoning_details\":[{\"type\":\"reasoning.summary\",\"summary\":\"first\"},{\"type\":\"reasoning.summary\",\"summary\":\" second\",\"id\":\"summary-id\"},{\"type\":\"reasoning.encrypted\",\"data\":\"opaque\"}]}}]}";
    private const string ExpectedSignature = "[{\"type\":\"reasoning.summary\",\"summary\":\"first second\",\"id\":\"summary-id\"},{\"type\":\"reasoning.encrypted\",\"data\":\"opaque\"}]";

    private static async Task FaultAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var source = new HeldSource(DetailChunk);
        var mapper = new OpenAICompletionsWireSource((_, _) => source,
            new() { CaptureSourceEmissionSnapshots = true });
        var frames = new List<StreamEvent>();
        var read = CollectAsync(mapper.StreamAsync(Request(), cancellation.Token), frames);
        var settlement = new HeldReadSettlement(source, read);
        Exception? bodyFault = null;
        try
        {
        await AdmitHeldReadAsync(source, read);
        var original = new IOException("authored original late source fault");
        if (cancel)
        {
            cancellation.Cancel();
            source.Pending.TrySetCanceled(cancellation.Token);
        }
        else source.Pending.TrySetException(original);
        await read;
        var terminal = frames.OfType<StreamTerminalEvent>().Single();
        Require(terminal is StreamError && terminal.Reason == (cancel ? StopReason.Aborted : StopReason.Error), "fault terminal reason");
        Require(terminal.NativeDiagnostic?.Code == (cancel ? NativeChatFailureCode.Cancelled : NativeChatFailureCode.SourceFailed), "fault diagnostic");
        Require(!frames.Any(frame => frame is ThinkingEnded), "failure published no successful thinking end");
        Require(source.CloseCount == 1, "owned enumerator closed once");
        Require(cancel ? source.Pending.Task.IsCanceled : source.Pending.Task.IsFaulted, "actual pending read task status");
        if (!cancel) Require(ReferenceEquals(source.Pending.Task.Exception!.InnerException, original), "raw original exception retained in source evidence");
        Require(Signature(terminal.Message) == ExpectedSignature, "terminal replay details");
        Require(((ThinkingContent)terminal.Message.Content.Single()).Thinking == "visible", "visible thinking retained");
        var captured = CompletionsSourceEventProjection.ReadEmission(terminal)!.Value;
        Require(captured.GetProperty("value").GetProperty("error").GetProperty("content")[0].GetProperty("thinkingSignature").GetString() == ExpectedSignature,
            "owned error emission replay details");
        Evidence.Add(new { scenario = cancel ? "actual-cancellation" : "late-source-fault", pendingOriginalTaskStatus = source.Pending.Task.Status.ToString(), readOriginalTaskStatus = read.Status.ToString(),
            pendingOriginalFaultGraph = source.Pending.Task.Exception is { } fault ? BuildFaultGraph(fault) : null, originalFaultIdentityMatched = !cancel && ReferenceEquals(source.Pending.Task.Exception!.InnerException, original),
            cancellationRequested = cancellation.IsCancellationRequested, closeCount = source.CloseCount, thinkingEndCount = frames.OfType<ThinkingEnded>().Count(),
            terminalReason = terminal.Reason.ToString(), terminalDiagnostic = terminal.NativeDiagnostic?.Code.ToString(), actualTerminalMessage = PiWireJson.WriteMessage(terminal.Message).Value.Clone(),
            actualOwnedErrorEmission = captured.Clone() });
        }
        catch (Exception error) { bodyFault = error; }
        finally { await settlement.SettleAsync(bodyFault); }
    }

    private static async Task BoundedAsync()
    {
        var chunk = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { reasoning_details = new[] { new { type = "reasoning.summary", summary = new string('\n', 40) } } } } } });
        var source = new HeldSource(chunk);
        var mapper = new OpenAICompletionsWireSource((_, _) => source, new(MaximumContentCharacters: 256));
        var frames = new List<StreamEvent>();
        var read = CollectAsync(mapper.StreamAsync(Request()), frames);
        var settlement = new HeldReadSettlement(source, read);
        Exception? bodyFault = null;
        try
        {
        await AdmitHeldReadAsync(source, read);
        source.Pending.TrySetException(new IOException("bounded original fault"));
        await read;
        var terminal = frames.OfType<StreamTerminalEvent>().Single();
        Require(terminal is StreamError && terminal.NativeDiagnostic?.Code == NativeChatFailureCode.SourceFailed, "overlay exhaustion preserves original failure");
        Require(Signature(terminal.Message) == "", "oversized replay overlay not admitted");
        Require(source.CloseCount == 1 && !frames.Any(frame => frame is ThinkingEnded), "bounded failure closes without thinking end");
        Evidence.Add(new { scenario = "bounded-overlay", pendingOriginalTaskStatus = source.Pending.Task.Status.ToString(), readOriginalTaskStatus = read.Status.ToString(), pendingOriginalFaultGraph = source.Pending.Task.Exception is { } fault ? BuildFaultGraph(fault) : null,
            maximumContentCharacters = 256, closeCount = source.CloseCount, thinkingEndCount = frames.OfType<ThinkingEnded>().Count(), actualTerminalMessage = PiWireJson.WriteMessage(terminal.Message).Value.Clone() });
        }
        catch (Exception error) { bodyFault = error; }
        finally { await settlement.SettleAsync(bodyFault); }
    }

    private static async Task SuccessAsync()
    {
        var source = new HeldSource(DetailChunk, successful: true);
        var mapper = new OpenAICompletionsWireSource((_, _) => source);
        var frames = new List<StreamEvent>();
        await CollectAsync(mapper.StreamAsync(Request()), frames);
        var terminal = frames.OfType<StreamTerminalEvent>().Single();
        Require(terminal is StreamDone && Signature(terminal.Message) == ExpectedSignature, "successful replay unchanged");
        Require(frames.OfType<ThinkingEnded>().Count() == 1 && source.CloseCount == 1, "success end and ownership unchanged");
        Evidence.Add(new { scenario = "success-regression", closeCount = source.CloseCount, thinkingEndCount = frames.OfType<ThinkingEnded>().Count(), actualTerminalMessage = PiWireJson.WriteMessage(terminal.Message).Value.Clone() });
    }

    private static string? Signature(AssistantMessage message) => PiWireJson.WriteMessage(message).Value.GetProperty("content")[0].GetProperty("thinkingSignature").GetString();
    private static async Task CollectAsync(IAsyncEnumerable<StreamEvent> stream, List<StreamEvent> frames)
    { await foreach (var frame in stream) frames.Add(frame); }
    private static async Task AdmitHeldReadAsync(HeldSource source, Task original)
    {
        Exception? direct = null;
        try
        {
            var winner = await Task.WhenAny(source.Waiting.Task, original);
            if (ReferenceEquals(winner, original))
            {
                await original;
                throw new InvalidOperationException("Actual original completed before held source admission.");
            }
            await source.Waiting.Task;
        }
        catch (Exception error) { direct = error; }
        if (direct is null) return;
        source.Pending.TrySetResult(false);
        try { await original; } catch { }
        try { await source.Pending.Task; } catch { }
        if (original.Exception is { } originalFault)
            throw new AggregateException("Held admission and actual original fault.", originalFault, direct);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(direct).Throw();
    }

    public static object BuildFaultGraph(params Exception?[] roots)
    {
        var ids = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
        var queue = new List<Exception>();
        int Admit(Exception item)
        {
            if (ids.TryGetValue(item, out var found)) return found;
            if (queue.Count >= 1024) throw new InvalidOperationException("Fault graph node bound exceeded.");
            var id = queue.Count; ids.Add(item, id); queue.Add(item); return id;
        }
        var rootIds = roots.Select(item => item is null ? (int?)null : Admit(item)).ToArray();
        var nodes = new List<object>(); var edges = 0;
        for (var index = 0; index < queue.Count; index++)
        {
            var item = queue[index];
            var children = item is AggregateException aggregate ? aggregate.InnerExceptions.ToArray() : item.InnerException is { } inner ? new[] { inner } : Array.Empty<Exception>();
            var childIds = new List<int>();
            foreach (var child in children) { if (++edges > 4096) throw new InvalidOperationException("Fault graph edge bound exceeded."); childIds.Add(Admit(child)); }
            nodes.Add(new { id = index, type = item.GetType().AssemblyQualifiedName, item.Message, stack = item.StackTrace, item.HResult,
                aggregate = item is AggregateException, cancellationException = item is OperationCanceledException, inner = childIds });
        }
        return new { roots = rootIds, nodes, nodeCount = nodes.Count, edgeCount = edges, referenceIdentity = true };
    }
    private static void Require(bool condition, string label)
    { if (!condition) throw new InvalidOperationException(label); }

    private sealed class HeldReadSettlement(HeldSource source, Task original)
    {
        public async ValueTask SettleAsync(Exception? bodyFault)
        {
            // Finally owns release and joins even if admission or assertions fail.
            source.Pending.TrySetResult(false);
            Exception? direct = null;
            try { await original; } catch (Exception error) { direct = error; }
            try { await source.Pending.Task; } catch { } // Expected fault/cancel DTO is retained and checked above.
            var roots = new[] { bodyFault, original.Exception, direct, bodyFault is null ? null : source.Pending.Task.Exception }.OfType<Exception>().ToArray();
            if (roots.Length != 0) throw new AggregateException("Held body, read original and direct join faults retained.", roots);
        }
    }

    private sealed class HeldSource(string chunk, bool successful = false) : IAsyncEnumerable<JsonData>, IAsyncEnumerator<JsonData>
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CloseCount { get; private set; }
        private int _read;
        public JsonData Current { get; private set; } = JsonData.EmptyObject;
        public IAsyncEnumerator<JsonData> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            if (_read++ == 0) { Current = JsonData.Parse(chunk); return ValueTask.FromResult(true); }
            if (successful)
            {
                if (_read == 2) { Current = JsonData.Parse("{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}"); return ValueTask.FromResult(true); }
                return ValueTask.FromResult(false);
            }
            Waiting.TrySetResult();
            return new(Pending.Task);
        }
        public ValueTask DisposeAsync() { CloseCount++; return ValueTask.CompletedTask; }
    }
}
