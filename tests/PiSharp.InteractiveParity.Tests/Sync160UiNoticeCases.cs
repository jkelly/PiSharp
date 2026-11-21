// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/interactive-mode-assistant-diagnostics.test.ts (all four cases, with the
// reasons and the OpenAI notice Pi v1.6.0 added) over interactive-mode.ts getDroppedReasoning and maybeShowThinkingDropNotice.
// Ported expectations, authored against the native port; not an upstream execution capture. Cases marked "native" have no upstream
// counterpart.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using static Expect;

/// <summary>Sync 1.6.0: the transcript notice for reasoning a provider withheld from a request (Anthropic thinking blocks, OpenAI
/// reasoning items), with its reasons, shown only for new losses and only while showCacheMissNotices is on.</summary>
internal static class Sync160UiNoticeCases
{
    /// <summary>The upstream fixture message: three thinking blocks dropped for one reason.</summary>
    private static JsonObject AnthropicMessage(int timestamp = 1, int dropped = 3, params string[] reasons)
    {
        var transformations = new JsonArray();
        for (var i = 0; i < dropped; i++)
            transformations.Add(new JsonObject { ["type"] = "thinking_dropped", ["path"] = $"messages.{2 + 3 * i}.content.0",
                ["reason"] = reasons.Length == 0 ? "prefix_binding_mismatch" : reasons[Math.Min(i, reasons.Length - 1)] });
        return Assistant("anthropic-messages", "anthropic", "claude-fable-5-1", timestamp, "anthropic_input_transformations", transformations);
    }

    /// <summary>interactive-mode-assistant-diagnostics.test.ts openAIMessage: the diagnostic as openai-responses-shared.ts
    /// appendReasoningRemovals writes it (type, id, action and reason of each removed reasoning item).</summary>
    private static JsonObject OpenAIMessage(string[] ids, string[] reasons)
    {
        var transformations = new JsonArray();
        for (var i = 0; i < ids.Length; i++)
            transformations.Add(new JsonObject { ["type"] = "reasoning", ["id"] = ids[i], ["action"] = "removed", ["reason"] = reasons[Math.Min(i, reasons.Length - 1)] });
        return Assistant("openai-responses", "openai", "gpt-6.2-sol", 3, "openai_input_transformations", transformations);
    }

    private static JsonObject Assistant(string api, string provider, string model, int timestamp, string diagnosticType, JsonArray transformations) => new()
    {
        ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "survived" }),
        ["api"] = api, ["provider"] = provider, ["model"] = model,
        ["usage"] = new JsonObject { ["input"] = 1, ["output"] = 1, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["totalTokens"] = 2 },
        ["stopReason"] = "stop", ["timestamp"] = timestamp,
        ["diagnostics"] = new JsonArray(new JsonObject { ["type"] = diagnosticType, ["timestamp"] = timestamp,
            ["details"] = new JsonObject { ["transformations"] = transformations } }),
    };

    /// <summary>sessionManager.getBranch(): one persisted assistant message.</summary>
    private static List<JsonObject> Branch(params JsonObject[] messages) =>
        [.. messages.Select((message, index) => new JsonObject { ["type"] = "message", ["id"] = $"e{index}", ["message"] = message.DeepClone() })];

    private static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";

    /// <summary>An Anthropic text response whose message_start reports <paramref name="transformations"/> (input_transformations).</summary>
    private static HttpResponseMessage AnthropicTextWithTransformations(string text, object[] transformations) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(
            Frame("message_start", new { type = "message_start", message = new { id = "msg_1", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(),
                usage = new { input_tokens = 3, output_tokens = 0 }, input_transformations = transformations } })
            + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
            + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })
            + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
            + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 2 } })
            + Frame("message_stop", new { type = "message_stop" }), Encoding.UTF8, "text/event-stream")
    };

    private static object[] Dropped(int count, params string[] reasons) => [.. Enumerable.Range(0, count).Select(index => (object)new
        { type = "thinking_dropped", path = $"messages.{index}.content.0", reason = reasons[Math.Min(index, reasons.Length - 1)] })];

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static readonly string[] Regular = ["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular"];

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---------------------------------------------------------------- interactive-mode-assistant-diagnostics.test.ts
        // Upstream calls maybeShowThinkingDropNotice on a fake mode; the text and the comparison with the previous response are
        // InteractiveMode.ReasoningDropNotice here, and the showCacheMissNotices gate and the chat line are the e2e cases below.
        yield return ("sync160.ui.notice.anthropic-thinking-drops-name-the-reason", Sync(() =>
        {
            Equal("Anthropic dropped 3 thinking blocks: prefix_binding_mismatch (details in session)",
                InteractiveMode.ReasoningDropNotice(AnthropicMessage(), []), "three blocks, one reason");
            Equal("Anthropic dropped 1 thinking block: prefix_binding_mismatch (details in session)",
                InteractiveMode.ReasoningDropNotice(AnthropicMessage(dropped: 1), []), "singular");
        }));
        yield return ("sync160.ui.notice.does-not-repeat-unchanged-anthropic-thinking-drops", Sync(() =>
        {
            Equal(null, InteractiveMode.ReasoningDropNotice(AnthropicMessage(timestamp: 2), Branch(AnthropicMessage())), "same count as the previous response");
            Equal("Anthropic dropped 4 thinking blocks: prefix_binding_mismatch (details in session)",
                InteractiveMode.ReasoningDropNotice(AnthropicMessage(timestamp: 2, dropped: 4), Branch(AnthropicMessage())), "one more than the previous response");
        }));
        yield return ("sync160.ui.notice.shows-openai-reasoning-removals-with-their-reasons", Sync(() =>
        {
            // The previous Anthropic response dropped more blocks; that must not hide the OpenAI removals.
            Equal("OpenAI dropped 2 reasoning items: prefix_mismatch, organization_mismatch (details in session)",
                InteractiveMode.ReasoningDropNotice(OpenAIMessage(["rs_1", "rs_2"], ["prefix_mismatch", "organization_mismatch"]), Branch(AnthropicMessage())),
                "after an Anthropic response");
        }));
        yield return ("sync160.ui.notice.shows-openai-reasoning-removals-only-when-new-items-are-removed", Sync(() =>
        {
            // OpenAI reports a removed item again on every later request that replays it.
            var branch = Branch(OpenAIMessage(["rs_1", "rs_2"], ["prefix_mismatch"]));
            Equal(null, InteractiveMode.ReasoningDropNotice(OpenAIMessage(["rs_1", "rs_2"], ["prefix_mismatch"]), branch), "the same two items");
            Equal("OpenAI dropped 3 reasoning items: prefix_mismatch (details in session)",
                InteractiveMode.ReasoningDropNotice(OpenAIMessage(["rs_1", "rs_2", "rs_3"], ["prefix_mismatch"]), branch), "a third item");
        }));
        // Native: getDroppedReasoning on session data that is not what a provider writes.
        yield return ("sync160.ui.notice.native-untyped-diagnostics", Sync(() =>
        {
            Equal(null, InteractiveMode.GetDroppedReasoning(new JsonObject { ["role"] = "assistant" }), "no diagnostics");
            var message = OpenAIMessage(["rs_1"], ["prefix_mismatch"]);
            var transformations = (JsonArray)message["diagnostics"]![0]!["details"]!["transformations"]!;
            transformations.Add("text");                                                              // not an object
            transformations.Add(new JsonObject { ["type"] = "function_call", ["action"] = "removed" }); // another item type
            transformations.Add(new JsonObject { ["type"] = "reasoning", ["reason"] = 7 });            // a reason that is not a string
            transformations.Add(new JsonObject { ["type"] = "reasoning", ["reason"] = "prefix_mismatch" });
            var dropped = InteractiveMode.GetDroppedReasoning(message)!;
            Equal(3, dropped.Count, "reasoning entries");
            Equal("prefix_mismatch", string.Join(",", dropped.Reasons), "distinct string reasons");
            Equal("OpenAI dropped 3 reasoning items: prefix_mismatch (details in session)", InteractiveMode.ReasoningDropNotice(message, []), "notice");
            // Without a reason the notice has no colon part; a diagnostic whose list holds nothing dropped is passed over.
            var unexplained = AnthropicMessage();
            foreach (var entry in (JsonArray)unexplained["diagnostics"]![0]!["details"]!["transformations"]!) ((JsonObject)entry!).Remove("reason");
            Equal("Anthropic dropped 3 thinking blocks (details in session)", InteractiveMode.ReasoningDropNotice(unexplained, []), "no reasons");
            var kept = AnthropicMessage();
            kept["diagnostics"]![0]!["details"]!["transformations"] = new JsonArray(new JsonObject { ["type"] = "kept" });
            ((JsonArray)kept["diagnostics"]!).Add(message["diagnostics"]![0]!.DeepClone());
            Equal("OpenAI", InteractiveMode.GetDroppedReasoning(kept)!.Diagnostic.Provider, "the first diagnostic with a dropped block counts");
        }));
        // The chat line, the showCacheMissNotices gate and the comparison with the persisted previous response, through the
        // Anthropic transport (input_transformations on message_start).
        yield return ("sync160.ui.notice.e2e-anthropic-notice-is-shown-for-new-drops-when-cache-miss-notices-are-on", async () =>
        {
            await using var pi = new InteractiveHarness("sync160-drop-notice", columns: 140);
            pi.Write(Path.Combine(pi.AgentDir, "settings.json"), """{"showCacheMissNotices":true}""");
            pi.Respond = (_, index) => index switch
            {
                0 => AnthropicTextWithTransformations("first answer", Dropped(3, "prefix_binding_mismatch")),
                1 => AnthropicTextWithTransformations("second answer", Dropped(3, "prefix_binding_mismatch")),
                _ => AnthropicTextWithTransformations("third answer", Dropped(4, "prefix_binding_mismatch", "prefix_binding_mismatch", "prefix_binding_mismatch", "model_binding_mismatch")),
            };
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("one");
            await pi.WaitFor("Anthropic dropped 3 thinking blocks: prefix_binding_mismatch (details in session)");
            await pi.Submit("two");
            await pi.WaitFor("second answer");
            await Task.Delay(300);
            Equal(1, Count(pi.Terminal.Text, "Anthropic dropped"), "the unchanged drops are not reported again: " + pi.Terminal.Text);
            await pi.Submit("three");
            await pi.WaitFor("Anthropic dropped 4 thinking blocks: prefix_binding_mismatch, model_binding_mismatch (details in session)");
            Equal(0, await pi.Quit(), "exit code");
        });
        yield return ("sync160.ui.notice.e2e-no-notice-while-cache-miss-notices-are-off", async () =>
        {
            await using var pi = new InteractiveHarness("sync160-drop-notice-off");
            pi.Respond = (_, _) => AnthropicTextWithTransformations("first answer", Dropped(3, "prefix_binding_mismatch"));
            pi.Start(Regular);
            await pi.WaitFor("escape interrupt");
            await pi.Submit("one");
            await pi.WaitFor("first answer");
            await Task.Delay(300);
            Check(!pi.Terminal.Text.Contains("dropped", StringComparison.Ordinal), "no notice: " + pi.Terminal.Text);
            // The diagnostic itself is in the session either way ("details in session").
            Contains(InteractiveHarness.ReadShared(pi.SessionFiles().Single()), "anthropic_input_transformations", "session file");
            Equal(0, await pi.Quit(), "exit code");
        });
    }
}
