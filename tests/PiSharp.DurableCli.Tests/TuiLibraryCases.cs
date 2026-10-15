// Authored for PiSharp: what the `pisharp durable` TUI shows of the pi-durable 1.2.0 library (durable work list area l; upstream deleted its
// durable agent in v1.2.0, so these cases have no upstream twin): nested tool calls as rows under their caller's card, a tool result's
// diagnostics as plain lines (a failed command's exit code as pi shows it), images as interactive mode shows them, and the alerts for a failed
// session and for pi.live.compactionFailure.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static TuiTests;

internal static partial class Program
{
    /// <summary>A 1x1 red PNG.</summary>
    private const string PixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC";

    private static DurableNestedCall Nested(string json, bool left = false) => new(J(json), left);

    private static ImmutableSortedDictionary<long, DurableNestedCall> NestedCalls(params DurableNestedCall[] calls) =>
        calls.ToImmutableSortedDictionary(call => (long)((PiSharp.Durable.Chord.JsonNumberValue)call.Slot["taskId"]).Value, call => call);

    private static IEnumerable<(string, Func<Task>)> TuiLibraryCases() =>
    [
        Case("tui.tool.nested-calls-under-their-card", async () =>
        {
            var call = ToolCallBlock("call_9", "bash", """{"command":"run-script"}""");
            var history = new[] { UserEntry(1, "run the script"), Entry(2, "pi.assistant", AssistantWire($"[{call}]", "toolUse")) };
            const string running = """{"run":{"taskId":7,"inputs":[1]},"tools":[{"callId":"call_9","name":"bash","taskId":9,"status":"running"}]}""";
            var read = Nested("""{"callId":"call_9/a","parentCallId":"call_9","parentTaskId":9,"name":"read","taskId":10,"arguments":{"path":"a.txt"},"status":"done","summary":{"isError":false,"durationMs":12}}""");
            var grep = Nested("""{"callId":"call_9/b","parentCallId":"call_9","parentTaskId":9,"name":"grep","taskId":11,"arguments":{"pattern":"x"},"status":"running"}""");
            var ls = Nested("""{"callId":"call_9/b/c","parentCallId":"call_9/b","parentTaskId":11,"name":"ls","taskId":12,"arguments":{},"status":"done","summary":{"isError":true,"durationMs":1500,"error":"no such dir"}}""");
            await using var run = await TuiRun.Start(View(history, live: running) with { NestedCalls = NestedCalls(read, grep, ls) });
            var screen = await run.WaitFor("ls {} 1.5s");
            // pi's codemode rows under the card: the call grep made is indented under grep's row.
            ShowsInOrder(screen, ["$ run-script", "✓ read {\"path\":\"a.txt\"} 12ms", "… grep {\"pattern\":\"x\"}", "✗ ls {} 1.5s"], "rows under the card");
            Check(screen.Split('\n').Any(line => line.StartsWith("    ✗ ls", StringComparison.Ordinal)) &&
                screen.Split('\n').Any(line => line.StartsWith("  ✓ read", StringComparison.Ordinal)), "the nested call's call is indented:\n" + screen);
            ShowsInOrder(screen, [Plain(CodemodeRenderers.FormatCall(TuiStateRow(read), Themes.Current, false))], "pi's codemode row");
            // The call ends: its result arrives, the slots leave pi.live, and grep, which never finished, reads as cancelled.
            run.Source.Publish(View([.. history, Entry(3, "pi.tool-result", ToolResultWire("call_9", "bash", "script done"))], live: "{}") with
            {
                NestedCalls = NestedCalls(read, grep with { Left = true }, ls),
            });
            screen = await run.WaitFor("⊘ grep");
            ShowsInOrder(screen, ["$ run-script", "script done", "✓ read {\"path\":\"a.txt\"} 12ms", "⊘ grep {\"pattern\":\"x\"}", "✗ ls {} 1.5s"],
                "rows kept after the call");
            Check(!screen.Contains("no such dir", StringComparison.Ordinal), "the error only when expanded");
            run.Send("\u000f");
            screen = await run.WaitFor("no such dir");
            ShowsInOrder(screen, ["✗ ls {} 1.5s", "no such dir"], "the error under its row");
        }),

        Case("tui.tool.nested-calls-collapse-to-the-newest-eight", async () =>
        {
            var call = ToolCallBlock("call_9", "bash", """{"command":"many"}""");
            var history = new[] { UserEntry(1, "go"), Entry(2, "pi.assistant", AssistantWire($"[{call}]", "toolUse")) };
            var calls = Enumerable.Range(0, 10).Select(index => Nested(
                $$$"""{"callId":"call_9/{{{index}}}","parentCallId":"call_9","parentTaskId":9,"name":"read","taskId":{{{20 + index}}},"arguments":{"path":"f{{{index}}}"},"status":"done","summary":{"isError":false}}""")).ToArray();
            await using var run = await TuiRun.Start(View(history,
                live: """{"run":{"taskId":7,"inputs":[1]},"tools":[{"callId":"call_9","name":"bash","taskId":9,"status":"running"}]}""") with
            { NestedCalls = NestedCalls(calls) });
            var screen = await run.WaitFor("f9");
            Check(screen.Contains("... (2 earlier calls,", StringComparison.Ordinal) && !screen.Contains("\"f1\"", StringComparison.Ordinal) &&
                screen.Contains("\"f2\"", StringComparison.Ordinal), "the newest eight:\n" + screen);
            run.Send("\u000f");
            screen = await run.WaitFor("\"f0\"");
            Check(!screen.Contains("earlier calls", StringComparison.Ordinal), "all rows when expanded:\n" + screen);
        }),

        Case("tui.tool.diagnostics-as-plain-lines", async () =>
        {
            // pi-durable 1.2.0: a nonzero exit is an error result whose exit_code diagnostic the Harness appends in a <harness> block.
            var call = ToolCallBlock("call_5", "bash", """{"command":"make"}""");
            var result = Entry(3, "pi.tool-result", $$"""
                {"role":"toolResult","toolCallId":"call_5","toolName":"bash","content":[{{TextBlock("compile error")}},{{TextBlock("<harness>\n[error] Command exited with code 2\n</harness>")}}],"isError":true,"timestamp":1}
                """) with { Data = J("""{"diagnostics":[{"severity":"error","code":"exit_code","message":"Command exited with code 2"}]}""") };
            await using var run = await TuiRun.Start(View([UserEntry(1, "build"), Entry(2, "pi.assistant", AssistantWire($"[{call}]", "toolUse")), result]));
            var screen = await run.WaitFor("Command exited with code 2");
            Check(!screen.Contains("harness", StringComparison.Ordinal) && !screen.Contains("[error]", StringComparison.Ordinal), "plain lines:\n" + screen);
            // As pi draws a failed command: its output, a blank line, the exit code, on the error background.
            var pi = new ToolExecutionComponent("bash", "call_5", JsonNode.Parse("""{"command":"make"}"""), null,
                BuiltInToolRenderers.CreateAllToolRenderers()["bash"], StandaloneTui(), Cwd);
            pi.SetArgsComplete();
            pi.UpdateResult(new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "compile error\n\nCommand exited with code 2" }),
                ["isError"] = true,
            });
            ShowsInOrder(screen, PlainLines(pi, 120), "pi's failed command card");
            var errorBg = Themes.Current.Bg("toolErrorBg", "x")[..Themes.Current.Bg("toolErrorBg", "x").IndexOf('x')];
            Check(run.Terminal.RawOutput.Contains(errorBg, StringComparison.Ordinal), "error background");
            // A result without diagnostics, and one whose last block is not the Harness's, are shown as they are.
            var plain = Entry(4, "pi.tool-result", ToolResultWire("call_5", "bash", "<harness>\n[error] typed by the tool\n</harness>")) with
            { Data = J("""{"diagnostics":[]}""") };
            var message = (JsonObject)JsonNode.Parse(plain.Model![0].ToJson())!;
            Check(ReferenceEquals(message, DurableTuiState.DisplayResult(plain, message)), "unchanged without diagnostics");
        }),

        Case("tui.tool.images-follow-pi-settings", async () =>
        {
            var call = ToolCallBlock("call_3", "read", """{"path":"pixel.png"}""");
            var result = Entry(3, "pi.tool-result", $$"""
                {"role":"toolResult","toolCallId":"call_3","toolName":"read","content":[{"type":"image","data":"{{PixelPng}}","mimeType":"image/png"},{{TextBlock("<harness>\n[info] Read image file [image/png]\n</harness>")}}],"isError":false,"timestamp":1}
                """) with { Data = J("""{"diagnostics":[{"severity":"info","message":"Read image file [image/png]"}]}""") };
            var view = View([UserEntry(1, "look"), Entry(2, "pi.assistant", AssistantWire($"[{call}]", "toolUse")), result]);
            var fallback = TerminalImage.ImageFallback("image/png", TerminalImage.GetImageDimensions(PixelPng, "image/png"));
            // terminal.showImages off: no image even on a terminal that draws them; expanded, the text in place of the image, as interactive
            // mode shows it, and the diagnostic as a line.
            await using (var hidden = await TuiRun.Start(view, settingsJson: """{"terminal":{"images":"kitty","showImages":false}}"""))
            {
                await hidden.WaitFor("read pixel.png");
                await Task.Delay(200);
                Check(!hidden.Terminal.RawOutput.Contains(PixelPng, StringComparison.Ordinal), "no image drawn");
                hidden.Send("\u000f");
                var screen = await hidden.WaitFor(fallback);
                Check(screen.Contains("Read image file [image/png]", StringComparison.Ordinal) && !screen.Contains("harness", StringComparison.Ordinal),
                    "the diagnostic as a line:\n" + screen);
            }
            // Shown: the image itself, drawn with the terminal's protocol.
            await using (var shown = await TuiRun.Start(view, settingsJson: """{"terminal":{"images":"kitty"}}"""))
            {
                await shown.WaitFor("read pixel.png");
                await shown.WaitUntil(_ => shown.Terminal.RawOutput.Contains(PixelPng, StringComparison.Ordinal), "the image drawn");
                Check(!shown.Screen.Contains(fallback, StringComparison.Ordinal), "no fallback text");
            }
        }),

        Case("tui.alert.session-failed", async () =>
        {
            const string busy = """{"run":{"taskId":7,"inputs":[1]}}""";
            await using var run = await TuiRun.Start(View([UserEntry(1, "work")], live: busy));
            await run.WaitFor("Working... (esc to abort)");
            run.Source.Publish(View([UserEntry(1, "work")], live: busy) with { Failure = "disk is full" });
            var message = DurableFailure.Message("disk is full");
            var screen = await run.WaitUntil(text => Flat(text).Contains(message, StringComparison.Ordinal), "the failure");
            Check(!screen.Contains("Working...", StringComparison.Ordinal), "nothing runs any more:\n" + screen);
            var errorColor = Themes.Current.Fg("error", "x")[..Themes.Current.Fg("error", "x").IndexOf('x')];
            Check(run.Terminal.RawOutput.Contains(errorColor + "Session failed: disk is full.", StringComparison.Ordinal), "error color");
            // The editor sends nothing more.
            await run.Submit("are you there?");
            run.Send("later");
            run.Send(FollowUpKey);
            await Task.Delay(300);
            Equal(0, run.Controller.Calls.Count, "calls: " + string.Join("; ", run.Controller.Calls));
            run.Send("\u0003");
            Equal(1, await run.Exited(), "exit code");
        }),

        Case("tui.alert.compaction-failure", async () =>
        {
            await using var run = await TuiRun.Start(View([UserEntry(1, "q")]));
            run.Source.Publish(View([UserEntry(1, "q")], live: """{"compactionFailure":{"taskId":3,"reason":"threshold","error":"summarizer down"}}"""));
            await run.WaitFor("Compaction failed (threshold): summarizer down");
            Check(run.Terminal.RawOutput.Contains(Themes.Current.Fg("error", "Compaction failed (threshold): summarizer down"), StringComparison.Ordinal),
                "error color");
            // A compaction that produced a summary clears it.
            run.Source.Publish(View([UserEntry(1, "q")], live: "{}"));
            await run.WaitUntil(text => !text.Contains("Compaction failed", StringComparison.Ordinal), "cleared");
            run.Send("\u0003");
            Equal(0, await run.Exited(), "a healthy session exits with 0");
        }),
    ];

    private static JsonObject TuiStateRow(DurableNestedCall call) => DurableTuiState.NestedRow(call);
}
