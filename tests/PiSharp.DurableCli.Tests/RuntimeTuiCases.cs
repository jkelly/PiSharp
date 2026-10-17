// Authored for PiSharp: `pisharp durable` end to end, as DurableMain runs it (DurableRuntime.OpenAsync, DurableTui.RunAsync, dispose) but on
// the interactive cases' VirtualTerminal instead of the process terminal, with a scripted model and a temporary agent directory. The kill
// case runs the TUI in a child process, types a prompt there, kills the process while the answer streams, and continues the session in this
// process: the TUI shows the finished turn.
using PiSharp.Cli.Durable;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Durable;
using PiSharp.Tui.Pi;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> RuntimeTuiCases() =>
    [
        Case("runtime.e2e.tui-prompt-and-exit", async () =>
        {
            await using var agent = new TestAgent("e2e");
            var streams = ScriptedStreams.Queue(ScriptedReply.Slow("Hello from the durable agent.", 5, TimeSpan.FromMilliseconds(20)));
            var durable = (DurableSession)await DurableRuntime.OpenAsync(new OpenDurableOptions(), agent.Host(streams));
            var terminal = new VirtualTerminal(120, 40);
            int code;
            try
            {
                var run = StartTui(durable, terminal, agent.AgentDir);
                await terminal.WaitForTextAsync("/compact · /tasks");
                await Type(terminal, "say hello");
                await terminal.WaitForTextAsync("Hello from the durable agent.");
                await UntilView(durable.View, Idle, "idle");
                terminal.Send("\u0004");
                code = await run.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally { await durable.DisposeAsync(); }
            Equal(0, code, "exit code");
            Check(!Directory.Exists(durable.Location.Directory + ".lock"), "dispose released the lock");
        }),
        Case("runtime.e2e.tui-kill-and-continue", async () =>
        {
            await using var agent = new TestAgent("e2e-kill");
            string directory;
            await using (var child = new Child(["--durable-tui-child", agent.Root]))
            {
                directory = (await child.WaitForLineAsync("CHECKPOINT ", TimeSpan.FromSeconds(90)))["CHECKPOINT ".Length..];
                await child.KillAsync();
            }
            var streams = ScriptedStreams.Queue(ScriptedReply.Answer("Finished after the restart."));
            var durable = (DurableSession)await DurableRuntime.OpenAsync(new OpenDurableOptions { ContinueSession = true },
                agent.Host(streams, DurableSessions.DefaultLock));
            var terminal = new VirtualTerminal(120, 40);
            try
            {
                Equal(directory, durable.Location.Directory, "the killed session");
                var run = StartTui(durable, terminal, agent.AgentDir);
                var screen = await terminal.WaitForTextAsync("Finished after the restart.", 30_000);
                Check(screen.Contains("keep streaming", StringComparison.Ordinal), "the earlier prompt is shown:\n" + screen);
                terminal.Send("\u0004");
                Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)), "exit code");
            }
            finally { await durable.DisposeAsync(); }
        }),
    ];

    private static Task<int> StartTui(IOpenDurable durable, VirtualTerminal terminal, string agentDir)
    {
        Themes.Environment = _ => null;
        return Task.Run(() => DurableTui.RunAsync(durable, new DurableTuiOptions(loop => new ProcessTerminal(loop, terminal, _ => null), agentDir),
            CancellationToken.None));
    }

    private static async Task Type(VirtualTerminal terminal, string text)
    {
        terminal.Send(text);
        await Task.Delay(30);
        terminal.Send("\r");
    }

    /// <summary><c>--durable-tui-child &lt;root&gt;</c>: `pisharp durable` on a virtual terminal over the TestAgent layout under
    /// <c>root</c> with an endless scripted answer; types a prompt and prints <c>CHECKPOINT &lt;session dir&gt;</c> once the TUI shows the
    /// answer streaming.</summary>
    internal static async Task<int> RunTuiChildAsync(string root)
    {
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => Environment.Exit(3), TaskScheduler.Default);
        try
        {
            var host = new DurableRuntimeHost
            {
                AgentDir = Path.Combine(root, "agent"), Home = Path.Combine(root, "home"), Cwd = Path.Combine(root, "project"), GetEnvironment = TestAgent.Env(),
                ApplicationDirectory = Path.Combine(root, "app"),
                LiveRuntime = new PiSharp.Cli.Commands.LiveSessionRuntime(TestAgent.Env(), () => null, Path.Combine(root, "agent", "auth.json"),
                    ModelsPath: Path.Combine(root, "agent", "models.json")),
                Streams = ScriptedStreams.Queue(ScriptedReply.Endless(TimeSpan.FromMilliseconds(20))),
            };
            var durable = (DurableSession)await DurableRuntime.OpenAsync(new OpenDurableOptions(), host);
            var terminal = new VirtualTerminal(120, 40);
            _ = StartTui(durable, terminal, host.AgentDir);
            await terminal.WaitForTextAsync("/compact · /tasks", 60_000);
            await Type(terminal, "keep streaming");
            await terminal.WaitForTextAsync("tick tick", 60_000);
            await UntilView(durable.View, view => view.Conversation.Doc("pi.live") is { } live && live.TryGetValue("generation", out var generation) &&
                generation is PiSharp.Durable.Chord.JsonObjectValue current && current.TryGetValue("message", out var message) &&
                message is PiSharp.Durable.Chord.JsonObjectValue partial && ScriptedRequest.Text(partial).Length > 0, "a committed partial", seconds: 60);
            Emit("CHECKPOINT " + durable.Location.Directory);
            await new TaskCompletionSource().Task;
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Emit("ERROR " + error.Message.ReplaceLineEndings(" "));
            return 1;
        }
    }
}
