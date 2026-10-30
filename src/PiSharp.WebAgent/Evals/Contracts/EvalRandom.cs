// Decision 0011 (run W5, E0): the evals area's only source of randomness (brief rule 5, "statistics are reproducible"). Every random choice
// (the Arena's column shuffle, the rater's bootstrap samples) draws from an EvalRandom made from a seed that is stored with what it decided,
// so the same seed always gives the same shuffle or interval, on any machine and any .NET version: SplitMix64 is fixed here, unlike
// System.Random's algorithm. Seeds are 53-bit (NewSeed), so a seed survives a JSON round trip through a browser exactly.
using System.Security.Cryptography;

namespace PiSharp.WebAgent.Evals;

/// <summary>A deterministic random sequence (SplitMix64) from a stored seed. Not thread-safe: one instance per use.</summary>
public sealed class EvalRandom
{
    /// <summary>The largest seed <see cref="NewSeed"/> makes: 2^53 − 1, the largest integer a JSON number carries exactly.</summary>
    public const long MaxSeed = (1L << 53) - 1;

    private ulong state;

    /// <summary>The sequence of <paramref name="seed"/>.</summary>
    public EvalRandom(long seed)
    {
        Seed = seed;
        state = unchecked((ulong)seed);
    }

    /// <summary>The seed this sequence started from.</summary>
    public long Seed { get; }

    /// <summary>A new random seed, 0 to <see cref="MaxSeed"/>, from the system's cryptographic generator.</summary>
    public static long NewSeed() => BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & MaxSeed;

    /// <summary>The next 64 random bits.</summary>
    public ulong NextUInt64()
    {
        unchecked
        {
            var z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A uniform double in [0, 1), with 53 random bits.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>A uniform integer in [0, <paramref name="maxExclusive"/>), without modulo bias.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is not positive.</exception>
    public int Next(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        var bound = (ulong)maxExclusive;
        // Values below the threshold would make the low remainders more likely; drawing again keeps every remainder equally likely.
        var threshold = unchecked(0UL - bound) % bound;
        while (true)
        {
            var value = NextUInt64();
            if (value >= threshold) return (int)(value % bound);
        }
    }

    /// <summary>Shuffles <paramref name="items"/> in place (Fisher–Yates, from the end), so every order is equally likely.</summary>
    public void Shuffle<T>(IList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (var index = items.Count - 1; index > 0; index--)
        {
            var other = Next(index + 1);
            (items[index], items[other]) = (items[other], items[index]);
        }
    }

    /// <summary>The order of <paramref name="count"/> items <see cref="Shuffle{T}"/> gives: entry <c>i</c> is the original index of the item
    /// that ends at position <c>i</c>.</summary>
    public int[] Permutation(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var order = Enumerable.Range(0, count).ToArray();
        Shuffle(order);
        return order;
    }
}
