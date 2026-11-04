// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/slot.ts (AnyDefinition, SlotDefinition; in part)
//
// Document slots (slice 6a §1.2): one per declared document name on its object, across the object's attempts. Area cv2-documents-core
// ported the part the documents engine and the handles check through: SlotDefinition, the definition a slot's values are of, with the
// checks of a new attempt's declaration and the old handles' version fence (R15). Upstream's AnyDefinition, a definition whatever its value
// and keyedness, is StateDefinition itself (Definition.cs). The slots themselves (DocumentSlot, PersistedSlot and SingletonSlot) are area
// cv2-documents-slots'. A declaration's refusal is upstream's Error, an InvalidOperationException here (the attempt fails broken); the
// version fence throws ChordErrors.
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Documents;

/// <summary>
/// The definition a slot's values are of (upstream <c>SlotDefinition</c>): the one that hydrated it last, the first declaration until then.
/// It checks a new attempt's declaration and fences the old handles (R15).
/// </summary>
internal sealed class SlotDefinition
{
    private StateDefinition current;
    private bool hydrated;

    public SlotDefinition(StateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        current = definition;
    }

    /// <summary>The definition the values are of.</summary>
    public StateDefinition Current => current;

    /// <summary>The definition the values were hydrated with, null before the first hydration.</summary>
    public StateDefinition? Hydrated => hydrated ? current : null;

    /// <summary>
    /// Checks a declaration of this name by a new attempt: a flipped keyed or persist, a version older than the values (old code) or the same
    /// version with another fingerprint (a schema changed without a bump) throw (the attempt fails broken).
    /// </summary>
    public void Check(StateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var (name, version, keyed, persist, fingerprint) = (current.Name, current.Version, current.Keyed, current.Persist, current.Fingerprint);
        if (definition.Keyed != keyed)
            throw new InvalidOperationException($"{name}: declared {(definition.Keyed ? "keyed" : "singleton")}, its documents are not");
        if (definition.Persist != persist)
            throw new InvalidOperationException($"{name}: declared {(definition.Persist ? "persisted" : "in memory")}, its documents are not");
        if (definition.Version < version)
            throw new InvalidOperationException($"{name}: version {definition.Version} is older than its documents' ({version})");
        if (definition.Version == version && !string.Equals(definition.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name}: the schema changed without a version bump (version {version})");
    }

    /// <summary>Records the hydration with <paramref name="definition"/>.</summary>
    public void Hydrate(StateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        current = definition;
        hydrated = true;
    }

    /// <summary>
    /// Throws <c>released</c> for a handle of <paramref name="version"/> once the values migrated past it (old code reads no new shape), and
    /// <c>not_ready</c> for a handle of a newer version until its activation migrated them (new code reads no old shape: a retry's factory).
    /// </summary>
    public void AssertVersion(long version)
    {
        var definition = current;
        if (version == definition.Version) return;
        var message = $"{definition.Name}: a handle of version {version}; the documents are at version {definition.Version}";
        if (version < definition.Version) throw new ChordError("released", message, "version");
        throw new ChordError("not_ready", $"{message} until the activation migrates them");
    }
}
