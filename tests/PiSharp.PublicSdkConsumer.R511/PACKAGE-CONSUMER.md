# Explicit local SDK package consumer

Default mode retains the source project references and the two existing project-only locks. The separate reviewed source-reference packet remains bound to its earlier exact source; this opt-in source successor inherits no grant or results.

`PublicSdkPackageConsumer=true` selects exact `[$(PublicSdkPackageVersion)]` package references to PiSharp.Extensions.Abstractions in the independent consumer and PiSharp.Extensions.Runtime in the harness. PiSharp.Contracts is their public transitive dependency. Both projects require an explicit version and `PublicSdkPackageLockRoot`. The latter supplies distinct `consumer/packages.lock.json` and `harness/packages.lock.json` paths. The executable harness continues to reference the independent consumer assembly. It references no SDK production project in package mode.

The owning packet must validate an absolute private lock root and the exact private version from the final composed package producer receipt. Packaging sets Version and PackageVersion consistently for Contracts, Abstractions and Runtime. A source-derived version such as `0.1.0-private.<composed-short-commit>` is a private identity, not a published upstream SDK version.

No package-mode locks or content hashes are invented here. A future separately admitted local-only force-evaluate restore must consume newly admitted exact local nupkgs and their exact producer/ZIP membership/dependency metadata. Its generated locks must be frozen, associated with the original restore receipt, and used in a subsequent locked restore before build and the same four controls. Existing source-mode project locks grant no package compatibility credit. All package feeds and caches must be private and closed; no network, credentials, Node or current production installation is authorized by these properties.

The two project XML files and this document are source-only and unexecuted. XML parsing does not prove MSBuild evaluation, restore viability, binary ABI equivalence, complete original Pi 0.99.1 compatibility or public distribution readiness.
