// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/registries/internal.ts, which only re-exports: registries for
// Chord's layers, a host's record of each registry, its contributions and its values by version. Its exports are internal types of
// PiSharp.Chord.Registries: Contributor, ContributionContent (with its ContributionKind) and RegistryContribution (contribution.ts),
// RegistryError (a record per kind, with its RegistryErrorKind) and RegistryRecord (record.ts), RegistryValueSource<V> and IRegistryWaiter
// (value.ts); checkProvider is RegistriesInternal.CheckProvider. The provider as a record serves it is IRegistryProvider (registry.ts), and
// RegistriesInternal also holds the helpers the layer's files share: JavaScript's identity on contributions and compositions (Same) and the
// casts between the untyped record and its typed tokens (TryCast, Cast, TypeName).
//
// Threads: as the state layer's (State/Internal.cs), a registry's record, contributions and values belong to their host, which serializes
// the calls that reach them; only the watch handles they make are safe on any thread.
namespace PiSharp.Chord.Registries;

/// <summary>Registries for Chord's layers (upstream <c>registries/internal.ts</c>): the helpers the layer's files share.</summary>
internal static partial class RegistriesInternal;
