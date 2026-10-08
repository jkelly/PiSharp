# Private native SDK package candidate

Three ordinary `net10.0` library packages are proposed:

| Package | Dependency |
| --- | --- |
| PiSharp.Contracts | none |
| PiSharp.Extensions.Abstractions | matching PiSharp.Contracts |
| PiSharp.Extensions.Runtime | matching PiSharp.Extensions.Abstractions |

The opt-in `tools/release/PiSharp.NativeSdk.targets` supplies package identity,
explicit private version/source commit, MIT/readme/notices and provenance
without changing shared projects. Normal native project references retain their
dependency graph. Native SDK archives contain one library DLL each and fixed
metadata/documents; no CLI, Agent, Node, tools, native runtime or third-party
package is part of this three-package graph. The byte validator compares the
assembly with the separately admitted current build and checks package metadata,
dependency identity and source notices. These checks do not prove consumption.

Source-only stage plan, not a grant: admit a coherent candidate containing the
separately reviewed SDK consumer and its package-mode switch; privately restore
the Runtime project in locked mode; build once; pack Contracts, Abstractions and
Runtime separately with `--no-build --no-restore` into one fresh local feed.
All five native originals need current exact grants and joined receipts. Use
the same explicit version/import/provenance properties for restore/build/pack.
Inspect each archive and retain exact package bytes before consumer restore.

The separately owned consumer must switch real project references to exact
`PackageReference` versions using `PublicSdkPackageConsumer=true` and a required
`PublicSdkPackageVersion`. Do not silently compile against the source graph.
Create new package-mode locks in an explicitly admitted outer scratch path from
the three measured local packages. This first restore is deliberate lock
generation, not a claimed locked restore; freeze and review the generated
package hashes/graph before a second locked restore. Only then build and run the
four public SDK controls against the actual packages, checking deps/assets
resolve all three exact identities and no source project fallback. Existing
project-reference locks or four source-reference passes cannot qualify this
package consumer.

The local NuGet config must clear inherited sources and contain only the private
feed. No acquisition, public push, signing, Node, live credentials or network is
authorized. Each original retains its process/capture/cleanup lifetime and its
own fresh evidence; stop on first nonpass. New six-stage CLI packaging ownership
does not implicitly permit these SDK operations. A separate bounded owner/driver
and immutable packet must bind this stage plan after the consumer seam is
reviewed and composed. All package functions and native stages here remain
unexecuted. SBOM/license closure and release acceptance remain separate.
