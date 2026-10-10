using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Expect;

/// <summary>
/// footer.ts end to end (1.1.0.3 fix): the footer reads the session on every render, so its usage totals (↑ input, ↓ output,
/// R cache read, W cache write, CH latest cache hit rate, $ cost), context usage, model and thinking level follow each appended
/// entry, model change, thinking change, session switch and compaction. Expectations are written from footer.ts (formatTokens,
/// toFixed), core/usage-totals.ts, agent-session.ts getContextUsage/getSessionStats and models.ts calculateCost with the catalog
/// prices of claude-sonnet-4-5 (3/15/0.3/3.75 per million, 1M window) and claude-haiku-4-5 (1/5/0.1/1.25, 200k window).
/// </summary>
internal static class FooterUsageCases
{
    private static readonly string[] Regular = ["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular"];

    private static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private static string Start(int input, int cacheRead, int cacheWrite) => Frame("message_start", new
    {
        type = "message_start", message = new { id = "msg_u", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(),
            usage = new { input_tokens = input, output_tokens = 0, cache_read_input_tokens = cacheRead, cache_creation_input_tokens = cacheWrite } }
    });

    /// <summary>An Anthropic text response with the given usage.</summary>
    internal static HttpResponseMessage Text(string text, int input, int output, int cacheRead = 0, int cacheWrite = 0) => Sse(
        Start(input, cacheRead, cacheWrite)
        + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
        + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })
        + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
        + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = output } })
        + Frame("message_stop", new { type = "message_stop" }));

    /// <summary>An Anthropic tool call response with the given usage.</summary>
    internal static HttpResponseMessage ToolCall(string name, object arguments, int input, int output, int cacheRead = 0, int cacheWrite = 0) => Sse(
        Start(input, cacheRead, cacheWrite)
        + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = "toolu_u1", name, input = new { } } })
        + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = JsonSerializer.Serialize(arguments) } })
        + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
        + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = output } })
        + Frame("message_stop", new { type = "message_stop" }));

    /// <summary>The footer's stats line (the one with the context percentage and the auto-compact indicator).</summary>
    internal static string? StatsLine(string screen) => screen.Split('\n').LastOrDefault(line => line.Contains("(auto)", StringComparison.Ordinal));

    /// <summary>Waits until the stats line is exactly <paramref name="left"/>, padding, then <paramref name="right"/> (the model side).</summary>
    internal static Task<string> WaitForFooter(InteractiveHarness pi, string left, string right) => pi.WaitUntil(text =>
        StatsLine(text) is { } line && line.StartsWith(left + "  ", StringComparison.Ordinal) && line.EndsWith("  " + right, StringComparison.Ordinal) &&
        line[left.Length..^right.Length].Trim().Length == 0, $"footer <{left} ... {right}>", 20_000);

    private static async Task Model(InteractiveHarness pi, string command)
    {
        pi.Type(command);
        await Task.Delay(300);
        pi.Type("\r");
        await Task.Delay(300);
        pi.Type("\r");
    }

    private static async Task<JsonObject> Stats(InteractiveHarness pi) =>
        (JsonObject)(await pi.Mode!.Rpc.RequestAsync(new JsonObject { ["type"] = "get_session_stats" }))!;

    private static string Num(JsonNode? node) => node!.GetValue<double>().ToString("R", CultureInfo.InvariantCulture);

    private sealed class NoFooterData : PiSharp.Cli.Interactive.Mode.IReadonlyFooterDataProvider
    {
        public string? GetGitBranch() => null;
        public IReadOnlyDictionary<string, string> GetExtensionStatuses() => new Dictionary<string, string>();
        public int GetAvailableProviderCount() => 1;
        public Action OnBranchChange(Action callback) => () => { };
    }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // The mode's session mirror (SessionState): the context usage arrives with get_session_stats separately from the entries, so
        // a new stats object re-renders the cached footer; a virtual model shows the routed physical model (AgentSession.routedModel:
        // findLatestResponse) and that model's context window (_limitsModel).
        yield return ("footer.mirror.stats-and-routed-model", Sync(() =>
        {
            var state = new PiSharp.Cli.Interactive.Mode.SessionState("/tmp/project")
            {
                SessionId = "s1", ThinkingLevel = "high",
                Model = new JsonObject { ["id"] = "auto", ["provider"] = "router", ["api"] = "pi-virtual", ["contextWindow"] = 1_000_000.0, ["reasoning"] = true },
                Stats = JsonNode.Parse("""{"contextUsage":{"tokens":null,"contextWindow":1000000,"percent":null}}""")!.AsObject(),
            };
            var footer = new PiSharp.Cli.Interactive.Mode.Components.FooterComponent(state, new NoFooterData()) { Environment = _ => null };
            string Line() => Strip(footer.Render(120)[1]);
            Check(Line().StartsWith("?/1.0M (auto)  ", StringComparison.Ordinal) && Line().EndsWith("  auto • high", StringComparison.Ordinal), "before stats: " + Line());
            state.Stats = JsonNode.Parse("""{"contextUsage":{"tokens":250000,"contextWindow":1000000,"percent":25}}""")!.AsObject();
            Check(Line().StartsWith("25.0%/1.0M (auto)  ", StringComparison.Ordinal), "new stats re-render with the same entries: " + Line());
            state.AvailableModels = [new JsonObject { ["id"] = "luna", ["provider"] = "openai", ["api"] = "openai-responses", ["contextWindow"] = 400_000.0 }];
            state.Entries.Add(new JsonObject { ["type"] = "message", ["id"] = "a1", ["message"] = new JsonObject
            {
                ["role"] = "assistant", ["provider"] = "openai", ["model"] = "luna", ["thinkingLevel"] = "medium", ["stopReason"] = "stop", ["content"] = new JsonArray(),
                ["usage"] = new JsonObject { ["input"] = 10.0, ["output"] = 5.0, ["cacheRead"] = 0.0, ["cacheWrite"] = 0.0, ["cost"] = new JsonObject { ["total"] = 0.5 } }
            } });
            state.LeafId = "a1";
            Check(Line().StartsWith("↑10 ↓5 $0.500 62.5%/400k (auto)  ", StringComparison.Ordinal) && Line().EndsWith("  auto • high → luna • medium", StringComparison.Ordinal),
                "routed physical model and its window: " + Line());
        }));

        // Each turn's usage reaches the footer when its message is appended; /thinking and /model change the model side, and the
        // limits model's window (getContextUsage) changes the context part while the totals stay.
        yield return ("e2e.footer.usage-thinking-and-model-follow-the-session", async () =>
        {
            await using var pi = new InteractiveHarness("footer-usage", columns: 120, rows: 60);
            pi.Respond = (_, index) => index switch
            {
                0 => Text("first answer", 1200, 250, 3000, 500),
                1 => Text("second answer", 800, 150, 14500, 0),
                _ => Text("third answer", 2900, 100)
            };
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await WaitForFooter(pi, "0.0%/1.0M (auto)", "claude-sonnet-4-5 • medium");

            await pi.Submit("one");
            await pi.WaitFor("first answer");
            // 1200*3 + 250*15 + 3000*0.3 + 500*3.75 = 10125 µ$; CH 3000/4700; context 4950/1M.
            await WaitForFooter(pi, "↑1.2k ↓250 R3.0k W500 CH63.8% $0.010 0.5%/1.0M (auto)", "claude-sonnet-4-5 • medium");

            await pi.Submit("two");
            await pi.WaitFor("second answer");
            // + 800*3 + 150*15 + 14500*0.3 = 9000 µ$; CH 14500/15300; context 15450/1M.
            await WaitForFooter(pi, "↑2.0k ↓400 R18k W500 CH94.8% $0.019 1.5%/1.0M (auto)", "claude-sonnet-4-5 • medium");

            // get_session_stats and /session use the same totals (agent-session.ts getSessionStats).
            var stats = await Stats(pi);
            Equal("2000|400|17500|500|20400", string.Join("|", new[] { "input", "output", "cacheRead", "cacheWrite", "total" }.Select(key => Num(stats["tokens"]![key]))), "get_session_stats tokens");
            Equal("0.019125", Math.Round(stats["cost"]!.GetValue<double>(), 9).ToString(CultureInfo.InvariantCulture), "get_session_stats cost");
            Equal("15450|1000000", Num(stats["contextUsage"]!["tokens"]) + "|" + Num(stats["contextUsage"]!["contextWindow"]), "get_session_stats context usage");
            await pi.Submit("/session");
            await pi.WaitFor("Session Info");
            await pi.WaitFor("Total: $0.019");
            foreach (var line in new[] { "Input: 20,000", "Cached: 17,500 (87.5%)", "Uncached: 2,500 (500 written to cache)", "Output: 400", "Total: 20,400", "Assistant: 2" })
                Contains(pi.Terminal.Text, line, "/session");

            pi.Type("/thinking high");
            await pi.WaitFor("/thinking high");
            await Task.Delay(300);
            pi.Type("\r");
            await Task.Delay(300);
            pi.Type("\r");
            await WaitForFooter(pi, "↑2.0k ↓400 R18k W500 CH94.8% $0.019 1.5%/1.0M (auto)", "claude-sonnet-4-5 • high");

            await Model(pi, "/model claude-haiku-4-5");
            // The same 15450 context tokens in haiku's 200k window.
            await WaitForFooter(pi, "↑2.0k ↓400 R18k W500 CH94.8% $0.019 7.7%/200k (auto)", "claude-haiku-4-5 • high");

            await pi.Submit("three");
            await pi.WaitFor("third answer");
            // + 2900*1 + 100*5 = 3400 µ$ at haiku's prices; CH 0/2900; context 3000/200k.
            await WaitForFooter(pi, "↑4.9k ↓500 R18k W500 CH0.0% $0.023 1.5%/200k (auto)", "claude-haiku-4-5 • high");
            Equal(0, await pi.Quit(), "exit code");
        });

        // A tool-call turn has two assistant messages: the first one's usage shows while its tool runs, before the next request.
        yield return ("e2e.footer.tool-turn-counts-each-assistant-message", async () =>
        {
            await using var pi = new InteractiveHarness("footer-tool", columns: 120);
            File.WriteAllText(Path.Combine(pi.Cwd, "notes.txt"), "alpha\nbeta\n");
            string? footerBeforeSecondRequest = null;
            pi.Respond = (_, index) =>
            {
                if (index == 0) return ToolCall("read", new { path = "notes.txt" }, 1000, 50, 0, 2000);
                var deadline = Environment.TickCount64 + 10_000;
                while (Environment.TickCount64 < deadline && StatsLine(pi.Terminal.Text)?.StartsWith("↑", StringComparison.Ordinal) != true) Thread.Sleep(25);
                footerBeforeSecondRequest = StatsLine(pi.Terminal.Text);
                return Text("Read it.", 500, 80, 3000, 0);
            };
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("read the notes");
            await pi.WaitFor("Read it.");
            // 1000*3 + 50*15 + 2000*3.75 = 11250 µ$; CH 0/3000.
            Check(footerBeforeSecondRequest?.StartsWith("↑1.0k ↓50 W2.0k CH0.0% $0.011 ", StringComparison.Ordinal) == true,
                "footer after the tool call message, before the next request: " + footerBeforeSecondRequest);
            // + 500*3 + 80*15 + 3000*0.3 = 3600 µ$; CH 3000/3500; context 3580/1M.
            await WaitForFooter(pi, "↑1.5k ↓130 R3.0k W2.0k CH85.7% $0.015 0.4%/1.0M (auto)", "claude-sonnet-4-5 • medium");
            Equal(0, await pi.Quit(), "exit code");
        });

        // /new starts with empty totals; /resume of the earlier session shows its totals again.
        yield return ("e2e.footer.new-and-resume-switch-the-totals", async () =>
        {
            await using var pi = new InteractiveHarness("footer-switch", columns: 120, rows: 40);
            pi.Respond = (_, index) => index == 0 ? Text("first answer", 1200, 250, 3000, 500) : Text("other answer", 100, 10);
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("one");
            await pi.WaitFor("first answer");
            await WaitForFooter(pi, "↑1.2k ↓250 R3.0k W500 CH63.8% $0.010 0.5%/1.0M (auto)", "claude-sonnet-4-5 • medium");
            Equal(true, (await pi.Mode!.Rpc.RequestAsync(new JsonObject { ["type"] = "get_state" }))?["autoCompactionEnabled"]?.GetValue<bool>(),
                "the session's automatic compaction follows compaction.enabled (default true)");
            await pi.Submit("/new");
            await pi.WaitFor("✓ New session started");
            await WaitForFooter(pi, "0.0%/1.0M (auto)", "claude-sonnet-4-5 • medium");
            Equal(true, (await pi.Mode!.Rpc.RequestAsync(new JsonObject { ["type"] = "get_state" }))?["autoCompactionEnabled"]?.GetValue<bool>(),
                "a new session's automatic compaction follows compaction.enabled too");
            await pi.Submit("other");
            await pi.WaitFor("other answer");
            // 100*3 + 10*15 = 450 µ$.
            await WaitForFooter(pi, "↑100 ↓10 $0.000 0.0%/1.0M (auto)", "claude-sonnet-4-5 • medium");
            await pi.Submit("/resume");
            await pi.WaitFor("Resume Session");
            await pi.WaitFor("one");
            // The current session is listed first (most recent); the earlier one is next.
            pi.Type("\u001b[B");
            await Task.Delay(300);
            pi.Type("\r");
            await WaitForFooter(pi, "↑1.2k ↓250 R3.0k W500 CH63.8% $0.010 0.5%/1.0M (auto)", "claude-sonnet-4-5 • medium");
            Equal(0, await pi.Quit(), "exit code");
        });

        // agent-session.ts autoCompactionEnabled is compaction.enabled, which the host applies to every session it runs (the mode only
        // shows it: footer.setAutoCompactEnabled), so a disabled setting holds for the startup session and a new one, without "(auto)".
        yield return ("e2e.footer.auto-compaction-disabled-by-setting-holds-for-new-sessions", async () =>
        {
            await using var pi = new InteractiveHarness("footer-auto-off", columns: 120, rows: 40);
            File.WriteAllText(Path.Combine(pi.AgentDir, "settings.json"), """{"compaction":{"enabled":false}}""");
            pi.Respond = (_, _) => Text("first answer", 1200, 250);
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("one");
            await pi.WaitFor("first answer");
            await pi.WaitUntil(text => text.Contains("0.1%/1.0M", StringComparison.Ordinal), "footer stats");
            Check(!pi.Terminal.Text.Contains("(auto)", StringComparison.Ordinal), "no auto indicator: " + pi.Terminal.Text);
            Equal(false, (await pi.Mode!.Rpc.RequestAsync(new JsonObject { ["type"] = "get_state" }))?["autoCompactionEnabled"]?.GetValue<bool>(),
                "the startup session follows compaction.enabled false");
            await pi.Submit("/new");
            await pi.WaitFor("✓ New session started");
            await pi.WaitUntil(text => text.Contains("0.0%/1.0M", StringComparison.Ordinal), "new session footer");
            Check(!pi.Terminal.Text.Contains("(auto)", StringComparison.Ordinal), "no auto indicator after /new: " + pi.Terminal.Text);
            Equal(false, (await pi.Mode!.Rpc.RequestAsync(new JsonObject { ["type"] = "get_state" }))?["autoCompactionEnabled"]?.GetValue<bool>(),
                "the new session follows compaction.enabled false too");
            Equal(0, await pi.Quit(), "exit code");
        });

        // footer.ts sums usage over ALL entries (the compaction entry's own summary usage included), and getContextUsage reports
        // unknown tokens until an assistant responds after the compaction.
        yield return ("e2e.footer.compaction-adds-its-usage-and-resets-context", async () =>
        {
            await using var pi = new InteractiveHarness("footer-compact", columns: 120);
            File.WriteAllText(Path.Combine(pi.AgentDir, "settings.json"), """{"compaction":{"keepRecentTokens":1}}""");
            var compacting = false;
            pi.Respond = (_, index) => compacting ? Text("## Goal\nsummary", 5000, 400) : index switch
            {
                0 => Text("first answer", 1200, 250, 3000, 500),
                1 => Text("second answer", 800, 150, 14500, 0),
                _ => Text("after answer", 3000, 100)
            };
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("one");
            await pi.WaitFor("first answer");
            await pi.Submit("two");
            await pi.WaitFor("second answer");
            await WaitForFooter(pi, "↑2.0k ↓400 R18k W500 CH94.8% $0.019 1.5%/1.0M (auto)", "claude-sonnet-4-5 • medium");
            compacting = true;
            await pi.Submit("/compact");
            // keepRecentTokens 1 cuts inside the last turn: a history summary and a turn-prefix summary, whose usages the compaction
            // entry combines (combineUsage): + 2 * (5000*3 + 400*15) = 42000 µ$. The cache hit rate stays the latest assistant
            // message's, and the context is unknown until an assistant responds after the compaction.
            await WaitForFooter(pi, "↑12k ↓1.2k R18k W500 CH94.8% $0.061 ?/1.0M (auto)", "claude-sonnet-4-5 • medium");
            Equal(4, pi.Requests.Count, "two summary requests");
            compacting = false;
            await pi.Submit("three");
            await pi.WaitFor("after answer");
            // + 3000*3 + 100*15 = 10500 µ$; CH 0/3000; context 3100/1M.
            await WaitForFooter(pi, "↑15k ↓1.3k R18k W500 CH0.0% $0.072 0.3%/1.0M (auto)", "claude-sonnet-4-5 • medium");
            Equal(0, await pi.Quit(), "exit code");
        });
    }
}
