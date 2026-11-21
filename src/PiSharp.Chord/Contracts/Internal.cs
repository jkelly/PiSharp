// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/contracts/internal.ts, which only re-exports: contracts for
// Chord's layers. Its value exports are the members of ContractsInternal, which each module's file extends with its own functions:
// accepts.ts (CheckAccepts), contract.ts (DataOf for contractData, CurrentVariants, VariantsAt, TakesNoArgs, VariantNamesOf for
// variantNames, VariantOrder, Local for LOCAL), evolve.ts (EditWith), fingerprint.ts (FingerprintOf, Hash64, NormalFormVersion),
// host-kind.ts (KindParams) and names.ts (CheckName). Its type exports are the internal records ContractData, VersionData, VariantData,
// MethodData, VariantNames, ParamsData and Snapshot, and the public MethodDeclaration (upstream's Method class, which the layers test with
// instanceof). The type-level helpers (ContractShape, CurrentVariants, ShapeOf, VariantImplementation, Increment, Params) have no C#
// counterpart: a contract's shape is its ContractData at run time, and Increment its version plus one. 1.6.0 adds joinKey (JoinKey,
// Params.cs), Snapshot and snapshotArguments (SnapshotArguments, Snapshot.cs), and with contract.ts and declare.ts AdmissionData,
// QueriesData, AdmissionDataOf (admissionData), EventSnapshot and MemberNamesOf.
namespace PiSharp.Chord.Contracts;

/// <summary>Contracts for Chord's layers (upstream <c>contracts/internal.ts</c>): the Chord-owned data behind contract handles and host
/// kinds, and the checks every layer shares. Chord-owned data is immutable by convention.</summary>
internal static partial class ContractsInternal;
