// PiSharp-only (decision 0010, M6): `pisharp durable attach` (src/PiSharp.Cli/Durable/DurableAttach.cs) against a device on a fixed loopback
// port that is stopped and reopened on the same data directory mid-session, a question form and a job approval answered from the
// terminal, and an older device without application features.
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using PiSharp.Cli.Durable;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Server;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> AttachCases() =>
    [
        Case("attach.survives-a-device-restart-without-duplicates-and-retries-with-the-same-request-id", async () =>
        {
            var root = Temp("attach-restart");
            var port = ClosedPort();
            var token = DeviceTokens.Generate();
            var device = await AttachDevice.StartAsync(root, port, token);
            using var client = new DurableDeviceClient(new Uri($"http://127.0.0.1:{port}/"), token);
            var (session, agent) = await MainAgentAsync(client, device.Work);
            var output = new Capture();
            var attach = new DurableAttach(client, await client.DeviceAsync(), session, agent, output);
            using var stop = new CancellationTokenSource();
            var follow = attach.FollowAsync(stop.Token);
            try
            {
                await UntilAsync(() => Task.FromResult(attach.View.Model.Ready), "the first snapshot");
                Check(await attach.HandleAsync("hello", CancellationToken.None), "a message keeps attach open");
                await UntilAsync(() => Task.FromResult(output.Text.Contains("Echo: hello", StringComparison.Ordinal)), "the reply printed");

                await device.DisposeAsync();
                await UntilAsync(() => Task.FromResult(output.Text.Contains("[disconnected", StringComparison.Ordinal)), "the disconnect printed");
                await attach.HandleAsync("while down", CancellationToken.None);
                var kept = attach.Unsent ?? throw new InvalidOperationException("the failed send is kept: " + output.Text);
                Equal("while down", kept.Text, "the kept text");
                Check(output.Text.Contains("/retry sends it again with request ID " + kept.RequestId, StringComparison.Ordinal), "the request ID is shown: " + output.Text);

                device = await AttachDevice.StartAsync(root, port, token);
                await UntilAsync(() => Task.FromResult(output.Text.Contains("[reconnected]", StringComparison.Ordinal)), "the reconnect printed", 40);
                Check(attach.Unsent is not null, "a reconnect does not resend the kept message");
                await attach.HandleAsync("/retry", CancellationToken.None);
                Check(attach.Unsent is null, "the retry was confirmed: " + output.Text);
                await UntilAsync(() => Task.FromResult(output.Text.Contains("Echo: while down", StringComparison.Ordinal)), "the retried reply printed");
                // The same request ID again is the same submission, not a second message.
                await client.SendMessageAsync(session, agent, "while down", kept.RequestId);
                await attach.HandleAsync("/retry", CancellationToken.None);
                Check(output.Text.Contains("Nothing to retry.", StringComparison.Ordinal), "nothing is left to retry");
                await Task.Delay(500);
                var transcript = (await client.TranscriptAsync(session, agent, markdown: true))!["text"]!.GetValue<string>();
                Equal(1, Occurrences(transcript, "Echo: while down"), "one reply to the retried message: " + transcript);
                Equal(1, Occurrences(transcript, "Echo: hello"), "one reply to the first message");
                foreach (var line in new[] { "> hello", "Echo: hello", "> while down", "Echo: while down" })
                    Equal(1, Occurrences(output.Text, line), $"\"{line}\" printed once across the restart: {output.Text}");
            }
            finally
            {
                await stop.CancelAsync();
                await follow;
                await device.DisposeAsync();
            }
        }),
        Case("attach.answers-a-question-form-and-a-job-approval-as-text", async () =>
        {
            var root = Temp("attach-answers");
            var port = ClosedPort();
            var token = DeviceTokens.Generate();
            await using var device = await AttachDevice.StartAsync(root, port, token);
            using var client = new DurableDeviceClient(new Uri($"http://127.0.0.1:{port}/"), token);
            var (session, agent) = await MainAgentAsync(client, device.Work);
            var output = new Capture();
            var attach = new DurableAttach(client, await client.DeviceAsync(), session, agent, output);
            using var stop = new CancellationTokenSource();
            var follow = attach.FollowAsync(stop.Token);
            try
            {
                await UntilAsync(() => Task.FromResult(attach.View.Model.Ready), "the first snapshot");
                await attach.HandleAsync("""tool: ask_user_question {"questions":[{"question":"Which version?","header":"Version","options":[{"label":"1.2.0.2","description":"Fixes"},{"label":"1.2.1","description":"Next"}],"multiSelect":false},{"question":"Anything else?","header":"Notes","options":[{"label":"No","description":"Nothing"},{"label":"Yes","description":"Say what"}],"multiSelect":false}]}""",
                    CancellationToken.None);
                try { await UntilAsync(() => Task.FromResult(attach.View.Model.Pending.Count == 1 && output.Text.Contains("[question", StringComparison.Ordinal)), "the question card printed"); }
                catch (TimeoutException) { throw new InvalidOperationException($"no card; pending {attach.View.Model.Pending.Count}: {output.Text}"); }
                var task = attach.View.Model.Pending[0].TaskId;
                foreach (var part in new[] { $"[question {task}] The agent asks:", "1. Which version?", "2) 1.2.1 — Next", $"/answer {task}" })
                    Check(output.Text.Contains(part, StringComparison.Ordinal), $"the card shows \"{part}\": {output.Text}");
                await attach.HandleAsync($"/answer {task} 2; ship it on Friday", CancellationToken.None);
                Check(output.Text.Contains($"Answered {task}.", StringComparison.Ordinal), "answered: " + output.Text);
                await UntilAsync(async () => (await client.TranscriptAsync(session, agent, markdown: true))!["text"]!.GetValue<string>() is var text &&
                    text.Contains("1.2.1", StringComparison.Ordinal) && text.Contains("ship it on Friday", StringComparison.Ordinal), "the choice and the free answer reached the agent");
                await attach.HandleAsync($"/answer {task} 1", CancellationToken.None);
                Check(output.Text.Contains("conflict", StringComparison.Ordinal), "a second answer is refused, and attach stays open: " + output.Text);

                var run = await device.Host.ResolveSession(session).RunJobAsync(agent, "release-check", PiSharp.Durable.Chord.JsonValue.Object([new("commits", 1.0)]));
                await UntilAsync(async () =>
                {
                    output.Clear();
                    await attach.HandleAsync("/pending", CancellationToken.None);
                    return output.Text.Contains($"/approve {run.Value}", StringComparison.Ordinal);
                }, "the job waits for approval", 60);
                await attach.HandleAsync($"/approve {run.Value}", CancellationToken.None);
                Check(output.Text.Contains($"Job run {run.Value}: approved.", StringComparison.Ordinal), "approved: " + output.Text);
                await UntilAsync(async () => (await client.JobsAsync(session, agent))!["runs"]!.AsArray().OfType<JsonObject>()
                    .Any(item => item["id"]!.GetValue<long>() == run.Value && item["status"]!.GetValue<string>() == "completed"), "the job completed", 60);

                await attach.HandleAsync("/model faux/echo-small", CancellationToken.None);
                await UntilAsync(() => Task.FromResult(attach.View.Model.Agent["model"]?["id"]?.GetValue<string>() == "echo-small" ||
                    attach.View.Model.Agent["model"]?["modelId"]?.GetValue<string>() == "echo-small"), "the model change reached the stream");
                output.Clear();
                await attach.HandleAsync("/status", CancellationToken.None);
                Check(output.Text.Contains("echo-small", StringComparison.Ordinal) && output.Text.Contains("idle", StringComparison.Ordinal), "status: " + output.Text);
                output.Clear();
                await attach.HandleAsync("/history 400", CancellationToken.None);
                Check(output.Text.Contains("Which version?", StringComparison.Ordinal), "history prints the transcript: " + output.Text);
                output.Clear();
                await attach.HandleAsync("/artifacts", CancellationToken.None);
                Check(!output.Text.Contains("does not offer", StringComparison.Ordinal), "this device offers artifacts: " + output.Text);
                Check(!await attach.HandleAsync("/quit", CancellationToken.None), "/quit detaches");
            }
            finally
            {
                await stop.CancelAsync();
                await follow;
            }
        }),
        Case("attach.an-older-device-says-what-it-does-not-offer", async () =>
        {
            var output = new Capture();
            using var client = new DurableDeviceClient(new Uri($"http://127.0.0.1:{ClosedPort()}/"), DeviceTokens.Generate());
            var attach = new DurableAttach(client, new PiSharp.Durable.Client.DeviceInfo { Uid = "old", Name = "Old device" }, "s", 1, output);
            Check(await attach.HandleAsync("/artifacts", CancellationToken.None), "attach stays open");
            Equal("Old device does not offer artifacts.", output.Text.Trim(), "an actionable unsupported result, not an offline error");
            output.Clear();
            await attach.HandleAsync("/history", CancellationToken.None);
            Check(output.Text.StartsWith("offline:", StringComparison.Ordinal), "an unreachable device is offline, which reads differently: " + output.Text);
            output.Clear();
            await attach.HandleAsync("/frobnicate", CancellationToken.None);
            Check(output.Text.Contains("/help lists the commands", StringComparison.Ordinal), "an unknown command points at /help");
        }),
        Case("attach.the-command-picks-the-only-session-and-detaches-on-end-of-input", async () =>
        {
            var root = Temp("attach-command");
            var port = ClosedPort();
            var token = DeviceTokens.Generate();
            await using var device = await AttachDevice.StartAsync(root, port, token);
            var env = new Dictionary<string, string> { ["PISHARP_DEVICE_TOKEN"] = token };
            var none = await CliAsync(root, env, "attach", "--device", $"http://127.0.0.1:{port}/");
            Equal(DurableExit.Usage, none.Exit, "no session: usage");
            Check(none.Err.Contains("no sessions", StringComparison.Ordinal), "says so: " + none.Err);
            using var client = new DurableDeviceClient(new Uri($"http://127.0.0.1:{port}/"), token);
            var (session, _) = await MainAgentAsync(client, device.Work);
            var attached = await CliAsync(root, env, "attach", "--device", $"http://127.0.0.1:{port}/");
            Equal(DurableExit.Ok, attached.Exit, "end of input detaches: " + attached.Err);
            Check(attached.Out.Contains($"session {session}", StringComparison.Ordinal), "attached to the only session: " + attached.Out);
            var refused = await CliAsync(root, env, "attach", "--device", $"http://127.0.0.1:{port}/", "--token", "x");
            Equal(DurableExit.Usage, refused.Exit, "a token is never an argument");
        }),
    ];

    private static async Task<(string Session, long Agent)> MainAgentAsync(DurableDeviceClient client, string cwd)
    {
        var session = (await client.CreateSessionAsync(new JsonObject { ["cwd"] = cwd, ["name"] = "attach" }))!["id"]!.GetValue<string>();
        var agent = (await client.AgentsAsync(session))!.AsArray().OfType<JsonObject>().Single(item => item["kind"]!.GetValue<string>() == "root")["id"]!.GetValue<long>();
        return (session, agent);
    }

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>A thread-safe text capture.</summary>
    private sealed class Capture : TextWriter
    {
        private readonly StringBuilder text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get { lock (text) return text.ToString(); }
        }

        public void Clear()
        {
            lock (text) text.Clear();
        }

        public override void Write(char value)
        {
            lock (text) text.Append(value);
        }

        public override void Write(string? value)
        {
            lock (text) text.Append(value);
        }
    }

    /// <summary>A device on a fixed loopback port with the faux model, reopened on the same data directory by starting another.</summary>
    private sealed class AttachDevice : IAsyncDisposable
    {
        private WebApplication app = null!;
        private int disposed;

        public DurableDeviceHost Host { get; private set; } = null!;
        public string Work { get; private set; } = "";

        public static async Task<AttachDevice> StartAsync(string root, int port, string token)
        {
            var device = new AttachDevice { Work = Path.Combine(root, "work") };
            Directory.CreateDirectory(device.Work);
            device.Host = await DurableDeviceHost.OpenAsync(new DurableDeviceOptions
            {
                DataDirectory = Path.Combine(root, "data"),
                StartDirectories = [device.Work],
                Models = new FauxDurableModels(),
                DefaultModel = FauxDurableModels.EchoRef,
            }, CancellationToken.None);
            device.app = DurableDeviceServer.Build(device.Host, new DurableDeviceServerOptions { Token = token }, $"http://127.0.0.1:{port}");
            await device.app.StartAsync();
            return device;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await app.StopAsync();
            await app.DisposeAsync();
            await Host.DisposeAsync();
        }
    }
}
