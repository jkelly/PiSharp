// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/examples/19-json.ts. JSON mode: stream one prompt's run as JSON
// lines, like `pi --mode json`, as the experimental agent events of WatchEventsAsync() (starting with a snapshot event, upstream --events)
// or as the raw ConversationView frames (starting with the view itself; each later line holds one commit's ops, upstream --ops). Upstream
// runs one mode per invocation; here the example runs both, events on the runner's storage and ops on MemoryStorage (upstream
// --storage memory), each under a "mode:" line of its own. Upstream uses OpenAI when OPENAI_API_KEY is set and otherwise pi-ai's faux
// provider at 200 tokens per second; here PacedModels always stands in with that pacing (token size fixed at 4, upstream's is random) and
// the faux usage estimate, on an ExampleClock that also drives the Harness throttles, so the run streams the same partial every time. Under
// Node the first partial varies from run to run with the random token size. The bash tool runs `ls` in a directory that stands in for
// upstream's working directory, packages/durable, with the same top-level entries (the listing feeds the next request's usage estimate).
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // 19: JSON mode.
    private static async Task JsonModeAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // A stand-in for upstream's working directory, packages/durable.
        var cwd = ExampleDirectory("pi-durable-json-cwd-");
        foreach (var file in new[] { "CHANGELOG.md", "README.md", "package.json", "tsconfig.build.json", "vitest.benchmark.config.ts", "vitest.config.ts" })
            await File.WriteAllTextAsync(Path.Combine(cwd, file), "");
        foreach (var folder in new[] { "docs", "src", "test" }) Directory.CreateDirectory(Path.Combine(cwd, folder));

        await output.WriteLineAsync("mode: events");
        var storage = await open();
        var root = await RunJsonAsync(storage, events: true, cwd, output);
        await output.WriteLineAsync("mode: ops");
        await RunJsonAsync(new MemoryStorage(), events: false, cwd, output);
        Directory.Delete(cwd, recursive: true);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root, 20))));
    }

    private static async Task<ConversationId> RunJsonAsync(IStorage storage, bool events, string cwd, TextWriter output)
    {
        const string prompt = "What is in this directory?";
        void Print(JsonValue value) => output.WriteLine(value.ToJson());

        var clock = new ExampleClock();
        var models = new PacedModels(clock, 200, 4,
            ScriptedReply.ToolCall("bash", JsonValue.Object([new("command", "ls")]), "call-1"),
            "This directory holds the durable package sources, tests, and docs.")
        { Api = PacedModels.RandomApi(clock) };
        var registry = Registry.Create();
        registry.Install(new Extension
        {
            Name = "coding",
            Tools = [CodingTools.CreateReadTool(), CodingTools.CreateBashTool()],
            Sections = [Define.Section("preamble", "You are a concise coding assistant.", tag: false)],
        });
        // C collation, so `ls` lists in the order Git Bash and macOS print by default whatever the machine's locale.
        var env = new LocalExecutionEnv(new LocalExecutionEnvOptions { Cwd = cwd, ShellEnv = new Dictionary<string, string> { ["LC_ALL"] = "C" } });
        var harness = await Harness.OpenAsync(clock.Observe(storage), new HarnessOptions
        {
            Models = models, Registry = registry, TimeProvider = clock, Env = (_, _) => Task.FromResult<IExecutionEnv?>(env),
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });

        // Attach before submitting, so the stream covers the whole run.
        Func<Task> stop;
        if (events)
        {
            var stream = await harness.WatchEventsAsync(root.Id, Context);
            Print(stream.Snapshot.ToJson());
            stream.Start((batch, _) =>
            {
                foreach (var agentEvent in batch) Print(agentEvent.ToJson());
                return Task.CompletedTask;
            });
            stop = () => stream.StopAsync();
        }
        else
        {
            var watch = await root.WatchAsync(Context);
            Print(JsonValue.Object([new("view", watch.Value.ToJson())]));
            watch.Start((_, ops, _) =>
            {
                Print(JsonValue.Object([new("ops", JsonValue.Array(ops.Select(op => (JsonValue)op.ToJson())))]));
                return Task.CompletedTask;
            });
            stop = () => watch.StopAsync();
        }

        var submission = await root.SubmitAsync(SubmissionDraft.Input(prompt), Context);
        await clock.RunUntilAsync(submission.WaitAsync(Context));
        await clock.RunUntilAsync(harness.WaitForIdleAsync(Context));
        // Let the last batch reach the listener before stopping.
        await clock.SettleAsync();
        await stop();
        await harness.CloseAsync(Context);
        return root.Id;
    }
}
