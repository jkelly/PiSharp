using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class TerminalEditorIntegrationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string host, string cli) =>
    [
        ("terminal editor integration actual decoder paste expansion logical caret tiny viewport and joined cleanup", InputAndView),
        ("terminal editor integration live presentation streams chunks progress errors prompt and resize", LivePresentation),
        ("terminal editor integration cooked presentation retains delta diagnostics", CookedPresentation),
        ("terminal editor integration authoritative block endings reconcile streamed and end-only content", AuthoritativeEndings),
        ("terminal editor integration input hint follows rebound and disabled submit bindings", ConfiguredHint),
        ("terminal editor integration actual durable CLI submits expanded marker payload after caret insertion", () => DurableSubmission(host, cli))
    ];
    private static async Task AuthoritativeEndings()
    {
        var output = new StreamingPresentation();
        using var frontend = new InteractiveSessionFrontend(output, nativePresentation: true);
        await Observe(new { type = "message_start", message = new { role = "assistant" } });
        await Update(new { type = "text_delta", contentIndex = 0, delta = "draft" });
        await Update(new { type = "text_end", contentIndex = 0, content = "corrected" });
        Check(output.Lines[^1] == "[assistant final block 0] corrected\n", "Corrected block ending was hidden by its streamed draft.");
        await Update(new { type = "text_delta", contentIndex = 1, delta = "unchanged" });
        await Update(new { type = "text_end", contentIndex = 1, content = "unchanged" });
        await Update(new { type = "text_end", contentIndex = 2, content = "end-only" });
        Check(output.Lines[^1] == "[assistant final block 2] end-only\n", "End-only block was hidden by another block's delta.");
        await Observe(new { type = "message_end", message = new { role = "assistant", stopReason = "stop", content = new object[]
        {
            new { type = "text", text = "corrected" }, new { type = "text", text = "unchanged" },
            new { type = "text", text = "end-only" }, new { type = "text", text = "final-only" },
            new { type = "toolCall", name = "read" }
        } } });
        Check(output.Lines[^1] == "[assistant final block 3] final-only\n[assistant final block 4] tool:read\n[assistant ended] stop\n",
            "Final reconciliation repeated matching blocks or lost final-only content/tool blocks.");
        await Observe(new { type = "message_start", message = new { role = "assistant" } });
        await Update(new { type = "text_delta", contentIndex = 0, delta = "another draft" });
        await Observe(new { type = "message_end", message = new { role = "assistant", content = "final correction", stopReason = "stop" } });
        Check(output.Lines[^1].StartsWith("[assistant final block 0] final correction\n", StringComparison.Ordinal),
            "Authoritative final correction without text_end was hidden.");
        await Observe(new { type = "message_start", message = new { role = "assistant" } });
        await Update(new { type = "text_delta", contentIndex = 0, delta = "discarded" });
        await Update(new { type = "text_end", contentIndex = 0, content = "" });
        Check(output.Lines[^1] == "[assistant final block 0] [empty]\n", "An empty authoritative replacement retained the draft as final.");
        Task Observe(object value) => frontend.ObserveAsync(PiSharp.Contracts.JsonData.Parse(JsonSerializer.Serialize(value)), default).AsTask();
        Task Update(object value) => Observe(new { type = "message_update", assistantMessageEvent = value });
    }

    private static async Task ConfiguredHint()
    {
        foreach (var keys in new[] { new[] { "ctrl+s" }, new[] { "ctrl+s", "alt+enter" }, Array.Empty<string>() })
        {
            var console = new ConsoleFixture { Viewport = new(96, 12, 0, 0, 96, 12) };
            var bindings = new TerminalKeybindings((_, _) => false,
                [new KeyValuePair<string, TerminalKeybindingValue?>("tui.input.submit", new TerminalKeybindingValue(keys))]);
            var visible = "";
            await using var view = new TerminalSessionView(console, console,
                (_, frame) => visible = string.Join('\n', frame.Frame.Rows.Select(row => row.Text)), null, bindings);
            await view.StartAsync(default);
            var editor = new TerminalTextEditorPasteController();
            await view.SetDraftAsync(new("", 0) { Layout = editor.CaptureLayoutInput() }, default);
            var expected = keys.Length == 0 ? "submit unbound" : string.Join(" / ", keys) + " sends";
            Check(visible.Contains("Message: " + expected, StringComparison.Ordinal) && !visible.Contains("Enter sends", StringComparison.Ordinal),
                "Input hint advertised a submit key that the active owner does not use.");
            await view.DisposeAsync(); console.AssertJoined();
        }
    }

    private sealed class StreamingPresentation : IInteractiveSessionPresentation, IInteractiveAssistantStreamingPresentation
    {
        internal readonly List<string> Lines = [], Deltas = [];
        public ValueTask PresentAsync(string displayText, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Lines.Add(displayText); return ValueTask.CompletedTask; }
        public ValueTask PresentAssistantDeltaAsync(string displayText, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Deltas.Add(displayText); return ValueTask.CompletedTask; }
    }
    private static async Task LivePresentation()
    {
        var console = new ConsoleFixture { Viewport = new(96, 12, 0, 0, 96, 12) };
        var latestFrame = "";
        await using var view = new TerminalSessionView(console, console,
            (_, frame) => latestFrame = string.Join('\n', frame.Frame.Rows.Select(row => row.Text)));
        using var frontend = new InteractiveSessionFrontend(view, nativePresentation: true);
        await view.StartAsync(default);
        var editor = new TerminalTextEditorPasteController(); editor.SetText("draft"); editor.SetCursor(2);
        var captured = editor.CaptureLayoutInput();
        await view.SetDraftAsync(new("draft", 2) { Layout = captured }, default);
        await Observe(new { type = "message_start", message = new { role = "assistant" } });
        await Delta("");
        await Delta("hello ");
        await Delta("world");
        var streamed = await FullFrame();
        Check(streamed.Contains("[assistant] hello world", StringComparison.Ordinal) &&
            !streamed.Contains("[assistant delta]", StringComparison.Ordinal), "Chunks did not compose into a readable live line.");
        Check(streamed.Contains("Message: Enter sends", StringComparison.Ordinal) && streamed.Contains("draft", StringComparison.Ordinal),
            "Persistent input hint or existing draft disappeared during streaming.");
        await Observe(new { type = "tool_execution_start", toolName = "read" });
        await Delta("next");
        var progress = await FullFrame();
        Check(progress.Contains("[tool start] read", StringComparison.Ordinal) && progress.Contains("[assistant] next", StringComparison.Ordinal),
            "Tool progress did not separate the next assistant segment.");
        await Observe(new { type = "message_end", message = new { role = "assistant", content = "hello worldnext", stopReason = "error", errorMessage = "injected failure\u001b[31m" } });
        var ended = await FullFrame();
        Check(!ended.Contains("hello worldnext", StringComparison.Ordinal) && ended.Contains("[assistant ended] error", StringComparison.Ordinal) &&
            ended.Contains("[assistant error] injected failure", StringComparison.Ordinal) && !ended.Contains("\u001b[31m", StringComparison.Ordinal),
            "Completion duplicated streamed content or lost/activated the provider error.");
        await Observe(new { type = "message_start", message = new { role = "assistant" } });
        await Observe(new { type = "message_end", message = new { role = "assistant", content = "final-only", stopReason = "stop" } });
        Check((await FullFrame()).Contains("[assistant] final-only", StringComparison.Ordinal), "A non-streaming final answer was hidden.");
        console.Viewport = new(1, 1, 0, 0, 1, 1); await view.RefreshViewportAsync(default);
        Check((await FullFrame()).Contains("draft", StringComparison.Ordinal) && editor.Snapshot.Text == "draft" &&
            editor.Snapshot.CursorUtf16Offset == 2 && editor.LayoutIdentity == captured.Identity,
            "Resize/stream presentation mutated the captured editor or failed to recover.");
        await view.DisposeAsync(); console.AssertJoined();
        Check(console.Frames[^1] == "\u001b[?2004l\u001b[?1049l", "Presentation did not restore the alternate screen.");

        Task Observe(object value) => frontend.ObserveAsync(PiSharp.Contracts.JsonData.Parse(JsonSerializer.Serialize(value)), default).AsTask();
        Task Delta(string value) => Observe(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", delta = value } });
        async Task<string> FullFrame()
        {
            var width = console.Viewport.Columns == 96 ? 95 : 96;
            console.Viewport = new(width, 12, 0, 0, width, 12);
            await view.RefreshViewportAsync(default); return latestFrame;
        }
    }

    private static async Task CookedPresentation()
    {
        using var output = new StringWriter();
        using var frontend = new InteractiveSessionFrontend(output, nativePresentation: true);
        await frontend.ObserveAsync(PiSharp.Contracts.JsonData.Parse("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"hello\"}}"), default);
        await frontend.ObserveAsync(PiSharp.Contracts.JsonData.Parse("{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":\"hello\",\"stopReason\":\"stop\"}}"), default);
        Check(output.ToString().Contains("[assistant delta] hello\n", StringComparison.Ordinal) &&
            output.ToString().Contains("[assistant] hello\n", StringComparison.Ordinal), "Cooked/RPC diagnostics changed.");
    }
    private static async Task InputAndView()
    {
        var console = new ConsoleFixture { Viewport = new(1, 1, 0, 0, 1, 1) };
        var view = new TerminalSessionView(console, console);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var snapshots = Channel.CreateUnbounded<TerminalDraftSnapshot>(); var lines = new List<string>();
        await view.StartAsync(cancel.Token);
        var input = new TerminalChatInput(console).RunAsync((line, _) => { lines.Add(line); return Task.FromResult(true); },
            async (draft, token) => { await view.SetDraftAsync(draft, token); snapshots.Writer.TryWrite(draft); },
            cancel.Cancel, cancel.Token);
        try
        {
            await console.Feed("e\u0301"); var initial = await Next(); Check(initial.Text == "e\u0301" && initial.CursorUtf16Offset == 2, "Decoded combining draft changed.");
            await console.Feed("\u001b[D"); var left = await Next(); Check(left.CursorUtf16Offset == 0, "Actual left key did not move across combining grapheme.");
            await console.Feed("X"); var inserted = await Next(); Check(inserted.Text == "Xe\u0301" && inserted.CursorUtf16Offset == 1, "Caret insertion changed draft order.");
            var payload = new string('p', 1001) + "\n\u6587\U0001f642";
            await console.Feed("\u001b[200~" + payload[..500]);
            await console.Feed(payload[500..] + "\u001b[201~");
            var pasted = await Next(); Check(pasted.Text.Contains("[paste #1", StringComparison.Ordinal) && !pasted.Text.Contains(payload, StringComparison.Ordinal), "Production input did not use bounded paste marker.");
            console.Viewport = new(3, 2, 0, 0, 3, 2); await view.RefreshViewportAsync(cancel.Token);
            await console.Feed("\r"); var cleared = await Next(); Check(cleared.Text == "" && lines.Single() == "X" + payload + "e\u0301", "Submitted line did not expand actual marker at its insertion position.");
            await console.Feed("\u001b"); var escaped = await Next(); Check(escaped.Text == "" && lines[^1] == "/cancel", "Escape did not clear and route cancellation.");
            await console.Feed("\u0004"); Check(await input.WaitAsync(Bound) == TerminalInputExit.Quit, "Empty Ctrl-D did not quit.");
            await view.DisposeAsync();
            Check(console.Frames.All(frame => !frame.Contains('\u6587') && !frame.Contains('\0') && !frame.Contains('\uD83D')), "Draft Unicode or NUL bypassed actual ASCII projection.");
            Check(console.Frames[^1].Contains("\u001b[?2004l\u001b[?1049l", StringComparison.Ordinal), "Actual terminal modes were not restored.");
            console.AssertJoined();
        }
        finally { cancel.Cancel(); console.Complete(); try { await input; } catch (OperationCanceledException) { } await view.DisposeAsync(); console.AssertJoined(); }
        async Task<TerminalDraftSnapshot> Next() => await snapshots.Reader.ReadAsync(cancel.Token).AsTask().WaitAsync(Bound);
    }
    private static async Task DurableSubmission(string host, string cli)
    {
        var files = await TerminalSessionCommandTests.WorkflowFiles.Create(host, cli, plugin: false);
        var payload = new string('q', 1001) + "\n\u6587\U0001f642"; var expanded = "before " + payload;
        var events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "combined", content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 0, item_id = "combined", delta = "combined-answer" },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "combined", content = new[] { new { type = "output_text", text = "combined-answer" } } } },
            new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } }
        };
        await File.WriteAllTextAsync(Path.Combine(files.Root, "original-script.json"), await File.ReadAllTextAsync(files.Script), new UTF8Encoding(false, true));
        await File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[] { new { requiredInputTexts = new[] { expanded }, events } } }), new UTF8Encoding(false, true));
        var console = new ConsoleFixture(); using var errors = new StringWriter(); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var work = TerminalSessionCommand.RunAsync(files.Args(), console, console, errors, cancel.Token);
        try
        {
            await console.History.Task.WaitAsync(Bound);
            await console.Feed("\u001b[200~" + payload + "\u001b[201~"); await console.Marker.Task.WaitAsync(Bound);
            await console.Feed("\u001b[Hbefore \r"); await console.Answer.Task.WaitAsync(Bound);
            await console.Feed("/quit\r"); Check(await work.WaitAsync(Bound) == 0 && errors.ToString() == "", "Combined terminal command failed: " + errors);
            var log = await TerminalSessionCommandTests.Complete(files.Session);
            var context = TerminalSessionCommandTests.Context(log);
            Check(context.Messages.Any(message => message.Role == "user" && UserText(message.WireBody.Value) == expanded), "Durable user message stored marker text or lost caret insertion.");
            Check(context.Messages.Any(message => message.Role == "assistant" && TerminalSessionCommandTests.TextOf(message.WireBody.Value) == "combined-answer"), "Provider startup did not complete actual combined turn.");
            await files.Retain(new { scenario = "combined-editor-paste-provider-startup", characters = expanded.Length, checkpoint = log.ValidatedPrefixByteLength, joined = true });
            console.AssertJoined();
        }
        finally { cancel.Cancel(); console.Complete(); await work; console.AssertJoined(); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string UserText(JsonElement message)
    {
        var content = message.GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString()! :
            string.Concat(content.EnumerateArray().Where(block => block.TryGetProperty("type", out var type) && type.GetString() == "text").Select(block => block.GetProperty("text").GetString()));
    }
    private sealed class ConsoleFixture : IConsoleTerminal, ITerminalViewportSource
    {
        private readonly Channel<string> input = Channel.CreateBounded<string>(8); private readonly object gate = new();
        private int reads, writes; private long readStarted, readSettled, writeStarted, writeSettled;
        internal TerminalViewport Viewport = new(96, 12, 0, 0, 96, 12); internal readonly List<string> Frames = [];
        internal readonly TaskCompletionSource History = new(TaskCreationOptions.RunContinuationsAsynchronously), Marker = new(TaskCreationOptions.RunContinuationsAsynchronously), Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool disposed;
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        public TerminalLeaseSnapshot Snapshot { get { lock (gate) return new(State, State, null, false, false, reads, writes, readStarted, readSettled, writeStarted, writeSettled); } }
        public TerminalViewport ReadViewport() => Viewport;
        internal ValueTask Feed(string text) => input.Writer.WriteAsync(text);
        internal void Complete() => input.Writer.TryComplete();
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            lock (gate) { Check(reads == 0, "Actual adapter admitted concurrent reads."); reads++; readStarted++; }
            try { if (!await input.Reader.WaitToReadAsync(token)) return 0; Check(input.Reader.TryRead(out var text) && text.Length <= destination.Length, "Console chunk exceeded bound."); var value = text ?? throw new IOException("Console fixture returned no data."); value.AsMemory().CopyTo(destination); return value.Length; }
            finally { lock (gate) { reads--; readSettled++; } }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); lock (gate)
            {
                Check(writes == 0, "Actual view admitted concurrent writes."); writes++; writeStarted++;
                try { var value = frame.ToString(); Frames.Add(value); if (value.Contains("[history]", StringComparison.Ordinal)) History.TrySetResult(); if (value.Contains("[paste #1", StringComparison.Ordinal)) Marker.TrySetResult(); if (value.Contains("combined-answer", StringComparison.Ordinal)) Answer.TrySetResult(); }
                finally { writes--; writeSettled++; }
            }
            return ValueTask.CompletedTask;
        }
        internal void AssertJoined() { lock (gate) Check(reads == 0 && writes == 0 && readStarted == readSettled && writeStarted == writeSettled && !disposed, "Borrowed console was disposed or physical work remained active."); }
        public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }
    }
}
