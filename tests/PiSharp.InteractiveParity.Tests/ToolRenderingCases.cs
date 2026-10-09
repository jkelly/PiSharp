using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Agent.Tools;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>
/// tool-execution.ts, diff.ts, bash-execution.ts, core/tools/render-utils.ts, core/tools/renderers/*.ts, extensions/codemode/renderer.ts
/// and the MCP tool renderers. Ported expectations of tool-execution-component.test.ts, bash-execution-width.test.ts,
/// codemode-renderer.test.ts, tool-renderer-examples.test.ts (edit shell), edit-tool-no-full-redraw.test.ts and output-pad.test.ts,
/// plus render snapshots of every built-in renderer written from the sources.
/// </summary>
internal static class ToolRenderingCases
{
    // Small 2x2 blue JPEG image
    private const string TinyJpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAACAAIDAREAAhEBAxEB/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/xAAVAQEBAAAAAAAAAAAAAAAAAAAGCf/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AD3VTB3/2Q==";
    private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    private sealed class FakeTerminal(int columns = 80, int rows = 24) : ITerminal
    {
        public List<string> Writes { get; } = [];
        public void Start(Action<string> onInput, Action onResize) { }
        public void Stop() { }
        public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50) => Task.CompletedTask;
        public void Write(string data) => Writes.Add(data);
        public int Columns => columns;
        public int Rows => rows;
        public bool KittyProtocolActive => true;
        public void MoveBy(int lines) { }
        public void HideCursor() { }
        public void ShowCursor() { }
        public void ClearLine() { }
        public void ClearFromCursor() { }
        public void ClearScreen() { }
        public void SetTitle(string title) { }
        public void SetProgress(bool active) { }
        public void SetProgramStatus(ProgramStatus status) { }
        public int FullClearCount => Writes.Count(write => write.Contains("\u001b[2J\u001b[H\u001b[3J", StringComparison.Ordinal));
    }

    /// <summary>vi.useFakeTimers / vi.setSystemTime: a settable clock whose timers never fire.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public long NowMs;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(NowMs);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static (TuiMainScreen Tui, UiLoop Loop, FakeTerminal Terminal) NewTui(int columns = 80, int rows = 24)
    {
        var loop = UiLoop.CreateManual();
        var terminal = new FakeTerminal(columns, rows);
        return (new TuiMainScreen(terminal, loop, environment: _ => null), loop, terminal);
    }

    private static ITui Ui() => NewTui().Tui;

    private static readonly ToolRenderers BaseDefinition = new();

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject TextResult(string text, bool isError = false, JsonNode? details = null, double? durationMs = null)
    {
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError };
        if (details is not null) result["details"] = details;
        if (durationMs is { } duration) result["durationMs"] = duration;
        return result;
    }

    private static JsonObject EmptyResult(bool isError = false, double? durationMs = null)
    {
        var result = new JsonObject { ["content"] = new JsonArray(), ["isError"] = isError };
        if (durationMs is { } duration) result["durationMs"] = duration;
        return result;
    }

    private static string Render(IComponent component, int width = 120) => string.Join("\n", Strip(component.Render(width)));

    private static ToolExecutionComponent Tool(string name, string args, ToolRenderers? definition, string cwd, ITui? ui = null, ToolExecutionOptions? options = null) =>
        new(name, "tool-" + name, Args(args), options, definition, ui ?? Ui(), cwd);

    private static ToolRenderers Text(string call, string? result = null) => new(
        RenderCall: (_, _, _) => new PiSharp.Tui.Pi.Text(call, 0, 0),
        RenderResult: result is null ? null : (_, _, _, _) => new PiSharp.Tui.Pi.Text(result, 0, 0));

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static void WithTime(Action<ManualTime> run)
    {
        var previous = BashRenderers.Time;
        var time = new ManualTime();
        BashRenderers.Time = time;
        try { run(time); }
        finally { BashRenderers.Time = previous; }
    }

    private static void WithCapabilities(TerminalCapabilities capabilities, Action run)
    {
        var previous = TerminalImage.GetCapabilities();
        TerminalImage.SetCapabilities(capabilities);
        try { run(); }
        finally { TerminalImage.SetCapabilities(previous); }
    }

    private static void WithContext(UiLoop loop, Action run)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(loop);
        try { run(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>Polls (running the loop's queued work) until the render contains the text.</summary>
    private static string WaitForRenderedText(Func<string> getRender, string expected, Action? onRetry = null, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var last = "";
        while (DateTime.UtcNow < deadline)
        {
            onRetry?.Invoke();
            last = getRender();
            if (last.Contains(expected, StringComparison.Ordinal)) return last;
            Thread.Sleep(10);
        }
        throw new InvalidOperationException($"Timed out waiting for render to include \"{expected}\". Last render:\n{last}");
    }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        foreach (var test in ToolExecutionComponentTests()) yield return test;
        foreach (var test in BashExecutionTests()) yield return test;
        foreach (var test in CodemodeRendererTests()) yield return test;
        foreach (var test in EditRedrawTests()) yield return test;
        foreach (var test in OutputPadTests()) yield return test;
        foreach (var test in SnapshotTests()) yield return test;
        foreach (var test in HelperTests()) yield return test;
        foreach (var test in ExtensionAdapterTests()) yield return test;
    }

    // ------------------------------------------------------------------ tool-execution-component.test.ts

    private static IEnumerable<(string Id, Func<Task> Run)> ToolExecutionComponentTests()
    {
        // Issue #10292: the component loads the PNG transcoder itself, so this works in any TUI host.
        // Issue #8577: a replaced partial image must not resurface.
        yield return ("tool.exec.converts-non-png-images", Sync(() => WithCapabilities(new(ImageProtocol.Kitty, true, true), () =>
        {
            var component = new ToolExecutionComponent("tool", "id", new JsonObject(), null, null, Ui(), Environment.CurrentDirectory);
            component.UpdateResult(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = "cGFydGlhbA==", ["mimeType"] = "image/jpeg" }), ["isError"] = false }, true);
            component.UpdateResult(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = TinyJpeg, ["mimeType"] = "image/jpeg" }), ["isError"] = false });
            var rendered = string.Join("\n", component.Render(120));
            Contains(rendered, ";iVBORw0KGgo", "converted PNG data");
            Check(!rendered.Contains("cGFydGlhbA==", StringComparison.Ordinal), "the replaced partial image does not resurface");
            // Invalidation reuses the converted Image, so the Kitty image ID stays the same.
            component.Invalidate();
            Equal(rendered, string.Join("\n", component.Render(120)), "render after invalidate");
        })));

        yield return ("tool.exec.stacks-custom-call-and-result", Sync(() =>
        {
            var component = Tool("custom_tool", "{}", Text("custom call", "custom result"), Environment.CurrentDirectory);
            Contains(Render(component), "custom call", "call before result");
            component.UpdateResult(TextResult("done", details: new JsonObject()));
            var rendered = Render(component);
            Contains(rendered, "custom call", "call");
            Contains(rendered, "custom result", "result");
        }));

        yield return ("tool.exec.self-rendered-empty-takes-no-space", Sync(() =>
        {
            var definition = new ToolRenderers(ToolRenderShell.Self, (_, _, _) => new PiSharp.Tui.Pi.Text("", 0, 0), (_, _, _, _) => new PiSharp.Tui.Pi.Text("", 0, 0));
            var component = Tool("custom_tool", "{}", definition, Environment.CurrentDirectory);
            Equal(0, component.Render(120).Count, "lines before the result");
            component.UpdateResult(new JsonObject { ["content"] = new JsonArray(), ["details"] = new JsonObject(), ["isError"] = false });
            Equal(0, component.Render(120).Count, "lines after the result");
        }));

        yield return ("tool.exec.builtin-override-without-renderers", Sync(() =>
        {
            var component = Tool("edit", """{"path":"README.md","oldText":"before","newText":"after"}""",
                BuiltInToolRenderers.WithBuiltInRenderers("edit", BaseDefinition), Environment.CurrentDirectory);
            component.UpdateResult(new JsonObject { ["content"] = new JsonArray(), ["details"] = new JsonObject { ["diff"] = "+1 after", ["firstChangedLine"] = 1 }, ["isError"] = false });
            var rendered = Render(component);
            Contains(rendered, "edit", "title");
            Contains(rendered, "README.md", "path");
            Check(!rendered.Contains(":1", StringComparison.Ordinal), "no line suffix");
        }));

        yield return ("tool.exec.legacy-file-path", Sync(() =>
        {
            var rendered = Render(Tool("read", """{"file_path":"README.md"}""", null, Environment.CurrentDirectory));
            Contains(rendered, "read", "title");
            Contains(rendered, "README.md", "path");
        }));

        yield return ("tool.exec.bash-no-duplicate-truncation", Sync(() =>
        {
            // The bash tool's final result for 4000 lines: the last 2000 lines and the footer naming the full output file.
            var fullOutputPath = Path.Combine(Path.GetTempPath(), "pi-bash-full.log");
            var text = string.Join("\n", Enumerable.Range(2001, 2000).Select(i => $"line-{i:0000}")) +
                $"\n\n[Showing lines 2001-4000 of 4000. Full output: {fullOutputPath}]";
            var details = new JsonObject
            {
                ["truncation"] = new JsonObject { ["truncated"] = true, ["truncatedBy"] = "lines", ["outputLines"] = 2000, ["totalLines"] = 4000, ["maxBytes"] = 51200 },
                ["fullOutputPath"] = fullOutputPath,
            };
            var component = Tool("bash", """{"command":"generate output"}""", BuiltInToolRenderers.ForToolDefinition("bash"), Environment.CurrentDirectory);
            component.SetExpanded(true);
            component.UpdateResult(TextResult(text, details: details));
            var rendered = Render(component, 200);
            Equal(1, CountOccurrences(rendered, "Full output:"), "Full output occurrences");
            Check(System.Text.RegularExpressions.Regex.IsMatch(rendered, @"line-4000[^\n]*\n[^\S\n]*\n \[Full output:"), "one blank line before the warning");
            Check(!System.Text.RegularExpressions.Regex.IsMatch(rendered, @"line-4000[^\n]*\n[^\S\n]*\n[^\S\n]*\n \[Full output:"), "not two blank lines");
            Contains(rendered, "Truncated: showing 2000 of 4000 lines", "truncation warning");
            Check(!rendered.Contains("[Showing lines 2001-4000 of 4000. Full output:", StringComparison.Ordinal), "footer removed");
        }));

        // Issue #9628: keep short durations precise and make long shell durations readable.
        foreach (var (ms, formatted) in new (long, string)[]
        {
            (0, "0.0s"), (4_200, "4.2s"), (59_900, "59.9s"), (59_999, "60.0s"), (60_000, "1m 0s"), (90_900, "1m 30s"),
            (1_592_200, "26m 32s"), (3_599_999, "59m 59s"), (3_600_000, "1h 0m 0s"), (7_384_900, "2h 3m 4s"),
        })
        {
            yield return ($"tool.exec.bash-duration-{ms}", Sync(() => WithTime(time =>
            {
                var component = Tool("bash", """{"command":"long-running-command"}""", BuiltInToolRenderers.ForToolDefinition("bash"), Environment.CurrentDirectory);
                component.MarkExecutionStarted();
                component.UpdateResult(EmptyResult(), true);

                time.NowMs += ms;
                component.Invalidate();
                var running = Render(component);

                component.UpdateResult(EmptyResult());
                var completed = Render(component);

                time.NowMs += 1_000;
                component.Invalidate();
                Equal(completed, Render(component), "completed render is stable");
                Contains(running, $"Elapsed {formatted}", "running");
                Contains(completed, $"Took {formatted}", "completed");
            })));
        }

        // #10549
        yield return ("tool.exec.bash-recorded-duration", Sync(() =>
        {
            string RenderBash(bool live)
            {
                var rendered = "";
                WithTime(time =>
                {
                    var component = Tool("bash", """{"command":"sleep 4"}""", BuiltInToolRenderers.ForToolDefinition("bash"), Environment.CurrentDirectory);
                    if (live)
                    {
                        component.MarkExecutionStarted();
                        component.UpdateResult(EmptyResult(), true);
                        // The wall clock jumps; the recorded duration does not.
                        time.NowMs += 3_600_000;
                    }
                    component.UpdateResult(EmptyResult(durationMs: 4_200));
                    rendered = Render(component);
                });
                return rendered;
            }
            Contains(RenderBash(true), "Took 4.2s", "live");
            Contains(RenderBash(false), "Took 4.2s", "restored");
        }));

        yield return ("tool.exec.read-no-duplicate-header", Sync(() =>
        {
            var component = Tool("read", """{"path":"README.md"}""", BuiltInToolRenderers.ForToolDefinition("read"), Environment.CurrentDirectory);
            component.UpdateResult(TextResult("hello"));
            Equal(1, System.Text.RegularExpressions.Regex.Matches(Render(component), @"\bread\b").Count, "read occurrences");
        }));

        // Issue #9996: strict tool schemas make models send null for omitted optional fields.
        yield return ("tool.exec.read-null-range", Sync(() =>
        {
            var rendered = Render(Tool("read", """{"path":"src/example.ts","offset":null,"limit":null}""", BuiltInToolRenderers.ForToolDefinition("read"), Environment.CurrentDirectory));
            Contains(rendered, "read src/example.ts", "header");
            Check(!rendered.Contains("src/example.ts:", StringComparison.Ordinal), "no range");
        }));

        yield return ("tool.exec.inherits-builtin-result-slot", Sync(() =>
        {
            var component = Tool("read", """{"path":"notes.txt"}""", BuiltInToolRenderers.WithBuiltInRenderers("read", Text("override call")), Environment.CurrentDirectory);
            component.UpdateResult(TextResult("hello"));
            component.SetExpanded(true);
            var rendered = Render(component);
            Contains(rendered, "override call", "call");
            Contains(rendered, "hello", "built-in result");
        }));

        yield return ("tool.exec.inherits-builtin-call-slot", Sync(() =>
        {
            var definition = new ToolRenderers(RenderResult: (_, _, _, _) => new PiSharp.Tui.Pi.Text("override result", 0, 0));
            var component = Tool("read", """{"path":"README.md"}""", BuiltInToolRenderers.WithBuiltInRenderers("read", definition), Environment.CurrentDirectory);
            component.UpdateResult(TextResult("hello"));
            var rendered = Render(component);
            Contains(rendered, "read", "title");
            Contains(rendered, "README.md", "path");
            Contains(rendered, "override result", "result");
        }));

        yield return ("tool.exec.custom-renderers-override-builtin", Sync(() =>
        {
            var definition = BuiltInToolRenderers.ForToolDefinition("read")! with
            {
                RenderCall = (_, _, _) => new PiSharp.Tui.Pi.Text("override call", 0, 0),
                RenderResult = (_, _, _, _) => new PiSharp.Tui.Pi.Text("override result", 0, 0),
            };
            var component = Tool("read", """{"path":"README.md"}""", definition, Environment.CurrentDirectory);
            component.UpdateResult(TextResult("hello"));
            var rendered = Render(component);
            Contains(rendered, "override call", "call");
            Contains(rendered, "override result", "result");
            Check(!rendered.Contains("read README.md", StringComparison.Ordinal), "built-in header not used");
        }));

        yield return ("tool.exec.shared-renderer-state", Sync(() =>
        {
            var definition = new ToolRenderers(
                RenderCall: (_, _, context) =>
                {
                    if (context.State.GetValueOrDefault("token") is null) context.State["token"] = "shared-token";
                    return new PiSharp.Tui.Pi.Text($"custom call {context.State["token"]}", 0, 0);
                },
                RenderResult: (_, _, _, context) => new PiSharp.Tui.Pi.Text($"custom result {context.State["token"]}", 0, 0));
            var component = Tool("custom_tool", "{}", definition, Environment.CurrentDirectory);
            component.UpdateResult(TextResult("done", details: new JsonObject()));
            var rendered = Render(component);
            Contains(rendered, "custom call shared-token", "call");
            Contains(rendered, "custom result shared-token", "result");
        }));

        yield return ("tool.exec.args-in-result-context", Sync(() =>
        {
            var definition = new ToolRenderers(
                RenderCall: (_, _, _) => new PiSharp.Tui.Pi.Text("call", 0, 0),
                RenderResult: (_, _, _, context) => new PiSharp.Tui.Pi.Text($"arg:{ToolJson.GetString(context.Args, "foo")}", 0, 0));
            var component = Tool("custom_tool", """{"foo":"bar"}""", definition, Environment.CurrentDirectory);
            component.UpdateResult(TextResult("done", details: new JsonObject()));
            Contains(Render(component), "arg:bar", "args");
        }));

        yield return ("tool.exec.fallback-call-header-args", Sync(() =>
        {
            var longValue = new string('x', 200);
            var args = new JsonObject { ["query"] = "pi", ["long"] = longValue, ["text"] = "line one\nline two" };
            var component = new ToolExecutionComponent("custom_tool", "tool-args", args, null, BaseDefinition, Ui(), Environment.CurrentDirectory);

            var collapsed = Render(component, 300);
            Contains(collapsed, "custom_tool query=\"pi\" long=\"xxx", "collapsed pairs");
            Contains(collapsed, "...", "ellipsis");
            Check(!collapsed.Contains(longValue, StringComparison.Ordinal), "long value cut");

            component.SetExpanded(true);
            var expanded = Render(component, 300);
            Contains(expanded, "  query: pi", "expanded string");
            Contains(expanded, longValue, "expanded long value");
            var expandedLines = expanded.Split('\n').Select(line => line.TrimEnd()).ToList();
            var textLine = expandedLines.FindIndex(line => line.EndsWith("  text: line one", StringComparison.Ordinal));
            Check(textLine > -1, "text line");
            Check(System.Text.RegularExpressions.Regex.IsMatch(expandedLines[textLine + 1], @"^\s+ {4}line two$"), "continuation indent: " + expandedLines[textLine + 1]);
        }));

        yield return ("tool.exec.fallback-results-collapse", Sync(() =>
        {
            var component = Tool("custom_tool", """{"foo":"bar"}""", BaseDefinition, Environment.CurrentDirectory);
            component.UpdateResult(TextResult(string.Join("\n", Enumerable.Range(1, 15).Select(i => $"line-{i}")), details: new JsonObject()));

            var collapsed = Render(component);
            Contains(collapsed, "custom_tool", "title");
            Contains(collapsed, "line-10", "line 10");
            Check(!collapsed.Contains("line-11", StringComparison.Ordinal), "line 11 hidden");
            Contains(collapsed, "5 more lines", "hint count");
            Contains(collapsed, "to expand", "hint");

            component.SetExpanded(true);
            var expanded = Render(component);
            Contains(expanded, "line-15", "line 15");
            Check(!expanded.Contains("more lines", StringComparison.Ordinal), "no hint");
        }));

        yield return ("tool.exec.write-trims-trailing-blank-lines", Sync(() =>
        {
            var rendered = Render(Tool("write", """{"path":"README.md","content":"one\ntwo\n"}""", BuiltInToolRenderers.ForToolDefinition("write"), Environment.CurrentDirectory));
            Contains(rendered, "one", "one");
            Contains(rendered, "two", "two");
            Check(!rendered.Contains("two\n\n", StringComparison.Ordinal), "no trailing blank line: " + rendered);
        }));

        yield return ("tool.exec.read-trims-trailing-blank-lines", Sync(() =>
        {
            var component = Tool("read", """{"path":"notes.txt"}""", BuiltInToolRenderers.ForToolDefinition("read"), Environment.CurrentDirectory);
            component.UpdateResult(TextResult("one\ntwo\n"));
            component.SetExpanded(true);
            var rendered = Render(component);
            Contains(rendered, "one", "one");
            Contains(rendered, "two", "two");
            Check(!rendered.Contains("two\n\n", StringComparison.Ordinal), "no trailing blank line");
        }));

        yield return ("tool.exec.read-errors-not-highlighted", Sync(() =>
        {
            var component = Tool("read", """{"path":"config.exs","offset":120,"limit":130}""", BuiltInToolRenderers.ForToolDefinition("read"), Environment.CurrentDirectory);
            const string error = "Offset 120 is beyond end of file (96 lines total)";
            component.UpdateResult(TextResult(error, isError: true));
            var rendered = string.Join("\n", component.Render(120));
            Contains(Strip(rendered), error, "error text");
            Contains(rendered, Themes.Current.Fg("toolOutput", error), "toolOutput color");
        }));

        yield return ("tool.exec.click-expands", Sync(() =>
        {
            var component = Tool("read", """{"path":"notes.txt"}""", BuiltInToolRenderers.ForToolDefinition("read"), Environment.CurrentDirectory);
            component.UpdateResult(TextResult("hidden content"));
            const int width = 120;
            var lines = component.Render(width);
            var resultRow = lines.FindIndex(line => Strip(line).Contains("notes.txt", StringComparison.Ordinal));
            Check(resultRow >= 0, "header row");
            var mouseEvent = new TuiMouseEvent(TuiMouseEventType.Click, TuiMouseButton.Left, 2, resultRow, 2, resultRow, width, lines.Count, ClickCount: 1);
            Check(component.HandleMouse(mouseEvent)?.Handled == true, "click handled");
            Contains(Render(component, width), "hidden content", "expanded");
        }));

        yield return ("tool.exec.read-collapsed-until-expanded", Sync(() =>
        {
            var component = Tool("read", """{"path":"notes.txt"}""", BuiltInToolRenderers.ForToolDefinition("read"), Environment.CurrentDirectory);
            component.UpdateResult(TextResult("hidden content"));
            var collapsed = Render(component);
            Contains(collapsed, "read", "title");
            Contains(collapsed, "notes.txt", "path");
            Check(!collapsed.Contains("hidden content", StringComparison.Ordinal), "collapsed hides content");
            component.SetExpanded(true);
            Contains(Render(component), "hidden content", "expanded");
        }));

        foreach (var test in CompactReadTests()) yield return test;
    }

    private static IEnumerable<(string Id, Func<Task> Run)> CompactReadTests()
    {
        var cwd = Environment.CurrentDirectory;
        var readme = Path.Combine(Path.GetTempPath(), "pisharp-package", "README.md");
        var outside = Path.GetFullPath(Path.Combine(cwd, "..", "AGENTS.md"));
        var scenarios = new (string Title, string Path, string Content, string Compact, string Hidden, string? Absent)[]
        {
            ("SKILL.md", Path.Combine(cwd, "attio", "SKILL.md"), "---\nname: attio\ndescription: CRM helper\n---\n\n# Hidden skill instructions",
                "[skill] attio", "Hidden skill instructions", "read skill attio"),
            ("AGENTS.md", Path.Combine(cwd, ".pi", "AGENTS.md"), "Hidden resource instructions", "read resource .pi/AGENTS.md", "Hidden resource instructions", null),
            ("AGENTS.override.md", Path.Combine(cwd, ".pi", "AGENTS.override.md"), "Hidden override instructions", "read resource .pi/AGENTS.override.md",
                "Hidden override instructions", null),
            ("outside AGENTS.md", outside, "Hidden outside resource instructions", "read resource " + outside.Replace('\\', '/'),
                "Hidden outside resource instructions", null),
            ("Pi documentation", readme, "Hidden docs content", "read docs README.md", "Hidden docs content", null),
        };
        foreach (var scenario in scenarios)
        {
            yield return ($"tool.exec.compact-read-{scenario.Title.Replace(' ', '-')}", Sync(() => WithReadme(readme, () =>
            {
                var component = new ToolExecutionComponent("read", "tool-compact", new JsonObject { ["path"] = scenario.Path }, null,
                    BuiltInToolRenderers.ForToolDefinition("read"), Ui(), cwd);
                component.UpdateResult(TextResult(scenario.Content));
                var collapsed = Render(component);
                Contains(collapsed, scenario.Compact, "compact header");
                Check(!collapsed.Contains(scenario.Hidden, StringComparison.Ordinal), "content hidden");
                if (scenario.Absent is not null) Check(!collapsed.Contains(scenario.Absent, StringComparison.Ordinal), "absent text");
                component.SetExpanded(true);
                Contains(Render(component), scenario.Hidden, "expanded content");
            })));
        }

        foreach (var (title, path, compact) in new[]
        {
            ("SKILL.md", Path.Combine(cwd, "attio", "SKILL.md"), "[skill] attio:120-329"),
            ("Pi documentation", readme, "read docs README.md:120-329"),
        })
        {
            yield return ($"tool.exec.compact-read-range-{title.Replace(' ', '-')}", Sync(() => WithReadme(readme, () =>
            {
                var component = new ToolExecutionComponent("read", "tool-compact-range", new JsonObject { ["path"] = path, ["offset"] = 120, ["limit"] = 210 }, null,
                    BuiltInToolRenderers.ForToolDefinition("read"), Ui(), cwd);
                var collapsed = Render(component);
                Contains(collapsed, compact, "compact range");
                Check(collapsed.IndexOf(":120-329", StringComparison.Ordinal) < collapsed.IndexOf("to expand", StringComparison.Ordinal), "range before the hint");
            })));
        }
    }

    private static void WithReadme(string readme, Action run)
    {
        var previous = ReadRenderers.ReadmePath;
        ReadRenderers.ReadmePath = () => readme;
        try { run(); }
        finally { ReadRenderers.ReadmePath = previous; }
    }

    // ------------------------------------------------------------------ bash-execution-width.test.ts and bash-execution.ts

    private static IEnumerable<(string Id, Func<Task> Run)> BashExecutionTests()
    {
        yield return ("tool.bash-exec.collapsed-respects-render-width", Sync(() =>
        {
            var component = new BashExecutionComponent("pwd", NewTui(200).Tui);
            var longLine = new string('x', 150);
            component.AppendOutput($"{longLine}\n{longLine}\n");
            component.SetComplete(0, false);
            var lines = component.Render(80);
            for (var i = 0; i < lines.Count; i++) Check(TextUtils.VisibleWidth(lines[i]) <= 80, $"Line {i} visibleWidth={TextUtils.VisibleWidth(lines[i])} > 80");
        }));

        yield return ("tool.bash-exec.recomputes-on-width-change", Sync(() =>
        {
            var component = new BashExecutionComponent("echo hello", NewTui(200).Tui);
            component.AppendOutput(string.Concat(Enumerable.Repeat("abcdefghij", 20)) + "\n");
            component.SetComplete(0, false);
            foreach (var line in component.Render(200)) Check(TextUtils.VisibleWidth(line) <= 200, "width 200");
            var lines60 = component.Render(60);
            for (var i = 0; i < lines60.Count; i++) Check(TextUtils.VisibleWidth(lines60[i]) <= 60, $"Line {i} visibleWidth={TextUtils.VisibleWidth(lines60[i])} > 60");
        }));

        var border = new string('─', 40);
        yield return ("tool.bash-exec.snapshot-running", Sync(() =>
        {
            var component = new BashExecutionComponent("pwd", Ui());
            component.AppendOutput("/tmp");
            Lines(["", border, " $ pwd", "", " /tmp", "", " ⠋ Running... (escape/ctrl+c to cancel)", border], component.Render(40), "running");
            component.SetComplete(0, false);
        }));

        yield return ("tool.bash-exec.snapshot-complete", Sync(() =>
        {
            var component = new BashExecutionComponent("pwd", Ui());
            component.AppendOutput("/tm");
            component.AppendOutput("p\r\n");
            component.SetComplete(0, false);
            Lines(["", border, " $ pwd", "", " /tmp", "", border], component.Render(40), "complete");
            Equal("/tmp\n", component.GetOutput(), "raw output");
            Equal("pwd", component.GetCommand(), "command");
        }));

        yield return ("tool.bash-exec.snapshot-collapsed-and-expanded", Sync(() =>
        {
            var component = new BashExecutionComponent("seq 25", Ui());
            component.AppendOutput(string.Join("\n", Enumerable.Range(1, 25)));
            component.SetComplete(2, false);
            // The preview keeps the last 20 visual lines of a newline plus the output, so the leading blank line is cut too.
            var collapsed = new List<string> { "", border, " $ seq 25" };
            collapsed.AddRange(Enumerable.Range(6, 20).Select(i => " " + i));
            collapsed.AddRange(["", " ... 5 more lines (ctrl+o to expand)", " (exit 2)", border]);
            Lines(collapsed, component.Render(40), "collapsed");
            component.SetExpanded(true);
            var expanded = new List<string> { "", border, " $ seq 25", "" };
            expanded.AddRange(Enumerable.Range(1, 25).Select(i => " " + i));
            expanded.AddRange(["", " (ctrl+o to collapse)", " (exit 2)", border]);
            Lines(expanded, component.Render(40), "expanded");
        }));

        yield return ("tool.bash-exec.snapshot-cancelled-truncated-excluded", Sync(() =>
        {
            var component = new BashExecutionComponent("make", Ui(), excludeFromContext: true);
            component.AppendOutput("\u001b[31mbuilding\u001b[0m");
            component.SetComplete(null, true, ToolOutputTruncator.Tail("a\nb\nc", new ToolOutputTruncationOptions(1, 100)), "/tmp/out.log");
            var wide = new string((char)0x2500, 60);
            Lines(["", wide, " $ make", "", " building", "", " (cancelled)", " Output truncated. Full output: /tmp/out.log", wide], component.Render(60), "cancelled");
            // !! commands use the dim color instead of bashMode.
            Contains(string.Join("\n", component.Render(40)), Themes.Current.Fg("dim", Themes.Current.Bold("$ make")), "dim header");
        }));
    }

    // ------------------------------------------------------------------ codemode-renderer.test.ts

    private static string RenderCodemodeResult(JsonObject result, bool isError = false, bool expanded = true, int width = 200)
    {
        var context = new ToolRenderContext
        {
            Args = new JsonObject { ["code"] = "" }, ToolCallId = "call", Cwd = "/", ExecutionStarted = true, ArgsComplete = true,
            IsPartial = false, Expanded = expanded, ShowImages = false, IsError = isError, OutputPad = 1,
        };
        var component = CodemodeRenderers.Renderers.RenderResult!(result, new ToolRenderResultOptions(expanded, false), Themes.Current, context);
        return string.Join("\n", string.Join("\n", Strip(component.Render(width))).Split('\n').Select(line => line.TrimEnd())).Trim();
    }

    private static IEnumerable<(string Id, Func<Task> Run)> CodemodeRendererTests()
    {
        const string Header = "Script completed\nWall time 0.1 seconds\nOutput:\n";
        static JsonObject Block(string text) => new() { ["type"] = "text", ["text"] = text };

        yield return ("tool.codemode.hides-header-shows-output", Sync(() =>
        {
            var text = RenderCodemodeResult(new JsonObject
            {
                ["content"] = new JsonArray(Block(Header), Block("hello")),
                ["details"] = Args("""{"calls":[{"id":"call/1","name":"read","args":"{\"path\":\"a\"}","status":"ok","durationMs":5}]}"""),
            });
            Equal("✓ read {\"path\":\"a\"} 5ms\n\nhello", text, "render");
        }));

        yield return ("tool.codemode.model-call-costs", Sync(() =>
        {
            var text = RenderCodemodeResult(new JsonObject
            {
                ["content"] = new JsonArray(Block(Header)),
                ["details"] = Args("""
                    {"calls":[
                      {"id":"call/models.classify/1","name":"models.classify","args":"scorer/judge","status":"ok","durationMs":5,"cost":0.000012936},
                      {"id":"call/models.classify/2","name":"models.classify","args":"scorer/judge","status":"ok","durationMs":5,"cost":0.02},
                      {"id":"call/models.classify/3","name":"models.classify","args":"scorer/judge","status":"ok","durationMs":5}]}
                    """),
            });
            Equal(string.Join("\n",
                "✓ models.classify scorer/judge 5ms $0.000013",
                "✓ models.classify scorer/judge 5ms $0.02",
                "✓ models.classify scorer/judge 5ms",
                "Model calls: $0.02"), text, "render");
        }));

        yield return ("tool.codemode.results-without-header", Sync(() =>
        {
            var text = RenderCodemodeResult(new JsonObject { ["content"] = new JsonArray(Block("The @options line must be followed by JavaScript source")) }, isError: true);
            Equal("The @options line must be followed by JavaScript source", text, "render");
        }));

        yield return ("tool.codemode.collapsed-wrapped-lines", Sync(() =>
        {
            var text = RenderCodemodeResult(new JsonObject
            {
                ["content"] = new JsonArray(Block(Header), Block(new string('x', 1000))),
                ["details"] = Args("""{"calls":[],"fullOutputPath":"/tmp/out.txt"}"""),
            }, expanded: false, width: 50);
            var lines = text.Split('\n');
            Equal(7, lines.Length, "line count");
            for (var i = 0; i < 5; i++) Equal(new string('x', 50), lines[i], $"line {i}");
            Check(lines[5].StartsWith("... (15 more lines,", StringComparison.Ordinal), "hint: " + lines[5]);
            Equal("Full output: /tmp/out.txt", lines[6], "full output");
        }));

        yield return ("tool.codemode.call-snapshot", Sync(() =>
        {
            var code = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"x{i};")) + "\r\n";
            var component = Tool("codemode", new JsonObject { ["code"] = code }.ToJsonString(), BuiltInToolRenderers.ForToolDefinition("codemode"), "/");
            var collapsed = new List<string> { "", "", " codemode" };
            collapsed.AddRange(Enumerable.Range(1, 10).Select(i => $" x{i};"));
            collapsed.AddRange([" ... (2 more lines, ctrl+o to expand)", ""]);
            Lines(collapsed, component.Render(60), "collapsed call");
            component.SetExpanded(true);
            var expanded = new List<string> { "", "", " codemode" };
            expanded.AddRange(Enumerable.Range(1, 12).Select(i => $" x{i};"));
            expanded.Add("");
            Lines(expanded, component.Render(60), "expanded call");
            Lines(["", "", " codemode [invalid arg]", ""], Tool("codemode", """{"code":5}""", BuiltInToolRenderers.ForToolDefinition("codemode"), "/").Render(60), "invalid code");
        }));

        yield return ("tool.codemode.earlier-calls-and-errors", Sync(() =>
        {
            var calls = new JsonArray([.. Enumerable.Range(1, 10).Select(i => (JsonNode)new JsonObject
            {
                ["id"] = $"c{i}", ["name"] = "read", ["args"] = $"{i}", ["status"] = i == 10 ? "error" : i == 9 ? "running" : "ok", ["error"] = i == 10 ? "boom\nline" : null,
            })]);
            var result = new JsonObject { ["content"] = new JsonArray(Block(Header)), ["details"] = new JsonObject { ["calls"] = calls } };
            var collapsed = RenderCodemodeResult(result, expanded: false);
            Equal(string.Join("\n", ["... (2 earlier calls, ctrl+o to expand)", .. Enumerable.Range(3, 6).Select(i => $"✓ read {i}"), "… read 9", "✗ read 10"]), collapsed, "collapsed");
            var expanded = RenderCodemodeResult(result);
            Equal(string.Join("\n", [.. Enumerable.Range(1, 8).Select(i => $"✓ read {i}"), "… read 9", "✗ read 10", "    boom", "    line"]), expanded, "expanded");
        }));
    }

    // ------------------------------------------------------------------ edit-tool-no-full-redraw.test.ts and tool-renderer-examples.test.ts

    private static List<EditTextPair> CreateLargeEdits(IReadOnlyList<string> lines, int count = 10)
    {
        int[] targets = [50, 150, 250, 350, 450, 550, 650, 750, 850, 950];
        return targets.Take(count).Select(lineNumber => new EditTextPair(
            $"{lines[lineNumber - 1]}\n{lines[lineNumber]}\n{lines[lineNumber + 1]}",
            $"{lines[lineNumber - 1]}\n{lines[lineNumber]} changed\n{lines[lineNumber + 1]}")).ToList();
    }

    private static JsonObject EditArgs(string path, IEnumerable<EditTextPair> edits) => new()
    {
        ["path"] = path,
        ["edits"] = new JsonArray([.. edits.Select(edit => (JsonNode)new JsonObject { ["oldText"] = edit.OldText, ["newText"] = edit.NewText })]),
    };

    private static IEnumerable<(string Id, Func<Task> Run)> EditRedrawTests()
    {
        yield return ("tool.edit.large-diff-preview-no-full-redraw", Sync(() =>
        {
            using var dir = new TempDir();
            var filePath = Path.Combine(dir.Path, "large-edit.txt");
            File.WriteAllText(filePath, string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"line {i}")) + "\n");
            var lines = File.ReadAllText(filePath).TrimEnd().Split('\n');
            var edits = CreateLargeEdits(lines);
            var diff = EditDiff.ComputeEditsDiffAsync(filePath, edits, Environment.CurrentDirectory).GetAwaiter().GetResult();
            if (diff.IsError) throw new InvalidOperationException(diff.Error);

            var (tui, loop, terminal) = NewTui();
            WithContext(loop, () =>
            {
                var root = new Container();
                for (var i = 0; i < 200; i++) root.AddChild(new PiSharp.Tui.Pi.Text($"history {i}", 0, 0));
                var component = new ToolExecutionComponent("edit", "tool-call-1", EditArgs(filePath, edits), null, BuiltInToolRenderers.ForToolDefinition("edit"), tui, Environment.CurrentDirectory);
                root.AddChild(component);
                tui.AddChild(root);
                tui.Start();
                loop.RunPending();
                tui.RenderNow();

                component.SetArgsComplete();
                tui.RequestRender();
                loop.RunPending();

                // Rendered text is compared without ANSI: the intra-line diff marks "changed" with theme.inverse (chalk, which
                // emits no codes in the upstream test environment; PiSharp's theme always does).
                var callOnlyRender = WaitForRenderedText(() => Render(component, 80), "line 50 changed", () => { loop.RunPending(); tui.RenderNow(); });
                Contains(callOnlyRender, "edit", "title");
                Contains(callOnlyRender, "line 950 changed", "last change");

                var redrawsBeforeResult = tui.FullRedraws;
                var clearsBeforeResult = terminal.FullClearCount;
                component.UpdateResult(new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Successfully replaced {edits.Count} block(s) in {filePath}." }),
                    ["details"] = new JsonObject { ["diff"] = diff.Diff, ["firstChangedLine"] = diff.FirstChangedLine },
                    ["isError"] = false,
                });
                tui.RequestRender();
                loop.RunPending();
                tui.RenderNow();

                Equal(redrawsBeforeResult, tui.FullRedraws, "full redraws");
                Equal(clearsBeforeResult, terminal.FullClearCount, "full clears");

                var settledRender = Render(component, 80);
                Contains(settledRender, "line 50 changed", "first change");
                Contains(settledRender, "line 950 changed", "last change");
                Check(!settledRender.Contains("Successfully replaced", StringComparison.Ordinal), "no success text");
                tui.Stop();
            });
        }));

        yield return ("tool.edit.reconstructs-preview-from-settled-result", Sync(() =>
        {
            using var dir = new TempDir();
            var filePath = Path.Combine(dir.Path, "replay-edit.txt");
            File.WriteAllText(filePath, string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}")) + "\n");
            var lines = File.ReadAllText(filePath).TrimEnd().Split('\n');
            var edits = CreateLargeEdits(lines, 2);
            var diff = EditDiff.ComputeEditsDiffAsync(filePath, edits, Environment.CurrentDirectory).GetAwaiter().GetResult();
            if (diff.IsError) throw new InvalidOperationException(diff.Error);
            File.Delete(filePath);

            var (tui, loop, _) = NewTui();
            WithContext(loop, () =>
            {
                var component = new ToolExecutionComponent("edit", "tool-call-replay", EditArgs(filePath, edits), null, BuiltInToolRenderers.ForToolDefinition("edit"), tui, Environment.CurrentDirectory);
                tui.AddChild(component);
                tui.Start();
                loop.RunPending();
                component.UpdateResult(new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Successfully replaced {edits.Count} block(s) in {filePath}." }),
                    ["details"] = new JsonObject { ["diff"] = diff.Diff, ["firstChangedLine"] = diff.FirstChangedLine },
                    ["isError"] = false,
                });
                loop.RunPending();
                var rendered = Render(component, 80);
                Contains(rendered, "line 50 changed", "first change");
                Contains(rendered, "line 150 changed", "second change");
                tui.Stop();
            });
        }));

        yield return ("tool.edit.preflight-error-without-diff", Sync(() =>
        {
            using var dir = new TempDir();
            var filePath = Path.Combine(dir.Path, "missing-edit.txt");
            File.WriteAllText(filePath, "line 0\nline 1\n");
            var (tui, loop, _) = NewTui();
            WithContext(loop, () =>
            {
                var component = new ToolExecutionComponent("edit", "tool-call-2", EditArgs(filePath, [new("does not exist", "replacement")]), null,
                    BuiltInToolRenderers.ForToolDefinition("edit"), tui, Environment.CurrentDirectory);
                tui.AddChild(component);
                tui.Start();
                loop.RunPending();
                component.SetArgsComplete();
                tui.RequestRender();
                loop.RunPending();
                var rendered = WaitForRenderedText(() => Render(component, 80), "Could not find", () => loop.RunPending());
                Check(!rendered.Contains("+1 ", StringComparison.Ordinal), "no added line");
                Check(!rendered.Contains("-1 ", StringComparison.Ordinal), "no removed line");
                tui.Stop();
            });
        }));

        yield return ("tool.edit.missing-file-preview-error", Sync(() =>
        {
            using var dir = new TempDir();
            var preview = EditDiff.ComputeEditsDiffAsync("nope.txt", [new("a", "b")], dir.Path).GetAwaiter().GetResult();
            Equal("Could not edit file: nope.txt. Error code: ENOENT.", preview.Error, "error");
        }));

        // Regression test for https://github.com/earendil-works/pi/issues/10072
        yield return ("tool.edit.default-shell-matches-shell-less-definition", Sync(() =>
        {
            using var dir = new TempDir();
            List<string> RenderEdit(ToolRenderers definition) => new ToolExecutionComponent("edit", "edit-shell-test",
                Args("""{"path":"notes.txt","oldText":"before","newText":"after"}"""), null, definition, Ui(), dir.Path).Render(40);
            var minimalMode = BuiltInToolRenderers.ForToolDefinition("edit")! with { RenderShell = ToolRenderShell.Default };
            var withoutShell = minimalMode with { RenderShell = null };
            Equal(string.Join("\n", RenderEdit(withoutShell)), string.Join("\n", RenderEdit(minimalMode)), "default shell render");
        }));
    }

    // ------------------------------------------------------------------ output-pad.test.ts

    private static IEnumerable<(string Id, Func<Task> Run)> OutputPadTests()
    {
        static List<string> RenderLines(IComponent component) =>
            Strip(component.Render(60)).Select(line => line.TrimEnd()).Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"[\w$(]")).ToList();

        ToolExecutionComponent CreateTool(ToolRenderers? definition, int outputPad)
        {
            var component = new ToolExecutionComponent("custom_tool", "id", new JsonObject(), new ToolExecutionOptions(OutputPad: outputPad), definition, Ui(), "/");
            component.UpdateResult(TextResult("ok"));
            return component;
        }

        var components = new (string Name, Func<int, (IComponent Component, Action<int> SetOutputPad)> Create)[]
        {
            ("bash-execution", outputPad =>
            {
                var component = new BashExecutionComponent("pwd", Ui(), false, outputPad);
                component.AppendOutput("/tmp");
                component.SetComplete(1, false);
                return (component, component.SetOutputPad);
            }),
            ("tool-execution", outputPad => { var c = CreateTool(BaseDefinition, outputPad); return (c, c.SetOutputPad); }),
            ("tool-execution-without-definition", outputPad => { var c = CreateTool(null, outputPad); return (c, c.SetOutputPad); }),
            ("self-rendered-edit-result", outputPad =>
            {
                var component = new ToolExecutionComponent("edit", "id", Args("""{"path":"file.txt","edits":[{"oldText":"old","newText":"new"}]}"""),
                    new ToolExecutionOptions(OutputPad: outputPad), BuiltInToolRenderers.ForToolDefinition("edit"), Ui(), "/");
                component.UpdateResult(TextResult("Could not find old text", isError: true));
                return (component, component.SetOutputPad);
            }),
        };
        foreach (var (name, create) in components)
        {
            yield return ($"tool.output-pad.{name}", Sync(() =>
            {
                var (component, setOutputPad) = create(0);
                var lines = RenderLines(component);
                Check(lines.Count > 0, "rendered lines");
                Equal(0, lines.Count(line => line.StartsWith(' ')), "indented lines at outputPad 0");
                setOutputPad(1);
                Equal(string.Join("\n", lines.Select(line => " " + line)), string.Join("\n", RenderLines(component)), "outputPad 1");
            }));
        }
    }

    // ------------------------------------------------------------------ render snapshots of the built-in renderers

    private static IEnumerable<(string Id, Func<Task> Run)> SnapshotTests()
    {
        var cwd = Environment.CurrentDirectory;
        ToolExecutionComponent Builtin(string name, string args) => new(name, "snap-" + name, Args(args), null, BuiltInToolRenderers.ForToolDefinition(name), Ui(), cwd);

        // read
        yield return ("tool.snapshot.read", Sync(() =>
        {
            var component = Builtin("read", """{"path":"src/a.txt"}""");
            Lines(["", "", " read src/a.txt", ""], component.Render(60), "pending");
            component.UpdateResult(TextResult("one\ntwo\n"));
            Lines(["", "", " read src/a.txt", ""], component.Render(60), "success collapsed");
            component.SetExpanded(true);
            Lines(["", "", " read src/a.txt", "", " one", " two", ""], component.Render(60), "success expanded");
            component.SetExpanded(false);
            component.UpdateResult(TextResult("ENOENT: no such file or directory", isError: true));
            Lines(["", "", " read src/a.txt", "", " ENOENT: no such file or directory", ""], component.Render(60), "error");
        }));
        yield return ("tool.snapshot.read-range-and-truncation", Sync(() =>
        {
            var component = Builtin("read", """{"path":"src/a.txt","offset":2,"limit":3}""");
            component.UpdateResult(TextResult("two\nthree\nfour", details: Args("""{"truncation":{"truncated":true,"truncatedBy":"lines","outputLines":3,"totalLines":9,"maxLines":3}}""")));
            component.SetExpanded(true);
            Lines(["", "", " read src/a.txt:2-4", "", " two", " three", " four", " [Truncated: showing 3 of 9 lines (3 line limit)]", ""], component.Render(60), "range + lines truncation");
            component.UpdateResult(TextResult("x", details: Args("""{"truncation":{"truncated":true,"truncatedBy":"bytes","outputLines":1,"firstLineExceedsLimit":true}}""")));
            Lines(["", "", " read src/a.txt:2-4", "", " x", " [First line exceeds 50.0KB limit]", ""], component.Render(60), "first line too long");
            Lines(["", "", " read [invalid arg]", ""], Builtin("read", """{"path":5}""").Render(60), "invalid path");
        }));

        // write
        yield return ("tool.snapshot.write", Sync(() =>
        {
            var content = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"l{i}")) + "\n";
            var component = Builtin("write", new JsonObject { ["path"] = "notes.txt", ["content"] = content }.ToJsonString());
            var collapsed = new List<string> { "", "", " write notes.txt", "" };
            collapsed.AddRange(Enumerable.Range(1, 10).Select(i => $" l{i}"));
            collapsed.AddRange([" ... (2 more lines, 12 total, ctrl+o to expand)", ""]);
            Lines(collapsed, component.Render(60), "pending collapsed");
            component.UpdateResult(TextResult("Successfully wrote 40 bytes to notes.txt"));
            Lines(collapsed, component.Render(60), "success collapsed");
            component.SetExpanded(true);
            var expanded = new List<string> { "", "", " write notes.txt", "" };
            expanded.AddRange(Enumerable.Range(1, 12).Select(i => $" l{i}"));
            expanded.Add("");
            Lines(expanded, component.Render(60), "success expanded");

            var failed = Builtin("write", """{"path":"a.txt","content":"x"}""");
            failed.UpdateResult(TextResult("EACCES: permission denied", isError: true));
            Lines(["", "", " write a.txt", "", " x", "", " EACCES: permission denied", ""], failed.Render(60), "error");
            Lines(["", "", " write a.txt", "", " [invalid content arg - expected string]", ""], Builtin("write", """{"path":"a.txt","content":5}""").Render(60), "invalid content");
        }));

        // edit (the tool definition draws its own shell)
        yield return ("tool.snapshot.edit", Sync(() =>
        {
            var component = Builtin("edit", """{"path":"file.txt","oldText":"old","newText":"new"}""");
            Lines(["", "", " edit file.txt", ""], component.Render(60), "pending");
            component.UpdateResult(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Successfully replaced 1 block(s) in file.txt." }),
                ["details"] = new JsonObject { ["diff"] = "-1 old\n+1 new", ["firstChangedLine"] = 1 }, ["isError"] = false });
            Lines(["", "", " edit file.txt", "", " -1 old", " +1 new", ""], component.Render(60), "success");
            var rendered = string.Join("\n", component.Render(60));
            Contains(rendered, Themes.Current.GetBgAnsi("toolSuccessBg"), "success background");

            var failed = Builtin("edit", """{"path":"file.txt","oldText":"old","newText":"new"}""");
            failed.UpdateResult(TextResult("Could not find old text", isError: true));
            Lines(["", "", " edit file.txt", "", "", " Could not find old text"], failed.Render(60), "error");
            Contains(string.Join("\n", failed.Render(60)), Themes.Current.GetBgAnsi("toolErrorBg"), "error background");
        }));

        // bash
        yield return ("tool.snapshot.bash", Sync(() =>
        {
            var component = Builtin("bash", """{"command":"seq 8","timeout":30}""");
            Lines(["", "", " $ seq 8 (timeout 30s)", ""], component.Render(60), "pending");
            component.UpdateResult(TextResult(string.Join("\n", Enumerable.Range(1, 8))));
            Lines(["", "", " $ seq 8 (timeout 30s)", "", " ... (3 earlier lines, ctrl+o to expand)", " 4", " 5", " 6", " 7", " 8", ""], component.Render(60), "success collapsed");
            component.SetExpanded(true);
            Lines(["", "", " $ seq 8 (timeout 30s)", "", " 1", " 2", " 3", " 4", " 5", " 6", " 7", " 8", ""], component.Render(60), "success expanded");

            var failed = Builtin("bash", """{"command":"false"}""");
            failed.UpdateResult(TextResult("boom\n\nCommand exited with code 1", isError: true, durationMs: 1500));
            Lines(["", "", " $ false", "", " boom", "", " Command exited with code 1", "", " Took 1.5s", ""], failed.Render(60), "error");
            Lines(["", "", " $ [invalid arg]", ""], Builtin("bash", """{"command":5}""").Render(60), "invalid command");
            Lines(["", "", " PS> Get-Date", ""], new ToolExecutionComponent("powershell", "ps", Args("""{"command":"Get-Date"}"""), null,
                BuiltInToolRenderers.ForToolDefinition("powershell"), Ui(), cwd).Render(60), "powershell prompt");
        }));

        // grep
        yield return ("tool.snapshot.grep", Sync(() =>
        {
            var component = Builtin("grep", """{"pattern":"TODO","path":"src","glob":"*.ts","limit":50}""");
            Lines(["", "", " grep /TODO/ in src (*.ts) limit 50", ""], component.Render(60), "pending");
            component.UpdateResult(TextResult(string.Join("\n", Enumerable.Range(1, 17).Select(i => $"m{i}")), details: Args("""{"matchLimitReached":50,"linesTruncated":true}""")));
            var collapsed = new List<string> { "", "", " grep /TODO/ in src (*.ts) limit 50", "" };
            collapsed.AddRange(Enumerable.Range(1, 15).Select(i => $" m{i}"));
            collapsed.AddRange([" ... (2 more lines, ctrl+o to expand)", " [Truncated: 50 matches limit, some lines truncated]", ""]);
            Lines(collapsed, component.Render(60), "success collapsed");
            component.SetExpanded(true);
            var expanded = new List<string> { "", "", " grep /TODO/ in src (*.ts) limit 50", "" };
            expanded.AddRange(Enumerable.Range(1, 17).Select(i => $" m{i}"));
            expanded.AddRange([" [Truncated: 50 matches limit, some lines truncated]", ""]);
            Lines(expanded, component.Render(60), "success expanded");
            var failed = Builtin("grep", """{"pattern":"("}""");
            failed.UpdateResult(TextResult("regex parse error", isError: true));
            Lines(["", "", " grep /(/ in .", "", " regex parse error", ""], failed.Render(60), "error");
        }));

        // find
        yield return ("tool.snapshot.find", Sync(() =>
        {
            var component = Builtin("find", """{"pattern":"*.cs"}""");
            Lines(["", "", " find *.cs in .", ""], component.Render(60), "pending");
            component.UpdateResult(TextResult("a.cs\nb.cs", details: Args("""{"truncation":{"truncated":true}}""")));
            Lines(["", "", " find *.cs in .", "", " a.cs", " b.cs", " [Truncated: 50.0KB limit]", ""], component.Render(60), "success");
            var limited = Builtin("find", """{"pattern":"*","path":"src","limit":2}""");
            limited.UpdateResult(TextResult(string.Join("\n", Enumerable.Range(1, 22).Select(i => $"f{i}")), details: Args("""{"resultLimitReached":2}""")));
            var collapsed = new List<string> { "", "", " find * in src (limit 2)", "" };
            collapsed.AddRange(Enumerable.Range(1, 20).Select(i => $" f{i}"));
            collapsed.AddRange([" ... (2 more lines, ctrl+o to expand)", " [Truncated: 2 results limit]", ""]);
            Lines(collapsed, limited.Render(60), "collapsed");
            Lines(["", "", " find [invalid arg] in [invalid arg]", ""], Builtin("find", """{"pattern":1,"path":2}""").Render(60), "invalid args");
        }));

        // ls
        yield return ("tool.snapshot.ls", Sync(() =>
        {
            var component = Builtin("ls", "{}");
            Lines(["", "", " ls .", ""], component.Render(60), "pending");
            component.UpdateResult(TextResult("a/\nb", details: Args("""{"entryLimitReached":500,"truncation":{"truncated":true,"maxBytes":2048}}""")));
            Lines(["", "", " ls .", "", " a/", " b", " [Truncated: 500 entries limit, 2.0KB limit]", ""], component.Render(60), "success");
            var failed = Builtin("ls", """{"path":"missing","limit":10}""");
            failed.UpdateResult(TextResult("Path not found: missing", isError: true));
            Lines(["", "", " ls missing (limit 10)", "", " Path not found: missing", ""], failed.Render(60), "error");
            Lines(["", "", " ls [invalid arg]", ""], Builtin("ls", """{"path":5}""").Render(60), "invalid path");
        }));

        // MCP tools
        yield return ("tool.snapshot.mcp", Sync(() =>
        {
            var component = new ToolExecutionComponent("mcp__srv__search", "mcp", Args("""{"query":"pi"}"""), null, BuiltInToolRenderers.ForToolDefinition("mcp__srv__search"), Ui(), cwd);
            Lines(["", "", " srv/search query=\"pi\"", ""], component.Render(60), "pending");
            component.UpdateResult(TextResult(new string('j', 400), details: Args("""{"server":"srv","tool":"search","fullOutputPath":"/tmp/mcp.txt"}""")));
            Lines(["", "", " srv/search query=\"pi\"", "", .. Enumerable.Repeat(" " + new string('j', 58), 5), " ... (2 more lines, ctrl+o to expand)", " Full output: /tmp/mcp.txt", ""],
                component.Render(60), "success collapsed");
            component.SetExpanded(true);
            Lines(["", "", " srv/search", "   query: pi", "", .. Enumerable.Repeat(" " + new string('j', 58), 6), " " + new string('j', 52), ""],
                component.Render(60), "success expanded");
            var failed = new ToolExecutionComponent("mcp__srv__search", "mcp", Args("{}"), null, BuiltInToolRenderers.ForToolDefinition("mcp__srv__search", "Search Server/search"), Ui(), cwd);
            failed.UpdateResult(TextResult("MCP tool srv/search returned an error", isError: true));
            Lines(["", "", " Search Server/search", "", " MCP tool srv/search returned an error", ""], failed.Render(60), "error");
            Contains(string.Join("\n", failed.Render(60)), Themes.Current.Fg("error", "MCP tool srv/search returned an error"), "error color");
        }));

        // The generic row of a tool without renderers, and image placeholders.
        yield return ("tool.snapshot.generic-and-images", Sync(() =>
        {
            var component = new ToolExecutionComponent("custom", "c", Args("""{"a":1}"""), null, null, Ui(), cwd);
            Lines(["", "", " custom", "", " {", "   \"a\": 1", " }", ""], component.Render(60), "pending");
            component.UpdateResult(new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "\u001b[31mred\u001b[0m\r" }, new JsonObject { ["type"] = "image", ["data"] = TinyPng, ["mimeType"] = "image/png" }),
                ["isError"] = false,
            });
            Lines(["", "", " custom", "", " {", "   \"a\": 1", " }", " red", " [Image: [image/png] 1x1]", ""], component.Render(60), "image placeholder without image support");
            Contains(string.Join("\n", component.Render(60)), Themes.Current.GetBgAnsi("toolSuccessBg"), "success background");
        }));

        yield return ("tool.snapshot.images-inline", Sync(() => WithCapabilities(new(ImageProtocol.ITerm2, true, true), () =>
        {
            var component = Builtin("read", """{"path":"pixel.png"}""");
            component.UpdateResult(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["data"] = TinyPng, ["mimeType"] = "image/png" }), ["isError"] = false });
            var lines = component.Render(60);
            Check(lines.Any(TerminalImage.IsImageLine), "inline image line");
            component.SetShowImages(false);
            Check(!component.Render(60).Any(TerminalImage.IsImageLine), "images hidden");
        })));
    }

    // ------------------------------------------------------------------ helpers: diff, JSON, numbers, paths

    private static IEnumerable<(string Id, Func<Task> Run)> HelperTests()
    {
        yield return ("tool.diff.render", Sync(() =>
        {
            var rendered = Diff.RenderDiff("  1 a\n-2 foo bar\n+2 foo baz\n  3 c\n     ...\n-4 x\n-5 y\n+4 z\n+6\tadded");
            Lines(["  1 a", "-2 foo bar", "+2 foo baz", "  3 c", "     ...", "-4 x", "-5 y", "+4 z", "+6 added"], rendered.Split('\n'), "lines");
            Contains(rendered, Themes.Current.Fg("toolDiffRemoved", "-2 foo " + Themes.Current.Inverse("bar")), "intra-line removal");
            Contains(rendered, Themes.Current.Fg("toolDiffAdded", "+2 foo " + Themes.Current.Inverse("baz")), "intra-line addition");
            Contains(rendered, Themes.Current.Fg("toolDiffRemoved", "-4 x"), "multi-line removal plain");
            Contains(rendered, Themes.Current.Fg("toolDiffContext", "  1 a"), "context color");
        }));

        yield return ("tool.diff.words", Sync(() =>
        {
            static string Show(string a, string b) => string.Join("|", JsDiff.DiffWords(a, b).Select(c => (c.Added ? "I:" : c.Removed ? "D:" : "K:") + c.Value));
            // The examples in jsdiff's dedupeWhitespaceInChangeObjects.
            Equal("K:foo |D:bar |K:baz", Show("foo bar baz", "foo baz"), "deletion");
            Equal("K:foo |D:bar|I:qux|K: baz", Show("foo bar baz", "foo qux baz"), "replacement");
            Equal("K:foo  |D: bar |K:baz", Show("foo   bar baz", "foo  baz"), "whitespace");
            Equal("K:same", Show("same", "same"), "identity");
        }));

        yield return ("tool.json.stringify-and-numbers", Sync(() =>
        {
            Equal("{\"a\":1,\"b\":\"x\\n\\u0001\",\"c\":[true,null],\"d\":{}}", ToolJson.Stringify(JsonNode.Parse("{\"a\":1.0,\"b\":\"x\\n\\u0001\",\"c\":[true,null],\"d\":{}}")), "compact");
            Equal("{\n  \"a\": [\n    1,\n    2\n  ],\n  \"b\": []\n}", ToolJson.Stringify(JsonNode.Parse("{\"a\":[1,2],\"b\":[]}"), 2), "indented");
            Equal("1e+21", ToolJson.JsNumber(1e21), "large exponent");
            Equal("100000000000000000000", ToolJson.JsNumber(1e20), "large integer");
            Equal("1.5e-7", ToolJson.JsNumber(1.5e-7), "small exponent");
            Equal("0.000001", ToolJson.JsNumber(1e-6), "small fixed");
            Equal("-12.5", ToolJson.JsNumber(-12.5), "negative");
            Equal("1.0KB", ToolTruncate.FormatSize(1024), "KB");
            Equal("1023B", ToolTruncate.FormatSize(1023), "B");
            Equal("2.5MB", ToolTruncate.FormatSize(2.5 * 1024 * 1024), "MB");
            Equal("$0.000013", CodemodeRenderers.FormatCost(0.000012936), "cost fraction");
            Equal("$1.2e-7", CodemodeRenderers.FormatCost(0.00000012), "cost exponent");
            Equal("$0.25", CodemodeRenderers.FormatCost(0.25), "cost cents");
            Equal("1.5s", CodemodeRenderers.FormatDuration(1500), "codemode duration");
            Equal("3ms", CodemodeRenderers.FormatDuration(2.5), "codemode rounding");
        }));

        yield return ("tool.render-utils.call-with-args", Sync(() =>
        {
            var theme = Themes.Current;
            Equal("t", Strip(RenderUtils.FormatToolCallWithArgs("t", null, theme, false)), "no args");
            Equal("t", Strip(RenderUtils.FormatToolCallWithArgs("t", new JsonObject(), theme, false)), "empty args");
            Equal("t args=[1,2]", Strip(RenderUtils.FormatToolCallWithArgs("t", new JsonArray(1, 2), theme, false)), "array args");
            Equal("t\n  args: [\n      1\n    ]", Strip(RenderUtils.FormatToolCallWithArgs("t", new JsonArray(1), theme, true)), "array args expanded");
            Equal("t\n  s: a   b", Strip(RenderUtils.FormatToolCallWithArgs("t", new JsonObject { ["s"] = "a\tb\r" }, theme, true)), "tabs and CR");
            var preview = Strip(RenderUtils.FormatToolCallWithArgs("t", new JsonObject { ["s"] = new string('y', 200) }, theme, false));
            Equal("t s=\"" + new string('y', 94) + "...", preview, "collapsed cut to 100 characters");
            Equal("", RenderUtils.Str(null), "str null");
            Equal(null, RenderUtils.Str(JsonValue.Create(1)), "str number");
        }));

        yield return ("tool.render-utils.paths", Sync(() =>
        {
            var previous = RenderUtils.HomeDirectory;
            var home = Path.Combine(Path.GetTempPath(), "pisharp-home");
            RenderUtils.HomeDirectory = () => home;
            try
            {
                Equal("~" + Path.DirectorySeparatorChar + "x.txt", RenderUtils.ShortenPath(Path.Combine(home, "x.txt")), "home shortened");
                Equal("rel.txt", RenderUtils.ShortenPath("rel.txt"), "relative unchanged");
                var cwd = Path.Combine(Path.GetTempPath(), "pisharp-cwd");
                Equal(Path.Combine(cwd, "a b.txt"), ToolPaths.ResolveToCwd("@a b.txt", cwd), "resolveToCwd strips @ and unicode spaces");
                Equal(Path.Combine(home, "n.txt"), ToolPaths.ResolveToCwd("~/n.txt", cwd), "tilde");
                Equal("sub/f.md", ToolPaths.FormatPathRelativeToCwdOrAbsolute(Path.Combine(cwd, "sub", "f.md"), cwd), "relative inside cwd");
                var styled = RenderUtils.LinkPath("x", "a.txt", cwd);
                Contains(styled, "\u001b]8;;file:", "hyperlink");
                Equal("x", Strip(styled), "hyperlink text");
            }
            finally { RenderUtils.HomeDirectory = previous; }
        }));

        yield return ("tool.registry", Sync(() =>
        {
            var all = BuiltInToolRenderers.CreateAllToolRenderers();
            Equal("bash,edit,find,grep,ls,powershell,read,write", string.Join(",", all.Keys.Order(StringComparer.Ordinal)), "built-in names");
            Equal(null, all["edit"].RenderShell, "createAllToolRenderers edit has no shell");
            Equal(ToolRenderShell.Self, BuiltInToolRenderers.ForToolDefinition("edit")!.RenderShell, "edit tool definition draws its own shell");
            Check(BuiltInToolRenderers.ForToolDefinition("codemode") is not null, "codemode");
            Check(BuiltInToolRenderers.ForToolDefinition("custom") is null, "unknown tool");
            Check(BuiltInToolRenderers.WithBuiltInRenderers("custom", null) is null, "unknown without definition");
            Check(ReferenceEquals(BaseDefinition, BuiltInToolRenderers.WithBuiltInRenderers("custom", BaseDefinition)), "unknown keeps definition");
            var merged = BuiltInToolRenderers.WithBuiltInRenderers("read", new ToolRenderers(ToolRenderShell.Self))!;
            Check(merged.RenderShell == ToolRenderShell.Self && merged.RenderCall is not null && merged.RenderResult is not null, "merged slots keep the definition's shell");
        }));
    }

    // ------------------------------------------------------------------ extension renderers (async rows)

    private static IEnumerable<(string Id, Func<Task> Run)> ExtensionAdapterTests()
    {
        yield return ("tool.extension.async-rows", Sync(() =>
        {
            var calls = 0;
            var renderers = new ExtensionToolRenderers(null,
                (context, width, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(new ExtensionCustomComponentRows([$"ext call {context.ToolName} w{width} expanded={context.Expanded}"], [0]));
                },
                (result, context, width, _) => Task.FromResult(new ExtensionCustomComponentRows(
                    [$"ext result {result.Value.GetProperty("content")[0].GetProperty("text").GetString()} partial={context.IsPartial}"], [0])));
            var component = ToolExecutionComponent.FromExtensionRenderers("ext_tool", "e1", Args("""{"q":1}"""), null, renderers, Ui(), "/");
            Equal("", Render(component), "nothing until rows arrive");
            var rendered = WaitForRenderedText(() => Render(component), "ext call ext_tool w118 expanded=False");
            Contains(rendered, "ext call", "call rows");
            var before = calls;
            Render(component);
            Equal(before, calls, "completed rows are reused for the same width and context");
            component.UpdateResult(TextResult("done"), true);
            WaitForRenderedText(() => Render(component), "ext result done partial=True");
            component.SetExpanded(true);
            WaitForRenderedText(() => Render(component), "expanded=True");
        }));

        yield return ("tool.extension.failed-renderer-falls-back", Sync(() =>
        {
            var renderers = new ExtensionToolRenderers(ExtensionToolRenderShell.Default,
                (_, _, _) => Task.FromException<ExtensionCustomComponentRows>(new InvalidOperationException("boom")));
            var component = ToolExecutionComponent.FromExtensionRenderers("ext_tool", "e2", Args("""{"q":1}"""), null, renderers, Ui(), "/");
            Render(component);
            var rendered = WaitForRenderedText(() => { component.Invalidate(); return Render(component); }, "ext_tool q=1");
            Contains(rendered, "ext_tool q=1", "generic call header");
        }));

        yield return ("tool.extension.self-shell", Sync(() =>
        {
            var renderers = new ExtensionToolRenderers(ExtensionToolRenderShell.Self,
                (_, width, _) => Task.FromResult(new ExtensionCustomComponentRows([new string('z', width + 10)], [width + 10])));
            var component = ToolExecutionComponent.FromExtensionRenderers("ext_tool", "e3", Args("{}"), null, renderers, Ui(), "/");
            var rendered = WaitForRenderedText(() => Render(component, 20), "zzz");
            Lines(["", new string('z', 20)], rendered.Split('\n'), "self shell rows cut to the width");
        }));
    }
}
