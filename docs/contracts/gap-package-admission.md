# Experimental package admission

This contract describes local package admission for the Pi 1.5.2 source candidate. It does not publish a release. The published 1.5.0.1 packages and their Pi 1.5.0 baseline remain historical records.

The release library list now contains 28 libraries plus the CLI tool. Five additional libraries are prepared:

| Package | Exact-version direct dependencies | Opt-in diagnostic |
|---|---|---|
| PiSharp.Chord | PiSharp.Contracts, PiSharp.Telemetry, PiSharp.Tools | PISHARP_CHORD001 |
| PiSharp.Chord.Hosting | PiSharp.Chord, PiSharp.Tools, PiSharp.Durable.Store, PiSharp.Tui; ASP.NET Core shared framework | PISHARP_CHORD001 |
| PiSharp.Durable.Store | PiSharp.Durable, PiSharp.Chord, PiSharp.Durable.Sqlite | PISHARP_DURABLE001 and PISHARP_CHORD001 |
| PiSharp.Env.Protocol | none | PISHARP_ENV001 |
| PiSharp.Env | PiSharp.Env.Protocol, PiSharp.Durable | PISHARP_ENV001 and PISHARP_DURABLE001 |

All require .NET 10. Chord.Hosting declares `Microsoft.AspNetCore.App` as a framework reference, not an independently restored NuGet package. Native plugins are trusted application code; assembly loading is not a security sandbox. Chord legacy protocol 4 and the next protocol remain distinct. Native hosting does not imply arbitrary Node/TypeScript plugin, Node IPC, or browser-build compatibility.

Chord's legacy process manager and native Hosting both reuse `PiSharp.Tools.Processes.OwnedChildProcess` for child containment. This deliberately adds Tools as a direct exact-version dependency to both packages, bringing Agent and AI transitively (Tools → Agent → AI, alongside Contracts and Telemetry). The broader managed dependency footprint avoids divergent Windows-job and Unix-group implementations. The core Chord package remains independent of Durable. In 1.5.2, Hosting additionally uses Durable.Store for the application's SQLite store and Tui for native terminal I/O; this adds no dependency cycle. Merely referencing a package does not invoke an AI provider or launch a child. All these siblings must be available at the same exact package version. The package consumer checks the restored closure and the public facade without granting any process execution by default.

## Deliberate implementation coupling

Chord's byte-bearing schema validation continues to use Contracts' internal schema engine through a friend-assembly declaration. This is an explicit experimental implementation choice. Every PiSharp sibling dependency is pinned to the exact same version, and the engine types remain internal. Public Chord APIs use public schema/value contracts rather than exposing the engine, its resolver, or JavaScript-model classes. Updating only one package is unsupported; use the same version of all siblings. A future public value-validation API can replace the coupling in a separately reviewed change.

## Daemon distribution

PiSharp.Env.Daemon is explicitly nonpackable as a library. `tools/Env/Publish-NativeDaemon.ps1` produces and validates a native executable for the current supported host/RID; Unix publishing statically links the PTY shim. SSH callers supply a trusted catalog of artifacts and hashes. Installing the Env client does not download a daemon or run an installer. Android uses a separate explicitly selected, verified upstream artifact path; its package hashes do not establish Android runtime qualification.

NativeAOT artifacts need their own runtime/dependency license manifest and platform execution evidence. The five managed packages contain the Pi MIT notice and ordinary exact dependency metadata; they do not contain daemon executables. See [SSH and artifacts](../env-ssh.md).

## Local acceptance gate

After source stabilizes, the validation coordinator runs the following with the pinned SDK in an isolated package cache. Use a fresh output directory on each run:

```powershell
./tools/release/test-gap-package-consumer.ps1 -SourceCommit <40-character-checkout-sha> -Version 1.5.2 -Pack -DotnetPath <pinned-dotnet> -OutputDirectory artifacts/package-consumer-unique
```

`-Pack` invokes the existing local package build and archive validation; omit it only when consuming an already prepared local feed. The consumer is copied into a fresh temporary directory outside the repository, with no project references or inherited repository build files. PiSharp packages resolve exclusively from the supplied feed into an empty cache. External dependencies use the explicitly configured NuGet source.

The gate verifies package metadata/dependencies, compile-time experimental diagnostics, package-only opt-in compilation, a byte-bearing real Chord host call, SQLite StoreStorage open/close, protocol framing, Env's public platform mapping, Hosting's cookie helper, and visibility of public signature types. Package hashes and stage logs are retained. A failed or missing stage cannot produce `packageConsumptionProven=true`; the gate never sets `releaseAccepted=true`.

This is bounded consumption evidence. Full host/reload/jobs/remote, plugin/HTTP/worker, remote-environment and native artifact tests remain required in their dedicated suites. Successful `pack` alone is not admission, publication, platform qualification, or complete upstream parity.
