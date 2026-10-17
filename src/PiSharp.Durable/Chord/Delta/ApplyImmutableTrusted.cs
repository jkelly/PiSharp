// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/chord/src/delta/apply-immutable-trusted.ts (applyImmutableTrusted).
// Upstream this is a second applier for operations the tracker produced itself: it skips assertValidOp and the own-property/bounds
// checks and copies each touched container once with defineProperty. Here values are immutable C# records, so the copy-once work tree
// of DeltaApply already is that applier and malformed tuples cannot be represented; the only work left to skip is the path-safety
// scan. It is therefore an internal option of DeltaApply, not a separate implementation, and adds no behaviour of its own: operations
// that do not resolve still throw (upstream throws TypeError there too).
namespace PiSharp.Durable.Chord.Delta;

/// <summary>Applies self-produced, already validated operations (applyImmutableTrusted).</summary>
internal static class DeltaApplyTrusted
{
    /// <summary>Applies one trusted batch; untouched subtrees keep their references, as with <see cref="DeltaApply.Apply"/>.</summary>
    internal static JsonValue Apply(JsonValue target, IEnumerable<Op> ops) => DeltaApply.ApplyBatches(target, [ops], trusted: true);
}
