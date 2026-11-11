using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace PiSharp.Tools.Files;

public sealed record DiffFormatterOptions(int ContextLines = 4, int MaximumLines = 8192, long MaximumWork = 4_000_000,
    int MaximumTraceCells = 1_000_000, int MaximumOutputCharacters = 131_072);
public sealed record FormattedEditDiff(string Diff, string Patch, int? FirstChangedLine);
public sealed record FormattedEditDisplay(string Diff, int? FirstChangedLine);

/// <summary>Bounded native Myers line diff and source-shaped display/unified output. No jsdiff byte-parity claim.</summary>
public static class DiffFormatter
{
    private sealed record Line(char Kind, string Text);
    private sealed record Part(char Kind, ImmutableArray<string> Lines);
    public static FormattedEditDiff Format(string path, string oldContent, string newContent, DiffFormatterOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(path); ArgumentNullException.ThrowIfNull(oldContent); ArgumentNullException.ThrowIfNull(newContent);
        options ??= new();
        ValidateOptions(options);
        var oldLines = EditPlan.SplitLines(oldContent); var newLines = EditPlan.SplitLines(newContent);
        if (oldLines.Length > options.MaximumLines || newLines.Length > options.MaximumLines) throw EditPlan.Limit();
        var lines = Compute(oldLines, newLines, options, token);
        var display = Display(lines, oldContent, newContent, options, token);
        var patch = Unified(path, lines, options.ContextLines);
        if (display.Diff.Length > options.MaximumOutputCharacters || patch.Length > options.MaximumOutputCharacters) throw EditPlan.Limit();
        return new(display.Diff, patch, display.FirstChangedLine);
    }

    public static FormattedEditDisplay FormatDisplay(string oldContent, string newContent, DiffFormatterOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(oldContent); ArgumentNullException.ThrowIfNull(newContent);
        options ??= new();
        ValidateOptions(options);
        var oldLines = EditPlan.SplitLines(oldContent); var newLines = EditPlan.SplitLines(newContent);
        if (oldLines.Length > options.MaximumLines || newLines.Length > options.MaximumLines) throw EditPlan.Limit();
        var result = Display(Compute(oldLines, newLines, options, token), oldContent, newContent, options, token);
        if (result.Diff.Length > options.MaximumOutputCharacters) throw EditPlan.Limit();
        return result;
    }

    /// <summary>Diff only bounded windows around the admitted replacements, before a write is attempted.</summary>
    public static FormattedEditDiff FormatEdit(string path, EditPlan plan, DiffFormatterOptions? options = null, CancellationToken token = default, bool includePatch = true)
    {
        options ??= new(); ValidateOptions(options);
        var before = plan.BaseContent; var after = plan.NewContent;
        var width = Math.Max(CountLines(before), CountLines(after)).ToString(CultureInfo.InvariantCulture).Length;
        var windows = new List<(int Start, int End, int NewStart, int NewEnd, int OldLine, int NewLine)>();
        var offsetDelta = 0; var lineDelta = 0; var cursor = 0; var cursorLine = 0;
        foreach (var replacement in plan.Replacements)
        {
            token.ThrowIfCancellationRequested();
            var matchEnd = replacement.MatchIndex + replacement.MatchLength;
            var start = LineStart(before, replacement.MatchIndex);
            for (var i = 0; i < 32 && start > 0; i++) start = LineStart(before, start - 1);
            var end = matchEnd;
            for (var i = 0; i <= 32 && end < before.Length; i++)
            {
                var lf = before.IndexOf('\n', i == 0 ? Math.Max(matchEnd - 1, replacement.MatchIndex) : end);
                end = lf < 0 ? before.Length : lf + 1;
            }
            var delta = replacement.NewText.Length - replacement.MatchLength;
            var linesDelta = CountFeeds(replacement.NewText, 0, replacement.NewText.Length) - CountFeeds(before, replacement.MatchIndex, matchEnd);
            if (windows.Count > 0 && start <= windows[^1].End)
            {
                var previous = windows[^1]; end = Math.Max(end, previous.End);
                previous.End = end; previous.NewEnd = end + offsetDelta + delta; windows[^1] = previous;
            }
            else
            {
                cursorLine += CountFeeds(before, cursor, start); cursor = start;
                windows.Add((start, end, start + offsetDelta, end + offsetDelta + delta, cursorLine, cursorLine + lineDelta));
            }
            offsetDelta += delta; lineDelta += linesDelta;
        }
        var display = new List<string>(); var patch = new StringBuilder(); int? first = null;
        var skip = " " + new string(' ', width) + " ...";
        foreach (var window in windows)
        {
            token.ThrowIfCancellationRequested();
            var oldText = before[window.Start..window.End]; var newText = after[window.NewStart..window.NewEnd];
            var oldLines = EditPlan.SplitLines(oldText); var newLines = EditPlan.SplitLines(newText);
            if (oldLines.Length > options.MaximumLines || newLines.Length > options.MaximumLines) throw EditPlan.Limit();
            var lines = Compute(oldLines, newLines, options, token);
            var shown = Display(lines, oldText, newText, options, token, window.OldLine + 1, window.NewLine + 1, width);
            first ??= shown.FirstChangedLine;
            var output = shown.Diff.Split('\n').ToList();
            if (display.Count > 0 && display[^1] == skip && output[0] == skip) output.RemoveAt(0);
            display.AddRange(output);
            if (includePatch)
            {
                var unified = Unified(path, lines, options.ContextLines, window.OldLine, window.NewLine);
                if (patch.Length > 0) unified = unified[(unified.IndexOf('\n', unified.IndexOf('\n') + 1) + 1)..];
                patch.Append(unified);
            }
            if (patch.Length > options.MaximumOutputCharacters || display.Sum(line => (long)line.Length + 1) > options.MaximumOutputCharacters + 1L) throw EditPlan.Limit();
        }
        return new(string.Join('\n', display), patch.ToString(), first);
    }
    private static int LineStart(string text, int offset) => offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
    private static int CountLines(string text) => CountFeeds(text, 0, text.Length) + 1;
    private static int CountFeeds(string text, int start, int end)
    {
        var count = 0;
        for (var i = start; i < end; i++) if (text[i] == '\n') count++;
        return count;
    }

    private static FormattedEditDisplay Display(ImmutableArray<Line> lines, string oldContent, string newContent, DiffFormatterOptions options, CancellationToken token, int oldStart = 1, int newStart = 1, int? numberWidth = null)
    {
        var parts = new List<Part>();
        for (var start = 0; start < lines.Length;)
        {
            var end = start + 1; while (end < lines.Length && lines[end].Kind == lines[start].Kind) end++;
            parts.Add(new(lines[start].Kind, lines.Skip(start).Take(end - start).Select(value => value.Text).ToImmutableArray())); start = end;
        }
        var width = numberWidth ?? Math.Max(oldContent.Split('\n').Length, newContent.Split('\n').Length).ToString(CultureInfo.InvariantCulture).Length;
        var display = new List<string>(); var oldNumber = oldStart; var newNumber = newStart; int? firstChanged = null;
        for (var index = 0; index < parts.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var part = parts[index];
            if (part.Kind != ' ')
            {
                firstChanged ??= newNumber;
                foreach (var line in part.Lines)
                {
                    var number = part.Kind == '+' ? newNumber++ : oldNumber++;
                    display.Add(part.Kind + Number(number).PadLeft(width) + " " + Body(line));
                }
                continue;
            }
            var leading = index > 0 && parts[index - 1].Kind != ' '; var trailing = index + 1 < parts.Count && parts[index + 1].Kind != ' ';
            var count = part.Lines.Length;
            var showAll = leading && trailing && count <= options.ContextLines * 2;
            for (var line = 0; line < count;)
            {
                var show = showAll || (leading && line < options.ContextLines) || (trailing && line >= count - options.ContextLines);
                if (show) { display.Add(" " + Number(oldNumber++).PadLeft(width) + " " + Body(part.Lines[line++])); newNumber++; }
                else
                {
                    var end = trailing ? Math.Max(line + 1, count - options.ContextLines) : count;
                    if (leading || trailing) display.Add(" " + new string(' ', width) + " ...");
                    oldNumber += end - line; newNumber += end - line; line = end;
                }
            }
        }
        return new(string.Join('\n', display), firstChanged);
    }

    private static ImmutableArray<Line> Compute(string[] before, string[] after, DiffFormatterOptions options, CancellationToken token)
    {
        // Large disjoint changed blocks need no Myers trace: every old line is removed and every new line added.
        // Trim equal edges first, then prove disjointness rather than exhausting a quadratic trace for bulk edits.
        if (before.Length + after.Length > 256)
        {
            var prefix = 0;
            while (prefix < Math.Min(before.Length, after.Length) && before[prefix] == after[prefix]) prefix++;
            var oldEnd = before.Length; var newEnd = after.Length;
            while (oldEnd > prefix && newEnd > prefix && before[oldEnd - 1] == after[newEnd - 1]) { oldEnd--; newEnd--; }
            if ((long)before.Length + after.Length > options.MaximumWork) throw EditPlan.Limit();
            var oldSet = new HashSet<string>(before[prefix..oldEnd], StringComparer.Ordinal);
            var disjoint = true;
            for (var i = prefix; i < newEnd; i++)
            {
                token.ThrowIfCancellationRequested();
                if (oldSet.Contains(after[i])) { disjoint = false; break; }
            }
            if (disjoint)
            {
                var direct = ImmutableArray.CreateBuilder<Line>();
                for (var i = 0; i < prefix; i++) direct.Add(new(' ', before[i]));
                for (var i = prefix; i < oldEnd; i++) direct.Add(new('-', before[i]));
                for (var i = prefix; i < newEnd; i++) direct.Add(new('+', after[i]));
                for (var i = oldEnd; i < before.Length; i++) direct.Add(new(' ', before[i]));
                token.ThrowIfCancellationRequested();
                return direct.ToImmutable();
            }
        }
        var trace = new List<int[]>(); long work = 0; var distance = 0;
        for (; distance <= before.Length + after.Length; distance++)
        {
            token.ThrowIfCancellationRequested();
            if ((long)(distance + 1) * (distance + 1) > options.MaximumTraceCells) throw EditPlan.Limit();
            var current = new int[2 * distance + 1];
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                if (++work > options.MaximumWork) throw EditPlan.Limit();
                var x = distance == 0 ? 0 : diagonal == -distance || (diagonal != distance && Get(trace[^1], distance - 1, diagonal - 1) < Get(trace[^1], distance - 1, diagonal + 1))
                    ? Get(trace[^1], distance - 1, diagonal + 1) : Get(trace[^1], distance - 1, diagonal - 1) + 1;
                var y = x - diagonal;
                while (x < before.Length && y < after.Length)
                {
                    if (++work > options.MaximumWork) throw EditPlan.Limit();
                    if (before[x] != after[y]) break;
                    x++; y++;
                }
                current[diagonal + distance] = x;
                if (x >= before.Length && y >= after.Length)
                {
                    trace.Add(current); return Reconstruct(before, after, trace, distance);
                }
            }
            trace.Add(current);
        }
        throw EditPlan.Limit();
    }
    private static int Get(int[] values, int distance, int diagonal) => values[diagonal + distance];
    private static ImmutableArray<Line> Reconstruct(string[] before, string[] after, List<int[]> trace, int distance)
    {
        var reversed = new List<Line>(); var x = before.Length; var y = after.Length;
        for (var step = distance; step > 0; step--)
        {
            var diagonal = x - y;
            var previousDiagonal = diagonal == -step || (diagonal != step && Get(trace[step - 1], step - 1, diagonal - 1) < Get(trace[step - 1], step - 1, diagonal + 1))
                ? diagonal + 1 : diagonal - 1;
            var previousX = Get(trace[step - 1], step - 1, previousDiagonal); var previousY = previousX - previousDiagonal;
            while (x > previousX && y > previousY) { reversed.Add(new(' ', before[--x])); y--; }
            if (x == previousX) reversed.Add(new('+', after[--y])); else reversed.Add(new('-', before[--x]));
        }
        while (x > 0 && y > 0) { reversed.Add(new(' ', before[--x])); y--; }
        reversed.Reverse();
        // Emit removals before additions within each contiguous changed block, as source display output does.
        var ordered = ImmutableArray.CreateBuilder<Line>();
        for (var start = 0; start < reversed.Count;)
        {
            if (reversed[start].Kind == ' ') { ordered.Add(reversed[start++]); continue; }
            var end = start; while (end < reversed.Count && reversed[end].Kind != ' ') end++;
            for (var index = start; index < end; index++) if (reversed[index].Kind == '-') ordered.Add(reversed[index]);
            for (var index = start; index < end; index++) if (reversed[index].Kind == '+') ordered.Add(reversed[index]);
            start = end;
        }
        return ordered.ToImmutable();
    }
    private static string Unified(string path, ImmutableArray<Line> lines, int context, int oldOffset = 0, int newOffset = 0)
    {
        // Quoted labels avoid manufacturing patch headers from filename tabs/newlines. Ordinary labels retain source spelling.
        var label = path.Contains('\n') || path.Contains('\r') || path.Contains('\t') ? System.Text.Json.JsonSerializer.Serialize(path) : path;
        var result = new StringBuilder("--- ").Append(label).Append("\n+++ ").Append(label).Append('\n');
        var oldPositions = new int[lines.Length + 1]; var newPositions = new int[lines.Length + 1];
        for (var index = 0; index < lines.Length; index++)
        { oldPositions[index + 1] = oldPositions[index] + (lines[index].Kind == '+' ? 0 : 1); newPositions[index + 1] = newPositions[index] + (lines[index].Kind == '-' ? 0 : 1); }
        for (var next = 0; next < lines.Length;)
        {
            var change = next; while (change < lines.Length && lines[change].Kind == ' ') change++;
            if (change == lines.Length) break;
            var start = Math.Max(next, change - context); var end = change;
            while (true)
            {
                while (end < lines.Length && lines[end].Kind != ' ') end++;
                var following = end; while (following < lines.Length && lines[following].Kind == ' ') following++;
                if (following < lines.Length && following - end <= context * 2) { end = following; continue; }
                end = Math.Min(lines.Length, end + context); break;
            }
            var oldCount = oldPositions[end] - oldPositions[start]; var newCount = newPositions[end] - newPositions[start];
            result.Append("@@ -").Append(Number(oldOffset + oldPositions[start] + (oldCount == 0 ? 0 : 1))).Append(',').Append(Number(oldCount))
                .Append(" +").Append(Number(newOffset + newPositions[start] + (newCount == 0 ? 0 : 1))).Append(',').Append(Number(newCount)).Append(" @@\n");
            for (var index = start; index < end; index++)
            {
                result.Append(lines[index].Kind).Append(Body(lines[index].Text)).Append('\n');
                if (!lines[index].Text.EndsWith('\n')) result.Append("\\ No newline at end of file\n");
            }
            next = end;
        }
        return result.ToString();
    }
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Body(string line) => line.EndsWith('\n') ? line[..^1] : line;
    internal static void ValidateOptions(DiffFormatterOptions options)
    {
        if (options.ContextLines is < 0 or > 32 || options.MaximumLines <= 0 || options.MaximumWork <= 0 || options.MaximumTraceCells <= 0 || options.MaximumOutputCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
}
