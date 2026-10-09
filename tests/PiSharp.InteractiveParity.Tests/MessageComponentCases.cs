using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>
/// Message and small shared components of coding-agent/src/modes/interactive/components: dynamic-border, themed-text,
/// countdown-timer, pi-logo, bordered-loader, status-indicator, visual-truncate, user-message, assistant-message, custom-message,
/// custom-entry, skill-invocation-message, branch-summary-message, compaction-summary-message, markdown-transform,
/// earendil-announcement and auth-url. Expectations port assistant-message.test.ts, user-message.test.ts, custom-message.test.ts,
/// collapsible-message-components.test.ts, status-indicator.test.ts (the indicator side; the editor embedding belongs to the
/// custom-editor port), themed-text.test.ts and auth-url-copy.test.ts (the AuthUrlComponent side), plus render snapshots authored
/// from reading the sources.
/// </summary>
internal static class MessageComponentCases
{
    private const string Osc133ZoneStart = "\u001b]133;A\u0007";
    private const string Osc133ZoneEnd = "\u001b]133;B\u0007";
    private const string Osc133ZoneFinal = "\u001b]133;C\u0007";
    private const string BgReset = "\u001b[49m";
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static Theme T => Themes.Current;

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // ---------------------------------------------------------------- dynamic-border.ts
        yield return ("msg.dynamic-border.render", Sync(() =>
        {
            var border = new DynamicBorder();
            Equal(T.Fg("border", "─────"), border.Render(5).Single(), "default border color");
            Lines(["─"], border.Render(0), "minimum width 1");
            Equal("<───>", new DynamicBorder(s => "<" + s + ">").Render(3).Single(), "custom color function");
        }));

        // ---------------------------------------------------------------- themed-text.ts
        yield return ("msg.themed-text.rebuilds-after-invalidate", Sync(() =>
        {
            // themed-text.test.ts: builds lazily and rebuilds with the current theme after invalidation.
            var builds = 0;
            var text = new ThemedText(() => { builds++; return T.Fg("accent", "hello"); });
            Equal(0, builds, "lazy build");
            var dark = string.Join("", text.Render(20));
            Themes.InitTheme("light");
            Equal(dark, string.Join("", text.Render(20)), "stale until invalidated");
            text.Invalidate();
            Contains(string.Join("", text.Render(20)), T.GetFgAnsi("accent"), "rebuilt with light accent");
            Equal(2, builds, "build count");
        }));
        yield return ("msg.themed-text.render", Sync(() =>
        {
            Lines(["", " hi", ""], new ThemedText(() => "hi").Render(10), "default padding 1/1");
            Lines(["hi"], new ThemedText(() => "hi", 0, 0).Render(10), "no padding");
        }));

        // ---------------------------------------------------------------- countdown-timer.ts
        yield return ("msg.countdown-timer.ticks-and-expires", Sync(() =>
        {
            Action? tick = null; var disposed = 0;
            var ticks = new List<int>(); var expired = 0;
            var tui = new FakeTui();
            using var timer = new CountdownTimer(2500, tui, ticks.Add, () => expired++, (action, ms) =>
            {
                Equal(1000.0, ms, "interval");
                tick = action;
                return new DisposeAction(() => disposed++);
            });
            Equal("3", string.Join(",", ticks), "initial tick is ceil(2500/1000)");
            tick!(); tick!();
            Equal("3,2,1", string.Join(",", ticks), "ticks down");
            Equal(0, expired, "not expired yet");
            Equal(2, tui.RenderRequests, "render per tick");
            tick!();
            Equal("3,2,1,0", string.Join(",", ticks), "reaches zero");
            Equal(1, expired, "expired");
            Equal(1, disposed, "interval cleared on expiry");
            timer.Dispose();
            Equal(1, disposed, "dispose after expiry is a no-op");
        }));

        // ---------------------------------------------------------------- pi-logo.ts
        yield return ("msg.pi-logo.lines", Sync(() =>
        {
            var lines = PiLogo.PiLogoLines();
            Lines(["▀▀█ ", "█▀ █"], lines, "logo", trimEnd: false);
            Equal(4, TextUtils.VisibleWidth(lines[0]), "top width");
            Equal(4, TextUtils.VisibleWidth(lines[1]), "bottom width");
            Equal("\u001b[38;2;228;138;122m\u001b[48;2;79;142;179m▀\u001b[0m\u001b[38;2;228;138;122m▀█\u001b[0m ", lines[0], "top truecolor");
            Equal("\u001b[38;2;79;142;179m█▀\u001b[0m \u001b[38;2;234;182;93m█\u001b[0m", lines[1], "bottom truecolor");
            Equal("\u001b[38;2;228;138;122mP\u001b[0m\u001b[38;2;234;182;93mi\u001b[0m", PiLogo.PiWordmark(), "wordmark");
            var previous = PiLogo.Environment;
            try
            {
                PiLogo.Environment = name => name == "TERM_PROGRAM" ? "Apple_Terminal" : null;
                Equal(!OperatingSystem.IsMacOS(), PiLogo.SupportsPiLogo(), "Apple Terminal only on macOS");
                PiLogo.Environment = _ => null;
                Equal(true, PiLogo.SupportsPiLogo(), "other terminals");
            }
            finally { PiLogo.Environment = previous; }
        }));

        // ---------------------------------------------------------------- bordered-loader.ts
        yield return ("msg.bordered-loader.render-cancellable", Sync(() =>
        {
            var tui = new FakeTui();
            var loader = new BorderedLoader(tui, T, "Working");
            try
            {
                Lines([new string('─', 24), "", " ⠋ Working", "", " escape/ctrl+c cancel", "", new string('─', 24)], loader.Render(24), "cancellable");
                var aborted = 0;
                loader.OnAbort = () => aborted++;
                Check(!loader.Signal.IsCancellationRequested, "not aborted");
                loader.HandleInput("x");
                Equal(0, aborted, "other keys ignored");
                loader.HandleInput("\u001b");
                Equal(1, aborted, "escape aborts");
                Check(loader.Signal.IsCancellationRequested, "signal aborted");
            }
            finally { loader.Dispose(); }
        }));
        yield return ("msg.bordered-loader.render-not-cancellable", Sync(() =>
        {
            var loader = new BorderedLoader(new FakeTui(), T, "Loading", cancellable: false);
            try
            {
                Lines([new string('─', 20), "", " ⠋ Loading", "", new string('─', 20)], loader.Render(20), "not cancellable");
                loader.HandleInput("\u001b");
                Check(!loader.Signal.IsCancellationRequested, "escape ignored");
            }
            finally { loader.Dispose(); }
        }));

        // ---------------------------------------------------------------- status-indicator.ts
        yield return ("msg.status-indicator.idle-height", Sync(() =>
        {
            // status-indicator.test.ts: keeps idle status at the same height as standalone status indicators.
            var lines = new IdleStatus().Render(20);
            Equal(2, lines.Count, "height");
            Check(lines.All(line => line == new string(' ', 20)), "blank lines of the full width");
        }));
        yield return ("msg.status-indicator.standalone-colors", Sync(() =>
        {
            // status-indicator.test.ts: the standalone line keeps the accent spinner and muted message.
            var indicator = new WorkingStatusIndicator(new FakeTui(), "Working");
            try
            {
                var lines = indicator.Render(20);
                Lines(["", " ⠋ Working"], lines, "standalone render");
                Contains(lines[1], T.GetFgAnsi("accent"), "accent spinner");
                Contains(lines[1], T.GetFgAnsi("muted"), "muted message");
                Equal(StatusIndicatorKind.Working, indicator.Kind, "kind");
            }
            finally { indicator.Dispose(); }
        }));
        yield return ("msg.status-indicator.render-in-border", Sync(() =>
        {
            // status-indicator.test.ts: "── ⠋ Working ───" embeds RenderInBorder; with the border color both parts use it.
            var color = T.GetThinkingBorderColor("high");
            var indicator = new WorkingStatusIndicator(new FakeTui(), "Working", null, text => color(text));
            try
            {
                var inBorder = indicator.RenderInBorder(20);
                Equal("⠋ Working", Strip(inBorder), "embedded label");
                Equal(3, inBorder.Split(T.GetFgAnsi("thinkingHigh")).Length, "spinner and message use the border color");
                // The loader text wraps at the narrow width first, so only the first wrapped line (the spinner) is embedded.
                Equal("⠋", Strip(indicator.RenderInBorder(5)), "first wrapped line at a narrow width");
                Equal("⠋", Strip(indicator.RenderInBorder(8)), "one column short still wraps");
                Equal("⠋ Working", Strip(indicator.RenderInBorder(9)), "exact width");
                Equal("⠋", Strip(indicator.RenderSpinnerInBorder(5)), "spinner only");
                Equal("", Strip(indicator.RenderSpinnerInBorder(0)), "spinner at zero width");
            }
            finally { indicator.Dispose(); }
            var custom = new WorkingStatusIndicator(new FakeTui(), "Busy", new LoaderIndicatorOptions(["*"]));
            try { Equal("* Busy", Strip(custom.RenderInBorder(20)), "custom frames are verbatim"); }
            finally { custom.Dispose(); }
        }));
        yield return ("msg.status-indicator.labels", Sync(() =>
        {
            // status-indicator.test.ts: compaction, summary and retry labels fit within the border width.
            var tui = new FakeTui();
            var indicators = new (StatusIndicator Indicator, StatusIndicatorKind Kind, string Label)[]
            {
                (new CompactionStatusIndicator(tui, CompactionStatusReason.Manual), StatusIndicatorKind.Compaction, "Compacting context... (escape to cancel)"),
                (new CompactionStatusIndicator(tui, CompactionStatusReason.Threshold), StatusIndicatorKind.Compaction, "Auto-compacting... (escape to cancel)"),
                (new CompactionStatusIndicator(tui, CompactionStatusReason.Overflow), StatusIndicatorKind.Compaction, "Context overflow detected, Auto-compacting... (escape to cancel)"),
                (new BranchSummaryStatusIndicator(tui), StatusIndicatorKind.BranchSummary, "Summarizing branch... (escape to cancel)"),
                (new RetryStatusIndicator(tui, 1, 3, 3000), StatusIndicatorKind.Retry, "Retrying (1/3) in 3s... (escape to cancel)"),
            };
            try
            {
                foreach (var (indicator, kind, label) in indicators)
                {
                    Equal(kind, indicator.Kind, "kind of " + label);
                    var standalone = TextUtils.JsTrim(Strip(indicator.Render(120)[1]));
                    Equal("⠋ " + label, standalone, "standalone label");
                    Equal(standalone, Strip(indicator.RenderInBorder(120)), "border label");
                    foreach (var width in new[] { 1, 4, 10, 20, 80, 120 })
                        Check(TextUtils.VisibleWidth(indicator.RenderInBorder(width)) <= width, $"fits width {width}");
                }
                Contains(indicators[4].Indicator.Render(80)[1], T.GetFgAnsi("warning"), "retry spinner uses warning");
            }
            finally { foreach (var entry in indicators) entry.Indicator.Dispose(); }
        }));
        yield return ("msg.status-indicator.retry-countdown", async () =>
        {
            // status-indicator.test.ts: after one second the retry label counts down.
            var tui = new FakeTui();
            var indicator = new RetryStatusIndicator(tui, 1, 3, 3000);
            try
            {
                await Task.Delay(1150);
                tui.Loop.RunPending();
                Contains(Strip(indicator.RenderInBorder(120)), "Retrying (1/3) in 2s", "counted down");
            }
            finally { indicator.Dispose(); }
        });
        yield return ("msg.status-indicator.retry-dispose", async () =>
        {
            // status-indicator.test.ts: disposes retry countdown updates.
            var tui = new FakeTui();
            var indicator = new RetryStatusIndicator(tui, 1, 3, 1000);
            var before = tui.RenderRequests;
            indicator.Dispose();
            await Task.Delay(1150);
            tui.Loop.RunPending();
            Equal(before, tui.RenderRequests, "no render requests after dispose");
        });

        // ---------------------------------------------------------------- visual-truncate.ts
        yield return ("msg.visual-truncate.lines", Sync(() =>
        {
            var empty = VisualTruncate.TruncateToVisualLines("", 3, 10);
            Equal(0, empty.VisualLines.Count + empty.SkippedCount, "empty text");
            var fits = VisualTruncate.TruncateToVisualLines("a\nb", 3, 10);
            Lines(["a", "b"], fits.VisualLines, "fits");
            Equal(0, fits.SkippedCount, "nothing skipped");
            Equal(10, fits.VisualLines[0].Length, "lines padded to the width");
            var end = VisualTruncate.TruncateToVisualLines("a\nb\nc\nd", 2, 10);
            Lines(["c", "d"], end.VisualLines, "keeps end");
            Equal(2, end.SkippedCount, "skipped");
            var start = VisualTruncate.TruncateToVisualLines("a\nb\nc\nd", 2, 10, 0, VisualTruncateKeep.Start);
            Lines(["a", "b"], start.VisualLines, "keeps start");
            var wrapped = VisualTruncate.TruncateToVisualLines("abcdefghij", 2, 4);
            Lines(["efgh", "ij"], wrapped.VisualLines, "wrapped visual lines");
            Equal(1, wrapped.SkippedCount, "one wrapped line hidden");
            var padded = VisualTruncate.TruncateToVisualLines("abcdefghij", 10, 6, 1);
            Lines([" abcd", " efgh", " ij"], padded.VisualLines, "paddingX 1");
        }));
        yield return ("msg.visual-truncate.preview", Sync(() =>
        {
            var calls = 0;
            string Hint(int hidden) { calls++; return $"... {hidden} more lines hidden ..."; }
            var end = new VisualLinePreview(new VisualLinePreviewOptions("1\n2\n3\n4\n5", 2, VisualTruncateKeep.End, Hint));
            Lines(["... 3 more lines hidden ...", "4", "5"], end.Render(40), "hint before kept end lines");
            end.Render(40);
            Equal(1, calls, "cached per width");
            Lines(["... 3 more...", "4", "5"], end.Render(13), "hint truncated to the width");
            Equal(2, calls, "re-rendered at a new width");
            var start = new VisualLinePreview(new VisualLinePreviewOptions("1\n2\n3", 2, VisualTruncateKeep.Start, Hint));
            Lines(["1", "2", "... 1 more lines hidden ..."], start.Render(40), "hint after kept start lines");
            Lines(["1"], new VisualLinePreview(new VisualLinePreviewOptions("1", 2, VisualTruncateKeep.End, Hint)).Render(40), "no hint when nothing hidden");
        }));

        // ---------------------------------------------------------------- user-message.ts
        yield return ("msg.user-message.osc-markers", Sync(() =>
        {
            // user-message.test.ts: keeps user message height stable while moving closing OSC markers off line end.
            var lines = new UserMessageComponent("hello").Render(20);
            Equal(3, lines.Count, "height");
            Check(lines[0].Contains(Osc133ZoneStart, StringComparison.Ordinal), "zone start on first line");
            Check(lines[0].EndsWith(BgReset, StringComparison.Ordinal), "first line ends with bg reset");
            Check(!lines[0].Contains(Osc133ZoneEnd, StringComparison.Ordinal), "no zone end on first line");
            Contains(lines[1], "hello", "content line");
            Check(lines[2].StartsWith(Osc133ZoneEnd + Osc133ZoneFinal, StringComparison.Ordinal), "zone end and final lead the last line");
            Check(lines[2].EndsWith(BgReset, StringComparison.Ordinal), "last line ends with bg reset");
        }));
        yield return ("msg.user-message.transformers", Sync(() =>
        {
            // user-message.test.ts: chains Markdown transformers with user message context.
            var calls = new List<string>();
            var component = new UserMessageComponent("The input is $x^2$.", null, 1,
            [
                (markdown, context) =>
                {
                    calls.Add("formula");
                    Equal(new MarkdownTransformContext("user", false, 78), context, "context");
                    return markdown.Replace("$x^2$", "x²", StringComparison.Ordinal);
                },
                (markdown, _) => { calls.Add("suffix"); return markdown + " Done."; },
            ]);
            Contains(Strip(string.Join("\n", component.Render(80))), "The input is x². Done.", "transformed");
            Equal("formula,suffix", string.Join(",", calls), "order");
        }));
        yield return ("msg.user-message.invalidate-reapplies", Sync(() =>
        {
            // user-message.test.ts: reapplies Markdown transformers when invalidated.
            var suffix = "before";
            var component = new UserMessageComponent("Message", null, 1, [(markdown, _) => markdown + " " + suffix]);
            Contains(Strip(string.Join("\n", component.Render(80))), "Message before", "first render");
            suffix = "after";
            component.Invalidate();
            Contains(Strip(string.Join("\n", component.Render(80))), "Message after", "after invalidate");
        }));
        yield return ("msg.user-message.output-pad", Sync(() =>
        {
            // assistant-message.test.ts: uses configured output padding for user messages.
            Check(Strip(new UserMessageComponent("hello", null, 1).Render(40)).Any(line => line.StartsWith(" hello", StringComparison.Ordinal)), "padded");
            Check(Strip(new UserMessageComponent("hello", null, 0).Render(40)).Any(line => line.StartsWith("hello", StringComparison.Ordinal)), "unpadded");
            var component = new UserMessageComponent("hello");
            component.SetOutputPad(3);
            Lines(["", "   hello", ""], component.Render(20), "SetOutputPad");
        }));
        yield return ("msg.user-message.render", Sync(() =>
        {
            var lines = new UserMessageComponent("1. first\n2. second \\*literal\\*").Render(30);
            Lines(["", " 1. first", " 2. second \\*literal\\*", ""], lines, "ordered markers and backslash escapes preserved");
            Check(lines.All(line => TextUtils.VisibleWidth(line) == 30), "full-width background lines");
            Contains(lines[1], T.GetBgAnsi("userMessageBg"), "user message background");
            Contains(lines[1], T.GetFgAnsi("userMessageText"), "user message text color");
        }));

        // ---------------------------------------------------------------- assistant-message.ts
        yield return ("msg.assistant-message.osc-markers", Sync(() =>
        {
            // assistant-message.test.ts: adds OSC 133 zone markers to assistant messages without tool calls.
            var lines = new AssistantMessageComponent(Assistant([TextBlock("hello")])).Render(40);
            Check(lines.Count > 0, "rendered");
            Contains(lines[0], Osc133ZoneStart, "zone start");
            Check(lines[^1].StartsWith(Osc133ZoneEnd + Osc133ZoneFinal, StringComparison.Ordinal), "zone end and final");
        }));
        yield return ("msg.assistant-message.no-osc-with-tool-calls", Sync(() =>
        {
            // assistant-message.test.ts: does not add OSC 133 zone markers when the message contains tool calls.
            var rendered = string.Join("\n", new AssistantMessageComponent(Assistant([TextBlock("calling tool"),
                new JsonObject { ["type"] = "toolCall", ["id"] = "tool-1", ["name"] = "read", ["arguments"] = new JsonObject { ["path"] = "file.txt" } }])).Render(60));
            Check(!rendered.Contains(Osc133ZoneStart, StringComparison.Ordinal), "no zone start");
            Check(!rendered.Contains(Osc133ZoneEnd, StringComparison.Ordinal), "no zone end");
            Check(!rendered.Contains(Osc133ZoneFinal, StringComparison.Ordinal), "no zone final");
        }));
        yield return ("msg.assistant-message.length-stop", Sync(() =>
        {
            // assistant-message.test.ts: renders length stops with neutral truncation wording.
            var component = new AssistantMessageComponent(Assistant([ThinkingBlock("private reasoning")], "length"), true);
            var rendered = Strip(string.Join("\n", component.Render(80)));
            Contains(rendered, "Thinking...", "hidden thinking label");
            Contains(rendered, "Response was truncated before completion.", "length notice");
            Lines(["", " Thinking...", "", " Response was truncated before completion."], component.Render(80), "length snapshot");
        }));
        yield return ("msg.assistant-message.coalesces-thinking", Sync(() =>
        {
            // assistant-message.test.ts: coalesces adjacent thinking blocks into one hidden thinking label.
            var component = new AssistantMessageComponent(Assistant([ThinkingBlock("first thought"), ThinkingBlock(""), ThinkingBlock("second thought"), TextBlock("answer")]), true);
            var rendered = Strip(string.Join("\n", component.Render(80)));
            Equal(1, rendered.Split("Thinking...").Length - 1, "one label");
            Contains(rendered, "answer", "answer");
            Lines(["", " Thinking...", "", " answer"], component.Render(80), "coalesced snapshot");
            var shown = new AssistantMessageComponent(Assistant([ThinkingBlock("first thought"), ThinkingBlock(""), ThinkingBlock("second thought"), TextBlock("answer")]));
            Lines(["", " first thought", "", " second thought", "", " answer"], shown.Render(80), "joined with a blank line when shown");
        }));
        yield return ("msg.assistant-message.click-toggles-thinking", Sync(() =>
        {
            // assistant-message.test.ts: collapses individual thinking runs when clicked.
            var component = new AssistantMessageComponent(Assistant([ThinkingBlock("first reasoning"), TextBlock("answer"), ThinkingBlock("second reasoning")]));
            const int width = 80;
            var lines = component.Render(width);
            var row = Strip(lines).FindIndex(line => line.Contains("first reasoning", StringComparison.Ordinal));
            Check(row >= 0, "first reasoning row");
            var result = component.HandleMouse(new TuiMouseEvent(TuiMouseEventType.Click, TuiMouseButton.Left, 1, row, 1, row, width, lines.Count, ClickCount: 1));
            Equal(true, result?.Handled, "handled");
            var collapsed = Strip(string.Join("\n", component.Render(width)));
            Check(!collapsed.Contains("first reasoning", StringComparison.Ordinal), "first run hidden");
            Contains(collapsed, "Thinking...", "label");
            Contains(collapsed, "second reasoning", "second run still shown");
            var right = component.HandleMouse(new TuiMouseEvent(TuiMouseEventType.Click, TuiMouseButton.Right, 1, row, 1, row, width, lines.Count));
            Equal(null, right, "right click ignored");
            component.SetHideThinkingBlock(false);
            Contains(Strip(string.Join("\n", component.Render(width))), "first reasoning", "SetHideThinkingBlock clears overrides");
        }));
        yield return ("msg.assistant-message.output-pad", Sync(() =>
        {
            // assistant-message.test.ts: uses configured output padding for text and thinking.
            var component = new AssistantMessageComponent(Assistant([TextBlock("hello"), ThinkingBlock("reasoning")]), false, null, "Thinking...", 1);
            var lines = Strip(component.Render(80));
            Check(lines.Any(line => line.Contains(" hello", StringComparison.Ordinal)), "padded text");
            Check(lines.Any(line => line.Contains(" reasoning", StringComparison.Ordinal)), "padded thinking");
            component.SetOutputPad(0);
            var updated = Strip(component.Render(80));
            Check(updated.Any(line => line.StartsWith("hello", StringComparison.Ordinal)), "unpadded text");
            Check(updated.Any(line => line.StartsWith("reasoning", StringComparison.Ordinal)), "unpadded thinking");
        }));
        yield return ("msg.assistant-message.transformer-chain", Sync(() =>
        {
            // assistant-message.test.ts: chains Markdown transformers in registration order.
            var calls = new List<string>();
            var component = new AssistantMessageComponent(Assistant([TextBlock("The result is $x^2$.")]), false, null, "Thinking...", 1,
            [
                (markdown, context) =>
                {
                    calls.Add("formula");
                    Equal(new MarkdownTransformContext("assistant", false, 78), context, "context");
                    return markdown.Replace("$x^2$", "x²", StringComparison.Ordinal);
                },
                (markdown, _) => { calls.Add("suffix"); return markdown + " Done."; },
            ]);
            Contains(Strip(string.Join("\n", component.Render(80))), "The result is x². Done.", "transformed");
            Equal("formula,suffix", string.Join(",", calls), "order");
        }));
        yield return ("msg.assistant-message.streaming-context", Sync(() =>
        {
            // assistant-message.test.ts: identifies partial assistant Markdown as streaming.
            var states = new List<bool>();
            var message = Assistant([TextBlock("partial")]);
            var component = new AssistantMessageComponent(null, false, null, "Thinking...", 1,
                [(markdown, context) => { states.Add(context.IsStreaming); return context.IsStreaming ? markdown : markdown + " transformed"; }]);
            component.UpdateContent(message, true);
            Check(!Strip(string.Join("\n", component.Render(80))).Contains("transformed", StringComparison.Ordinal), "streaming untouched");
            component.UpdateContent(message, false);
            Contains(Strip(string.Join("\n", component.Render(80))), "partial transformed", "final transformed");
            Equal("True,False", string.Join(",", states), "streaming states");
        }));
        yield return ("msg.assistant-message.width-change", Sync(() =>
        {
            // assistant-message.test.ts: reapplies Markdown transformers when available width changes.
            var widths = new List<int>();
            var component = new AssistantMessageComponent(Assistant([TextBlock("answer")]), false, null, "Thinking...", 1,
                [(markdown, context) => { widths.Add(context.AvailableWidth); return $"{markdown} ({context.AvailableWidth})"; }]);
            Contains(Strip(string.Join("\n", component.Render(80))), "answer (78)", "width 80");
            component.Render(80);
            Contains(Strip(string.Join("\n", component.Render(60))), "answer (58)", "width 60");
            Equal("78,58", string.Join(",", widths), "widths");
        }));
        yield return ("msg.assistant-message.transformer-throws", Sync(() =>
        {
            // assistant-message.test.ts: continues the Markdown transformer chain when a transformer throws.
            var calls = new List<string>();
            var component = new AssistantMessageComponent(Assistant([TextBlock("still visible")]), false, null, "Thinking...", 1,
            [
                (markdown, _) => { calls.Add("first"); return markdown.Replace("still", "remains", StringComparison.Ordinal); },
                (_, _) => { calls.Add("throw"); throw new InvalidOperationException("broken transformer"); },
                (markdown, _) => { calls.Add("last"); return markdown + " after error"; },
            ]);
            Contains(Strip(string.Join("\n", component.Render(80))), "remains visible after error", "chain continues");
            Equal("first,throw,last", string.Join(",", calls), "calls");
        }));
        yield return ("msg.assistant-message.transform-does-not-mutate", Sync(() =>
        {
            // assistant-message.test.ts: transforms text and thinking Markdown without mutating the original message.
            var message = Assistant([TextBlock("answer"), ThinkingBlock("reasoning")]);
            var before = message["content"]!.ToJsonString();
            var component = new AssistantMessageComponent(message, false, null, "Thinking...", 1, [(markdown, context) => $"{context.MessageType}:{markdown}"]);
            var rendered = Strip(string.Join("\n", component.Render(80)));
            Contains(rendered, "assistant:answer", "text type");
            Contains(rendered, "assistant-thinking:reasoning", "thinking type");
            Equal(before, message["content"]!.ToJsonString(), "message unchanged");
        }));
        yield return ("msg.assistant-message.errors", Sync(() =>
        {
            Lines(["", " Operation aborted"], new AssistantMessageComponent(Assistant([], "aborted")).Render(40), "aborted default");
            Lines(["", " Operation aborted"], new AssistantMessageComponent(Assistant([], "aborted", "Request was aborted")).Render(40), "aborted generic");
            Lines(["", " hi", "", " Stopped by user"], new AssistantMessageComponent(Assistant([TextBlock("hi")], "aborted", "Stopped by user")).Render(40), "aborted custom");
            Lines(["", " Error: Unknown error"], new AssistantMessageComponent(Assistant([], "error")).Render(40), "error default");
            Lines(["", " Error: boom"], new AssistantMessageComponent(Assistant([], "error", "boom")).Render(40), "error message");
            var withTool = new AssistantMessageComponent(Assistant([new JsonObject { ["type"] = "toolCall", ["id"] = "1", ["name"] = "x", ["arguments"] = new JsonObject() }], "error", "boom"));
            Equal(0, withTool.Render(40).Count, "tool errors are left to the tool components");
            Contains(new AssistantMessageComponent(Assistant([], "error", "boom")).Render(40)[1], T.GetFgAnsi("error"), "error color");
        }));
        yield return ("msg.assistant-message.render", Sync(() =>
        {
            var component = new AssistantMessageComponent(Assistant([ThinkingBlock("  plan  "), TextBlock("  **Done**  "), TextBlock("   ")]), false, null, "Thinking...");
            var lines = component.Render(30);
            Lines(["", " plan", "", " Done"], lines, "text and thinking");
            Contains(lines[1], T.GetFgAnsi("thinkingText"), "thinking color");
            Contains(lines[1], "\u001b[3m", "thinking italic");
            component.SetHideThinkingBlock(true);
            component.SetHiddenThinkingLabel("Reasoning hidden");
            Lines(["", " Reasoning hidden", "", " Done"], component.Render(30), "hidden label");
            Lines([], new AssistantMessageComponent().Render(30), "no message");
        }));

        // ---------------------------------------------------------------- custom-message.ts
        yield return ("msg.custom-message.renderer-output-pad", Sync(() =>
        {
            // custom-message.test.ts: provides output padding to custom renderers and updates it.
            var seen = new List<MessageRenderOptions>();
            var component = new CustomMessageComponent(Custom("test", "custom"), (_, options, _) => { seen.Add(options); return new Text("custom", options.OutputPad, 0); }, null, 1);
            Equal(1, seen.Count, "one render");
            Equal(new MessageRenderOptions(false, 1), seen[0], "initial options");
            Check(Strip(component.Render(40)).Any(line => line.StartsWith(" custom", StringComparison.Ordinal)), "padded");
            component.SetOutputPad(0);
            Equal(new MessageRenderOptions(false, 0), seen[^1], "updated options");
            Check(Strip(component.Render(40)).Any(line => line.StartsWith("custom", StringComparison.Ordinal)), "unpadded");
            component.SetExpanded(true);
            Equal(new MessageRenderOptions(true, 0), seen[^1], "expanded");
            var count = seen.Count;
            component.SetExpanded(true);
            Equal(count, seen.Count, "unchanged expanded state does not rebuild");
            Lines(["", "custom"], component.Render(40), "spacer then renderer output");
        }));
        yield return ("msg.custom-message.default-render", Sync(() =>
        {
            var component = new CustomMessageComponent(Custom("note", "body text"));
            var lines = component.Render(30);
            Lines(["", "", " [note]", "", " body text", ""], lines, "string content");
            Contains(lines[2], T.GetBgAnsi("customMessageBg"), "custom background");
            Contains(lines[2], T.GetFgAnsi("customMessageLabel"), "label color");
            Contains(lines[2], "\u001b[1m[note]\u001b[22m", "bold label");
            var array = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "one" }, new JsonObject { ["type"] = "image", ["data"] = "x", ["mimeType"] = "image/png" },
                new JsonObject { ["type"] = "text", ["text"] = "two" });
            Lines(["", "", "  [note]", "", "  one", "  two", ""], new CustomMessageComponent(Custom("note", array), null, null, 2).Render(30), "text blocks joined, padding 2");
        }));
        yield return ("msg.custom-message.renderer-fallback", Sync(() =>
        {
            var fallback = Lines2(new CustomMessageComponent(Custom("note", "body"), (_, _, _) => null).Render(30));
            var throwing = Lines2(new CustomMessageComponent(Custom("note", "body"), (_, _, _) => throw new InvalidOperationException("bad")).Render(30));
            var plain = Lines2(new CustomMessageComponent(Custom("note", "body")).Render(30));
            Equal(plain, fallback, "null renderer result uses the default rendering");
            Equal(plain, throwing, "throwing renderer uses the default rendering");
            var calls = 0;
            var component = new CustomMessageComponent(Custom("note", "body"), (_, _, _) => { calls++; return calls == 1 ? new Text("custom", 0, 0) : null; });
            Lines(["", "custom"], component.Render(30), "renderer output");
            component.Invalidate();
            Lines(["", "", " [note]", "", " body", ""], component.Render(30), "rebuild on invalidate replaces the renderer output");
        }));

        // ---------------------------------------------------------------- custom-entry.ts
        yield return ("msg.custom-entry.render", Sync(() =>
        {
            var seen = new List<EntryRenderOptions>();
            var component = new CustomEntryComponent(Entry("bookmark"), (entry, options, _) =>
            {
                seen.Add(options);
                return new Text($"{entry["customType"]}:{entry["data"]?["label"]}", 1, 0);
            });
            Check(component.HasContent(), "has content");
            Lines(["", " bookmark:here"], component.Render(30), "spacer then entry");
            component.SetExpanded(true);
            Equal(new EntryRenderOptions(true), seen[^1], "expanded passed");
            var empty = new CustomEntryComponent(Entry("hidden"), (_, _, _) => null);
            Check(!empty.HasContent(), "no content");
            Lines([], empty.Render(30), "renders nothing");
        }));
        yield return ("msg.custom-entry.renderer-failure", Sync(() =>
        {
            var component = new CustomEntryComponent(Entry("bookmark"), (_, _, _) => throw new InvalidOperationException("kaput"), 2);
            Check(component.HasContent(), "error box counts as content");
            var lines = component.Render(50);
            Lines(["", "", "  [bookmark] renderer failed: kaput", ""], lines, "error box");
            Contains(lines[2], T.GetFgAnsi("error"), "error color");
            Contains(lines[2], T.GetBgAnsi("customMessageBg"), "custom background");
            component.SetOutputPad(0);
            Lines(["", "", "[bookmark] renderer failed: kaput", ""], component.Render(50), "output pad applies to the error box");
        }));

        // ---------------------------------------------------------------- collapsible messages
        yield return ("msg.collapsible.compaction-toggle", Sync(() =>
        {
            // collapsible-message-components.test.ts: toggles a compaction summary when clicked.
            var component = new CompactionSummaryMessageComponent(new JsonObject { ["role"] = "compactionSummary", ["summary"] = "compaction details", ["tokensBefore"] = 1234, ["timestamp"] = 0 });
            Check(!RenderText(component).Contains("compaction details", StringComparison.Ordinal), "collapsed");
            ClickRow(component, "[compaction]");
            Contains(RenderText(component), "compaction details", "expanded");
            ClickRow(component, "[compaction]");
            Check(!RenderText(component).Contains("compaction details", StringComparison.Ordinal), "collapsed again");
        }));
        yield return ("msg.collapsible.branch-toggle", Sync(() =>
        {
            // collapsible-message-components.test.ts: toggles a branch summary when clicked.
            var component = new BranchSummaryMessageComponent(new JsonObject { ["role"] = "branchSummary", ["summary"] = "branch details", ["fromId"] = "entry-1", ["timestamp"] = 0 });
            Check(!RenderText(component).Contains("branch details", StringComparison.Ordinal), "collapsed");
            ClickRow(component, "[branch]");
            Contains(RenderText(component), "branch details", "expanded");
            ClickRow(component, "[branch]");
            Check(!RenderText(component).Contains("branch details", StringComparison.Ordinal), "collapsed again");
        }));
        yield return ("msg.collapsible.skill-toggle", Sync(() =>
        {
            // collapsible-message-components.test.ts: toggles a skill invocation when clicked.
            var component = new SkillInvocationMessageComponent(new ParsedSkillBlock("example-skill", "/tmp/example-skill.md", "skill details", null));
            Check(!RenderText(component).Contains("skill details", StringComparison.Ordinal), "collapsed");
            ClickRow(component, "[skill]");
            Contains(RenderText(component), "skill details", "expanded");
            ClickRow(component, "[skill]");
            Check(!RenderText(component).Contains("skill details", StringComparison.Ordinal), "collapsed again");
        }));
        yield return ("msg.compaction-summary.render", Sync(() =>
        {
            var component = new CompactionSummaryMessageComponent(new JsonObject { ["role"] = "compactionSummary", ["summary"] = "Kept the plan.", ["tokensBefore"] = 1234567, ["timestamp"] = 0 });
            var lines = component.Render(60);
            Lines(["", " [compaction]", "", " Compacted from 1,234,567 tokens (ctrl+o to expand)", ""], lines, "collapsed");
            Check(lines.All(line => TextUtils.VisibleWidth(line) == 60), "full-width box");
            Contains(lines[3], T.GetFgAnsi("dim") + "ctrl+o", "dim key");
            component.SetExpanded(true);
            Lines(["", " [compaction]", "", " Compacted from 1,234,567 tokens", "", " Kept the plan.", ""], component.Render(60), "expanded");
            component.SetOutputPad(0);
            Lines(["", "[compaction]", "", "Compacted from 1,234,567 tokens", "", "Kept the plan.", ""], component.Render(60), "output pad 0");
        }));
        yield return ("msg.branch-summary.render", Sync(() =>
        {
            var component = new BranchSummaryMessageComponent(new JsonObject { ["role"] = "branchSummary", ["summary"] = "Tried a fix.", ["fromId"] = "a", ["timestamp"] = 0 });
            Lines(["", " [branch]", "", " Branch summary (ctrl+o to expand)", ""], component.Render(50), "collapsed");
            component.SetExpanded(true);
            Lines(["", " [branch]", "", " Branch Summary", "", " Tried a fix.", ""], component.Render(50), "expanded");
        }));
        yield return ("msg.skill-invocation.render", Sync(() =>
        {
            var component = new SkillInvocationMessageComponent(new ParsedSkillBlock("deploy", "/skills/deploy/SKILL.md", "Run the steps.", "go"), null, 2);
            Lines(["", "  [skill] deploy (ctrl+o to expand)", ""], component.Render(50), "collapsed");
            component.SetExpanded(true);
            Lines(["", "  [skill]", "  deploy", "", "  Run the steps.", ""], component.Render(50), "expanded");
        }));

        // ---------------------------------------------------------------- markdown-transform.ts
        yield return ("msg.markdown-transform.chain", Sync(() =>
        {
            var contexts = new List<MarkdownTransformContext>();
            var transform = MarkdownTransform.CreateMarkdownTransform(MarkdownMessageType.AssistantThinking, true,
            [
                (markdown, context) => { contexts.Add(context); return null; },
                (markdown, _) => markdown + "!",
                (_, _) => throw new InvalidOperationException(),
                (markdown, _) => markdown + "?",
            ]);
            Equal("hi!?", transform("hi", 42), "null keeps the Markdown, throws are skipped");
            Equal(new MarkdownTransformContext("assistant-thinking", true, 42), contexts.Single(), "context");
            Equal("same", MarkdownTransform.CreateMarkdownTransform("user", false, [])("same", 1), "no transformers");
        }));

        // ---------------------------------------------------------------- earendil-announcement.ts
        yield return ("msg.earendil-announcement.render", Sync(() =>
        {
            var previous = EarendilAnnouncementComponent.GetBundledInteractiveAssetPath;
            try
            {
                // config.ts getBundledInteractiveAssetPath: the shipped asset is found by default.
                Check(previous is not null && File.Exists(previous("clankolas.png")), "the bundled clankolas.png ships with the application");
                EarendilAnnouncementComponent.GetBundledInteractiveAssetPath = null;
                EarendilAnnouncementComponent.ResetImageCacheForTests();
                var lines = new EarendilAnnouncementComponent().Render(80);
                Lines([new string('─', 80), " pi has joined Earendil", "", " Read the blog post:", " https://mariozechner.at/posts/2026-04-08-ive-sold-out/", "",
                    new string('─', 80)], lines, "without the image");
                Contains(lines[0], T.GetFgAnsi("accent"), "accent border");
                Contains(lines[1], "\u001b[1m", "bold title");
                Contains(lines[4], T.GetFgAnsi("mdLink"), "link color");

                using var dir = new TempDir();
                File.WriteAllBytes(Path.Combine(dir.Path, "clankolas.png"), Convert.FromBase64String(OnePixelPng));
                EarendilAnnouncementComponent.GetBundledInteractiveAssetPath = name => Path.Combine(dir.Path, name);
                Equal(7, new EarendilAnnouncementComponent().Render(80).Count, "image load is attempted once per process");
                EarendilAnnouncementComponent.ResetImageCacheForTests();
                var withImage = Strip(new EarendilAnnouncementComponent().Render(80));
                Equal(9, withImage.Count, "image and spacer added");
                Contains(withImage[6], "[Image: clankolas.png [image/png] 1x1]", "image fallback without image protocol");
                Equal("", withImage[7].TrimEnd(), "spacer after the image");
            }
            finally
            {
                EarendilAnnouncementComponent.GetBundledInteractiveAssetPath = previous;
                EarendilAnnouncementComponent.ResetImageCacheForTests();
            }
        }));

        // ---------------------------------------------------------------- auth-url.ts
        yield return ("msg.auth-url.copy", async () =>
        {
            // auth-url-copy.test.ts (AuthUrlComponent side): the copy hint, then the copied confirmation.
            var url = "https://auth.example.invalid/authorize?" + new string('x', 300);
            var copied = new List<string>();
            var tui = new FakeTui();
            var component = new AuthUrlComponent(tui, url, text => { copied.Add(text); return Task.CompletedTask; });
            Equal(url, component.Url, "url");
            var rendered = Strip(string.Join("\n", component.Render(80)));
            Contains(rendered, "ctrl+x to copy", "copy hint");
            Contains(rendered, OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open", "click hint");
            Contains(string.Join("", component.Render(80)), "\u001b]8;;" + url, "hyperlinked URL");
            Equal(1, tui.RenderRequests, "render requested for the hint");
            await component.Copy();
            Equal(url, copied.Single(), "copied URL");
            Contains(Strip(string.Join("\n", component.Render(80))), "Copied URL to clipboard", "confirmation");
            Contains(string.Join("", component.Render(80)), T.GetFgAnsi("success"), "success color");
            Equal(2, tui.RenderRequests, "render requested after copy");
        });
        yield return ("msg.auth-url.copy-failure", async () =>
        {
            var component = new AuthUrlComponent(new FakeTui(), "https://example.invalid/a", _ => throw new InvalidOperationException("no clipboard tool"));
            await component.Copy();
            var lines = component.Render(80);
            Lines([" https://example.invalid/a", $" {(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl")}+click to open • no clipboard tool"], lines, "error hint");
            Contains(lines[1], T.GetFgAnsi("error"), "error color");
        });
    }

    private static string Lines2(List<string> lines) => string.Join("\n", lines);

    private static string RenderText(IComponent component) => Strip(string.Join("\n", component.Render(80)));

    private static void ClickRow(Box component, string marker)
    {
        var lines = component.Render(80);
        var row = Strip(lines).FindIndex(line => line.Contains(marker, StringComparison.Ordinal));
        Check(row >= 0, "marker row " + marker);
        var result = component.HandleMouse(new TuiMouseEvent(TuiMouseEventType.Click, TuiMouseButton.Left, 2, row, 2, row, 80, lines.Count, ClickCount: 1));
        Equal(true, result?.Handled, "click handled on " + marker);
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };
    private static JsonObject ThinkingBlock(string thinking) => new() { ["type"] = "thinking", ["thinking"] = thinking };

    private static JsonObject Assistant(JsonObject[] content, string stopReason = "stop", string? errorMessage = null)
    {
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray([.. content]),
            ["api"] = "openai-responses",
            ["provider"] = "openai",
            ["model"] = "gpt-4o-mini",
            ["usage"] = new JsonObject
            {
                ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["totalTokens"] = 0,
                ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["total"] = 0 },
            },
            ["stopReason"] = stopReason,
            ["timestamp"] = 0,
        };
        if (errorMessage is not null) message["errorMessage"] = errorMessage;
        return message;
    }

    private static JsonObject Custom(string customType, JsonNode content) =>
        new() { ["role"] = "custom", ["customType"] = customType, ["content"] = content, ["display"] = true, ["timestamp"] = 0 };

    private static JsonObject Entry(string customType) =>
        new() { ["type"] = "custom", ["id"] = "e1", ["parentId"] = null, ["timestamp"] = "2026-01-01T00:00:00.000Z", ["customType"] = customType, ["data"] = new JsonObject { ["label"] = "here" } };

    private sealed class DisposeAction(Action action) : IDisposable { public void Dispose() => action(); }

    /// <summary>The <c>{ requestRender }</c> TUI stub of the upstream tests, with a manual loop for timers.</summary>
    private sealed class FakeTui : ITui
    {
        public int RenderRequests { get; private set; }
        public UiLoop Loop { get; } = UiLoop.CreateManual();
        public void RequestRender(bool force = false) => RenderRequests++;
        public TuiMode Mode => TuiMode.Regular;
        public ITerminal Terminal => throw new NotSupportedException();
        public int FullRedraws => 0;
        public List<IComponent> Children { get; } = [];
        public void AddChild(IComponent component) => Children.Add(component);
        public void RemoveChild(IComponent component) => Children.Remove(component);
        public void Clear() => Children.Clear();
        public bool ShowHardwareCursor { get; set; }
        public bool ClearOnShrink { get; set; }
        public IComponent? FocusedComponent { get; private set; }
        public void SetFocus(IComponent? component) => FocusedComponent = component;
        public IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null) => throw new NotSupportedException();
        public void HideOverlay() { }
        public bool HasOverlay() => false;
        public void Start() { }
        public void Stop(bool preserveScreen = false) { }
        public void RenderNow(bool force = false) { }
        public Action AddInputListener(Func<string, TuiInputListenerResult?> listener) => () => { };
        public Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener) => () => { };
        public void SetTerminalColorSchemeNotifications(bool enabled) { }
        public Task<TerminalColors> QueryTerminalColors(int timeoutMs, Action<TerminalColors>? onLateReply = null) => Task.FromResult(new TerminalColors(null, null, null));
        public Action? OnDebug { get; set; }
        public void Invalidate() { }
    }
}
