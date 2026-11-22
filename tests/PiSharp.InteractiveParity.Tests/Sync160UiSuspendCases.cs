// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/modes/interactive/interactive-mode.ts (setSuspendSignalHandler before the UI
// starts and on a replaced renderer, #12272/#12283). Authored against the native port in the virtual terminal; not an upstream
// execution capture. Signal delivery itself is covered by PiSharp.Tui.Tests (sync160.suspend-signal.*).
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Tui.Pi;
using static Expect;

/// <summary>Sync 1.6.0: SIGTSTP from outside takes the Ctrl+Z path.</summary>
internal static class Sync160UiSuspendCases
{
    private static readonly string[] Regular = ["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular"];

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private sealed class Registration(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // #12283: the handler is set before the first start and again on a replaced renderer, and it is the Ctrl+Z path.
        yield return ("sync160.ui.suspend.sigtstp-from-outside-takes-the-ctrl-z-path-also-after-a-renderer-switch", async () =>
        {
            var listeners = new List<Action>(); var created = 0; var disposed = 0;
            var previous = TuiBase.SuspendSignalRegistrar;
            TuiBase.SuspendSignalRegistrar = listener =>
            {
                lock (listeners) { listeners.Add(listener); created++; }
                return new Registration(() => { lock (listeners) disposed++; });
            };
            try
            {
                await using var pi = new InteractiveHarness("sync160-suspend");
                var suspends = 0;
                // Ctrl+Z without the stop signal: the TUI gives the terminal back and takes it again.
                pi.Configure = context => context with { Suspend = (stop, resume) => { Interlocked.Increment(ref suspends); stop(); resume(); } };
                (int Created, int Disposed) Registrations() { lock (listeners) return (created, disposed); }
                Action Latest() { lock (listeners) return listeners[^1]; }
                async Task SignalAndExpectSuspend(string what)
                {
                    var before = Volatile.Read(ref suspends);
                    Latest()();
                    if (OperatingSystem.IsWindows())
                    {
                        // Windows has no job control: the Ctrl+Z path says so and suspends nothing.
                        await pi.WaitFor("Suspend to background is not supported on Windows");
                        Equal(before, Volatile.Read(ref suspends), what + ": nothing is suspended on Windows");
                    }
                    else await pi.WaitUntil(_ => Volatile.Read(ref suspends) == before + 1, what + ": suspended once");
                }

                pi.Start(Regular);
                await pi.WaitFor("escape interrupt");
                var atStart = Registrations();
                Equal(1, atStart.Created - atStart.Disposed, "one listener while the TUI owns the terminal");
                await SignalAndExpectSuspend("regular renderer");
                var afterSignal = Registrations();
                Equal(1, afterSignal.Created - afterSignal.Disposed, "one listener after the suspend");

                // /settings: TUI mode regular -> fullscreen replaces the renderer.
                await pi.Submit("/settings");
                await pi.WaitFor("Auto-compact");
                pi.Type("TUI mode");
                await pi.WaitUntil(text => text.Contains("TUI mode", StringComparison.Ordinal) && !text.Contains("Auto-compact", StringComparison.Ordinal), "settings filtered to TUI mode");
                pi.Type("\r");
                await pi.WaitUntil(_ => Registrations().Created > afterSignal.Created, "the new renderer registers a listener");
                var afterSwitch = Registrations();
                Equal(1, afterSwitch.Created - afterSwitch.Disposed, "the replaced renderer's listener is gone and the new one listens");
                await pi.WaitFor("TUI mode: fullscreen");
                if (!OperatingSystem.IsWindows()) await SignalAndExpectSuspend("fullscreen renderer");
                else
                {
                    // The notice of the first signal may still be in the chat, so look for a change: one more notice, or the notice
                    // in place of the "TUI mode: fullscreen" status (back-to-back status lines replace each other).
                    const string notice = "Suspend to background is not supported on Windows";
                    var before = Count(pi.Terminal.Text, notice);
                    Latest()();
                    await pi.WaitUntil(text => Count(text, notice) > before || (text.Contains(notice, StringComparison.Ordinal) && !text.Contains("TUI mode: fullscreen", StringComparison.Ordinal)),
                        "the new renderer's handler ran the Ctrl+Z path");
                    Equal(0, Volatile.Read(ref suspends), "nothing is suspended on Windows");
                }
                pi.Type("\u001b");
                await pi.WaitUntil(text => !text.Contains("Interface layout", StringComparison.Ordinal), "settings closed");
                Equal(0, await pi.Quit(), "exit code");
            }
            finally { TuiBase.SuspendSignalRegistrar = previous; }
        });
    }
}
