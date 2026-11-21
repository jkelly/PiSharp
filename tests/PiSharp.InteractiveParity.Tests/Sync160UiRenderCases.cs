// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/assistant-message.test.ts and user-message.test.ts (the #9549 cases Pi v1.6.0
// added) and packages/coding-agent/src/modes/interactive/components/tool-execution.ts (#8036, which upstream fixed without a test).
// Ported expectations, authored against the native port; not an upstream execution capture. Cases marked "native" have no upstream
// counterpart.
using System.Diagnostics;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>Sync 1.6.0 changes that the native code already had: OSC 133 markers on render output that is reused across frames,
/// and a self-rendered tool result with hundreds of thousands of lines.</summary>
internal static class Sync160UiRenderCases
{
    private const string Osc133ZoneStart = "\u001b]133;A\u0007";
    private const string Osc133ZoneEnd = "\u001b]133;B\u0007";

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---------------------------------------------------------------- assistant-message.test.ts, user-message.test.ts
        // #9549: container output is reused across frames, so markers must not be added to it in place.
        yield return ("sync160.ui.assistant-message.adds-osc-133-zone-markers-once-across-repeated-renders", Sync(() =>
        {
            var component = new AssistantMessageComponent(new JsonObject { ["role"] = "assistant", ["stopReason"] = "stop",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hello" }) });
            var first = component.Render(40);
            var second = component.Render(40);
            Check(ReferenceEquals(first, second), "the same lines are returned while nothing changed");
            Equal(1, Count(second[0], Osc133ZoneStart), "zone start markers");
            Equal(1, Count(second[^1], Osc133ZoneEnd), "zone end markers");
        }));
        yield return ("sync160.ui.user-message.adds-osc-133-zone-markers-once-across-repeated-renders", Sync(() =>
        {
            var component = new UserMessageComponent("hello");
            var first = component.Render(20);
            var second = component.Render(20);
            Check(ReferenceEquals(first, second), "the same lines are returned while nothing changed");
            Equal(1, Count(second[0], Osc133ZoneStart), "zone start markers");
            Equal(1, Count(second[2], Osc133ZoneEnd), "zone end markers");
        }));

        // ---------------------------------------------------------------- tool-execution.ts (#8036)
        // Upstream crashed with "RangeError: Maximum call stack size exceeded" because it spread the wrapped lines of a self-rendered
        // result into one push call. The native list has no argument limit; this renders the diff upstream verified its fix with
        // (two lines of about 12 MB at 100 columns, about 240,000 wrapped lines) and checks that every line arrives, twice.
        yield return ("sync160.ui.tool-execution.native-a-very-large-edit-diff-renders", Sync(() =>
        {
            const int lineLength = 12_000_000, width = 100;
            var loop = UiLoop.CreateManual();
            var tui = new TuiMainScreen(new Sync160UiTerminal(), loop, environment: _ => null);
            var diff = "-1 " + new string('a', lineLength) + "\n+1 " + new string('b', lineLength);
            var component = new ToolExecutionComponent("edit", "big-diff", new JsonObject { ["path"] = "export.html",
                    ["edits"] = new JsonArray(new JsonObject { ["oldText"] = "a", ["newText"] = "b" }) },
                null, BuiltInToolRenderers.ForToolDefinition("edit"), tui, "/");
            component.UpdateResult(new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Successfully replaced 1 block(s) in export.html." }),
                ["details"] = new JsonObject { ["diff"] = diff, ["firstChangedLine"] = 1 }, ["isError"] = false,
            });
            var watch = Stopwatch.StartNew();
            var lines = component.Render(width);
            var elapsed = watch.Elapsed;
            Check(lines.Count > 2 * lineLength / width, $"every wrapped line is returned: {lines.Count}");
            Equal("", lines[0], "the leading empty line of a self-rendered result");
            var last = Strip(lines.Last(line => Strip(line).Trim().Length > 0)).TrimEnd();
            Check(last.EndsWith('b'), "the last line with text is the end of the added line: " + (last.Length > 40 ? last[^40..] : last));
            Equal(lines.Count, component.Render(width).Count, "a second render (a resumed session renders it on every frame)");
            Check(elapsed < TimeSpan.FromSeconds(60), $"rendered in {elapsed.TotalSeconds:F1}s");
        }));
    }
}
