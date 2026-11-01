using System.Text.Json.Nodes;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Server;

// Decision 0011 (WU1): AgentFrames.ReadAsync, the agent stream's frames without HTTP. An in-process reader and the device API's agent stream
// watch the same agent: their frames match, and both reduce to the server's transcript.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> AgentFramesCases() =>
    [
        Case("agent-frames.match-what-the-http-stream-sends", async () =>
        {
            await using var server = await TestServer.StartAsync("frames-match");
            var id = await server.CreateSessionAsync("Frames");
            var session = server.Host.ResolveSession(id);
            var main = $"/v1/sessions/{id}/agents/main";
            var remote = new AgentModel();
            await using var http = new FrameLog(server.Client, main + "/events?docs=pisharp.job-runs", frame => remote.Apply(frame));
            await WaitUntilAsync(() => remote.Ready, "the HTTP snapshot");
            var local = new AgentModel();
            await using var frames = new LocalFrames(session, 1, new AgentFrameOptions { Docs = ["pisharp.job-runs"] }, frame => local.Apply(frame));
            await WaitUntilAsync(() => local.Ready, "the in-process snapshot");

            await server.Post(main + "/messages", Message("""tool: read_state {"path":"/counter"}""", "fm-0"));
            await server.Post(main + "/messages", Message("slow: " + string.Join(' ', Enumerable.Repeat("matching frames", 15)), "fm-1"));
            static bool Done(AgentModel model) => !model.Running && model.Items.OfType<AssistantItem>().Count(item => !item.Streaming) >= 2;
            static List<JsonObject> Events(IEnumerable<JsonObject> all) => [.. all.Where(frame => Type(frame) is "batch")];
            await WaitUntilAsync(() => Done(remote) && Done(local) && Events(http.Frames).Count == Events(frames.Frames).Count, "both viewers caught up");

            var sent = http.Frames.Skip(1).ToList();
            var read = frames.Frames;
            Equal("hello", Type(http.Frames[0]), "only HTTP says hello");
            Equal(sent[0].ToJsonString(), read[0].ToJsonString(), "the same snapshot frame");
            Equal(string.Join("\n", Events(sent).Select(frame => frame.ToJsonString())), string.Join("\n", Events(read).Select(frame => frame.ToJsonString())),
                "the same batch frames, in the same order");
            Equal(string.Join(",", sent.Select(Type)), string.Join(",", read.Select(Type)), "the same frame types, in the same order");
            List<string> Docs(IEnumerable<JsonObject> all) => [.. all.Where(frame => Type(frame) == "doc").Select(frame => frame.ToJsonString())];
            Equal(1, Docs(read).Count, "an unchanged document is sent once");
            Equal(Docs(sent).Single(), Docs(read).Single(), "the same document frame");
            Equal(sent.Last(frame => Type(frame) == "inbox").ToJsonString(), read.Last(frame => Type(frame) == "inbox").ToJsonString(), "the same final inbox");

            var transcript = (await server.Get(main + "/transcript"))!["items"]!.ToJsonString();
            Equal(transcript, Transcript.ToJson(local.Items).ToJsonString(), "the in-process frames reduce to the server's transcript");
            Equal(transcript, Transcript.ToJson(remote.Items).ToJsonString(), "so do the HTTP frames");
            var stored = Transcript.Build(await AgentFrames.ActiveEntriesAsync(session, 1));
            Equal(transcript, Transcript.ToJson(stored).ToJsonString(), "the stored entries render the same transcript");
        }),

        Case("agent-frames.errors-inbox-and-stopping-a-reader", async () =>
        {
            await using var server = await TestServer.StartAsync("frames-stop");
            var id = await server.CreateSessionAsync("Stopping");
            var session = server.Host.ResolveSession(id);
            var main = $"/v1/sessions/{id}/agents/main";
            Throws<HostInvalidException>(() => AgentFrames.ReadAsync(session, 1, new AgentFrameOptions { Docs = ["nope"] }), "an unknown document fails the call");
            await using (var missing = AgentFrames.ReadAsync(session, 99).GetAsyncEnumerator())
                await ThrowsAsync<HostNotFoundException>(async () => await missing.MoveNextAsync(), "an unknown agent fails the first frame");

            var model = new AgentModel();
            var reader = AgentFrames.ReadAsync(session, 1).GetAsyncEnumerator();
            Check(await reader.MoveNextAsync(), "a first frame");
            Equal("snapshot", Type(reader.Current), "the first frame is the snapshot");
            model.Apply(reader.Current);
            await server.Post(main + "/messages", Message("slow: " + string.Join(' ', Enumerable.Repeat("word", 40)), "fs-1"));
            while (!model.Running && await reader.MoveNextAsync()) model.Apply(reader.Current);
            Check(model.Running, "the reader saw the run start");
            await server.Post(main + "/messages", Message("queued behind it", "fs-2"));
            var inbox = await AgentFrames.InboxAsync(session, 1);
            Check(inbox.Any(item => item.Content == "queued behind it" && item.Mode == "followUp"), "the queued message with its text");
            await reader.DisposeAsync();

            var answer = await server.Post(main + "/messages", Message("queued behind it", "fs-2", waitMs: 15000));
            Equal("Echo: queued behind it", AnswerText(answer), "the agent kept working after the reader stopped");

            using var stop = new CancellationTokenSource();
            var count = 0;
            var reading = Task.Run(async () =>
            {
                await foreach (var _ in AgentFrames.ReadAsync(session, 1, cancellationToken: stop.Token)) Interlocked.Increment(ref count);
            });
            await WaitUntilAsync(() => Volatile.Read(ref count) > 0, "the snapshot");
            await stop.CancelAsync();
            try { await reading.WaitAsync(Patience); }
            catch (OperationCanceledException) { /* Cancelled while waiting for a frame. */ }
            Check(reading.IsCompleted, "cancellation ends the frames");
        }),
    ];

    /// <summary>An in-process reader of <see cref="AgentFrames.ReadAsync"/>, collected in the background until disposed.</summary>
    private sealed class LocalFrames : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly List<JsonObject> frames = [];
        private readonly Task reading;

        public LocalFrames(HostedSession session, long agentId, AgentFrameOptions options, Action<JsonObject> onFrame)
        {
            reading = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in AgentFrames.ReadAsync(session, agentId, options, stop.Token))
                        lock (frames)
                        {
                            frames.Add(frame);
                            onFrame(frame);
                        }
                }
                catch (OperationCanceledException) { /* Stopped. */ }
            });
        }

        public IReadOnlyList<JsonObject> Frames
        {
            get { lock (frames) return [.. frames]; }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await reading;
            stop.Dispose();
        }
    }
}
