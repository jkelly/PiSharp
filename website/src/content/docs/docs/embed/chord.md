---
title: Chord and native hosting
description: The experimental Chord v2 host, native plugins, HTTP and worker processes.
---

:::caution[Candidate 1.5.0.1 — experimental and unpublished]
`PiSharp.Chord` and `PiSharp.Chord.Hosting` are prepared for admission, not available as the candidate on NuGet. Build from source. Their public APIs use `PISHARP_CHORD001`; hosting also requires the ASP.NET Core shared framework. See the [package contract](https://github.com/jkelly/PiSharp/blob/main/docs/contracts/gap-package-admission.md).
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
| `ProcessWorkers` | Create before host load and include its stable facet. Supply executable/arguments, build provider, principal factory, authorization and lock directory. Authorized references start addressed workers. |
| `WorkerMain.RunAsync` | Child entry point; the callback creates the host and loads plugins through `WorkerContext.LoadPluginsAsync`. Separate current-user named pipes carry private control and framed Chord v2 traffic. |

Worker crash/retry, reload and retained process-tree cleanup are tested with real child processes in [hosting tests](https://github.com/jkelly/PiSharp/tree/main/tests/PiSharp.Chord.Hosting.Tests). The framing is distinct from Node IPC. The package does not provide JavaScript module evaluation, npm installation, browser asset compilation or arbitrary Node child compatibility.

## Legacy Chord is separate

The source also implements the older **protocol-4** runtime and cluster/manager behavior. It has its own lifecycle and compatibility tests and is not interchangeable with the v2 host or wire protocol. Use the [source mapping](https://github.com/jkelly/PiSharp/blob/main/compatibility/gap-status-2026-11-08.json) to select the appropriate runtime; the native hosting package describes v2 only.
