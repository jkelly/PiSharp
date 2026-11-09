# Gap-closure package preparation (unreleased)

No version or release is published by this change. The source baseline remains Pi 1.5.0, with selected newer behavior documented in the gap reports.

The local release pipeline prepares 28 libraries and one CLI package (29 total), including five new experimental libraries: Chord, Chord.Hosting, Durable.Store, Env.Protocol and Env. All PiSharp package dependencies remain exact-version siblings. Hosting additionally requires the ASP.NET Core shared framework. The Env libraries use `PISHARP_ENV001`; Chord uses `PISHARP_CHORD001`; Durable.Store retains `PISHARP_DURABLE001`.

The daemon remains a separate NativeAOT artifact and is excluded from NuGet library packing. No default CLI dependency on the new packages is added. Historical published package counts and original 1.5.0 release notes are unchanged.

Chord and Chord.Hosting depend directly on the existing PiSharp.Tools package for shared native child containment. Tools brings Agent and AI transitively. This is a deliberate dependency-footprint tradeoff to preserve one Windows-job/Unix-group lifetime implementation; it adds no Durable dependency, implicit provider call or package count change.

See [the admission contract](../contracts/gap-package-admission.md) for the deliberate Contracts/Chord friend-assembly coupling, dependency/license requirements, and package-only external consumer gate. The [local acceptance report](../../AI_DOCS/gap-implementation-results.md) records passing Windows runtime, native-artifact, and fresh package-consumer checks. Hosted Linux/macOS and other platform qualifications remain separate; local acceptance does not publish a release.
