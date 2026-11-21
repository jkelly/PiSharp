# chord-queries: Chord 1.5.2 -> 1.6.0, named queries, readers, lists and the park step

Work package `chord-queries` of the Pi 1.6.0 sync, 2026-11-21. Branch `sync/1.6.0-chord-queries`, from `2dabe61a` (after the merges of `chord-store` and `chord-remote`). Upstream v1.6.0 is `E:/AI/PiSharp/upstream-v160` (`faac585884ae2c013a6b1400f3e9e77f10b34bac`). The plan was `AI_DOCS/agent-reports/sync-1.6.0-chord-discovery.md`; the layers below are the foundation, store and remote reports beside this file. Where this report says "upstream wins", a report's proposal was changed after reading the source.

This package ports the host half of slice 14, first part: named queries (`self.Queries`, a reference's `queries` member, `PageAsync`), their readers (`self.LiveQuery`, `self.Count`), lists (`self.List`, tiling), the member check and staging for them, and the reusable park step in `Attempt` that the next package (`chord-admission`) drives. Admission, `denied` and workers are not in it.

## 1. Upstream changes in this area

Status is `ported` (code and a case that ran), `already-ported`, `no-op` or `deferred`, as the plan file defines them. Nothing in this package's scope is deferred.

| Upstream | Status | C# | Cases |
|---|---|---|---|
| `host/named-query.ts` (new): `NamedQueryHandle`, `OwnQuery`, `ReferenceQuery`, `ownQueries`, `QueriesOptions`, `PartnerRow` | ported | new `Host/NamedQuery.cs` | `host.named-query.*` (17) |
| `host/query-reader.ts` (new): `Reader`, `GivenQuery`, `GivenArguments`, `Opened`, `Reading` | ported | new `Host/QueryReader.cs` | `host.query-reader.*` (16) |
| `host/read-declarations.ts` (new): `openReaders`, `namedBuilders`, `firstOf`, `listName`, `oneSource`, `namedQueryExpected`, `undeclaredTable`, `readerSite`, `rowsReader`, `liveReader`, `listReader` | ported | new `Host/ReadDeclarations.cs` | `host.read-declarations.*` (4), and through the reader, named-query and list cases |
| `host/read-declarations.ts`: `tableExpected` | no-op: a named query cannot be handed to `self.Query` (its table is an `ITable`) | — | — |
| `host/list.ts` (new): `List`, `ListMemory`, `ListOptions` | ported | new `Host/ListSource.cs` (`List` is `System.Collections.Generic`'s) | `host.list.*` (19) |
| `host/tiling.ts` (new): `Tiling`, `TilingOwner`, `MAX_PAGES` | ported | new `Host/Tiling.cs` | `host.tiling.*` (29) |
| `host/attempt.ts`: `queries`, `liveQuery`, `count`, `list`, `#readerSite`, `ListHandle`, `QueryBuilders` | ported | `Host/ReadDeclarations.cs` (partial of `Self`), `Host/Contracts.cs` | the cases above |
| `host/attempt.ts`: `#park(dependencies, step, deadline)`, `#checkDependencies`, `#awaitReaders`, `#ownSteps` (readers before natives), `#readerWake`, `detach` (readers closed, tables' reads retired) | ported | `Host/Attempt.cs` (`ParkAsync`, `CheckDependencies`, `AwaitReadersAsync`, `LoadOwnAsync`, `OpenOwnAsync`) | the whole suite (every activation goes through it); `host.query-reader.a-column-set-nobody-serves-*`, `-readers-before-natives-*` |
| `host/attempt.ts`: `queryMember` | ported | `Host/NamedQuery.cs` (partial of `Attempt`) | every case that reads through a reference |
| `host/dependencies.ts`: `Parking`, `Blocked`, `Blocker` | ported | `Host/Internal.cs` (`Parking`, `IBlocked`, `Blocker`) | `host.read-declarations.*` |
| `host/dependencies.ts`: `pinKeys` exported, the `denied` branch of `checkDependencies` | other package (`chord-admission`) | — | — |
| `host/members.ts`: `checkQueries` | ported | `Host/NamedQuery.cs` (`QueryImplementations.Check`), `Host/Staging.cs` (`ValidateImplementation`) | `host.named-query.the-member-check-*` |
| `host/staging.ts`: `queries`, `liveQuery`, `count`, `list` recorded, `INERT_NAMED` | ported | `Host/ReadDeclarations.cs` (partial of `RecordingSelf`, `RecordingQueries`, `RecordingQuery`, `RecordingList`), `Host/Staging.cs` (`RecordingValue.QueriesMember`) | `host.named-query.staging-*` (2) |
| `host/object.ts`: `listMemory`, `queryMember` | ported | `Host/NamedQuery.cs` (partial of `ChordObject`: `ListMemoryOf`, `QueryMember`) | `host.list.reload-*`, the reader cases |
| `host/reference.ts`: `queryMember`, `followInstance`, `#toldOfInstance`, a reference's `queries` members | ported | `Host/NamedQuery.cs` (partial of `Binding`: `QueriesMember`, `QueryMember`, `FollowInstance`, `ToldOfInstance`), one line in `Host/Reference.cs` (`Moved`) | `host.query-reader.a-providers-restart-*`, `host.list.reload-of-the-stores-plugin-*` |
| `host/reference-source.ts` (unchanged upstream; the reference's member forwards its source's call) | ported (a 1.5.2 gap closed, see 4.4) | `Host/Reference.cs` (`ReferenceMember`) | `host.query-reader.160-a-references-member-*`, `host.list.160-through-a-reference-*` |
| `host/host.ts`: `query_failed { query }` for readers (the named query's label) | ported | `Host/ReadDeclarations.cs` (`ReaderSite`) | `host.query-reader.160-query-failed-names-the-named-query-*` |
| `host/index.ts`, `host/internal.ts` | exports only | public types in `Host/Contracts.cs`, `Host/NamedQuery.cs`, `Host/ReadDeclarations.cs` | — |
| `remote/remote-object.ts`: `queryMember(): never` | ported | `Remote/RemoteObject.cs` (`QueryMember` throws `invalid/member`) | — (a remote contract cannot declare a `queries` member, so no reference reaches it) |
| `docs/slice-14.md`, `docs/slice-14-design.md`, `test/bench/slice-14.ts`, `test/examples/69..72` | no-op: upstream documentation, benchmark, examples | — | — |

### Upstream test cases

| Upstream test file | Cases | Ported | Notes |
|---|---|---|---|
| `test/host/named-query.test.ts` | 17 | 17 | see 3 |
| `test/host/query-reader.test.ts` | 14 | 14 | see 3; 2 authored cases beside them |
| `test/host/read-declarations.test.ts` | 4 | 4 | — |
| `test/host/list.test.ts` | 15 `it` (the invariant runs for 4 seeds: 18 runs) | 15 (18 cases) | see 3; 1 authored case beside them |
| `test/host/tiling.test.ts` | 14 `it` (the invariant runs for 16 seeds: 29 runs) | 14 (29 cases) | see 3 |

All 64 upstream cases have a native case. 85 new cases in all: 82 port upstream cases and 3 are authored (`160` in their id): `host.query-reader.160-query-failed-names-the-named-query-for-a-live-query-a-count-and-a-list`, `host.query-reader.160-a-references-member-passes-its-live-querys-frame-on-as-it-is`, `host.list.160-through-a-reference-more-sends-only-the-new-rows-and-a-moving-row-is-one-insert-and-one-removal`.

## 2. Suites

Pinned SDK, `-c Release`, commands as in the plan file. See the end of this file for the final counts (section 8).

## 3. Where a ported case differs from upstream

No case was left out. Parts of cases that could not be written as upstream has them, and what stands for them:

1. **Typed declarations** (`query-reader` "the wrong form of each call", `named-query` "refuses a table given to a reader and a named query given to self.query"). Upstream hands a table to `self.liveQuery`/`self.count`/`self.list` and a named query to `self.query` (`as never`). An `ITable` is no `INamedQuery`, so neither can be written, and `tableExpected` has no native counterpart. The cases assert what the types still allow: a named query that is none of Chord's (`namedQueryExpected`, for each of the three declarations, by an attempt and by staging), a table that is none of the factory's (`undeclaredTable`, for `self.Queries` and `self.Query`), a page size below one, and a private query without its schema.
2. **Identity (`toBe`)**. "the loaded rows are the same objects", "an unlisted column's update changes no item", "its readers keep their rows": a `JsonNode` has one parent, so no node is shared between two values (the foundation's differ contract). The cases assert equality, that the mapping did not run again, and, for lists, the frame itself: a load is one insert behind the loaded rows, a moving row one insert and one removal.
3. **Commit groups** (`tiling` "a commit group of two owners", the two-owner invariant, `list` invariant). The native store writes each commit in a transaction of its own (group commit is not ported; chord-store report 4.4). The cases issue the same commits; they run one after another. Consequences:
   - The assertions on the number of groups (`grouped() > 50`, "the two commits formed one group", the `onCommitted` sequence checks) are left out.
   - "a run patched into a merged page": upstream's group computes the second commit's run for the old third page before the merge, so the moved row shows at once. Natively the second commit runs the merged page, which is full with a row behind it: the list shows `s4 s2 s1` and says more, and `more` loads `s5`. The case asserts that, then the rename and the delete as upstream.
   - "a group that rolled back and ran again alone" is "a commit refused in its step leaves the same pages": the refused commit fails alone, the others stand.
   - The path a group exercises (`Tiling.Patch`: a run of a page an earlier adoption cut or merged) is reached by the cases of one commit and several pages, and by the two cases of point 5.
4. **What a commit wrote** (the two-owner invariant's `probe.pending`). Upstream takes it from a queue when the commit is queued. A native transaction continues on the thread its object's line resumed it on, so the commits of two objects called together are queued in no fixed order. The methods take what their call wrote when they are called (on the dispatcher, in call order) and hand it to their own commit through a thread-static (`TilingTx`). All 16 seeds check after every adoption (more than 600 checks each).
5. **A page's run that fails** (`tiling` "a failed page fails the list until that page runs well", "a failed page a retired page's later run is patched into"). Upstream replaces the `run` method of one `Read`; `Read` is sealed here. The cases hand the read a failed run in a batch of its own, and adopt another page's (or the retired page's) fetched run the same way. Both keep upstream's assertions: another page's good run does not heal the list, and a failed page that a retired page's run was patched into is run by the next commit instead of being skipped.
6. **Between the migration's commits** (`named-query` "a provider's restart under its readers"). Upstream replaces `store.commit`. Here the store's read seam (`ChordStore.Reads`) holds the migration's read of its rows, which comes between the same two commits, until the other owner's commit ran.
7. **Waiting for a reload**. `ReloadAsync` returns at its objects' first check, which natively comes before a restarted object's tables are prepared (the native activation parks on its dependencies first, a 1.5.2 structure). The cases await `DrainReloadsAsync` where upstream awaits `flushed()`.
8. **Frames**. Where a case compares frames one by one it subscribes to the list as a link's provider side does (`Sync160Recorder`): a watch may fold two frames into one.
9. **`store.reads.hold` / `ready`** are called on the host's dispatcher, where the list lives.
10. **GC cases** use weak references (`ExpectCollected`) to the old code's `OwnQuery`, the readers' live queries and the list.

## 4. Decisions

### 4.1 The park step (what `chord-admission` builds on)

```csharp
// Host/Internal.cs
internal sealed record Blocker(string Label, string State);                 // State: "loading" | "failed"
internal interface IBlocked { void BlockedBy(Blocker? blocker); }           // the episode's parked_long clock
internal abstract record Parking
{
    internal sealed record Ready : Parking;
    internal sealed record Parked(RefState Shown, bool FirstPass, FailureCause? Cause) : Parking;
    internal sealed record Broken(ChordError Error) : Parking;
    internal static readonly Parking Go;            // the shared Ready
    internal static readonly Task<Parking> GoNow;   // for a check that awaits nothing
}

// Host/Attempt.cs
internal async Task ParkAsync(Func<IBlocked, Task<Parking>> step);
internal void Wake();
```

`ParkAsync` runs `step` at once and again after every `Wake()` until it answers `Ready` with no wake since the check began. One call is one parking episode.

- **`Ready`**: returns. If the episode parked at least once, `chord.park.duration` is recorded, a new activation span starts (linked to the parked one) and the object shows `loading` (upstream `object.unparked`).
- **`Parked(shown, firstPass, cause)`**: `Owner.SetState(shown)`, `ReloadCycles.Recheck()`, `Owner.FirstChecked(firstPass)`, `Checked.TrySetResult(cause)` (the reload's report: null reports no failure), the `chord.park` span event, then waits for the next `Wake()`. A wake that came while the step ran is not lost: the wake task is taken before the step.
- **`Broken(error)`**: throws the attempt's failure (`broken`), which `ActivateAsync`'s handler turns into `Owner.Failed`. So `ParkAsync` must be awaited inside `ActivateAsync`'s `try`.
- **The attempt's seal** ends the wait with `OperationCanceledException` (as before).
- **`parked_long`**: one report per episode, `ReadyTimeout` after its first parked check, naming the blocker the step named last with `episode.BlockedBy(...)` (the native default "a dependency / loading" when none was named).
- **The deadline** (`ReadyTimeout`) covers only the own steps (`ActivationDeadline.RunAsync(LoadOwnAsync)`, then `RunAsync(OpenOwnAsync)` with what is left); parks are outside it.
- **Wake** is `Attempt.Wake()`; continuations of the waiting attempt are posted to the dispatcher, so a check runs after the batch that woke it.

Its users now, in `ActivateAsync`:

```
factory, member check, derives attached
await ParkAsync(CheckDependencies);                 // self.Get references and self.Registry values
await deadline.RunAsync(LoadOwnAsync);              // documents, tables prepared, sets bound, own queries, jobs
if (Self.Readers.Count > 0) await AwaitReadersAsync();   // ParkAsync(episode => ReadDeclarations.OpenReadersAsync(...))
await deadline.RunAsync(OpenOwnAsync);              // natives, followed sources, members' first values
```

How admission should drive it:

1. Before the factory, inside the same `try`: `await ParkAsync(episode => DecideAsync(decision, episode))`. The step returns `Parking.Go` once the decision allows, `new Parking.Parked(RefState.Failed(new(FailureKind.Denied, error)), FirstPass: true, cause)` for a denial (then waits for a `Wake()` when the decision changes), and `Broken` for its own code.
2. Its readers of named queries (`IAdmitSelf.LiveQuery`) are made with `ReadDeclarations.ReaderSite(host, label, attempt.Wake)` and `ReadDeclarations.RowsReader(site, query, args, first)`, and opened inside its step with `ReadDeclarations.OpenReadersAsync(readers, wake, episode, label, context)`, where `wake` is an `IDependent` whose `Invalidate` calls `attempt.Wake()` (as `Attempt.ReaderWake`). Remove the wake from the readers' sources when the step is done (`reader.Source.Dependents.Remove(wake)`), and `reader.Close(error)` them when the decision is released.
3. Differences from upstream's `#park(dependencies, step, deadline)` to mind: there is no `dependencies` parameter. The native dependency step reads `Self.Dependencies` and `Self.RegistryDependencies`, and `ChordObject.WaitEdges()` and `Retry` read `Self.Dependencies` too. An admission's own dependencies (its `self.Get` before the factory) are not in those lists unless admission puts them there or gives `WaitEdges`/`Retry` its own.
4. `Parked` always settles `Checked` (the reload's first check). An admission parked on a denial should pass the cause it wants the reload report to show.

### 4.2 Upstream wins over the discovery report

- **`LiveQuery`, `Count` and `List` are on `ISelf`, `Queries` on `IStoreSelf`**, as upstream's `Self` and `StoreSelf` (the discovery report's 3.5 has it so; the brief's wording put all four on the store's self).
- **`IListHandle.Value`** is `{ rows, moreAfter }`, the watchable itself; `MoreAsync(context)` returns a `Task` (upstream resolves `null`).
- **`ListOptions(long First)`**; the page size of `LiveQuery` is a `long first`.
- **`ReloadObject.cs` was not needed.** The discovery report did not list it for this package, and the retirement of a restarting provider's reads is done in `Attempt.CloseCoreAsync` (4.3).

### 4.3 Native structure

- **Readers after the tables.** Upstream prepares tables before the dependency park; the native activation parks on dependencies first. The readers open after `LoadOwnAsync` (tables prepared, sets bound, own queries run) and before the natives, so a reader of the object's own named query finds its table ready, and an attempt waiting on a reader holds no native.
- **A restarting provider's offered queries retire before its successor prepares the tables.** The native restart keeps a native-less old attempt's sources until its successor's first check (`PreserveSourcesAtClose`). `Attempt.CloseCoreAsync` now calls `Self.RetireOffered()` in that case, after the old attempt's admitted work settled: its readers' reads leave the store's index (upstream `detach`), so no commit runs a statement compiled for the table as it was. Readers keep their values and read the successor's query when it is live (`Binding.ToldOfInstance`, called from `Binding.Moved`).
- **A page an adoption makes loads in a later turn** (`Tiling.LoadLater`). Upstream's `void page.read.load()` always completes in a later microtask. A store's read here may complete at once (a synchronous backend), inside the adoption that made the page, which re-entered `Retile` before it had assigned its pages (a stack overflow in the first run of the list invariant). The rest of an overflowing range and a page behind a cut run are loaded through `Batch.Schedule`.
- **The end of activation skips a failed framed source.** The native loop that reads every `Source` member (chord-store 3.4, rule 5) would fail the activation if a reader that had its first value failed again while the natives opened. Such a member is a failed member of a live object, as upstream's (which reads only derives there). A framed source with no value and no failure still fails the activation `not_ready`.
- **An attempt leaving parking shows `loading`** (upstream `object.unparked`). Natively a parked object kept showing `failed { waiting }` through its own steps until it went live. The existing suite did not depend on it.
- **Staging checks a `queries` member** as an attempt does (a recording's `self.Queries` knows its builders): a missing builder for a listed query, a second schema and a private query without one fail the reload's staging, where upstream would fail the successor's activation.
- **`PageAsync` runs on the host's dispatcher** (it may be called from any thread); the builder is called under the facet's callback lease.
- **Arguments** are a `JsonNode` (null is JSON null) or a JSON watchable made by Chord (a derive, a state member, a reference's member, a constant); anything else is refused at the declaration.
- **A mapping's item** that already has a parent is copied (a value's row is a node of its own).

### 4.4 `ReferenceMember`: the known issue from chord-store

Checked early. No list case asserts a frame over a link (the link case asserts values, the state and no error), so the brief did not require a change. It was made anyway, because it is small and it is what "more sends only the new rows" means for a holder: `ReferenceMember` (`Host/Reference.cs`) now passes its source's ops on when that frame is the batch's only change of the member (upstream's reference source forwards its source's call), and no longer keeps its "framed" mark while nothing follows it, which made its first frame after creation or a retarget `["r", value]`. Any other change in the batch (a retarget, a second frame, a failure) falls back to the diff of the two values, as before. The full Chord suite passed unchanged with it. Two authored cases pin it (a live query's and a list's ops through a reference). The comment in `tests/PiSharp.Chord.Tests/Store/LiveQueryCases.cs` (chord-store's file) that explains why three cases watch the live query instead of the reference describes the old behaviour; the cases still pass and were not edited.

### 4.5 Observations

- **A reader's first failed run is reported `query_failed` even while its attempt parks on it**, as upstream (`FramedSource.failWith` reports before a first value too; only the owner's own `self.Query` is quiet at activation).
- **Transactions of different objects called together commit in no fixed order** natively (3.4). Nothing in the product depends on it; it matters to tests that pair calls with commits.
- **`host/dependencies.test.ts` (+2) and `host/reference-source.test.ts` (+1)** new upstream cases are admission's (the `denied` branch).

## 5. Edits outside the owned paths

- `src/PiSharp.Chord/Remote/RemoteObject.cs`: the throwing `QueryMember` (three lines), as the plan assigns.

Nothing else. `Host/HostData.cs` (chord-store's) was not edited: `tableExpected` has no native counterpart (3.1), and `Self.Query`'s refusal already has upstream's `undeclaredTable` message. `Store/**`, `State/**`, `tests/PiSharp.Chord.Tests/ChordSupport.cs` and `Program.cs` are untouched. `Host/Host.cs` was not changed at all (its header still names the 1.5.x commit; `chord-admission` finishes it). The headers of `Host/{Attempt,Staging,Object,Contracts}.cs` now name `faac5858...` and say what of 1.6.0 is still `chord-admission`'s.

## 6. For root and the next packages

- **New files** for the integrity table and the source inventory: `src/PiSharp.Chord/Host/{NamedQuery,QueryReader,ReadDeclarations,ListSource,Tiling}.cs`; `tests/PiSharp.Chord.Tests/Sync160QueriesSupport.cs`, `Host/Sync160{NamedQuery,QueryReader,ReadDeclarations,List,Tiling}Cases.cs`.
- **`chord-admission`**: section 4.1. `Reader`, `GivenQuery`, `IQueryReader`, `ReadDeclarations.{OpenReadersAsync,ReaderSite,RowsReader}` are what `host/admission.ts` imports upstream. `Binding.QueryMember` throws the reference's failure; a `denied` state needs its own branch there. `ChordObject.QueryMember` is upstream's `#live()` for queries.
- **`tests/PiSharp.Chord.Tests/Store/LiveQueryCases.cs`**: its header comment about `ReferenceMember` is out of date (4.4); root may reword it.

## 7. Native API added, for the release notes

All experimental (`PISHARP_CHORD001`).

```csharp
// Contracts (foundation): Schemas.Queries(item, (name, argsSchema)...), INamedQuery.PageAsync, QueryPage

// Host
public delegate JsonNode? QueryMap(IReadOnlyDictionary<string, JsonNode?> row, IReadOnlyDictionary<string, JsonNode?> partner);
public sealed record QueriesOptions(QueryMap? Map = null, IReadOnlyList<string>? Reads = null, IReadOnlyList<string>? PartnerReads = null);
public sealed record QueryBuilder(Func<JsonNode?, Query> Build, JsonNode? Args = null);   // Args: a private query's own schema
public sealed record ListOptions(long First);
public interface IListHandle { IWatchable<JsonNode?> Value { get; } Task MoreAsync(ChordContext context); }   // Value: { rows, moreAfter }

IReadOnlyDictionary<string, INamedQuery> IStoreSelf.Queries(ITable table, QueriesOptions options, IReadOnlyDictionary<string, QueryBuilder> builders);
IWatchable<JsonNode?> ISelf.LiveQuery(INamedQuery query, object? args, long first);   // value { rows, more }; args: JsonNode or a JSON watchable
IWatchable<JsonNode?> ISelf.Count(INamedQuery query, object? args);
IListHandle ISelf.List(string name, IReadOnlyList<(INamedQuery Query, object? Args)> sources, ListOptions options);
IReadOnlyDictionary<string, INamedQuery> IReference.QueriesMember(string name);
```

**Added**

- Named queries: a `local` contract lists them with `Schemas.Queries(item, (name, argsSchema)...)`; the table's owner implements the member with `self.Queries(table, options, builders)`; any object of the store's host reads one live with `self.LiveQuery(namedQuery, args, first)`, counts it with `self.Count(namedQuery, args)` or reads a page with `namedQuery.PageAsync(...)`. Another object's query is `reference.QueriesMember("member")["name"]`. Arguments may follow a value (a JSON watchable): the member shows the previous rows until the read for the new arguments ran.
- Lists: `self.List(name, [(namedQuery, args)], new ListOptions(first))` returns `Value` and `MoreAsync`. Pages lie between fixed bookmarks; loaded rows stay live; a load sends only the new rows; a row that moves leaves no gap and no duplicate; a reload of the list's plugin keeps the pages it had; a reload of the provider's plugin does not restart the list.
- `HostError` `query_failed` names the named query for `self.LiveQuery`, `self.Count` and `self.List` (`Query` is the named query's label, for example `app.notes: query "notes.ofBoard"`).

**Changed**

- A reference's state member publishes its source's frame as it is: its first frame after it was made or retargeted no longer replaces the value whole, and later frames are the source's own ops (they were a second structural diff).
- An object that was parked (`failed` waiting, or `loading`) shows `loading` while it runs its own activation steps; it kept showing what it had waited for until it went live.
- Reload staging refuses a `queries` member that is not what `self.Queries` returned, a listed query without a builder, a second argument schema and a private query without one.

**Binary-breaking, source-compatible**

- `ISelf`, `IStoreSelf` and `IReference` gained default-implemented members (`LiveQuery`, `Count`, `List`; `Queries`; `QueriesMember`).

## 8. Suites: final counts

(Filled in at the end of the work; see the last commit of this file.)
