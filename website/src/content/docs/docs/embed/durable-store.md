---
title: Durable on Chord's store
description: Share storage, followed views and plugin registries with the real Chord host.
---

:::caution[Experimental — from 1.5.0.1]
`PiSharp.Durable.Store` is available on NuGet at version **1.5.0.1** and retains `PISHARP_DURABLE001`. [Chord and hosting](/docs/embed/chord/) have separate experimental and framework requirements. Keep all PiSharp sibling references at the same exact version.
:::

`StoreStorage` implements Durable's `IStorage` over a real Chord v2 store. It declares records under an owner, uses the shared allocator, and commits through the store queue. The application owns the underlying store: closing the adapter releases its claims after calls drain and leaves the store open. A separate owner can share the same store; duplicate claims are refused. The [implementation](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Durable.Store/Storage.cs) requires a store opened by Chord's `Tables.OpenStoreAsync`.

```csharp
// store is the IStore opened by Chord; the application owns its lifetime.
var storage = await PiSharp.Durable.Store.StoreStorage.OpenAsync(
    store, "app/agent", PiSharp.Durable.Chord.DurableContext.Background);
```

`RegistryReader.Create` adapts a registry composed by the real host, allowing contributed extensions to follow plugin reload and removal. The [registry adapter](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Durable.Store/RegistryReader.cs) and [runnable examples](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Chord.Examples) show acquisition, disposal and failure handling together.

## Run examples 35–37

From a source checkout:

```powershell
dotnet run --project samples/Durable.Chord.Examples -- 35 36 37
```

These are offline examples with real SQLite, `Hosts.Create`, loaded facets, native resources and the shared store. No provider credentials are needed.

| Example | Demonstrates |
| --- | --- |
| 35 | Chord tables and Durable sessions share owner, sequence, allocator and archive. Close/reopen the database and read both. |
| 36 | Follow conversation/task views from a host object and publish a derived member. |
| 37 | Compose extensions through `RegistryReader`, execute the contributed task and follow a contributed harness across session reopening. |

`ViewBridge` explicitly projects typed immutable Durable views into the host's JSON members and returns the observer detacher. It forwards replacement, retirement and terminal failure; the projection does not replace either runtime. [Integration tests](https://github.com/jkelly/PiSharp/tree/main/tests/PiSharp.Chord.Durable.Integration.Tests) add atomic two-view publication, pre-load acquisition, plugin reload/removal and lost commit acknowledgement.

Historical hand-built registry tests remain lower-level evidence. The real-host examples extend that evidence. The [release qualification record](https://github.com/jkelly/PiSharp/blob/main/AI_DOCS/release-1.5.0.1-publication-results.md) separates package publication, source tests and platform qualifications.
