// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/index.ts, which only re-exports: contracts for
// apps, the public types of PiSharp.Chord.Contracts. remote and local are Contract.Remote and Contract.Local (declare.ts), hostKind is the
// HostKind constructor and a call of a kind its Address method (host-kind.ts), and the schema helpers str, num, int, bool, nul, lit,
// oneOf, union, obj, arr, map, opt, bytes, unknown, keyed and method are the members of Schemas (schema.ts); a schema is a JsonNode
// (plain JSON Schema), opt(...) an Optional, keyed(...) a KeyedMember and method(...) a MethodDeclaration. evolve is Contract.Evolve, whose
// edit gets a ContractEditor (evolve.ts). The type exports map as follows: Contract and Address are classes; HostKind is a class whose
// Kind is its name; Schema is JsonNode; the schema interfaces (StringSchema, NumberSchema, IntegerSchema, BooleanSchema, NullSchema) and
// Bounds, Defaulted and Literal are the helpers' optional parameters and the Literal struct; KeyedMember and Optional are classes;
// AcceptsTable is a class of one shape for every contract, with VersionMappings, StateSection, StateEntry, StateMapping and MethodMapping
// (accepts.ts), checked when a provider binds it. Static, ParamsOf, ParamsOfKind, KindShape, Implementation and KeyedWatchable are
// TypeScript type-level mappings with no C# counterpart: C# has no literal or mapped types, so declarations are checked when they run
// (decision 0013, the telemetry schemas' precedent), a contract's params arrive as a JsonObject, and an implementation's members are bound
// by name where a host serves it (phase 6).
namespace PiSharp.Chord.Contracts;
