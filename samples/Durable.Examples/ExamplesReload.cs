// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/examples/31-reload-and-restart.ts. Upstream stands in for a model
// with pi-ai's faux provider; here ScriptedModels does, so the example runs offline. It prints what upstream's console.log prints, as JSON.
// The example restarts the process itself, so it needs storage that survives close: upstream always uses a SQLite file, and so does the
// memory run here (a temporary one); a file backend reopens its own location.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // 31: reload an extension while a call runs, then restart the process. Running work finishes on the code it started with; stored choices
    // are names, so they survive a restart and bind to whatever code the new process installs.
    private static async Task ReloadAndRestartAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // Stand-in for code loaded from disk: each call builds the extension as the file currently reads.
        Action started = () => { };
        var gate = Task.CompletedTask;
        Extension LoadVersioned(string version) => new()
        {
            Name = "versioned",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "version",
                    Description = "Report the tool's code version",
                    Execute = async (_, _, _) =>
                    {
                        started();
                        await gate;
                        return new ToolExecutionResult { Content = JsonValue.Array(JsonValue.Object([new("type", "text"), new("text", version)])) };
                    },
                }),
            ],
        };

        static ScriptedReply CallVersion() => ScriptedReply.ToolCall("version", JsonObjectValue.Empty);
        var models = new ScriptedModels(CallVersion(), "Done.", CallVersion(), "Done.");

        // The process restarts, so the storage must survive close: a memory run uses a temporary SQLite file, as upstream does.
        var storage = await open();
        string? temporary = null;
        if (storage is MemoryStorage)
        {
            await storage.CloseAsync(Context);
            temporary = ExampleDirectory("pi-durable-reload-");
            open = async () => await SqliteStorage.OpenAsync(Path.Combine(temporary, "session.sqlite"));
            storage = await open();
        }

        // First process.
        var registry = Registry.Create();
        registry.Install(LoadVersioned("v1"));
        var harness = await Harness.OpenAsync(storage, new HarnessOptions { Models = models, Registry = registry }, Context);
        // Selected by name. The name is what is stored, never the code.
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux, Extensions = ExtensionSelection.Only(LoadVersioned("v1")) });
        async Task<string> LastResult()
        {
            var page = await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context);
            return ResultText((JsonObjectValue)page.Items.First(entry => Entries.ToolResultEntry.Is(entry)).Model![0]);
        }

        // The file changes while a call runs: the running call finishes on v1, the next call uses v2.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate = release.Task;
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        started = () => running.TrySetResult();
        var submission = await root.SubmitAsync(SubmissionDraft.Input("Which version?"), Context);
        await running.Task;
        registry.Install(LoadVersioned("v2"));
        release.SetResult();
        await submission.WaitAsync(Context);
        await output.WriteLineAsync("call running during the reload: " + await LastResult());
        await (await root.SubmitAsync(SubmissionDraft.Input("And now?"), Context)).WaitAsync(Context);
        await output.WriteLineAsync("next call: " + await LastResult());
        await harness.CloseAsync(Context);

        // Second process: the conversation still selects "versioned", but this process has not installed it yet.
        registry = Registry.Create();
        harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry }, Context);
        root = await harness.RootAsync(Context);
        async Task<string> Tools() => Json(JsonValue.Array((await root.AgentAsync(Context)).Tools.Select(tool => (JsonValue)tool.Name)));
        await output.WriteLineAsync("after restart, before install: " + await Tools());
        registry.Install(LoadVersioned("v3"));
        await output.WriteLineAsync("after install: " + await Tools());

        await harness.CloseAsync(Context);
        if (temporary is not null) Directory.Delete(temporary, recursive: true);
    }
}
