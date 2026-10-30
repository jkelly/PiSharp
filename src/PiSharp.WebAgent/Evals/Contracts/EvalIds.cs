// Decision 0011 (run W5, E0): the ids of the evals area's rows. A battle, test case, suite and suite run each have a text id: a two-letter
// prefix and 32 lower-case hex digits (like the run engine's "r" ids), made on the server or by a page, so a retried create POST that carries
// the same id is the same row (idempotent). Baselines, drift signals and price rows are SQLite integer ids. A suite target's id is derived
// from what it is (SuiteTarget.Id), so it never needs one of these.
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PiSharp.WebAgent.Evals;

/// <summary>Text ids of the evals area: <c>&lt;prefix&gt;&lt;32 hex&gt;</c>.</summary>
public static partial class EvalIds
{
    /// <summary>An Arena battle (<see cref="ArenaBattle"/>).</summary>
    public const string Battle = "ab";

    /// <summary>A test case (<see cref="TestCase"/>).</summary>
    public const string Case = "tc";

    /// <summary>A suite (<see cref="Suite"/>).</summary>
    public const string Suite = "su";

    /// <summary>A suite run (<see cref="SuiteRun"/>).</summary>
    public const string Run = "sr";

    /// <summary>Every prefix.</summary>
    public static IReadOnlyList<string> Prefixes { get; } = [Battle, Case, Suite, Run];

    /// <summary>A new random id with <paramref name="prefix"/>.</summary>
    /// <exception cref="ArgumentException">Not one of <see cref="Prefixes"/>.</exception>
    public static string New(string prefix)
    {
        if (!Prefixes.Contains(prefix, StringComparer.Ordinal)) throw new ArgumentException($"\"{prefix}\" is not an evals id prefix.", nameof(prefix));
        return prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>Whether <paramref name="id"/> is an id with <paramref name="prefix"/>.</summary>
    public static bool IsValid(string prefix, string? id) =>
        id is { Length: 34 } && id.StartsWith(prefix, StringComparison.Ordinal) && Prefixes.Contains(prefix, StringComparer.Ordinal) && Hex().IsMatch(id.AsSpan(2));

    /// <summary><paramref name="id"/> trimmed, when it is an id with <paramref name="prefix"/>.</summary>
    /// <param name="prefix">The expected prefix.</param>
    /// <param name="id">The id.</param>
    /// <param name="what">What it names, for the message (<c>suite</c>, <c>test case</c>).</param>
    /// <exception cref="ArgumentException">It is not.</exception>
    public static string Require(string prefix, string? id, string what)
    {
        var trimmed = id?.Trim();
        return IsValid(prefix, trimmed) ? trimmed! : throw new ArgumentException($"\"{id}\" is not a {what} id.");
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Hex();
}
