using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

// Source-derived terminal behavior and bounded unfinished-tool/thinking distinctions.
internal static class LengthControls
{
    private static readonly ChatRequest Request = new(new("fixture", "openai-responses", "openai"), []);
    private const string Added = "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"m\"}}";
    private const string Delta = "{\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"partial\"}";
    private const string Length = "{\"type\":\"response.incomplete\",\"response\":{\"id\":\"r\",\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"usage\":{\"input_tokens\":12,\"output_tokens\":3,\"total_tokens\":15}}}";
    private static void Check(bool value, [CallerLineNumber] int line = 0)
    { if (!value) throw new InvalidOperationException($"Responses Length assertion at line {line}."); }
    private static string Text(AssistantMessage value) => string.Concat(value.Content.OfType<TextContent>().Select(part => part.Text));

    internal static Task Reducer()
    {
        // Isolate shared reducer admission from Responses transport finalization.
        var fallback = new AssistantMessage("openai-responses", "openai", "fixture", 0, [], TokenUsage.Zero, StopReason.Pending);
        var reducer = new AssistantStreamReducer(fallback);
        reducer.Apply(new StreamStarted(fallback)); reducer.Apply(new TextStarted(0, new("")));
        reducer.Apply(new TextDelta(0, "partial"));
        var terminal = new StreamDone(StopReason.Length, reducer.Snapshot() with { StopReason = StopReason.Length });
        reducer.Apply(terminal);
        Check(reducer.IsTerminal && Text(reducer.Snapshot()) == "partial");
        return Task.CompletedTask;
    }

    internal static async Task PartialText()
    {
        // Prevent the regression from surviving only in the downstream ChatRun layer.
        var (events, result) = await Run(new ResponsesTextToolTransport((_, token) => Source([Added, Delta, Length], token)));
        Check(events[^1] is StreamDone { Reason: StopReason.Length } && result.Failure is null);
        Check(!events.OfType<TextEnded>().Any() && Text(result.Message) == "partial");
        Check(result.Message.Usage.Input == 12 && result.Message.Usage.Output == 3 && result.Message.Usage.TotalTokens == 15);
        Check(result.Message.ExtraProperties!.TryGet("rawStopReason", out var raw) && raw!.Value.GetString() == "incomplete.max_output_tokens");
        Check(((StreamTerminalEvent)events[^1]).NativeSourceException is null);
    }

    internal static async Task UnfinishedKinds()
    {
        foreach (var added in new[] {
            "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"id\":\"f\",\"call_id\":\"c\",\"name\":\"tool\"}}",
            "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"reasoning\",\"id\":\"thinking\"}}" })
        {
            var (events, result) = await Run(new ResponsesTextToolTransport((_, token) => Source([Added, Delta, added, Length], token)));
            Check(events[^1] is StreamError { Reason: StopReason.Error } && result.Failure is not null && Text(result.Message) == "partial");
            Check(!events.OfType<ToolCallEnded>().Any() && !events.OfType<ThinkingEnded>().Any());
        }
        var completed = "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}";
        var (frames, completion) = await Run(new ResponsesTextToolTransport((_, token) => Source([Added, Delta, completed], token)));
        Check(frames[^1] is StreamError && completion.Failure is not null && Text(completion.Message) == "partial");
        // Shared admission remains constrained to Responses Length text; it must not weaken other APIs/reasons.
        foreach (var (api, reason, tool) in new[] { ("openai-completions", StopReason.Length, false),
            ("openai-responses", StopReason.Stop, false), ("openai-responses", StopReason.Length, true) })
        {
            var start = new AssistantMessage(api, "openai", "fixture", 0, [], TokenUsage.Zero, StopReason.Pending);
            var reducer = new AssistantStreamReducer(start); reducer.Apply(new StreamStarted(start));
            if (tool) reducer.Apply(new ToolCallStarted(0, new("call", "tool", JsonData.EmptyObject)));
            else reducer.Apply(new TextStarted(0, new("partial")));
            var rejected = false;
            try { reducer.Apply(new StreamDone(reason, reducer.Snapshot() with { StopReason = reason })); }
            catch (StreamProtocolException) { rejected = true; }
            Check(rejected);
        }
    }

    internal static async Task Errors()
    {
        var failed = "{\"type\":\"response.failed\",\"response\":{\"status\":\"failed\",\"error\":{\"code\":\"backend\",\"message\":\"genuine failure\"}}}";
        var (frames, result) = await Run(new ResponsesTextToolTransport((_, token) => Source([Added, Delta, failed], token)));
        Check(frames[^1] is StreamError { Reason: StopReason.Error } && result.Failure is not null && Text(result.Message) == "partial");
        var supplied = new InvalidOperationException("genuine read failure after incomplete");
        var (faultFrames, faultResult) = await Run(new ResponsesTextToolTransport((_, token) => Source([Added, Delta, Length], token, supplied)));
        await JoinObservedSource((StreamTerminalEvent)faultFrames[^1]);
        Check(faultFrames[^1] is StreamError { Reason: StopReason.Error } error && ReferenceEquals(error.NativeSourceException, supplied));
        Check(faultResult.Failure is not null && Text(faultResult.Message) == "partial" && faultResult.Message.Usage.TotalTokens == 15);
    }

    internal static async Task CleanupFailure()
    {
        var supplied = new InvalidOperationException("genuine disposal failure after incomplete");
        var original = Task.FromException(supplied);
        var record = new OriginalTaskRecord("length-expected-cleanup-fault", original); Controls.Originals.Add(record);
        var source = new CleanupSource(original);
        var wrapper = new OriginalTaskRecord("length-expected-cleanup-wrapper-fault"); Controls.Originals.Add(wrapper);
        try
        {
            var (frames, result) = await Run(new ResponsesTextToolTransport((_, _) => source));
            Check(frames[^1] is StreamError { Reason: StopReason.Error } failure &&
                failure.NativeSourceException is null && failure.NativeCleanupExceptions is { Count: 1 } cleanup && ReferenceEquals(cleanup[0], supplied) &&
                failure.NativeCleanupTasks is { Count: 1 } tasks && ReferenceEquals(tasks[0], source.ActualDisposalTask));
            Check(result.Failure is not null && Text(result.Message) == "partial" && result.Message.Usage.TotalTokens == 15);
        }
        finally
        {
            // Join the deliberately faulted original even if Run or an assertion fails.
            if (source.ActualDisposalTask is { } actual)
            {
                wrapper.Original = actual;
                try { await actual; } catch (Exception error) { wrapper.Direct = error; } finally { wrapper.Capture(); }
            }
            try { await original; } catch (Exception error) { record.Direct = error; } finally { record.Capture(); }
        }
        Check(record.FullException is not null && ReferenceEquals(record.Direct, supplied));
    }

    private sealed class CleanupSource(Task disposal) : IAsyncEnumerable<JsonData>
    {
        internal Task? ActualDisposalTask { get; private set; }
        public IAsyncEnumerator<JsonData> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(Source([Added, Delta, Length], cancellationToken).GetAsyncEnumerator(cancellationToken), this, disposal);
        private sealed class Enumerator(IAsyncEnumerator<JsonData> inner, CleanupSource owner, Task disposal) : IAsyncEnumerator<JsonData>
        {
            public JsonData Current => inner.Current;
            public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
            public ValueTask DisposeAsync()
            {
                var original = DisposeCore(); owner.ActualDisposalTask = original;
                return new ValueTask(original);
            }
            private async Task DisposeCore()
            {
                // The wrapper owns and joins its underlying iterator before its deliberate disposal failure.
                await inner.DisposeAsync();
                await disposal;
            }
        }
    }

    internal static async Task Abort()
    {
        using var cancellation = new CancellationTokenSource();
        var (frames, result) = await Run(new ResponsesTextToolTransport((_, token) => Source([Added, Delta], token, cancel: cancellation.Cancel)), cancellation.Token);
        await JoinObservedSource((StreamTerminalEvent)frames[^1]);
        Check(frames[^1] is StreamError { Reason: StopReason.Aborted } error && error.NativeSourceException is OperationCanceledException);
        Check(result.Message.StopReason == StopReason.Aborted && Text(result.Message) == "partial" && result.Failure?.Kind == ChatFailureKind.Cancelled);
        Check(!frames.OfType<TextEnded>().Any());
    }

    private static async Task JoinObservedSource(StreamTerminalEvent terminal)
    {
        var original = terminal.NativeSourceTask ?? throw new InvalidOperationException("Missing original source task.");
        var record = new OriginalTaskRecord("length-expected-source-fault-or-cancel", original); Controls.Originals.Add(record);
        try { await original; } catch (Exception error) { record.Direct = error; } finally { record.Capture(); }
        Check(original.IsFaulted || original.IsCanceled);
    }

    private static async IAsyncEnumerable<JsonData> Source(string[] frames, [EnumeratorCancellation] CancellationToken token,
        Exception? failure = null, Action? cancel = null)
    {
        await Task.CompletedTask;
        foreach (var frame in frames) { token.ThrowIfCancellationRequested(); yield return JsonData.Parse(frame); }
        cancel?.Invoke(); token.ThrowIfCancellationRequested();
        if (failure is not null) throw failure;
    }

    private static async Task<(List<StreamEvent>, ChatResult)> Run(IChatTransport transport, CancellationToken token = default)
    {
        var run = await new ChatClient(transport, capacity: 2).StartWithAbortSettlementAsync(Request, token);
        // Retain actual originals before assertions, and join completion/disposal even if reading fails.
        var completion = new OriginalTaskRecord("length-run-completion", run.Completion); Controls.Originals.Add(completion);
        var reader = new OriginalTaskRecord("length-run-reader"); Controls.Originals.Add(reader);
        var disposal = new OriginalTaskRecord("length-run-disposal"); Controls.Originals.Add(disposal);
        var events = new List<StreamEvent>(); ChatResult? result = null;
        async Task Read() { await foreach (var frame in run.ReadEventsAsync()) events.Add(frame); }
        try { var original = Read(); reader.Original = original; await original; }
        catch (Exception error) { reader.Direct = error; }
        finally
        {
            reader.Capture();
            try { result = await run.Completion; } catch (Exception error) { completion.Direct = error; } finally { completion.Capture(); }
            try { var original = run.DisposeAsync().AsTask(); disposal.Original = original; await original; }
            catch (Exception error) { disposal.Direct = error; } finally { disposal.Capture(); }
        }
        var faults = new[] { reader, completion, disposal }.SelectMany(record => record.Faults()).ToArray();
        if (faults.Length != 0) throw new AggregateException("Length run originals failed after all joins.", faults);
        return (events, result ?? throw new InvalidOperationException("Missing Length run completion."));
    }
}
