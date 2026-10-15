// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/examples/14-chat.ts and 15-system-prompt.ts. Upstream stands in for a
// model with pi-ai's faux provider; here ScriptedModels does (see ScriptedModels.cs), so the examples run offline. Each prints what
// upstream's console.log prints, as JSON. On a file backend the storage is then reopened in a plain Session and the durable transcript is
// read back.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools.Env;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    private static readonly ModelRef Faux = new("faux", "faux-1");

    private static async Task<List<EntryRecord>> TranscriptAsync(ISession session, ConversationId conversation, int limit)
    {
        var page = await session.CommitAsync(tx => tx.ScanEntriesAsync(new EntryQuery(conversation), limit), Context);
        return [.. page.Items.Reverse()];
    }

    private static string Kinds(IEnumerable<EntryRecord> entries) => Json(JsonValue.Array(entries.Select(entry => (JsonValue)entry.Kind)));

    // 14: a chat turn.
    private static async Task ChatAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // ScriptedModels stands in for a real model: it answers each request with the next scripted text.
        var models = new ScriptedModels("Paris.");
        var registry = Registry.Create();
        registry.Install(Define.Extension(new Extension
        {
            Name = "terse",
            Sections = [Define.Section("preamble", "You answer in one word.", tag: false)],
        }));
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });

        // SubmitAsync durably admits user input and returns a submission. The built-in pi.generation task prepares the system prompt from
        // the agent's sections, calls the model, and appends the answer; WaitAsync resolves once the input is answered or has failed.
        var capital = await root.SubmitAsync(SubmissionDraft.Input("Capital of France?"), Context);
        var answered = await capital.WaitAsync(Context);
        if (answered is { Status: SubmissionStatus.Done, Type: SubmissionType.Input, Answer: { } answer })
        {
            var entry = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), Context);
            var reply = entry?.Model?[0] as JsonObjectValue;
            await output.WriteLineAsync("answer: " + Json(reply?["role"] is JsonStringValue { Value: "assistant" } ? reply["content"] : reply));
        }

        // The transcript holds the user input, the positional system prompt, and the answer.
        var transcript = await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context);
        await output.WriteLineAsync("transcript: " + Kinds(transcript.Items.Reverse()));
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            var entries = await TranscriptAsync(reopened, root.Id, 10);
            await output.WriteLineAsync("transcript: " + Kinds(entries));
            await output.WriteLineAsync("answer: " + Json(((JsonObjectValue)entries[^1].Model![0])["content"]));
        });
    }

    // 15: system prompt sections and per-conversation instructions.
    private static async Task SystemPromptAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var models = new ScriptedModels("Done.", "Done.", "Done.");

        // Pi stores no prompt state. Before each model request, the sections of the conversation's selected extensions render the desired
        // prompt, and only the difference to what the model already saw is appended to the transcript as a pi.system entry.
        var coding = new Extension
        {
            Name = "coding",
            Sections =
            [
                // Tag false sends the text as is; by default it is wrapped in <key>...</key>.
                Define.Section("preamble", "You are a coding agent.", tag: false),
                // Sections see the environment built for this request, here its working directory.
                Define.Section("cwd", (input, _) => ValueTask.FromResult(input.Env?.Cwd)),
            ],
        };
        var agentsMd = new Extension { Name = "agents-md", Sections = [Define.Section("agents_md", "Run npm run check after changes.")] };
        // Another extension decorates a section by key without replacing it.
        var terse = new Extension
        {
            Name = "terse",
            Wraps = [Define.WrapSection("preamble", preamble => preamble with
            {
                Render = async (input, renderContext) => $"{await preamble.Render(input, renderContext)} Be terse.",
            })],
        };

        var registry = Registry.Create();
        registry.Install(coding);
        registry.Install(agentsMd);
        registry.Install(terse);
        // The environment follows each conversation's agent cwd.
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = models,
            Registry = registry,
            Env = (target, _) => Task.FromResult<IExecutionEnv?>(new ExampleEnv(target.Cwd ?? "/")),
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux, Cwd = "/repo" });

        // A subagent deselects AGENTS.md and gets its own instructions, rendered last as the `instructions` section.
        var subagent = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless)
        {
            Agent = new AgentChange
            {
                Model = Faux,
                Cwd = "/repo",
                Extensions = ExtensionSelection.Edit(remove: [agentsMd]),
                Instructions = "Only read; never edit files.",
            },
        }, Context);

        async Task<string> SystemEntriesAsync(ISession session, ConversationId conversation) =>
            Json(JsonValue.Array((await TranscriptAsync(session, conversation, 20))
                .Where(entry => Entries.SystemEntry.Is(entry)).SelectMany(entry => entry.Model ?? JsonArrayValue.Empty)));

        await (await root.SubmitAsync(SubmissionDraft.Input("Fix the build."), Context)).WaitAsync(Context);
        await (await subagent.SubmitAsync(SubmissionDraft.Input("Read the logs."), Context)).WaitAsync(Context);
        await output.WriteLineAsync("root system prompt: " + await SystemEntriesAsync(harness, root.Id));
        await output.WriteLineAsync("subagent system prompt: " + await SystemEntriesAsync(harness, subagent.Id));

        // When a section's output changes, the next request appends only the change.
        await root.ConfigureAsync(new AgentChange { Cwd = "/repo/packages" }, Context);
        await (await root.SubmitAsync(SubmissionDraft.Input("Now the package."), Context)).WaitAsync(Context);
        await output.WriteLineAsync("root system entries after cwd change: " + await SystemEntriesAsync(harness, root.Id));

        await harness.CloseAsync(Context);
        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("root system entries after cwd change: " + await SystemEntriesAsync(reopened, root.Id)));
    }

    /// <summary>
    /// The example environment: a local execution environment whose working directory is the agent's <c>cwd</c> as given (upstream's
    /// <c>new NodeExecutionEnv({ cwd })</c> keeps POSIX paths as they are; LocalFileSystem resolves them against the current drive on
    /// Windows, so this environment reports the logical path and runs file operations and commands through LocalExecutionEnv).
    /// </summary>
    private sealed class ExampleEnv(string cwd) : IExecutionEnv
    {
        private readonly LocalExecutionEnv files = new(Directory.GetCurrentDirectory());
        public string Id => files.Id;
        public string Cwd { get; set; } = cwd;
        public Task<Result<string, FileError>> AbsolutePathAsync(string path, DurableContext context) => files.AbsolutePathAsync(path, context);
        public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, DurableContext context) => files.JoinPathAsync(parts, context);
        public Task<Result<string, FileError>> ReadTextFileAsync(string path, DurableContext context) => files.ReadTextFileAsync(path, context);
        public Task<Result<ITextLineReader, FileError>> OpenTextLineReaderAsync(string path, DurableContext context) => files.OpenTextLineReaderAsync(path, context);
        public Task<Result<System.Collections.Immutable.ImmutableArray<string>, FileError>> ReadTextLinesAsync(string path, ReadTextLinesOptions? options,
            DurableContext context) => files.ReadTextLinesAsync(path, options, context);
        public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, DurableContext context) => files.ReadBinaryFileAsync(path, context);
        public Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(string path, BinaryReaderOptions? options, DurableContext context) =>
            files.OpenBinaryReaderAsync(path, options, context);
        public Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) =>
            files.WriteFileAsync(path, content, context);
        public Task<Result<Unit, FileError>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) =>
            files.AppendFileAsync(path, content, context);
        public Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, DurableContext context) => files.TruncateFileAsync(path, size, context);
        public Task<Result<Unit, FileError>> FlushFileAsync(string path, DurableContext context) => files.FlushFileAsync(path, context);
        public Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, DurableContext context) =>
            files.RenameFileAsync(sourcePath, destinationPath, context);
        public Task<Result<FileMetadata, FileError>> FileInfoAsync(string path, DurableContext context) => files.FileInfoAsync(path, context);
        public Task<Result<System.Collections.Immutable.ImmutableArray<FileMetadata>, FileError>> ListDirAsync(string path, DurableContext context) =>
            files.ListDirAsync(path, context);
        public Task<Result<IDirReader, FileError>> OpenDirReaderAsync(string path, DurableContext context) => files.OpenDirReaderAsync(path, context);
        public Task<Result<string, FileError>> CanonicalPathAsync(string path, DurableContext context) => files.CanonicalPathAsync(path, context);
        public Task<Result<bool, FileError>> ExistsAsync(string path, DurableContext context) => files.ExistsAsync(path, context);
        public Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, DurableContext context) =>
            files.CreateDirAsync(path, options, context);
        public Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, DurableContext context) => files.RemoveAsync(path, options, context);
        public Task<Result<IFileWatcher, FileError>> WatchAsync(IReadOnlyList<WatchTarget> targets, Action<WatchChange> onChange, DurableContext context) =>
            files.WatchAsync(targets, onChange, context);
        public Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, DurableContext context) => files.CreateTempDirAsync(prefix, context);
        public Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, DurableContext context) => files.CreateTempFileAsync(options, context);
        public Task<Result<ShellExecResult, ExecutionError>> ExecAsync(ShellCommand command, ShellExecOptions? options, DurableContext context) =>
            files.ExecAsync(command, options, context);
        public Task CleanupAsync(DurableContext context) => files.CleanupAsync(context);
    }
}
