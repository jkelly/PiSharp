# chord-store: Chord 1.5.2 -> 1.6.0, the store's reads, column sets and live queries

Work package `chord-store` of the Pi 1.6.0 sync, 2026-11-21. Branch `sync/1.6.0-chord-store`, from `9aa79a22` (the merge of `chord-foundation`). Upstream v1.6.0 is `E:/AI/PiSharp/upstream-v160` (`faac585884ae2c013a6b1400f3e9e77f10b34bac`), v1.5.2 is `E:/AI/PiSharp/upstream-v152`. The plan was `AI_DOCS/agent-reports/sync-1.6.0-chord-discovery.md`; the layer under it is `AI_DOCS/agent-reports/sync-1.6.0-chord-foundation.md`. Where this report says "upstream wins", a report's proposal was changed after reading the source.

This package ports the store half of slice 14: column sets, read plans, the read index and the commit-time skip, the `LiveQuery` rewrite and `self.Query` over it, and the smaller store changes (`SettledAsync`, `OnCommitted`, delete host data by plugin, `query_failed` carrying `Query`). Named queries, `LiveQuery`/`Count`/`List` on `ISelf`, lists and tiling are `chord-queries`; section 3 is what it starts from.

## 1. Upstream changes in this area

Status is one of `ported` (code and a case that ran), `already-ported`, `no-op` or `deferred`, as the plan file defines them.

| Upstream | Status | C# | Cases |
|---|---|---|---|
| `store/column-set.ts` (new): `defineColumns`, `ColumnSet`, `checkExposes`, `checkExposedOnce`, `sameColumn`, `indexPath` | ported | `Store/ColumnSet.cs` (new) | `store.column-set.*` (7), `host.host-data.160-one-object-exposing-a-set-by-two-tables-*` |
| `store/expression.ts`: `exists`, `existsNode`, `Match`, `ColumnRef.set`, `WhereNode` `exists`, scopes (`tableScope`, `setScope`, `joinedScope`, `partnerName`), `compile`, `compileCondition` with `Expected`, `compileExists`, `columnsOf`, `existsIn`, `checkMatch`, `checkPair`, `checkNode`, `NODE_DEPTH` | ported | `Store/Expression.cs` | `store.read-plan.exists-*`, `store.read-plan.exists-node-and-where-node-*`, `store.read-plan.checked-when-it-opens-*`, `store.expression.160-*` |
| `store/query.ts`: `join`, `whereNode`, two-argument `where`/`orderBy`, `joined`, `expected`, `column`, `renamed`, `decodeCursor(..., column)` | ported | `Store/Query.cs` | `store.read-plan.join-*`, `store.query.160-*`, `store.page.160-*` |
| `store/sql.ts`: `ConditionSql`, `whereSql(node, params, render)`, `exists` rendered by the read | ported | `Store/Sql.cs` | every `store.read-plan.*` case that runs SQL, `store.evaluate.160-*` |
| `store/evaluate.ts`: `exists` is refused in memory | ported | `Store/Evaluate.cs` | `store.evaluate.160-exists-is-not-evaluated-in-memory` |
| `store/table.ts`: `TableOptions.exposes`, `TableDefinition.exposes` (not in the fingerprint) | ported | `Store/Table.cs` | `store.column-set.exposes-*` (3) |
| `store/table-evolve.ts`: `evolveTable(previous, edit, { exposes })`, `EvolveOptions` | ported | `Store/TableEvolve.cs` | `store.column-set.evolve-table-*` (2) |
| `store/rows.ts`: `decodeScalar` extracted | ported | `Store/Rows.cs` (`StoreInternal.DecodeScalar`) | every case that decodes a row (the existing `store.rows.*`, `store.table-handle.*`) |
| `store/read-plan.ts` (new): `ReadPlan` (`select`, `count`, `probe`), `Bound`, `Bindings`, `Projection`, `Use`, `Position`, `Range`, `StoredRead` | ported | `Store/ReadPlan.cs` (new) | `store.read-plan.*` (21) |
| `store/read.ts` (new): `Read`, `ReadRun`, `ReadResult`, `ReadSink`, `ReadKind`, `identity`, `identityOf` | ported | `Store/Read.cs` (new) | `store.reads.*`, `store.skip.*` |
| `store/reads.ts` (new): `Reads` (index by table, bindings, hold/ready, watchers, retire, `onHostKind`), `setsOf`, `ReadRefresh` | ported | `Store/Reads.cs` (new) | `store.reads.*` (12), `store.live-query.a-refresh-applied-again-*` |
| `store/skip.ts` (new): `skips`, `Change`, `PROBES_PER_READ` | ported | `Store/Skip.cs` (new; `Change` is `TableChange`) | `store.skip.*` (15) |
| `store/open-query.ts` (new): `OpenQuery`, `OpenRead`, `QuerySpec`, `ReadSite` | ported | `Store/OpenQuery.cs` (new) | `store.reads.*`, `store.skip.*`, `store.live-query.*` |
| `store/page.ts` (new): `readPage`, `StoredPage` | ported | `Store/Page.cs` (new) | `store.page.160-*`, the existing `store.table-handle.*` |
| `store/live-query.ts`: `LiveQuery extends FramedSource`, `LiveTarget`, `LiveShape`, `DecodedRows`, `checkLiveQuery`; `LiveQueries`, `QueryRefresh`, `refreshOf` removed | ported (a rewrite) | `Store/LiveQuery.cs` | `store.live-query.*` (24) |
| `store/table-changes.ts`: `changes()` | ported | `Store/TableChanges.cs` (`Changes()`) | `store.skip.*`, `documents.transaction.160-*` |
| `store/table-handle.ts`: `page`/`count` through a plan, a joined query refused, `TableOwner.queries` removed | ported | `Store/TableHandle.cs` | `store.read-plan.a-table-handles-own-reads-*`, the existing `store.table-handle.*` |
| `store/store.ts`: `reads` | ported | `Store/Store.cs` (`ChordStore.ReadIndex`, `IChordStore.ReadIndex`) | every reads case |
| `store/store.ts`: `settled()` | ported | `Store/Store.cs` (`ChordStore.SettledAsync`) | `store.store.settled-*` (2) |
| `store/store.ts`: `Store.onCommitted(listener, onError)` | ported | `Store/Store.cs` (`IStore.OnCommitted`) | `store.store.on-committed-*` (3) |
| `store/step.ts` (no upstream change) | ported support | `Store/Step.cs` (`Step.Transaction`, see 4.3) | `store.live-query.160-refused-step-adopts-nothing` |
| `store/host-data.ts`: `deleteHostData(..., owners?)`, `removeHostData(..., owners?)`, `tableOwner` | ported | `Store/HostData.cs` | `host.host-data.a-delete-limited-to-one-plugin-*` (3) |
| `store/index.ts` | exports only | `Store/Index.cs` (comment) | — |
| `store/README.md` | no-op: upstream documentation | — | — |
| `documents/transaction.ts`: `#refreshQueries` stages `ReadRefresh` for the tables written, whether or not a read exists | ported | `Documents/Transaction.cs` | `documents.transaction.160-*`, `store.live-query.publication-*` |
| `host/host-data.ts`: the refresh through `store.reads.onHostKind`; `deleteFrom(..., only)` | ported | `Host/HostData.cs` | `host.host-data.a-delete-limited-to-one-plugin-*`, the existing `host.host-data.import-replace-delete-*` |
| `host/attempt.ts` (store hunks): `table` checks `checkExposedOnce`; `query` rebuilt over `LiveQuery`; `#prepareTables` holds, `#bindSets`; the query load; `detach` retires the handles' reads | ported | `Host/Attempt.cs` (four small hunks) and `Host/HostData.cs` (`Self.Query`, `HoldTables`, `BindSets`, `LoadQueriesAsync`, `RetireReads`) | `store.live-query.*` (the host cases), `host.host-data.160-*` (3) |
| `host/object.ts` (store hunks): `liveQueries` removed, `unbindSets`, called by `remove` | ported | `Host/Object.cs`, one line in `Host/ReloadObject.cs` (`RemoveForReload`) | `host.host-data.160-a-column-set-is-bound-*`, `host.host-data.160-a-reload-*` |
| `host/host.ts` (store hunks): `query_failed { query }`, close calls `unbindSets`, `deleteHostData(..., { plugin })`, `HostDeleteOptions` | ported | `Host/Contracts.cs`, `Host/Host.cs`, `Host/HostData.cs` | `store.live-query.failure-*`, `host.host-data.*` |
| `host/staging.ts` (store hunk): `checkExposedOnce` on recorded tables | ported | `Host/Staging.cs` | `host.host-data.160-one-object-exposing-a-set-by-two-tables-*` |
| `telemetry.ts`: `chord.query.probes`, `chord.query.skips`, `chord.query.rows` emitted | ported (the rows are the foundation's) | `Store/Skip.cs`, `Store/Reads.cs`, `Store/Read.cs` | `store.skip.*` (exact counts), `store.live-query.publication-a-commit-writing-another-table-*` |

### Upstream test cases

| Upstream test file | New or changed at 1.6.0 | Ported | Not ported, and why |
|---|---|---|---|
| `test/store/column-set.test.ts` (new) | 7 | 7 | — |
| `test/store/read-plan.test.ts` (new) | 21 | 21 | — |
| `test/store/reads.test.ts` (new) | 12 | 12 | — |
| `test/store/skip.test.ts` (new) | 8 `it` (the oracle runs for seeds 1 to 8: 15 runs) | 8 (15 cases) | — |
| `test/store/live-query.test.ts` | 12 `it` (the declaration refusals run for 5 rows: 16 runs); 3 changed bodies | 12 (14 cases) | Two of the five refusal rows: "no limit" (`undefined`) and "a fractional limit" (`2.5`). The limit is a `long`; neither can be written. The limit `0` row covers the check |
| `test/store/store.test.ts` | 5 new | 5 | — |
| `test/host/host-data.test.ts` | 3 new | 3 | — |
| `test/documents/transaction.test.ts` | 0 | — | Unchanged upstream; one authored case pins the 1.6.0 change in `transaction.ts` |
| `test/support.ts` | helpers | as the cases need them | `Sync160StoreSupport.cs`; the fixtures of the host tests of `chord-queries` and `chord-admission` (`Match`, `Ticker`, `Desk`, `recorder`, `until`, `waitLive`) are theirs |

Authored cases (no upstream counterpart), all with `160` in their id: ten `store.live-query.160-*` (the seven 1.5.2 ones rewritten over the store's reads; one frame per commit when a value is set several times in it; a later read switching in one frame and `Fail` closing its reads; the `{ rows, more }` and count shapes), `store.expression.160-*`, `store.query.160-*`, `store.evaluate.160-*`, `store.page.160-*`, three `host.host-data.160-*` (a set bound at activation and read by another object, dropped by a reload and at close, exposed twice) and `documents.transaction.160-*`. 18 in all.

### Where a ported case differs from upstream

1. **`EXPLAIN QUERY PLAN` (three `read-plan` cases).** They assert upstream's regular expressions unchanged, and pass: the SQLite bundled with Microsoft.Data.Sqlite prints the same plan text as node:sqlite for these statements (`SEARCH t ...; SEARCH p USING PRIMARY KEY`, `SEARCH t USING PRIMARY KEY (team=? AND session=?)`, `SEARCH t USING PRIMARY KEY (id=?)`). No property had to stand in for the text.
2. **The skip oracle.** `Sync160Random` is mulberry32 step for step, and the writes draw from it in upstream's order, so a seed writes the same rows. Upstream queues one to three commits together and its store writes them as one backend transaction (group commit, S18). The native store has no group commit: it was not ported at 1.5.2 and is not part of this delta (see 4.4). The same commits run one transaction each; the oracle's assertion (every adopted result equals a fresh run after every commit) is unchanged and holds for all eight seeds, and so do upstream's lower bounds on the three counters. The deterministic cases assert upstream's exact `{ runs, probes, skips }` triples.
3. **Row identity ("keeps an unchanged row the same object across runs").** Upstream compares two values' row objects by `===`. A `JsonNode` has one parent, so two values share no node (the foundation's differ contract). The case asserts what it is about on the frame (the changed row alone is diffed: `[["s",[0,"text"],"T2"]]`) and on the value before (untouched), and `store.live-query.160-decoded-rows-*` asserts the reuse itself on `DecodedRows` (the same `DecodedRow` instance and token for an unchanged stored row).
4. **Frames are read at the live query, not at the reference's member (three publication cases).** The native `ReferenceMember` (`Host/Reference.cs`, not this package's) relays values and diffs them again, and the first frame it makes after it was made or retargeted is `["r", value]` whatever its source published. Upstream's expected ops are asserted on the member's source in its attempt; the values are read through the reference. See 5.2: this is a 1.5.2 trait worth a decision.
5. **A condition as data** is the JSON of a compiled `WhereNode`; the cases pass JSON text where upstream writes an object literal.
6. **`onCommitted`** is announced on the thread pool, not in a microtask; the cases wait for the announcement instead of awaiting `Promise.resolve()`.
7. **"a refresh applied again"** stubs a `Read` upstream; here a real `Read` over a counting executor is applied twice (`Read` is sealed).
8. **The delete-by-plugin fixture** serves the board and the ledger from two facets instead of one facet with a filtering `serve`; the view, which none of the three cases uses, is left out.

No existing case was weakened. Seven were replaced: the 1.5.2 `store.live-query.*` cases were authored against `LiveQueries`, `QueryRefresh` and `StoreInternal.RefreshOf`, which 1.6.0 removes; each is rewritten against the new API with the same assertions where the behaviour still exists (see the header of `Store/LiveQueryCases.cs`). One expectation changed because upstream changed it: a commit that touches a live query's table costs one query and **two** statements more than one that does not (the probe; upstream's own cost guard changed from one).

## 2. Suites

Pinned SDK, `-c Release`, commands as in the plan file.

The final runs are recorded here when the last commit is made; the runs made while the work was in progress had no failure.

## 3. What `chord-queries` builds on

Everything here is `internal` to `PiSharp.Chord` unless marked public. Names differ from the discovery report's 3.5 where noted.

### 3.1 Public (experimental, `PISHARP_CHORD001`)

```csharp
// Store/ColumnSet.cs
public sealed class ColumnSetOptions { Columns; required Key; Indexes; }
public static ColumnSet Tables.DefineColumns(string name, ColumnSetOptions options);
public sealed class ColumnSet { string Name; IReadOnlyDictionary<string, Property> Columns; IReadOnlyList<string> ColumnNames;
    IReadOnlyList<string> Key; IReadOnlyDictionary<string, IReadOnlyList<string>> Indexes; IReadOnlyDictionary<string, ColumnRef> Refs; }
public ColumnSet? TableOptions.Exposes { get; init; }     public ColumnSet? TableDefinition.Exposes { get; }
public sealed class EvolveOptions { ColumnSet? Exposes; bool ExposesNone; }     // upstream: ColumnSet | "none"
public static TableDefinition Tables.EvolveTable(TableDefinition previous, Func<TableEditor, TableEditor> edit, EvolveOptions? options = null);

// Store/Expression.cs
public string? ColumnRef.Set { get; }
public sealed record WhereNode.Exists(string Set, IReadOnlyList<ExistsPair> Pairs, WhereNode? Inner);
public readonly record struct ExistsPair(string SetColumn, string Column);
public static IReadOnlyDictionary<string, ColumnRef> Expressions.Match(params (string SetColumn, ColumnRef Column)[] pairs);
public static Condition Expressions.Exists(ColumnSet set, IReadOnlyDictionary<string, ColumnRef> match, Func<Refs, Condition>? inner = null);
public static Condition Expressions.ExistsNode(ColumnSet set, IReadOnlyDictionary<string, ColumnRef> match, JsonNode? inner);   // a trust boundary
public static Condition Expressions.ExistsNode(ColumnSet set, IReadOnlyDictionary<string, ColumnRef> match, WhereNode inner);

// Store/Query.cs
public static WhereNode Tables.Compile(ColumnSet set, Condition condition);
public Query Query.Where(Func<Refs, Refs, Condition> fn);
public Query Query.OrderBy(Func<Refs, Refs, ColumnRef?> fn, OrderDirection direction = OrderDirection.Asc);
public Query Query.Join(ColumnSet set, Func<Refs, IReadOnlyDictionary<string, ColumnRef>> match);
public Query Query.WhereNode(ColumnSet set, JsonNode? node);     // a trust boundary
public Query Query.WhereNode(ColumnSet set, WhereNode node);
public ColumnSet? Query.JoinedSet { get; }

// Store/Store.cs, Host
public Task ChordStore.SettledAsync();
IDisposable IStore.OnCommitted(Action<long> listener, Action<Exception> onError);
public sealed record HostError(..., string? Query = null);          // was Table
public sealed record HostDeleteOptions(string? Plugin = null);
public Task<HostDataCounts> ChordHost.DeleteHostDataAsync(Address address, ChordContext context, HostDeleteOptions? options = null);
```

`Refs` is `IReadOnlyDictionary<string, ColumnRef>`. A match is built with `Expressions.Match(("id", t["session"]))`; any `IReadOnlyDictionary<string, ColumnRef>` is taken, in its enumeration order. A table "spreads" a set's columns with `new OrderedDictionary<string, Property>(set.Columns) { ["title"] = Str() }`.

### 3.2 Internal: plans and reads

```csharp
// Store/ReadPlan.cs
sealed record Bound(string Table, TableDefinition Definition);
interface IBindings { Bound? Get(string set); }
sealed record Projection(IReadOnlyList<string>? Reads = null, IReadOnlyList<string>? PartnerReads = null);   // Projection.All: every column
enum ReadShape { Rows, Count }
sealed record ReadRange(long Limit, IReadOnlyList<object?>? After = null, IReadOnlyList<object?>? Through = null);   // upstream Range
sealed class Use { Role (Own | Joined | Exists), Table, Depends, Enters, PairedBy, Pairs, Keyed, Stable }             // a class: keyed by reference
sealed class ReadPlan { ReadPlan(Query, string table, Projection, ReadShape, IBindings); Query; Table; Order; Uses;
    Statement Select(ReadRange); Statement Count(); Statement Probe(ReadRange, columns, values); }

// Store/Read.cs
abstract record ReadRun { Rows(ReadResult Result) | Count(long Number) | Failed(Exception Error) }                   // compare by reference
sealed record ReadResult(Rows, bool More, IReadOnlySet<string> Keys, IReadOnlyDictionary<Use, HashSet<string>> Paired, IReadOnlyList<object?>? Past);
abstract record ReadKind { Rows(ReadRange Range) | Count }
interface IReadSink { void Adopt(ReadRun run); }        sealed class ReadSink(Action<ReadRun> adopt) : IReadSink
sealed class Read { Plan; What; Sink; Know(rows); Forget(); Latest(transaction); RunAsync(step, ctx); FetchAsync(reader, ctx); Ran(transaction, run); Adopt(run); Probed(result) }
string StoreInternal.Identity(columns, row);   string StoreInternal.IdentityOf(values);

// Store/Reads.cs
sealed class Reads : IBindings { Add; Delete; Opened; Closed; Retire(owner); On(table); OnHostKind(kind); Get(set);
    Bind(set, Bound, owner); Unbind(set, owner); BoundBy(owner); Hold(table); Ready(table); IsHeld(table); Action Watch(set, Action changed); }
sealed class ReadRefresh { ReadRefresh(Reads, tables, Func<IReadOnlyList<TableChange>> changes, ChordContext); IStagedWrite Write; Adopt(); }
IReadOnlyList<string> StoreInternal.SetsOf(Query);
Reads IChordStore.ReadIndex { get; }      // upstream store.reads

// Store/Skip.cs
enum TableChangeKind { Put, Update, Delete, Bulk }
sealed record TableChange(string Table, TableChangeKind Kind, IReadOnlyDictionary<string, object?>? Key, IReadOnlyList<string>? Assigned);   // upstream Change
Task<bool> StoreInternal.SkipsAsync(Read, IReadOnlyList<TableChange>, Step, ChordContext);      const int ProbesPerRead = 8;

// Store/OpenQuery.cs
sealed record QuerySpec(Query Query, string Table, Projection Projection, object Owner);
sealed record ReadSite(IChordStore Store, Batch Batch);
sealed record Compiled(ReadPlan? Plan, Exception? Error);
sealed class OpenQuery { OpenQuery(ReadSite, QuerySpec, ReadShape, Action rebound, ChordContext); Spec; Site; Context; Compiled; Closed;
    OpenRead Read(ReadKind, IReadSink); OpenRead ReadKnown(ReadRange, rows, IReadSink); Forget(OpenRead); Close(); }
sealed class OpenRead { What; Read; Register(); Know(rows); Forget(); Task<ReadRun?> LoadAsync(); Close(); }

// Store/Page.cs
sealed record StoredPage(IReadOnlyList<IReadOnlyDictionary<string, object?>> Stored, string? Next = null);
Task<StoredPage> StoreInternal.ReadPageAsync(IChordStore, ReadPlan, long limit, string? after, ChordContext);

// Store/LiveQuery.cs
sealed record LiveTarget(QuerySpec Spec, Func<IReadOnlyDictionary<string, object?>, ChordContext, JsonNode?> Decode);
abstract record LiveShape { Rows(ReadRange Range, bool WithMore = false) | Count }
sealed class LiveQuery : FramedSource { LiveQuery(ReadSite, IFramedOwner, LiveShape); Task LoadAsync(LiveTarget, ctx); Task<ReadRun?> ReadAsync(LiveTarget, ctx);
    Fail(Exception, ctx); Detach(Exception); }
sealed record DecodedRow(Stored, JsonNode? Row, long Token) { JsonNode? Copy(); }
sealed class DecodedRows { List<DecodedRow> Decode(stored, LiveTarget, ctx); Reset(); }
ImmutableArray<Op>? StoreInternal.DiffRows(IReadOnlyList<DecodedRow> before, IReadOnlyList<DecodedRow> after, ImmutableArray<PathSegment> path);

// other
string StoreInternal.PartnerName(set, column);      JsonNode StoreInternal.DecodeScalar(Column, object value);
Query.Joined (Joined(ColumnSet Set, IReadOnlyList<ExistsPair> Pairs)), Query.Expected, Query.Column(name)
WhereNode StoreInternal.CheckNode(JsonNode? node, ISide? side, string what, int depth = 0);     // upstream checkNode, returning the typed node
Scope StoreInternal.TableScope / SetScope / JoinedScope;  CompileCondition(scope, condition, what, expected)
// Host/HostData.cs, partial Self:  List<(LiveQuery Live, LiveTarget Target)> Queries (declared in Attempt.cs);
//   HoldTables(); BindSets(); Task LoadQueriesAsync(); RetireReads(Exception)
```

### 3.3 What differs from the discovery report's 3.5, and why

- **`IChordStore.ReadIndex`, not `Reads`.** `ChordStore.Reads` is taken: it is the test seam for a spied `store.read` (the documents tests' `holdNextRead`). Renaming the seam would have edited four test files outside this package.
- **`TableChange`, not `Change`.** `PiSharp.Chord.Contracts.Change` exists (`contracts/history.ts`), and files that import both namespaces could not name either.
- **`LiveShape.Rows(Range, WithMore)` has no `present` function.** Upstream's two uses are the rows themselves and `{ rows, more }`. The frame's differ must know where the rows are in the value, so the shape says it: `WithMore: false` is `self.Query`'s value, `WithMore: true` is `self.LiveQuery`'s `{ "rows": [...], "more": bool }`.
- **`ReadRun`, `ReadKind`, `LiveShape` are closed record hierarchies**, `Use` a class (a result keys a map by the use).
- **Identities are strings.** `StoreInternal.Identity` is always the JSON text of the values (a written key is doubles, a fetched row longs; discovery 5.4).
- **`Compiled` is a record** with `Plan` or `Error` set, for upstream's `{ plan } | { error }`.

### 3.4 Rules the next package must keep

1. **Threads.** An `OpenQuery`, its `OpenRead`s and a `LiveQuery` belong to their site's batch, that is to the host's dispatcher: they are made, read, told of a binding and closed there. `Reads` and `Read` are also read by the commit step on the store's queue and are locked. `OpenRead.LoadAsync` leaves the dispatcher for the store's guarded read and comes back through `Batch.Within`; it decides there, not on the read's thread, whether a commit's run or a close overtook it. A new framed source (a list) follows the same rule; `LiveQuery.LoadAsync` awaits without `ConfigureAwait(false)` on purpose.
2. **`LiveTarget.Decode` returns a node of its own with no parent.** `DecodedRows` keeps it and never hands it out; a value gets `DecodedRow.Copy()`.
3. **A framed source built of decoded rows overrides `Diff`** as `LiveQuery` does: a `ConditionalWeakTable` from each value given to `Set` to its `DecodedRow` list, and `StoreInternal.DiffRows(before, after, path)` for the ops (`null` means replace the value). A list's value `{ rows, moreAfter }` uses path `["rows"]` and its own tokens are comparable across pages (tokens are process-wide).
4. **`FramedSource` was not changed.** `Diff(before, after)` with the references given to `Set` was enough; the batch context was not needed (the foundation's open question 3).
5. **The end of activation reads every `Source` member** (`Attempt.ActivateOwnAsync`: `_ = source.Value`), where upstream reads only its derives. A `LiveQuery` member that has no value yet (its table is held, or a commit's run is still to be adopted) throws `not_ready` there and fails the activation. `self.Query` over an own table always has a value by then. A reader that parks (`self.LiveQuery` of another object's named query) must have its first value before that loop or the loop must skip framed sources that are not ready: `chord-queries` decides with its park step.
6. **`Self.Query` refuses a table that is not the factory's** with an `ArgumentException` and upstream's message (`self.query: the table is not one this factory declared`). Upstream's `tableExpected` (a named query handed to `self.query`) is `chord-queries`'s to add, in `Self.Query` and `RecordingSelf.Query` (both in `Host/HostData.cs`).
7. **`RetireReads`** closes the open queries a handle offers (`Reads.Retire(handle)`); a named query's `QuerySpec.Owner` must be the table handle for that to reach it.

## 4. Decisions

### 4.1 Upstream wins over the discovery report

- **The skip oracle asserts bounds, not exact triples.** Discovery 5.4 says the oracle has exact `{ runs, probes, skips }` triples per seed. Upstream's oracle asserts `> 200`, `> 100`, `> 100`; the exact triples are the deterministic cases'. Both are ported as upstream has them.
- **The refresh is staged for every table the transaction touched** (discovery 5.4). Native 1.5.2 filtered with `TableChanges.HasWrites`; upstream stages `ReadRefresh` for every table a write was attempted on. The filter is removed: a table whose only write was refused before staging now counts one `chord.query.skips` per read over it, as upstream does.
- **An activation whose live query cannot load fails `broken`.** Upstream's whole load phase is one `try` whose failures are `broken`. Native 1.5.2 classified a failing query load as `transient`. `LoadQueriesAsync` and a refused `BindSets` now raise `BrokenOpen`; the rest of the native load phase (documents, table preparation) keeps its 1.5.2 classification, which this delta does not touch.

### 4.2 Row identity and the frame

`DecodedRows` keeps each decoded row once with a token; a value given to `Set` is built of copies; `LiveQuery.Diff` computes the frame from the two values' tokens (`DiffRows`): unchanged rows cost one comparison, a changed row is diffed alone. The frames are upstream's (`["p", [], 0, 0, [row]]`, `["s", [1, "text"], "A"]`, a window shift as one insert and one removal). When a live query switches to another read (`ReadAsync` again) the rows are decoded anew and share no token with the value before; that one frame is the structural diff of the two values (`FramedSource`'s default), which is what upstream's `diffRevisions` does with rows that are new objects. A count's value is a number and is replaced.

### 4.3 A step's transaction token

Upstream keeps a step's not yet adopted run with the backend transaction it ran in and tells transactions apart by the executor object. An `ISqliteDatabase` adapter may hand every transaction the same executor, so `Step` has an internal `Transaction` token: a new object per commit step, one shared by the steps of a series, the executor when the store gives none. A rolled-back step's run is then never taken for a later step's (`store.live-query.160-refused-step-adopts-nothing`).

### 4.4 Group commit is not ported

Upstream's store writes the commits queued behind a running step as one backend transaction (S18, present at 1.5.2 too). The native `Store/Store.cs` never had it. It is not in the 1.5.2 -> 1.6.0 delta, so it is not deferred work of this package, but two things here lean on it: the skip oracle's "grouped commits" (4.1) and upstream's comment that a rolled-back group applies again alone (`ReadRefresh` clears its runs at each application, which is ported and tested). Reporting it so the status files can say so.

### 4.5 `onCommitted` without microtasks

The announcement is queued to the thread pool, at most one at a time, and reads `LastSeq` when it runs: one call for however many commits it covers, never inside a commit, a throwing listener removed and its error handed to `onError`. A listener therefore runs on a pool thread.

## 5. For root and the sibling packages

### 5.1 Edits outside the owned paths

- `src/PiSharp.Chord/Host/ReloadObject.cs`: one line, `UnbindSets();` in `RemoveForReload` (upstream `object.ts` `remove`). `ChordObject` is a partial class and the method the 4.3 table gives this package (`UnbindSets`, in `Object.cs`) has to be called where a removed type's object is sealed.

The five shared kernel files carry only the hunks the 4.3 table assigns: `Attempt.cs` (the table and query load loop: `HoldTables`, `BindSets`, `LoadQueriesAsync`; the type of `Self.Queries`; `CheckExposedOnce` in `Self.Table`; `RetireReads` at the detach step, where the old query detach line was), `Object.cs` (`LiveQueries` out, `UnbindSets` in, at the same lines), `Host.cs` (one line in `CloseCoreAsync`), `Staging.cs` (one line in `RecordingSelf.Table`), `Contracts.cs` (`HostError.Query`, `HostDeleteOptions`). The bodies live in `Host/HostData.cs`, which this package owns. Nothing was reformatted or reordered. `tests/PiSharp.Chord.Tests/ChordSupport.cs` and `Program.cs` are untouched; `Telemetry.cs` and `State/Framed.cs` are untouched.

### 5.2 Findings outside this package

- **`ReferenceMember` (`Host/Reference.cs`) replaces the value whole in its first frame.** A reference's member is made by its first use, records its source's value and marks itself framed without a delivery; the first change after that is published as `["r", value]`, and every later frame is a second structural diff of two values, not the source's ops. Values are right; frames are wider than upstream's and cost a deep comparison the source already avoided. It is 1.5.2 behaviour and `Reference.cs` is `chord-remote`'s in this wave. Whether a remote hold takes a member's ops from the source or through this relay was not traced. Worth a look before `chord-queries` ports the list cases that assert a frame over a link.
- **New files** for the integrity table and the source inventory: `src/PiSharp.Chord/Store/{ColumnSet,ReadPlan,Read,Reads,Skip,OpenQuery,Page}.cs`; `tests/PiSharp.Chord.Tests/Sync160StoreSupport.cs`, `Store/Sync160{ColumnSet,ReadPlan,Reads,Skip,Store}Cases.cs`, `Host/HostDataSync160Cases.cs`.
- **Headers.** The store files brought to 1.6.0 name `faac585884ae2c013a6b1400f3e9e77f10b34bac` and the 1.6.0 path. The shared kernel files (`Host/{Attempt,Object,Host,Staging,Contracts,ReloadObject}.cs`) keep their headers: the packages that finish them move them.

### 5.3 Breaking native API, for the release notes

Both assemblies are `[Experimental("PISHARP_CHORD001")]`.

- `HostError.Table` is `HostError.Query` (source-breaking for a caller that names it): `query_failed` names the query, which for `self.Query` is its table's name.
- `IStore` gained `OnCommitted` (breaking for an implementer of `IStore` outside PiSharp; `ChordStore` is the only one in the repository).
- `ChordHost.DeleteHostDataAsync` and `Tables.EvolveTable` gained an optional trailing parameter (source-compatible, binary-breaking).
- `Self.Query` and staging refuse a table that is not the factory's with an `ArgumentException` (`...: self.query: the table is not one this factory declared`); 1.5.2 threw a `ChordError` `invalid/args` with another message.
- An activation whose live query fails to load is `activation_failed` with a `broken` cause (1.5.2: `transient`, retried with backoff).
- A table handle's `PageAsync` and `CountAsync` refuse a joined query (`invalid/args`); their SQL is aliased (`FROM "tbl" AS t`), which nothing public shows.
- Behaviour: a commit runs a live query again only when its writes can change it; a `put` of a row outside a window costs one statement more (the probe).

Added (experimental): column sets (`Tables.DefineColumns`, `TableOptions.Exposes`, `EvolveOptions`), `Expressions.Exists`/`ExistsNode`/`Match`, `Tables.Compile`, `Query.Join`/`WhereNode` and the two-argument `Where`/`OrderBy`, `WhereNode.Exists`, `ChordStore.SettledAsync`, `IStore.OnCommitted`, `HostDeleteOptions`, the counters `chord.query.probes`, `chord.query.skips`, `chord.query.rows`.

## 6. Deferred

Nothing in this package's area is deferred. Not in it, and whose it is:

- `self.Queries`, `self.LiveQuery`, `self.Count`, `self.List`, `INamedQuery.PageAsync`, readers, lists, tiling, `tableExpected`: `chord-queries`.
- `ProcessWorkerOptions.Store` and the settled wait at a worker's exit (the consumer of `SettledAsync`): `chord-workers`.
- Group commit (4.4): not part of 1.6.0.
