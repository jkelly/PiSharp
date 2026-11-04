// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/names.ts
//
// Names that become SQL names or stored identities in the store (decision "Restricted names"): object types, host kinds, tables, state
// definitions and jobs use npm package-name characters, lowercase, so `<host kind>:<object type>:<table>` is injective and readable, and a
// document's or a job record's name is stored as declared.
using System.Text.RegularExpressions;

namespace PiSharp.Chord.Contracts;

internal static partial class ContractsInternal
{
    /// <summary>Lowercase npm package-name characters: <c>:</c> cannot occur, so names joined with it stay apart. <c>\z</c>, not <c>$</c>:
    /// JavaScript's <c>$</c> matches only at the end.</summary>
    private static readonly Regex Name = new(@"^[a-z0-9@/._~-]+\z", RegexOptions.CultureInvariant);

    /// <summary>Throws unless <paramref name="name"/> is a nonempty lowercase name of <c>[a-z0-9@/._~-]</c> (<paramref name="what"/> names
    /// it in the error).</summary>
    internal static void CheckName(string? name, string what)
    {
        // Declaration policy: callers reach this check with any string, and with null.
        if (name is null || !Name.IsMatch(name))
            throw new ArgumentException($"{what} {ChordJson.Quote(name)}: expected lowercase [a-z0-9@/._~-]");
    }
}
