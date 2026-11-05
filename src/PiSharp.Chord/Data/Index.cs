// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/data/index.ts, which only re-exports: data for apps, the
// public types of PiSharp.Chord.Data. ChordError, isChordError (ChordError.Is) and PluginError come from errors.ts; broken (Failure.Broken)
// and FailureCause (with its FailureKind) from failure.ts; Principal and PrincipalKeyFunction from principal.ts, where a principal is a
// JsonObject (plain JSON with a "kind") and PrincipalKeyFunction a delegate. ChordErrorCode and ChordErrorReason are TypeScript literal
// types: a code and a reason are strings here, and the pairings their types refuse are refused when a ChordError is made (Errors.cs).
namespace PiSharp.Chord.Data;
