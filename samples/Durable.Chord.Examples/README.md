# Durable on the real Chord host

These offline C# examples track `packages/durable/test/examples/35-37` at upstream
`f9a26365568127461c47a07a37dcb42c140a3ac5` (v1.5.0). They use real SQLite,
`Hosts.Create`, facet loading, native resources, the shared store, and followed
Durable views. No provider keys or model calls are needed.

```powershell
dotnet run --project samples/Durable.Chord.Examples -- 35 36 37
```

Omit the numbers to run all three. Example 35 creates and removes its own temporary
SQLite directory. Examples 36 and 37 use private in-memory SQLite databases.

| Example | Behavior |
| --- | --- |
| 35 | A Chord table and Durable session share an owner, sequence, allocator and archive. Close the host, reuse its store attachment, then reopen the database and read both kinds of data. |
| 36 | Follow a Durable conversation and task graph from a host object. Expose their derived summary as a Chord member and observe one frame per write. |
| 37 | Compose plugin extensions through `RegistryReader`, execute the contributed task, follow a contributed Harness from another object, and retry after closing the Session. |

`ViewBridge` makes the C# representation boundary explicit: Durable has typed
immutable views; Chord's host currently exposes JSON members. It projects the entry
kinds and task count used by these examples into JSON and forwards replacements,
retirement and terminal failures. The real observer's detacher is returned to the
host. This is a projection adapter, not a substitute for either runtime.

The companion executable test project is
`tests/PiSharp.Chord.Durable.Integration.Tests`. It adds atomic two-view publication,
pre-load acquisition, plugin reload/removal, and a lost SQLite commit acknowledgement
case. Historical hand-built registry tests remain useful lower-level tests; these
examples and tests exercise the real host lifecycle.

All dependencies retain their experimental package boundaries. These examples do
not imply the Chord or Durable packages are ready for publication.
