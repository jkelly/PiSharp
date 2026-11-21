// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/data/internal.ts, which only re-exports: data for Chord's
// layers. Its value exports are the members of DataInternal, which each module's file extends with its own functions: args.ts (BindProvider,
// Binds, CheckArgs), errors.ts (AsChordError, ChordErrorReasons for CHORD_ERROR_REASONS, ChordErrorFromJson, Invalid, ProtocolVersion for
// PROTOCOL_VERSION), failure.ts (OpenFailure, SameCause, WaitingFor), params.ts (CheckAddress, CheckParams, IdentityKey), principal.ts
// (CanonicalJson, PrincipalJson, PrincipalKey) and validator.ts (Check, ValidatorFor). Its type exports are the internal Provider and CheckedArgs
// (args.ts) and Validator (validator.ts, pi-schema's compiled check of a Chord-owned schema). Value (JSON plus bytes) has
// no C# type of its own: a value is a JsonNode tree whose byte leaves are JsonValues holding a byte[] (Args.cs).
namespace PiSharp.Chord.Data;

/// <summary>Data for Chord's layers (upstream <c>data/internal.ts</c>): per-call checks of params and arguments, principal keys, and
/// errors on the wire. Every function that works takes a required context and counts its work through the context's telemetry.</summary>
internal static partial class DataInternal;
