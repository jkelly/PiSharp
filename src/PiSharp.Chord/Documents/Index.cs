// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/index.ts, which only re-exports, and
// packages/chord/src/next/documents/README.md: documents for apps, the public types of PiSharp.Chord.Documents. From definition.ts:
// CommitPersistence, defineState (StateDefinition.Define, with StateOptions, CheckpointWhen and CheckpointInfo), JsonObject (the BCL's
// System.Text.Json.Nodes.JsonObject) and StateDefinition. From handles.ts: SingletonHandle and KeyedHandle (ISingletonHandle and
// IKeyedHandle); HandleOf is a conditional type, and a declaration's handle is the one its definition's Keyed calls for. From transaction.ts:
// Tx, area cv2-documents-slots'.
//
// The README's rules, as far as the ported parts go:
// - defineState(name, { schema, version, initial, keyed?, migrate?, persist?, checkpointWhen? }): the root is a JSON object. initial() is
//   checked against the schema once, when the declaring definition is first used (retaken only when the declaring version or fingerprint
//   changes). persist: true requires checkpointWhen (here a refusal when Define runs) and a `local` type with empty params with a store on
//   its host (P3, the host's check, phase 6).
// - Documents belong to their object, not to an attempt: a new attempt's self.state gets handles to the same documents, and an old handle
//   below the slot's version is refused released/version (SlotDefinition.AssertVersion). An ending attempt's handles are sealed: anything
//   taking the line is refused released (Documents.OnLineAsync, AssertAdmitted). An object's transactions run one at a time on its line.
// - A stored document of a newer version, a migrate that throws or returns a value outside the schema, or the same version with another
//   fingerprint fails the object broken; the data stays.
namespace PiSharp.Chord.Documents;
