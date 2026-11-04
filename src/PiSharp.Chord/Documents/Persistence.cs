// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/persistence.ts (Stored; in part)
//
// Persisted documents' store side (slice 6a §8, §10). Area cv2-documents-core ported the record a keyed resident keeps (residency.ts's
// Resident): Stored, what is stored of a loaded document, Durable's LoadedDocument fields, in the documents engine's number types
// (Store/Engine.cs: ids, revisions and versions are longs, the delta count an int). The rest of the file, the Persistence class with its
// loads, finds, key pages and staged writes, and the FoundDocument, Loaded, DocumentChange, Held and StoredChange types with storedAfter
// and checkReadable, is area cv2-documents-slots'.
namespace PiSharp.Chord.Documents;

/// <summary>What is stored of a loaded document (upstream <c>Stored</c>): its record's id and revision, its base's version, the deltas
/// after it.</summary>
/// <param name="Id">The document record's id.</param>
/// <param name="Revision">The record's revision.</param>
/// <param name="Version">The version of the stored base: below the definition's, the next change stores a base (rule 1).</param>
/// <param name="DeltasSinceBase">The deltas stored after the base.</param>
internal sealed record Stored(long Id, long Revision, long Version, int DeltasSinceBase);
