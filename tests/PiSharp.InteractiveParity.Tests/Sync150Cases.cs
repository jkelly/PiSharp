// Pi 1.5.0 sync, core-session rows: markdown transformer entry identity (user-message.test.ts "passes persisted entry identity in
// transformer context; live leaves it undefined", interactive-mode.ts renderSessionEntries messageMeta) and extension keybinding
// hints (extension-keybinding-hints.test.ts). Expectations are authored from reading the v1.5.0 sources and tests; nothing is
// captured from an upstream run.
#pragma warning disable PISHARP_EXTUI001 // Pi 1.5.0 keybinding hints are [Experimental].
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Tui.Pi;
using static Expect;

internal static class Sync150Cases
{
    private const string Expected = "ctrl+e | ctrl+e to expand";

    /// <summary>A user override proves the hints read the host's configured keybindings, not the defaults.</summary>
    private static void UseExpandOverride() => KeybindingsManager.SetGlobal(new KeybindingsManager(AppKeybindings.Definitions(_ => null),
        new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal) { ["app.tools.expand"] = ["ctrl+e"] }));

    private static string HintText(Func<string, string> keyText, Func<string, string, string> keyHint) =>
        $"{keyText("app.tools.expand")} | {Strip(keyHint("app.tools.expand", "to expand"))}";

    private static JsonObject Assistant(string text) => new()
    {
        ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["api"] = "openai-responses",
        ["provider"] = "openai", ["model"] = "gpt-4o-mini", ["stopReason"] = "stop", ["timestamp"] = 0,
    };

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("sync150.user-message-transformer-context-carries-entry-identity", Sync(() =>
        {
            var captured = new List<(string? Id, string? Timestamp)>();
            MarkdownTransformer capture = (markdown, context) => { captured.Add((context.MessageId, context.Timestamp)); return markdown; };
            new UserMessageComponent("restored", null, 1, [capture], "entry-abc", "2025-01-15T10:30:00.000Z").Render(80);
            new UserMessageComponent("live", null, 1, [capture]).Render(80);
            Equal("entry-abc@2025-01-15T10:30:00.000Z,@", string.Join(",", captured.Select(item => item.Id + "@" + item.Timestamp)), "restored then live");
        }));

        yield return ("sync150.assistant-message-transformer-context-carries-entry-identity", Sync(() =>
        {
            var captured = new List<MarkdownTransformContext>();
            MarkdownTransformer capture = (markdown, context) => { captured.Add(context); return markdown; };
            new AssistantMessageComponent(Assistant("restored"), false, null, "Thinking...", 1, [capture], "entry-def", "2025-01-15T10:31:00.000Z").Render(80);
            new AssistantMessageComponent(Assistant("live"), false, null, "Thinking...", 1, [capture]).Render(80);
            Equal(new MarkdownTransformContext("assistant", false, 78, "entry-def", "2025-01-15T10:31:00.000Z"), captured[0], "restored");
            Equal(new MarkdownTransformContext("assistant", false, 78), captured[^1], "live");
        }));

        // The context the Node bridge hands to TypeScript transformers: messageId and timestamp stay undefined for live messages.
        yield return ("sync150.bridged-transformer-context-carries-entry-identity", Sync(() =>
        {
            Equal("""{"messageType":"user","isStreaming":false,"availableWidth":78,"messageId":"entry-abc","timestamp":"2025-01-15T10:30:00.000Z"}""",
                InteractiveHostServices.MarkdownContextJson(new("user", false, 78, "entry-abc", "2025-01-15T10:30:00.000Z")).ToJsonString(), "persisted");
            Equal("""{"messageType":"assistant","isStreaming":true,"availableWidth":40}""",
                InteractiveHostServices.MarkdownContextJson(new("assistant", true, 40)).ToJsonString(), "live");
        }));

        // interactive-mode.ts renderSessionEntries: messages rebuilt from saved entries carry their entry's id and timestamp; a new turn's
        // live messages carry none.
        yield return ("sync150.resumed-session-transformers-see-entry-identity", async () =>
        {
            await using var pi = new InteractiveHarness("sync150-entry-identity");
            var cwd = JsonSerializer.Serialize(pi.Cwd);
            var session = pi.Write("entry-identity.jsonl", string.Join("\n",
                "{\"type\":\"session\",\"version\":3,\"id\":\"01a00000-0000-7000-8000-0000000150a1\",\"timestamp\":\"2026-10-09T10:00:00.000Z\",\"cwd\":" + cwd + "}",
                "{\"type\":\"message\",\"id\":\"b1\",\"parentId\":null,\"timestamp\":\"2026-10-09T10:00:00.001Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"saved question\"}],\"timestamp\":1}}",
                "{\"type\":\"message\",\"id\":\"b2\",\"parentId\":\"b1\",\"timestamp\":\"2026-10-09T10:00:00.002Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"saved answer\"}],\"api\":\"anthropic-messages\",\"provider\":\"anthropic\",\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input\":1,\"output\":1,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":2,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}},\"stopReason\":\"stop\",\"timestamp\":2}}") + "\n");
            var seen = new List<(string Markdown, string? Id, string? Timestamp)>();
            pi.Configure = context => context with
            {
                MarkdownTransformers = () => [(markdown, transform) => { lock (seen) seen.Add((markdown, transform.MessageId, transform.Timestamp)); return markdown; }]
            };
            pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular", "--session", session);
            await pi.WaitFor("saved answer");
            await pi.Submit("live question");
            await pi.WaitFor("Hello from the fake model.");
            List<(string Markdown, string? Id, string? Timestamp)> rows;
            lock (seen) rows = [.. seen];
            string Of(string text) => string.Join(",", rows.Where(row => row.Markdown.Contains(text, StringComparison.Ordinal)).Select(row => row.Id + "@" + row.Timestamp).Distinct());
            Equal("b1@2026-10-09T10:00:00.001Z", Of("saved question"), "saved user message");
            Equal("b2@2026-10-09T10:00:00.002Z", Of("saved answer"), "saved assistant message");
            Equal("@", Of("live question"), "live user message");
            Equal("@", Of("Hello from the fake model."), "live assistant message");
            Equal(0, await pi.Quit(), "exit code");
        });

        yield return ("sync150.message-and-entry-render-options-carry-host-key-hints", Sync(() =>
        {
            UseExpandOverride();
            var seen = new List<string>();
            var message = new JsonObject { ["role"] = "custom", ["customType"] = "test", ["content"] = "custom", ["display"] = true, ["timestamp"] = 0 };
            new CustomMessageComponent(message, (_, options, _) => { seen.Add("message " + HintText(options.KeyText, options.KeyHint)); return new Text("custom", 0, 0); });
            var entry = new JsonObject { ["type"] = "custom", ["id"] = "entry-1", ["parentId"] = null, ["timestamp"] = "2026-10-09T10:00:00.000Z", ["customType"] = "test" };
            new CustomEntryComponent(entry, (_, options, _) => { seen.Add("entry " + HintText(options.KeyText, options.KeyHint)); return new Text("custom", 0, 0); });
            Equal("message " + Expected + ";entry " + Expected, string.Join(";", seen), "render options");
        }));

        yield return ("sync150.extension-tool-render-context-carries-host-key-hints", async () =>
        {
            UseExpandOverride();
            var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var renderers = ExtensionToolRendererAdapter.ToToolRenderers(new ExtensionToolRenderers(null, (context, _, _) =>
            {
                seen.TrySetResult(context.Keybindings is { } hints ? HintText(hints.KeyText, hints.KeyHint) : "no hints");
                return Task.FromResult(new ExtensionCustomComponentRows(["call"], [4]));
            }));
            var component = renderers.RenderCall!(new JsonObject(), Themes.Current, new ToolRenderContext { ToolName = "custom_tool", ToolCallId = "tool-1", Cwd = "/" });
            component.Render(80);
            Equal(Expected, await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)), "tool render context");
        });
    }
}
