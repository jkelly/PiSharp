// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/tui/test/program-resume.test.ts,
// terminal-image.test.ts and stdin-buffer.test.ts.
using PiSharp.Tui.Pi;

internal static class Sync170TuiTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("sync170.program-resume.encodes-and-validates", () =>
        {
            var sender = new ProgramResumeSender("dummy-token-replace-me", 74877, 1795543200);
            var result = ProgramResume.FormatProgramResume(new(["nvim", "-S", "/home/user/.local/state/nvim/sessions/8f2c1d.vim"]), sender);
            Require(result == "\x1b]7577;pid=74877:time=1795543200:argv=bnZpbQ==,LVM=,L2hvbWUvdXNlci8ubG9jYWwvc3RhdGUvbnZpbS9zZXNzaW9ucy84ZjJjMWQudmlt:mac=9dbb99bfb856749a87563839d7ce9062818d78cf25909db93f309e0cd3b1909b\x1b\\");
            Require(ProgramResume.FormatProgramResumeClear(sender) == "\x1b]7577;pid=74877:time=1795543200:clear=1:mac=c30603369f98fef581a4098be889175b0c74f0641e49f8b25f4326cb38f97c93\x1b\\");
            foreach (ProgramResumeCommand command in new ProgramResumeCommand[] {
                new(["./relative"]), new(["-option"]), new(["pi", "\ud800"]), new(["pi", "control\u009b"]),
                new(["pi"], "relative"), new(["pi", new string('x', 2048)]), new(Enumerable.Repeat("pi", 65).ToArray()) })
                Require(ProgramResume.FormatProgramResume(command, sender) is null);
            return Task.CompletedTask;
        });
        yield return ("sync170.input.large-paste-with-split-terminator", () =>
        {
            var input = new StdinBuffer(UiLoop.CreateManual());
            var chunk = new string('x', 4096);
            string? pasted = null;
            string? following = null;
            input.Paste += value => pasted = value;
            input.Data += value => following = value;
            input.Process("\x1b[200~");
            for (var i = 0; i < 256; i++) input.Process(chunk);
            input.Process("\x1b[20");
            input.Process("1~z");
            Require(pasted == new string('x', chunk.Length * 256) && following == "z");
            return Task.CompletedTask;
        });
        yield return ("sync170.image.hyperlink-length-bound", () =>
        {
            Require(TerminalImage.Hyperlink("label", new string('a', 8192)).StartsWith("\x1b]8;", StringComparison.Ordinal));
            Require(TerminalImage.Hyperlink("label", new string('a', 8193)) == "label");
            return Task.CompletedTask;
        });
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Pi 1.7 TUI expectation failed.");
    }
}
