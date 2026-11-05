// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/registries/index.ts, which only re-exports, and
// packages/chord/src/next/registries/README.md: registries for apps, the public types of PiSharp.Chord.Registries. defineRegistry is
// Registry.Define (with RegistryOptions), and Registry, RegistryEntry, RegistryConflict, ComposeContext, RegistryProvider and
// RegistryAccepts (with RegistryAdapters, the entry of one version) are the classes and records of those names (registry.ts); RegistryValue
// is the interface IRegistryValue<V>; RegistryTable<C> is IReadOnlyList<RegistryEntry<C>>, and keyedEntries and orderedEntries are
// RegistryTable.KeyedEntries and RegistryTable.OrderedEntries, with TableItem (entries.ts). Contribution<C> has no C# type: the host's
// contribute (phase 6) takes the value as an object and follows it when it is a Chord watchable.
//
// The README's rules hold: a registry is an extension point between plugins. The defining plugin shares a token and serves it with
// compose (host.serve, phase 6), which is pure and synchronous and always returns a value; a conflict it resolved is reported once per
// episode; it runs at the end of load's setups, then lazily (a change of the table marks it; it composes when read or, while someone
// watches, observes or waits for it, in the batch that changed the table), and an unchanged table composes nothing. A throwing compose
// is reported once per episode and keeps its last good composition while every contribution it was built from is in the table (decision
// 5). The table is sorted by plugin name, then type (a facet's entry first), never load order; an older contribution is adapted once per
// value, a newer one or one without an adapter is left out and reported once; a Chord watchable contribution is followed, and left out
// while it fails; anything else is a plain value. Readers of an older version get the composition mapped back once per composition;
// without a mapping, or newer than the provider, they get contract_mismatch; without a provider a registry has no value
// (unavailable/undeclared). Registries carry natives: an entry lives exactly as long as its contributor, and use sites look up at each use
// and keep nothing.
namespace PiSharp.Chord.Registries;
