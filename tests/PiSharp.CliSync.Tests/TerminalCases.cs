using System.Text;
using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

// Upstream: packages/tui/src/keybindings.ts, packages/tui/src/program-status.ts, packages/tui/src/terminal.ts and
// packages/coding-agent/src/modes/interactive/program-status-reporter.ts at abe508e1b89912adde45528136c3221eb69acdd7.
internal static partial class Program
{
    private const string Esc = "\u001b";

    private static void Keybindings()
    {
        string[] Keys(string id) => [.. TerminalKeybindingDefinitions.Tui.Single(pair => pair.Key == id).Value.DefaultKeys.Keys];
        Names(["home", "ctrl+a"], Keys("tui.editor.cursorLineStart"), "line start");
        Names(["end", "ctrl+e"], Keys("tui.editor.cursorLineEnd"), "line end");
        Names(["ctrl+home"], Keys("tui.altScreen.top"), "transcript top");
        Names(["ctrl+end"], Keys("tui.altScreen.bottom"), "transcript bottom");
        Check(TerminalKeybindingDefinitions.Tui.Single(pair => pair.Key == "tui.altScreen.top").Value.DefaultKeys.IsScalar, "top default is a scalar like upstream.");
        foreach (var platform in new[] { "win32", "linux", "darwin" })
        {
            var registry = new TerminalKeybindings(TerminalAgentKeybindingDefinitions.Create(platform), (data, key) => data == key);
            Names(["home", "ctrl+a"], registry.GetKeys("tui.editor.cursorLineStart"), platform + " line start");
            Names(["ctrl+end"], registry.GetKeys("tui.altScreen.bottom"), platform + " transcript bottom");
            Check(!registry.GetConflicts().Any(conflict => conflict.Key is "home" or "end" or "ctrl+home" or "ctrl+end"), platform + " Home/End conflict.");
        }
        var raw = new TerminalKeybindings((data, key) => TerminalInputDecoder.MatchesRawKey(data, key));
        Check(raw.Matches(Esc + "[H", "tui.editor.cursorLineStart") && !raw.Matches(Esc + "[H", "tui.altScreen.top"), "Home moved off the editor.");
        Check(raw.Matches(Esc + "[1;5H", "tui.altScreen.top") && !raw.Matches(Esc + "[1;5H", "tui.editor.cursorLineStart"), "Ctrl+Home still moves the editor.");
        Check(raw.Matches(Esc + "[1;5F", "tui.altScreen.bottom") && !raw.Matches(Esc + "[1;5F", "tui.editor.cursorLineEnd"), "Ctrl+End still moves the editor.");
    }

    private static string Osc(string body) => Esc + "]7501;" + body + Esc + "\\";
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static void FormatStatus()
    {
        Equal(Osc("state=working:app=pi:msg=bXkgc2Vzc2lvbg=="), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Working, "pi", Message: "my session")), "working");
        Equal(Osc("state=blocked:app=pi:kind=permission:msg=QWxsb3c/"),
            TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Blocked, "pi", TerminalProgramBlockedKind.Permission, "Allow?")), "blocked");
        Equal(Osc("state=blocked:kind=auth"), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Blocked, Kind: TerminalProgramBlockedKind.Auth)), "auth");
        Equal(Osc("state=done"), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Done, Kind: TerminalProgramBlockedKind.Question)), "kind only when blocked");
        Equal(Osc("state=idle"), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Idle, "has space")), "invalid app omitted");
        Equal(Osc("state=idle"), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Idle, new string('a', 33))), "long app omitted");
        Equal(Osc("state=idle:app=" + new string('a', 32)), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Idle, new string('a', 32))), "32-char app");
        Equal(Osc("state=idle:app=A.b+c_d-1"), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Idle, "A.b+c_d-1")), "app alphabet");
        Equal(Osc("state=error:msg=bGluZTEgbGluZTIgeA=="),
            TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Error, Message: " line1\nline2\t" + (char)0x85 + "x" + (char)0x7f + " ")), "control runs become one space, then trim");
        Equal(Osc("state=error"), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Error, Message: "\n \t" + (char)0x2028)), "blank message omitted");
        Equal(Osc("state=clear"), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Clear)), "clear");
        var two = new string('a', 2047) + (char)0xe9; // 2049 UTF-8 bytes
        Equal(Osc("state=done:msg=" + B64(new string('a', 2047))), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Done, Message: two)), "2-byte cut");
        var three = new string('a', 2046) + (char)0x20ac;
        Equal(Osc("state=done:msg=" + B64(new string('a', 2046))), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Done, Message: three)), "3-byte cut");
        var four = new string('a', 2045) + char.ConvertFromUtf32(0x1f600);
        Equal(Osc("state=done:msg=" + B64(new string('a', 2045))), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Done, Message: four)), "surrogate pair kept whole");
        var exact = new string('a', 2044) + char.ConvertFromUtf32(0x1f600);
        Equal(Osc("state=done:msg=" + B64(exact)), TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Done, Message: exact)), "2048 bytes kept");
        Check(B64(exact).Length <= 2732, "Encoded message exceeds the spec limit.");
    }

    private static void Replies()
    {
        Equal(Esc + "]7501;?" + Esc + "\\", TerminalProgramStatusProtocol.Query, "query");
        Check(TerminalProgramStatusProtocol.IsReply(Esc + "]7501;?" + Esc + "\\") && TerminalProgramStatusProtocol.IsReply(Esc + "]7501;?\u0007") &&
            TerminalProgramStatusProtocol.IsReply(Esc + "]7501;?v=1:x=y" + Esc + "\\"), "Replies were not recognized.");
        Check(!TerminalProgramStatusProtocol.IsReply(Esc + "]7501;state=idle" + Esc + "\\") && !TerminalProgramStatusProtocol.IsReply(Esc + "]7501;?" + Esc + "\\x") &&
            !TerminalProgramStatusProtocol.IsReply(Esc + "]7501;?a" + Esc + "b" + Esc + "\\") && !TerminalProgramStatusProtocol.IsReply(Esc + "]7501;?\u0007\n") &&
            !TerminalProgramStatusProtocol.IsReply(Esc + "]7502;?" + Esc + "\\"), "Non-replies were recognized.");
    }

    private static void Channel()
    {
        var working = new TerminalProgramStatus(TerminalProgramState.Working, "pi");
        var channel = new TerminalProgramStatusChannel();
        Equal("", channel.Set(working), "unsupported before start");
        Equal(Osc("state=working:app=pi"), channel.Start("1"), "PI_PROGRAM_STATUS=1 re-sends the latest status");
        Equal(Osc("state=done:app=pi"), channel.Set(working with { State = TerminalProgramState.Done }), "supported report");
        Equal(Osc("state=clear"), channel.Stop(), "stop clears the shown status");
        Equal("", channel.Set(working), "stopped"); Check(!channel.IsSupported, "Stop kept support.");
        Equal("", channel.Start("0"), "PI_PROGRAM_STATUS=0"); Equal("", channel.Start(null), "no negotiation without override");
        Equal(Osc("state=working:app=pi"), channel.Start("1"), "restart reports again");
        Equal(Osc("state=clear"), channel.Set(new(TerminalProgramState.Clear)), "explicit clear is written");
        Equal("", channel.Stop(), "nothing to clear");
        // Negotiating terminals: the query goes before the DA1 query and support follows the reply.
        var negotiated = new TerminalProgramStatusChannel(); negotiated.Set(working);
        Equal(TerminalProgramStatusProtocol.Query, negotiated.StartNegotiation(null), "query sent");
        Equal("", negotiated.ReportLatest(), "no report before the reply");
        Check(!negotiated.TryObserveReply(Esc + "[?62c", out _), "DA1 consumed as status reply.");
        Check(negotiated.TryObserveReply(Esc + "]7501;?" + Esc + "\\", out var confirmed) && confirmed == Osc("state=working:app=pi") && negotiated.IsSupported,
            "Reply did not confirm support.");
        Check(negotiated.TryObserveReply(Esc + "]7501;?" + Esc + "\\", out var duplicate) && duplicate == "", "Unsolicited reply re-sent.");
        var late = new TerminalProgramStatusChannel();
        Equal(TerminalProgramStatusProtocol.Query, late.StartNegotiation("2"), "other override values still query");
        late.ObserveDeviceAttributes(1); Check(late.TryObserveReply(Esc + "]7501;?\u0007", out var stillPending) && stillPending == "" && late.IsSupported,
            "An earlier owed DA1 ended the query.");
        var unsupported = new TerminalProgramStatusChannel(); unsupported.StartNegotiation(null); unsupported.ObserveDeviceAttributes(0);
        Check(unsupported.TryObserveReply(Esc + "]7501;?" + Esc + "\\", out var ignored) && ignored == "" && !unsupported.IsSupported,
            "A reply after the last DA1 confirmed support.");
        var forced = new TerminalProgramStatusChannel(); forced.Set(working);
        Equal("", forced.StartNegotiation("1"), "override skips the query"); Equal(Osc("state=working:app=pi"), forced.ReportLatest(), "override reports at once");
        Equal("", new TerminalProgramStatusChannel().StartNegotiation("0"), "override 0 skips the query");
    }

    private static JsonElement Event(string json) => JsonDocument.Parse(json).RootElement;
    private static string? Sent(TerminalProgramStatus? status) => status is null ? null : TerminalProgramStatusProtocol.Format(status);

    private static void Reporter()
    {
        var reporter = new ProgramStatusReporter();
        Equal(Osc("state=idle:app=pi"), Sent(reporter.Report()), "initial report");
        Equal(null, Sent(reporter.Report()), "duplicate suppressed");
        Equal(null, Sent(reporter.HandleEvent(Event("""{"type":"response","command":"get_state","success":true,"data":{"sessionName":"demo"}}"""))), "idle has no name");
        Equal(Osc("state=working:app=pi:msg=" + B64("demo")), Sent(reporter.HandleEvent(Event("""{"type":"agent_start"}"""))), "working");
        Equal(null, Sent(reporter.HandleEvent(Event("""{"type":"message_end","message":{"role":"assistant","stopReason":"error","errorMessage":"Boom\r\nstack"}}"""))), "outcome is deferred");
        Equal(null, Sent(reporter.HandleEvent(Event("""{"type":"message_end","message":{"role":"user"}}"""))), "user message");
        Equal(null, Sent(reporter.HandleEvent(Event("""{"type":"message_update"}"""))), "other events");
        Equal(Osc("state=working:app=pi:msg=" + B64("Compacting context")), Sent(reporter.HandleEvent(Event("""{"type":"compaction_start","reason":"threshold"}"""))), "compacting");
        Equal(Osc("state=working:app=pi:msg=" + B64("demo")), Sent(reporter.HandleEvent(Event("""{"type":"compaction_end","reason":"threshold"}"""))), "compaction done");
        Equal(Osc("state=error:app=pi:msg=" + B64("Boom")), Sent(reporter.HandleEvent(Event("""{"type":"agent_settled","aborted":false}"""))), "error outcome, first line");
        Equal(Osc("state=working:app=pi:msg=" + B64("demo")), Sent(reporter.HandleEvent(Event("""{"type":"agent_start"}"""))), "retry run");
        reporter.HandleEvent(Event("""{"type":"message_end","message":{"role":"assistant","stopReason":"error","errorMessage":"Bad"}}"""));
        reporter.HandleEvent(Event("""{"type":"message_end","message":{"role":"assistant","stopReason":"stop"}}"""));
        Equal(Osc("state=done:app=pi:msg=" + B64("demo")), Sent(reporter.HandleEvent(Event("""{"type":"agent_settled"}"""))), "successful retry replaces the error");
        Equal(Osc("state=done:app=pi:msg=" + B64("renamed")), Sent(reporter.HandleEvent(Event("""{"type":"session_info_changed","name":"renamed"}"""))), "name change");
        Equal(Osc("state=blocked:app=pi:kind=question:msg=" + B64("Pick one")), Sent(reporter.SetBlocked("a", new(TerminalProgramBlockedKind.Question, "Pick one"))), "dialog");
        Equal(Osc("state=blocked:app=pi:kind=permission:msg=" + B64("Allow?")), Sent(reporter.SetBlocked("b", new(TerminalProgramBlockedKind.Permission, "Allow?"))), "latest dialog");
        Equal(Osc("state=blocked:app=pi:kind=question:msg=" + B64("Pick one")), Sent(reporter.SetBlocked("b", null)), "earlier dialog again");
        Equal(null, Sent(reporter.HandleEvent(Event("""{"type":"agent_start"}"""))), "blocked wins over working");
        Equal(Osc("state=working:app=pi:msg=" + B64("renamed")), Sent(reporter.SetBlocked("a", null)), "dialogs closed");
        Equal(Osc("state=idle:app=pi"), Sent(reporter.HandleEvent(Event("""{"type":"agent_settled","aborted":true}"""))), "aborted run");
        Equal(Osc("state=error:app=pi:msg=" + B64("Compaction failed")),
            Sent(reporter.HandleEvent(Event("""{"type":"compaction_end","reason":"manual","errorMessage":"Compaction failed\nmore"}"""))), "manual compaction error");
        Equal(Osc("state=done:app=pi:msg=" + B64("renamed")), Sent(reporter.HandleEvent(Event("""{"type":"compaction_end","reason":"manual"}"""))), "manual compaction");
        Equal(Osc("state=idle:app=pi"), Sent(reporter.HandleEvent(Event("""{"type":"compaction_end","reason":"manual","aborted":true}"""))), "aborted compaction");
        Equal(null, Sent(reporter.HandleEvent(Event("""{"type":"compaction_end","reason":"threshold","errorMessage":"x"}"""))), "automatic compaction while idle");
        reporter.HandleEvent(Event("""{"type":"agent_start"}"""));
        reporter.HandleEvent(Event("""{"type":"compaction_end","reason":"overflow","aborted":true}"""));
        Equal(Osc("state=idle:app=pi"), Sent(reporter.HandleEvent(Event("""{"type":"agent_settled"}"""))), "failed recovery compaction");
        reporter.HandleEvent(Event("""{"type":"agent_start"}"""));
        reporter.HandleEvent(Event("""{"type":"compaction_end","reason":"overflow","errorMessage":""}"""));
        reporter.HandleEvent(Event("""{"type":"message_end","message":{"role":"assistant","stopReason":"error"}}"""));
        Equal(Osc("state=error:app=pi:msg=" + B64("Error")), Sent(reporter.HandleEvent(Event("""{"type":"agent_settled"}"""))), "missing error text");
        reporter.HandleEvent(Event("""{"type":"agent_start"}"""));
        Equal(Osc("state=idle:app=pi"), Sent(reporter.Reset()), "reset");
        Check(ProgramStatusReporter.Observes(Event("""{"type":"agent_settled"}""")) && !ProgramStatusReporter.Observes(Event("""{"type":"message_update"}""")),
            "Observed event filter.");
    }

    private sealed class FakeConsole : IConsoleTerminal
    {
        private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
        internal readonly List<string> Writes = [];
        public TerminalLeaseSnapshot Snapshot => new(State, State, null, false, false, 0, 0, 0, 0, 0, 0);
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) { lock (Writes) Writes.Add(frame.ToString()); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FixedViewport : ITerminalViewportSource
    {
        public TerminalViewport ReadViewport() => new(80, 24, 0, 0, 80, 24);
    }

    private static async Task TerminalView()
    {
        var console = new FakeConsole(); var reporter = new ProgramStatusReporter();
        var view = new TerminalSessionView(console, new FixedViewport()) { ProgramStatus = new(), ProgramStatusOverride = "1" };
        await view.ReportProgramStatusAsync(reporter.Report, default);
        Check(console.Writes.Count == 0, "Reported before the terminal started.");
        await view.StartAsync(default);
        Names([Esc + "[?1049h" + Esc + "[?2004h"], console.Writes, "start writes");
        await view.ReportProgramStatusAsync(reporter.Report, default);
        await view.ReportProgramStatusAsync(reporter.Report, default);
        await view.ReportProgramStatusAsync(() => reporter.HandleEvent(Event("""{"type":"agent_start"}""")), default);
        await view.DisposeAsync();
        Equal(Osc("state=idle:app=pi"), console.Writes[1], "initial status");
        Equal(Osc("state=working:app=pi"), console.Writes[2], "working status without a session name");
        Equal(Osc("state=clear") + Esc + "[?2004l" + Esc + "[?1049l", console.Writes[^1], "stop clears before leaving the screen");
        Equal(1, console.Writes.Count(write => write.Contains("state=idle", StringComparison.Ordinal)), "duplicate status written");
        var plain = new FakeConsole();
        var disabled = new TerminalSessionView(plain, new FixedViewport()) { ProgramStatus = new(), ProgramStatusOverride = "0" };
        await disabled.StartAsync(default);
        await disabled.ReportProgramStatusAsync(new ProgramStatusReporter().Report, default);
        await disabled.DisposeAsync();
        Check(!plain.Writes.Any(write => write.Contains("]7501;", StringComparison.Ordinal)), "PI_PROGRAM_STATUS=0 wrote program status.");
        Equal(Esc + "[?2004l" + Esc + "[?1049l", plain.Writes[^1], "unchanged stop sequence");
    }
}
