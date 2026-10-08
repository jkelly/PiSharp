using PiSharp.AI;
using PiSharp.Contracts;

internal static class ThinkingCheckpointTests
{
    private static readonly AssistantMessage Message = new("openai-responses", "openai", "synthetic", 1, [], TokenUsage.Zero, StopReason.Pending);
    private static JsonFields Signature(string text) => JsonFields.Empty.Set("thinkingSignature", JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(text)));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Thinking checkpoint invariant failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    public static Task Contract()
    {
        var reducer = new AssistantStreamReducer(Message);
        reducer.Apply(new StreamStarted(Message));
        reducer.Apply(new ThinkingStarted(0, new("")));
        reducer.Apply(new ThinkingDelta(0, "provisional"));
        var frame = new ThinkingCheckpoint(0, "authoritative", Signature("pending"));
        var roundtrip = PiWireJson.ReadEvent(PiWireJson.WriteEvent(frame).Value);
        Check(roundtrip is ThinkingCheckpoint);
        reducer.Apply(roundtrip);
        var checkpoint = reducer.Snapshot();
        Check(((ThinkingContent)checkpoint.Content[0]).Thinking == "authoritative");
        Check(((ThinkingContent)checkpoint.Content[0]).ExtraProperties!.Values["thinkingSignature"].Value.GetString() == "pending");
        Reject<StreamProtocolException>(() => reducer.Apply(new StreamDone(StopReason.Stop, checkpoint with { StopReason = StopReason.Stop })));
        var aborted = reducer.Failure(new(ChatFailureKind.Cancelled, "cancelled"));
        Check(((ThinkingContent)aborted.Message.Content[0]).Thinking == "authoritative");
        reducer.Apply(new ThinkingEnded(0, "authoritative", Signature("backfilled")));
        Check(((ThinkingContent)checkpoint.Content[0]).ExtraProperties!.Values["thinkingSignature"].Value.GetString() == "pending");
        Reject<StreamProtocolException>(() => reducer.Apply(frame));
        var final = reducer.Snapshot() with { StopReason = StopReason.Stop };
        reducer.Apply(new StreamDone(StopReason.Stop, final));
        Reject<StreamProtocolException>(() => reducer.Apply(frame));
        return Task.CompletedTask;
    }
    public static Task BoundsAndKinds()
    {
        var reducer = new AssistantStreamReducer(Message, new(MaximumCharacters: 128));
        Reject<StreamProtocolException>(() => reducer.Apply(new ThinkingCheckpoint(0, "x")));
        reducer.Apply(new StreamStarted(Message));
        reducer.Apply(new TextStarted(0, new("")));
        Reject<StreamProtocolException>(() => reducer.Apply(new ThinkingCheckpoint(0, "x")));
        reducer.Apply(new ThinkingStarted(1, new("")));
        Reject<StreamProtocolException>(() => reducer.Apply(new ThinkingCheckpoint(2, "x")));
        Reject<StreamLimitException>(() => reducer.Apply(new ThinkingCheckpoint(1, new string('x', 129))));
        Reject<StreamLimitException>(() => reducer.Apply(new ThinkingCheckpoint(1, "", Signature(new string('x', 129)))));
        Check(((ThinkingContent)reducer.Snapshot().Content[1]).Thinking == "");
        reducer.Apply(new ThinkingCheckpoint(1, "longer", Signature("first")));
        reducer.Apply(new ThinkingCheckpoint(1, "x"));
        var content = (ThinkingContent)reducer.Snapshot().Content[1];
        Check(content.Thinking == "x" && !content.ExtraProperties!.Values.ContainsKey("thinkingSignature"));
        return Task.CompletedTask;
    }
    public static async Task CancellationAndCleanup()
    {
        foreach (var api in new[] { "openai-responses", "openai-completions", "anthropic-messages", "google-generative-ai", "pi-messages" })
        foreach (var checkpointBeforeCancel in new[] { false, true })
        {
            var message = Message with { Api = api };
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = false;
            async IAsyncEnumerable<StreamEvent> Events()
            {
                try
                {
                    yield return new StreamStarted(message);
                    yield return new ThinkingStarted(0, new("initial"));
                    if (checkpointBeforeCancel) yield return new ThinkingCheckpoint(0, "authoritative", Signature("pending"));
                    entered.TrySetResult();
                    await release.Task;
                    // Deliberately ignore cancellation: ChatRun must still reject this.
                    yield return new ThinkingCheckpoint(0, "too late", Signature("rejected"));
                }
                finally
                {
                    cleanupEntered.TrySetResult();
                    await cleanupRelease.Task;
                    disposed = true;
                }
            }
            var request = new ChatRequest(new(message.Model, message.Api, message.Provider), [], message.Timestamp);
            var task = new ChatClient(new Transport(Events), capacity: 1).CompleteAsync(request, cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel(); release.TrySetResult();
                await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!task.IsCompleted && !disposed);
                cleanupRelease.TrySetResult();
                var result = await task;
                Check(disposed && result.Failure?.Kind == ChatFailureKind.Cancelled && result.Message.StopReason == StopReason.Aborted);
                var thinking = (ThinkingContent)result.Message.Content.Single();
                Check(thinking.Thinking == (checkpointBeforeCancel ? "authoritative" : "initial"));
                if (checkpointBeforeCancel) Check(thinking.ExtraProperties!.Values["thinkingSignature"].Value.GetString() == "pending");
            }
            finally { release.TrySetResult(); cleanupRelease.TrySetResult(); await task; }
        }
    }
    private sealed class Transport(Func<IAsyncEnumerable<StreamEvent>> events) : IChatTransport
    {
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) => events();
    }
}