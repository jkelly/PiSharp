// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/history.ts (Change)
//
// The type-level history of a contract (VariantShape, Variants, Step, History, CurrentStep, StepAt, OlderVersions, ChangesAfter,
// MemberNames, VariantsWith, MethodAt, Increment) has no C# counterpart: C# has no literal or mapped types, and a contract's history is its
// ContractData.Versions at run time. Its one value a version records is Change. Area cv2-contracts added this file because every declared
// version records its changes (VersionData.Changes, empty for a base); area cv2-evolve, which owns history.ts, ports history.test.ts.
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
