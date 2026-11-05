// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/internal.ts, which only re-exports: documents for
// Chord's layers, an object's documents and slots, the handles' classes, the seal token and transactions. Its exports are internal types of
// PiSharp.Chord.Documents: AddressSource (address-source.ts), Documents and DocumentsOwner (documents.ts: IDocumentsOwner, with its
// DocumentsStore, and the engine's view of a slot, IDeclaredSlot, and of the transaction holding the line, ILineHolder), SealToken
// (fence.ts) and SlotDefinition (slot.ts; its AnyDefinition is StateDefinition). Line (line.ts) and Residency, Resident and IResidentSlot
// (residency.ts) are the engine's own, and Stored (persistence.ts) the record a resident keeps. Area cv2-documents-slots ported the rest:
// KeyDeclarer, Keyed and Singleton (handles.ts: IKeyDeclarer, Keyed, Singleton), KeyedSlot and KeySource (keyed-slot.ts), DocumentChange,
// Held, Persistence, StoredChange and storedAfter (persistence.ts; DocumentsInternal.StoredAfter), DocumentSlot, PersistedSlot and
// SingletonSlot (slot.ts: IDocumentSlot, IPersistedSlot, SingletonSlot), and Staged and Transaction (transaction.ts: IStaged, Transaction).
// DocumentsInternal holds the layer's free functions.
//
// Threads: as the state layer's (State/Internal.cs), an object's documents belong to their host, which serializes the calls that reach
// them; the line, the seal tokens and the residency's timer are safe on any thread (Documents.cs).
namespace PiSharp.Chord.Documents;

/// <summary>Documents for Chord's layers (upstream <c>documents/internal.ts</c>): the layer's free functions.</summary>
internal static partial class DocumentsInternal;
