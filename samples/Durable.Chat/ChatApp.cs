// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/examples/16-real-model.ts, with 17-coding-tools.ts's tools.
// Milestone M1 of decision 0005: a real model streams an answer through NativeDurableModels into a Harness on SQLite (by default); the
// storage is then closed, reopened and the stored answer entry printed, to show it survived. Milestone M2: with --tools the model can read,
// write and edit files and run bash commands in --cwd through LocalExecutionEnv; each tool call runs as a durable pi.tool task, and the turn
// ends with the model's final answer. --continue reopens an existing directory and shows its earlier turns first.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;

namespace PiSharp.Durable.Chat;

/// <summary>What one Durable.Chat run does.</summary>
public sealed record ChatOptions
{
    /// <summary>The provider id, such as <c>anthropic</c>.</summary>
    public required string Provider { get; init; }
    /// <summary>The model id.</summary>
    public required string ModelId { get; init; }
    /// <summary>The thinking level, if any.</summary>
    public string? Thinking { get; init; }
    /// <summary><c>memory</c>, <c>jsonl</c> or <c>sqlite</c>.</summary>
    public string Storage { get; init; } = "sqlite";
    /// <summary>The storage directory; a fresh temporary one when null.</summary>
    public string? Directory { get; init; }
    /// <summary>Reopen <see cref="Directory"/> and show its earlier turns first.</summary>
    public bool Continue { get; init; }
    /// <summary>The prompt; optional with <see cref="Continue"/>.</summary>
    public string? Prompt { get; init; }
    /// <summary>Offer the coding tools (read, write, edit, bash) over the local file system and shell in <see cref="Cwd"/>.</summary>
    public bool Tools { get; init; }
    /// <summary>The tools' working directory; the current directory when null.</summary>
    public string? Cwd { get; init; }
}

/// <summary>Durable.Chat: one prompt answered through a real provider, with optional coding tools, on durable storage.</summary>
public static class ChatApp
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>The command-line usage.</summary>
    public const string Usage = "Usage: Durable.Chat --provider <id> --model <id> [--thinking <level>] [--storage memory|jsonl|sqlite] [--dir <path>] " +
        "[--continue] [--tools [--cwd <path>]] [\"<prompt>\"]";

    /// <summary>Parses command-line arguments; null when they are invalid.</summary>
    public static ChatOptions? Parse(IReadOnlyList<string> args)
    {
        string? provider = null, modelId = null, directory = null, prompt = null, thinking = null, cwd = null;
        var storage = "sqlite";
        bool continueSession = false, tools = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--provider" when index + 1 < args.Count: provider = args[++index]; break;
                case "--model" when index + 1 < args.Count: modelId = args[++index]; break;
                case "--thinking" when index + 1 < args.Count: thinking = args[++index]; break;
                case "--storage" when index + 1 < args.Count: storage = args[++index]; break;
                case "--dir" when index + 1 < args.Count: directory = args[++index]; break;
                case "--cwd" when index + 1 < args.Count: cwd = args[++index]; break;
                case "--continue": continueSession = true; break;
                case "--tools": tools = true; break;
                case var text when !text.StartsWith("--", StringComparison.Ordinal) && prompt is null: prompt = text; break;
                default: return null;
            }
        }
        if (provider is null || modelId is null || storage is not ("memory" or "jsonl" or "sqlite") || (prompt is null && !continueSession) ||
            (cwd is not null && !tools)) return null;
        return new ChatOptions
        {
            Provider = provider, ModelId = modelId, Thinking = thinking, Storage = storage, Directory = directory, Continue = continueSession,
            Prompt = prompt, Tools = tools, Cwd = cwd,
        };
    }

    /// <summary>Runs one chat turn and prints it to <paramref name="output"/>; returns the exit code.</summary>
    /// <param name="options">What to run.</param>
    /// <param name="output">Where the turn prints.</param>
    /// <param name="models">Model access options: the environment reader for API keys and, in tests, the HTTP handler.</param>
    public static async Task<int> RunAsync(ChatOptions options, TextWriter output, NativeDurableModelsOptions models)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(models);
        if (options.Continue && (options.Directory is null || options.Storage == "memory"))
        {
            await output.WriteLineAsync("--continue reopens an existing --dir with jsonl or sqlite storage.");
            return 2;
        }

        // Production code passes model access with real providers; the Harness never talks to a provider any other way. NativeDurableModels
        // reads the provider's API key from its environment variable through the reader the host passes (upstream: openaiProvider() reads
        // OPENAI_API_KEY); the portable durable core never reads the process environment itself.
        var variables = NativeDurableModels.ApiKeyVariables(options.Provider);
        if (models.ResolveApiKey is null && variables.Count > 0 && variables.All(name => string.IsNullOrEmpty(models.Environment?.Invoke(name))))
        {
            await output.WriteLineAsync($"skipped: {string.Join(" or ", variables)} is not set");
            return 0;
        }
        using var access = new NativeDurableModels(models);
        var model = access.GetModel(options.Provider, options.ModelId);
        if (model is null)
        {
            await output.WriteLineAsync($"Unknown model {options.Provider}/{options.ModelId}.");
            return 2;
        }
        if (!NativeDurableModels.Supports(model))
        {
            await output.WriteLineAsync($"{options.Provider}/{options.ModelId} ({model.Api}) has no durable route yet; see decision 0005, \"Model adapter\".");
            return 2;
        }

        var directory = Path.GetFullPath(options.Directory ?? Path.Combine(Path.GetTempPath(), "pisharp-durable-chat", Guid.NewGuid().ToString("N")));
        if (options.Storage != "memory") await output.WriteLineAsync($"storage: {options.Storage} under {directory}");
        var cwd = Path.GetFullPath(options.Cwd ?? System.IO.Directory.GetCurrentDirectory());
        if (options.Tools) await output.WriteLineAsync($"tools: read, write, edit, bash in {cwd}");

        var registry = Registry.Create();
        registry.Install(Define.Extension(new Extension
        {
            Name = "concise",
            Sections =
            [
                Define.Section("preamble", options.Tools
                    ? "You are a concise coding assistant. Use the tools to read and change files and to run commands in the working directory."
                    : "You are a concise assistant.", tag: false),
                // With tools, the model sees the working directory of the environment built for each request.
                Define.Section("cwd", (input, _) => ValueTask.FromResult(options.Tools ? input.Env?.Cwd : null)),
            ],
        }));
        // CodingTools brings read, write, edit and bash; they reach files and processes only through the environment the Harness builds for
        // each call, which follows the conversation's agent cwd.
        if (options.Tools) registry.Install(CodingTools.Extension);

        async Task<IStorage> OpenStorageAsync() => options.Storage switch
        {
            "memory" => new MemoryStorage(),
            "jsonl" => await JsonlStorage.OpenAsync(System.IO.Directory.CreateDirectory(directory).FullName, Context),
            _ => await SqliteStorage.OpenAsync(Path.Combine(System.IO.Directory.CreateDirectory(directory).FullName, "session.sqlite")),
        };

        var harness = await Harness.OpenAsync(await OpenStorageAsync(), new HarnessOptions
        {
            Models = access,
            Registry = registry,
            Env = options.Tools ? (target, _) => Task.FromResult<IExecutionEnv?>(new LocalExecutionEnv(target.Cwd ?? cwd)) : null,
        }, Context);
        EntryId? answerId = null;
        var toolResults = 0;
        var exitCode = 0;
        try
        {
            var root = await harness.RootAsync(Context, new AgentChange
            {
                Model = new ModelRef(options.Provider, options.ModelId),
                ThinkingLevel = options.Thinking is null ? (Patch<string>?)null : Patch<string>.Set(options.Thinking),
                Cwd = options.Tools ? Patch<string>.Set(cwd) : (Patch<string>?)null,
            });
            if (options.Continue) await PrintTurnsAsync(root, output);
            if (options.Prompt is { } prompt)
            {
                // While the answer streams, generation commits throttled partials to the conversation's pi.live document; watching it streams
                // the answer and shows each tool call as it starts. The watch sees only committed values.
                var liveWatch = await harness.WatchDocAsync(Live.LiveDoc, root.Id, Context) ?? throw new InvalidOperationException("pi.live is absent");
                var printer = new LivePrinter(output);
                liveWatch.Start((value, _, _) =>
                {
                    printer.Show(value);
                    return Task.CompletedTask;
                });
                harness.Resume();
                await output.WriteAsync("answer: ");
                var submission = await root.SubmitAsync(SubmissionDraft.Input(prompt), Context);
                var settled = await submission.WaitAsync(Context);
                await liveWatch.StopAsync();
                if (settled.Status == SubmissionStatus.Done && settled.Type == SubmissionType.Input && settled.Answer is { } answer)
                {
                    // The last throttle window may not have been committed as a partial; the answer entry has the rest.
                    var entry = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), Context);
                    if (entry?.Model is { Count: > 0 } messages && FirstText(messages[0]) is { } text) printer.Text(text);
                    await output.WriteLineAsync();
                    answerId = answer;
                    toolResults = await PrintToolResultsAsync(root, output);
                }
                else
                {
                    await output.WriteLineAsync();
                    await output.WriteLineAsync($"unanswered: {settled.Reason} {settled.Detail?.ToJson()}");
                    exitCode = 1;
                }
            }
        }
        finally
        {
            await harness.CloseAsync(Context);
        }

        // Reopen the storage in a fresh Session and read the answer back: it survived close.
        if (answerId is { } stored)
        {
            if (options.Storage == "memory") await output.WriteLineAsync("memory storage: nothing survives close");
            else
            {
                var session = Session.Create(await OpenStorageAsync());
                try
                {
                    var entry = await session.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, stored), Context);
                    await output.WriteLineAsync($"after reopen: entry {stored.Value} ({entry?.Kind ?? "missing"}): " +
                        (entry?.Model is { Count: > 0 } m ? FirstText(m[0]) : null));
                    if (options.Tools)
                    {
                        var page = await session.CommitAsync(tx => tx.ScanEntriesAsync(new EntryQuery(ConversationId.Root), 256), Context);
                        await output.WriteLineAsync($"after reopen: {page.Items.Count(item => Entries.ToolResultEntry.Is(item))} tool results " +
                            $"(this turn: {toolResults})");
                    }
                }
                finally
                {
                    await session.CloseAsync(Context);
                }
            }
        }
        return exitCode;
    }

    /// <summary>Prints the live answer text as it grows and each tool call as it starts.</summary>
    private sealed class LivePrinter(TextWriter output)
    {
        private readonly Lock gate = new();
        private readonly HashSet<string> calls = new(StringComparer.Ordinal);
        private string printed = "";

        public void Show(JsonObjectValue? value)
        {
            if (value is null) return;
            if (value.TryGetValue("generation", out var generation) && generation is JsonObjectValue live &&
                live.TryGetValue("message", out var message) && FirstText(message) is { } text) Text(text);
            if (value.TryGetValue("tools", out var tools) && tools is JsonArrayValue slots)
                foreach (var slot in slots.OfType<JsonObjectValue>())
                    if (slot.TryGetValue("callId", out var callId) && callId is JsonStringValue id && slot.TryGetValue("name", out var name) &&
                        name is JsonStringValue tool) Call(id.Value, tool.Value);
        }

        /// <summary>Prints what <paramref name="text"/> adds to the text printed so far; a text that does not extend it belongs to the next
        /// generation of the run and starts on a new line.</summary>
        public void Text(string text)
        {
            lock (gate)
            {
                if (text.Length == 0 || text == printed) return;
                if (text.StartsWith(printed, StringComparison.Ordinal)) output.Write(text[printed.Length..]);
                else if (printed.StartsWith(text, StringComparison.Ordinal)) return;
                else
                {
                    output.WriteLine();
                    output.Write(text);
                }
                printed = text;
            }
        }

        private void Call(string callId, string name)
        {
            lock (gate)
            {
                if (!calls.Add(callId)) return;
                output.WriteLine();
                output.Write($"[{name}]");
                printed = "";
            }
        }
    }

    /// <summary>The tool results of the turn just answered, oldest first: each tool, whether it failed, and its first line.</summary>
    private static async Task<int> PrintToolResultsAsync(IConversation root, TextWriter output)
    {
        var page = await root.EntriesAsync(new EntryQueryBounds(), 256, null, Context);
        var results = new List<JsonObjectValue>();
        foreach (var entry in page.Items)
        {
            if (entry.Kind == "pi.user") break;
            if (Entries.ToolResultEntry.Is(entry) && entry.Model is { Count: > 0 } model) results.Add((JsonObjectValue)model[0]);
        }
        results.Reverse();
        foreach (var result in results)
        {
            var text = FirstText(result) ?? "";
            var firstLine = text.Split('\n')[0];
            var failed = result.TryGetValue("isError", out var isError) && isError is JsonBooleanValue { Value: true };
            await output.WriteLineAsync($"tool {((JsonStringValue)result["toolName"]).Value} {(failed ? "error" : "ok")}: {firstLine}");
        }
        return results.Count;
    }

    // Earlier user and assistant turns of the root conversation, oldest first.
    private static async Task PrintTurnsAsync(IConversation root, TextWriter output)
    {
        JsonObjectValue? cursor = null;
        var turns = 0;
        do
        {
            var page = await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 256, cursor, Context);
            foreach (var entry in page.Items)
            {
                if (entry.Kind is not ("pi.user" or "pi.assistant") || entry.Model is not { Count: > 0 } messages) continue;
                await output.WriteLineAsync($"{(entry.Kind == "pi.user" ? "user" : "assistant")}: {FirstText(messages[0]) ?? "(no text)"}");
                turns++;
            }
            cursor = page.Next;
        } while (cursor is not null);
        if (turns == 0) await output.WriteLineAsync("(no earlier turns)");
    }

    // The text of a pi-ai message: string content, or its first text block.
    private static string? FirstText(JsonValue message)
    {
        if (message is not JsonObjectValue body || !body.TryGetValue("content", out var content)) return null;
        if (content is JsonStringValue text) return text.Value;
        if (content is not JsonArrayValue blocks) return null;
        foreach (var block in blocks)
            if (block is JsonObjectValue item && item.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" } &&
                item.TryGetValue("text", out var value) && value is JsonStringValue blockText) return blockText.Value;
        return null;
    }
}
