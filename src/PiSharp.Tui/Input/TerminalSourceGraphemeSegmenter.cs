using System.Globalization;
using System.Text;

namespace PiSharp.Tui.Input;

/// <summary>Source-profile Indic boundaries shared by editing, wrapping and frame admission.</summary>
internal static class TerminalSourceGraphemeSegmenter
{
    internal static int[] GetOffsets(string text)
    {
        var offsets = StringInfo.ParseCombiningCharacters(text);
        if (offsets.Length <= 1) return offsets;
        var read = 0; var written = 0; var position = 0;
        //0no consonant anchor,1consonant with extends,2consonant with at least one linker.
        var state = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var kind = TerminalSourceIndicConjunctData.Classify(rune.Value);
            // InCB Extend/Linker have Source GB9 behavior within the anchored conjunct;
            // this also retains newer Source combining scalars absent from the runtime table.
            var joined = state != 0 && kind is 2 or 3 || state == 2 && kind == 1;
            if (read < offsets.Length && offsets[read] == position)
            { var offset = offsets[read++]; if (!joined) offsets[written++] = offset; }
            state = kind switch { 1 => 1, 3 when state != 0 => 2, 2 => state, _ => 0 };
            position += rune.Utf16SequenceLength;
        }
        if (written != offsets.Length) Array.Resize(ref offsets, written);
        return offsets;
    }

    internal static IEnumerable<string> Elements(string text)
    {
        var offsets = GetOffsets(text);
        for (var at = 0; at < offsets.Length; at++)
            yield return text[offsets[at]..(at + 1 < offsets.Length ? offsets[at + 1] : text.Length)];
    }
}
