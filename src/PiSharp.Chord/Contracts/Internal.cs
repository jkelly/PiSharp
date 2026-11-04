// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/internal.ts, which only re-exports: contracts for
// Chord's layers. Its value exports are the members of ContractsInternal, which each module's file extends with its own functions:
// accepts.ts (CheckAccepts), contract.ts (DataOf for contractData, CurrentVariants, VariantsAt, TakesNoArgs, VariantNamesOf for
// variantNames, VariantOrder, Local for LOCAL), evolve.ts (EditWith), fingerprint.ts (FingerprintOf, Hash64, NormalFormVersion),
// host-kind.ts (KindParams) and names.ts (CheckName). Its type exports are the internal records ContractData, VersionData, VariantData,
// MethodData, VariantNames, ParamsData and Snapshot, and the public MethodDeclaration (upstream's Method class, which the layers test with
// instanceof). The type-level helpers (ContractShape, CurrentVariants, ShapeOf, VariantImplementation, Increment, Params) have no C#
// counterpart: a contract's shape is its ContractData at run time, and Increment its version plus one.
namespace PiSharp.Chord.Contracts;

/// <summary>Contracts for Chord's layers (upstream <c>contracts/internal.ts</c>): the Chord-owned data behind contract handles and host
/// kinds, and the checks every layer shares. Chord-owned data is immutable by convention.</summary>
internal static partial class ContractsInternal;
