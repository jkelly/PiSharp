// jsdiff 8.0.4 (BSD-3-Clause, Copyright (c) 2009-2015, Kevin Decker; see THIRD-PARTY-NOTICES.md): libesm/diff/base.js, libesm/diff/line.js
// and libesm/patch/create.js, reduced to what packages/durable/src/tools/edit-diff.ts (Pi abe508e1b89912adde45528136c3221eb69acdd7) calls:
// diffLines without options and createTwoFilesPatch with FILE_HEADERS_ONLY and a context size, both synchronous and without a timeout.
// The Myers walk, its tie-breaking and the hunk construction follow the JavaScript line for line, so the output is byte-identical.
using System.Globalization;
using System.Text;

namespace PiSharp.Durable.Tools;

/// <summary>One part of a line diff (jsdiff <c>ChangeObject</c>).</summary>
/// <param name="Value">The joined lines, each with its line break.</param>
/// <param name="Count">How many lines.</param>
/// <param name="Added">Lines only in the new text.</param>
/// <param name="Removed">Lines only in the old text.</param>
internal sealed record LineChange(string Value, int Count, bool Added, bool Removed);

/// <summary>jsdiff's <c>diffLines</c> and <c>createTwoFilesPatch</c>.</summary>
internal static class JsDiff
{
    private sealed class Component
    {
        public int Count;
        public bool Added;
        public bool Removed;
        public Component? Previous;
    }

    private sealed class DiffPath
    {
        public int OldPos;
        public Component? LastComponent;
    }

    /// <summary>jsdiff <c>diffLines(oldStr, newStr)</c>.</summary>
    public static List<LineChange> DiffLines(string oldString, string newString)
    {
        var oldTokens = RemoveEmpty(Tokenize(oldString));
        var newTokens = RemoveEmpty(Tokenize(newString));
        int newLen = newTokens.Count, oldLen = oldTokens.Count;
        var editLength = 1;
        var maxEditLength = newLen + oldLen;
        // jsdiff indexes a JavaScript array by diagonal, negative diagonals included.
        var bestPath = new Dictionary<int, DiffPath?> { [0] = new DiffPath { OldPos = -1 } };
        var newPos = ExtractCommon(bestPath[0]!, newTokens, oldTokens, 0);
        if (bestPath[0]!.OldPos + 1 >= oldLen && newPos + 1 >= newLen)
            return BuildValues(bestPath[0]!.LastComponent, newTokens, oldTokens);
        double minDiagonalToConsider = double.NegativeInfinity, maxDiagonalToConsider = double.PositiveInfinity;
        while (editLength <= maxEditLength)
        {
            for (var diagonalPath = (int)Math.Max(minDiagonalToConsider, -editLength); diagonalPath <= Math.Min(maxDiagonalToConsider, editLength);
                diagonalPath += 2)
            {
                var removePath = bestPath.GetValueOrDefault(diagonalPath - 1);
                var addPath = bestPath.GetValueOrDefault(diagonalPath + 1);
                // No one else is going to attempt to use this value, clear it.
                if (removePath is not null) bestPath[diagonalPath - 1] = null;
                var canAdd = false;
                if (addPath is not null)
                {
                    // What newPos will be after an insertion.
                    var addPathNewPos = addPath.OldPos - diagonalPath;
                    canAdd = 0 <= addPathNewPos && addPathNewPos < newLen;
                }
                var canRemove = removePath is not null && removePath.OldPos + 1 < oldLen;
                if (!canAdd && !canRemove)
                {
                    // A terminal path: prune it.
                    bestPath[diagonalPath] = null;
                    continue;
                }
                // Branch from the prior path farthest from the origin that does not pass the bounds of the diff graph.
                var basePath = !canRemove || (canAdd && removePath!.OldPos < addPath!.OldPos)
                    ? AddToPath(addPath!, added: true, removed: false, 0)
                    : AddToPath(removePath!, added: false, removed: true, 1);
                newPos = ExtractCommon(basePath, newTokens, oldTokens, diagonalPath);
                if (basePath.OldPos + 1 >= oldLen && newPos + 1 >= newLen) return BuildValues(basePath.LastComponent, newTokens, oldTokens);
                bestPath[diagonalPath] = basePath;
                if (basePath.OldPos + 1 >= oldLen) maxDiagonalToConsider = Math.Min(maxDiagonalToConsider, diagonalPath - 1);
                if (newPos + 1 >= newLen) minDiagonalToConsider = Math.Max(minDiagonalToConsider, diagonalPath + 1);
            }
            editLength++;
        }
        throw new InvalidOperationException("The line diff did not converge.");
    }

    private static DiffPath AddToPath(DiffPath path, bool added, bool removed, int oldPosInc)
    {
        var last = path.LastComponent;
        if (last is not null && last.Added == added && last.Removed == removed)
            return new DiffPath
            {
                OldPos = path.OldPos + oldPosInc,
                LastComponent = new Component { Count = last.Count + 1, Added = added, Removed = removed, Previous = last.Previous },
            };
        return new DiffPath
        {
            OldPos = path.OldPos + oldPosInc,
            LastComponent = new Component { Count = 1, Added = added, Removed = removed, Previous = last },
        };
    }

    private static int ExtractCommon(DiffPath basePath, List<string> newTokens, List<string> oldTokens, int diagonalPath)
    {
        int newLen = newTokens.Count, oldLen = oldTokens.Count;
        int oldPos = basePath.OldPos, newPos = oldPos - diagonalPath, commonCount = 0;
        while (newPos + 1 < newLen && oldPos + 1 < oldLen && string.Equals(oldTokens[oldPos + 1], newTokens[newPos + 1], StringComparison.Ordinal))
        {
            newPos++;
            oldPos++;
            commonCount++;
        }
        if (commonCount != 0) basePath.LastComponent = new Component { Count = commonCount, Previous = basePath.LastComponent };
        basePath.OldPos = oldPos;
        return newPos;
    }

    private static List<string> RemoveEmpty(List<string> tokens) => [.. tokens.Where(token => token.Length != 0)];

    /// <summary>jsdiff's line <c>tokenize</c>: <c>value.split(/(\n|\r\n)/)</c>, the final empty piece dropped, each line joined with its
    /// break.</summary>
    private static List<string> Tokenize(string value)
    {
        var linesAndNewlines = new List<string>();
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var length = value[index] == '\n' ? 1 : value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n' ? 2 : 0;
            if (length == 0) continue;
            linesAndNewlines.Add(value[start..index]);
            linesAndNewlines.Add(value.Substring(index, length));
            index += length - 1;
            start = index + 1;
        }
        linesAndNewlines.Add(value[start..]);
        // Ignore the final empty token that occurs if the string ends with a new line.
        if (linesAndNewlines[^1].Length == 0) linesAndNewlines.RemoveAt(linesAndNewlines.Count - 1);
        var retLines = new List<string>();
        for (var index = 0; index < linesAndNewlines.Count; index++)
        {
            if (index % 2 == 1) retLines[^1] += linesAndNewlines[index];
            else retLines.Add(linesAndNewlines[index]);
        }
        return retLines;
    }

    private static List<LineChange> BuildValues(Component? lastComponent, List<string> newTokens, List<string> oldTokens)
    {
        var components = new List<Component>();
        for (var component = lastComponent; component is not null; component = component.Previous) components.Add(component);
        components.Reverse();
        var values = new List<LineChange>(components.Count);
        int newPos = 0, oldPos = 0;
        foreach (var component in components)
        {
            string value;
            if (!component.Removed)
            {
                value = string.Concat(newTokens.Skip(newPos).Take(component.Count));
                newPos += component.Count;
                if (!component.Added) oldPos += component.Count;
            }
            else
            {
                value = string.Concat(oldTokens.Skip(oldPos).Take(component.Count));
                oldPos += component.Count;
            }
            values.Add(new LineChange(value, component.Count, component.Added, component.Removed));
        }
        return values;
    }

    private sealed class Hunk
    {
        public int OldStart;
        public int OldLines;
        public int NewStart;
        public int NewLines;
        public required List<string> Lines;
    }

    /// <summary>jsdiff <c>createTwoFilesPatch(oldFileName, newFileName, oldStr, newStr, undefined, undefined, { context,
    /// headerOptions: FILE_HEADERS_ONLY })</c>.</summary>
    public static string CreateTwoFilesPatch(string oldFileName, string newFileName, string oldString, string newString, int context)
    {
        var hunks = StructuredPatch(oldString, newString, context);
        var ret = new List<string> { "--- " + oldFileName, "+++ " + newFileName };
        foreach (var hunk in hunks)
        {
            // Unified Diff Format quirk: if the chunk size is 0, the first number is one lower than one would expect.
            if (hunk.OldLines == 0) hunk.OldStart -= 1;
            if (hunk.NewLines == 0) hunk.NewStart -= 1;
            ret.Add(string.Create(CultureInfo.InvariantCulture, $"@@ -{hunk.OldStart},{hunk.OldLines} +{hunk.NewStart},{hunk.NewLines} @@"));
            ret.AddRange(hunk.Lines);
        }
        return string.Join('\n', ret) + "\n";
    }

    private static List<Hunk> StructuredPatch(string oldString, string newString, int context)
    {
        // Step 1: build up the patch with no "\ No newline at end of file" lines and with the arrays of lines containing trailing newline
        // characters; tidy up later. An empty value is appended to make cleanup easier.
        var diff = DiffLines(oldString, newString).Select(change => (change.Added, change.Removed, Lines: SplitLines(change.Value))).ToList();
        diff.Add((false, false, []));
        static IEnumerable<string> ContextLines(IEnumerable<string> lines) => lines.Select(entry => " " + entry);
        var hunks = new List<Hunk>();
        int oldRangeStart = 0, newRangeStart = 0, oldLine = 1, newLine = 1;
        var curRange = new List<string>();
        for (var i = 0; i < diff.Count; i++)
        {
            var current = diff[i];
            var lines = current.Lines;
            if (current.Added || current.Removed)
            {
                // If there is previous context, start with that.
                if (oldRangeStart == 0)
                {
                    oldRangeStart = oldLine;
                    newRangeStart = newLine;
                    if (i > 0)
                    {
                        var previous = diff[i - 1].Lines;
                        curRange = context > 0 ? [.. ContextLines(previous.Skip(Math.Max(0, previous.Count - context)))] : [];
                        oldRangeStart -= curRange.Count;
                        newRangeStart -= curRange.Count;
                    }
                }
                foreach (var line in lines) curRange.Add((current.Added ? "+" : "-") + line);
                if (current.Added) newLine += lines.Count;
                else oldLine += lines.Count;
            }
            else
            {
                // Identical context lines: close out any changes that have been output, or join overlapping ones.
                if (oldRangeStart != 0)
                {
                    if (lines.Count <= context * 2 && i < diff.Count - 2)
                    {
                        curRange.AddRange(ContextLines(lines));
                    }
                    else
                    {
                        var contextSize = Math.Min(lines.Count, context);
                        curRange.AddRange(ContextLines(lines.Take(contextSize)));
                        hunks.Add(new Hunk
                        {
                            OldStart = oldRangeStart,
                            OldLines = oldLine - oldRangeStart + contextSize,
                            NewStart = newRangeStart,
                            NewLines = newLine - newRangeStart + contextSize,
                            Lines = curRange,
                        });
                        oldRangeStart = 0;
                        newRangeStart = 0;
                        curRange = [];
                    }
                }
                oldLine += lines.Count;
                newLine += lines.Count;
            }
        }
        // Step 2: eliminate the trailing `\n` from each line of each hunk and, where needed, add "\ No newline at end of file".
        foreach (var hunk in hunks)
        {
            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                if (hunk.Lines[i].EndsWith('\n')) hunk.Lines[i] = hunk.Lines[i][..^1];
                else
                {
                    hunk.Lines.Insert(i + 1, "\\ No newline at end of file");
                    i++; // Skip the line just added.
                }
            }
        }
        return hunks;
    }

    /// <summary>The lines of <paramref name="text"/>, each with its trailing newline where present.</summary>
    private static List<string> SplitLines(string text)
    {
        var hasTrailingNewline = text.EndsWith('\n');
        var result = text.Split('\n').Select(line => line + "\n").ToList();
        if (hasTrailingNewline) result.RemoveAt(result.Count - 1);
        else result[^1] = result[^1][..^1];
        return result;
    }
}
