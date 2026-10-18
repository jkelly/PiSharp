using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PiSharp.Durable.Client;
using PiSharp.Durable.Server;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> StreamCases() =>
    [
        Case("streams.agent-frames-reduce-to-the-transcript", async () =>
        {
            await using var server = await TestServer.StartAsync("streams-agent");
            var id = await server.CreateSessionAsync("Streams");
            var main = $"/v1/sessions/{id}/agents/main";
            var model = new AgentModel();
            await using var frames = new FrameLog(server.Client, main + "/events?docs=pisharp.job-runs", frame => model.Apply(frame));
            await WaitUntilAsync(() => model.Ready, "the snapshot");
            Equal("hello", Type(frames.Frames[0]), "first frame");
            Equal("snapshot", Type(frames.Frames[1]), "second frame");
            Check(frames.Any(frame => Type(frame) == "doc" && frame["kind"]?.GetValue<string>() == "pisharp.job-runs"), "the requested document");

            await server.Post(main + "/messages", Message("""tool: read_state {"path":"/counter"}""", "s-0"));
            await server.Post(main + "/messages", Message("slow: " + string.Join(' ', Enumerable.Repeat("durable streaming words", 20)), "s-1"));
            var sawPartial = false;
            await WaitUntilAsync(() =>
            {
                sawPartial |= model.Items.OfType<AssistantItem>().Any(item => item.Streaming && item.Blocks.OfType<TextBlockView>().Any(block => block.Text.Length > 10));
                return !model.Running && model.Items.OfType<AssistantItem>().Count(item => !item.Streaming) >= 2 && sawPartial;
            }, "the slow reply streamed and ended");
            var serverItems = (await server.Get(main + "/transcript"))!["items"]!;
            var reduced = Transcript.ToJson(model.Items);
            Equal(serverItems.ToJsonString(), reduced.ToJsonString(), "the reduced items equal the server's transcript");
            var types = frames.Frames.Where(frame => Type(frame) == "batch").SelectMany(frame => frame["events"]!.AsArray().Select(item => item!["type"]!.GetValue<string>())).ToHashSet();
            foreach (var expected in new[] { "run_start", "message_start", "message_update", "message_end", "tool_execution_start", "tool_execution_end", "run_end" })
                Check(types.Contains(expected), "missing " + expected);
            Check(model.Items.OfType<AssistantItem>().SelectMany(item => item.Blocks).OfType<ToolBlockView>().Any(block => block.Call.Name == "read_state" && block.Call.Status == ToolStatus.Done && block.Call.Result!.Contains("revision")),
                "a tool call carries its result");
        }),

        Case("streams.late-joiner-inbox-and-closing-a-viewer", async () =>
        {
            await using var server = await TestServer.StartAsync("streams-late");
            var id = await server.CreateSessionAsync("Late");
            var main = $"/v1/sessions/{id}/agents/main";
            var model = new AgentModel();
            await using var frames = new FrameLog(server.Client, main + "/events", frame => model.Apply(frame));
            await WaitUntilAsync(() => model.Ready, "the snapshot");
            await server.Post(main + "/messages", Message("slow: " + string.Join(' ', Enumerable.Repeat("word", 60)), "l-1"));
            await WaitUntilAsync(() => model.Running, "the run started");
            var late = new AgentModel();
            var lateFrames = new FrameLog(server.Client, main + "/events", frame => late.Apply(frame));
            await WaitUntilAsync(() => late.Ready, "the late snapshot");
            Check(late.Running, "a late joiner sees the run");
            await server.Post(main + "/messages", Message("then say bye", "l-2", "steer"));
            await WaitUntilAsync(() => late.Inbox.Any(item => item.Content == "then say bye" && item.Mode == "steer"), "the inbox with content");
            await lateFrames.DisposeAsync();
            await WaitUntilAsync(() => !model.Running && model.Items.OfType<AssistantItem>().Any(item =>
                item.Blocks.OfType<TextBlockView>().Any(block => block.Text == "Echo: then say bye")), "the run continued after the viewer closed");
        }),

        Case("streams.session-and-device-frames", async () =>
        {
            await using var server = await TestServer.StartAsync("streams-session");
            var id = await server.CreateSessionAsync("Session stream");
            var main = $"/v1/sessions/{id}/agents/main";
            var session = new SessionModel();
            await using var sessionFrames = new FrameLog(server.Client, $"/v1/sessions/{id}/events", frame => session.Apply(frame));
            await using var deviceFrames = new FrameLog(server.Client, "/v1/events?docs=app.shared-state");
            await WaitUntilAsync(() => session.Ready, "the session frame");
            Equal("session", Type(sessionFrames.Frames[1]), "the session frame follows hello");
            await WaitUntilAsync(() => deviceFrames.Any(frame => Type(frame) == "tree"), "the tree");
            Check(deviceFrames.Any(frame => Type(frame) == "doc" && frame["kind"]?.GetValue<string>() == "app.shared-state"), "the App State document");
            await server.Post(main + "/messages", Message("slow: " + string.Join(' ', Enumerable.Repeat("word", 20)), "ss-1", waitMs: 15000));
            await WaitUntilAsync(() => sessionFrames.Any(frame => Type(frame) == "tasks" && frame["tasks"]!.AsArray().Any(task => task!["kind"]!.GetValue<string>() == "pi.generation")),
                "a generation task");
            await WaitUntilAsync(() => session.Tasks.Count == 0, "the tasks drained");
            await server.Patch(main, new JsonObject { ["name"] = "Renamed live" });
            await WaitUntilAsync(() => session.Agents.FirstOrDefault(agent => agent.Id == 1)?.Name == "Renamed live", "the rename on the session stream");
            List<JsonObject> Activity() => [.. deviceFrames.Frames.Where(frame => Type(frame) == "activity").SelectMany(frame => frame["items"]!.AsArray().OfType<JsonObject>())];
            await WaitUntilAsync(() => Activity().Any(item => item["type"]!.GetValue<string>() == "agent.finished" && (item["preview"]?.GetValue<string>() ?? "").Length > 0),
                "a finished agent with a preview");
            Check(Activity().Any(item => item["type"]!.GetValue<string>() == "agent.started"), "a started agent");
            await server.Post(main + "/state", new JsonObject { ["increment"] = true });
            await WaitUntilAsync(() => deviceFrames.Any(frame => Type(frame) == "doc" && frame["revision"]?.GetValue<long>() == 1), "the App State change");
            var trees = deviceFrames.Frames.Where(frame => Type(frame) == "tree").Select(frame => frame.ToJsonString()).ToList();
            Equal(trees.Count, trees.Distinct().Count(), "tree frames are deduplicated");
        }),

        Case("streams.errors-before-the-stream-starts", async () =>
        {
            await using var server = await TestServer.StartAsync("streams-errors");
            var id = await server.CreateSessionAsync("Errors");
            var bad = await server.RawAsync(HttpMethod.Get, $"/v1/sessions/{id}/agents/main/events?docs=nope");
            Equal(400, bad.Status, "an unknown document");
            Equal(404, (await server.RawAsync(HttpMethod.Get, $"/v1/sessions/{id}/agents/99/events")).Status, "an unknown agent");
            Equal(404, (await server.RawAsync(HttpMethod.Get, "/v1/sessions/nope/events")).Status, "an unknown session");
            Equal(400, (await server.RawAsync(HttpMethod.Get, "/v1/events?docs=nope")).Status, "an unknown device document");
        }),

        Case("streams.a-slow-viewer-is-cut-off", async () =>
        {
            var http = new DefaultHttpContext();
            var body = new StalledStream();
            http.Response.Body = body;
            var options = new DurableDeviceServerOptions { Token = DeviceTokens.Generate(), MaxStreamBacklogBytes = 100_000 };
            var sse = await SseStream.OpenAsync(http, new JsonObject { ["kind"] = "device" }, options);
            await WaitUntilAsync(() => body.Written > 0, "hello reached the client");
            for (var i = 0; i < 100 && !sse.Closed.IsCancellationRequested; i++)
                sse.Send(new JsonObject { ["type"] = "activity", ["items"] = new JsonArray(), ["pad"] = new string('x', 10_000) });
            Check(sse.Closed.IsCancellationRequested, "the stream closed once the backlog passed the limit");
            body.Release();
            await sse.DisposeAsync();
        }),

        Case("streams.client-follow-reconnects-and-stops-on-unauthorized", async () =>
        {
            await using var server = await TestServer.StartAsync("streams-follow");
            using var stop = new CancellationTokenSource(Patience);
            var hellos = 0;
            await foreach (var frame in server.Client.FollowAsync("/v1/events", cancellationToken: stop.Token))
                if (frame.Type == "hello" && ++hellos == 1) break;
            Equal(1, hellos, "followed");
            using var wrong = new DurableDeviceClient(server.Base, new string('y', 40));
            await FailsAsync(async () => { await foreach (var _ in wrong.FollowAsync("/v1/events", cancellationToken: stop.Token)) { } }, "unauthorized",
                "unauthorized ends the enumeration");
        }),
    ];

    /// <summary>A response body whose writes complete only after the first, until released: a client that stopped reading.</summary>
    private sealed class StalledStream : Stream
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long written;

        public long Written => Interlocked.Read(ref written);

        public void Release() => released.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Add(ref written, buffer.Length) > buffer.Length) await released.Task.WaitAsync(cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
