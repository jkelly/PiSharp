using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;

// Milestone M1 (decision 0005), offline: the path samples/Durable.Chat runs against a real provider. NativeDurableModels sends the
// request through PiSharp.AI's Anthropic transport to a recording handler serving recorded SSE; a Harness turn on file storage commits
// the answer; the storage is reopened in a fresh Session and the answer is read back; a reopened Harness answers a second prompt with the
// first turn in the request it sends.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> M1Cases()
    {
        foreach (var kind in new[] { "sqlite", "jsonl" })
            yield return Case($"m1.{kind}.chat-turn-survives-reopen-and-continues", async () =>
            {
                var directory = Temp("m1-" + kind);
                async Task<IStorage> Open() => kind == "sqlite"
                    ? await SqliteStorage.OpenAsync(Path.Combine(directory, "session.sqlite"))
                    : await JsonlStorage.OpenAsync(directory, DurableContext.Background);
                var handler = new RecordingHandler(_ => Sse(AnthropicText));
                using var models = Models(new() { ["ANTHROPIC_API_KEY"] = "test-anthropic-key" }, handler);
                var registry = Registry.Create();
                registry.Install(Define.Extension(new Extension
                {
                    Name = "concise",
                    Sections = [Define.Section("preamble", "You are a concise assistant.", tag: false)],
                }));
                var context = DurableContext.Background;

                async Task<EntryId> TurnAsync(string prompt)
                {
                    var harness = await Harness.OpenAsync(await Open(), new HarnessOptions { Models = models, Registry = registry }, context);
                    try
                    {
                        var root = await harness.RootAsync(context, new AgentChange { Model = new ModelRef("anthropic", "claude-sonnet-4-5") });
                        harness.Resume();
                        var submission = await root.SubmitAsync(SubmissionDraft.Input(prompt), context);
                        var settled = await submission.WaitAsync(context);
                        Equal(SubmissionStatus.Done, settled.Status, prompt + ": status " + settled.Reason + " " + settled.Detail?.ToJson());
                        return settled.Answer ?? throw new InvalidOperationException(prompt + ": no answer");
                    }
                    finally { await harness.CloseAsync(context); }
                }

                var first = await TurnAsync("first question");
                var request = handler.Requests.Single();
                Equal("test-anthropic-key", request.Header("x-api-key"), "key from the injected environment");
                Check(request.Body.Contains("You are a concise assistant.", StringComparison.Ordinal), "preamble section sent: " + request.Body);
                Check(request.Body.Contains("first question", StringComparison.Ordinal), "prompt sent");

                // Reopened in a plain Session, the committed answer is there.
                var session = Session.Create(await Open());
                try
                {
                    var entry = await session.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, first), context);
                    Check(entry?.Model is { Count: 1 }, "answer entry with one model message");
                    Check(entry!.Model![0].ToJson().Contains("\"Hello there\"", StringComparison.Ordinal), "answer text: " + entry.Model![0].ToJson());
                }
                finally { await session.CloseAsync(context); }

                // A reopened Harness continues the same root conversation: the second request carries the first turn.
                var second = await TurnAsync("second question");
                Check(second.Value > first.Value, "second answer after the first");
                Equal(2, handler.Requests.Count, "two requests");
                var body = handler.Requests[1].Body;
                foreach (var part in new[] { "first question", "Hello there", "second question" })
                    Check(body.Contains(part, StringComparison.Ordinal), $"second request carries '{part}': {body}");
            });
    }
}
