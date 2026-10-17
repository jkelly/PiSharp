using System.Text.Json;
using PiSharp.Durable;
using PiSharp.Durable.Chat;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using static SessionTests;
using JsonValueKind = System.Text.Json.JsonValueKind;

// Milestone M2 (decision 0005), offline: the path samples/Durable.Chat --tools runs against a real provider. ChatApp sends each request
// through NativeDurableModels and PiSharp.AI's Anthropic transport to a recording handler serving recorded SSE: tool_use answers that read
// notes.txt, edit it and run `cat notes.txt` in bash, then a final text answer. The turn runs on SQLite in a temporary directory with the
// coding tools over LocalExecutionEnv in another; every tool result and the answer are committed, the storage is reopened in a plain
// Session and read back, and a second ChatApp run with --continue resumes the conversation with the whole tool turn in its request.
internal static partial class Program
{
    private static string AnthropicToolUse(string id, string name, string inputJson, int message) =>
        $"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{{\"id\":\"msg_m2_{message}\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[],\"stop_reason\":null,\"usage\":{{\"input_tokens\":40,\"output_tokens\":1}}}}}}\n\n" +
        $"event: content_block_start\ndata: {{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{{\"type\":\"text\",\"text\":\"\"}}}}\n\n" +
        $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"Using {name}.\"}}}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        $"event: content_block_start\ndata: {{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{{\"type\":\"tool_use\",\"id\":\"{id}\",\"name\":\"{name}\",\"input\":{{}}}}}}\n\n" +
        $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{JsonSerializer.Serialize(inputJson[..(inputJson.Length / 2)])}}}}}\n\n" +
        $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{JsonSerializer.Serialize(inputJson[(inputJson.Length / 2)..])}}}}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":30}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    private static string AnthropicAnswer(string text, int message) =>
        $"event: message_start\ndata: {{\"type\":\"message_start\",\"message\":{{\"id\":\"msg_m2_{message}\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[],\"stop_reason\":null,\"usage\":{{\"input_tokens\":80,\"output_tokens\":1}}}}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":{JsonSerializer.Serialize(text)}}}}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":12}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    /// <summary>The <c>tool_result</c> blocks of an Anthropic request body: tool_use_id and the result text.</summary>
    private static List<(string Id, string Text, bool IsError)> ToolResultsOf(JsonElement body)
    {
        var results = new List<(string, string, bool)>();
        foreach (var message in body.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("content").ValueKind != JsonValueKind.Array) continue;
            foreach (var block in message.GetProperty("content").EnumerateArray())
            {
                if (block.GetProperty("type").GetString() != "tool_result") continue;
                var content = block.GetProperty("content");
                var text = content.ValueKind == JsonValueKind.String ? content.GetString()!
                    : string.Concat(content.EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));
                results.Add((block.GetProperty("tool_use_id").GetString()!, text,
                    block.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True));
            }
        }
        return results;
    }

    private static IEnumerable<(string, Func<Task>)> M2Cases()
    {
        yield return Case("m2.sqlite.tool-turn-reads-edits-runs-and-answers-then-survives-reopen", async () =>
        {
            var directory = Temp("m2-storage");
            var work = Temp("m2-work");
            try
            {
                await File.WriteAllTextAsync(Path.Combine(work, "notes.txt"), "hello world\n");
                string[] streams =
                [
                    AnthropicToolUse("toolu_m2_read", "read", """{"path":"notes.txt"}""", 1),
                    AnthropicToolUse("toolu_m2_edit", "edit", """{"path":"notes.txt","edits":[{"oldText":"world","newText":"durable"}]}""", 2),
                    AnthropicToolUse("toolu_m2_bash", "bash", """{"command":"cat notes.txt"}""", 3),
                    AnthropicAnswer("notes.txt now says hello durable.", 4),
                    AnthropicAnswer("It still says hello durable.", 5),
                ];
                var served = 0;
                var handler = new RecordingHandler(_ => Sse(streams[Interlocked.Increment(ref served) - 1]));
                var models = new NativeDurableModelsOptions
                {
                    Environment = Env(new() { ["ANTHROPIC_API_KEY"] = "test-anthropic-key" }),
                    HttpHandler = handler,
                };
                var options = ChatApp.Parse(["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--dir", directory, "--tools", "--cwd", work,
                    "Change world to durable in notes.txt and show the file."])!;
                Equal("sqlite", options.Storage, "default storage");
                var output = new StringWriter();
                Equal(0, await ChatApp.RunAsync(options, output, models), "exit code: " + output);
                var printed = output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);

                // The tools ran for real in the working directory.
                Equal("hello durable\n", await File.ReadAllTextAsync(Path.Combine(work, "notes.txt")), "notes.txt");
                Equal(4, handler.Requests.Count, "requests");
                Equal("test-anthropic-key", handler.Requests[0].Header("x-api-key"), "key from the injected environment");

                // The first request declares the coding tools as upstream declares them.
                var tools = handler.Requests[0].Json.GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
                Equal("bash,edit,read,write", string.Join(",", tools.Keys.Order(StringComparer.Ordinal)), "declared tools");
                // Anthropic's input_schema is {type, properties, required}, as pi-ai builds it from the declared parameters.
                foreach (var (name, tool) in tools)
                {
                    var declared = (JsonObjectValue)JsonValue.Parse(CodingToolsParameters(name));
                    var schema = (JsonObjectValue)JsonValue.FromElement(tool.GetProperty("input_schema"));
                    Equal(declared["properties"].ToJson(), schema["properties"].ToJson(), name + " schema properties");
                    Equal(declared["required"].ToJson(), schema["required"].ToJson(), name + " schema required");
                }
                Contains(handler.Requests[0].Body, "You are a concise coding assistant.", "preamble");
                Contains(handler.Requests[0].Body, $"<cwd>\\n{JsonEncodedText.Encode(Path.GetFullPath(work))}\\n</cwd>", "cwd section");

                // Each following request carries the previous tool results.
                var afterRead = ToolResultsOf(handler.Requests[1].Json);
                Equal(1, afterRead.Count, "results in request 2");
                Equal(("toolu_m2_read", "hello world\n", false), afterRead[0], "read result");
                var afterBash = ToolResultsOf(handler.Requests[3].Json);
                Equal("toolu_m2_read|toolu_m2_edit|toolu_m2_bash", string.Join("|", afterBash.Select(result => result.Id)), "results in request 4");
                Equal(("toolu_m2_edit", "Successfully replaced 1 block(s) in notes.txt.", false), afterBash[1], "edit result");
                Equal(("toolu_m2_bash", "hello durable\n", false), afterBash[2], "bash result");

                // What the sample printed: tool calls as they started, the answer, the tool summary, and the read-back after reopen.
                foreach (var part in new[] { "[read]", "[edit]", "[bash]", "notes.txt now says hello durable.", "tool read ok: hello world",
                    "tool edit ok: Successfully replaced 1 block(s) in notes.txt.", "tool bash ok: hello durable",
                    "(pi.assistant): notes.txt now says hello durable.", "after reopen: 3 tool results (this turn: 3)" })
                    Contains(printed, part, "printed");

                // Reopened in a plain Session, the whole tool turn is committed in order.
                var session = Session.Create(await SqliteStorage.OpenAsync(Path.Combine(directory, "session.sqlite")));
                try
                {
                    var page = await session.CommitAsync(tx => tx.ScanEntriesAsync(new EntryQuery(ConversationId.Root), 64), DurableContext.Background);
                    var entries = page.Items.Reverse().ToList();
                    Equal("pi.user,pi.system,pi.assistant,pi.tool-result,pi.assistant,pi.tool-result,pi.assistant,pi.tool-result,pi.assistant",
                        string.Join(",", entries.Select(entry => entry.Kind)), "kinds");
                    var results = entries.Where(entry => entry.Kind == "pi.tool-result").Select(entry => (JsonObjectValue)entry.Model![0]).ToList();
                    Equal("read:hello world\n|edit:Successfully replaced 1 block(s) in notes.txt.|bash:hello durable\n",
                        string.Join("|", results.Select(result => $"{S(result["toolName"])}:{ResultBlocksText(result)}")), "results");
                    Check(results.All(result => result["isError"] is JsonBooleanValue { Value: false }), "no error results");
                    var tasks = await session.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = ConversationId.Root, Kind = "pi.tool" }, 20),
                        DurableContext.Background);
                    Equal(3, tasks.Items.Count(), "tool tasks");
                    Check(tasks.Items.All(task => task.State.ToJson().ToJson().Contains("\"status\":\"terminal\"", StringComparison.Ordinal) &&
                        task.State.ToJson().ToJson().Contains("\"completed\"", StringComparison.Ordinal)), "every tool task completed: " +
                        string.Join(", ", tasks.Items.Select(task => task.State.ToJson().ToJson())));
                }
                finally { await session.CloseAsync(DurableContext.Background); }

                // A reopened Harness continues the conversation: the next request carries the whole tool turn.
                var resumed = new StringWriter();
                Equal(0, await ChatApp.RunAsync(ChatApp.Parse(["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--dir", directory, "--continue",
                    "--tools", "--cwd", work, "Does it still say that?"])!, resumed, models), "resumed exit code: " + resumed);
                Contains(resumed.ToString(), "assistant: notes.txt now says hello durable.", "earlier turn shown");
                Contains(resumed.ToString(), "It still says hello durable.", "second answer");
                Equal(5, handler.Requests.Count, "requests after resume");
                Equal("toolu_m2_read|toolu_m2_edit|toolu_m2_bash", string.Join("|", ToolResultsOf(handler.Requests[4].Json).Select(result => result.Id)),
                    "the resumed request carries the tool turn");
            }
            finally
            {
                await RemoveTempDirAsync(work);
                await RemoveTempDirAsync(directory);
            }
        });
    }

    private static string CodingToolsParameters(string name) =>
        PiSharp.Durable.Tools.CodingTools.Extension.Tools!.Single(tool => tool.Name == name).Parameters.ToJson();

    private static string ResultBlocksText(JsonObjectValue result) =>
        string.Concat(((JsonArrayValue)result["content"]).Cast<JsonObjectValue>().Where(part => S(part["type"]) == "text").Select(part => S(part["text"])));
}
