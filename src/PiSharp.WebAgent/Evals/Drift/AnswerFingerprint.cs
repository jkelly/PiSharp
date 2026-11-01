// Decision 0011 (run W5, E3): the answer fingerprint Drift Watch compares (SuiteResult.Fingerprint, the plan's "answer fingerprint: SimHash
// outside the baseline's own spread"). A 64-bit SimHash (Charikar) over the answer's words and word pairs: the text is lower-cased
// (invariant culture) and split into runs of letters and digits; each word and each pair of neighbouring words is a feature weighted by how
// often it occurs, hashed with 64-bit FNV-1a over its UTF-8 bytes and mixed with SplitMix64's finalizer; a bit of the fingerprint is set when
// the features with that bit set outweigh the others. Similar answers get fingerprints a few bits apart, unrelated ones about 32 bits apart
// (Distance). The value depends only on the text, so it is the same on every machine and run. The suite runner (E4) stores Of(answer text)
// with each result; an answer with no words has no fingerprint (null).
using System.Numerics;
using System.Text;

namespace PiSharp.WebAgent.Evals.Drift;

/// <summary>The 64-bit SimHash of an answer, and the distance between two.</summary>
public static class AnswerFingerprint
{
    /// <summary>The most characters read (longer answers are fingerprinted on their start: the same cut as <see cref="SuiteResult.Cut"/>).</summary>
    public const int MaxChars = SuiteResult.MaxTextLength;

    /// <summary>The fingerprint of <paramref name="text"/>, or null when it has no words.</summary>
    public static long? Of(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var words = Words(text.Length > MaxChars ? text[..MaxChars] : text);
        if (words.Count == 0) return null;
        var features = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < words.Count; index++)
        {
            Count(features, words[index]);
            if (index > 0) Count(features, words[index - 1] + " " + words[index]);
        }
        Span<long> weights = stackalloc long[64];
        weights.Clear();
        foreach (var (feature, count) in features)
        {
            var hash = Mix(Fnv1a(feature));
            for (var bit = 0; bit < 64; bit++)
                weights[bit] += ((hash >> bit) & 1) != 0 ? count : -count;
        }
        ulong fingerprint = 0;
        for (var bit = 0; bit < 64; bit++)
            if (weights[bit] > 0) fingerprint |= 1UL << bit;
        return unchecked((long)fingerprint);
    }

    /// <summary>How many of the 64 bits differ between <paramref name="first"/> and <paramref name="second"/> (0: the same; about 32: unrelated).</summary>
    public static int Distance(long first, long second) => BitOperations.PopCount(unchecked((ulong)(first ^ second)));

    private static void Count(Dictionary<string, int> features, string feature) =>
        features[feature] = features.TryGetValue(feature, out var count) ? count + 1 : 1;

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                word.Append(Rune.ToLowerInvariant(rune).ToString());
                continue;
            }
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    private static ulong Fnv1a(string feature)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in Encoding.UTF8.GetBytes(feature))
        {
            hash ^= value;
            hash = unchecked(hash * 1099511628211UL);
        }
        return hash;
    }

    /// <summary>SplitMix64's finalizer, so neighbouring FNV values spread over every bit.</summary>
    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
