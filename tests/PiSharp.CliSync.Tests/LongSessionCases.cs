using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;

// Long sessions: Pi v1.1.0 caps no transcript message count or session entry count (packages/agent/src/agent-loop.ts,
// packages/ai/src/api/anthropic-messages.ts convertMessages, coding-agent core/session-manager.ts); the request is bounded by
// the model's context, which compaction maintains. A session of about 2,000 context messages, including 260 loadout system
// records from 130 tool enable/disable cycles, and more than 10,000 records in all, is opened by the production RPC host
// (RpcSessionCommand → OfflineSessionProfile → LiveSessionSelection) and its next prompt sends the whole transcript.
// Authored expectations; fake endpoint; no network.
internal static partial class Program
{
    private static Task LiveLongSessionOpensAndPrompts() => WithLiveRoot("live-long-session", async root =>
    {
        const int Turns = 435, Cycles = 130, CustomPerTurn = 20;
        var path = Path.Combine(root, "session.jsonl"); var lines = new StringBuilder(); var markers = new List<string>();
        var count = 0; string? parent = null; long time = 1_800_000_000_000;
        void Add(string type, string fields)
        {
            var id = "e" + (++count).ToString("x7");
            lines.Append("{\"type\":\"").Append(type).Append("\",\"id\":\"").Append(id).Append("\",\"parentId\":").Append(parent is null ? "null" : "\"" + parent + "\"")
                .Append(",\"timestamp\":\"").Append(DateTimeOffset.FromUnixTimeMilliseconds(time).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Append("\",").Append(fields).Append("}\n");
            parent = id;
        }
        void Message(string message) => Add("message", "\"message\":" + message);
        static string Declaration(string name) => JsonSerializer.Serialize(new { name, description = "Tool " + name,
            parameters = new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } } });
        lines.Append(JsonSerializer.Serialize(new { type = "session", version = 3, id = "long-session", timestamp = "2027-01-15T08:00:00.000Z", cwd = root })).Append('\n');
        string[] active = ["read"], enabled = ["read", "bash", "edit", "write"];
        Message("{\"role\":\"system\",\"content\":\"Long-session base prompt\",\"toolsAdded\":[" + Declaration("read") + "],\"timestamp\":" + time++ + "}");
        const string Identity = "\"api\":\"anthropic-messages\",\"provider\":\"anthropic\",\"model\":\"claude-sonnet-4-5\"";
        const string Usage = "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}";
        for (var turn = 0; turn < Turns; turn++)
        {
            if (turn < 2 * Cycles)
            {
                var next = turn % 2 == 0 ? enabled : ["read"];
                Message("{\"role\":\"system\",\"content\":\"\",\"toolsRemoved\":[" + string.Join(",", active.Select(name => JsonSerializer.Serialize(new { name }))) +
                    "],\"toolsAdded\":[" + string.Join(",", next.Select(Declaration)) + "],\"timestamp\":" + time++ + "}");
                active = next;
            }
            var id = turn.ToString("D4");
            Message("{\"role\":\"user\",\"content\":\"u" + id + "x question\",\"timestamp\":" + time++ + "}");
            Message("{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"call_" + id + "\",\"name\":\"read\",\"arguments\":{\"path\":\"p" + id + "x\"}}]," +
                Identity + "," + Usage + ",\"stopReason\":\"toolUse\",\"timestamp\":" + time++ + "}");
            Message("{\"role\":\"toolResult\",\"toolCallId\":\"call_" + id + "\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"r" + id +
                "x contents\"}],\"isError\":false,\"timestamp\":" + time++ + "}");
            Message("{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"a" + id + "x answer\"}]," + Identity + "," + Usage + ",\"stopReason\":\"stop\",\"timestamp\":" + time++ + "}");
            // Entries outside the model context (extension state) also count toward the session's records.
            for (var custom = 0; custom < CustomPerTurn; custom++) Add("custom", "\"customType\":\"long-session-fixture\",\"data\":{\"turn\":" + turn + ",\"n\":" + custom + "}");
            markers.AddRange(["u" + id + "x", "p" + id + "x", "r" + id + "x", "a" + id + "x"]);
        }
        Check(count > 10_000, "session records " + count);
        await File.WriteAllTextAsync(path, lines.ToString(), new UTF8Encoding(false));

        var endpoint = new LiveEndpoint(seen => seen.Url == MessagesUrl ? AnthropicStream() : throw new InvalidOperationException("Unexpected URL " + seen.Url));
        string[] args = ["session", "rpc", "--session", path, "--workspace", root, "--live", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--session-mode", "open"];
        await using (var rpc = new LiveRpc(args, new LiveSessionRuntime(Env(("ANTHROPIC_API_KEY", "env-key")), () => endpoint)))
        {
            await rpc.Prompt("long", "final question");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            Names(["stop:"], rpc.StopReasons(), "long-session assistant stop");
        }
        var request = endpoint.Snapshot().Single();
        using var body = JsonDocument.Parse(request.Body!);
        var messages = body.RootElement.GetProperty("messages");
        var text = request.Body!;
        var missing = markers.Where(marker => !text.Contains(marker, StringComparison.Ordinal)).ToArray();
        Check(missing.Length == 0, $"request lacks {missing.Length} of {markers.Count} message markers, first {missing.FirstOrDefault()}");
        // Each turn is a user message, an assistant tool use, its result and an answer; the final prompt is last.
        Check(messages.GetArrayLength() >= 4 * Turns + 1 && text.Contains("final question", StringComparison.Ordinal), "request messages " + messages.GetArrayLength());
        Console.WriteLine($"  long session: {count} records, {messages.GetArrayLength()} request messages, body {Encoding.UTF8.GetByteCount(text)} bytes");
    });
}
