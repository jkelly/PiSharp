using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Output;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;

// wire.agent-settled-aborted remaining emission: JSON mode (docs/json.md) and native extension observers (agent-session.ts,
// extensions/runner.ts); wire.rpc-bash-ansi host surfaces: interactive !/!! (interactive-mode.ts) and bounded deltas.
// These cases need PiSharp.Cli internals, so they live with the CLI sync suite. Authored expectations.
internal static partial class Program
{
    private static readonly ModelDescriptor SettledModel = new("settled-model", "openai-responses", "authored-provider");

    private sealed class SettledTransport(TaskCompletionSource? hold = null) : IChatTransport
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var message = new AssistantMessage(SettledModel.Api, SettledModel.Provider, SettledModel.Id, 1, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            Entered.TrySetResult();
            if (hold is not null) await hold.Task.WaitAsync(token);
            yield return new TextStarted(0, new("")); yield return new TextDelta(0, "done"); yield return new TextEnded(0, "done");
            yield return new StreamDone(StopReason.Stop, message);
        }
    }

    private static async Task<(PersistentAgentSession Session, string Directory)> SettledSession(IChatTransport transport)
    {
        var directory = Temp("settled-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var ids = 0;
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "settled-header",
            timestamp = "2026-10-08T00:00:00.000Z", cwd = directory }));
        var session = await PersistentAgentSession.CreateAsync(Path.Combine(directory, "session.jsonl"), header,
            new AgentConfiguration(SettledModel, transport, []), () => 7, () => "settled-entry-" + Interlocked.Increment(ref ids));
        return (session, directory);
    }

    private static TranscriptEntry SettledUser(string text) =>
        new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 7 })));

    private static async Task JsonModeAgentSettled()
    {
        // A real one-shot run settles with aborted:false. In json mode an abort is the command's own cancellation, which ends
        // delivery, so the aborted spelling is checked by publishing the session's settlement observation directly.
        foreach (var abort in new[] { false, true })
        {
            var (session, directory) = await SettledSession(new SettledTransport());
            try
            {
                using var output = new StringWriter();
                await using (var json = new SessionJsonEventOutput(session, output))
                {
                    await json.StartAsync();
                    await session.PromptAsync(SettledUser("go")); await session.WaitForIdleAsync();
                    if (abort) await json.EmitAsync(new SessionOperationSettled(2, "cancelled", null) { Aborted = true }, CancellationToken.None);
                    json.ThrowIfFailed();
                }
                var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var types = lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("type").GetString()).ToArray();
                // Source docs/json.md: agent_end closes the low-level run, then agent_settled closes the session-level run.
                Equal("agent_end", types[abort ? ^3 : ^2], "record before agent_settled");
                Equal("""{"type":"agent_settled","aborted":false}""", lines[abort ? ^2 : ^1], "agent_settled record");
                if (abort) Equal("""{"type":"agent_settled","aborted":true}""", lines[^1], "aborted agent_settled record");
                Equal(abort ? 2 : 1, types.Count(type => type == "agent_settled"), "agent_settled count");
            }
            finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }
        }
    }

    private sealed class ObservingExtension(List<string> seen) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
        {
            registry.Observe(new("settled-observer", "agent_settled", (observation, _, _) =>
            { lock (seen) seen.Add(observation.ToString()); return ValueTask.CompletedTask; }));
            registry.Observe(new("failing-observer", "agent_settled", (_, _, _) => throw new InvalidOperationException("observer failure")));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task ExtensionAgentSettled()
    {
        var seen = new List<string>(); var diagnostics = new List<string>();
        var registry = new ExtensionRegistry();
        await registry.ActivateAsync("settled-extension", new ObservingExtension(seen));
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new SettledTransport(hold);
        var (session, directory) = await SettledSession(transport);
        var owner = new ReplaceableAgentSession(session, (_, _) => throw new NotSupportedException());
        try
        {
            new NativeAgentSettledObservationBinding(registry, registry.CaptureSnapshot(), (diagnostic, _) =>
            { lock (diagnostics) diagnostics.Add(diagnostic.EventName + ":" + diagnostic.Failure); return ValueTask.CompletedTask; })
                .Attach(owner, owner.Current);
            hold.TrySetResult();
            await session.PromptAsync(SettledUser("first")); await session.WaitForIdleAsync();
            Names(["""{"type":"agent_settled","aborted":false}"""], seen, "settled observation");
            // A throwing observer is reported; the run still settles normally.
            Names(["agent_settled:HandlerFailed"], diagnostics, "observer failure diagnostic");
            Check(session.Snapshot.Fault is null, "observer failure faulted the session");
        }
        finally { await owner.DisposeAsync(); Directory.Delete(directory, recursive: true); }
    }

    private static async Task InteractiveUserBash()
    {
        var sent = new List<string>(); using var view = new StringWriter();
        using var frontend = new InteractiveSessionFrontend(view);
        frontend.Bind((record, _) => { sent.Add(record.ToString()); return Task.CompletedTask; });
        await frontend.LineAsync("!  ls -la ", CancellationToken.None);
        await frontend.LineAsync("!!git status", CancellationToken.None);
        await frontend.ObserveAsync(JsonData.Parse("""{"type":"bash_execution_update","id":"chat-1","delta":"a.txt\n"}"""), CancellationToken.None);
        await frontend.ObserveAsync(JsonData.Parse("""{"id":"chat-1","type":"response","command":"bash","success":true,"data":{"output":"a.txt\n","exitCode":2,"cancelled":false,"truncated":true,"fullOutputPath":"/tmp/pi-bash-0123456789abcdef.log"}}"""), CancellationToken.None);
        await frontend.LineAsync("!!git status", CancellationToken.None);
        await frontend.LineAsync("/abort", CancellationToken.None);
        await frontend.LineAsync("!", CancellationToken.None);
        Names([
            """{"id":"chat-1","type":"bash","command":"ls -la","excludeFromContext":false}""",
            """{"id":"chat-3","type":"bash","command":"git status","excludeFromContext":true}""",
            """{"id":"chat-4","type":"abort_bash"}""",
            """{"id":"chat-5","type":"prompt","message":"!","streamingBehavior":"steer"}"""], sent, "frontend RPC commands");
        Equal(string.Join("\n", "$ ls -la", "[warning] A bash command is already running. Use /abort to cancel it first.", "a.txt",
            "(exit 2)", "Output truncated. Full output: /tmp/pi-bash-0123456789abcdef.log", "$ git status", ""),
            view.ToString().ReplaceLineEndings("\n"), "frontend bash view");
        // Session progress deltas are bounded; a longer chunk is split without separating a surrogate pair.
        var chunk = new string('x', 65_535) + "\U0001F600" + "tail";
        var deltas = UserBashHost.Deltas(chunk).ToArray();
        Equal(2, deltas.Length, "delta count"); Equal(65_535, deltas[0].Length, "first delta keeps the pair whole");
        Equal(chunk, string.Concat(deltas), "deltas rejoin");
    }
}
