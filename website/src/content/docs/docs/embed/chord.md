---
title: Chord and native hosting
description: The experimental Chord v2 host, native plugins, HTTP and worker processes.
---

:::caution[Experimental — from 1.5.0.1]
`PiSharp.Chord` and `PiSharp.Chord.Hosting` are available on NuGet at version **1.6.0**. Their public APIs use `PISHARP_CHORD001`; hosting also requires the ASP.NET Core shared framework. Keep all PiSharp sibling references at the same exact version. See the [package contract](https://github.com/jkelly/PiSharp/blob/main/docs/contracts/gap-package-admission.md).
:::

:::note[Updated in 1.6.0]
PiSharp 1.6.0 follows Chord 1.6.0: events, named queries, lists, column sets, shared holds, admission, and sign-ins in place of the app kit's sessions. Everyone signs in again after the update, and 1.5.x and 1.6.0 hosts do not interoperate over trusted links. See [New in 1.6.0](#new-in-160).
:::

Chord is the reactive host and store used by the [Durable.Store adapter](/docs/embed/durable-store/). The v2 implementation includes host roots and resources, facet loading/reload, jobs, links, remote routes, registries, state watches and a SQLite store. See [host source](https://github.com/jkelly/PiSharp/tree/main/src/PiSharp.Chord/Host) and the [current mapping](https://github.com/jkelly/PiSharp/blob/main/compatibility/gap-status-2026-11-08.json) for implementation and test boundaries.

## Native plugin generations

Each plugin directory contains a `chord.plugin.json` manifest. The [NativePlugins sample](https://github.com/jkelly/PiSharp/tree/main/samples/chord/NativePlugins) supplies complete projects, shared contracts and a private dependency:

```json
{"name":"app.notes","assembly":"Notes.dll","type":"App.NotesPlugin","project":"Notes.csproj"}
```

The entry implements `IChordPlugin.CreateFacet(PluginContext)`; `PluginContext.Kind` distinguishes server and worker roles. Omit `project` for a prebuilt assembly. Supply an explicit absolute .NET SDK path to `PluginBuilder`; restore is opt-in. Build diagnostics and deadlines are bounded, and child processes retain Windows job or Unix group ownership.

`PluginRuntime` loads a complete `PluginBuildSet`, shares the Chord/Hosting/Contracts/Telemetry assembly identities, and accepts explicit application `SharedAssemblies`. Failed builds retain the previous successful plugin. Unchanged digests reuse the same facet identity. After a committed reload, `DrainRetiredAsync` waits for old callbacks and resources before releasing generations. A drain timeout reports failure and leaves executing code loaded. Plugins are trusted application code; collectible load contexts provide lifetime and dependency isolation.

`PluginManager` watches, builds and performs explicit rollout steps. File changes alone do not apply code. The [hosting guide in source](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Chord.Hosting/README.md) documents ordering, cancellation and retirement in detail.

## HTTP and workers

| Surface | Host integration |
| --- | --- |
| `ChordHttp` | Load its facet under `chord.http` with route/authenticator contributions. `Port = 0` selects an ephemeral loopback listener; omit the port to mount `HandleAsync` in ASP.NET. `ListeningAsync` reports the actual URI. |
| Request lifetime | Routes, streams and sockets hold a Chord root through response settlement. Contributions include wrappers, allowed hosts, relay validation, origins and CSP. Cross-site webhooks receive an anonymous root. |
| Shutdown | Close HTTP before the host, then dispose the plugin runtime. Requests drain to `CloseTimeout`, after which their scopes are cancelled. |
| `ProcessWorkers` | Create before host load and include its stable facet. Supply executable/arguments, build provider, principal factory, authorization, lock directory and the server host's `Store` (required). An optional `Admit` decides per principal and address before a worker starts. Authorized, admitted references start addressed workers; `ProcessWorkerState.Incarnation` names the running one. |
| `WorkerMain.RunAsync` | Child entry point; the callback creates the host and loads plugins through `WorkerContext.LoadPluginsAsync`. Separate current-user named pipes carry private control and framed Chord v2 traffic. |

Worker crash/retry, reload and retained process-tree cleanup are tested with real child processes in [hosting tests](https://github.com/jkelly/PiSharp/tree/main/tests/PiSharp.Chord.Hosting.Tests). The framing is distinct from Node IPC. The package does not provide JavaScript module evaluation, npm installation, browser asset compilation or arbitrary Node child compatibility.

## New in 1.6.0

PiSharp 1.6.0 follows Chord 1.6.0. The [1.6.0 release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.6.0.md) list every change, what was tested and what was not. Everything below is experimental (`PISHARP_CHORD001`).

### Before you update

- **Everyone signs in again.** The app kit's `Sessions` is `SignIns` (with `EphemeralSignIns`, `SignInKind`, `SignInInfo` and `IssuedSignIn`). The cookie is `chord-sign-in`, and the contract and stored document are `chord.app.sign-ins`. There is no migration: browser cookies and device tokens stop authenticating, the owner token file is replaced at the next start, paired devices pair again, and the old `chord.app.sessions` document stays in the store unread.
- **Update hosts together.** The protocol version stays 1, but a 1.6.0 host announces no call window on a trusted link, which a 1.5.x peer rejects or reads as a zero window. 1.5.x and 1.6.0 hosts do not interoperate over trusted links: workers and host-to-host routes.
- **Update devices that share a sync remote together.** An upload after the outbox was turned off and on creates an archive slot after the remote's head. A 1.5.x client that reads such a remote refuses the slot with `HistoryUnavailableError`.
- **Changed options.** `TelemetryIngestOptions.Admit` is required (`_ => true` keeps the old behavior). `ChordHttpOptions.LinksOpened` takes a third argument, an `Action` that terminates that link. `HostError.Table` is `HostError.Query`. `IStore` gains `OnCommitted`. `Client.StartAsync` completes before the server's first script set is applied.

### Events

An event member is a fire-and-forget value that an object emits and its holders receive. Declare it in the contract with `Schemas.Event(schema)`. The implementation supplies the member with an event source from `self.Events()` and calls `Emit` on it. A holder subscribes through its reference:

```csharp
// In the implementation: ["changed"] = changed, where changed = self.Events();
changed.Emit(JsonValue.Create(42));

// In a holder:
IEventSubscription subscription = await reference.Event("changed").OnAsync(e => Handle(e.Value, e.At), context);
// ...
subscription.Stop();
```

- A `ChordEvent` has the `Value` and `At`, the emitting host's time in Unix milliseconds.
- An event is delivered once to each reference bound to the object before the emit. It is never kept, and it is dropped for a holder that cannot keep up.
- Events cross links and relays. A host leaves at most `HostOptions.EventWindow` (32) of its own event messages unacknowledged per link and origin; a flush that finds the window full drops that origin's events. `chord.events.dropped` counts drops with their reason.
- An event listener that throws is reported as `listener_error`.
- `VersionMappings.Events` maps an event for holders of an older contract version, or refuses it.

### Named queries and lists

A named query lets one object of the store's host read another object's table by name, without the table.

| Step | API |
| --- | --- |
| List the queries in a local contract | `Schemas.Queries(item, (name, argsSchema)...)` |
| Implement the member in the table's owner | `self.Queries(table, options, builders)`, with a `QueryBuilder` per name |
| Read one live | `self.LiveQuery(namedQuery, args, first)`: a watchable of `{ rows, more }` |
| Count it | `self.Count(namedQuery, args)` |
| Read one page | `namedQuery.PageAsync(args, limit, context)` |
| Reach another object's query | `reference.QueriesMember("member")["name"]` |
| Load more as a screen scrolls | `self.List(name, sources, new ListOptions(first))`: `Value` and `MoreAsync` |

- Arguments may follow a value: the member shows the previous rows until the read for the new arguments ran.
- A list's pages lie between fixed bookmarks. Loaded rows stay live, a load sends only the new rows, and a row that moves leaves no gap and no duplicate. A reload of the list's plugin keeps the pages it had.
- A failed read is reported as `query_failed`, and `HostError.Query` names the query.
- A remote contract cannot declare a `queries` member: named queries are local to the store's host.

### Column sets

A column set lets another plugin's query test or join rows of a table it does not own.

- `Tables.DefineColumns(name, options)` defines the set, and `TableOptions.Exposes` makes a table expose it.
- `Expressions.Exists` and `Expressions.ExistsNode` test for a matching row. `Query.Join` joins one row. `Tables.Compile` and `Query.WhereNode` pass a condition as data.
- A table handle's `PageAsync` and `CountAsync` take a query with `Exists` and refuse one with a join.

A commit runs a live query again only when its writes can change it. The store indexes every live read by the tables it uses, and the counters `chord.query.probes`, `chord.query.skips` and `chord.query.rows` report the work. The native store writes each commit in a transaction of its own; it has no group commit.

### Shared holds and call windows

- Remote references of one host to one object share one wire hold. A second reference is live at once, and the provider counts one per hold in presence and `Activity.References`.
- `HostOptions.CallWindow` (10,000, was 256) limits the calls running on an untrusted link. A trusted link has no window.
- A route's resolver may be told the peer's incarnation at the handshake through `ResolvedLink.Opened`.

### Admission and workers

- **Admission.** A remote contract may declare an admission schema: the value its factory is handed about a caller. Serve its type with `IFacetHost.Serve(Contract, AdmittedHostFactory, ServeOptions)` and `ServeOptions.Admit`: the `Admit` function reads data about the caller through `IAdmitSelf` and returns a watchable of `Admission.Allow(value)` or `Admission.Deny(reason)`. A denial shows the reference as `failed` with the cause kind `FailureKind.Denied`: getters reject at once, no value is kept, and running calls reject `forbidden` with reason `revoked`. Another value rebuilds the object.
- **Route admission.** `RouteOptions.Admit` asks one decision per principal and address before anything is resolved or forwarded. `ProcessWorkerOptions.Admit` passes it to the workers' route, so no worker starts for a caller who is denied.
- **Workers.** `ProcessWorkerOptions.Store` is required, so that a worker's exit is published only once the commits queued before the exit settled. The workers close a worker's link when it exits, before they publish the exit. `ProcessWorkerState.Incarnation` carries the running worker's incarnation.
- **Terminal scopes.** `TerminalScope.On(member, listener)` subscribes to an event member.
- **Terminal scope.** `TerminalScope.Variant` narrows a held reference to one variant while it is live in it; this part is implemented. `TerminalScope.On` subscribes to an event member.

## Legacy Chord is separate

The source also implements the older **protocol-4** runtime and cluster/manager behavior. It has its own lifecycle and compatibility tests and is not interchangeable with the v2 host or wire protocol. Use the [source mapping](https://github.com/jkelly/PiSharp/blob/main/compatibility/gap-status-2026-11-08.json) to select the appropriate runtime; the native hosting package describes v2 only.
