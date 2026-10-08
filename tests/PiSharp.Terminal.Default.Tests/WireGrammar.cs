using System.Text.RegularExpressions;

// Complete strings here are joined actual writes. The grammar owns every command, including seams.
internal static class WireGrammar
{
    internal static void Check(string wire, int columns, int rows)
    {
        for (var at = 0; at < wire.Length; at++)
        {
            var c = wire[at];
            if (c != '\u001b') { TerminalDefaultQualificationTests.Check(c >= 32 && c is not (>= '\u007f' and <= '\u009f'), "User control on wire"); continue; }
            var end = wire.IndexOfAny(['m', 'J', 'K', 'H', 'h', 'l'], at + 1); TerminalDefaultQualificationTests.Check(end >= 0, "Incomplete owned packet");
            var packet = wire[at..(end + 1)]; var move = Regex.Match(packet, "^\\x1b\\[([1-9][0-9]*);([1-9][0-9]*)H$");
            TerminalDefaultQualificationTests.Check(packet is "\u001b[0m" or "\u001b[7m" or "\u001b[2J" or "\u001b[2K" or "\u001b[?25h" or "\u001b[?25l" or "\u001b[?1049h" or "\u001b[?1049l" or "\u001b[?2004h" or "\u001b[?2004l" ||
                move.Success && int.Parse(move.Groups[1].Value) <= rows && int.Parse(move.Groups[2].Value) <= columns, "Unowned or unbounded terminal packet"); at = end;
        }
    }
}
