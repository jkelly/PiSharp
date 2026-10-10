// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/sdk.ts (createAgentSession: the restored, CLI or
// default level is clamped with clampThinkingLevel; only a new session, or one without a thinking entry, records it),
// packages/coding-agent/src/core/agent-session.ts (setThinkingLevel/_clampThinkingLevel, cycleThinkingLevel, setModel/cycleModel with
// _getThinkingLevelForModelSwitch) and packages/ai/src/models.ts (clampThinkingLevel: the level, else the nearest supported level above,
// else the nearest below).
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.Cli.Pi;

// Issue #5: /clear (new_session) crashed with "RPC command failed." on a model whose thinking cannot be turned off, because the new
// session was refused at "off" instead of being clamped. anthropic/claude-haiku-5-5 maps off and minimal to null (levels low..max);
// anthropic/claude-haiku-4-5 has no map (levels off..high). Expectations were written from the pinned sources by reading.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ThinkingClampCases() =>
    [
        // packages/ai/test/max-thinking.test.ts and test/suite/agent-session-model-extension.test.ts, on the supported levels each
        // model has there (getSupportedThinkingLevels), plus each branch of clampThinkingLevel.
        ("thinking-clamp.clamp-thinking-level-ports-models-ts", Sync(() =>
        {
            ImmutableArray<string> ordinary = ["off", "minimal", "low", "medium", "high"];
            Equal("high", ThinkingLevels.Clamp(ordinary, "max"), "max-thinking: ordinary reasoning model clamps max to high");
            Equal("max", ThinkingLevels.Clamp(["off", "minimal", "low", "medium", "high", "max"], "xhigh"), "max-thinking: a hole between high and max");
            Equal("off", ThinkingLevels.Clamp(["off"], "high"), "agent-session: a model without reasoning clamps to off");
            foreach (var level in ordinary) Equal(level, ThinkingLevels.Clamp(ordinary, level), "a supported level is kept");
            ImmutableArray<string> withoutOff = ["low", "medium", "high", "xhigh", "max"]; // thinkingLevelMap { off: null, minimal: null }
            Equal("low", ThinkingLevels.Clamp(withoutOff, "off"), "off: the nearest supported level above");
            Equal("low", ThinkingLevels.Clamp(withoutOff, "minimal"), "minimal: the nearest supported level above");
            Equal("high", ThinkingLevels.Clamp(["low", "high"], "medium"), "medium: above before below");
            Equal("low", ThinkingLevels.Clamp(["off", "low"], "max"), "max: the nearest supported level below");
            Equal("low", ThinkingLevels.Clamp(withoutOff, "bogus"), "an unknown level: the first supported level");
            Equal("off", ThinkingLevels.Clamp([], "high"), "no supported levels: off");
        })),
        ("thinking-clamp.new-session-on-a-model-without-off-records-the-clamped-level", async () =>
        {
            using var sandbox = new Sandbox("clamp-new-session");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"),
                """{"defaultProvider":"anthropic","defaultModel":"claude-haiku-5-5","defaultThinkingLevel":"off"}""");
            var events = new List<JsonNode>();
            var responses = await RpcUntilIdle(sandbox, ["--mode", "rpc"], events,
                """{"id":"1","type":"get_state"}""",
                """{"id":"2","type":"new_session"}""",
                """{"id":"3","type":"get_state"}""",
                """{"id":"4","type":"prompt","message":"after clear"}""",
                """{"id":"5","type":"get_state"}""");
            // sdk.ts: the new session's level is defaultThinkingLevel ("off") clamped to the model: off and minimal are unsupported, so
            // the nearest level above, "low".
            Equal("low", responses[0]["data"]!["thinkingLevel"]!.GetValue<string>(), "startup level");
            Check(responses[1]["success"]!.GetValue<bool>(), "new_session: " + responses[1].ToJsonString());
            Equal("claude-haiku-5-5", responses[2]["data"]!["model"]!["id"]!.GetValue<string>(), "new session model");
            Equal("low", responses[2]["data"]!["thinkingLevel"]!.GetValue<string>(), "new session level");
            Check(responses[3]["success"]!.GetValue<bool>(), "prompt: " + responses[3].ToJsonString());
            Equal("low", responses[4]["data"]!["thinkingLevel"]!.GetValue<string>(), "level after the turn");
            Check(!events.Any(line => line["type"]?.GetValue<string>() == "agent_end" && line.ToJsonString().Contains("\"stopReason\":\"error\"", StringComparison.Ordinal)),
                "the turn after new_session failed");
            Equal("claude-haiku-5-5", sandbox.Requests[^1].Json.GetProperty("model").GetString(), "the request goes to the new session's model");
            // The new session recorded its model and the clamped level once (appendModelChange, appendThinkingLevelChange), then the turn.
            var file = responses[2]["data"]!["sessionFile"]!.GetValue<string>();
            var entries = File.ReadLines(file).Skip(1).Select(line => JsonNode.Parse(line)!).ToList();
            Names(["model_change", "thinking_level_change", "message:system", "message:user", "message:assistant"], entries.Select(Kind), "new session entries");
            Equal("claude-haiku-5-5", entries[0]["modelId"]!.GetValue<string>(), "recorded model");
            Equal("low", entries[1]["thinkingLevel"]!.GetValue<string>(), "recorded clamped level");
        }),
        ("thinking-clamp.resume-opens-at-the-clamped-level-and-later-turns-run", async () =>
        {
            using var sandbox = new Sandbox("clamp-resume");
            // The branch records "off" on a model that cannot turn thinking off.
            var session = SeedSession(sandbox, "haiku55.jsonl", "anthropic", "claude-haiku-5-5");
            var seeded = File.ReadAllLines(session).Length;
            var responses = await RpcSequence(sandbox, ["--mode", "rpc", "--session", session],
                """{"id":"1","type":"get_state"}""",
                """{"id":"2","type":"get_available_thinking_levels"}""");
            Equal("claude-haiku-5-5", responses[0]["data"]!["model"]!["id"]!.GetValue<string>(), "restored model");
            Equal("low", responses[0]["data"]!["thinkingLevel"]!.GetValue<string>(), "restored level clamped");
            Names(["low", "medium", "high", "xhigh", "max"], responses[1]["data"]!["levels"]!.AsArray().Select(level => level!.GetValue<string>()), "levels");
            // sdk.ts appends a thinking_level_change only to a session without one: the recorded "off" stays, nothing is written.
            Equal(seeded, File.ReadAllLines(session).Length, "opening records nothing");
            // Later turns run at the clamped level (the branch's "off" is tolerated, not refused).
            for (var turn = 1; turn <= 2; turn++)
            {
                var (code, stdout, stderr) = await sandbox.Run("-p", "--session", session, "turn " + turn);
                Check(code == 0 && stdout == "ok\n", $"turn {turn}: {code} {stdout} {stderr}");
                Equal("claude-haiku-5-5", sandbox.Requests[^1].Json.GetProperty("model").GetString(), "turn " + turn + " model");
            }
            var appended = File.ReadLines(session).Skip(seeded).Select(line => JsonNode.Parse(line)!).ToList();
            // Only the turns are recorded: no thinking_level_change for the clamped level.
            Names(["message:system", "message:user", "message:assistant", "message:user", "message:assistant"], appended.Select(Kind), "only the turns are recorded");
            // An RPC run on the resumed session: setting the clamped level records nothing (it is not a change of the session's level),
            // another level is recorded, and a turn runs at it.
            var events = new List<JsonNode>();
            var before = File.ReadAllLines(session).Length;
            responses = await RpcUntilIdle(sandbox, ["--mode", "rpc", "--session", session], events,
                """{"id":"1","type":"set_thinking_level","level":"low"}""",
                """{"id":"2","type":"set_thinking_level","level":"high"}""",
                """{"id":"3","type":"prompt","message":"rpc turn"}""",
                """{"id":"4","type":"get_state"}""");
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "rpc on the resumed session: " + string.Join("\n", responses.Select(r => r.ToJsonString())));
            Equal("high", responses[3]["data"]!["thinkingLevel"]!.GetValue<string>(), "set after resume");
            appended = File.ReadLines(session).Skip(before).Select(line => JsonNode.Parse(line)!).ToList();
            Names(["thinking_level_change", "message:user", "message:assistant"], appended.Select(Kind), "the change and the turn");
            Equal("high", appended[0]["thinkingLevel"]!.GetValue<string>(), "recorded level");
        }),
        ("thinking-clamp.set-cycle-and-model-switch-clamp-like-agent-session", async () =>
        {
            using var sandbox = new Sandbox("clamp-switch");
            var session = SeedSession(sandbox, "haiku45.jsonl", "anthropic", "claude-haiku-4-5");
            var seeded = File.ReadAllLines(session).Length;
            var responses = await RpcSequence(sandbox, ["--mode", "rpc", "--session", session, "--models", "anthropic/claude-haiku-4-5:off,anthropic/claude-haiku-5-5:off"],
                """{"id":"1","type":"set_thinking_level","level":"max"}""",
                """{"id":"2","type":"get_state"}""",
                """{"id":"3","type":"set_thinking_level","level":"off"}""",
                """{"id":"4","type":"set_model","provider":"anthropic","modelId":"claude-haiku-5-5"}""",
                """{"id":"5","type":"get_state"}""",
                """{"id":"6","type":"set_thinking_level","level":"minimal"}""",
                """{"id":"7","type":"set_thinking_level","level":"off"}""",
                """{"id":"8","type":"get_state"}""",
                """{"id":"9","type":"cycle_thinking_level"}""",
                """{"id":"10","type":"cycle_model"}""",
                """{"id":"11","type":"cycle_model"}""",
                """{"id":"12","type":"get_state"}""");
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "every command succeeds: " + string.Join("\n", responses.Select(r => r.ToJsonString())));
            // haiku-4-5 has no xhigh or max: "max" clamps down to "high".
            Equal("high", responses[1]["data"]!["thinkingLevel"]!.GetValue<string>(), "max clamped down");
            // setModel: no settings default, so the current level ("off"), clamped to haiku-5-5 ("low").
            Equal("claude-haiku-5-5", responses[4]["data"]!["model"]!["id"]!.GetValue<string>(), "set_model");
            Equal("low", responses[4]["data"]!["thinkingLevel"]!.GetValue<string>(), "set_model clamps the current level");
            Equal("low", responses[7]["data"]!["thinkingLevel"]!.GetValue<string>(), "minimal and off clamp up to low");
            Equal("medium", responses[8]["data"]!["level"]!.GetValue<string>(), "cycle from the clamped level");
            // cycleModel over the scope: the scoped ":off" is explicit, clamped to each model.
            Equal("claude-haiku-4-5", responses[9]["data"]!["model"]!["id"]!.GetValue<string>(), "cycle to haiku-4-5");
            Equal("off", responses[9]["data"]!["thinkingLevel"]!.GetValue<string>(), "scoped off on haiku-4-5");
            Equal(true, responses[9]["data"]!["isScoped"]!.GetValue<bool>(), "scoped cycle");
            Equal("claude-haiku-5-5", responses[10]["data"]!["model"]!["id"]!.GetValue<string>(), "cycle to haiku-5-5");
            Equal("low", responses[10]["data"]!["thinkingLevel"]!.GetValue<string>(), "scoped off clamped on haiku-5-5");
            Equal("low", responses[11]["data"]!["thinkingLevel"]!.GetValue<string>(), "state after the cycle");
            // setThinkingLevel records the clamped level only when it changes (minimal and off on haiku-5-5 leave "low").
            var recorded = File.ReadLines(session).Skip(seeded).Select(line => JsonNode.Parse(line)!)
                .Select(entry => entry["type"]!.GetValue<string>() == "model_change" ? "model:" + entry["modelId"]!.GetValue<string>() : "thinking:" + entry["thinkingLevel"]!.GetValue<string>());
            Names(["thinking:high", "thinking:off", "model:claude-haiku-5-5", "thinking:low", "thinking:medium", "model:claude-haiku-4-5", "thinking:off",
                "model:claude-haiku-5-5", "thinking:low"], recorded, "recorded changes");
        }),
        ("thinking-clamp.tree-clone-fork-and-switch-open-clamped-sessions", async () =>
        {
            using var sandbox = new Sandbox("clamp-replace");
            var session = SeedSession(sandbox, "haiku55.jsonl", "anthropic", "claude-haiku-5-5");
            var other = SeedSession(sandbox, "other55.jsonl", "anthropic", "claude-haiku-5-5");
            // A second exchange, so a fork before its prompt keeps messages (a fork without messages opens as a new session).
            var (code, stdout, stderr) = await sandbox.Run("-p", "--session", session, "second");
            Check(code == 0 && stdout == "ok\n", $"second turn: {code} {stdout} {stderr}");
            var second = File.ReadLines(session).Select(line => JsonNode.Parse(line)!).Last(entry => Kind(entry) == "message:user")["id"]!.GetValue<string>();
            // Every session these commands open records "off" on haiku-5-5; each opens at the clamped "low" (sdk.ts createAgentSession),
            // and tree navigation keeps the session's level over the branch's (navigateTree leaves agent.state.thinkingLevel alone).
            var responses = await RpcDialog(sandbox, ["--mode", "rpc", "--session", session], seen => seen.Count switch
            {
                0 => """{"id":"1","type":"get_state"}""",
                1 => """{"id":"2","type":"pisharp_capture_navigation","mode":"tree","generation":""" + seen[0]["data"]!["pisharpGeneration"]!.ToJsonString() + "}",
                2 => """{"id":"3","type":"pisharp_select_navigation","mode":"tree","viewId":""" + seen[1]["data"]!["viewId"]!.ToJsonString() +
                    ""","targetId":"a3","generation":""" + seen[1]["data"]!["generation"]!.ToJsonString() + "}",
                3 => """{"id":"4","type":"get_state"}""",
                4 => """{"id":"5","type":"fork","entryId":""" + JsonValue.Create(second)!.ToJsonString() + "}",
                5 => """{"id":"6","type":"get_state"}""",
                6 => """{"id":"7","type":"clone"}""",
                7 => """{"id":"8","type":"get_state"}""",
                8 => """{"id":"9","type":"switch_session","sessionPath":""" + JsonValue.Create(other)!.ToJsonString() + "}",
                9 => """{"id":"10","type":"get_state"}""",
                10 => """{"id":"11","type":"set_thinking_level","level":"bogus"}""",
                11 => """{"id":"12","type":"get_state"}""",
                _ => null
            });
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "every command succeeds: " + string.Join("\n", responses.Select(r => r.ToJsonString())));
            Equal("Selected", responses[2]["data"]!["disposition"]?.GetValue<string>(), "navigated");
            foreach (var (index, what) in new[] { (0, "resumed"), (3, "after tree navigation"), (5, "fork"), (7, "clone"), (9, "switch_session") })
            {
                Equal("claude-haiku-5-5", responses[index]["data"]!["model"]!["id"]!.GetValue<string>(), what + " model");
                Equal("low", responses[index]["data"]!["thinkingLevel"]!.GetValue<string>(), what + " level");
            }
            // setThinkingLevel clamps a level the model does not list; an unknown one becomes the model's first level.
            Equal("low", responses[11]["data"]!["thinkingLevel"]!.GetValue<string>(), "unknown level clamped");
            // A turn on the clone (its branch records "off") runs at the clamped level.
            var clone = responses[7]["data"]!["sessionFile"]!.GetValue<string>();
            (code, stdout, stderr) = await sandbox.Run("-p", "--session", clone, "on the clone");
            Check(code == 0 && stdout == "ok\n", $"turn on the clone: {code} {stdout} {stderr}");
        }),
    ];

    /// <summary>An entry's type, with the message role (the first request records the tool loadout as a system message).</summary>
    private static string Kind(JsonNode entry) => entry["type"]!.GetValue<string>() is "message" ? "message:" + entry["message"]!["role"]!.GetValue<string>() : entry["type"]!.GetValue<string>();

    /// <summary>As <see cref="RpcSequence(Sandbox, string[], string[])"/>, but after a prompt's response the next command waits for that
    /// run's agent_end, so every turn has settled before the input ends.</summary>
    private static async Task<List<JsonNode>> RpcUntilIdle(Sandbox sandbox, string[] args, List<JsonNode> events, params string[] commands)
    {
        var input = new ScriptedInput(); var responses = new List<JsonNode>();
        bool answered = false, ended = false; // the outstanding prompt's response and agent_end, in either order
        void Next() { if (responses.Count < commands.Length) input.Send(commands[responses.Count]); else input.Complete(); }
        using var output = new LineOutput(line =>
        {
            lock (responses)
            {
                if (line["type"]?.GetValue<string>() != "response")
                {
                    events.Add(line);
                    if (line["type"]?.GetValue<string>() == "agent_end") { ended = true; if (answered) { answered = ended = false; Next(); } }
                    return;
                }
                responses.Add(line);
                if (line["command"]?.GetValue<string>() == "prompt" && line["success"]?.GetValue<bool>() == true)
                { answered = true; if (ended) { answered = ended = false; Next(); } }
                else { ended = false; Next(); }
            }
        });
        lock (responses) Next();
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        Equal(0, await PiCommand.RunAsync(args, host, CancellationToken.None), "rpc exit; " + stderr);
        return responses;
    }

}
