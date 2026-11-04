// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/history.ts
//
// The history of a contract: every version's members and the changes that produced it. Upstream's history is type-level (VariantShape,
// Variants, Step, History, CurrentStep, StepAt, OlderVersions, ChangesAfter, MemberNames, VariantsWith, MethodAt, Increment): the types
// of a contract's implementations and accepts tables are computed from it. C# has no literal or mapped types, so a contract's history is
// its ContractData.Versions at run time, oldest first: a VariantShape is a VariantData, a step a VersionData, CurrentStep the last
// version, StepAt the version of a number, Increment the version plus one (Contract.Evolve), and OlderVersions and ChangesAfter are the
// functions below, which accepts.ts's needs computes inline. Its one value a version records is Change. Area cv2-contracts added Change,
// because every declared version records its changes (VersionData.Changes, empty for a base); area cv2-evolve ported the rest.
namespace PiSharp.Chord.Contracts;

/// <summary>The side of a member an edit changed: its state schema, its arguments, its result, or a local contract's keyed member (slice 7b).</summary>
internal enum ChangeSide
{
    State,
    Args,
    Result,
    Keyed,
}

/// <summary>What one edit changed: one side of one member in one variant.</summary>
internal readonly record struct Change(string Variant, string Member, ChangeSide Side);

internal static partial class ContractsInternal
{
    /// <summary>The versions before the current one, oldest first (upstream's type-level <c>OlderVersions</c>).</summary>
    internal static IEnumerable<VersionData> OlderVersions(ContractData data) => data.Versions.Take(data.Versions.Count - 1);

    /// <summary>Every change made after version <paramref name="version"/>, in history order (upstream's type-level
    /// <c>ChangesAfter</c>); none after the current version, or for a version the history does not have.</summary>
    internal static IEnumerable<Change> ChangesAfter(ContractData data, long version)
    {
        var index = data.Versions.ToList().FindIndex(step => step.Version == version);
        return index == -1 ? [] : data.Versions.Skip(index + 1).SelectMany(step => step.Changes);
    }
}
