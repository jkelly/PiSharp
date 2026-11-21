# chord-foundation: Chord 1.5.2 -> 1.6.0, the contract, wire and state layer

Work package `chord-foundation` of the Pi 1.6.0 sync, 2026-11-21. Branch `sync/1.6.0-chord-foundation`, from `abac6b2c`. Upstream v1.6.0 is `E:/AI/PiSharp/upstream-v160` (`faac585884ae2c013a6b1400f3e9e77f10b34bac`), v1.5.2 is `E:/AI/PiSharp/upstream-v152` (`b4afdddf44e059c67ccf5097e5317d913521b470`). The plan was `AI_DOCS/agent-reports/sync-1.6.0-chord-discovery.md`; where this report says "upstream wins", the discovery report's proposal was changed after reading the source.

This package delivers the shapes the other six Chord packages code against. It does not make a host emit an event, run a named query or decide an admission: those are `chord-remote`, `chord-queries` and `chord-admission`. Section 3 is the API they start from.

## 1. Upstream changes in this area

Status is one of `ported` (code and a case that ran), `already-ported`, `no-op` or `deferred`, as the plan file defines them.

| Upstream | Status | C# | Cases |
|---|---|---|---|
| `contracts/accepts.ts`: an `events` section; a changed event member still declared needs a function or `"refuse"`, a removed or added one nothing | ported | `Contracts/Accepts.cs` (`VersionMappings.Events`, `Needs.Events`, `CheckEventVariant`, `CheckEventMapping`) | `contracts.accepts.event-mappings-*` (4) |
| `contracts/contract.ts`: `MemberKind` `event`/`queries`, `VariantData.events/queries`, `QueriesData`, `AdmissionData`, `ContractData.admission`, `VariantNames.events` with the shared empty list, `memberNames`, `NamedQuery`, `QueryPage`, `EventMember`, `EventSource`, `EventSubscription` | ported | `Contracts/Contract.cs` | `contracts.contract.member-names-*` (3), `contracts.contract.160-variant-names-*` (1), and every declare case |
| `contracts/contract.ts`: `AdmittedOf`, `NamedQueriesOf`, `VariantImplementation` | no-op: TypeScript type-level | — | — |
| `contracts/declare.ts`: event members (local marker, a remote variant's `events`), `queries` members (local only), `admission` on `remote(...)`, `admissionData`, `eventSnapshot`, `readQueries`, kinds recorded for both, `"an event member"`, the `events` section in member paths | ported | `Contracts/Declare.cs` | `contracts.declare.admission-*` (3), `contracts.declare.queries-members-*` (2), `contracts.declare.event-members-*` (3) |
| `contracts/declare.ts`: `checkKeys(..., "admission")` (an unknown key in a local declaration) | no-op: the C# declarations are typed, `LocalDeclaration` has no such property | — | asserted as such in `contracts.declare.admission-is-json-only-*` |
| `contracts/evolve.ts`: `event`, `addEvent`, `removeEvent` (remote and local forms), `admission(schema)`, queries carried over | ported | `Contracts/Evolve.cs` | `contracts.evolve.event-edits-*` (3), `contracts.evolve.160-*` (1), `contracts.declare.160-admission-edits-*` (1) |
| `contracts/fingerprint.ts`: `{ event }` and `{ queries, args }` nodes; admission not in the fingerprint | ported | `Contracts/Fingerprint.cs` | `contracts.fingerprint.event-members-*` (1), the admission cases |
| `contracts/history.ts`: `Change` side `event` | ported | `Contracts/History.cs` (`ChangeSide.Event`) | `contracts.evolve.event-edits-change-add-and-remove-*` |
| `contracts/history.ts`: `VariantShape.events/queries` | no-op: type-level | — | — |
| `contracts/index.ts`: exports `memberNames`, `event`, `queries`, `EventDeclaration`, `QueriesMember`, the event and query interfaces | ported | public types of `PiSharp.Chord.Contracts` (`Contracts/Index.cs` lists the mapping) | `contracts.schema.160-every-member-kind-*` |
| `contracts/index.ts`: type exports `Method`, `LiteralsSchema`, `HostKeyed` | already-ported: `MethodDeclaration`, the `JsonObject` `Schemas.OneOf` returns, and `KeyedMember.KeyHost` were public at 1.5.2 | — | the same case checks all three |
| `contracts/internal.ts`: exports `joinKey`, `Snapshot`, `snapshotArguments` | already-ported: `ContractsInternal.JoinKey`, `Snapshot`, `SnapshotArguments` | header only | — |
| `contracts/params.ts`: `joinKey` copies only parts that need escaping | ported (same key; no copy for a part without `\` or `\|`) | `Contracts/Params.cs` | the existing `contracts.params.*` and `data.params.*` cases (unchanged, all pass) |
| `contracts/schema.ts`: `queries(item, args)`/`QueriesMember`, `event(schema)`/`EventDeclaration` | ported | `Contracts/Schema.cs` (`Schemas.Queries`, `Schemas.Event`) | the declare cases |
| `data/failure.ts`: cause kind `denied` | ported | `Data/Failure.cs` (`FailureKind.Denied`) | `data.failure.160-denied-*`, `link.160-the-message-check-*` |
| `data/principal.ts`: `principalJson` | ported | `Data/Principal.cs` (`DataInternal.PrincipalJson`) | `data.principal.principal-json-*` (3) |
| `data/internal.ts` | exports only | header | — |
| `link/message.ts`: `hello.calls` optional, `ack.events`, `event`/`WireEvent`, `CAUSE_KINDS` + `denied` | ported | `Link/Message.cs` | `link.160.frozen-codec-goldens`, `link.160-the-message-check-*` |
| `link/binary.ts`: kind 18, flag `0x20` on `ack`, `calls` 0 = absent, cause byte 3 | ported | `Link/BinaryCodec.cs` | `link.message.binary-*` (2), the goldens |
| `link/text.ts`: `"h"` with 2 or 3 fields, `"a"` with 0 or 1, `"E"`, cause letter `"d"` | ported | `Link/TextCodec.cs` | `link.message.text-*` (1), `link.message.an-event-keeps-a-deflated-part-*`, the goldens |
| `link/channel.ts`: optional window both ways, counted pings and pongs, `fence(then)` | ported | `Link/Channel.cs` | `link.channel.a-fence-*` (2 cases, each for both codecs), `link.160-a-fence-asked-before-the-handshake-*`, `link.160-a-channel-without-a-call-window-*` |
| `link/internal.ts` | exports only | — | — |
| `state/derive.ts`: `invalidate` lists a watched derive once per batch | ported (the 1.5.2 port had the bug) | `State/Derive.cs` | `state.derive.read-between-two-changes-*` (5) |
| `state/framed.ts` (new) | ported | `State/Framed.cs` (new) | `state.framed.*` (8 upstream, 4 authored) |
| `state/internal.ts` | exports only | header | — |
| `telemetry.ts`: counters `chord.query.probes/skips/rows`, `chord.events.emitted/sent/dropped/encodes`, span event `chord.admit` on `chord.relay`, `chord.hold`, `chord.activate` | ported as schema rows and two helpers; nothing emits them yet | `Telemetry.cs` | `telemetry.160.*` (3), `telemetry.152.schema-and-cached-instruments` (count 25 -> 32) |
| `*/README.md` | no-op: upstream documentation | — | — |

### Upstream test cases

| Upstream test file | New at 1.6.0 | Ported | Not ported, and why |
|---|---|---|---|
| `test/contracts/accepts.test.ts` | 4 | 4 | — |
| `test/contracts/contract.test.ts` | 3 | 3 | — |
| `test/contracts/declare.test.ts` | 9 | 8 | "type the implementation as one named query per listed name": only `expectTypeOf` lines, no run-time assertion. The `expectTypeOf` lines inside the ported cases (`AdmittedOf`, `EventMember<T>`) are dropped the same way |
| `test/contracts/evolve.test.ts` | 3 | 3 | the two `expectTypeOf` lines |
| `test/contracts/fingerprint.test.ts` | 1 | 1 | — |
| `test/contracts/schema.test.ts` | 1 | 0, one authored counterpart | It runs `tsc` on a contract and checks the emitted `.d.ts` names every type through Chord's entry point. There is no declaration emit in C#; `contracts.schema.160-every-member-kind-is-declared-and-named-through-public-types` declares the same two contracts through public types and checks each named type is public |
| `test/data/principal.test.ts` | 3 | 3 | — |
| `test/link/channel.test.ts` | 2 (each under `forEachCodec`) | 2 x 2 codecs | — |
| `test/link/message.test.ts` | 0 new `it`, new assertions in 4 existing ones and 6 new `EVERY_KIND` rows | all | The rows are in the 1.6.0 goldens (the 53 frames of the 1.5.2 fixture, these 6, and 3 combinations added by the generator); the assertions are four `link.message.*` cases |
| `test/state/derive.test.ts` | 5 | 5 | — |
| `test/state/framed.test.ts` | 8 | 8 | — |
| `test/telemetry.test.ts` | 0 (one changed expectation) | 0 | The changed case ("an object's first hold is a root linked to its getter") is shared holds: `chord-remote` |

Authored cases (no upstream counterpart), all named with `160`: the run-time checks that stand in for upstream's types (`contracts.declare.160-admission-edits-are-checked-when-they-run`, `contracts.evolve.160-event-edits-of-the-other-kinds-form-are-refused-when-they-run`), the link order of event members, the message check for the new fields, two channel cases (a fence asked before the handshake and one that cancels or asks another while it runs; a channel without a call window), four framed-source cases (reads before the first value and after the end, `Quiet`, the `Diff` seam, one frame per commit through a real watch), `data.failure.160-denied-is-a-cause-kind-of-its-own`, and three telemetry cases.

No existing case was weakened or deleted. One expectation changed because upstream changed it: the metric count in `telemetry.152.schema-and-cached-instruments` (25 -> 32).

## 2. Suites

Pinned SDK, `-c Release`, commands as in the plan file. Builds: `src/PiSharp.Chord.Hosting`, `tests/PiSharp.Chord.Tests`, `tests/PiSharp.Chord.Hosting.Tests`, `tests/PiSharp.Chord.Durable.Integration.Tests`: 0 errors, 0 warnings each.

| Suite | Base `abac6b2c` | This branch |
|---|---|---|
| `PiSharp.Chord.Tests` (no arguments) | 1,024 cases, 1,024 passed, 0 failed | 1,083 cases, 1,083 passed, 0 failed (the 1,024 of the base and 59 new) |
| `PiSharp.Chord.Hosting.Tests` (no arguments) | 46 cases, 46 passed, 0 failed | 46 cases, 46 passed, 0 failed |
| `PiSharp.Chord.Durable.Integration.Tests` | 7 passed, 0 failed | 7 passed, 0 failed |

Neither Chord suite has a skip status, and the integration suite skipped nothing: 0 skipped everywhere. No case failed on the base or on this branch, so there is no failure to compare. The base runs were made from this worktree at `abac6b2c` before any edit. The runs on this branch were made from the tree that the five source commits hold (the commits were made after the run, with no source change in between).

The 59 new cases: 23 contracts, 4 data, 12 link, 17 state, 3 telemetry. 44 port upstream cases or assertions (the goldens case among them) and 15 are authored (section 1, the `schema.test.ts` counterpart included).

Not run: every other suite in the repository. Nothing outside `src/PiSharp.Chord` was changed and the only public-surface changes are additions (section 5 lists the one that is not), but `PiSharp.Durable.Tests`, the benchmarks and the samples were neither built nor run here.

## 3. The API later packages start from

All of it is in `PiSharp.Chord` (assembly-level `[Experimental("PISHARP_CHORD001")]`), so every public type below is experimental as the rest of Chord is.

### 3.1 Contracts (`namespace PiSharp.Chord.Contracts`)

```csharp
// Declaring
public static QueriesMember Schemas.Queries(JsonNode item, params (string Name, JsonNode Args)[] queries);   // local contracts only
public static EventDeclaration Schemas.Event(JsonNode schema);                                               // a local contract's marker
public sealed class QueriesMember { public JsonNode Item { get; } public IReadOnlyDictionary<string, JsonNode?> Args { get; } }
public sealed class EventDeclaration { public JsonNode Schema { get; } }
public readonly struct Member            // + implicit conversions from EventDeclaration and QueriesMember
public sealed class RemoteDeclaration    { /* ... */ public JsonNode? Admission { get; init; } }             // never in the fingerprint
public sealed class VariantDeclaration   { /* State, Methods */ public OrderedDictionary<string, Member> Events { get; init; } }

// Evolving (ContractEditor): the remote forms name a variant, the local ones do not
public ContractEditor Event(string variant, string member, JsonNode? schema);    public ContractEditor Event(string member, JsonNode? schema);
public ContractEditor AddEvent(string variant, string member, JsonNode? schema); public ContractEditor AddEvent(string member, JsonNode? schema);
public ContractEditor RemoveEvent(string variant, string member);                public ContractEditor RemoveEvent(string member);
public ContractEditor Admission(JsonNode? schema);   // remote only; the last edit of a version (an edit after it throws)

// Accepting older versions
public OrderedDictionary<string, StateEntry> VersionMappings.Events { get; init; }   // by variant (StateSection) for remote, flat for local

// Reading a contract
public sealed record MemberNames(IReadOnlyList<string> State, IReadOnlyList<string> Methods, IReadOnlyList<string> Events);
public MemberNames Contract.MemberNames(string? variant = null);      // declaration order; null is a local contract's one variant

// What implementations hand out (declared here, implemented by chord-remote and chord-queries)
public sealed record QueryPage(IReadOnlyList<JsonNode?> Items, string? Next = null);
public interface INamedQuery { Task<QueryPage> PageAsync(JsonNode? args, long limit, ChordContext context, PageOptions? options = null); }
public sealed record ChordEvent(JsonNode? Value, long At);            // At: the emitting host's Unix milliseconds
public interface IEventSubscription { void Stop(); }
public interface IEventMember { Task<IEventSubscription> OnAsync(Action<ChordEvent> listener, ChordContext context); }
public interface IEventSource : IEventMember { void Emit(JsonNode? value); }
```

Internal, for the host and remote layers:

```csharp
internal enum MemberKind { State, Method, Keyed, Event, Queries }
internal enum ChangeSide { State, Args, Result, Keyed, Event }
internal sealed record ContractData(/* Kind, Name, Params, HostAddress, Versions, Kinds */, AdmissionData? Admission = null);
internal sealed record AdmissionData(Snapshot Schema, string Fingerprint);            // Fingerprint: of the admission schema alone
internal sealed record VariantData(State, Methods, Keyed,
    IReadOnlyDictionary<string, Snapshot> Events, IReadOnlyDictionary<string, QueriesData> Queries);
internal sealed record QueriesData(Snapshot Item, IReadOnlyDictionary<string, Snapshot> Args);   // Args closed, declaration order
internal sealed record VariantNames(State, Methods, IReadOnlyList<string> Events);    // sorted: the index a WireEvent.Member is
ContractsInternal.VariantNamesOf(VariantData)      // variants without events share one empty Events list
ContractsInternal.MemberNamesOf(Contract, string)  // declaration order
ContractsInternal.AdmissionDataOf(object? schema, string path)   // JSON only
ContractsInternal.EventSnapshot(object? schema, string path)     // JSON only
ContractsInternal.ReadQueries(QueriesMember, string path)
ContractsInternal.JoinKey(IEnumerable<string>)     // unchanged key; the sharing key of shared holds uses it
```

Points that differ from the discovery report's section 3.5:

- **The event and query interfaces live in `Contracts`**, not in the host. Upstream declares `EventMember`, `EventSource`, `EventSubscription`, `NamedQuery` and `QueryPage` in `contracts/contract.ts` and exports them from `contracts/index.ts` (upstream wins). `chord-remote` and `chord-queries` implement them and add `ISelf.Events()`, `IReference.Event(name)` and the query members to `Host/Contracts.cs`; they should not declare these types again.
- **An event mapping is a `StateMapping`.** `VersionMappings.Events` reuses `StateEntry`, `StateSection` and `StateMapping` instead of three new types with the same shape (a function from the current value to the old one, or `Refuse`). `chord-remote`'s `Adapted` event mappings read `StateMapping.Map` and `.Refuses`.
- **`Admission(schema)` returns the editor and seals it.** Upstream makes it the last edit of a version by returning no editor. `Contract.Evolve` needs its editor back, so here every edit made after `Admission` throws `"<name> v<N>: admission(schema) is the last edit of a version"`, and `Admission` on a local contract throws. This is one of the two decisions the discovery report left open; a terminal editor type would need a second `Evolve` overload. Say so if a different shape is wanted before `chord-admission` builds on it.
- **`MemberKind` order** is upstream's (`Event` before `Queries`).

What is left for later packages in this layer: nothing. No later package should need to edit `Contracts/**`. Until they land, the host treats the new members like this: `PluginStaging.ValidateImplementation` (`Host/Staging.cs`) checks state, keyed and method members only, so a contract with event or `queries` members can be declared and served, an implementation that supplies such a member is refused ("not a member of the implementation variant"), and one that leaves them out is accepted.

### 3.2 Data (`namespace PiSharp.Chord.Data`)

```csharp
public enum FailureKind { Transient, Broken, Waiting, Denied }        // the wire's cause kinds, in code order
internal static string DataInternal.PrincipalJson(JsonObject principal, ChordContext context);   // JSON.stringify's text, cached per object, one chord.data.walks per object
```

`Remote/Hold.cs` writes a cause as `kind.ToString().ToLowerInvariant()` and `Remote/RemoteObject.cs` reads one with `Enum.Parse<FailureKind>(..., true)`, so `Denied` already crosses a link in both directions. Nothing in the host treats a `Denied` cause specially yet: that is `chord-admission`. The host has no `switch` over `FailureKind`, but it compares kinds, and a `Denied` cause falls on the "neither waiting nor transient" side of each comparison: `Host/Object.cs:105` and `:115` (no backoff, which is right), `Host/Object.cs:238` (a wake starts a new attempt for any failed state that is not `Waiting`, so a denied one too) and `Host/Reference.cs:97` (only `Broken` throws).

### 3.3 Link (`namespace PiSharp.Chord.Link`, all internal)

```csharp
internal sealed record HelloMessage(long Protocol, string Incarnation, long? Calls = null) : Message("hello");   // null: no window
internal sealed record AckMessage(long? Events = null) : Message("ack");                                         // null: a state ack
internal sealed record EventMessage(IReadOnlyList<WireEvent> Events) : Message("event");                         // Origin allowed
internal sealed record WireEvent(long Hold, long Member, long At, Part Value);
internal static readonly string[] MessageCheck.CauseKinds = ["transient", "broken", "waiting", "denied"];
internal sealed record ChannelOptions(ICodec Codec, string Incarnation, long? Calls, bool Trusted, Action<Exception> Report, ...);
internal long? Channel.PeerCalls { get; }       // null: the peer announced no window; also null before the channel opened
internal Action Channel.Fence(Action then);     // returns the cancel
```

- Protocol version stays 1. Every frame of `tests/fixtures/chord-link-v152.json` still decodes and re-encodes byte for byte.
- Binary: kind byte 18 is `event`; `0x20` on kind 13 is an event ack whose count follows; `0x20` on any kind but `call` and `ack` is `"binary: an unknown kind"` (it was a message-schema error before; upstream's message wins). Text: `["E",[[hold,member,at,part]...]]`, `["a",n]`, `["h",protocol,incarnation]`.
- `MessageCheck.Check` covers the new fields: `calls` and `events` at least 1 when present, the entries' `hold`, `member` and `at` at least 0, the four cause kinds.
- **`Fence`.** A ping's number is taken and its bytes are written under one lock, and the handshake's queued messages are flushed under the same lock, so the nth ping on the wire is ping n even with `Tick` on another thread. `then` runs where the channel handles what it receives (the link's delivery, like `IChannelHandler`), outside the channel's locks: the caller moves to its dispatcher as `HostLink` does for messages. A fence asked of a closed channel returns a no-op cancel and never runs. A `then` may cancel a later fence or ask a new one.

What is left for `chord-remote` here, and it is the first thing to do because the wire now carries shapes the remote layer does not route:

1. `Remote/HostLink.cs` still passes `256` as `ChannelOptions.Calls` for every link. Upstream: `trusted ? undefined : callWindow`.
2. `HostLink.MessageAsync` has no case for `EventMessage`: one that arrives is dropped. It routes every `AckMessage` to the provider, where `Ack()` counts it as a state ack: an `AckMessage` with `Events` set must go to the event window instead. Neither can happen between two native hosts today (nothing sends `event`), but a 1.6.0 TypeScript peer can send both.
3. `Consumer.Opened(long)` now gets `channel.PeerCalls ?? long.MaxValue` (the one compile fix, section 5): a peer without a window is not limited. `chord-remote` replaces this with its `CallWindow<T>`.

### 3.4 State (`namespace PiSharp.Chord.State`, all internal)

```csharp
internal interface IFramedOwner { string Label { get; } void Report(Exception error); }
internal abstract class FramedSource : Source
{
    protected FramedSource(Batch batch, IFramedOwner owner);
    public bool Ready { get; }                 // has a value
    public Exception? Failure { get; }         // while failed
    public bool Detached { get; }              // its owner ended
    public override JsonNode? Value { get; }   // not_ready "<label>: read before its first value" before the first; the failure or the owner's error otherwise
    public override WatchStart Start();        // failed: the failure and the last good value
    public override void Deliver(ChordContext context);
    protected void Set(JsonNode? next);
    protected void FailWith(Exception failure);
    protected void Quiet();                    // the next failure is not reported
    protected void End(Exception ended);
    protected virtual ImmutableArray<Op> Diff(JsonNode? before, JsonNode? after);   // default: DeltaDiff.DiffRevisions
}
```

`IFramedOwner.Report` takes an `Exception`, not the `HostError` the discovery report proposed: upstream's owner gets the raw error and wraps it itself (`query_failed`), and the state layer does not know the host's error type (upstream wins).

**The differ contract** (the discovery report's risk 5.1.6). Upstream's frame is `diffRevisions(before, after)` and gets a narrow diff because unchanged rows are the same JavaScript objects. A `JsonNode` has one parent, so two values of a framed source share no node. The base class therefore diffs structurally by default, as a derive does, and gives the subclass a seam. The file comment of `State/Framed.cs` states the contract; in short:

1. Every value given to `Set` is a tree of its own. No node of it is, or later becomes, part of another value given to `Set`. A subclass that carries rows from one value to the next copies them (`DeepClone`) or builds them again.
2. A value given to `Set` is never changed afterwards. The source keeps it by reference as what its watches have, hands it to watches and subscribers by reference, and passes it back as `before`.
3. `Diff(before, after)` is called by `Deliver`, at most once per batch, only when the batch left a value and the watches had one. Both arguments are the very references given to `Set`: `after` the batch's last, `before` the one current when the batch first changed the source while watched. A subclass can key its own bookkeeping (row tokens, decoded rows) by these references, weakly, and return ops without walking the trees.
4. The ops, applied to `before`, give a value deeply equal to `after`; no ops mean no frame. A throw is reported by the batch as a failed delivery and the watches miss the frame.

After a failure (or before any watch had a value) the frame is `[["r", value]]` and `Diff` is not asked. `state.framed.160-a-subclass-diff-gets-the-values-it-set-by-reference-and-its-ops-are-the-frame` pins points 2 to 4.

**Threads.** A framed source belongs to its batch's host and is not thread-safe, like every source. `Set`, `FailWith`, `Quiet` and `End` are called inside the batch (`Batch.Commit` or `Batch.Within`, which moves to the owner's thread when the host installed one). A store that finishes a read on another thread goes through `Batch.Within` before it sets the source. `Deliver` and `Diff` run inside the same commit on the same thread. `chord-store` writes the first subclass (`LiveQuery`); if this contract does not fit what its row decoding needs, it should change `State/Framed.cs` and say so in its report.

**The derive fix** changes no signature: `Derive.Invalidate` lists a watched derive for the batch's frame at its first marking only and keeps the value its watches had, so a derive read between two changes of one commit publishes one frame, diffed from the value before the commit.

### 3.5 Telemetry (`ChordTelemetry`, public)

```csharp
// schema rows (counters): chord.query.probes, chord.query.skips, chord.query.rows,
//   chord.events.emitted, chord.events.sent, chord.events.dropped { reason: window | too_large | mapping }, chord.events.encodes
// span event chord.admit { outcome } on chord.relay, chord.hold, chord.activate
public static void ChordTelemetry.Admitted(ITelemetrySpan span, bool allowed);        // adds chord.admit with allow or deny
public static SpanAttributes ChordTelemetry.EventsDropped(string reason);             // the attributes of one chord.events.dropped count
```

Emitters call `context.Count("chord.query.probes")`, `ChordTelemetry.Metrics(metrics).Counter("chord.events.dropped").Add(1, ChordTelemetry.EventsDropped("window"))` and `ChordTelemetry.Admitted(span, allowed)`. `chord-store`, `chord-remote` and `chord-admission` should not need to edit `Telemetry.cs`. Version constants in that file were not touched.

## 4. Deferred

Nothing in this package's area is deferred. What the area does not contain, and who has it:

- Emitting and receiving events, call windows by trust, shared holds, the new expectation of the re-hold telemetry case: `chord-remote`.
- `self.Queries`, `LiveQuery`, lists, the query counters' emission: `chord-store` and `chord-queries`.
- Deciding an admission, the `denied` paths, the consumers of `Channel.Fence` and `ChordTelemetry.Admitted`: `chord-admission`.

## 5. Edits outside the owned paths

One, to keep the solution compiling after `Channel.PeerCalls` became `long?`:

- `src/PiSharp.Chord/Remote/HostLink.cs` line 44: `Consumer.Opened(channel.PeerCalls)` -> `Consumer.Opened(channel.PeerCalls ?? long.MaxValue)`.

The positional records `VariantData` and `VariantNames` gained fields, but nothing outside `Contracts/**` constructs them, and nothing switches over `FailureKind` (the host compares single kinds, see 3.2), so the other two fixes the discovery report expected were not needed.

`tests/PiSharp.Chord.Tests/ChordSupport.cs` and both `Program.cs` files are untouched. New helpers are in `tests/PiSharp.Chord.Tests/Sync160FoundationSupport.cs`.

## 6. For root

- **New files** for the integrity table and the source inventory: `src/PiSharp.Chord/State/Framed.cs`; `tests/PiSharp.Chord.Tests/Sync160FoundationSupport.cs`, `Telemetry160Cases.cs`, `Contracts/Sync160ContractsCases.cs`, `Data/Sync160DataCases.cs`, `Link/Sync160LinkCases.cs`, `Link/generate-goldens160.mjs`, `State/Sync160DeriveCases.cs`, `State/Sync160FramedCases.cs`; `tests/fixtures/chord-link-v160.json` (62 frames, written with `\n`, generated by Node v24.22.0 from the frozen v1.6.0 checkout).
- **Headers.** The files brought to 1.6.0 now name `faac585884ae2c013a6b1400f3e9e77f10b34bac` and the 1.6.0 path (`packages/chord/src/contracts/...`, no longer `src/next/...`). Files in the owned directories with no 1.6.0 change keep their old header: `Contracts/{HostKind,Names,Snapshot}.cs`, `Data/{Args,Errors,Index,Params,Validator}.cs`, `Link/{Link,Part,RawDeflate,Transports}.cs`, `State/{Batch,Index,NativeSource,Watchable,Writable}.cs`.
- **Changelog lines.** Added (experimental): event members and `queries` members in contracts, a remote contract's admission schema, `Contract.MemberNames`, `FailureKind.Denied`, the interfaces `INamedQuery`, `IEventMember`, `IEventSource`, `IEventSubscription`. Fixed: a watched derive read between two changes of one commit published two frames for that commit. Changed, binary-breaking and source-compatible: `FailureKind` gained a member. The wire note of the discovery report's 5.3 (a 1.6.0 trusted side sends `hello` without `calls`) becomes true for PiSharp only when `chord-remote` changes `HostLink`; after this package a PiSharp host still announces 256 and accepts a peer that announces nothing.
- **`ChordNext.Commit`** still names the 1.5.2 commit and the suite's report prints it as `sourceSha`. It is a version constant (the `meta` package's); the new case files name the 1.6.0 commit in their headers.

## 7. Open questions for the packages that follow

1. `chord-remote`: is an event mapping as a `StateMapping` enough, or does `Adapted` need the old and current schemas beside it? The snapshots are in `VariantData.Events` per version, so nothing in `Accepts.cs` should have to change.
2. `chord-remote`: `WireEvent.At` is a `long` (Unix milliseconds) and `ChordEvent.At` the same. If the host's clock is a `TimeProvider`, `GetUtcNow().ToUnixTimeMilliseconds()` gives upstream's `Date.now()`.
3. `chord-store`: does `LiveQuery` want `Diff` to see more than the two root references (for example the batch's context)? It runs inside `Deliver(context)`; the seam passes only the values because upstream's does. Adding the context is a one-line change in `Framed.cs`.
4. `chord-admission`: `AdmissionData.Fingerprint` is the fingerprint of the admission schema's normal form (`ContractsInternal.FingerprintOf`), what upstream's staging compares on a reload. The admitted value itself is never validated, as upstream says; if the host wants a run-time check anyway, `AdmissionData.Schema.Schema` is the JSON Schema to compile.
5. `chord-admission`: `Channel.Fence` runs `then` on the link's delivery path. If a fence must run in order with handled messages on the host's dispatcher, post it there from `then`; the channel does not know the dispatcher.
6. Root: the `Admission(schema)` shape in 3.1 (a sealing editor) is the one decision here that is cheap to change now and awkward later.
