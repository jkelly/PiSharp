// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/examples/16-real-model.ts. Milestone M1 of decision 0005: a
// real model streams an answer through NativeDurableModels into a Harness on SQLite (by default); the storage is then closed, reopened
// and the stored answer entry printed, to show it survived. --continue reopens an existing directory and shows its earlier turns first.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;

const string Usage = "Usage: Durable.Chat --provider <id> --model <id> [--thinking <level>] [--storage memory|jsonl|sqlite] [--dir <path>] " +
    "[--continue] [\"<prompt>\"]";
var context = DurableContext.Background;

string? provider = null, modelId = null, directory = null, prompt = null, thinking = null;
var storageKind = "sqlite";
var continueSession = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--provider" when index + 1 < args.Length: provider = args[++index]; break;
        case "--model" when index + 1 < args.Length: modelId = args[++index]; break;
        case "--thinking" when index + 1 < args.Length: thinking = args[++index]; break;
        case "--storage" when index + 1 < args.Length: storageKind = args[++index]; break;
        case "--dir" when index + 1 < args.Length: directory = args[++index]; break;
        case "--continue": continueSession = true; break;
        case var text when !text.StartsWith("--", StringComparison.Ordinal) && prompt is null: prompt = text; break;
        default: Console.Error.WriteLine(Usage); return 2;
    }
}
if (provider is null || modelId is null || storageKind is not ("memory" or "jsonl" or "sqlite") || (prompt is null && !continueSession))
{
    Console.Error.WriteLine(Usage);
    return 2;
}
if (continueSession && (directory is null || storageKind == "memory"))
{
    Console.Error.WriteLine("--continue reopens an existing --dir with jsonl or sqlite storage.");
    return 2;
}

// Production code passes model access with real providers; the Harness never talks to a provider any other way. NativeDurableModels
// reads the provider's API key from its environment variable through the reader the host passes (upstream: openaiProvider() reads
// OPENAI_API_KEY); the portable durable core never reads the process environment itself.
var variables = NativeDurableModels.ApiKeyVariables(provider);
if (variables.Count > 0 && variables.All(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
{
    Console.WriteLine($"skipped: {string.Join(" or ", variables)} is not set");
    return 0;
}
using var models = new NativeDurableModels(new() { Environment = Environment.GetEnvironmentVariable });
var model = models.GetModel(provider, modelId);
if (model is null)
{
    Console.Error.WriteLine($"Unknown model {provider}/{modelId}.");
    return 2;
}
if (!NativeDurableModels.Supports(model))
{
    Console.Error.WriteLine($"{provider}/{modelId} ({model.Api}) has no durable route yet; see decision 0005, \"Model adapter\".");
    return 2;
}

directory = Path.GetFullPath(directory ?? Path.Combine(Path.GetTempPath(), "pisharp-durable-chat", Guid.NewGuid().ToString("N")));
if (storageKind != "memory") Console.WriteLine($"storage: {storageKind} under {directory}");

var registry = Registry.Create();
registry.Install(Define.Extension(new Extension
{
    Name = "concise",
    Sections = [Define.Section("preamble", "You are a concise assistant.", tag: false)],
}));

var harness = await Harness.OpenAsync(await OpenStorageAsync(), new HarnessOptions { Models = models, Registry = registry }, context);
EntryId? answerId = null;
var exitCode = 0;
try
{
    var root = await harness.RootAsync(context, new AgentChange
    {
        Model = new ModelRef(provider, modelId),
        ThinkingLevel = thinking is null ? (Patch<string>?)null : Patch<string>.Set(thinking),
    });
    if (continueSession) await PrintTurnsAsync(root);
    if (prompt is not null)
    {
        // While the answer streams, generation commits throttled partials to the conversation's pi.live document; watching it streams
        // the answer. The watch sees only committed values.
        var liveWatch = await harness.WatchDocAsync(Live.LiveDoc, root.Id, context) ?? throw new InvalidOperationException("pi.live is absent");
        var printed = "";
        void PrintText(string text)
        {
            if (text.Length <= printed.Length || !text.StartsWith(printed, StringComparison.Ordinal)) return;
            Console.Write(text[printed.Length..]);
            printed = text;
        }
        liveWatch.Start((value, _, _) =>
        {
            if (value is not null && value.TryGetValue("generation", out var generation) && generation is JsonObjectValue live &&
                live.TryGetValue("message", out var message) && FirstText(message) is { } text) PrintText(text);
            return Task.CompletedTask;
        });
        harness.Resume();
        Console.Write("answer: ");
        var submission = await root.SubmitAsync(SubmissionDraft.Input(prompt), context);
        var settled = await submission.WaitAsync(context);
        await liveWatch.StopAsync();
        if (settled.Status == SubmissionStatus.Done && settled.Type == SubmissionType.Input && settled.Answer is { } answer)
        {
            // The last throttle window may not have been committed as a partial; the answer entry has the rest.
            var entry = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), context);
            if (entry?.Model is { Count: > 0 } messages && FirstText(messages[0]) is { } text) PrintText(text);
            Console.WriteLine();
            answerId = answer;
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"unanswered: {settled.Reason} {settled.Detail?.ToJson()}");
            exitCode = 1;
        }
    }
}
finally
{
    await harness.CloseAsync(context);
}

// Reopen the storage in a fresh Session and read the answer back: it survived close.
if (answerId is { } stored)
{
    if (storageKind == "memory") Console.WriteLine("memory storage: nothing survives close");
    else
    {
        var session = Session.Create(await OpenStorageAsync());
        try
        {
            var entry = await session.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, stored), context);
            Console.WriteLine($"after reopen: entry {stored.Value} ({entry?.Kind ?? "missing"}): {(entry?.Model is { Count: > 0 } m ? FirstText(m[0]) : null)}");
        }
        finally
        {
            await session.CloseAsync(context);
        }
    }
}
return exitCode;

async Task<IStorage> OpenStorageAsync() => storageKind switch
{
    "memory" => new MemoryStorage(),
    "jsonl" => await JsonlStorage.OpenAsync(Directory.CreateDirectory(directory!).FullName, context),
    _ => await SqliteStorage.OpenAsync(Path.Combine(Directory.CreateDirectory(directory!).FullName, "session.sqlite")),
};

// Earlier user and assistant turns of the root conversation, oldest first.
async Task PrintTurnsAsync(IConversation root)
{
    JsonObjectValue? cursor = null;
    var turns = 0;
    do
    {
        var page = await root.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, 256, cursor, context);
        foreach (var entry in page.Items)
        {
            if (entry.Kind is not ("pi.user" or "pi.assistant") || entry.Model is not { Count: > 0 } messages) continue;
            Console.WriteLine($"{(entry.Kind == "pi.user" ? "user" : "assistant")}: {FirstText(messages[0]) ?? "(no text)"}");
            turns++;
        }
        cursor = page.Next;
    } while (cursor is not null);
    if (turns == 0) Console.WriteLine("(no earlier turns)");
}

// The text of a pi-ai message: string content, or its first text block.
static string? FirstText(JsonValue message)
{
    if (message is not JsonObjectValue body || !body.TryGetValue("content", out var content)) return null;
    if (content is JsonStringValue text) return text.Value;
    if (content is not JsonArrayValue blocks) return null;
    foreach (var block in blocks)
        if (block is JsonObjectValue item && item.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" } &&
            item.TryGetValue("text", out var value) && value is JsonStringValue blockText) return blockText.Value;
    return null;
}
