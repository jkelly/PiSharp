---
title: Remote environments and daemon
description: Use the pi-env protocol, reconnecting client, remote adapters and native daemon.
---

:::caution[Experimental — from 1.5.0.1]
`PiSharp.Env.Protocol` and `PiSharp.Env` are available on NuGet at version **1.5.0.1**, with diagnostic `PISHARP_ENV001`. The daemon is a separate NativeAOT executable, excluded from library packing. Qualified native ZIPs for Linux x64, macOS arm64 and Windows x64 are attached to the [release](https://github.com/jkelly/PiSharp/releases/tag/v1.5.0.1). Keep each binary's accompanying notices and verify the release integrity records before deployment.
:::

`PiSharp.Env.Protocol` defines [framing and bounds](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Env.Protocol/FrameCodec.cs). `PiSharp.Env` adds a lazy reconnecting `Connection`, remote filesystem/process/terminal/watch adapters, and SSH bootstrap. `RemoteExecutionEnv` implements Durable's `IExecutionEnv`. These are explicit host choices, not a default dependency or remote mode added to the CLI.

## Connect to an owned daemon

The [`ConnectionOptions`](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Env/ConnectionModels.cs) command contains the executable and arguments before the connection appends `serve --token`. Supply exactly one of `Command` and `CommandFactory`:

```csharp
using PiSharp.Env;

await using var connection = new Connection(new ConnectionOptions
{
    Command = [daemonExecutablePath],
});
var info = await connection.InfoAsync(); // starts, authenticates and checks hello
var env = new RemoteExecutionEnv(new RemoteExecutionEnvOptions
{
    Connection = connection,
    Id = "remote",
    Cwd = info.Cwd,
});
```

The [client source](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Env/Connection.cs) defines startup, ping, silence, abort and shutdown bounds. Requests lost with a connection are **never replayed**: a mutation's outcome may be unknown. Handles belong to one connection session and do not migrate after reconnect. `CleanupAsync` releases environment processes, watchers and handles; dispose the connection to join owned startup and process cleanup.

Remote filesystem operations preserve binary payloads and descriptor metadata. Process adapters expose raw pipes and exit state; watches support cancellation. Unix terminal operations use the native PTY shim. **Windows terminal creation returns `ENOTSUP`**; filesystem and ordinary process operations remain available there.

## SSH and artifact trust

[`SshConnectOptions`](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Env/Ssh.cs) requires a host, known-hosts file, host-key alias and `DaemonCatalog`. Verify the host key independently before accepting it. Unknown and changed keys have distinct exceptions. Bootstrap detects the remote platform, selects a catalog artifact and verifies its hash at every reconnect. Catalog/cache validation and upload are explicit; the source does not establish testing against a real SSH host.

## Build and qualify the native daemon

```powershell
./tools/Env/Publish-NativeDaemon.ps1 -OutputDirectory artifacts/env-native-check
```

Use a fresh output directory. The [native qualification guide](https://github.com/jkelly/PiSharp/blob/main/tools/Env/README.md) documents prerequisites, notices, hash manifests and smoke cases. The executable is compiled for the actual host RID; no cross-compilation is configured. Native/trim warnings fail publication. Retain the complete notices bundle with redistributed binaries.

The [env-native workflow](https://github.com/jkelly/PiSharp/blob/main/.github/workflows/env-native.yml) records exact source, toolchain, ABI and test results. The three native platform jobs passed for release source `8144e3ea`; only the tested platform/revision is qualified. This does not establish older glibc/macOS compatibility, musl, Android or other RIDs. See [release qualification](/docs/contribute/releasing/#four-required-validation-gates) and the [publication evidence](https://github.com/jkelly/PiSharp/blob/main/AI_DOCS/release-1.5.0.1-publication-results.md).
