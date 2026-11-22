// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/surfaces/variant.ts.
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;

namespace PiSharp.Chord.Surfaces;

/// <summary>A remote reference as a UI reads it in one variant: the reference while it is live in that variant, nothing otherwise. What the
/// terminal scope's <c>Variant</c> returns (upstream also React's <c>useVariant</c>, which is browser-only). A held reference's variant is
/// <c>null</c> until it binds and may change while it is held, and it keeps its last variant while it loads, so comparing
/// <see cref="IReference.Variant"/> alone would leave an editor's controls up for a reader. Upstream's <c>VariantRef</c>,
/// <c>VariantOf</c> and <c>InVariant</c> are TypeScript typing with no C# counterpart: a variant is a string here.</summary>
public static class Variants
{
    /// <summary><paramref name="reference"/> when it is live and bound in <paramref name="variant"/>, else <c>null</c>: no reference,
    /// loading (also between two variants), failed (denied too), ended or released, or another variant. True of the moment it is read:
    /// no guarantee across an <c>await</c>.</summary>
    public static T? LiveIn<T>(T? reference, string variant) where T : class, IReference
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (reference is null) return null;
        // Variant throws on a released reference: read only while live. Another thread may release it between the two reads (upstream
        // has one thread), which reads as nothing too.
        try { return reference.State.Value.Kind == "live" && reference.Variant == variant ? reference : null; }
        catch (ChordError) { return null; }
    }
}
