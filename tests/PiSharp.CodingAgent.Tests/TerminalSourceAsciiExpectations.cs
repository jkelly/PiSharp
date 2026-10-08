using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

// Expected rows and hardware anchors come from unchanged public Source Editor/TuiAltScreen captures.
// Source-owned styles are interpreted only as expected inert data; native writes stay independently guarded.
internal static class TerminalSourceAsciiExpectations
{
    private sealed record Expected(string[] Rows, int Row, int Column, bool Visible);
    private static readonly Dictionary<(int Columns, int Rows, string Text), Expected> Captures = Load();
    internal static bool RowsMatch(IReadOnlyList<string> screen, int columns, string text)
    {
        var expected = Get(columns, screen.Count, text); var origin = screen.Count - expected.Rows.Length;
        return origin >= 0 && expected.Rows.Select((row, at) => screen[origin + at] == row).All(value => value);
    }
    internal static bool HardwareAnchorInBounds(int columns, int rows, int row, int column, bool visible)
    {
        var expected = Get(columns, rows, "");
        return row == rows - expected.Rows.Length + expected.Row && column >= 0 && column < Math.Min(columns, 256) && visible == expected.Visible;
    }
    internal static bool PacketMatches(JsonElement packet, string text)
    {
        var geometry = packet.GetProperty("geometry"); var columns = geometry.GetProperty("Columns").GetInt32();
        var rows = geometry.GetProperty("Rows").GetInt32(); var screen = packet.GetProperty("screen").EnumerateArray().Select(row => row.GetString()!).ToArray();
        if (screen.Length != rows || !RowsMatch(screen, columns, text)) return false;
        var expected = Get(columns, rows, text); var cursor = packet.GetProperty("cursor");
        return cursor.GetProperty("Row").GetInt32() == rows - expected.Rows.Length + expected.Row &&
            cursor.GetProperty("Column").GetInt32() == expected.Column && cursor.GetProperty("Visible").GetBoolean() == expected.Visible;
    }
    private static Expected Get(int columns, int rows, string text)
    {
        if (Captures.TryGetValue((columns, rows, text), out var exact)) return exact;
        foreach (var prefix in new[] { "nonce:", "accepted:", "resume:", "?quick explain ", "?quick denied " })
            if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length == prefix.Length + 8 &&
                text[prefix.Length..].All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                Captures.TryGetValue((columns, rows, prefix + "00000000"), out var template))
                return template with { Rows = template.Rows.Select(row => row.Replace(prefix + "00000000", text, StringComparison.Ordinal)).ToArray() };
        throw new InvalidOperationException($"No pinned public Source capture for caller draft at {columns}x{rows}: {text}");
    }
    private static Dictionary<(int Columns, int Rows, string Text), Expected> Load()
    {
        var result = new Dictionary<(int Columns, int Rows, string Text), Expected>();
        foreach (var (name, sha, count) in new[] {
            ("actual-caller-source-observations-r9.json", "2bbef3206b8a10d3f8a35eb48ba8950d07d5a5ed3c51e6fbef29f182f0d62721", 28),
            ("actual-command-source-observations-r9.json", "9bb0cee698a025aa9295b4a8cf577d779c084bffcc8439183d386bd80913ca25", 14),
            ("actual-quit-source-observations-r16.json", "4566b5e1b973f75c39ed7ca76f89abbefdf4e6e79d20c080bf9aa14acd90d73f", 1) })
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "terminal-default-source", name));
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha) throw new InvalidOperationException("Pinned caller Source capture changed.");
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            if (root.GetProperty("upstreamCommit").GetString() != "d86654abb8862e201933517d6f1fce9f88dd117f" ||
                root.GetProperty("sourceModified").GetBoolean() || root.GetProperty("prohibitedEffects").GetArrayLength() != 0 ||
                root.GetProperty("observations").GetArrayLength() != count) throw new InvalidOperationException("Caller Source capture provenance differs.");
            foreach (var row in root.GetProperty("observations").EnumerateArray())
            {
                var input = row.GetProperty("input"); var observation = row.GetProperty("observation");
                if (observation.GetProperty("error").ValueKind != JsonValueKind.Null || !observation.GetProperty("terminalStopped").GetBoolean())
                    throw new InvalidOperationException("Caller Source capture did not settle.");
                var component = observation.GetProperty("componentRows").EnumerateArray().Select(value => value.GetString()!
                    .Replace("\u001b_pi:c\u0007", "", StringComparison.Ordinal).Replace("\u001b[7m", "", StringComparison.Ordinal).Replace("\u001b[0m", "", StringComparison.Ordinal)).ToArray();
                if (component.Length != 3 || component.Any(value => value.Any(c => c < ' ' || c == '\u007f')))
                    throw new InvalidOperationException("Unexpected caller Source component grammar.");
                var writes = observation.GetProperty("renderWrites"); var last = writes[writes.GetArrayLength() - 1].GetString()!;
                var anchors = Regex.Matches(last, "\u001b\\[([0-9]+);([0-9]+)H"); var modes = Regex.Matches(last, "\u001b\\[\\?25([hl])");
                if (anchors.Count == 0 || modes.Count == 0) throw new InvalidOperationException("Source hardware anchor is absent.");
                var anchor = anchors[^1];
                result.Add((input.GetProperty("columns").GetInt32(), input.GetProperty("terminalRows").GetInt32(), input.GetProperty("text").GetString()!),
                    new(component, int.Parse(anchor.Groups[1].Value, CultureInfo.InvariantCulture) - 1,
                        int.Parse(anchor.Groups[2].Value, CultureInfo.InvariantCulture) - 1, modes[^1].Groups[1].Value == "h"));
            }
        }
        return result;
    }
}
