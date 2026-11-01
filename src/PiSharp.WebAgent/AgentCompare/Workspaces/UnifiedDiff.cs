// Decision 0011 (run W4, unit R4): reads git's unified diffs into files, hunks and lines for the diff modal, and writes the headers of the
// per-file `git diff --no-index` patches that make up a copy's diff. Diffs are made with --no-renames, so a file's old and new paths are
// the same and a rename shows as a deletion plus a new file. Paths git quotes (C-style, for '"', '\' and control characters) are unquoted;
// lines are decoded as UTF-8 (invalid bytes become U+FFFD) and lose a trailing CR. Binary files ("GIT binary patch", "Binary files … differ")
// have no lines.
using System.Globalization;
using System.Text;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>Parses and writes unified diffs.</summary>
internal static class UnifiedDiff
{
    /// <summary>The files of <paramref name="patch"/>, in the order they appear.</summary>
    public static List<WorkspaceFileDiff> Parse(byte[] patch)
    {
        var files = new List<WorkspaceFileDiff>();
        var lines = Lines(patch);
        FileBuilder? file = null;
        HunkBuilder? hunk = null;
        int oldLeft = 0, newLeft = 0;
        foreach (var line in lines)
        {
            if (hunk is not null && (oldLeft > 0 || newLeft > 0 || line.StartsWith('\\')))
            {
                // Inside a hunk the counts say how many lines are left, so a content line can never be mistaken for a header.
                switch (line.Length == 0 ? ' ' : line[0])
                {
                    case '+':
                        hunk.Lines.Add(new WorkspaceDiffLine(WorkspaceDiffLineKind.Added, line[1..]));
                        file!.Added++;
                        newLeft--;
                        continue;
                    case '-':
                        hunk.Lines.Add(new WorkspaceDiffLine(WorkspaceDiffLineKind.Removed, line[1..]));
                        file!.Removed++;
                        oldLeft--;
                        continue;
                    case '\\':
                        hunk.Lines.Add(new WorkspaceDiffLine(WorkspaceDiffLineKind.Note, line.TrimStart('\\', ' ')));
                        continue;
                    default:
                        hunk.Lines.Add(new WorkspaceDiffLine(WorkspaceDiffLineKind.Context, line.Length == 0 ? "" : line[1..]));
                        oldLeft--;
                        newLeft--;
                        continue;
                }
            }
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (file is not null) files.Add(file.Build());
                file = new FileBuilder(DiffGitPath(line["diff --git ".Length..]));
                hunk = null;
                continue;
            }
            if (file is null) continue;
            if (file.Binary) continue; // the binary patch's data lines
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                (oldLeft, newLeft) = HunkCounts(line);
                hunk = new HunkBuilder(line);
                file.Hunks.Add(hunk);
            }
            else if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                file.Change = WorkspaceFileChange.Added;
                file.NewMode = line["new file mode ".Length..].Trim();
            }
            else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                file.Change = WorkspaceFileChange.Deleted;
                file.OldMode = line["deleted file mode ".Length..].Trim();
            }
            else if (line.StartsWith("old mode ", StringComparison.Ordinal)) file.OldMode = line["old mode ".Length..].Trim();
            else if (line.StartsWith("new mode ", StringComparison.Ordinal)) file.NewMode = line["new mode ".Length..].Trim();
            else if (line == "GIT binary patch" || (line.StartsWith("Binary files ", StringComparison.Ordinal) && line.EndsWith(" differ", StringComparison.Ordinal)))
                file.Binary = true;
        }
        if (file is not null) files.Add(file.Build());
        return files;
    }

    /// <summary>Rewrites the header of one <c>git diff --no-index</c> file patch so it names <paramref name="path"/> on both sides
    /// (<c>a/&lt;path&gt;</c>, <c>b/&lt;path&gt;</c>, <c>/dev/null</c> for a missing side), as <c>git diff</c> in a repository would.</summary>
    public static byte[] Rename(byte[] patch, string path, WorkspaceFileChange change)
    {
        var text = patch.AsSpan();
        var output = new MemoryStream(patch.Length + 64);
        var inHeader = true;
        var start = 0;
        while (start < text.Length)
        {
            var end = text[start..].IndexOf((byte)'\n');
            var next = end < 0 ? text.Length : start + end + 1;
            var line = text[start..next];
            if (inHeader)
            {
                var content = Encoding.UTF8.GetString(line).TrimEnd('\n', '\r');
                string? replaced = null;
                if (content.StartsWith("diff --git ", StringComparison.Ordinal)) replaced = "diff --git " + Quote("a/" + path) + " " + Quote("b/" + path);
                else if (content.StartsWith("--- ", StringComparison.Ordinal))
                    replaced = "--- " + (change == WorkspaceFileChange.Added ? "/dev/null" : Quote("a/" + path));
                else if (content.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    replaced = "+++ " + (change == WorkspaceFileChange.Deleted ? "/dev/null" : Quote("b/" + path));
                    inHeader = false;
                }
                else if (content.StartsWith("Binary files ", StringComparison.Ordinal))
                {
                    replaced = "Binary files " + (change == WorkspaceFileChange.Added ? "/dev/null" : Quote("a/" + path)) + " and " +
                        (change == WorkspaceFileChange.Deleted ? "/dev/null" : Quote("b/" + path)) + " differ";
                    inHeader = false;
                }
                else if (content.StartsWith("@@ ", StringComparison.Ordinal) || content == "GIT binary patch") inHeader = false;
                if (replaced is not null)
                {
                    output.Write(Encoding.UTF8.GetBytes(replaced + "\n"));
                    start = next;
                    continue;
                }
            }
            output.Write(line);
            start = next;
        }
        return output.ToArray();
    }

    /// <summary><paramref name="path"/> quoted the way git quotes a path with <c>core.quotepath=false</c>: unchanged unless it holds a
    /// '"', '\' or a control character.</summary>
    public static string Quote(string path)
    {
        if (!path.Any(c => c is '"' or '\\' || char.IsControl(c))) return path;
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\a' => "\\a",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\v' => "\\v",
                '\f' => "\\f",
                '\r' => "\\r",
                _ when char.IsControl(c) => "\\" + Convert.ToString(c, 8).PadLeft(3, '0'),
                _ => c.ToString(),
            });
        }
        return quoted.Append('"').ToString();
    }

    /// <summary>Reads a C-quoted path that starts at <paramref name="start"/> (on the opening quote); returns it and the index after the
    /// closing quote.</summary>
    public static (string Path, int Next) Unquote(string text, int start)
    {
        var bytes = new List<byte>();
        var index = start + 1;
        while (index < text.Length && text[index] != '"')
        {
            var c = text[index];
            if (c == '\\' && index + 1 < text.Length)
            {
                var escape = text[index + 1];
                if (escape is >= '0' and <= '3' && index + 3 < text.Length && text[index + 2] is >= '0' and <= '7' && text[index + 3] is >= '0' and <= '7')
                {
                    // \ooo: one byte of a UTF-8 sequence (or a control character).
                    bytes.Add((byte)(((escape - '0') << 6) | ((text[index + 2] - '0') << 3) | (text[index + 3] - '0')));
                    index += 4;
                    continue;
                }
                bytes.Add(escape switch
                {
                    'a' => 7, 'b' => 8, 't' => 9, 'n' => 10, 'v' => 11, 'f' => 12, 'r' => 13,
                    _ => (byte)escape,
                });
                index += 2;
                continue;
            }
            bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            index++;
        }
        return (Encoding.UTF8.GetString([.. bytes]), Math.Min(index + 1, text.Length));
    }

    // "a/P b/P" (both sides name the same path with --no-renames), either side possibly quoted.
    private static string DiffGitPath(string rest)
    {
        if (rest.StartsWith('"')) return StripSide(Unquote(rest, 0).Path);
        var length = (rest.Length - 5) / 2;
        if (length > 0 && rest.Length == 2 * length + 5)
        {
            var path = rest.Substring(2, length);
            if (rest == "a/" + path + " b/" + path) return path;
        }
        var split = rest.IndexOf(" b/", StringComparison.Ordinal);
        return StripSide(split > 0 ? rest[..split] : rest);
    }

    private static string StripSide(string side) => side.Length > 2 && side[1] == '/' ? side[2..] : side;

    // "@@ -a,b +c,d @@ …": the old and new line counts (a missing count is 1).
    private static (int Old, int New) HunkCounts(string header)
    {
        var parts = header.Split(' ');
        return (Count(parts.ElementAtOrDefault(1)), Count(parts.ElementAtOrDefault(2)));

        static int Count(string? range)
        {
            if (range is null || range.Length < 2) return 0;
            var comma = range.IndexOf(',');
            return comma < 0 ? 1 : int.TryParse(range.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
        }
    }

    private static List<string> Lines(byte[] patch)
    {
        var lines = new List<string>();
        var start = 0;
        while (start < patch.Length)
        {
            var end = Array.IndexOf(patch, (byte)'\n', start);
            var stop = end < 0 ? patch.Length : end;
            var length = stop - start;
            if (length > 0 && patch[stop - 1] == '\r') length--;
            lines.Add(Encoding.UTF8.GetString(patch, start, length));
            start = stop + 1;
        }
        return lines;
    }

    private sealed class HunkBuilder(string header)
    {
        public string Header { get; } = header;

        public List<WorkspaceDiffLine> Lines { get; } = [];
    }

    private sealed class FileBuilder(string path)
    {
        public WorkspaceFileChange Change { get; set; } = WorkspaceFileChange.Modified;

        public bool Binary { get; set; }

        public int Added { get; set; }

        public int Removed { get; set; }

        public string? OldMode { get; set; }

        public string? NewMode { get; set; }

        public List<HunkBuilder> Hunks { get; } = [];

        public WorkspaceFileDiff Build() => new()
        {
            Path = path,
            Change = Change,
            Binary = Binary,
            Added = Added,
            Removed = Removed,
            OldMode = OldMode,
            NewMode = NewMode,
            Hunks = [.. Hunks.Select(hunk => new WorkspaceDiffHunk(hunk.Header, hunk.Lines))],
        };
    }
}
