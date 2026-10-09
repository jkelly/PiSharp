// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/agent/src/agent.ts and packages/coding-agent/src/core/extensions/runner.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

// Counts and sizes upstream Pi does not cap (owner decision 0004, "follow what Pi does"): agent.ts, agent-loop.ts and
// core/extensions/{runner,wrapper}.ts hold every registered tool, every queued steering/follow-up message, every progress update and
// every content block of a tool result, so a Pi entry run must not stop at a PiSharp-only count. Each case runs the real Node bridge
// through PiCommand.RunAsync with a fake Anthropic endpoint.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CapCases() =>
    [
        ("caps.two-hundred-extension-tools-many-updates-and-large-results", ManyExtensionTools),
        ("caps.many-and-large-queued-follow-ups-from-an-extension", ManyFollowUps),
        ("caps.context-and-input-handlers-over-a-long-session", LongSessionHandlers),
        ("caps.extension-callbacks-in-a-large-session", LargeSessionCallbacks),
        ("caps.large-and-deep-custom-entries-persist-and-reload", LargeCustomEntries),
    ];

    // agent.ts setTools / agent-loop.ts: every registered tool is declared and callable (formerly 128 per binding and per agent);
    // wrapper.ts onUpdate forwards every update (formerly 4096 per batch); a result keeps every content block (formerly 128) and its
    // full text (formerly 65,536 characters for an extension tool).
    private static async Task ManyExtensionTools()
    {
        using var sandbox = NodeSandbox("many-tools");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "many.ts"), """
            export default function (pi: any) {
              for (let i = 0; i < 200; i++) {
                pi.registerTool({ name: "tool_" + i, label: "Tool " + i, description: "Tool number " + i,
                  parameters: { type: "object", properties: {} },
                  async execute(_id: string, _params: any, _signal: any, onUpdate: any) {
                    if (i !== 199) return { content: [{ type: "text", text: "plain " + i }], details: {} };
                    for (let n = 0; n < 5000; n++) onUpdate?.({ content: [{ type: "text", text: "update " + n }], details: {} });
                    const blocks: any[] = [{ type: "text", text: "x".repeat(100_000) }];
                    for (let n = 1; n < 200; n++) blocks.push({ type: "text", text: "b" + n });
                    return { content: blocks, details: { n: 199 } };
                  } });
              }
            }
            """);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("tool_199", new { }) : AnthropicText("done");
        var (code, stdout, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "Use the last tool"]);
        Equal(0, code, "exit; " + stderr);
        Equal("done", stdout.Trim(), "final text");
        var declared = sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToHashSet();
        Check(Enumerable.Range(0, 200).All(index => declared.Contains("tool_" + index)), "all 200 extension tools declared: " + declared.Count);
        var result = sandbox.Requests[1].Json.GetProperty("messages").EnumerateArray().Last().GetProperty("content").EnumerateArray()
            .First(item => item.GetProperty("type").GetString() == "tool_result");
        Check(!result.TryGetProperty("is_error", out var error) || !error.GetBoolean(), "the tool result is not an error");
        // anthropic-messages.ts convertContentBlocks: a text-only result is its blocks joined with newlines.
        Equal(new string('x', 100_000) + "\n" + string.Join("\n", Enumerable.Range(1, 199).Select(index => "b" + index)),
            result.GetProperty("content").GetString(), "every content block and the full text reached the model");
    }

    // agent.ts followUp/steer push onto unbounded arrays: 300 follow-ups (formerly 256 per queue) and one of 100,000 characters
    // (formerly 65,536) are all delivered, one per turn (followUpMode one-at-a-time).
    private static async Task ManyFollowUps()
    {
        using var sandbox = NodeSandbox("many-follow-ups");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "queue.ts"), """
            export default function (pi: any) {
              pi.registerTool({ name: "queue_many", label: "Queue", description: "Queues follow-ups",
                parameters: { type: "object", properties: {} },
                async execute(_id: string, _params: any, _signal: any, _onUpdate: any, ctx: any) {
                  pi.sendUserMessage("L" + "y".repeat(100_000), { deliverAs: "followUp" });
                  for (let n = 0; n < 300; n++) pi.sendUserMessage("follow " + n, { deliverAs: "followUp" });
                  for (let wait = 0; wait < 600; wait++) {
                    if (ctx.hasPendingMessages()) break;
                    await new Promise((resolve) => setTimeout(resolve, 50));
                  }
                  return { content: [{ type: "text", text: "queued" }], details: {} };
                } });
            }
            """);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("queue_many", new { }) : AnthropicText("ack " + index);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "Queue"]);
        Equal(0, code, "exit; " + stderr);
        var users = sandbox.Requests.Last().Json.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "user")
            .Select(message => message.GetProperty("content")).Select(content => content.ValueKind == JsonValueKind.String ? content.GetString()!
                : string.Concat(content.EnumerateArray().Select(item => item.TryGetProperty("text", out var t) ? t.GetString() : ""))).ToList();
        Check(users.Any(text => text == "L" + new string('y', 100_000)), "the 100,000-character follow-up was delivered");
        Check(Enumerable.Range(0, 300).All(index => users.Contains("follow " + index)), "every one of the 300 follow-ups was delivered: " + users.Count);
    }

    // runner.ts emitContext/emitInput: a context handler sees every message of a session longer than 1,024 messages (formerly a
    // fixed 1,024-message dispatch and validation bound) and may trim them for the request; an input handler sees an extension
    // message of 100,000 characters (formerly 65,536).
    private static async Task LongSessionHandlers()
    {
        // The Pi binding's dispatch admits a context of more than 1,024 messages, and so does the default dispatcher, whose context
        // bound is now PiRequestBudget.RequestMessages (one million); a context beyond that is still refused.
        var context = Enumerable.Range(0, 1500).Select(index => new PiSharp.Contracts.TranscriptEntry("user", PiSharp.Contracts.JsonData.Parse(
            "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"q\"}],\"timestamp\":" + index + "}"))).ToImmutableArray();
        var dispatcher = new PiSharp.Extensions.Runtime.Dispatch.ExtensionEventDispatcher(_ => { }, PiSharp.Cli.Commands.PiPayloadBudget.PiEventDispatch,
            messages => PiSharp.Agent.AgentLoopRunner.ValidateRequestMessages(messages, null, int.MaxValue));
        Equal(1500, (await dispatcher.DispatchContextAsync(dispatcher.ContextHandlers.CaptureSnapshot(), new(context))).Messages.Length, "Pi context dispatch");
        var defaults = new PiSharp.Extensions.Runtime.Dispatch.ExtensionEventDispatcher(_ => { }, null, _ => { });
        Equal(1500, (await defaults.DispatchContextAsync(defaults.ContextHandlers.CaptureSnapshot(), new(context))).Messages.Length, "default context dispatch");
        var bounded = new PiSharp.Extensions.Runtime.Dispatch.ExtensionEventDispatcher(_ => { },
            new PiSharp.Extensions.Runtime.Dispatch.ExtensionEventDispatchOptions(MaximumContextMessages: 1_024), _ => { });
        await ThrowsAsync<ArgumentException>(() => bounded.DispatchContextAsync(bounded.ContextHandlers.CaptureSnapshot(), new(context)).AsTask());

        using var sandbox = NodeSandbox("long-session-handlers");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "observe.ts"), """
            import { appendFileSync } from "node:fs";
            const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
            export default function (pi: any) {
              pi.on("context", (event: any) => { log("context", event.messages.length); return { messages: event.messages.slice(-51) }; });
              pi.on("input", (event: any) => { log("input", event.text.length); return { action: "continue" }; });
              pi.registerTool({ name: "long_follow_up", label: "Long", description: "Queues a long follow-up",
                parameters: { type: "object", properties: {} },
                async execute(_id: string, _params: any, _signal: any, _onUpdate: any, ctx: any) {
                  pi.sendUserMessage("z".repeat(100_000), { deliverAs: "followUp" });
                  for (let wait = 0; wait < 600 && !ctx.hasPendingMessages(); wait++) await new Promise((resolve) => setTimeout(resolve, 50));
                  return { content: [{ type: "text", text: "queued" }], details: {} };
                } });
            }
            """);
        var lines = new List<string> { new JsonObject { ["type"] = "session", ["version"] = 3, ["id"] = "long-handlers",
            ["timestamp"] = "2026-01-01T00:00:00.000Z", ["cwd"] = sandbox.Cwd }.ToJsonString() };
        string? parent = null;
        for (var index = 0; index < 1100; index++)
        {
            var id = "m" + index;
            var message = index % 2 == 0
                ? new JsonObject { ["role"] = "user", ["content"] = "question " + index, ["timestamp"] = index }
                : new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "answer " + index }),
                    ["api"] = "anthropic-messages", ["provider"] = "anthropic", ["model"] = "claude-sonnet-4-5", ["stopReason"] = "stop", ["timestamp"] = index,
                    ["usage"] = new JsonObject { ["input"] = 1, ["output"] = 1, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["totalTokens"] = 2,
                        ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["total"] = 0 } } };
            lines.Add(new JsonObject { ["type"] = "message", ["id"] = id, ["parentId"] = parent, ["timestamp"] = "2026-01-01T00:00:00.000Z", ["message"] = message }.ToJsonString());
            parent = id;
        }
        var file = sandbox.Write("long-handlers.jsonl", string.Join("\n", lines) + "\n");
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("long_follow_up", new { }) : AnthropicText("reply " + index);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "--session", file, "go"]);
        Equal(0, code, "exit; " + stderr);
        var records = LogLines(sandbox).Select(line => JsonNode.Parse(line)!.AsArray()).ToList();
        Equal("1101,1103,1105", string.Join(",", records.Where(record => record[0]!.GetValue<string>() == "context").Select(record => record[1]!.ToJsonString())),
            "the context handler saw the whole session before each request");
        Check(records.Any(record => record[0]!.GetValue<string>() == "input" && record[1]!.GetValue<int>() == 100_000), "the input handler saw the long follow-up");
        Equal(3, sandbox.Requests.Count, "the prompt, the tool turn and the follow-up");
        Equal(51, sandbox.Requests[0].Json.GetProperty("messages").GetArrayLength(), "the request carries the handler's messages");
    }

    // runner.ts createContext gives every handler and tool the session manager whatever the session holds: an extension tool runs in a
    // session of 5,000 entries and 3 MB of text (formerly every callback failed past 4,096 branch entries or 1 MiB of branch text).
    private static async Task LargeSessionCallbacks()
    {
        using var sandbox = NodeSandbox("large-session-callbacks");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "count.ts"), """
            export default function (pi: any) {
              pi.registerTool({ name: "count_entries", label: "Count", description: "Counts session entries",
                parameters: { type: "object", properties: {} },
                async execute(_id: string, _params: any, _signal: any, _onUpdate: any, ctx: any) {
                  return { content: [{ type: "text", text: "entries=" + ctx.sessionManager.getEntries().length }], details: {} };
                } });
            }
            """);
        var lines = new List<string> { new JsonObject { ["type"] = "session", ["version"] = 3, ["id"] = "large-session",
            ["timestamp"] = "2026-01-01T00:00:00.000Z", ["cwd"] = sandbox.Cwd }.ToJsonString() };
        lines.Add(new JsonObject { ["type"] = "message", ["id"] = "m0", ["parentId"] = null, ["timestamp"] = "2026-01-01T00:00:00.000Z",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new string('q', 3_000_000), ["timestamp"] = 0 } }.ToJsonString());
        for (var index = 1; index < 5000; index++)
            lines.Add(new JsonObject { ["type"] = "custom", ["customType"] = "note", ["data"] = new JsonObject { ["n"] = index },
                ["id"] = "c" + index, ["parentId"] = index == 1 ? "m0" : "c" + (index - 1), ["timestamp"] = "2026-01-01T00:00:00.000Z" }.ToJsonString());
        var file = sandbox.Write("large-session.jsonl", string.Join("\n", lines) + "\n");
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("count_entries", new { }) : AnthropicText("done");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "--session", file, "count"]);
        Equal(0, code, "exit; " + stderr);
        Equal("entries=5003", ToolResultText(sandbox.Requests[1]), "the tool ran with the whole session");
    }

    // session-manager.ts appendCustomEntry writes JSON.stringify(entry) of any size or depth and loadEntriesFromFile reads it back: an
    // entry of 20 MB and 50 levels (formerly 16 MiB per record and 32 levels) is written, and the session resumes with it.
    private static async Task LargeCustomEntries()
    {
        using var sandbox = NodeSandbox("large-custom-entries");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "store.ts"), """
            export default function (pi: any) {
              pi.registerTool({ name: "store_big", label: "Store", description: "Stores a big entry",
                parameters: { type: "object", properties: {} },
                async execute() {
                  let deep: any = { leaf: true };
                  for (let level = 0; level < 50; level++) deep = { next: deep };
                  pi.appendEntry("big-state", { blob: "x".repeat(20_000_000), deep });
                  return { content: [{ type: "text", text: "stored" }], details: {} };
                } });
              pi.registerTool({ name: "read_big", label: "Read", description: "Reads the big entry",
                parameters: { type: "object", properties: {} },
                async execute(_id: string, _params: any, _signal: any, _onUpdate: any, ctx: any) {
                  const entry = ctx.sessionManager.getEntries().find((item: any) => item.type === "custom" && item.customType === "big-state");
                  let depth = 0; for (let node = entry?.data?.deep; node?.next; node = node.next) depth++;
                  return { content: [{ type: "text", text: "blob=" + (entry?.data?.blob?.length ?? -1) + " depth=" + depth }], details: {} };
                } });
            }
            """);
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("store_big", new { }) : AnthropicText("done");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "store"]);
        Equal(0, code, "exit; " + stderr);
        var file = sandbox.SessionFiles().Single();
        Check(File.ReadLines(file).Any(line => line.Contains("\"customType\":\"big-state\"", StringComparison.Ordinal) && line.Length > 20_000_000), "the entry was written whole");
        sandbox.Requests.Clear();
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("read_big", new { }, "toolu_02") : AnthropicText("done");
        (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "--session", file, "read"]);
        Equal(0, code, "resume exit; " + stderr);
        Equal("blob=20000000 depth=50", ToolResultText(sandbox.Requests[1]), "the resumed session holds the entry");
    }
}
