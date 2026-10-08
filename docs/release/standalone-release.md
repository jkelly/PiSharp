# Offline standalone release artifacts

`tools/release/standalone-release.ps1` defines three functions. Importing it
defines functions; it does not build, publish, install, unpack, or run a product.
The caller supplies an already prepared self-contained publish directory.

* `Write-PiSharpStandaloneArchive` writes a new root-layout ZIP for `win-x64`,
  `linux-x64`, or `osx-arm64`. File inputs remain open for reading during archive
  creation. File names use ordinal ordering, ZIP timestamps are fixed at 1980,
  and compression is disabled. Unix apphosts carry regular-file mode 0755;
  other entries carry 0644. Duplicate paths, links, nonportable archive paths,
  oversized payloads, and output within the input directory are refused.
* `New-PiSharpStandaloneRelease` validates the ZIP with the existing distribution
  validator and writes a new JSON manifest containing source commit, explicit
  version, RID, pinned SDK/baseline, archive checksum/length, and every file's
  checksum/length/mode. Both destinations must be new. Structural rejection
  removes the newly owned outputs and retains the input directory.
* `Resolve-PiSharpStandaloneRelease` takes an explicit archive, manifest,
  independently supplied manifest SHA-256, version, source commit, and RID.
  It retains read handles while checking the manifest and archive. It checks
  the full manifest inventory and existing dependency/license/provenance
  requirements. The optional current version and current archive checksum
  must be supplied together. Matching bytes return `AlreadyCurrent`;
  different versions return `SideBySideCandidate`; the same version with
  different bytes is rejected.

The resolver has no implicit latest-version lookup or downgrade ordering.
`SideBySideCandidate` means the specified offline artifact passed structural
checks. It does not install it or authorize an update. No session, settings,
authentication, extension, or user-data path is accepted by these functions.
The checksum must come from the caller's existing authority; this code adds no
signature trust, downloader, credential handling, or execution permission.
Read sharing denies writer/delete sharing on Windows during the lease interval.
The caller must also own the staging directory exclusively; read handles alone
do not provide a cross-platform immutable filesystem guarantee. Receipts describe
the checked snapshot, not ownership after the functions return.

This per-artifact manifest is distinct from the existing four-artifact release
set metadata in `ReleaseArtifactManifest.ps1` and its byte-admission companion.
Those functions remain unchanged. A release owner may include the resulting
archive hash in that existing release-set contract; a single standalone receipt
does not establish a complete four-artifact release.

The supported native ZIP layout follows the existing distribution validator.
Pinned upstream Pi v0.99.1 documents npm global installation (Node >=22.19) and
an installer URL. Its `scripts/build-binaries.sh` produces Windows ZIPs and
Unix `tar.gz` archives containing a `pi` directory for six targets. This native
implementation deliberately uses the repository's three-RID root-ZIP contract;
it does not claim upstream installer format compatibility or six-target
support. The installer implementation was not present in the pinned source,
so downloader/update behavior is not inferred from the README URL.

This closes a concrete source gap between a prepared native payload and an
offline provenance-bound release candidate. It does not establish CLI runtime
qualification, public SDK package/ABI acceptance, clean-machine installation,
upgrade/uninstall behavior, runtime redistribution/SBOM closure, signatures,
publication, or cross-machine reproducible builds. Returned receipts always
retain `platformQualified=false` and `installed=false`.

`tests/release/standalone-release.tests.ps1` contains synthetic controls for all
three targets, enabled YAML dependencies/licenses, repeatable archive bytes,
timestamp/creation-order independence, mismatched checksum/source/version/RID,
same-version mutation, archive tampering, current-receipt pairing, existing
output preservation, structural rejection cleanup, and output/input separation.
Fake DLL/apphost/runtime bytes are never executed. The suite writes ZIPs only
inside a fresh GUID scratch directory and deletes that directory afterward.
The controls were authored without executing them; running them requires a
separate bounded execution allocation. They cannot qualify real binaries.
